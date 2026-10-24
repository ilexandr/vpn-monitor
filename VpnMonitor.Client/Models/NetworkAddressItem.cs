namespace VpnMonitor.Client.Models;

public sealed class NetworkAddressItem
{
    public string InterfaceName { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public bool IsUp { get; init; }
}
