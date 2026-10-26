using System.Text.Json.Serialization;
namespace VpnMonitor.Client.Models;
public sealed record AdminUser
{
    [JsonPropertyName("username")] public string Username { get; init; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("is_admin")] public bool IsAdmin { get; init; }
    [JsonPropertyName("group")] public string? Group { get; init; }
    [JsonPropertyName("group_exists")] public bool GroupExists { get; init; }
    public string Description => IsAdmin ? "Администратор · все группы" : (Enabled ? "Вход разрешён" : "Заблокирован") + (GroupExists ? " · " + Group : " · группа не найдена");
}
public sealed record AdminUsersSnapshot
{
    [JsonPropertyName("items")] public AdminUser[] Items { get; init; } = [];
    [JsonPropertyName("groups")] public string[] Groups { get; init; } = [];
    [JsonPropertyName("csrf_token")] public string CsrfToken { get; init; } = "";
}
