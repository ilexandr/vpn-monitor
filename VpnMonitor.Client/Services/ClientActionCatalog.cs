using VpnMonitor.Client.Models;

namespace VpnMonitor.Client.Services;

public static class ClientActionCatalog
{
    public static List<BuiltinActionOptions> Defaults() =>
    [
        new() { Id = "favorite", Name = "Избранное" },
        new() { Id = "watch", Name = "Уведомления Online/Offline" },
        new() { Id = "rdp", Name = "RDP", Group = "Подключение" },
        new() { Id = "ssh", Name = "SSH", Group = "Подключение" },
        new() { Id = "radmin-control", Name = "Radmin — управление", Group = "Подключение" },
        new() { Id = "radmin-files", Name = "Radmin — передача файлов", Group = "Подключение" },
        new() { Id = "shares", Name = "Общие папки" },
        new() { Id = "ping", Name = "Пинг", Group = "Диагностика" },
        new() { Id = "trace", Name = "Tracert", Group = "Диагностика" },
        new() { Id = "history", Name = "История клиента", Group = "Диагностика" },
        new() { Id = "copy-name", Name = "Имя", Group = "Копировать" },
        new() { Id = "copy-vpn", Name = "VPN IP", Group = "Копировать" },
        new() { Id = "copy-external", Name = "External IP", Group = "Копировать" }
    ];

    public static List<BuiltinActionOptions> Resolve(IEnumerable<BuiltinActionOptions>? saved)
    {
        var remaining = Defaults().ToDictionary(a => a.Id, StringComparer.Ordinal);
        var result = new List<BuiltinActionOptions>();
        foreach (var option in saved ?? [])
            if (option is not null && !string.IsNullOrEmpty(option.Id) && remaining.Remove(option.Id, out var fallback))
                result.Add(new() { Id = option.Id, Name = string.IsNullOrWhiteSpace(option.Name) ? fallback.Name : option.Name,
                    Group = option.Group ?? "", Enabled = option.Enabled });
        result.AddRange(remaining.Values);
        return result;
    }

    public static void Validate(IReadOnlyCollection<BuiltinActionOptions>? options)
    {
        var known = Defaults().Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (options is null || options.Count > known.Count) throw new ArgumentException("Некорректный список встроенных действий.");
        foreach (var option in options)
            if (option is null || !known.Contains(option.Id) || !seen.Add(option.Id) ||
                string.IsNullOrWhiteSpace(option.Name) || option.Name.Length > 80 || option.Name.Any(char.IsControl) ||
                option.Group is null || option.Group.Length > 80 || option.Group.Any(char.IsControl))
                throw new ArgumentException("Некорректное встроенное действие: проверьте название и группу.");
    }
}
