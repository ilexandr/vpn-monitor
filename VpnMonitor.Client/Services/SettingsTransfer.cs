using System.Text.Json;
using System.Text.Json.Nodes;
using VpnMonitor.Client.Models;
namespace VpnMonitor.Client.Services;
public static class SettingsTransfer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static string Export(ClientSettings settings)
    {
        var data = JsonSerializer.SerializeToNode(settings)!.AsObject();
        data.Remove(nameof(ClientSettings.ProtectedPassword));
        return new JsonObject { ["Format"] = "VpnMonitor.Settings", ["Version"] = 1, ["Settings"] = data }.ToJsonString(Options);
    }
    public static ClientSettings Import(string json)
    {
        if (json.Length > 2_000_000) throw new ArgumentException("Файл настроек слишком большой.");
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new ArgumentException("Пустой файл.");
        if (root["Format"]?.GetValue<string>() != "VpnMonitor.Settings" || root["Version"]?.GetValue<int>() != 1)
            throw new ArgumentException("Неподдерживаемый формат или версия настроек.");
        var data = root["Settings"]?.AsObject() ?? throw new ArgumentException("Отсутствует раздел Settings.");
        data.Remove(nameof(ClientSettings.ProtectedPassword));
        var settings = data.Deserialize<ClientSettings>() ?? throw new ArgumentException("Некорректные настройки.");
        if (!Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var url) || (url.Scheme != "http" && url.Scheme != "https") ||
            string.IsNullOrWhiteSpace(settings.Username)) throw new ArgumentException("Некорректное подключение.");
        if (settings.RefreshSeconds is < 2 or > 300 || settings.ActiveCheckSeconds is < 2 or > 300 ||
            settings.SshPort is < 1 or > 65535 || settings.RadminPort is < 1 or > 65535 || settings.ActiveCheckTcpPort is < 1 or > 65535)
            throw new ArgumentException("Некорректный интервал или порт.");
        if (settings.Theme is not ("light" or "dark") || settings.StatusNotificationSource is not ("active" or "awg") ||
            settings.StatusNotificationSource == "active" && settings.StatusNotificationsEnabled && !settings.ActiveChecksEnabled)
            throw new ArgumentException("Несовместимые параметры темы или уведомлений.");
        if (!(settings.ShowStatusColumn || settings.ShowComputerColumn || settings.ShowVpnIpColumn ||
            settings.ShowHandshakeColumn || settings.ShowTrafficColumn || settings.ShowGroupsColumn))
            throw new ArgumentException("Должна быть включена хотя бы одна колонка.");
        if (settings.CustomActions is null || settings.CustomActions.Count > 500 ||
            settings.FavoritePeerIds is null || settings.StatusNotificationPeerIds is null)
            throw new ArgumentException("Некорректные списки настроек.");
        foreach (var action in settings.CustomActions)
        {
            if (action is null) throw new ArgumentException("Пустое пользовательское действие.");
            CustomActionService.Validate(action);
        }
        settings.ProtectedPassword = "";
        return settings;
    }
    public static void Copy(ClientSettings source, ClientSettings target)
    {
        var clone = JsonSerializer.Deserialize<ClientSettings>(JsonSerializer.Serialize(source))!;
        foreach (var property in typeof(ClientSettings).GetProperties())
            property.SetValue(target, property.GetValue(clone));
    }
}