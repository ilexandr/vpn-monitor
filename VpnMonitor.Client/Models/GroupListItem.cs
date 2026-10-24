namespace VpnMonitor.Client.Models;

public sealed class GroupListItem
{
    public string Name { get; init; } = "";
    public int Online { get; init; }
    public int Total { get; init; }
    public string CountText => $"{Online}/{Total}";
}
