using System.Text.Json.Serialization;

namespace VpnMonitor.Client.Models;

public sealed class GroupInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("background_color")]
    public string? BackgroundColor { get; set; }

    [JsonPropertyName("peer_ids")]
    public List<string> PeerIds { get; set; } = [];
}
