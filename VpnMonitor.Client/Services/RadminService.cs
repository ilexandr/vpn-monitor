using System.IO;
using System.Diagnostics;
using Microsoft.Win32;

namespace VpnMonitor.Client.Services;

public sealed class RadminService
{
    public const int DefaultPort = 4899;

    public string? ResolveExecutable(string? configuredPath)
    {
        if (IsExecutable(configuredPath))
            return Path.GetFullPath(configuredPath!);

        var candidates = new List<string?>
        {
            Path.Combine(AppContext.BaseDirectory, "Radmin.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Radmin Viewer 3", "Radmin.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Radmin Viewer 3", "Radmin.exe"),
            TryReadAppPath(RegistryHive.CurrentUser, RegistryView.Default),
            TryReadAppPath(RegistryHive.LocalMachine, RegistryView.Registry32),
            TryReadAppPath(RegistryHive.LocalMachine, RegistryView.Registry64),
            FindOnPath()
        };

        return candidates.FirstOrDefault(IsExecutable);
    }

    public Process StartFullControl(string executablePath, string host, int port) =>
        Start(executablePath, host, port, fileTransfer: false);

    public Process StartFileTransfer(string executablePath, string host, int port) =>
        Start(executablePath, host, port, fileTransfer: true);

    private static Process Start(string executablePath, string host, int port, bool fileTransfer)
    {
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("Radmin Viewer не найден.", executablePath);

        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Не указан адрес удаленного компьютера.", nameof(host));

        if (port is < 1 or > 65535)
            port = DefaultPort;

        var arguments = $"/connect:{host}:{port}" + (fileTransfer ? " /file" : "");
        return Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Не удалось запустить Radmin Viewer.");
    }

    private static bool IsExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private static string? TryReadAppPath(RegistryHive hive, RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Radmin.exe");
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindOnPath()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path))
                return null;

            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory.Trim(), "Radmin.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch
        {
            // Detection is best effort only.
        }

        return null;
    }
}
