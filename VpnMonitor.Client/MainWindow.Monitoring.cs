using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;

namespace VpnMonitor.Client;

public partial class MainWindow
{
    private readonly IncidentTracker _incidents = new();
    private readonly NotificationPolicy _notificationPolicy = new();
    private IncidentArchive _incidentArchive = null!;
    private long _incidentRevision;
    private string? _monitoringScope;
    private bool _incidentsLoaded, _incidentsLoading, _servicesRunning;
    private MonitoringSettings _monitoringSettings = new();
    private ServiceMonitor _serviceMonitor = new();
    private readonly DispatcherTimer _servicesTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private CancellationTokenSource _servicesCancellation = new();
    private readonly Dictionary<string, bool> _servicePrevious = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool?> _notificationStates = new(StringComparer.Ordinal);
    private HashSet<string> _authorizedIncidentPeers = new(StringComparer.Ordinal);
    private MonitoringWindow? _monitoringWindow;

    private void InitializeMonitoring()
    {
        _incidentArchive = new(Path.Combine(_settingsService.SettingsDirectory, "incidents"));
        _servicesTimer.Tick += async (_, _) => { await RefreshServicesAsync(); DrainNotifications(); };
    }
    private void ConfigureMonitoring(string? scope)
    {
        if (_monitoringScope != scope)
        {
            SaveIncidentsOnExit(); _monitoringWindow?.Close();
            _incidents.Reset(); _monitoringScope = scope; _incidentsLoaded = false;
            _authorizedIncidentPeers.Clear();
        }
        _monitoringSettings = scope is null ? new() : MonitoringConfiguration.Get(_settings, scope);
        try { MonitoringConfiguration.Validate(_monitoringSettings); }
        catch (ArgumentException ex) { _monitoringSettings = new(); App.Log("Invalid monitoring configuration; using defaults.", ex); }
        ResetServiceMonitoring();
        _notificationPolicy.Reset(); _notificationStates.Clear();
        ServiceColumn.Visibility = _monitoringSettings.ServiceChecksEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (!_settings.SavePeerHistory)
        {
            try { _incidentArchive.ClearAll(++_incidentRevision); }
            catch (Exception ex) { App.Log("Could not remove incident archives.", ex); }
        }
    }
    private void ResetServiceMonitoring()
    {
        _servicesCancellation.Cancel(); _servicesCancellation.Dispose(); _servicesCancellation = new();
        _serviceMonitor = new(); _servicePrevious.Clear();
        _incidents.InterruptClients(DateTimeOffset.Now);
        foreach (var peer in _viewModel.Peers) peer.Services = null;
    }
    private async Task ObserveMonitoringApiAsync()
    {
        var now = DateTimeOffset.Now;
        if (_viewModel.HasError)
        {
            ObserveConnection(false, now); _incidents.InterruptClients(now);
            _notificationPolicy.CancelPending("API недоступен; события клиентов подавлены", now);
            _notificationStates.Clear(); _servicePrevious.Clear();
            return;
        }
        ObserveConnection(true, now);
        var ids = _viewModel.Peers.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        if (!_authorizedIncidentPeers.IsSubsetOf(ids))
        {
            _monitoringWindow?.Close(); _notificationPolicy.Reset(); _notificationStates.Clear(); _servicePrevious.Clear();
        }
        _authorizedIncidentPeers = ids;
        if (!_incidentsLoaded && !_incidentsLoading && _monitoringScope is { } scope)
        {
            _incidentsLoading = true; var revision = _connectionRevision;
            try
            {
                var saved = _settings.SavePeerHistory ? await Task.Run(() => _incidentArchive.Load(scope, now)) : [];
                if (scope != _monitoringScope || revision != _connectionRevision || _allowExit) return;
                _incidents.Restore(saved, ids, now); _incidentsLoaded = true;
            }
            catch (Exception ex)
            { if (scope == _monitoringScope && revision == _connectionRevision) _incidentsLoaded = true; App.Log("Incident archive unavailable; using current session.", ex); }
            finally { _incidentsLoading = false; }
        }
        _incidents.RetainPeers(ids);
        ApplyServiceResults();
        ObservePeerIncidents();
        _ = RefreshServicesAsync();
    }
    private void ObserveConnection(bool apiHealthy, DateTimeOffset now)
    {
        var vpn = _tunnelHealth.IsDegraded;
        _incidents.Observe("connection", vpn && !apiHealthy ? "VPN / сервер" : "Сервер/API",
            vpn && !apiHealthy ? "Нет общей VPN-связи: возможен сбой туннеля или сервера" : "Нет ответа API мониторинга",
            null, apiHealthy, now);
    }
    private void ObservePeerIncidents()
    {
        var now = DateTimeOffset.Now;
        if (_viewModel.HasError || !_viewModel.AwgState.IsCurrent || _referenceServerAvailable == false)
        { _incidents.InterruptClients(now); return; }
        foreach (var peer in _viewModel.Peers)
        {
            bool? healthy = _settings.ActiveChecksEnabled ? peer.Reachability.IsReachable : peer.IsOnline;
            _incidents.Observe("peer:" + peer.Id, "Клиент", peer.Name + (_settings.ActiveChecksEnabled ? " — нет ответа ping/TCP" : " — AWG Offline"), peer.Id, healthy, now);
        }
    }
    private void ApplyServiceResults()
    {
        foreach (var peer in _viewModel.Peers)
            peer.Services = !_viewModel.HasError && _viewModel.AwgState.IsCurrent ? _serviceMonitor.Get(peer, _monitoringSettings) : null;
    }
    private async Task RefreshServicesAsync()
    {
        if (_allowExit || _servicesRunning || !_monitoringSettings.ServiceChecksEnabled || _viewModel.HasError || !_viewModel.AwgState.IsCurrent) return;
        var monitor = _serviceMonitor; var revision = _connectionRevision; var token = _servicesCancellation.Token;
        _servicesRunning = true;
        try
        {
            var snapshot = _viewModel.Peers.ToArray();
            var reference = await monitor.ScanAsync(snapshot, _monitoringSettings, new Uri(_settings.ServerUrl), token);
            if (token.IsCancellationRequested || _allowExit || revision != _connectionRevision || !ReferenceEquals(monitor, _serviceMonitor)) return;
            if (_viewModel.HasError || !_viewModel.AwgState.IsCurrent)
            { monitor.Reset(); ApplyServiceResults(); _incidents.InterruptClients(DateTimeOffset.Now); return; }
            ApplyServiceResults();
            if (reference == false)
            {
                _incidents.InterruptClients(DateTimeOffset.Now); _servicePrevious.Clear();
                _notificationPolicy.CancelPending("VPN-сервер недоступен; проверки сервисов прерваны", DateTimeOffset.Now);
                return;
            }
            if (reference is null) return;
            var observedKeys = new HashSet<string>(StringComparer.Ordinal);
            var watched = (_settings.StatusNotificationPeerIds ?? []).ToHashSet(StringComparer.Ordinal);
            foreach (var peer in _viewModel.Peers)
                if (peer.Services is { } services)
                    foreach (var port in services.Ports)
                    {
                        var key = "service:" + peer.Id + ":" + port.Port;
                        observedKeys.Add(key); _notificationStates[key] = port.Available;
                        _incidents.Observe(key, "Сервис", peer.Name + " · " + port.Name + " :" + port.Port, peer.Id, port.Available, services.CheckedAt);
                        if (port.Available is { } healthy)
                        {
                            if (_servicePrevious.TryGetValue(key, out var previous) && previous != healthy && watched.Contains(peer.Id) &&
                                _monitoringSettings.ServiceNotifications && _settings.StatusNotificationsEnabled && (healthy ? _settings.NotifyWhenOnline : _settings.NotifyWhenOffline))
                                _notificationPolicy.Enqueue(new(key, peer.Name + " · " + port.Name,
                                    healthy ? "Сервис снова доступен" : "Сервис не отвечает после 3 проверок", peer.Id, peer.Groups.ToArray(), healthy), _monitoringSettings.Notifications, DateTimeOffset.Now);
                            _servicePrevious[key] = healthy;
                        }
                        else _servicePrevious.Remove(key);
                    }
            foreach (var key in _notificationStates.Keys.Where(k => k.StartsWith("service:") && !observedKeys.Contains(k)).ToArray())
            { _notificationStates.Remove(key); _servicePrevious.Remove(key); }
            DrainNotifications();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { App.Log("Service checks unavailable; monitoring continues.", ex); }
        finally { _servicesRunning = false; }
    }
    private void QueuePeerNotifications(IEnumerable<Peer> peers, bool active)
    {
        foreach (var peer in _viewModel.Peers)
            _notificationStates["peer:" + peer.Id] = active ? peer.Reachability.IsReachable : peer.AwgState.IsCurrent ? peer.IsOnline : null;
        foreach (var peer in peers)
        {
            var online = active ? peer.Reachability.IsReachable == true : peer.IsOnline;
            _notificationPolicy.Enqueue(new("peer:" + peer.Id, peer.Name,
                active ? online ? "Клиент доступен" : "Клиент не отвечает после 3 проверок" : online ? "Клиент Online (AWG)" : "Клиент Offline (AWG)",
                peer.Id, peer.Groups.ToArray(), online), _monitoringSettings.Notifications, DateTimeOffset.Now);
        }
        DrainNotifications();
    }
    private void DrainNotifications()
    {
        var rules = _monitoringSettings.Notifications;
        var allowed = _viewModel.Peers.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var ready = _notificationPolicy.Drain(rules, DateTimeOffset.Now,
            item => !_viewModel.AwgState.IsCurrent || _viewModel.HasError || item.PeerId is { } id && !allowed.Contains(id) ? null : _notificationStates.GetValueOrDefault(item.Key));
        if (_trayIcon is null || ready.Count == 0) return;
        try
        {
        if (ready.Count >= rules.AggregateThreshold)
        {
            var message = string.Join("\n", ready.Take(5).Select(i => i.Title + " — " + i.Message));
            if (ready.Count > 5) message += $"\nЕщё событий: {ready.Count - 5}";
            _trayIcon.ShowBalloonTip(6000, $"VPN Monitor — {ready.Count} событий", message[..Math.Min(900, message.Length)], System.Windows.Forms.ToolTipIcon.Info);
        }
        else foreach (var item in ready)
            _trayIcon.ShowBalloonTip(4500, "VPN Monitor — " + (item.Online ? "Online" : "Offline"),
                (item.Title + "\n" + item.Message)[..Math.Min(900, item.Title.Length + item.Message.Length + 1)], System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _notificationPolicy.Record(DateTimeOffset.Now, "Windows", "Не удалось передать уведомление");
            App.Log("Could not hand monitoring notifications to Windows.", ex);
        }
    }
    private bool SuppressTunnelNotice()
    {
        if (!NotificationPolicy.IsQuiet(_monitoringSettings.Notifications, DateTimeOffset.Now)) return false;
        _notificationPolicy.Record(DateTimeOffset.Now, "Общий туннель AWG", "Расписание тишины"); return true;
    }
    private bool ApplySavedView(SavedClientView view)
    {
        if (_monitoringScope is not { } scope || !SavedViewService.Apply(view, scope, _viewModel, _settings)) return false;
        ApplyColumnVisibility(); UpdateFilterButtons(); RestoreGroupSelection(); UpdateSortIndicators(); SaveSettings();
        ShowStatusHint("Представление: " + view.Name); return true;
    }
    private System.Windows.Controls.MenuItem SavedViewsMenu()
    {
        var style = TryFindResource("ActionMenuItemStyle") as Style;
        var root = new System.Windows.Controls.MenuItem { Header = "Представления списка", Style = style };
        if (_monitoringScope is { } scope)
            foreach (var view in SavedViewService.Visible(_settings, scope, _viewModel))
            {
                var item = new System.Windows.Controls.MenuItem { Header = view.Name, Style = style };
                item.Click += (_, e) => { e.Handled = true; if (!ApplySavedView(view)) ShowStatusHint("Представление недоступно."); };
                root.Items.Add(item);
            }
        var manage = new System.Windows.Controls.MenuItem { Header = "Сохранить или настроить…", Style = style };
        manage.Click += (_, e) => { e.Handled = true; ShowMonitoring(3); }; root.Items.Add(manage);
        return root;
    }
    private async Task SaveIncidentsAsync()
    {
        if (!_incidentsLoaded || !_settings.SavePeerHistory || _monitoringScope is not { } scope || !_incidents.Dirty) return;
        var entries = _incidents.Entries.ToArray(); var revision = ++_incidentRevision; _incidents.MarkSaved();
        try { await Task.Run(() => _incidentArchive.Save(scope, entries, revision, DateTimeOffset.Now)); }
        catch (Exception ex) { _incidents.MarkDirty(); App.Log("Could not save incident archive.", ex); }
    }
    private void SaveIncidentsOnExit()
    {
        if (!_incidentsLoaded || !_settings.SavePeerHistory || _monitoringScope is not { } scope) return;
        try { _incidentArchive.Save(scope, _incidents.Entries.ToArray(), ++_incidentRevision, DateTimeOffset.Now); }
        catch (Exception ex) { App.Log("Could not flush incident archive.", ex); }
    }
    private void ShowMonitoring(int page = 0, Window? owner = null)
    {
        if (_monitoringScope is null) { ShowStatusHint("Сначала настройте подключение."); return; }
        if (_monitoringWindow is not null)
        {
            if (owner is not null) _monitoringWindow.Owner = owner;
            _monitoringWindow.Activate();
            ((System.Windows.Controls.ListBox)_monitoringWindow.FindName("Navigation")).SelectedIndex = page;
            return;
        }
        var scope = _monitoringScope;
        bool Authorized() => scope == _monitoringScope && _viewModel.AwgState.IsCurrent && !_viewModel.HasError;
        var context = new MonitoringContext
        {
            Read = () => _monitoringSettings, Authorized = Authorized,
            Peers = () => Authorized() ? _viewModel.Peers.ToArray() : [],
            Groups = () => Authorized() ? _viewModel.Groups.Select(g => g.Name).ToArray() : [],
            Incidents = _incidents, Policy = _notificationPolicy, RadminPort = _settings.RadminPort,
            Save = settings =>
            {
                if (!Authorized()) throw new InvalidOperationException("Нет актуальных данных текущего аккаунта.");
                MonitoringConfiguration.Validate(settings);
                var old = _settings.MonitoringScopes;
                _settings.MonitoringScopes = old.Where(s => s.Scope != scope).Append(new() { Scope = scope, Settings = settings }).TakeLast(20).ToList();
                try { _settingsService.Save(_settings); }
                catch { _settings.MonitoringScopes = old; throw; }
                _monitoringSettings = settings; ResetServiceMonitoring(); _notificationPolicy.CancelPending("Правила изменены", DateTimeOffset.Now);
                ServiceColumn.Visibility = settings.ServiceChecksEnabled ? Visibility.Visible : Visibility.Collapsed;
                _ = RefreshServicesAsync();
            },
            ClearCompleted = () => { _incidents.ClearCompleted(); SaveIncidentsOnExit(); },
            Views = () => SavedViewService.Visible(_settings, scope, _viewModel).ToArray(),
            SaveView = name =>
            {
                if (!Authorized() || string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || name.Any(char.IsControl)) throw new ArgumentException("Дождитесь данных и введите название до 80 символов.");
                var view = SavedViewService.Capture(name, scope, _viewModel, _settings);
                var old = _settings.SavedViews;
                _settings.SavedViews = old.Where(v => v.Scope != scope || !string.Equals(v.Name, view.Name, StringComparison.OrdinalIgnoreCase)).Append(view).ToList();
                try { MonitoringConfiguration.ValidateLists(_settings); _settingsService.Save(_settings); }
                catch { _settings.SavedViews = old; throw; }
            },
            ApplyView = ApplySavedView,
            DeleteView = view =>
            {
                if (!Authorized() || view.Scope != scope) throw new InvalidOperationException("Представление недоступно.");
                var old = _settings.SavedViews; _settings.SavedViews = old.Where(v => v.Id != view.Id).ToList();
                try { _settingsService.Save(_settings); } catch { _settings.SavedViews = old; throw; }
            }
        };
        _monitoringWindow = OwnedWindowBehavior.Attach(new MonitoringWindow(context), owner ?? this);
        _monitoringWindow.Closed += (_, _) => _monitoringWindow = null;
        _monitoringWindow.Show();
        ((System.Windows.Controls.ListBox)_monitoringWindow.FindName("Navigation")).SelectedIndex = page;
    }
}
