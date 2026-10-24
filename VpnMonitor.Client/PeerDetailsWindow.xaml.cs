using System.Net;
using System.Windows;
using WpfButton = System.Windows.Controls.Button;
using WpfClipboard = System.Windows.Clipboard;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;

namespace VpnMonitor.Client;

public partial class PeerDetailsWindow : Window
{
    private readonly Peer _peer;
    private readonly Action<Peer> _pingAction;
    private readonly Action<Peer>? _traceAction;
    private readonly Action<Peer>? _sshAction;
    private readonly Action<Peer> _rdpAction;
    private readonly Action<Peer> _sharedFoldersAction;
    private readonly Action<Peer, string?>? _shareAction;
    private readonly Action<Peer>? _customShareAction;
    private readonly Action<Peer, bool> _radminAction;
    private readonly Action<Peer> _favoriteAction;
    private readonly Func<Peer, bool> _notificationQuery;
    private readonly Action<Peer> _notificationAction;
    private readonly ServiceCheckService _serviceCheckService = new();
    private readonly int _radminPort;
    private readonly SettingsService _settingsService;
    private readonly ClientSettings _settings;
    private CancellationTokenSource? _serviceCheckCts;

    public PeerDetailsWindow(
        Peer peer,
        Action<Peer> pingAction,
        Action<Peer> rdpAction,
        Action<Peer> sharedFoldersAction,
        Action<Peer, bool> radminAction,
        Action<Peer> favoriteAction,
        Func<Peer, bool> notificationQuery,
        Action<Peer> notificationAction,
        int radminPort,
        SettingsService settingsService,
        ClientSettings settings,
        Action<Peer>? traceAction = null,
        Action<Peer, string?>? shareAction = null,
        Action<Peer>? customShareAction = null,
        Action<Peer>? sshAction = null)
    {
        InitializeComponent();

        _peer = peer;
        _pingAction = pingAction;
        _traceAction = traceAction;
        _sshAction = sshAction;
        _rdpAction = rdpAction;
        _sharedFoldersAction = sharedFoldersAction;
        _shareAction = shareAction;
        _customShareAction = customShareAction;
        _radminAction = radminAction;
        _favoriteAction = favoriteAction;
        _notificationQuery = notificationQuery;
        _notificationAction = notificationAction;
        _radminPort = radminPort is >= 1 and <= 65535 ? radminPort : RadminService.DefaultPort;
        _settingsService = settingsService;
        _settings = settings;

        RestoreWindowSize();

        DataContext = peer;
        Title = peer.Name;
        RadminServiceLabel.Text = $"Radmin · {_radminPort}";

        var hasVpnIp = IPAddress.TryParse(peer.VpnIp, out _);
        PingButton.IsEnabled = hasVpnIp;
        TraceButton.IsEnabled = hasVpnIp && traceAction is not null;
        RdpButton.IsEnabled = hasVpnIp;
        SharedFoldersButton.IsEnabled = hasVpnIp;
        SshButton.IsEnabled = hasVpnIp && sshAction is not null;
        RadminControlButton.IsEnabled = hasVpnIp;
        RadminFilesButton.IsEnabled = hasVpnIp;
        RefreshServicesButton.IsEnabled = hasVpnIp;

        NameCopyButton.IsEnabled = !string.IsNullOrWhiteSpace(peer.Name);
        VpnIpCopyButton.IsEnabled = !string.IsNullOrWhiteSpace(peer.VpnIp);
        ExternalIpCopyButton.IsEnabled = !string.IsNullOrWhiteSpace(peer.EndpointHost);
        UpdateFavoriteButton();
        UpdateNotificationButton();
    }

    private void UpdateNotificationButton()
    {
        var enabled = _notificationQuery(_peer);
        NotificationButton.Content = enabled ? "🔔" : "🔕";
        NotificationButton.ToolTip = enabled
            ? "Отключить уведомления Online/Offline"
            : "Уведомлять о смене Online/Offline";
        NotificationButton.SetResourceReference(WpfButton.ForegroundProperty, enabled ? "AccentBrush" : "MutedBrush");
    }

    private void UpdateFavoriteButton()
    {
        FavoriteButton.Content = _peer.IsFavorite ? "★" : "☆";
        FavoriteButton.ToolTip = _peer.IsFavorite ? "Убрать из избранного" : "Добавить в избранное";
        FavoriteButton.SetResourceReference(WpfButton.ForegroundProperty, _peer.IsFavorite ? "AccentBrush" : "MutedBrush");
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        ThemeService.ApplyWindowChrome(this);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshServiceStatusAsync();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveWindowSize();

        _serviceCheckCts?.Cancel();
        _serviceCheckCts?.Dispose();
        _serviceCheckCts = null;
    }

    private void RestoreWindowSize()
    {
        var workArea = SystemParameters.WorkArea;

        if (_settings.PeerDetailsWindowWidth is > 0)
            Width = Math.Clamp(_settings.PeerDetailsWindowWidth.Value, MinWidth, Math.Max(MinWidth, workArea.Width));

        if (_settings.PeerDetailsWindowHeight is > 0)
            Height = Math.Clamp(_settings.PeerDetailsWindowHeight.Value, MinHeight, Math.Max(MinHeight, workArea.Height));
    }

    private void SaveWindowSize()
    {
        try
        {
            var width = WindowState == WindowState.Normal ? ActualWidth : RestoreBounds.Width;
            var height = WindowState == WindowState.Normal ? ActualHeight : RestoreBounds.Height;

            if (!double.IsFinite(width) || width <= 0)
                width = Width;
            if (!double.IsFinite(height) || height <= 0)
                height = Height;

            _settings.PeerDetailsWindowWidth = Math.Max(MinWidth, width);
            _settings.PeerDetailsWindowHeight = Math.Max(MinHeight, height);
            _settingsService.Save(_settings);

            App.Log($"Peer details size saved: {_settings.PeerDetailsWindowWidth:0}x{_settings.PeerDetailsWindowHeight:0}.");
        }
        catch (Exception ex)
        {
            App.Log("Failed to save peer details window size.", ex);
        }
    }

    private async void RefreshServices_Click(object sender, RoutedEventArgs e)
    {
        await RefreshServiceStatusAsync();
    }

    private async Task RefreshServiceStatusAsync()
    {
        if (string.IsNullOrWhiteSpace(_peer.VpnIp) || !IPAddress.TryParse(_peer.VpnIp, out _))
        {
            SetAllServiceStatusesUnavailable("нет IP");
            return;
        }

        _serviceCheckCts?.Cancel();
        _serviceCheckCts?.Dispose();
        _serviceCheckCts = new CancellationTokenSource();
        var token = _serviceCheckCts.Token;

        SetServiceChecking(PingStatusDot, PingStatusText);
        SetServiceChecking(RdpStatusDot, RdpStatusText);
        SetServiceChecking(SmbStatusDot, SmbStatusText);
        SetServiceChecking(RadminStatusDot, RadminStatusText);
        RefreshServicesButton.IsEnabled = false;

        try
        {
            var result = await _serviceCheckService.CheckAsync(_peer.VpnIp, _radminPort, token);
            if (token.IsCancellationRequested)
                return;

            SetServiceState(
                PingStatusDot,
                PingStatusText,
                result.PingReachable,
                result.PingReachable && result.PingMilliseconds.HasValue
                    ? $"{result.PingMilliseconds.Value} мс"
                    : "нет ответа");

            SetServiceState(RdpStatusDot, RdpStatusText, result.RdpOpen,
                result.RdpOpen ? "доступен" : "нет соединения");
            SetServiceState(SmbStatusDot, SmbStatusText, result.SmbOpen,
                result.SmbOpen ? "доступен" : "нет соединения");
            SetServiceState(RadminStatusDot, RadminStatusText, result.RadminOpen,
                result.RadminOpen ? "доступен" : "нет соединения");
        }
        catch (OperationCanceledException)
        {
            // Окно закрывается или запущена новая проверка.
        }
        catch (Exception ex)
        {
            App.Log($"Service check failed for {_peer.VpnIp}.", ex);
            SetAllServiceStatusesUnavailable("ошибка");
        }
        finally
        {
            if (!token.IsCancellationRequested)
                RefreshServicesButton.IsEnabled = true;
        }
    }

    private static void SetServiceChecking(WpfTextBlock dot, WpfTextBlock text)
    {
        dot.SetResourceReference(WpfTextBlock.ForegroundProperty, "MutedBrush");
        text.SetResourceReference(WpfTextBlock.ForegroundProperty, "MutedBrush");
        text.Text = "проверка…";
    }

    private static void SetServiceState(WpfTextBlock dot, WpfTextBlock text, bool success, string value)
    {
        var resource = success ? "OnlineBrush" : "ErrorTextBrush";
        dot.SetResourceReference(WpfTextBlock.ForegroundProperty, resource);
        text.SetResourceReference(WpfTextBlock.ForegroundProperty, resource);
        text.Text = value;
    }

    private void SetAllServiceStatusesUnavailable(string value)
    {
        SetServiceState(PingStatusDot, PingStatusText, false, value);
        SetServiceState(RdpStatusDot, RdpStatusText, false, value);
        SetServiceState(SmbStatusDot, SmbStatusText, false, value);
        SetServiceState(RadminStatusDot, RadminStatusText, false, value);
    }

    private void CopyValue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || button.Tag is not string value || string.IsNullOrWhiteSpace(value))
            return;

        WpfClipboard.SetText(value);
        var old = button.Content;
        button.Content = $"{value}  ✓";
        button.SetResourceReference(WpfButton.ForegroundProperty, "CopySuccessBrush");
        button.SetResourceReference(WpfButton.BackgroundProperty, "CopySuccessBackgroundBrush");

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            button.Content = old;
            button.ClearValue(WpfButton.ForegroundProperty);
            button.ClearValue(WpfButton.BackgroundProperty);
        };
        timer.Start();
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        _favoriteAction(_peer);
        UpdateFavoriteButton();
    }

    private void Notification_Click(object sender, RoutedEventArgs e)
    {
        _notificationAction(_peer);
        UpdateNotificationButton();
    }

    private void Ping_Click(object sender, RoutedEventArgs e) => _pingAction(_peer);
    private void Trace_Click(object sender, RoutedEventArgs e) => _traceAction?.Invoke(_peer);

    private void Rdp_Click(object sender, RoutedEventArgs e) => _rdpAction(_peer);

    private void SharedFolders_Click(object sender, RoutedEventArgs e)
    {
        var menu = NetworkShareMenu.CreateContextMenu(share =>
        {
            if (share is null) _sharedFoldersAction(_peer);
            else _shareAction?.Invoke(_peer, share);
        }, () => _customShareAction?.Invoke(_peer));
        menu.PlacementTarget = SharedFoldersButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
        menu.IsOpen = true;
    }
    private void Ssh_Click(object sender, RoutedEventArgs e) => _sshAction?.Invoke(_peer);

    private void RadminControl_Click(object sender, RoutedEventArgs e) => _radminAction(_peer, false);

    private void RadminFiles_Click(object sender, RoutedEventArgs e) => _radminAction(_peer, true);
}
