using System.IO;
using System.Text.Json;

namespace VpnMonitor.Client.Services;

public sealed record UpdateJournalSummary(string Id, string Phase, string Message, DateTimeOffset UpdatedAt)
{
    public static UpdateJournalSummary? Read(string? directory = null)
    {
        directory ??= Path.GetDirectoryName(AwgProfileCatalogReader.CatalogPath)!;
        if (!Directory.Exists(directory)) return null;
        var files = new DirectoryInfo(directory).EnumerateFiles("update-*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc).Take(20);
        foreach (var file in files)
        {
            var id = Path.GetFileNameWithoutExtension(file.Name)[7..];
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            try
            {
                using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (input.Length > 64 * 1024) continue;
                using var doc = JsonDocument.Parse(input);
                var root = doc.RootElement;
                if (root.GetProperty("Id").GetString() != id) continue;
                var phase = root.GetProperty("Phase").GetString();
                if (phase is not ("Downloading" or "Preparing" or "Installing" or "Restoring" or "Completed" or "Failed")) continue;
                var message = root.GetProperty("Message").GetString() ?? "";
                return new(id, phase, message, new DateTimeOffset(file.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException) { }
        }
        return null;
    }

    public string PhaseText => Phase switch
    {
        "Downloading" => "Загрузка", "Preparing" => "Подготовка", "Installing" => "Установка",
        "Restoring" => "Восстановление", "Completed" => "Завершено", "Failed" => "Ошибка", _ => Phase
    };
}
