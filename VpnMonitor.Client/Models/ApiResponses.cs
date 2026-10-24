using System.Text.Json.Serialization;

namespace VpnMonitor.Client.Models;

public sealed class MeResponse
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("is_admin")]
    public bool IsAdmin { get; set; }

    [JsonPropertyName("group")]
    public string? Group { get; set; }
}

public sealed class PeersResponse
{
    [JsonPropertyName("updated")]
    public string? Updated { get; set; }

    [JsonPropertyName("configuration")]
    public string? Configuration { get; set; }

    [JsonPropertyName("items")]
    public List<Peer> Items { get; set; } = [];
}

public sealed class GroupsResponse
{
    [JsonPropertyName("updated")]
    public string? Updated { get; set; }

    [JsonPropertyName("configuration")]
    public string? Configuration { get; set; }

    [JsonPropertyName("items")]
    public List<GroupInfo> Items { get; set; } = [];
}

public sealed class SummaryResponse
{
    [JsonPropertyName("configuration")]
    public string? Configuration { get; set; }

    [JsonPropertyName("updated")]
    public string? Updated { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("online")]
    public int Online { get; set; }

    [JsonPropertyName("offline")]
    public int Offline { get; set; }

    [JsonPropertyName("groups")]
    public int Groups { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
