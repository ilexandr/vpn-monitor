using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using VpnMonitor.Client.Models;

namespace VpnMonitor.Client.Services;

public sealed record ServicePortState(string Name, int Port, bool? Available, int Failures);
public sealed record ServiceSnapshot(string PeerId, string ProfileName, DateTimeOffset CheckedAt, IReadOnlyList<ServicePortState> Ports, bool ClientOffline = false)
{
    public bool? Healthy => Ports.Any(p => p.Available == false) ? false : Ports.Count > 0 && Ports.All(p => p.Available == true) ? true : null;
    public string Text => ClientOffline ? "Клиент Offline" : Healthy switch { true => "Доступны", false => "Есть сбой", _ => "Неизвестно" };
    public string Details => ClientOffline ? "Клиент недоступен; проверки сервисов приостановлены.\nПосле восстановления доступности проверки начнутся заново." : ProfileName + " · " + CheckedAt.LocalDateTime.ToString("HH:mm:ss") + "\n" +
        string.Join("\n", Ports.Select(p => $"{p.Name} :{p.Port} — {(p.Available switch { true => "доступен", false => "нет соединения", _ => "проверяется" })}"));
}
public sealed class ServiceMonitor
{
    public const int MaxRequestsPerCycle = 128;
    private readonly SemaphoreSlim _cycle = new(1, 1);
    private readonly Dictionary<string, (string Signature, ServiceSnapshot Snapshot)> _states = new(StringComparer.Ordinal);
    private readonly Func<string, int, CancellationToken, Task<bool>> _probe;
    private readonly Func<Uri, CancellationToken, Task<bool>> _reference;
    private readonly Func<DateTimeOffset> _now;
    public ServiceMonitor(Func<string, int, CancellationToken, Task<bool>>? probe = null,
        Func<Uri, CancellationToken, Task<bool>>? reference = null, Func<DateTimeOffset>? now = null)
    { _probe = probe ?? ConnectAsync; _reference = reference ?? new NetworkReachabilityProbe().ServerAvailableAsync; _now = now ?? (() => DateTimeOffset.Now); }
    public static bool CanCheck(Peer peer) => peer.Reachability.IsReachable
        ?? (peer.IsOnline || string.IsNullOrWhiteSpace(peer.Status));
    public ServiceSnapshot? Get(Peer peer, MonitoringSettings settings)
    {
        var profile = MonitoringConfiguration.Resolve(settings.Profiles, peer);
        if (settings.ServiceChecksEnabled && profile is not null && !CanCheck(peer))
        { _states.Remove(peer.Id); return new(peer.Id, profile.Name, DateTimeOffset.MinValue, [], true); }
        return settings.ServiceChecksEnabled && profile is not null && _states.TryGetValue(peer.Id, out var s) && s.Signature == Signature(peer, profile) ? s.Snapshot : null;
    }
    public void Reset() => _states.Clear();
    public async Task<bool?> ScanAsync(IReadOnlyList<Peer> peers, MonitoringSettings settings, Uri server, CancellationToken token)
    {
        if (!settings.ServiceChecksEnabled || !await _cycle.WaitAsync(0, token)) return null;
        try
        {
            var now = _now();
            var allowed = peers.Where(CanCheck).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _states.Keys.Where(id => !allowed.Contains(id)).ToArray()) _states.Remove(id);
            var jobs = peers.Where(CanCheck).Where(p => IPAddress.TryParse(p.VpnIp, out _))
                .Select(p => (Peer: p, Profile: MonitoringConfiguration.Resolve(settings.Profiles, p)))
                .Where(j => j.Profile is not null)
                .Where(j => Get(j.Peer, settings) is not { } snapshot || now - snapshot.CheckedAt >= TimeSpan.FromSeconds(j.Profile!.IntervalSeconds))
                .OrderBy(j => Get(j.Peer, settings)?.CheckedAt ?? DateTimeOffset.MinValue).ToArray();
            var selected = new List<(Peer Peer, ServiceProfile Profile, string Signature, string Host)>(); var count = 0;
            foreach (var job in jobs)
            {
                if (count + job.Profile!.Ports.Count > MaxRequestsPerCycle) break;
                selected.Add((job.Peer, job.Profile, Signature(job.Peer, job.Profile), job.Peer.VpnIp!)); count += job.Profile.Ports.Count;
            }
            if (selected.Count == 0) return null;
            if (!await _reference(server, token)) { Reset(); return false; }
            var replies = new ConcurrentDictionary<(string, int), bool>();
            await Parallel.ForEachAsync(selected.SelectMany(j => j.Profile.Ports.Select(p => (j.Peer, j.Host, Port: p))),
                new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Clamp(settings.ParallelChecks, 1, 16) },
                async (job, ct) => replies[(job.Peer.Id, job.Port.Port)] = await _probe(job.Host, job.Port.Port, ct));
            if (!await _reference(server, token)) { Reset(); return false; }
            token.ThrowIfCancellationRequested();
            foreach (var (peer, profile, signature, _) in selected)
            {
                if (!CanCheck(peer) || Signature(peer, profile) != signature) { _states.Remove(peer.Id); continue; }
                var previous = Get(peer, settings);
                var ports = profile.Ports.Select(p =>
                {
                    var old = previous?.Ports.FirstOrDefault(old => old.Port == p.Port);
                    var ok = replies[(peer.Id, p.Port)]; var failures = ok ? 0 : Math.Min(3, (old?.Failures ?? 0) + 1);
                    return new ServicePortState(p.Name, p.Port, ok ? true : failures >= 3 ? false : old?.Available, failures);
                }).ToArray();
                _states[peer.Id] = (Signature(peer, profile), new(peer.Id, profile.Name, now, ports));
            }
            return true;
        }
        finally { _cycle.Release(); }
    }
    private static string Signature(Peer peer, ServiceProfile profile) => peer.VpnIp + "|" + profile.Id + "|" + profile.Name + "|" +
        string.Join(";", profile.Ports.Select(p => p.Name + ":" + p.Port));
    private static async Task<bool> ConnectAsync(string host, int port, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(1000);
        using var client = new TcpClient();
        try { await client.ConnectAsync(host, port, timeout.Token); return true; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }
}
