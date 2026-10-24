using System.Globalization;
using System.Text;
using VpnMonitor.Client.Models;
namespace VpnMonitor.Client.Services;
public static class PeerCsvExport
{
    public static string Create(IEnumerable<Peer> peers)
    {
        var output = new StringBuilder();
        Add(output, ["Имя", "VPN IP", "AWG", "Доступность", "Проверено", "Endpoint", "Handshake",
            "Получено", "Отправлено", "Группы", "Избранное", "Заметки"]);
        foreach (var peer in peers)
            Add(output, [peer.Name, peer.VpnIp, peer.AwgState.IsCurrent ? peer.StatusText : "Неизвестно",
                peer.Reachability.Text, peer.Reachability.CheckedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                peer.Endpoint, peer.HandshakeText, peer.ReceiveText, peer.SentText,
                peer.GroupsText, peer.IsFavorite ? "Да" : "Нет", peer.Notes]);
        return output.ToString();
    }
    private static void Add(StringBuilder output, IEnumerable<string?> values) =>
        output.AppendJoin(';', values.Select(Cell)).Append("\r\n");

    private static string Cell(string? value)
    {
        value ??= "";
        // Keep spreadsheet applications from treating exported names/notes as formulas.
        var first = value.TrimStart();
        if (first.Length > 0 && "=+-@".Contains(first[0]) || value.StartsWith('\t') || value.StartsWith('\r'))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}