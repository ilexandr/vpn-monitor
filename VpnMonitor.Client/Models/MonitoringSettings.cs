namespace VpnMonitor.Client.Models;

public sealed class MonitoringSettings
{
    public bool ServiceChecksEnabled { get; set; }
    public bool ServiceNotifications { get; set; }
    public int ParallelChecks { get; set; } = 8;
    public List<ServiceProfile> Profiles { get; set; } = [];
    public NotificationRules Notifications { get; set; } = new();
}
public sealed class ServiceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Новый профиль";
    public string TargetKind { get; set; } = "all";
    public string Target { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 30;
    public List<ServicePort> Ports { get; set; } = [];
}
public sealed record ServicePort(string Name, int Port);
public sealed class NotificationRules
{
    public int OfflineDelaySeconds { get; set; }
    public bool QuietHoursEnabled { get; set; }
    public int QuietStartMinute { get; set; } = 22 * 60;
    public int QuietEndMinute { get; set; } = 8 * 60;
    public int AggregateThreshold { get; set; } = 3;
    public List<GroupMaintenance> Maintenance { get; set; } = [];
}
public sealed record GroupMaintenance(string Group, DateTimeOffset Until);
public sealed class ScopedMonitoringSettings
{
    public string Scope { get; set; } = "";
    public MonitoringSettings Settings { get; set; } = new();
}
public sealed class SavedClientView
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Scope { get; set; } = "";
    public string Name { get; set; } = "";
    public string Search { get; set; } = "";
    public string? Group { get; set; }
    public string Filter { get; set; } = "all";
    public bool FavoritesOnly { get; set; }
    public string SortMember { get; set; } = "IsOnline";
    public bool SortDescending { get; set; } = true;
    public bool Status { get; set; } = true;
    public bool Computer { get; set; } = true;
    public bool VpnIp { get; set; } = true;
    public bool Handshake { get; set; }
    public bool Traffic { get; set; }
    public bool Groups { get; set; }
}
