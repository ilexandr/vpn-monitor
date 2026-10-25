using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using VpnMonitor.Client;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;

internal static class HistoryRefreshChecks
{
    public static async Task RunAsync()
    {
        if (System.Windows.Application.Current is null)
        {
            var app = new App(false); app.InitializeComponent();
        }
        System.Windows.Application.Current!.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var directory = Path.Combine(Path.GetTempPath(), "VpnMonitor-history-refresh-" + Guid.NewGuid().ToString("N"));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var stop = new CancellationTokenSource();
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    try
                    {
                        using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                        var request = await reader.ReadLineAsync(stop.Token) ?? "";
                        var headers = new StringBuilder(); string? line;
                        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(stop.Token))) headers.AppendLine(line);
                        var admin = headers.ToString().Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:fake")));
                        var account = admin ? "admin" : "user";
                        received.TrySetResult();
                        await release.Task.WaitAsync(stop.Token);
                        var json = request.Contains("/me ") ? $"{{\"username\":\"{account}\",\"is_admin\":{(admin ? "true" : "false")},\"group\":\"user\"}}"
                            : request.Contains("/groups ") ? "{\"items\":[]}"
                            : $"{{\"items\":[{{\"id\":\"{account}\",\"status\":\"running\",\"groups\":[\"user\"]}}]}}";
                        var response = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(json)}\r\nConnection: close\r\n\r\n{json}");
                        await stream.WriteAsync(response, stop.Token);
                    }
                    catch (IOException) { /* Reconfiguration intentionally closes the old HTTP connection. */ }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        MainWindow? window = null;
        try
        {
            var settingsService = new SettingsService(Path.Combine(directory, "settings"));
            settingsService.Save(new ClientSettings { ServerUrl = url, Username = "admin", ProtectedPassword = settingsService.ProtectPassword("fake"),
                ActiveChecksEnabled = false, SavePeerHistory = true, StatusNotificationsEnabled = false });
            var archiveDirectory = Path.Combine(directory, "history");
            var seed = new PeerHistoryArchive(archiveDirectory);
            var entry = new PeerHistoryEntry(DateTimeOffset.Now.AddMinutes(-1), "admin", "AWG", "ADMIN_ARCHIVE", "Online");
            seed.Save(PeerHistoryArchive.Scope(url, "admin"), [entry], 1);
            seed.Save(PeerHistoryArchive.Scope(url, "user"), [entry with { PeerId = "user", Previous = "USER_ARCHIVE" }, entry with { PeerId = "foreign" }], 2);
            var incidentSeed = new IncidentArchive(Path.Combine(settingsService.SettingsDirectory, "incidents"));
            var incidentTime = DateTimeOffset.Now.AddMinutes(-1);
            var incident = new Incident(Guid.NewGuid().ToString("N"), "peer:admin", "Client", "ADMIN_INCIDENT", "admin", incidentTime, incidentTime, incidentTime, "Восстановлено");
            incidentSeed.Save(PeerHistoryArchive.Scope(url, "admin"), [incident], 1, DateTimeOffset.Now);
            incidentSeed.Save(PeerHistoryArchive.Scope(url, "user"), [incident with { Id = Guid.NewGuid().ToString("N"), PeerId = "user", Title = "USER_INCIDENT", Key = "peer:user" }, incident with { Id = Guid.NewGuid().ToString("N"), PeerId = "foreign" }], 1, DateTimeOffset.Now);
            window = new MainWindow(settingsService, new PeerHistoryArchive(archiveDirectory));
            Configure(window);
            var first = Refresh(window);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Refresh(window);
            var history = Field<PeerHistory>(window, "_peerHistory");
            Require(history.Entries.Count == 0, "Overlapping refresh must not load or discard history before authorization completes.");
            release.TrySetResult(); await first.WaitAsync(TimeSpan.FromSeconds(5));
            Require(history.Entries.Any(e => e.Previous == "ADMIN_ARCHIVE"), "First authorized refresh must restore the existing archive.");
            var incidents = Field<IncidentTracker>(window, "_incidents");
            Require(incidents.Entries.Any(e => e.Title == "ADMIN_INCIDENT"), "Authorized main-window refresh must restore incident archives.");
            var model = Field<VpnMonitor.Client.ViewModels.MainViewModel>(window, "_viewModel");
            var row = model.Peers.Single(); var resets = 0;
            model.PeersView.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };
            await Refresh(window).WaitAsync(TimeSpan.FromSeconds(5));
            Require(ReferenceEquals(row, model.Peers.Single()) && resets == 0,
                "Ordinary polling must preserve client rows and avoid view resets that dismiss hover tooltips.");
            var updated = new Peer { Id = row.Id, Name = "Renamed", Status = "stopped", Receive = 12, Groups = ["new"] };
            var changed = new HashSet<string?>(); row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            row.UpdateFrom(updated);
            Require(!row.IsOnline && row.Receive == 12 && changed.Contains(nameof(Peer.StatusText)) && changed.Contains(nameof(Peer.TrafficText)) && changed.Contains(nameof(Peer.GroupsText)),
                "Preserved rows must notify status, traffic and group bindings when API values change.");
            var diagnostics = await Capture(window);
            Require(diagnostics.Report.Contains("Администратор API") && !diagnostics.Report.Contains("Последний успешный ответ API: —"),
                "Main-window diagnostics must report the last authenticated API success.");

            received = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldRefresh = Refresh(window);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Field<ClientSettings>(window, "_settings").Username = "user";
            Configure(window);
            await Refresh(window);
            Require(history.Entries.Count == 0, "Account switch must immediately hide prior history while an old request is pending.");
            Require(incidents.Entries.Count == 0, "Account switch must clear previous incidents before the new authorization finishes.");
            diagnostics = await Capture(window);
            Require(diagnostics.Report.Contains("Режим доступа: Не подтверждён") && diagnostics.Report.Contains("Последний успешный ответ API: —"),
                "Diagnostic account switch must clear prior role and successful API timestamps.");
            release.TrySetResult(); await oldRefresh.WaitAsync(TimeSpan.FromSeconds(5));
            Require(history.Entries.Count == 0, "Late completion of an old account must not authorize or erase the new account archive.");
            await Refresh(window).WaitAsync(TimeSpan.FromSeconds(5));
            Require(history.Entries.Any(e => e.Previous == "USER_ARCHIVE") && history.Entries.All(e => e.PeerId == "user"),
                "The new account must restore only its authorized history after a fresh response.");
            Require(incidents.Entries.Any(e => e.Title == "USER_INCIDENT") && incidents.Entries.All(e => e.PeerId is null or "user"),
                "Incident restore must retain only the new account's authorized peer IDs.");
            var count = history.Entries.Count;
            Configure(window);
            Require(history.Entries.Count == count, "Applying ordinary settings must preserve current history.");
            Console.WriteLine("PASS: real main-window delayed API, overlapping refresh, pending account switch, authorized archive restore and same-account settings preservation.");
        }
        finally
        {
            release.TrySetResult(); window?.Close(); stop.Cancel(); await server;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    private static void Configure(MainWindow window) => typeof(MainWindow).GetMethod("EnsureConfigured", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [false]);
    private static Task Refresh(MainWindow window) => (Task)typeof(MainWindow).GetMethod("RefreshDataAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
    private static Task<DiagnosticCapture> Capture(MainWindow window) => (Task<DiagnosticCapture>)typeof(MainWindow).GetMethod("CaptureDiagnosticsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
