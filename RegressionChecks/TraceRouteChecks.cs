using System.Net;
using System.Net.NetworkInformation;
using VpnMonitor.Client.Services;

internal static class TraceRouteChecks
{
    public static async Task RunAsync()
    {
        var target = IPAddress.Parse("10.20.0.2");
        var probe = new FakeProbe { Reply = ttl => ttl == 1
            ? new(IPStatus.TtlExpired, "10.20.0.1", 3)
            : new(IPStatus.Success, target.ToString(), 5) };
        var session = new TraceRouteSession(probe);
        var hops = new List<TraceHop>();
        Require(await session.RunAsync(target, 30, hops.Add, default) == TraceCompletion.Reached
            && hops.Count == 2 && probe.Calls == 6 && hops[0].Address == "10.20.0.1",
            "Trace must collect three replies per hop and stop at its target.");

        probe.Calls = 0;
        probe.Reply = _ => new(IPStatus.TimedOut, null, null);
        hops.Clear();
        Require(await session.RunAsync(target, 2, hops.Add, default) == TraceCompletion.HopLimit
            && probe.Calls == 6 && hops.All(h => h.Address == "*" && h.First == "*"),
            "Silent hops must respect the limit and display unknown values.");
        probe.Reply = _ => new(IPStatus.DestinationHostUnreachable, "10.20.0.1", null);
        Require(await session.RunAsync(target, 30, _ => { }, default) == TraceCompletion.Unreachable,
            "Unreachable replies must terminate tracing.");
        probe.Reply = _ => new(IPStatus.Success, "10.20.0.99", 1);
        Require(await session.RunAsync(target, 1, _ => { }, default) == TraceCompletion.HopLimit,
            "A reply from another IP must not be mistaken for the target.");
        try { await session.RunAsync(target, 65, _ => { }, default); throw new Exception("Invalid hop limit accepted."); }
        catch (ArgumentOutOfRangeException) { }

        probe.Block = true;
        using var cancellation = new CancellationTokenSource();
        var callbacks = 0;
        var running = session.RunAsync(target, 30, _ => callbacks++, cancellation.Token);
        try
        {
            await session.RunAsync(target, 30, _ => { }, cancellation.Token);
            throw new Exception("Overlapping trace accepted.");
        }
        catch (InvalidOperationException) { }
        cancellation.Cancel();
        try { await running; throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        Require(callbacks == 0, "Cancelled requests must not publish partial rows.");
        probe.Block = false;
        probe.Reply = _ => new(IPStatus.Success, target.ToString(), 0);
        Require(await session.RunAsync(target, 1, _ => { }, default) == TraceCompletion.Reached,
            "Trace must restart after cancellation.");

        var loopback = new TraceRouteSession();
        Require(await loopback.RunAsync(IPAddress.Loopback, 1, _ => { }, default) == TraceCompletion.Reached,
            "Native loopback traceroute failed.");
        Console.WriteLine("PASS: traceroute hops, limits, unreachable, cancellation, no overlap, restart, native ICMP.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class FakeProbe : ITraceRouteProbe
    {
        public Func<int, TraceProbeReply> Reply = _ => new(IPStatus.TimedOut, null, null);
        public int Calls;
        public bool Block;
        public async Task<TraceProbeReply> SendAsync(IPAddress target, int ttl, CancellationToken token)
        {
            Calls++;
            if (Block) await Task.Delay(Timeout.Infinite, token);
            return Reply(ttl);
        }
    }
}