namespace VpnMonitor.Client.Models;
public sealed record AwgProfileInfo(string Id, string Name);
public sealed class AwgProfileCatalog
{
    public int Version { get; set; } = 1;
    public string? SelectedId { get; set; }
    public List<AwgProfileInfo> Profiles { get; set; } = [];
}