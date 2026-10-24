using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using VpnMonitor.Client.Models;

namespace VpnMonitor.Client.Services;

public sealed record ProbeTarget(string Id, string Address);
public sealed record ProbeReply(bool Success, long? Milliseconds, string Detail);
public sealed record ReachabilityScan(bool ServerAvailable, IReadOnlyDictionary<string, ReachabilityResult> Results);

public interface IReachabilityProbe
{
    Task<bool> ServerAvailableAsync(Uri server, CancellationToken token);
    Task<ProbeReply> CheckAsync(IPAddress address, bool tcpFallback, int tcpPort, CancellationToken token);
}

public sealed class NetworkReachabilityProbe : IReachabilityProbe
{
    private const int TimeoutMs = 800;

    private static async Task<bool> ConnectAsync(string host, int port, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeoutMs);
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }

    public Task<bool> ServerAvailableAsync(Uri server, CancellationToken token) =>
        ConnectAsync(server.DnsSafeHost, server.Port, token);

    public async Task<ProbeReply> CheckAsync(IPAddress address, bool tcpFallback, int tcpPort, CancellationToken token)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeoutMs).WaitAsync(token);
            if (reply.Status == IPStatus.Success)
                return new(true, reply.RoundtripTime, "Ответ на ping с VPN IP");
        }
        catch (PingException) { }
        catch (SocketException) { }
        token.ThrowIfCancellationRequested();
        if (tcpFallback && await ConnectAsync(address.ToString(), tcpPort, token))
            return new(true, null, $"Ответ TCP-порта {tcpPort}; ping не ответил");
        return new(false, null, tcpFallback ? $"Нет ответа ping и TCP-порта {tcpPort}" : "Нет ответа на ping");
    }
}

/// <summary>One cycle at a time; state changes only after both reference-server checks succeed.</summary>
public sealed class ReachabilityMonitor(IReachabilityProbe? probe = null)
{
    private readonly IReachabilityProbe _probe = probe ?? new NetworkReachabilityProbe();
    private readonly Dictionary<string, (string Address, ReachabilityResult Result)> _states = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    public const int MaxParallelChecks = 16;

    public ReachabilityResult GetResult(string id, string? address) =>
        _states.TryGetValue(id, out var state) && state.Address == address
            ? state.Result : ReachabilityResult.Unknown();

    public async Task<ReachabilityScan?> ScanAsync(IReadOnlyList<ProbeTarget> targets, Uri server,
        bool tcpFallback, int tcpPort, CancellationToken token = default)
    {
        if (!await _gate.WaitAsync(0, token)) return null;
        try
        {
            if (!await _probe.ServerAvailableAsync(server, token)) return Unavailable(targets, token);
            var replies = new ConcurrentDictionary<string, ProbeReply>(StringComparer.Ordinal);
            await Parallel.ForEachAsync(targets, new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelChecks, CancellationToken = token
            }, async (target, ct) =>
            {
                if (IPAddress.TryParse(target.Address, out var ip))
                    replies[target.Id] = await _probe.CheckAsync(ip, tcpFallback, tcpPort, ct);
            });
            // A tunnel lost during the cycle must not produce a wave of Offline events.
            if (!await _probe.ServerAvailableAsync(server, token)) return Unavailable(targets, token);
            token.ThrowIfCancellationRequested();
            var results = new Dictionary<string, ReachabilityResult>(StringComparer.Ordinal);
            foreach (var target in targets)
            {
                var previous = GetResult(target.Id, target.Address);
                ReachabilityResult result;
                if (!replies.TryGetValue(target.Id, out var reply))
                    result = ReachabilityResult.Unknown("Нет VPN IP");
                else if (reply.Success)
                    result = new(true, reply.Milliseconds, 0, reply.Detail, DateTime.Now);
                else
                {
                    var failures = Math.Min(3, previous.Failures + 1);
                    result = new(failures >= 3 ? false : previous.IsReachable, null, failures,
                        $"{reply.Detail}; подряд неудачных проверок: {failures}/3", DateTime.Now);
                }
                results[target.Id] = result;
            }
            _states.Clear();
            foreach (var target in targets) _states[target.Id] = (target.Address, results[target.Id]);
            return new(true, results);
        }
        finally { _gate.Release(); }
    }

    private ReachabilityScan Unavailable(IReadOnlyList<ProbeTarget> targets, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _states.Clear();
        var results = new Dictionary<string, ReachabilityResult>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var unknown = ReachabilityResult.Unknown("Сервер недоступен");
            _states[target.Id] = (target.Address, unknown);
            results[target.Id] = unknown;
        }
        return new(false, results);
    }
}
