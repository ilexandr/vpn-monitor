using System.Diagnostics;
using System.Net;

namespace VpnMonitor.Client.Services;

public sealed class PingSession(IReachabilityProbe? probe = null)
{
    private readonly IReachabilityProbe _probe = probe ?? new NetworkReachabilityProbe();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RunAsync(IPAddress address, Action<ProbeReply> onReply, CancellationToken token,
        TimeSpan? interval = null)
    {
        var period = interval ?? TimeSpan.FromSeconds(1);
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        if (!await _gate.WaitAsync(0, token)) throw new InvalidOperationException("Ping уже выполняется.");
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var started = Stopwatch.StartNew();
                var reply = await _probe.CheckAsync(address, false, 0, token);
                token.ThrowIfCancellationRequested();
                onReply(reply);
                var delay = period - started.Elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            }
        }
        finally { _gate.Release(); }
    }
}
