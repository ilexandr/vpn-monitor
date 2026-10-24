using System.Diagnostics;
using System.IO;
using System.Net;
using VpnMonitor.Client.Models;
namespace VpnMonitor.Client.Services;
public static class CustomActionService
{
    public static void Validate(CustomAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Name) || action.Name.Length > 80)
            throw new ArgumentException("Укажите название действия (до 80 символов).");
        if ((action.Group?.Length ?? 0) > 80 || (action.Group?.Any(char.IsControl) ?? false))
            throw new ArgumentException("Название группы: до 80 символов без управляющих символов.");
        if (!Path.IsPathFullyQualified(action.Executable) ||
            !string.Equals(Path.GetExtension(action.Executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Выберите полный путь к программе .exe.");
        if (action.Arguments is null || action.Arguments.Count > 100 ||
            action.Arguments.Any(a => a is null || a.Length > 4096 || a.Any(char.IsControl)))
            throw new ArgumentException("Допускается до 100 аргументов без управляющих символов, каждый до 4096 символов.");
    }
    public static ProcessStartInfo CreateStartInfo(CustomAction action, string address)
    {
        Validate(action);
        if (!IPAddress.TryParse(address, out var ip)) throw new ArgumentException("У клиента нет корректного VPN IP.");
        var info = new ProcessStartInfo(action.Executable) { UseShellExecute = true };
        foreach (var argument in action.Arguments)
            info.ArgumentList.Add(argument.Replace("{ip}", ip.ToString(), StringComparison.Ordinal));
        return info;
    }
}