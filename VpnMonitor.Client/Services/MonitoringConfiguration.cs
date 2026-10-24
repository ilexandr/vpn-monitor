using VpnMonitor.Client.Models;

namespace VpnMonitor.Client.Services;

public static class MonitoringConfiguration
{
    public static bool IsScope(string? scope) => scope is { Length: 64 } && scope.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    public static MonitoringSettings Get(ClientSettings settings, string scope) =>
        settings.MonitoringScopes?.FirstOrDefault(s => s is not null && s.Scope == scope)?.Settings ?? new();
    public static ServiceProfile? Resolve(IEnumerable<ServiceProfile> profiles, Peer peer) => profiles.Where(p => p.Enabled)
        .Where(p => p.TargetKind == "all" || p.TargetKind == "peer" && p.Target == peer.Id ||
            p.TargetKind == "group" && peer.Groups.Contains(p.Target, StringComparer.OrdinalIgnoreCase))
        .OrderByDescending(p => p.TargetKind == "peer" ? 2 : p.TargetKind == "group" ? 1 : 0).FirstOrDefault();
    public static void Validate(MonitoringSettings settings)
    {
        if (settings is null || settings.ParallelChecks is < 1 or > 16 || settings.Profiles is null || settings.Profiles.Count > 100)
            throw new ArgumentException("Параллельных проверок: 1–16, профилей: до 100.");
        var ids = new HashSet<string>();
        foreach (var p in settings.Profiles)
            if (p is null || !Guid.TryParseExact(p.Id, "N", out _) || !ids.Add(p.Id) || !Text(p.Name, 80) ||
                p.TargetKind is not ("all" or "group" or "peer") || p.Target is null || p.Target.Length > 512 || p.Target.Any(char.IsControl) ||
                p.TargetKind != "all" && string.IsNullOrWhiteSpace(p.Target) || p.IntervalSeconds is < 5 or > 3600 ||
                p.Ports is null || p.Ports.Count is < 1 or > 8 || p.Ports.Any(s => s is null || !Text(s.Name, 80) || s.Port is < 1 or > 65535) ||
                p.Ports.Select(s => s.Port).Distinct().Count() != p.Ports.Count)
                throw new ArgumentException("Профиль: название до 80 символов, интервал 5–3600 сек., 1–8 разных TCP-портов.");
        var n = settings.Notifications;
        if (n is null || n.OfflineDelaySeconds is < 0 or > 3600 || n.QuietStartMinute is < 0 or > 1439 ||
            n.QuietEndMinute is < 0 or > 1439 || n.AggregateThreshold is < 2 or > 100 || n.Maintenance is null ||
            n.Maintenance.Count > 100 || n.Maintenance.Any(m => m is null || !Text(m.Group, 200)))
            throw new ArgumentException("Некорректные правила уведомлений или обслуживания.");
    }
    public static void ValidateLists(ClientSettings settings)
    {
        if (settings.MonitoringScopes is null || settings.MonitoringScopes.Count > 20 || settings.SavedViews is null || settings.SavedViews.Count > 200)
            throw new ArgumentException("Слишком много настроек мониторинга или представлений.");
        var scopes = new HashSet<string>();
        foreach (var s in settings.MonitoringScopes)
        { if (s is null || !IsScope(s.Scope) || !scopes.Add(s.Scope)) throw new ArgumentException("Некорректная область мониторинга."); Validate(s.Settings); }
        var ids = new HashSet<string>();
        var sorts = new[] { "IsOnline", "Name", "VpnIpSortKey", "HandshakeSortKey", "TrafficTotal", "GroupsText" };
        foreach (var v in settings.SavedViews)
            if (v is null || !Guid.TryParseExact(v.Id, "N", out _) || !ids.Add(v.Id) || !IsScope(v.Scope) || !Text(v.Name, 80) ||
                v.Search is null || v.Search.Length > 512 || v.Group?.Length > 200 || v.Filter is not ("all" or "online" or "offline") ||
                !sorts.Contains(v.SortMember) || !(v.Status || v.Computer || v.VpnIp || v.Handshake || v.Traffic || v.Groups))
                throw new ArgumentException("Некорректное сохранённое представление.");
    }
    private static bool Text(string? text, int length) => !string.IsNullOrWhiteSpace(text) && text.Length <= length && !text.Any(char.IsControl);
}
