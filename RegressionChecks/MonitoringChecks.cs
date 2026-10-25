using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VpnMonitor.Client;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;
using VpnMonitor.Client.ViewModels;

internal static class MonitoringChecks
{
    public static async Task RunAsync()
    {
        if (System.Windows.Application.Current is null) { var app = new App(false); app.InitializeComponent(); }
        System.Windows.Application.Current!.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Incidents(); Rules(); ViewsAndTransfer(); await ServicesAsync(); await WindowsAsync();
        Console.WriteLine("PASS: incident correlation/duration/unknown/DPAPI, notification delay/quiet/maintenance, scoped views, service profiles/budget/recovery and themed monitoring/AWG UI.");
    }
    private static void Incidents()
    {
        var now = DateTimeOffset.Now;
        var tracker = new IncidentTracker();
        tracker.Observe("connection", "API", "API unavailable", null, false, now);
        tracker.Observe("connection", "VPN", "VPN/server unavailable", null, false, now.AddSeconds(10));
        Require(tracker.Entries.Count == 1 && tracker.Entries[0].Level == "VPN" && tracker.Entries[0].Duration == TimeSpan.FromSeconds(10), "Global outage must correlate and preserve its original start.");
        tracker.Observe("connection", "API", "restored", null, true, now.AddSeconds(20));
        Require(tracker.Entries[0].Duration == TimeSpan.FromSeconds(20) && tracker.Entries[0].Result == "Восстановлено", "Recovery must close the outage with measured duration.");
        tracker.Observe("peer:own", "Client", "own offline", "own", false, now);
        tracker.Observe("peer:own", "Client", "own offline", "own", false, now.AddSeconds(5));
        tracker.InterruptClients(now.AddSeconds(100));
        var own = tracker.Entries.Single(i => i.PeerId == "own");
        Require(own.Duration == TimeSpan.FromSeconds(5) && own.Result == "Наблюдение прервано", "Unknown observations must not count unobserved downtime.");
        var foreign = own with { Id = Guid.NewGuid().ToString("N"), Key = "peer:foreign", PeerId = "foreign", EndedAt = null };
        tracker.Restore([foreign], new HashSet<string> { "own" }, now.AddSeconds(100));
        Require(tracker.Entries.All(i => i.PeerId != "foreign"), "Unauthorized incident archives must not populate the UI.");
        tracker.ClearCompleted(); Require(tracker.Entries.Count == 0, "Clearing completed incidents must remove closed events.");
        tracker.Observe("peer:own", "Client", "current", "own", false, now); tracker.ClearCompleted();
        Require(tracker.Entries.Count == 1, "Clearing completed events must preserve open outages.");
        var directory = Path.Combine(Path.GetTempPath(), "VpnMonitor-incidents-" + Guid.NewGuid().ToString("N"));
        try
        {
            var scope = PeerHistoryArchive.Scope("http://example.test", "user");
            var archive = new IncidentArchive(directory);
            archive.Save(scope, tracker.Entries.ToArray(), 2, now);
            var path = Path.Combine(directory, scope + ".dpapi");
            Require(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("current"), "Incident files must be DPAPI encrypted.");
            var restored = new IncidentTracker(); restored.Restore(archive.Load(scope, now), new HashSet<string> { "own" }, now);
            Require(restored.Entries.Single().Result == "Наблюдение прервано", "Restart must interrupt an old active incident rather than extend downtime.");
            archive.Save(scope, [], 3, now); archive.Save(scope, tracker.Entries.ToArray(), 2, now);
            Require(archive.Load(scope, now).Length == 0, "An older queued save must not resurrect cleared events.");
            archive.ClearAll(4); archive.Save(scope, tracker.Entries.ToArray(), 3, now);
            Require(!File.Exists(path), "Opt-out must block stale writes.");
            var csv = IncidentExport.Create([own with { Title = "=formula;\"name\"" }]);
            Require(csv.Contains("'=formula") && csv.Contains("\"\"name\"\""), "Incident CSV must escape text and neutralize spreadsheet formulas.");
        }
        finally
        {
            if (Directory.Exists(directory))
            { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        }
    }
    private static void Rules()
    {
        var now = new DateTimeOffset(DateTime.Today.AddHours(12));
        var rules = new NotificationRules { OfflineDelaySeconds = 30 };
        var policy = new NotificationPolicy();
        var offline = new NotificationCandidate("peer:a", "A", "offline", "a", ["Office"], false);
        policy.Enqueue(offline, rules, now);
        Require(policy.Drain(rules, now.AddSeconds(29), _ => false).Count == 0, "Offline delay must wait for the configured duration.");
        policy.Enqueue(offline with { Online = true }, rules, now.AddSeconds(10));
        Require(policy.Drain(rules, now.AddSeconds(40), _ => true).Count == 0 && policy.Decisions.Any(d => d.Reason.Contains("Краткий")), "A recovered short outage must cancel its delayed pair.");
        policy.Enqueue(offline, rules, now);
        Require(policy.Drain(rules, now.AddSeconds(30), _ => false).Count == 1 && policy.Drain(rules, now.AddSeconds(31), _ => false).Count == 0, "A stable outage must produce exactly one decision.");
        rules.QuietHoursEnabled = true; rules.QuietStartMinute = 22 * 60; rules.QuietEndMinute = 8 * 60;
        Require(NotificationPolicy.IsQuiet(rules, new(DateTime.Today.AddHours(23))) && NotificationPolicy.IsQuiet(rules, new(DateTime.Today.AddHours(7))) &&
            !NotificationPolicy.IsQuiet(rules, now) && !NotificationPolicy.IsQuiet(rules, new(DateTime.Today.AddHours(8))), "Quiet hours must cross midnight with an exclusive end.");
        policy.Enqueue(offline, rules, now); Require(policy.Drain(rules, new(DateTime.Today.AddHours(23)), _ => false).Count == 0, "Quiet hours must suppress rather than defer events.");
        rules.QuietHoursEnabled = false; rules.Maintenance = [new("Office", now.AddHours(1))];
        policy.Enqueue(offline, rules, now); Require(policy.Drain(rules, now.AddMinutes(1), _ => false).Count == 0 && policy.Decisions[0].Reason.Contains("обслуживании"), "Group maintenance must explain suppression.");
        rules.Maintenance.Clear(); policy.Enqueue(offline, rules, now);
        Require(policy.Drain(rules, now.AddMinutes(1), _ => null).Count == 0, "Lost API/tunnel observations must not release stale delayed Offline messages.");
        policy.Enqueue(offline, rules, now); policy.CancelPeer("a", now);
        Require(policy.Drain(rules, now.AddMinutes(1), _ => false).Count == 0, "Removing a watched peer must cancel delayed notifications.");
        for (var i = 0; i < 150; i++) policy.Record(now, "test", "reason");
        Require(policy.Decisions.Count == 100, "Notification audit must remain bounded.");
    }
    private static void ViewsAndTransfer()
    {
        var scope = PeerHistoryArchive.Scope("http://example.test", "user");
        using var model = new MainViewModel(); model.AwgState.IsCurrent = true;
        model.Groups.Add(new() { Name = "Office" }); model.SelectedGroup = "Office"; model.SearchText = "Test PC"; model.Filter = "offline";
        var settings = new ClientSettings(); var view = SavedViewService.Capture("Office offline", scope, model, settings);
        settings.SavedViews.Add(view);
        Require(SavedViewService.Visible(settings, scope, model).Count() == 1 && !SavedViewService.Visible(settings, new string('A', 64), model).Any(), "Views must be isolated by server/account.");
        model.SearchText = "changed"; model.SelectedGroup = null;
        Require(SavedViewService.Apply(view, scope, model, settings) && model.SearchText == "Test PC" && model.Filter == "offline" && model.SelectedGroup == "Office", "Views must restore search/filter/group together.");
        model.Groups.Clear(); Require(!SavedViewService.Apply(view, scope, model, settings), "A removed group must not be restored from a saved view.");
        model.AwgState.IsCurrent = false; Require(!SavedViewService.Visible(settings, scope, model).Any(), "Unconfirmed identity must not reveal saved view names.");
        settings.MonitoringScopes = [new() { Scope = scope, Settings = new() { Profiles = [new() { Name = "RDP", Ports = [new("RDP", 3389)] }] } }];
        var copy = SettingsTransfer.Import(SettingsTransfer.Export(settings));
        Require(copy.MonitoringScopes[0].Settings.Profiles[0].Ports[0].Port == 3389 && copy.SavedViews[0].Search == "Test PC", "Monitoring and views must survive settings transfer without credentials.");
        var profile = settings.MonitoringScopes[0].Settings.Profiles[0]; profile.IntervalSeconds = 1;
        try { MonitoringConfiguration.ValidateLists(settings); throw new Exception("Unsafe service interval accepted."); } catch (ArgumentException) { }
        Require(MonitoringWindow.ParsePorts("RDP:3389\nSMB:445").Count == 2, "The profile editor must parse named TCP ports.");
    }
    private static async Task ServicesAsync()
    {
        var now = DateTimeOffset.Now; var available = true; var calls = 0; var running = 0; var maximum = 0;
        var service = new ServiceMonitor(async (_, _, token) =>
        {
            Interlocked.Increment(ref calls); var active = Interlocked.Increment(ref running); maximum = Math.Max(maximum, active);
            try { await Task.Delay(2, token); return available; } finally { Interlocked.Decrement(ref running); }
        }, (_, _) => Task.FromResult(true), () => now);
        var settings = new MonitoringSettings { ServiceChecksEnabled = true, ParallelChecks = 3,
            Profiles = [new() { Name = "Default", Ports = [new("RDP", 3389)], IntervalSeconds = 5 },
                new() { Name = "Group", TargetKind = "group", Target = "Office", Ports = [new("SMB", 445)], IntervalSeconds = 5 },
                new() { Name = "Peer", TargetKind = "peer", Target = "a", Ports = [new("Radmin", 4899)], IntervalSeconds = 5 }] };
        var peers = new[] { new Peer { Id = "a", VpnIp = "127.0.0.1", Groups = ["Office"] }, new Peer { Id = "b", VpnIp = "127.0.0.1", Groups = ["Office"] }, new Peer { Id = "c", VpnIp = "127.0.0.1" } };
        await service.ScanAsync(peers, settings, new("http://127.0.0.1"), default);
        Require(service.Get(peers[0], settings)?.ProfileName == "Peer" && service.Get(peers[1], settings)?.ProfileName == "Group" && service.Get(peers[2], settings)?.ProfileName == "Default", "Profiles must inherit peer over group over default.");
        var count = calls; await service.ScanAsync(peers, settings, new("http://127.0.0.1"), default);
        Require(calls == count && maximum <= 3, "Service interval and configured concurrency must be enforced.");
        available = false;
        for (var i = 0; i < 3; i++) { now = now.AddSeconds(5); await service.ScanAsync(peers, settings, new("http://127.0.0.1"), default); }
        Require(service.Get(peers[0], settings)?.Healthy == false, "A service outage must be confirmed by three failures.");
        available = true; now = now.AddSeconds(5); await service.ScanAsync(peers, settings, new("http://127.0.0.1"), default);
        Require(service.Get(peers[0], settings)?.Healthy == true, "Service recovery must be visible independently of computer availability.");
        peers[0].Status = "stopped";
        var beforeOffline = calls; now = now.AddSeconds(5);
        await service.ScanAsync([peers[0]], settings, new("http://127.0.0.1"), default);
        Require(calls == beforeOffline && service.Get(peers[0], settings) is { ClientOffline: true, Healthy: null },
            "Offline hosts must pause service probes and cannot report a service failure.");
        peers[0].Status = "running"; available = false;
        await service.ScanAsync([peers[0]], settings, new("http://127.0.0.1"), default);
        Require(service.Get(peers[0], settings)?.Ports[0].Failures == 1 && service.Get(peers[0], settings)?.Healthy is null,
            "A recovered host must restart service failure confirmation rather than inherit an old outage.");
        peers[0].Reachability = new(true, 1, 0, "ping", DateTime.Now); peers[0].Status = "stopped";
        Require(ServiceMonitor.CanCheck(peers[0]), "Active reachability must take priority over stale AWG Offline.");
        peers[0].Reachability = new(false, null, 3, "ping", DateTime.Now);
        Require(!ServiceMonitor.CanCheck(peers[0]), "Confirmed active unreachability must pause service checks.");
        peers[0].Reachability = ReachabilityResult.Unknown(); peers[0].Status = "running"; available = true;
        peers[0].VpnIp = "127.0.0.2"; Require(service.Get(peers[0], settings) is null, "Changing a target address must invalidate prior service results.");
        var references = 0;
        var interrupted = new ServiceMonitor((_, _, _) => Task.FromResult(false), (_, _) => Task.FromResult(++references == 1), () => now);
        Require(await interrupted.ScanAsync(peers, settings, new("http://127.0.0.1"), default) == false && interrupted.Get(peers[1], settings) is null,
            "Reference loss during a cycle must discard service failures.");
        calls = 0;
        var many = Enumerable.Range(0, 30).Select(i => new Peer { Id = "p" + i, VpnIp = "127.0.0.1" }).ToArray();
        var budget = new MonitoringSettings { ServiceChecksEnabled = true, Profiles = [new() { Ports = Enumerable.Range(1, 8).Select(i => new ServicePort("TCP" + i, 1000 + i)).ToList() }] };
        await service.ScanAsync(many, budget, new("http://127.0.0.1"), default);
        Require(calls == 128, "One cycle must not exceed its TCP request budget.");
        await service.ScanAsync(many, budget, new("http://127.0.0.1"), default);
        Require(calls == 240 && many.All(p => service.Get(p, budget) is not null), "Budgeted cycles must fairly check remaining clients.");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellable = new ServiceMonitor(async (_, _, token) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return true; }, (_, _) => Task.FromResult(true));
        using (var stop = new CancellationTokenSource())
        {
            var scan = cancellable.ScanAsync(peers, settings, new("http://127.0.0.1"), stop.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(await cancellable.ScanAsync(peers, settings, new("http://127.0.0.1"), default) is null, "Service cycles must not overlap.");
            stop.Cancel();
            try { await scan; throw new Exception("Cancelled service cycle completed."); } catch (OperationCanceledException) { }
            Require(cancellable.Get(peers[1], settings) is null, "Cancellation must not publish partial service results.");
        }
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var native = new ServiceMonitor(reference: (_, _) => Task.FromResult(true));
        var nativeSettings = new MonitoringSettings { ServiceChecksEnabled = true, Profiles = [new() { Ports = [new("Loopback", port)] }] };
        Require(await native.ScanAsync([peers[1]], nativeSettings, new("http://127.0.0.1"), default) == true && native.Get(peers[1], nativeSettings)?.Healthy == true, "The native TCP probe must connect to a real local service.");
    }
    private static async Task WindowsAsync()
    {
        var scope = PeerHistoryArchive.Scope("http://example.test", "test");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ThemeService.Apply(theme);
            var saved = new MonitoringSettings { Profiles = [new() { Name = "Office services", TargetKind = "group", Target = "Office", Ports = [new("RDP", 3389), new("SMB", 445)] },
                new() { Name = "Default RDP", Ports = [new("RDP", 3389)] }] };
            var tracker = new IncidentTracker(); tracker.Observe("connection", "Сервер/API", "API недоступен", null, false, DateTimeOffset.Now);
            var views = new List<SavedClientView>(); var saves = 0;
            var context = new MonitoringContext
            {
                Read = () => saved, Save = s => { saved = s; saves++; }, Authorized = () => true,
                Peers = () => [new() { Id = "a", Name = "Office PC", Groups = ["Office"] }], Groups = () => ["Office"],
                Incidents = tracker, Policy = new(), ClearCompleted = tracker.ClearCompleted,
                Views = () => views.ToArray(), SaveView = n => views.Add(new() { Name = n, Scope = scope }), ApplyView = _ => true, DeleteView = v => views.Remove(v)
            };
            var window = new MonitoringWindow(context) { ShowInTaskbar = false, ShowActivated = false, Left = SystemParameters.VirtualScreenLeft - 4000, Top = SystemParameters.VirtualScreenTop };
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(Equals(window.Foreground, System.Windows.Application.Current!.FindResource("TextBrush")), "Monitoring window must use the current theme foreground.");
                var navigation = (ListBox)window.FindName("Navigation");
                for (var page = 0; page < 4; page++)
                { navigation.SelectedIndex = page; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Render((FrameworkElement)window.Content, "monitoring-" + theme + "-" + page); }
                navigation.SelectedIndex = 1;
                ((TextBox)window.FindName("ProfileNameBox")).Text = "Edited";
                var profiles = (ListBox)window.FindName("ProfilesList");
                profiles.SelectedIndex = 1; profiles.SelectedIndex = 0;
                Require(((TextBox)window.FindName("ProfileNameBox")).Text == "Edited", "Switching between profile rows must preserve draft edits and row identity.");
                ((CheckBox)window.FindName("ServicesEnabledBox")).IsChecked = true;
                ((TextBox)window.FindName("DelayBox")).Text = "45";
                ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(saves == 1 && saved.ServiceChecksEnabled && saved.Profiles[0].Name == "Edited" && saved.Notifications.OfflineDelaySeconds == 45,
                    "The monitoring editor must save typed profile and rule values together.");
                var original = saved.Profiles[0].Name; ((TextBox)window.FindName("ProfileNameBox")).Text = "Unsaved";
                Require(saved.Profiles[0].Name == original, "Draft profile edits must remain private until Apply.");
                window.Width = 860; window.Height = 560; navigation.SelectedIndex = 2;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Render((FrameworkElement)window.Content, "monitoring-min-" + theme);
            }
            finally { window.Close(); }
            var catalog = new AwgProfileCatalog { SelectedId = new string('a', 32), Profiles = [new(new string('a', 32), "Office VPN"), new(new string('b', 32), "Reserve VPN")] };
            uint state = 1;
            var manager = new AwgManagementWindow((_, _) => throw new Exception("Read-only style checks must never execute an AWG command"), () => (catalog, state, state == 1 ? "Остановлена" : "Работает"));
            try
            {
                manager.RefreshState();
                Require(((Button)manager.FindName("StartButton")).IsEnabled && !((Button)manager.FindName("StopButton")).IsEnabled, "Stopped AWG must expose Connect and disable Disconnect.");
                state = 4; manager.RefreshState();
                Require(Equals(manager.Foreground, System.Windows.Application.Current!.FindResource("TextBrush")), "AWG manager must share the client foreground palette.");
                Require(!((Button)manager.FindName("StartButton")).IsEnabled && ((Button)manager.FindName("StopButton")).IsEnabled && !((Button)manager.FindName("SelectButton")).IsEnabled,
                    "Running AWG must disable profile switching and expose Disconnect.");
                var content = (FrameworkElement)manager.Content; content.Measure(new Size(800, 560)); content.Arrange(new Rect(0, 0, 800, 560)); content.UpdateLayout();
                Render(content, "awg-manager-" + theme);
                content.Measure(new Size(660, 430)); content.Arrange(new Rect(0, 0, 660, 430)); content.UpdateLayout();
                Render(content, "awg-manager-min-" + theme);
            }
            finally { manager.Close(); }
            var confirmation = new ThemedMessageBox("Отключить VPN у всех пользователей компьютера?", "Общий туннель",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel, "Отключить");
            var dialogContent = (FrameworkElement)confirmation.Content;
            dialogContent.Measure(new Size(452, 300)); dialogContent.Arrange(new Rect(0, 0, 452, dialogContent.DesiredSize.Height));
            Render(dialogContent, "confirmation-" + theme);
            Require(confirmation.Result == MessageBoxResult.Cancel, "Closing confirmation must default to cancel."); confirmation.Close();
            using (var menu = new System.Windows.Forms.ContextMenuStrip())
            {
                menu.Items.Add("Открыть"); TrayMenuTheme.Apply(menu);
                var expected = ((SolidColorBrush)System.Windows.Application.Current.FindResource("PanelBrush")).Color;
                Require(menu.BackColor.R == expected.R && menu.BackColor.G == expected.G && menu.BackColor.B == expected.B,
                    "Tray menu must use the active application theme.");
            }
            if (theme == ThemeService.Dark)
            {
                var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var busyManager = new AwgManagementWindow((_, _) => pending.Task, () => (catalog, state, "Работает"))
                { ShowInTaskbar = false, ShowActivated = false, Left = SystemParameters.VirtualScreenLeft - 4000, Top = SystemParameters.VirtualScreenTop };
                busyManager.Show();
                var command = (Task)typeof(AwgManagementWindow).GetMethod("ExecuteCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(busyManager, ["start", "-"])!;
                var list = (ListBox)busyManager.FindName("ProfileList"); list.UpdateLayout();
                Require(!list.IsEnabled && ThemeService.CurrentTheme == ThemeService.Dark, "Busy AWG must remain in dark mode.");
                var border = (Border)VisualTreeHelper.GetChild(list, 0);
                Require(((SolidColorBrush)border.Background).Color == ((SolidColorBrush)busyManager.FindResource("PanelBrush")).Color,
                    "The disabled profile list must retain its themed background.");
                Render((FrameworkElement)busyManager.Content, "awg-manager-busy-dark");
                busyManager.Close();
                Require(busyManager.IsVisible, "A busy manager must finish its command before closing for update.");
                pending.SetResult(0); await command;
                Require(!busyManager.IsVisible, "A deferred close must complete automatically after the command so update coordination does not hang.");
            }
        }
    }
    private static void Render(FrameworkElement element, string name)
    {
        element.UpdateLayout(); var image = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "regression", "ui-0.1.72"); Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
