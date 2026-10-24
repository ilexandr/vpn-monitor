using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

namespace VpnMonitor.Client.Services;

public sealed class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VpnMonitor.Client";

    public bool IsEnabledForCurrentExecutable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var currentValue = key?.GetValue(ValueName) as string;

        if (string.IsNullOrWhiteSpace(currentValue))
            return false;

        return string.Equals(
            currentValue.Trim(),
            BuildCommand(),
            StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new InvalidOperationException("Не удалось открыть раздел автозапуска текущего пользователя Windows.");

        if (enabled)
        {
            key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string BuildCommand()
    {
        var path = GetExecutablePath();
        return $"\"{path}\"";
    }

    private static string GetExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            return Path.GetFullPath(Environment.ProcessPath);

        using var process = Process.GetCurrentProcess();
        var path = process.MainModule?.FileName;

        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Не удалось определить путь к исполняемому файлу VPN Monitor.");

        return Path.GetFullPath(path);
    }
}
