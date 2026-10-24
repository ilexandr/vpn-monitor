using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;

namespace VpnMonitor.Client.Services;

public static class SshService
{
    public static string? ResolveExecutable()
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        if (File.Exists(system)) return system;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(directory.Trim().Trim('"'), "ssh.exe"));
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }
        return null;
    }

    public static ProcessStartInfo CreateStartInfo(string executable, string address, string username, int port)
    {
        if (!Path.IsPathFullyQualified(executable))
            throw new ArgumentException("Не найден SSH-клиент.");
        if (!IPAddress.TryParse(address, out var ip))
            throw new ArgumentException("У клиента нет корректного VPN IP.");
        var user = username.Trim();
        const string allowed = "_-.@\\$";
        if (user.Length is < 1 or > 256 || user.StartsWith('-') ||
            user.Any(c => !char.IsLetterOrDigit(c) && !allowed.Contains(c)))
            throw new ArgumentException("Укажите логин SSH без пробелов и специальных команд.");
        if (port is < 1 or > 65535)
            throw new ArgumentException("Порт SSH должен быть от 1 до 65535.");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("-l");
        start.ArgumentList.Add(user);
        start.ArgumentList.Add(ip.ToString());
        return start;
    }
}