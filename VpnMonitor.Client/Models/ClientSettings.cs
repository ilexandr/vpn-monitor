namespace VpnMonitor.Client.Models;

public sealed class ClientSettings
{
    public string ServerUrl { get; set; } = "http://10.20.0.1";
    public string Username { get; set; } = "admin";
    public string ProtectedPassword { get; set; } = "";
    public int RefreshSeconds { get; set; } = 5;
    public string SshUsername { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public List<CustomAction> CustomActions { get; set; } = [];
    public List<BuiltinActionOptions> BuiltinActions { get; set; } = [];
    public List<ScopedMonitoringSettings> MonitoringScopes { get; set; } = [];
    public List<SavedClientView> SavedViews { get; set; } = [];
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public string Theme { get; set; } = "light";

    public string RadminViewerPath { get; set; } = "";
    public int RadminPort { get; set; } = 4899;
    public string RadminUsername { get; set; } = "Admin";
    public bool RadminAutofillUsername { get; set; } = true;

    public bool ShowStatusColumn { get; set; } = true;
    public bool ShowComputerColumn { get; set; } = true;
    public bool ShowVpnIpColumn { get; set; } = true;
    public bool ShowHandshakeColumn { get; set; } = true;
    public bool ShowTrafficColumn { get; set; } = true;
    public bool ShowGroupsColumn { get; set; } = true;

    public List<string> FavoritePeerIds { get; set; } = [];
    public bool FavoritesOnly { get; set; }

    public List<string> StatusNotificationPeerIds { get; set; } = [];
    public bool StatusNotificationsEnabled { get; set; } = true;
    public bool NotifyWhenOnline { get; set; } = true;
    public bool NotifyWhenOffline { get; set; } = true;
    public string StatusNotificationSource { get; set; } = "active";
    public bool ActiveChecksEnabled { get; set; } = true;
    public bool SavePeerHistory { get; set; } = true;
    public int ActiveCheckSeconds { get; set; } = 5;
    public bool ActiveChecksWatchedOnly { get; set; }
    public bool ActiveCheckTcpFallback { get; set; }
    public int ActiveCheckTcpPort { get; set; } = 4899;

    public string? SelectedGroup { get; set; }
    public string Filter { get; set; } = "all";
    public string SortMember { get; set; } = "IsOnline";
    public bool SortDescending { get; set; } = true;

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public double? PeerDetailsWindowWidth { get; set; }
    public double? PeerDetailsWindowHeight { get; set; }

    public double? SettingsWindowWidth { get; set; }
    public double? SettingsWindowHeight { get; set; }
}
