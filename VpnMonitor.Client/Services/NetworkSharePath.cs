using System.Diagnostics;

using System.Net;
using System.Net.Sockets;

namespace VpnMonitor.Client.Services;

public static class NetworkSharePath
{
    // Windows UNC syntax: https://learn.microsoft.com/openspecs/windows_protocols/ms-dtyp/62e862f4-2a51-452e-8eeb-dc4ff5ee33cc
    public static string Create(string address, string? share = null)
    {
        if (!IPAddress.TryParse(address, out var ip))
            throw new ArgumentException("У клиента нет корректного VPN IP.", nameof(address));

        var host = ip.AddressFamily == AddressFamily.InterNetworkV6
            ? ip.ToString().Replace(':', '-').Replace('%', 's') + ".ipv6-literal.net"
            : ip.ToString();
        var root = @"\\" + host;
        if (share is null) return root;

        var name = share.Trim();
        const string invalid = "\\/\"[]:;|=,+*?<>";
        if (name.Length is < 1 or > 80 || name is "." or ".." ||
            name.Any(c => char.IsControl(c) || invalid.Contains(c)))
            throw new ArgumentException("Введите только имя общей папки (например C$, ADMIN$, D$ или Backup), без пути и специальных символов.", nameof(share));
        return root + @"\" + name;
    }

    public static ProcessStartInfo ShellStartInfo(string address, string? share = null) => new()
    {
        // Open the folder object itself, not an Explorer process with a separately parsed command line.
        FileName = Create(address, share),
        Verb = "open",
        UseShellExecute = true
    };
}