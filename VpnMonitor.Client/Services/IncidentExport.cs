using System.Text;

namespace VpnMonitor.Client.Services;

public static class IncidentExport
{
    public static string Create(IEnumerable<Incident> incidents)
    {
        var text = new StringBuilder("Начало;Уровень;Событие;Длительность;Завершение;Результат\r\n");
        foreach (var entry in incidents)
            text.AppendLine(string.Join(";", new[] { entry.StartedAt.ToString("O"), entry.Level, entry.Title, entry.DurationText,
                entry.EndedAt?.ToString("O") ?? "", entry.Result }.Select(Quote)));
        return text.ToString();
    }
    private static string Quote(string value)
    {
        if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
