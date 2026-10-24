using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using VpnMonitor.Client;
using VpnMonitor.Client.Services;

internal static class DiagnosticChecks
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VpnMonitor-diagnostics-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string password = "fixture-private-password";
            var key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
            var source = $"PrivateKey = {key}\nPresharedKey={key}\nPassword=other-password\nAuthorization: Basic dXNlcjpwYXNz\n" +
                "{\"Password\":\"json-secret\",\"Authorization\":\"Bearer json-token\"}\n" +
                "{\"access_token\":\"snake-secret\",\"client_secret\":\"client-secret\"}\n" +
                "https://login:uri-pass@example.invalid/path?token=query-secret#fragment-secret\n" +
                $"Unexpected {password}\nC:\\Users\\Fixture User\\AppData\\log\nBearer standalone-token\n";
            var redactor = new DiagnosticRedactor([password]);
            var clean = redactor.Redact(source);
            foreach (var secret in new[] { key, password, "other-password", "json-secret", "json-token", "snake-secret", "client-secret", "uri-pass", "query-secret", "fragment-secret", "Fixture", "standalone-token", "dXNlcjpwYXNz" })
                Require(!clean.Contains(secret), "Diagnostic masking leaked fixture: " + secret);
            Require(clean.Contains("example.invalid/path") && clean.Contains("[скрыто]"), "Masking must retain useful server information.");
            Require(DiagnosticReport.ServerOrigin("https://login:secret@example.invalid:8443/private?token=hidden") == "https://example.invalid:8443",
                "Connection summary must exclude credentials, paths and queries.");
            var capture = new DiagnosticCapture("Отчёт: русский текст\n" + source, redactor);
            var logPath = Path.Combine(directory, "client.log");
            File.WriteAllText(logPath, "[2026-10-24 12:00:00.000] " + source);
            File.WriteAllText(logPath + ".1", "[2026-10-24 11:00:00.000] Previous " + password);
            File.WriteAllText(Path.Combine(directory, "settings.json"), "NEVER_INCLUDE_SETTINGS");
            var zipPath = Path.Combine(directory, "report.zip");
            await SupportPackage.WriteAsync(zipPath, capture, false, logPath, CancellationToken.None);
            using (var zip = ZipFile.OpenRead(zipPath)) Require(zip.Entries.Count == 2 && zip.GetEntry("client.log.txt") is null, "Logs must be opt-in.");
            await SupportPackage.WriteAsync(zipPath, capture, true, logPath, CancellationToken.None);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                Require(zip.Entries.Select(e => e.FullName).Order().SequenceEqual(new[] { "README.txt", "client.log.txt", "client.previous.log.txt", "report.txt" }.Order()), "ZIP must contain only the allowlisted files.");
                foreach (var entry in zip.Entries)
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    var content = await reader.ReadToEndAsync();
                    Require(!content.Contains(password) && !content.Contains(key) && !content.Contains("NEVER_INCLUDE_SETTINGS"), "ZIP entries must stay masked and exclude settings.");
                }
            }
            var before = File.ReadAllBytes(zipPath);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await SupportPackage.WriteAsync(zipPath, capture, true, logPath, cancelled.Token); throw new Exception("Cancelled report continued."); }
            catch (OperationCanceledException) { }
            Require(File.ReadAllBytes(zipPath).SequenceEqual(before) && Directory.GetFiles(directory, "*.tmp").Length == 0, "Cancellation must preserve existing reports and remove temporary files.");
            try { await SupportPackage.WriteAsync(zipPath, capture, true, directory, CancellationToken.None); throw new Exception("Invalid log path accepted."); }
            catch (UnauthorizedAccessException) { }
            Require(File.ReadAllBytes(zipPath).SequenceEqual(before) && Directory.GetFiles(directory, "*.tmp").Length == 0, "Export failure must preserve the destination.");

            var updates = Path.Combine(directory, "updates"); Directory.CreateDirectory(updates);
            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(updates, "update-" + id + ".json"), JsonSerializer.Serialize(new { Id = id, Phase = "Failed", Message = "tunnel restoration failed" }));
            File.WriteAllText(Path.Combine(updates, "update-" + Guid.NewGuid().ToString("N") + ".json"), "corrupt");
            var summary = UpdateJournalSummary.Read(updates);
            Require(summary?.Id == id && summary.PhaseText == "Ошибка" && summary.Message.Contains("restoration"), "Public update summary must skip corrupt records and retain the useful result.");
            using (var writer = new FileStream(Path.Combine(updates, "update-" + id + ".json"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                Require(UpdateJournalSummary.Read(updates)?.Id == id, "Diagnostic reading must coexist with the updater's writable state handle.");
            File.WriteAllText(Path.Combine(updates, "update-not-a-guid.json"), "{}");
            Require(UpdateJournalSummary.Read(updates)?.Id == id, "Unexpected state filenames must be ignored.");
            await CheckRollingLogAsync(directory);
            await CheckWindowsAsync(capture);
            Console.WriteLine("PASS: diagnostic secret masking, report ZIP allowlist/opt-in, Unicode, cancellation/failure preservation, public update result, log rotation and diagnostic UI.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task CheckRollingLogAsync(string directory)
    {
        var path = Path.Combine(directory, "rolling.log");
        File.WriteAllText(path, string.Concat(Enumerable.Repeat("Старая запись\n", 1000)), new UTF8Encoding(false));
        for (var i = 0; i < 80; i++) RollingLog.Append(path, $"[{i}] Русская запись {new string('x', 80)}\n", 1024);
        Require(File.Exists(path + ".3") && !File.Exists(path + ".4"), "Rotation must keep exactly three backups.");
        foreach (var file in Directory.GetFiles(directory, "rolling.log*"))
        {
            Require(new FileInfo(file).Length <= 1024, "Current and legacy oversized logs must be bounded.");
            _ = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(file));
        }
        Require(File.ReadAllText(path).Contains("[79]"), "Rotation must retain the most recent event.");
        RollingLog.Append(path, new string('Ж', 10000), 1024);
        Require(new FileInfo(path).Length <= 1024 && File.ReadAllText(path).Contains("сокращена"), "Oversized individual events must also be bounded.");

        var shared = Path.Combine(directory, "shared.log");
        Process StartChild(string marker)
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach (var argument in new[] { "--rolling-log-child", shared, marker }) info.ArgumentList.Add(argument);
            return Process.Start(info)!;
        }
        using var first = StartChild("one"); using var second = StartChild("two");
        await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(10));
        Require(first.ExitCode == 0 && second.ExitCode == 0 && File.ReadAllLines(shared).Length == 20, "Independent processes must coordinate writes to one user's log.");
    }

    private static async Task CheckWindowsAsync(DiagnosticCapture capture)
    {
        var window = new DiagnosticsWindow(() => Task.FromResult(capture));
        await (Task)typeof(DiagnosticsWindow).GetMethod("RefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
        Require(((TextBox)window.FindName("ReportText")).IsReadOnly && ((TextBox)window.FindName("ReportText")).Text.Contains("русский текст"), "Diagnostic report must render read-only Unicode text.");
        Require(((CheckBox)window.FindName("IncludeLogsBox")).IsChecked != true && ((Button)window.FindName("ExportButton")).IsEnabled, "Log inclusion must default off and export becomes available after capture.");
        ((Button)window.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var invoked = false;
        var about = new AboutWindow(() => invoked = true);
        ((Button)about.FindName("DiagnosticsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); about.Close();
        Require(invoked, "About window must route diagnostics to the main application.");
        var viewer = new LogWindow();
        Require(((ComboBox)viewer.FindName("LogChoiceBox")).Items.Count == 4, "Log viewer must offer current and three rotated files.");
        viewer.Close();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
