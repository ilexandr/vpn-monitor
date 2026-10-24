using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
namespace AwgPrototype;

internal static class RouteGuard
{
    internal static bool Overlaps(string a, string b)
    {
        static (byte[] Bytes, int Prefix) Parse(string text)
        {
            var p = text.Trim().Split('/');
            var ip = IPAddress.Parse(p[0]);
            var prefix = int.Parse(p[1]);
            if (prefix < 0 || prefix > ip.GetAddressBytes().Length * 8) throw new ArgumentException();
            return (ip.GetAddressBytes(), prefix);
        }
        var x = Parse(a); var y = Parse(b);
        if (x.Bytes.Length != y.Bytes.Length) return false;
        var bits = Math.Min(x.Prefix, y.Prefix);
        for (var i = 0; i < bits; i++)
            if ((x.Bytes[i / 8] & (1 << (7 - i % 8))) != (y.Bytes[i / 8] & (1 << (7 - i % 8)))) return false;
        return true;
    }

    internal static string[] Requested(string config)
    {
        var routes = new List<string>();
        foreach (var line in config.Split('\n'))
        {
            var parts = line.Split('=', 2);
            if (parts.Length == 2 && parts[0].Trim().Equals("AllowedIPs", StringComparison.OrdinalIgnoreCase))
                routes.AddRange(parts[1].Split(',').Select(p => p.Trim()));
        }
        return routes.ToArray();
    }

    internal static bool HasConflict(string config)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference='Stop'; Get-NetRoute | Where-Object { $_.InterfaceAlias -ne 'VpnMonitor.AwgPrototype' -and $_.DestinationPrefix -notin @('0.0.0.0/0','::/0') } | Select-Object -ExpandProperty DestinationPrefix" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { process.Kill(true); throw new InvalidOperationException(); }
        if (process.ExitCode != 0) throw new InvalidOperationException();
        var active = output.GetAwaiter().GetResult().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return Requested(config).Any(requested => active.Any(route => Overlaps(requested, route)));
    }
}