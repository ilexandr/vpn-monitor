using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VpnMonitor.Client.Services;

public sealed record TraceProbeReply(IPStatus Status, string? Address, long? Milliseconds)
{
    public string Time => Milliseconds is long value ? value == 0 ? "<1 мс" : $"{value} мс" : "*";
}

public sealed record TraceHop(int Number, IReadOnlyList<TraceProbeReply> Replies)
{
    public string Address => string.Join(", ", Replies.Select(r => r.Address).Where(a => !string.IsNullOrEmpty(a)).Distinct()) is { Length: > 0 } value ? value : "*";
    public string First => Replies[0].Time;
    public string Second => Replies[1].Time;
    public string Third => Replies[2].Time;
    public string Note => Replies.Any(r => r.Status == IPStatus.Success) ? "Ответ получен"
        : Replies.Any(r => r.Status == IPStatus.TtlExpired) ? "Промежуточный узел"
        : Replies.All(r => r.Status == IPStatus.TimedOut) ? "Нет ответа"
        : string.Join(", ", Replies.Select(r => r.Status.ToString()).Distinct());
}

public enum TraceCompletion { Reached, HopLimit, Unreachable }

public interface ITraceRouteProbe
{
    Task<TraceProbeReply> SendAsync(IPAddress target, int ttl, CancellationToken token);
}

public sealed class NetworkTraceRouteProbe : ITraceRouteProbe
{
    public async Task<TraceProbeReply> SendAsync(IPAddress target, int ttl, CancellationToken token)
    {
        using var ping = new Ping();
        var elapsed = Stopwatch.StartNew();
        try
        {
            var reply = await ping.SendPingAsync(target, 1000, new byte[32], new PingOptions(ttl, true)).WaitAsync(token);
            var replied = reply.Status is IPStatus.Success or IPStatus.TtlExpired;
            var address = reply.Address;
            var usableAddress = address is not null && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);
            return new(reply.Status, usableAddress ? address!.ToString() : null,
                replied ? reply.Status == IPStatus.Success ? reply.RoundtripTime : elapsed.ElapsedMilliseconds : null);
        }
        catch (PingException) { return new(IPStatus.Unknown, null, null); }
        catch (SocketException) { return new(IPStatus.Unknown, null, null); }
    }
}

public sealed class TraceRouteSession(ITraceRouteProbe? probe = null)
{
    private readonly ITraceRouteProbe _probe = probe ?? new NetworkTraceRouteProbe();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<TraceCompletion> RunAsync(IPAddress target, int maxHops, Action<TraceHop> onHop, CancellationToken token)
    {
        if (maxHops is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxHops));
        if (!await _gate.WaitAsync(0, token)) throw new InvalidOperationException("Трассировка уже выполняется.");
        try
        {
            for (var ttl = 1; ttl <= maxHops; ttl++)
            {
                var replies = new List<TraceProbeReply>();
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    replies.Add(await _probe.SendAsync(target, ttl, token));
                    token.ThrowIfCancellationRequested();
                }
                onHop(new(ttl, replies));
                if (replies.Any(r => r.Status == IPStatus.Success && IPAddress.TryParse(r.Address, out var address) && address.Equals(target)))
                    return TraceCompletion.Reached;
                if (replies.Any(r => r.Status is IPStatus.DestinationNetworkUnreachable or IPStatus.DestinationHostUnreachable
                    or IPStatus.DestinationProtocolUnreachable or IPStatus.DestinationPortUnreachable or IPStatus.Unknown))
                    return TraceCompletion.Unreachable;
            }
            return TraceCompletion.HopLimit;
        }
        finally { _gate.Release(); }
    }
}