using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;

internal static class ReachabilityChecks
{
    public static async Task RunAsync()
    {
        var defaults = JsonSerializer.Deserialize<ClientSettings>("{}")!;
        Require(defaults.ActiveChecksEnabled && defaults.StatusNotificationSource == "active" && defaults.ActiveCheckSeconds == 5,
            "Existing settings must get active checks by default.");
        var probe = new FakeProbe();
        var monitor = new ReachabilityMonitor(probe);
        var server = new Uri("http://127.0.0.1");
        ProbeTarget[] targets = [new("peer", "10.20.0.2")];
        var peer = new Peer { Id = "peer", VpnIp = targets[0].Address };
        var settings = new ClientSettings { StatusNotificationPeerIds = ["peer"] };
        var tracker = new PeerStatusTracker();
        async Task<ReachabilityScan> ScanAsync()
        {
            var result = (await monitor.ScanAsync(targets, server, false, 4899))!;
            peer.Reachability = result.Results["peer"];
            return result;
        }
        IReadOnlyList<Peer> Events() => tracker.Update([peer], settings, p => p.Reachability.IsReachable);

        await ScanAsync();
        Require(peer.Reachability.IsReachable == true && Events().Count == 0, "Initial successful check must be silent.");
        probe.Success = false;
        for (var failure = 1; failure <= 2; failure++)
        {
            await ScanAsync();
            Require(peer.Reachability.IsReachable == true && peer.Reachability.Failures == failure && Events().Count == 0,
                "One or two packet losses must not cause Offline.");
        }
        await ScanAsync();
        Require(peer.Reachability.IsReachable == false && Events().Count == 1, "Three failed cycles must cause one Offline.");
        await ScanAsync();
        Require(Events().Count == 0, "Repeated failure must not repeat notifications.");
        probe.Success = true;
        await ScanAsync();
        Require(peer.Reachability.IsReachable == true && peer.Reachability.Failures == 0 && Events().Count == 1,
            "A successful response must restore availability immediately.");

        probe.Success = false;
        await ScanAsync();
        probe.Success = true;
        await ScanAsync();
        probe.Success = false;
        await ScanAsync();
        Require(peer.Reachability.Failures == 1, "Success must reset the failure counter.");

        probe.ServerAnswers = new Queue<bool>([false]);
        var outage = await ScanAsync();
        Require(!outage.ServerAvailable && peer.Reachability.IsReachable is null && Events().Count == 0,
            "Reference server loss must produce unknown, not Offline.");
        tracker.Reset();
        probe.Success = true;
        await ScanAsync();
        Require(Events().Count == 0, "Recovery from local tunnel loss must establish a silent baseline.");
        probe.ServerAnswers = new Queue<bool>([true, false]);
        probe.Success = false;
        outage = await ScanAsync();
        Require(!outage.ServerAvailable && peer.Reachability.IsReachable is null,
            "Tunnel lost during a cycle must invalidate its results.");

        probe.Success = true;
        await ScanAsync();
        targets = [new("peer", "10.20.0.3")];
        probe.Success = false;
        await ScanAsync();
        Require(peer.Reachability.IsReachable is null && peer.Reachability.Failures == 1,
            "Changed addresses must not inherit old reachability.");
        targets = [new("peer", "")];
        await ScanAsync();
        Require(peer.Reachability.IsReachable is null, "Missing IP must remain unknown.");

        probe.Success = true;
        targets = [new("peer", "10.20.0.2")];
        await ScanAsync();
        probe.DelayMs = 50;
        var many = Enumerable.Range(1, 55).Select(i => new ProbeTarget($"peer-{i}", $"10.20.0.{i}")).ToArray();
        await monitor.ScanAsync(many, server, true, 3389);
        Require(probe.MaxConcurrent is > 1 and <= ReachabilityMonitor.MaxParallelChecks && probe.LastTcpFallback && probe.LastPort == 3389,
            "Concurrency must be bounded and TCP settings forwarded.");
        var previous = monitor.GetResult(many[0].Id, many[0].Address);
        probe.DelayMs = 10000;
        using var cancellation = new CancellationTokenSource();
        var running = monitor.ScanAsync(many, server, false, 4899, cancellation.Token);
        Require(await monitor.ScanAsync(many, server, false, 4899) is null, "Overlapping cycles must be skipped.");
        cancellation.Cancel();
        try { await running; throw new InvalidOperationException("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
        Require(monitor.GetResult(many[0].Id, many[0].Address) == previous, "Cancelled checks must not overwrite prior state.");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var reference = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        var network = new NetworkReachabilityProbe();
        Require(await network.ServerAvailableAsync(reference, default), "Local TCP reference must be reachable.");
        Require((await network.CheckAsync(IPAddress.Loopback, false, 4899, default)).Success, "Loopback ICMP check failed.");
        listener.Stop();
        Require(!await network.ServerAvailableAsync(reference, default), "Closed reference port must be unavailable.");
        Console.WriteLine("PASS: reachability thresholds, recovery, loss/reset, cancellation, concurrency, TCP reference, ICMP.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeProbe : IReachabilityProbe
    {
        public bool Success = true;
        public int DelayMs;
        public Queue<bool> ServerAnswers = new();
        public int MaxConcurrent;
        private int _concurrent;
        public bool LastTcpFallback;
        public int LastPort;

        public Task<bool> ServerAvailableAsync(Uri server, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(ServerAnswers.Count == 0 || ServerAnswers.Dequeue());
        }
        public async Task<ProbeReply> CheckAsync(IPAddress address, bool tcpFallback, int tcpPort, CancellationToken token)
        {
            var active = Interlocked.Increment(ref _concurrent);
            lock (this) MaxConcurrent = Math.Max(MaxConcurrent, active);
            LastTcpFallback = tcpFallback;
            LastPort = tcpPort;
            try
            {
                if (DelayMs > 0) await Task.Delay(DelayMs, token);
                token.ThrowIfCancellationRequested();
                return new(Success, Success ? 5L : null, Success ? "Ответ" : "Нет ответа");
            }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
    }
}
