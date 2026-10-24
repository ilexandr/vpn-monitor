using System.Net;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;

internal static class PingChecks
{
    public static async Task RunAsync()
    {
        var history = new PingHistory();
        Require(history.Average is null && history.Minimum is null && history.LossPercent == 0, "Empty statistics are invalid.");
        history.Add(10);
        history.Add(null);
        history.Add(30);
        history.Add(0);
        Require(history.Sent == 4 && history.Received == 3 && history.Lost == 1 && history.LossPercent == 25,
            "Loss counters must include failed probes.");
        Require(history.Minimum == 0 && history.Maximum == 30 && Math.Abs(history.Average!.Value - 40d / 3) < 0.001,
            "RTT statistics must exclude failures and include zero latency.");
        for (var index = 0; index < 300; index++) history.Add(5);
        Require(history.Recent.Count == PingHistory.HistoryLimit && history.Sent == 304 && history.Received == 303,
            "Bounded history must not reset session totals.");
        try { history.Add(-1); throw new InvalidOperationException("Negative RTT accepted."); }
        catch (ArgumentOutOfRangeException) { }
        history.Clear();
        Require(history.Sent == 0 && history.Recent.Count == 0 && history.Average is null && history.Maximum is null,
            "Reset must clear all statistics.");

        var probe = new BlockingProbe();
        var session = new PingSession(probe);
        using var first = new CancellationTokenSource();
        var callbacks = 0;
        var blocked = session.RunAsync(IPAddress.Loopback, _ => callbacks++, first.Token);
        try
        {
            await session.RunAsync(IPAddress.Loopback, _ => { }, first.Token);
            throw new InvalidOperationException("Overlapping ping sessions were allowed.");
        }
        catch (InvalidOperationException ex) when (ex.Message == "Ping уже выполняется.") { }
        first.Cancel();
        await ExpectCancellation(blocked);
        Require(callbacks == 0, "Cancelled requests must not publish results.");

        probe.Block = false;
        using var second = new CancellationTokenSource();
        var restarted = session.RunAsync(IPAddress.Loopback, _ =>
        {
            callbacks++;
            if (callbacks == 3) second.Cancel();
        }, second.Token, TimeSpan.FromMilliseconds(5));
        await ExpectCancellation(restarted);
        Require(callbacks == 3, "Restart after pause must work and stop cleanly.");
        Console.WriteLine("PASS: ping statistics, bounded history, cancellation, no overlap, restart.");
    }

    private static async Task ExpectCancellation(Task task)
    {
        try { await task; throw new InvalidOperationException("Cancellation ignored."); }
        catch (OperationCanceledException) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BlockingProbe : IReachabilityProbe
    {
        public bool Block = true;
        public Task<bool> ServerAvailableAsync(Uri server, CancellationToken token) => throw new NotSupportedException();
        public async Task<ProbeReply> CheckAsync(IPAddress address, bool tcpFallback, int tcpPort, CancellationToken token)
        {
            Require(!tcpFallback, "Continuous ping must measure ICMP only.");
            if (Block) await Task.Delay(Timeout.Infinite, token);
            return new(true, 5, "Ответ");
        }
    }
}
