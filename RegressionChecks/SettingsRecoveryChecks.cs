using System.IO;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;

internal static class SettingsRecoveryChecks
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VpnMonitor-settings-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new SettingsService(directory);
            var password = service.ProtectPassword("fixture-password");
            service.Save(new ClientSettings { Username = "first", ProtectedPassword = password, Theme = "dark" });
            service.Save(new ClientSettings { Username = "second", ProtectedPassword = password, Theme = "light" });
            Require(service.Load().Username == "second", "Atomic save must publish the current settings.");
            File.WriteAllText(service.SettingsPath, "{corrupted");
            var restored = service.Load();
            Require(restored.Username == "first" && restored.Theme == "dark" && service.UnprotectPassword(restored.ProtectedPassword) == "fixture-password",
                "A corrupt primary file must load the previous valid settings including DPAPI credentials.");
            var previous = File.ReadAllBytes(service.PreviousSettingsPath);
            service.Save(restored);
            Require(File.ReadAllBytes(service.PreviousSettingsPath).SequenceEqual(previous), "Saving after recovery must preserve a good backup instead of replacing it with corrupt data.");
            using (var reader = new FileStream(service.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                service.Save(new ClientSettings { Username = "third", ProtectedPassword = password });
            Require(service.Load().Username == "third" && Directory.GetFiles(directory, "*.tmp").Length == 0, "Settings readers must not block replacement, and temporary files must be cleaned.");
            Require(new SettingsService().SettingsDirectory.Contains(Path.Combine("artifacts", "regression", "settings")), "Default services in checks must be isolated from the user's settings.");
            Require(VpnMonitor.Client.App.ClientLogPath.Contains(Path.Combine("artifacts", "regression", "logs")), "Checks must not write the user's client log.");
            Console.WriteLine("PASS: atomic settings, previous valid snapshot, corruption recovery with DPAPI, shared reader and process-wide test settings/log isolation.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
