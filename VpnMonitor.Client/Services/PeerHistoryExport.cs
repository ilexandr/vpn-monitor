using System.Text;
namespace VpnMonitor.Client.Services;

public static class PeerHistoryExport
{
    public static bool Matches(PeerHistoryEntry entry, string peerId, string query) =>
        entry.PeerId == peerId && query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(word => $"{entry.LocalTime} {entry.Source} {entry.Previous} {entry.Current}"
                .Contains(word, StringComparison.OrdinalIgnoreCase));

    public static string Create(IEnumerable<PeerHistoryEntry> entries)
    {
        var output = new StringBuilder();
        PeerCsvExport.Add(output, ["Время", "Источник", "Было", "Стало"]);
        foreach (var entry in entries)
            PeerCsvExport.Add(output, [entry.Time.ToString("O"), entry.Source, entry.Previous, entry.Current]);
        return output.ToString();
    }
}
