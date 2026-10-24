using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WpfButton = System.Windows.Controls.Button;
using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;
using WpfBrushes = System.Windows.Media.Brushes;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;
using VpnMonitor.Client.ViewModels;

namespace VpnMonitor.Client;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService;
    private ClientSettings _settings;
    private readonly MainViewModel _viewModel = new();
    private readonly PeerHistory _peerHistory = new();
    private readonly PeerHistoryArchive _historyArchive;
    private readonly DispatcherTimer _historySaveTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private string? _historyScope;
    private bool _historyLoaded, _historyLoading, _historyDirty, _historySaving, _historyPersistence;
    private long _historyRevision;
    private long _connectionRevision;
    private bool _dataRefreshRunning;
    private DateTimeOffset? _lastApiAttempt, _lastApiSuccess;
    private readonly TunnelHealthMonitor _tunnelHealth = new();
    private readonly DispatcherTimer _tunnelHealthTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly CancellationTokenSource _tunnelHealthCancellation = new();
    private bool _tunnelHealthRunning;
    private string? _tunnelHealthTarget;
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _reachabilityTimer = new();
    private ReachabilityMonitor _reachabilityMonitor = new();
    private CancellationTokenSource _reachabilityCancellation = new();
    private bool _reachabilityRunning;
    private long _lastReachabilityStart;
    private bool? _referenceServerAvailable;
    private readonly Dictionary<string, PingWindow> _pingWindows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TraceRouteWindow> _traceWindows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openingShareServers = new(StringComparer.OrdinalIgnoreCase);
    private readonly NetworkShareService _networkShareService = new();
    private readonly CancellationTokenSource _shareLifetimeCancellation = new();
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayOwnedIcon;
    private TrayNetworkPopup? _trayNetworkPopup;
    private readonly AutostartService _autostartService = new();
    private readonly RadminService _radminService = new();
    private readonly RadminLoginAutofillService _radminLoginAutofillService = new();
    private DispatcherTimer? _statusHintTimer;
    private bool _suppressGroupSelection;
    private bool _allowExit;
    private bool _networkChangeSubscribed;
    private readonly PeerStatusTracker _statusTracker = new();

    public MainWindow() : this(new SettingsService(), new PeerHistoryArchive()) { }

    public MainWindow(SettingsService settingsService, PeerHistoryArchive historyArchive)
    {
        _settingsService = settingsService;
        _historyArchive = historyArchive;
        InitializeComponent();

        _settings = _settingsService.Load();
        ThemeService.Apply(_settings.Theme);
        DataContext = _viewModel;

        _tunnelHealthTimer.Tick += async (_, _) => await RefreshTunnelHealthAsync();
        _timer.Tick += async (_, _) => await RefreshDataAsync();
        _reachabilityTimer.Tick += async (_, _) => await RefreshReachabilityAsync();
        _peerHistory.Entries.CollectionChanged += (_, _) => _historyDirty = true;
        _historySaveTimer.Tick += async (_, _) => await SaveHistoryAsync();
        _historySaveTimer.Start();

    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        ThemeService.ApplyWindowChrome(this);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        App.Log("MainWindow Loaded event started.");

        try { _autostartService.SetEnabled(_settings.StartWithWindows); }
        catch (Exception ex) { App.Log("Failed to synchronize Windows autostart setting.", ex); }

        InitializeTray();
        _tunnelHealthTimer.Start();
        _ = RefreshTunnelHealthAsync();
        RestoreWindowState();

        if (!_settings.StartMinimized)
            EnsureWindowVisible("Loaded");

        if (!EnsureConfigured(showSettingsIfMissing: true))
            return;

        _viewModel.Filter = _settings.Filter;
        _viewModel.SelectedGroup = _settings.SelectedGroup;
        _viewModel.FavoritesOnly = _settings.FavoritesOnly;
        _viewModel.SetFavoriteIds(_settings.FavoritePeerIds);
        _viewModel.ApplySort(_settings.SortMember, _settings.SortDescending);
        UpdateFilterButtons();
        UpdateSortIndicators();
        ApplyColumnVisibility();
        ConfigureTimer();

        try
        {
            await RefreshDataAsync();
        }
        catch (Exception ex)
        {
            App.Log("Initial refresh failed. Main window remains available.", ex);
            ShowStatusHint("Ошибка первого обновления — окно оставлено открытым");
        }

        if (_settings.StartMinimized && _trayIcon is not null)
        {
            App.Log("StartMinimized is enabled; hiding MainWindow to tray.");
            Hide();
        }
        else
        {
            EnsureWindowVisible("After initial refresh");
            App.Log($"MainWindow visible. Left={Left:0}, Top={Top:0}, Width={ActualWidth:0}, Height={ActualHeight:0}, State={WindowState}.");
        }
    }


    private void InitializeTray()
    {
        if (_trayIcon is not null)
            return;

        try
        {
            _trayOwnedIcon = LoadTrayIcon();

            var trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "VPN Monitor",
                Icon = _trayOwnedIcon,
                Visible = true
            };

            trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
            trayIcon.MouseClick += TrayIcon_MouseClick;

            var trayMenu = new System.Windows.Forms.ContextMenuStrip();
            trayMenu.Items.Add("Открыть", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
            trayMenu.Items.Add("Обновить", null, (_, _) => Dispatcher.InvokeAsync(RefreshDataAsync));
            trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            trayMenu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(CloseFromTray));
            trayIcon.ContextMenuStrip = trayMenu;

            _trayIcon = trayIcon;
            UpdateTrayTooltip();

            if (!_networkChangeSubscribed)
            {
                NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
                _networkChangeSubscribed = true;
            }
        }
        catch (Exception ex)
        {
            App.Log("Tray initialization failed. Continuing without tray icon.", ex);
            _trayIcon = null;
            _trayOwnedIcon?.Dispose();
            _trayOwnedIcon = null;
        }
    }

    private void NetworkAddressChanged(object? sender, EventArgs e)
    {
        // Событие может приходить не из UI-потока.
        _ = Dispatcher.BeginInvoke(new Action(RefreshTrayNetworkUi));
    }

    private void TrayIcon_MouseClick(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button != System.Windows.Forms.MouseButtons.Left)
            return;

        Dispatcher.Invoke(ToggleTrayNetworkPopup);
    }

    private void ToggleTrayNetworkPopup()
    {
        if (_trayNetworkPopup is { IsVisible: true })
        {
            _trayNetworkPopup.Close();
            return;
        }

        var popup = new TrayNetworkPopup(GetIpv4NetworkItems());
        popup.OpenRequested += (_, _) => ShowFromTray();
        popup.RefreshRequested += async (_, _) => await RefreshDataAsync();
        popup.SettingsRequested += (_, _) =>
        {
            ShowFromTray();
            Settings_Click(this, new RoutedEventArgs());
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trayNetworkPopup, popup))
                _trayNetworkPopup = null;
        };

        _trayNetworkPopup = popup;
        ThemeService.ApplyWindowChrome(popup);
        popup.Show();
        popup.Activate();
    }

    private void RefreshTrayNetworkUi()
    {
        UpdateTrayTooltip();
        if (_trayNetworkPopup is { IsVisible: true })
            _trayNetworkPopup.UpdateItems(GetIpv4NetworkItems());
    }

    private void UpdateTrayTooltip()
    {
        if (_trayIcon is null)
            return;

        try
        {
            var count = GetIpv4NetworkItems().Count;
            _trayIcon.Text = count == 0
                ? "VPN Monitor • IPv4 нет"
                : $"VPN Monitor • {count} IPv4";
        }
        catch (Exception ex)
        {
            App.Log("Failed to update tray IPv4 tooltip.", ex);
            try { _trayIcon.Text = "VPN Monitor"; } catch { }
        }
    }

    private static List<NetworkAddressItem> GetIpv4NetworkItems()
    {
        var items = new List<NetworkAddressItem>();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties properties;
            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                    continue;

                items.Add(new NetworkAddressItem
                {
                    InterfaceName = string.IsNullOrWhiteSpace(networkInterface.Name)
                        ? "Интерфейс"
                        : networkInterface.Name.Trim(),
                    Address = address.ToString(),
                    IsUp = networkInterface.OperationalStatus == OperationalStatus.Up
                });
            }
        }

        return items
            .GroupBy(x => $"{x.InterfaceName}\u0000{x.Address}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(x => x.IsUp)
            .ThenBy(x => x.InterfaceName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => ParseIpv4ForSort(x.Address))
            .ToList();
    }

    private static uint ParseIpv4ForSort(string address)
    {
        if (!IPAddress.TryParse(address, out var ip))
            return uint.MaxValue;

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4)
            return uint.MaxValue;

        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var uri = new Uri("pack://application:,,,/Assets/AWG_Monitor.ico", UriKind.Absolute);
        var resource = System.Windows.Application.GetResourceStream(uri);

        if (resource?.Stream is null)
            return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();

        using var stream = resource.Stream;
        using var icon = new System.Drawing.Icon(stream);
        return (System.Drawing.Icon)icon.Clone();
    }

    private bool EnsureConfigured(bool showSettingsIfMissing)
    {
        var password = _settingsService.UnprotectPassword(_settings.ProtectedPassword);

        if (string.IsNullOrWhiteSpace(_settings.ServerUrl) ||
            string.IsNullOrWhiteSpace(_settings.Username) ||
            string.IsNullOrEmpty(password))
        {
            if (!showSettingsIfMissing)
                return false;

            if (!OpenSettings())
            {
                WpfMessageBox.Show(this, "Без настроек подключения приложение не сможет получить список клиентов.",
                    "VPN Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            password = _settingsService.UnprotectPassword(_settings.ProtectedPassword);
        }

        ConfigureHistory();
        _connectionRevision++;
        _lastApiAttempt = _lastApiSuccess = null;
        _viewModel.Configure(_settings.ServerUrl, _settings.Username, password);
        _reachabilityCancellation.Cancel();
        _reachabilityCancellation.Dispose();
        _reachabilityCancellation = new CancellationTokenSource();
        _reachabilityMonitor = new ReachabilityMonitor();
        _lastReachabilityStart = 0;
        _referenceServerAvailable = null;
        ResetStatusNotificationSnapshot();
        return true;
    }

    private void ConfigureHistory()
    {
        string? scope = null;
        try { scope = PeerHistoryArchive.Scope(_settings.ServerUrl, _settings.Username); }
        catch (ArgumentException) { /* API configuration will report an invalid address. */ }
        if (_historyScope != scope)
        {
            SaveHistoryOnExit();
            _diagnosticsWindow?.Close();
            foreach (var window in OwnedWindows.OfType<PeerHistoryWindow>().ToArray()) window.Close();
            _peerHistory.Reset();
            _historyScope = scope;
            _historyLoaded = false;
        }
        _historyPersistence = _settings.SavePeerHistory;
        if (!_historyPersistence)
        {
            try { _historyArchive.ClearAll(++_historyRevision); }
            catch (Exception ex) { App.Log("Could not remove saved history archives.", ex); }
        }
        _historyDirty = true;
    }

    private async Task SaveHistoryAsync()
    {
        if (_historySaving || !_historyPersistence || !_historyLoaded || !_historyDirty || _historyScope is null) return;
        _historySaving = true;
        _historyDirty = false;
        var scope = _historyScope;
        var snapshot = _peerHistory.Entries.ToArray();
        var revision = ++_historyRevision;
        try { await Task.Run(() => _historyArchive.Save(scope, snapshot, revision)); }
        catch (Exception ex) { _historyDirty = true; App.Log("Could not save peer history archive.", ex); }
        finally { _historySaving = false; }
    }

    private void SaveHistoryOnExit()
    {
        if (!_historyPersistence || !_historyLoaded || _historyScope is null) return;
        try { _historyArchive.Save(_historyScope, _peerHistory.Entries.ToArray(), ++_historyRevision); }
        catch (Exception ex) { App.Log("Could not flush peer history archive.", ex); }
    }

    private async Task RefreshTunnelHealthAsync()
    {
        if (_tunnelHealthRunning || _tunnelHealthCancellation.IsCancellationRequested) return;
        _tunnelHealthRunning = true;
        try
        {
            var target = _settings.ServerUrl;
            if (_tunnelHealthTarget != target) { _tunnelHealth.Reset(); _tunnelHealthTarget = target; }
            var running = AwgServiceStatus.Read().State == 4;
            bool? reachable = null;
            if (running && Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                reachable = await TunnelHealthMonitor.ProbeAsync(uri, _tunnelHealthCancellation.Token);
            if (_tunnelHealthCancellation.IsCancellationRequested || target != _settings.ServerUrl) return;
            // Ignore results from a connection that was stopped while the probe was in flight.
            running = running && AwgServiceStatus.Read().State == 4;
            if (reachable == false) _viewModel.MarkAwgDataStale();
            if (_tunnelHealth.Observe(running, reachable))
            {
                App.Log(_tunnelHealth.Text);
                _trayIcon?.ShowBalloonTip(7000, "Общий туннель AWG", _tunnelHealth.Text,
                    _tunnelHealth.IsDegraded ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _tunnelHealth.Observe(true, null); App.Log("Tunnel health check unavailable.", ex); }
        finally { _tunnelHealthRunning = false; }
    }

    private void ConfigureTimer()
    {
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.RefreshSeconds, 2, 300));
        _timer.Start();
        _reachabilityTimer.Stop();
        _reachabilityTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.ActiveCheckSeconds, 2, 300));
        if (_settings.ActiveChecksEnabled) _reachabilityTimer.Start();
        ReachabilityColumn.Visibility = _settings.ActiveChecksEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshDataAsync()
    {
        if (_dataRefreshRunning || _allowExit) return;
        _dataRefreshRunning = true;
        var connectionRevision = _connectionRevision;
        var selectedPeerId = (PeersGrid.SelectedItem as Peer)?.Id;

        RefreshButton.IsEnabled = false;
        try
        {
            _lastApiAttempt = DateTimeOffset.Now;
            await _viewModel.RefreshAsync();
            if (connectionRevision != _connectionRevision || _allowExit) return;
            if (!_viewModel.HasError) _lastApiSuccess = DateTimeOffset.Now;
            RefreshTrayNetworkUi();
            UpdateAccessVisuals();
            RestoreGroupSelection();
            ApplyReachabilityResults();
            if (_referenceServerAvailable == false) _viewModel.MarkAwgDataStale();

            if (!_viewModel.HasError)
            {
                if (!_historyLoaded)
                {
                    if (_historyLoading) return;
                    _historyLoading = true;
                    var scope = _historyScope;
                    var persistence = _historyPersistence;
                    try
                    {
                        var saved = persistence && scope is not null
                            ? await Task.Run(() => _historyArchive.Load(scope)) : [];
                        if (connectionRevision != _connectionRevision || scope != _historyScope || persistence != _historyPersistence || _allowExit) return;
                        _peerHistory.RestoreAuthorized(saved, _viewModel.Peers.Select(p => p.Id));
                    }
                    catch (Exception ex) { App.Log("Peer history archive unavailable; starting a fresh history.", ex); }
                    finally { _historyLoading = false; }
                    if (connectionRevision != _connectionRevision || scope != _historyScope || persistence != _historyPersistence || _allowExit) return;
                    _historyLoaded = true;
                }
                _peerHistory.RetainPeers(_viewModel.Peers.Select(p => p.Id));
                foreach (var peer in _viewModel.Peers)
                {
                    _peerHistory.Observe(peer.Id, "AWG", peer.AwgState.IsCurrent ? peer.StatusText : "Неизвестно");
                    if (peer.AwgState.IsCurrent) _peerHistory.Observe(peer.Id, "Endpoint", peer.Endpoint ?? "—");
                }
                ProcessStatusNotifications();
                _ = RefreshReachabilityAsync();
            }

            if (_viewModel.HasError)
                foreach (var peer in _viewModel.Peers) _peerHistory.Observe(peer.Id, "AWG", "Неизвестно");
            // Peers are replaced on refresh, so restore the selected logical peer by PublicKey.
            if (!string.IsNullOrWhiteSpace(selectedPeerId))
            {
                var restored = _viewModel.PeersView
                    .Cast<Peer>()
                    .FirstOrDefault(p => string.Equals(p.Id, selectedPeerId, StringComparison.Ordinal));
                PeersGrid.SelectedItem = restored;
            }
        }
        finally
        {
            _dataRefreshRunning = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDataAsync();
        await RefreshReachabilityAsync(force: true);
    }

    private void ApplyReachabilityResults()
    {
        var watched = new HashSet<string>(_settings.StatusNotificationPeerIds ?? [], StringComparer.Ordinal);
        foreach (var peer in _viewModel.Peers)
            peer.Reachability = !_settings.ActiveChecksEnabled
                ? ReachabilityResult.Unknown("Проверки выключены")
                : _settings.ActiveChecksWatchedOnly && !watched.Contains(peer.Id)
                    ? ReachabilityResult.Unknown("Не выбран для проверки")
                    : _reachabilityMonitor.GetResult(peer.Id, peer.VpnIp);
    }

    private async Task RefreshReachabilityAsync(bool force = false)
    {
        if (_allowExit || !_settings.ActiveChecksEnabled || _reachabilityRunning) return;
        var interval = Math.Clamp(_settings.ActiveCheckSeconds, 2, 300) * 1000L;
        if (!force && _lastReachabilityStart != 0 && Environment.TickCount64 - _lastReachabilityStart < interval) return;
        var watched = new HashSet<string>(_settings.StatusNotificationPeerIds ?? [], StringComparer.Ordinal);
        var targets = _viewModel.Peers
            .Where(p => !string.IsNullOrWhiteSpace(p.Id) && (!_settings.ActiveChecksWatchedOnly || watched.Contains(p.Id)))
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .Select(g => new ProbeTarget(g.Key, g.Last().VpnIp ?? "")).ToArray();
        if (targets.Length == 0) return;
        var token = _reachabilityCancellation.Token;
        var monitor = _reachabilityMonitor;
        _reachabilityRunning = true;
        _lastReachabilityStart = Environment.TickCount64;
        try
        {
            var scan = await monitor.ScanAsync(targets, new Uri(_settings.ServerUrl),
                _settings.ActiveCheckTcpFallback, Math.Clamp(_settings.ActiveCheckTcpPort, 1, 65535), token);
            if (scan is null || token.IsCancellationRequested || _allowExit) return;
            var referenceRecovered = _referenceServerAvailable == false && scan.ServerAvailable;
            _referenceServerAvailable = scan.ServerAvailable;
            ApplyReachabilityResults();
            foreach (var peer in _viewModel.Peers)
            {
                _peerHistory.Observe(peer.Id, "Доступность", peer.Reachability.IsReachable switch
                {
                    true => "Доступен", false => "Недоступен", _ => "Неизвестно"
                });
                if (!scan.ServerAvailable) _peerHistory.Observe(peer.Id, "AWG", "Неизвестно");
            }
            if (!scan.ServerAvailable)
            {
                _viewModel.MarkAwgDataStale();
                ResetStatusNotificationSnapshot();
            }
            else
                ProcessStatusNotifications(active: true);
            if (referenceRecovered) _ = RefreshDataAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || _allowExit) return;
            App.Log("Active reachability check failed. Monitoring continues.", ex);
            foreach (var peer in _viewModel.Peers)
            {
                peer.Reachability = ReachabilityResult.Unknown("Ошибка проверки");
                _peerHistory.Observe(peer.Id, "Доступность", "Неизвестно");
            }
            ResetStatusNotificationSnapshot();
        }
        finally { _reachabilityRunning = false; }
    }

    internal void ShutdownForUpdate()
    {
        _allowExit = true;
        System.Windows.Application.Current.Shutdown();
    }

    private AboutWindow? _aboutWindow;
    private void About_Click(object sender, RoutedEventArgs e)
    {
        if (_aboutWindow is not null) { _aboutWindow.Activate(); return; }
        _aboutWindow = new AboutWindow(ShowDiagnostics) { Owner = this };
        _aboutWindow.Closed += (_,_) => _aboutWindow = null;
        _aboutWindow.Show();
    }

    private AwgStatusWindow? _awgStatusWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private void ShowDiagnostics()
    {
        if (_diagnosticsWindow is not null) { _diagnosticsWindow.Activate(); return; }
        _diagnosticsWindow = new DiagnosticsWindow(CaptureDiagnosticsAsync, () => Log_Click(this, new RoutedEventArgs())) { Owner = this };
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show();
    }

    private Task<DiagnosticCapture> CaptureDiagnosticsAsync()
    {
        var state = new DiagnosticState(_settings.ServerUrl, _viewModel.AwgState.IsCurrent, _dataRefreshRunning, _viewModel.ErrorMessage,
            _lastApiAttempt, _lastApiSuccess,
            _lastApiSuccess is null || _viewModel.HasError ? "Не подтверждён" : _viewModel.IsAdmin ? "Администратор API" : "Ограниченный пользователь API",
            _viewModel.TotalCount, _settings.ActiveChecksEnabled, Math.Clamp(_settings.ActiveCheckSeconds, 2, 300), _settings.SavePeerHistory, _tunnelHealth.Details);
        var redactor = new DiagnosticRedactor([_settingsService.UnprotectPassword(_settings.ProtectedPassword), _settings.ProtectedPassword]);
        return Task.Run(() => new DiagnosticCapture(DiagnosticReport.Create(state), redactor));
    }
    private void AwgStatus_Click(object sender, RoutedEventArgs e)
    {
        if (_awgStatusWindow is not null) { _awgStatusWindow.Activate(); return; }
        _awgStatusWindow = new AwgStatusWindow(() => _tunnelHealth.Details) { Owner = this };
        _awgStatusWindow.Closed += (_, _) => _awgStatusWindow = null;
        _awgStatusWindow.Show();
    }

    private LogWindow? _logWindow;
    private void Log_Click(object sender, RoutedEventArgs e)
    {
        if (_logWindow is not null)
        {
            if (_logWindow.WindowState == WindowState.Minimized) _logWindow.WindowState = WindowState.Normal;
            _logWindow.Activate();
            return;
        }
        _logWindow = new LogWindow(ShowDiagnostics) { Owner = this };
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
    }

    private async void ExportPeers_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Список клиентов CSV (*.csv)|*.csv",
            FileName = $"VpnMonitor-clients-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            // Snapshot the current authorized view on the dispatcher before writing asynchronously.
            var peers = _viewModel.PeersView.Cast<Peer>().ToArray();
            var csv = PeerCsvExport.Create(peers);
            await System.IO.File.WriteAllTextAsync(dialog.FileName, csv, new System.Text.UTF8Encoding(true));
            ShowStatusHint($"Экспортировано клиентов: {peers.Length}");
        }
        catch (Exception ex) { ShowActionError("Не удалось экспортировать список клиентов.", ex); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!OpenSettings())
            return;

        ThemeService.Apply(_settings.Theme);
        ApplyColumnVisibility();
        UpdateFilterButtons();
        UpdateGroupVisuals();

        if (!EnsureConfigured(showSettingsIfMissing: false))
            return;

        ConfigureTimer();
        _ = RefreshDataAsync();
    }

    private bool OpenSettings()
    {
        var dialog = new SettingsWindow(_settingsService, _settings)
        {
            Owner = this
        };

        var result = dialog.ShowDialog() == true;
        if (result)
        {
            _settings = _settingsService.Load();
            ThemeService.Apply(_settings.Theme);
            _viewModel.SetFavoriteIds(_settings.FavoritePeerIds);
            _viewModel.FavoritesOnly = _settings.FavoritesOnly;
            ApplyColumnVisibility();
        }

        return result;
    }

    private void AllClients_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsRestrictedUser)
            return;

        _suppressGroupSelection = true;
        GroupsList.SelectedItem = null;
        _suppressGroupSelection = false;

        _viewModel.SelectedGroup = null;
        _viewModel.FavoritesOnly = false;
        _settings.SelectedGroup = null;
        _settings.FavoritesOnly = false;
        SaveSettings();
        UpdateGroupVisuals();
    }

    private void FavoriteClients_Click(object sender, RoutedEventArgs e)
    {
        _suppressGroupSelection = true;
        GroupsList.SelectedItem = null;
        _suppressGroupSelection = false;

        _viewModel.SelectedGroup = _viewModel.IsRestrictedUser
            ? _viewModel.RestrictedGroup
            : null;
        _viewModel.FavoritesOnly = true;
        _settings.SelectedGroup = _viewModel.IsRestrictedUser
            ? _viewModel.RestrictedGroup
            : null;
        _settings.FavoritesOnly = true;
        SaveSettings();
        UpdateGroupVisuals();
    }

    private void GroupsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGroupSelection)
            return;

        if (GroupsList.SelectedItem is GroupListItem group)
        {
            if (_viewModel.IsRestrictedUser &&
                !string.Equals(group.Name, _viewModel.RestrictedGroup, StringComparison.CurrentCulture))
            {
                RestoreGroupSelection();
                return;
            }

            _viewModel.SelectedGroup = group.Name;
            _viewModel.FavoritesOnly = false;
            _settings.SelectedGroup = group.Name;
            _settings.FavoritesOnly = false;
            SaveSettings();
            UpdateGroupVisuals();
        }
    }

    private void RestoreGroupSelection()
    {
        _suppressGroupSelection = true;

        if (_viewModel.IsRestrictedUser &&
            !_viewModel.FavoritesOnly &&
            !string.IsNullOrWhiteSpace(_viewModel.RestrictedGroup))
        {
            _viewModel.SelectedGroup = _viewModel.RestrictedGroup;
        }

        if (_viewModel.FavoritesOnly || string.IsNullOrWhiteSpace(_viewModel.SelectedGroup))
        {
            GroupsList.SelectedItem = null;
        }
        else
        {
            GroupsList.SelectedItem = _viewModel.Groups.FirstOrDefault(
                g => string.Equals(g.Name, _viewModel.SelectedGroup, StringComparison.CurrentCulture));
        }

        _suppressGroupSelection = false;
        UpdateGroupVisuals();
    }

    private void UpdateAccessVisuals()
    {
        var restricted = _viewModel.IsRestrictedUser;

        AllClientsButton.Visibility = restricted
            ? Visibility.Collapsed
            : Visibility.Visible;

        ClientsSectionTitle.Text = restricted ? "ДОСТУП" : "КЛИЕНТЫ";
        GroupsSectionTitle.Text = restricted ? "ВАША ГРУППА" : "ГРУППЫ";

        UserAccessBadge.Visibility = Visibility.Visible;
        UserAccessText.Text = restricted
            ? $"{_viewModel.CurrentUsername} · {_viewModel.RestrictedGroup ?? "группа"}"
            : $"{_viewModel.CurrentUsername} · admin";

        UserAccessBadge.ToolTip = restricted
            ? $"Ограниченный доступ: только группа «{_viewModel.RestrictedGroup ?? _viewModel.CurrentUsername}»"
            : "Администратор: полный доступ";

        // Если раньше под admin был сохранён другой раздел, при входе обычного
        // пользователя не оставляем этот выбор в настройках.
        if (restricted && !string.IsNullOrWhiteSpace(_viewModel.RestrictedGroup))
        {
            _settings.SelectedGroup = _viewModel.RestrictedGroup;
            if (!_viewModel.FavoritesOnly)
                _viewModel.SelectedGroup = _viewModel.RestrictedGroup;
        }
    }

    private void UpdateGroupVisuals()
    {
        var allActive = !_viewModel.IsRestrictedUser &&
                        !_viewModel.FavoritesOnly &&
                        string.IsNullOrWhiteSpace(_viewModel.SelectedGroup);
        var favoritesActive = _viewModel.FavoritesOnly;

        AllClientsButton.Background = allActive
            ? GetBrush("SidebarSelectedBrush")
            : WpfBrushes.Transparent;
        AllClientsButton.Foreground = allActive
            ? GetBrush("SidebarSelectedTextBrush")
            : GetBrush("TextBrush");

        FavoriteClientsButton.Background = favoritesActive
            ? GetBrush("SidebarSelectedBrush")
            : WpfBrushes.Transparent;
        FavoriteClientsButton.Foreground = favoritesActive
            ? GetBrush("SidebarSelectedTextBrush")
            : GetBrush("TextBrush");
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string filter })
            return;

        _viewModel.Filter = filter;
        _settings.Filter = filter;
        SaveSettings();
        UpdateFilterButtons();
    }

    private void UpdateFilterButtons()
    {
        foreach (var button in new[] { FilterAllButton, FilterOnlineButton, FilterOfflineButton })
        {
            var active = string.Equals(button.Tag as string, _viewModel.Filter, StringComparison.Ordinal);
            if (active)
            {
                button.SetResourceReference(WpfButton.BackgroundProperty, "ActiveButtonBrush");
                button.SetResourceReference(WpfButton.ForegroundProperty, "ActiveButtonForegroundBrush");
            }
            else
            {
                // Let FilterButtonStyle handle normal + hover colors.
                button.ClearValue(WpfButton.BackgroundProperty);
                button.ClearValue(WpfButton.ForegroundProperty);
            }
        }
    }

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: Peer peer })
            return;

        ToggleFavorite(peer);
        e.Handled = true;
    }

    private void ToggleFavorite(Peer peer)
    {
        var favorite = !_viewModel.IsFavorite(peer.Id);
        _viewModel.SetFavorite(peer, favorite);

        _settings.FavoritePeerIds ??= [];
        if (favorite)
        {
            if (!_settings.FavoritePeerIds.Contains(peer.Id, StringComparer.Ordinal))
                _settings.FavoritePeerIds.Add(peer.Id);
            ShowStatusHint($"★ {peer.Name} добавлен в избранное");
        }
        else
        {
            _settings.FavoritePeerIds.RemoveAll(id => string.Equals(id, peer.Id, StringComparison.Ordinal));
            ShowStatusHint($"☆ {peer.Name} удален из избранного");
        }

        SaveSettings();
        UpdateGroupVisuals();
        PeersGrid.Items.Refresh();
    }

    private bool IsStatusNotificationEnabled(Peer peer) =>
        !string.IsNullOrWhiteSpace(peer.Id) &&
        (_settings.StatusNotificationPeerIds ?? []).Contains(peer.Id, StringComparer.Ordinal);

    private void ToggleStatusNotification(Peer peer)
    {
        if (string.IsNullOrWhiteSpace(peer.Id))
            return;

        _settings.StatusNotificationPeerIds ??= [];
        var enabled = !_settings.StatusNotificationPeerIds.Contains(peer.Id, StringComparer.Ordinal);

        if (enabled)
        {
            _settings.StatusNotificationPeerIds.Add(peer.Id);
            ShowStatusHint($"🔔 {peer.Name}: уведомления включены");
        }
        else
        {
            _settings.StatusNotificationPeerIds.RemoveAll(id => string.Equals(id, peer.Id, StringComparison.Ordinal));
            ShowStatusHint($"🔕 {peer.Name}: уведомления отключены");
        }

        SaveSettings();
    }

    private void ResetStatusNotificationSnapshot() => _statusTracker.Reset();

    private void ProcessStatusNotifications(bool active = false)
    {
        if ((_settings.StatusNotificationSource == "active") != active) return;
        try
        {
            var changed = _statusTracker.Update(_viewModel.Peers, _settings,
                active ? peer => peer.Reachability.IsReachable : peer => peer.AwgState.IsCurrent ? peer.IsOnline : null);
            foreach (var peer in changed)
                ShowPeerStatusNotification(peer);
        }
        catch (Exception ex)
        {
            App.Log("Status notification processing failed. UI continues normally.", ex);
            ResetStatusNotificationSnapshot();
        }
    }

    private void ShowPeerStatusNotification(Peer peer)
    {
        if (_trayIcon is null)
            return;

        var active = _settings.StatusNotificationSource == "active";
        var online = active ? peer.Reachability.IsReachable == true : peer.IsOnline;
        var title = online ? "VPN Monitor — Online" : "VPN Monitor — Offline";
        var ip = string.IsNullOrWhiteSpace(peer.VpnIp) ? "" : $" ({peer.VpnIp})";
        var status = active
            ? (online ? "Клиент доступен (активная проверка)" : "Клиент не отвечает после 3 проверок")
            : (online ? "Клиент появился в сети (AWG)" : "Клиент ушёл Offline (AWG)");
        var message = $"{peer.Name}{ip}\n{status}";

        try
        {
            _trayIcon.ShowBalloonTip(
                4500,
                title,
                message,
                online
                    ? System.Windows.Forms.ToolTipIcon.Info
                    : System.Windows.Forms.ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            App.Log("Failed to show peer status notification.", ex);
        }
    }

    private void PeersGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Handle the click at DataGrid preview stage, before DataGridRow can select itself.
        var button = FindAncestor<WpfButton>(e.OriginalSource as DependencyObject);
        if (button?.Tag is not string ip || string.IsNullOrWhiteSpace(ip))
            return;

        e.Handled = true;
        WpfClipboard.SetText(ip);
        AnimateCopiedIp(button, ip);
    }

    private void AnimateCopiedIp(WpfButton button, string ip)
    {
        var oldContent = button.Content;

        button.Content = $"{ip} ✓";
        button.SetResourceReference(WpfButton.ForegroundProperty, "CopySuccessBrush");
        button.SetResourceReference(WpfButton.BackgroundProperty, "CopySuccessBackgroundBrush");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            button.Content = oldContent;
            // Restore style/theme-driven values so hover and future theme changes keep working.
            button.ClearValue(WpfButton.ForegroundProperty);
            button.ClearValue(WpfButton.BackgroundProperty);
        };
        timer.Start();
    }

    private void PeersGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not Peer peer)
            return;

        PeersGrid.SelectedItem = peer;
        row.Focus();

        var menu = BuildPeerContextMenu(peer);
        menu.PlacementTarget = row;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildPeerContextMenu(Peer peer)
    {
        var menu = new ContextMenu();

        AddActionMenuItem(menu,
            _viewModel.IsFavorite(peer.Id) ? "Убрать из избранного" : "Добавить в избранное",
            () => ToggleFavorite(peer));
        AddActionMenuItem(menu,
            IsStatusNotificationEnabled(peer) ? "Отключить уведомления Online/Offline" : "Уведомлять Online/Offline",
            () => ToggleStatusNotification(peer));
        menu.Items.Add(CreateMenuSeparator());

        AddActionMenuItem(menu, "Копировать имя", () => CopyPeerValue(peer.Name, "Имя"),
            !string.IsNullOrWhiteSpace(peer.Name));
        AddActionMenuItem(menu, "Копировать VPN IP", () => CopyPeerValue(peer.VpnIp, "VPN IP"),
            !string.IsNullOrWhiteSpace(peer.VpnIp));
        AddActionMenuItem(menu, "Копировать External IP", () => CopyPeerValue(peer.EndpointHost, "External IP"),
            !string.IsNullOrWhiteSpace(peer.EndpointHost));

        menu.Items.Add(CreateMenuSeparator());

        AddActionMenuItem(menu, "Пинг", () => StartPing(peer), HasUsableVpnIp(peer));
        AddActionMenuItem(menu, "Tracert", () => StartTraceRoute(peer), HasUsableVpnIp(peer));
        AddActionMenuItem(menu, "RDP", () => StartRdp(peer), HasUsableVpnIp(peer));
        AddActionMenuItem(menu, "SSH", () => StartSsh(peer), HasUsableVpnIp(peer));
        if (_settings.CustomActions is { Count: > 0 })
            menu.Items.Add(CustomActionMenu.Create(_settings.CustomActions, definition => RunCustomAction(peer, definition), HasUsableVpnIp(peer)));
        AddActionMenuItem(menu, "История клиента", () => ShowPeerHistory(peer));
        menu.Items.Add(NetworkShareMenu.CreateSubmenu(share => OpenSharedFolder(peer, share),
            () => ChooseSharedFolder(peer), HasUsableVpnIp(peer)));

        menu.Items.Add(CreateMenuSeparator());

        AddActionMenuItem(menu, "Radmin Viewer — Управление", () => StartRadmin(peer, fileTransfer: false), HasUsableVpnIp(peer));
        AddActionMenuItem(menu, "Radmin Viewer — Передача файлов", () => StartRadmin(peer, fileTransfer: true), HasUsableVpnIp(peer));

        return menu;
    }

    private void AddActionMenuItem(ContextMenu menu, string title, Action action, bool enabled = true)
    {
        var item = new MenuItem
        {
            Header = title,
            IsEnabled = enabled,
            Style = TryFindResource("ActionMenuItemStyle") as Style
        };

        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private Separator CreateMenuSeparator() => new()
    {
        Margin = new Thickness(7, 5, 7, 5),
        Height = 1,
        Style = TryFindResource("ContextSeparatorStyle") as Style
    };

    private void CopyPeerValue(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        WpfClipboard.SetText(value);
        ShowStatusHint($"{label} скопирован: {value}");
    }

    private static bool HasUsableVpnIp(Peer peer) =>
        IPAddress.TryParse(peer.VpnIp, out _);

    private bool TryGetVpnIp(Peer peer, out string ip)
    {
        ip = peer.VpnIp?.Trim() ?? "";
        if (IPAddress.TryParse(ip, out _))
            return true;

        WpfMessageBox.Show(this,
            "У клиента нет корректного VPN IP.",
            "VPN Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void StartPing(Peer peer)
    {
        if (!TryGetVpnIp(peer, out var ip))
            return;

        try
        {
            if (_pingWindows.TryGetValue(ip, out var existing))
            {
                existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }
            var owner = System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
            var window = new PingWindow(peer.Name, ip) { Owner = owner };
            _pingWindows[ip] = window;
            window.Closed += (_, _) => _pingWindows.Remove(ip);
            window.Show();
            ShowStatusHint($"Запущен ping {ip}");
        }
        catch (Exception ex)
        {
            ShowActionError("Не удалось запустить ping.", ex);
        }
    }

    private void StartTraceRoute(Peer peer)
    {
        if (!TryGetVpnIp(peer, out var ip)) return;
        try
        {
            if (_traceWindows.TryGetValue(ip, out var existing))
            {
                existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }
            var owner = System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
            var window = new TraceRouteWindow(peer.Name, ip) { Owner = owner };
            _traceWindows[ip] = window;
            window.Closed += (_, _) => _traceWindows.Remove(ip);
            window.Show();
        }
        catch (Exception ex) { ShowActionError("Не удалось запустить трассировку.", ex); }
    }

    private void RunCustomAction(Peer peer, CustomAction definition)
    {
        try
        {
            if (!TryGetVpnIp(peer, out var address)) return;
            using var process = Process.Start(CustomActionService.CreateStartInfo(definition, address));
            ShowStatusHint($"Запущено: {definition.Name}");
        }
        catch (Exception ex) { ShowActionError("Не удалось запустить действие.", ex); }
    }

    private void ShowPeerHistory(Peer peer)
    {
        var window = new PeerHistoryWindow(peer.Name, _peerHistory, peer.Id, _settings.SavePeerHistory, SaveHistoryOnExit) { Owner = this };
        window.Show();
    }

    private void StartSsh(Peer peer)
    {
        if (!TryGetVpnIp(peer, out var ip)) return;
        try
        {
            var executable = SshService.ResolveExecutable();
            if (executable is null)
            {
                WpfMessageBox.Show(this, "В Windows не найден ssh.exe. Добавьте компонент «Клиент OpenSSH» в дополнительных компонентах Windows.",
                    "VPN Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var owner = System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
            var dialog = new SshConnectionWindow(peer.Name, ip, executable, _settings.SshUsername, _settings.SshPort) { Owner = owner };
            if (dialog.ShowDialog() != true) return;
            using var process = Process.Start(SshService.CreateStartInfo(executable, ip, dialog.Username, dialog.Port));
            _settings.SshUsername = dialog.Username;
            _settings.SshPort = dialog.Port;
            SaveSettings();
            ShowStatusHint($"SSH: {ip}:{dialog.Port}");
        }
        catch (Exception ex) { ShowActionError("Не удалось открыть SSH.", ex); }
    }

    private void StartRdp(Peer peer)
    {
        if (!TryGetVpnIp(peer, out var ip))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "mstsc.exe",
                Arguments = $"/v:{ip}",
                UseShellExecute = true
            });
            ShowStatusHint($"Открывается RDP: {ip}");
        }
        catch (Exception ex)
        {
            ShowActionError("Не удалось запустить RDP.", ex);
        }
    }

    private void OpenSharedFolders(Peer peer) => OpenSharedFolder(peer, null);

    private async void OpenSharedFolder(Peer peer, string? share)
    {
        if (_allowExit || !TryGetVpnIp(peer, out var ip)) return;
        string? path = null;
        string? server = null;
        var pending = false;
        Window? owner = null;
        EventHandler? ownerClosed = null;
        CancellationTokenSource? operation = null;
        try
        {
            path = NetworkSharePath.Create(ip, share);
            server = NetworkSharePath.Create(ip);
            pending = _openingShareServers.Add(server);
            if (!pending)
            {
                ShowStatusHint($"Подключение к {server} уже выполняется.");
                return;
            }
            owner = System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
            var ownerHandle = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            operation = CancellationTokenSource.CreateLinkedTokenSource(_shareLifetimeCancellation.Token);
            ownerClosed = (_, _) => operation.Cancel();
            owner.Closed += ownerClosed;
            var token = operation.Token;
            var progress = new Progress<string>(message =>
            {
                if (!_allowExit && pending) ShowStatusHint(message, autoClear: false);
            });
            App.Log($"Connecting SMB before opening folder: {path}");
            if (!await _networkShareService.ConnectAsync(ip, share, ownerHandle, progress, token))
            {
                App.Log($"SMB login cancelled: {server}");
                if (!_allowExit) ShowStatusHint("Открытие сетевой папки отменено.");
                return;
            }
            token.ThrowIfCancellationRequested();
            ShowStatusHint($"Открывается: {path}", autoClear: false);
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var process = Process.Start(NetworkSharePath.ShellStartInfo(ip, share));
            });
            App.Log($"Windows Shell accepted authenticated folder request: {path}");
            if (!_allowExit) ShowStatusHint($"Запрошено открытие: {path}");
        }
        catch (OperationCanceledException)
        {
            if (!_allowExit) ShowStatusHint("Открытие сетевой папки отменено.");
        }
        catch (Exception ex)
        {
            App.Log($"Network folder opening failed: {path ?? ip}", ex);
            if (!_allowExit)
            {
                ShowStatusHint("Сетевую папку открыть не удалось.");
                var message = ex is System.ComponentModel.Win32Exception native
                    ? NetworkShareService.DescribeError(native.NativeErrorCode) : ex.Message;
                WpfMessageBox.Show(owner ?? this, $"Не удалось открыть {path ?? ip}.\n\n{message}",
                    "VPN Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            if (owner is not null && ownerClosed is not null) owner.Closed -= ownerClosed;
            operation?.Dispose();
            if (pending && server is not null) _openingShareServers.Remove(server);
            pending = false;
        }
    }

    private void ChooseSharedFolder(Peer peer)
    {
        if (!TryGetVpnIp(peer, out var ip)) return;
        var owner = System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
        var dialog = new ShareOpenWindow(peer.Name, ip) { Owner = owner };
        if (dialog.ShowDialog() == true) OpenSharedFolder(peer, dialog.ShareName);
    }

    private void StartRadmin(Peer peer, bool fileTransfer)
    {
        if (!TryGetVpnIp(peer, out var ip))
            return;

        var executable = _radminService.ResolveExecutable(_settings.RadminViewerPath);
        if (string.IsNullOrWhiteSpace(executable))
        {
            var answer = WpfMessageBox.Show(this,
                "Radmin Viewer не найден. Укажите путь к Radmin.exe в настройках.\n\nОткрыть настройки сейчас?",
                "VPN Monitor", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (answer != MessageBoxResult.Yes || !OpenSettings())
                return;

            executable = _radminService.ResolveExecutable(_settings.RadminViewerPath);
            if (string.IsNullOrWhiteSpace(executable))
                return;
        }

        // Keep the successfully detected path so portable/custom installations work next time too.
        if (!string.Equals(_settings.RadminViewerPath, executable, StringComparison.OrdinalIgnoreCase))
        {
            _settings.RadminViewerPath = executable;
            SaveSettings();
        }

        try
        {
            var port = _settings.RadminPort is >= 1 and <= 65535
                ? _settings.RadminPort
                : RadminService.DefaultPort;

            Process process;
            if (fileTransfer)
            {
                process = _radminService.StartFileTransfer(executable, ip, port);
                ShowStatusHint($"Radmin: передача файлов → {ip}:{port}");
            }
            else
            {
                process = _radminService.StartFullControl(executable, ip, port);
                ShowStatusHint($"Radmin: управление → {ip}:{port}");
            }

            if (_settings.RadminAutofillUsername && !string.IsNullOrWhiteSpace(_settings.RadminUsername))
                _ = AutofillRadminUsernameAsync(process, executable, _settings.RadminUsername);
        }
        catch (Exception ex)
        {
            ShowActionError("Не удалось запустить Radmin Viewer.", ex);
        }
    }

    private async Task AutofillRadminUsernameAsync(Process process, string executablePath, string username)
    {
        try
        {
            var filled = await _radminLoginAutofillService.TryFillUsernameAsync(process, executablePath, username);
            if (filled)
                ShowStatusHint($"Radmin: логин «{username}» подставлен");
            else
                App.Log("Radmin login autofill: окно авторизации не найдено или поле логина недоступно.");
        }
        catch (Exception ex)
        {
            // Автоподстановка — вспомогательная функция. Ошибка не должна мешать самому подключению Radmin.
            App.Log("Radmin login autofill failed.", ex);
        }
    }

    private void ShowActionError(string message, Exception ex)
    {
        App.Log(message, ex);
        WpfMessageBox.Show(this, $"{message}\n\n{ex.Message}",
            "VPN Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ShowStatusHint(string message, bool autoClear = true)
    {
        StatusHintText.Text = message;

        _statusHintTimer?.Stop();
        if (!autoClear) return;
        _statusHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _statusHintTimer.Tick += (_, _) =>
        {
            _statusHintTimer?.Stop();
            StatusHintText.Text = "★ — избранное • двойной клик — карточка • ПКМ — действия • VPN IP — копирование • крестик — в трей";
        };
        _statusHintTimer.Start();
    }

    private void PeersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<WpfButton>(e.OriginalSource as DependencyObject) is not null)
            return;

        if (PeersGrid.SelectedItem is not Peer peer)
            return;

        new PeerDetailsWindow(
            peer,
            StartPing,
            StartRdp,
            OpenSharedFolders,
            StartRadmin,
            ToggleFavorite,
            IsStatusNotificationEnabled,
            ToggleStatusNotification,
            _settings.RadminPort,
            _settingsService,
            _settings,
            StartTraceRoute,
            OpenSharedFolder,
            ChooseSharedFolder,
            StartSsh)
        {
            Owner = this
        }.ShowDialog();
    }

    private void OpenVpnMonitorWeb_Click(object sender, RoutedEventArgs e)
    {
        OpenWebAddress("http://10.20.0.1/", "VPN Monitor");
    }

    private void OpenWgDashboardPeers_Click(object sender, RoutedEventArgs e)
    {
        var configuration = string.IsNullOrWhiteSpace(_viewModel.ConfigurationName)
            ? "awg0"
            : _viewModel.ConfigurationName.Trim();

        var url = $"http://10.20.0.1:10086/#/configuration/{Uri.EscapeDataString(configuration)}/peers";
        OpenWebAddress(url, "WGDashboard");
    }

    private void OpenWebAddress(string url, string label)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            ShowStatusHint($"Открывается {label}");
        }
        catch (Exception ex)
        {
            ShowActionError($"Не удалось открыть {label}.", ex);
        }
    }

    private void PeersGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;

        var member = e.Column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(member))
            return;

        var descending = _viewModel.SortMember == member
            ? !_viewModel.SortDescending
            : member == "IsOnline";

        _viewModel.ApplySort(member, descending);
        _settings.SortMember = member;
        _settings.SortDescending = descending;
        SaveSettings();
        UpdateSortIndicators();
    }

    private void UpdateSortIndicators()
    {
        foreach (var column in PeersGrid.Columns)
        {
            column.SortDirection = column.SortMemberPath == _viewModel.SortMember
                ? (_viewModel.SortDescending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending)
                : null;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = ColumnsButton,
            Placement = PlacementMode.Bottom
        };

        AddColumnMenuItem(menu, "status", "Статус", _settings.ShowStatusColumn);
        AddColumnMenuItem(menu, "computer", "Компьютер", _settings.ShowComputerColumn);
        AddColumnMenuItem(menu, "vpn", "VPN IP", _settings.ShowVpnIpColumn);
        AddColumnMenuItem(menu, "handshake", "Handshake", _settings.ShowHandshakeColumn);
        AddColumnMenuItem(menu, "traffic", "Трафик", _settings.ShowTrafficColumn);
        AddColumnMenuItem(menu, "groups", "Группы", _settings.ShowGroupsColumn);

        menu.IsOpen = true;
    }

    private void AddColumnMenuItem(ContextMenu menu, string key, string title, bool isChecked)
    {
        var item = new MenuItem
        {
            Header = title,
            Tag = key,
            IsCheckable = true,
            IsChecked = isChecked,
            StaysOpenOnClick = true
        };

        item.Click += (_, _) =>
        {
            SetColumnSetting(key, item.IsChecked);

            if (VisibleColumnCount() == 0)
            {
                item.IsChecked = true;
                SetColumnSetting(key, true);
                return;
            }

            ApplyColumnVisibility();
            SaveSettings();
        };

        menu.Items.Add(item);
    }

    private void SetColumnSetting(string key, bool value)
    {
        switch (key)
        {
            case "status": _settings.ShowStatusColumn = value; break;
            case "computer": _settings.ShowComputerColumn = value; break;
            case "vpn": _settings.ShowVpnIpColumn = value; break;
            case "handshake": _settings.ShowHandshakeColumn = value; break;
            case "traffic": _settings.ShowTrafficColumn = value; break;
            case "groups": _settings.ShowGroupsColumn = value; break;
        }
    }

    private int VisibleColumnCount() => new[]
    {
        _settings.ShowStatusColumn,
        _settings.ShowComputerColumn,
        _settings.ShowVpnIpColumn,
        _settings.ShowHandshakeColumn,
        _settings.ShowTrafficColumn,
        _settings.ShowGroupsColumn
    }.Count(x => x);

    private void ApplyColumnVisibility()
    {
        StatusColumn.Visibility = _settings.ShowStatusColumn ? Visibility.Visible : Visibility.Collapsed;
        ComputerColumn.Visibility = _settings.ShowComputerColumn ? Visibility.Visible : Visibility.Collapsed;
        VpnIpColumn.Visibility = _settings.ShowVpnIpColumn ? Visibility.Visible : Visibility.Collapsed;
        HandshakeColumn.Visibility = _settings.ShowHandshakeColumn ? Visibility.Visible : Visibility.Collapsed;
        TrafficColumn.Visibility = _settings.ShowTrafficColumn ? Visibility.Visible : Visibility.Collapsed;
        GroupsColumn.Visibility = _settings.ShowGroupsColumn ? Visibility.Visible : Visibility.Collapsed;
    }

    private static System.Windows.Media.Brush GetBrush(string key) =>
        System.Windows.Application.Current.TryFindResource(key) as System.Windows.Media.Brush ?? WpfBrushes.Transparent;

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
            return;

        if (_trayIcon is null)
            return;

        Hide();
    }

    private void ShowFromTray()
    {
        _trayNetworkPopup?.Close();

        Show();
        WindowState = WindowState.Normal;
        EnsureWindowVisible("Tray open");

        // Надёжно поднимаем окно поверх остальных окон, затем возвращаем
        // обычный Topmost, чтобы приложение не оставалось "всегда сверху".
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();

        App.Log($"MainWindow opened from tray. Left={Left:0}, Top={Top:0}, Width={ActualWidth:0}, Height={ActualHeight:0}.");
    }

    private void CloseFromTray()
    {
        _trayNetworkPopup?.Close();
        _allowExit = true;
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // Крестик/Alt+F4 теперь прячут приложение в tray.
        // Реальный выход выполняется только через пункт «Выход» в меню tray.
        if (!_allowExit && _trayIcon is not null)
        {
            e.Cancel = true;
            SaveWindowState();
            Hide();

            return;
        }

        _allowExit = true;
        SaveWindowState();
        _historySaveTimer.Stop();
        SaveHistoryOnExit();
        _timer.Stop();
        _reachabilityTimer.Stop();
        _reachabilityCancellation.Cancel();
        _reachabilityCancellation.Dispose();
        _tunnelHealthTimer.Stop();
        _tunnelHealthCancellation.Cancel();
        _shareLifetimeCancellation.Cancel();
        _shareLifetimeCancellation.Dispose();
        _trayNetworkPopup?.Close();
        _trayNetworkPopup = null;

        if (_networkChangeSubscribed)
        {
            NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
            _networkChangeSubscribed = false;
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayOwnedIcon?.Dispose();
        _trayOwnedIcon = null;

        _viewModel.Dispose();
    }

    private void RestoreWindowState()
    {
        var workArea = SystemParameters.WorkArea;

        var width = IsUsableDimension(_settings.WindowWidth)
            ? Math.Clamp(_settings.WindowWidth!.Value, MinWidth, Math.Max(MinWidth, workArea.Width))
            : Width;

        var height = IsUsableDimension(_settings.WindowHeight)
            ? Math.Clamp(_settings.WindowHeight!.Value, MinHeight, Math.Max(MinHeight, workArea.Height))
            : Height;

        Width = width;
        Height = height;

        var restoredPosition = false;

        if (IsFinite(_settings.WindowLeft) && IsFinite(_settings.WindowTop))
        {
            var candidate = new Rect(
                _settings.WindowLeft!.Value,
                _settings.WindowTop!.Value,
                width,
                height);

            if (HasUsefulVisibleArea(candidate))
            {
                Left = candidate.Left;
                Top = candidate.Top;
                WindowStartupLocation = WindowStartupLocation.Manual;
                restoredPosition = true;
            }
            else
            {
                App.Log(
                    $"Saved MainWindow position is outside visible desktop: " +
                    $"Left={candidate.Left:0}, Top={candidate.Top:0}, Width={candidate.Width:0}, Height={candidate.Height:0}. " +
                    "Resetting to primary work area.");
            }
        }

        if (!restoredPosition)
            CenterWindowInPrimaryWorkArea(width, height);

        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void EnsureWindowVisible(string reason)
    {
        if (_settings.StartMinimized && !IsVisible)
            return;

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        var width = ActualWidth;
        if (!double.IsFinite(width) || width < MinWidth)
            width = double.IsFinite(Width) && Width >= MinWidth ? Width : MinWidth;

        var height = ActualHeight;
        if (!double.IsFinite(height) || height < MinHeight)
            height = double.IsFinite(Height) && Height >= MinHeight ? Height : MinHeight;

        var candidate = new Rect(Left, Top, width, height);

        if (HasUsefulVisibleArea(candidate))
            return;

        App.Log(
            $"MainWindow is not sufficiently visible ({reason}): " +
            $"Left={candidate.Left:0}, Top={candidate.Top:0}, Width={candidate.Width:0}, Height={candidate.Height:0}. " +
            "Moving to primary work area.");

        CenterWindowInPrimaryWorkArea(width, height);
    }

    private void CenterWindowInPrimaryWorkArea(double width, double height)
    {
        var workArea = SystemParameters.WorkArea;

        width = Math.Clamp(width, MinWidth, Math.Max(MinWidth, workArea.Width));
        height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, workArea.Height));

        Width = width;
        Height = height;
        Left = workArea.Left + Math.Max(0, (workArea.Width - width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - height) / 2);
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    private static bool HasUsefulVisibleArea(Rect rect)
    {
        if (!double.IsFinite(rect.Left) ||
            !double.IsFinite(rect.Top) ||
            !double.IsFinite(rect.Width) ||
            !double.IsFinite(rect.Height) ||
            rect.Width <= 0 ||
            rect.Height <= 0)
        {
            return false;
        }

        var virtualDesktop = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        var intersection = Rect.Intersect(rect, virtualDesktop);

        // Достаточно, чтобы на текущем наборе мониторов была видна хотя бы
        // часть окна, за которую его можно схватить мышью.
        return !intersection.IsEmpty &&
               intersection.Width >= 120 &&
               intersection.Height >= 80;
    }

    private static bool IsFinite(double? value) =>
        value.HasValue && double.IsFinite(value.Value);

    private static bool IsUsableDimension(double? value) =>
        IsFinite(value) && value!.Value > 0;

    private void SaveWindowState()
    {
        var bounds = RestoreBounds;

        if (double.IsFinite(bounds.Left) &&
            double.IsFinite(bounds.Top) &&
            double.IsFinite(bounds.Width) &&
            double.IsFinite(bounds.Height) &&
            bounds.Width >= MinWidth &&
            bounds.Height >= MinHeight)
        {
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
        }

        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _settingsService.Save(_settings);
        }
        catch
        {
            // Настройки интерфейса не должны останавливать мониторинг.
        }
    }
}
