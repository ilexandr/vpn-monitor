using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VpnMonitor.Client.Models;

namespace VpnMonitor.Client;

public partial class TrayNetworkPopup : Window
{
    private DispatcherTimer? _statusTimer;
    private bool _isClosing;

    public event EventHandler? OpenRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;

    public TrayNetworkPopup(IReadOnlyList<NetworkAddressItem> items)
    {
        InitializeComponent();
        UpdateItems(items);
        Loaded += (_, _) => PositionNearTray();
    }

    public void UpdateItems(IReadOnlyList<NetworkAddressItem> items)
    {
        InterfacesList.ItemsSource = items.ToList();
        SummaryText.Text = items.Count == 1 ? "1 IPv4 адрес" : $"{items.Count} IPv4 адресов";
    }

    private void PositionNearTray()
    {
        try
        {
            UpdateLayout();

            var cursor = System.Windows.Forms.Cursor.Position;
            var screen = System.Windows.Forms.Screen.FromPoint(cursor);
            var dpi = VisualTreeHelper.GetDpi(this);

            var left = screen.WorkingArea.Right / dpi.DpiScaleX - ActualWidth - 8;
            var top = screen.WorkingArea.Bottom / dpi.DpiScaleY - ActualHeight - 8;

            Left = Math.Max(screen.WorkingArea.Left / dpi.DpiScaleX + 8, left);
            Top = Math.Max(screen.WorkingArea.Top / dpi.DpiScaleY + 8, top);
        }
        catch (Exception ex)
        {
            App.Log("Failed to position tray network popup.", ex);
        }
    }

    private void CopyIp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string ip } || string.IsNullOrWhiteSpace(ip))
            return;

        try
        {
            System.Windows.Clipboard.SetText(ip);
            ShowStatus($"Скопировано: {ip}");
        }
        catch (Exception ex)
        {
            App.Log("Failed to copy tray IPv4 address.", ex);
            ShowStatus("Не удалось скопировать IP");
        }
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        _statusTimer?.Stop();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer?.Stop();
            StatusText.Text = string.Empty;
        };
        _statusTimer.Start();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        // Сначала закрываем popup, а уже потом активируем главное окно.
        // Иначе активация MainWindow вызывает Deactivated у popup и WPF
        // получает два Close() одновременно.
        CloseSafely();
        OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
        ShowStatus("Обновление запущено");
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // Закрываем popup до открытия модального окна настроек, чтобы
        // Deactivated не пытался повторно закрыть уже закрывающееся окно.
        CloseSafely();
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseSafely();

    private void Window_Deactivated(object? sender, EventArgs e) => CloseSafely();

    private void CloseSafely()
    {
        if (_isClosing)
            return;

        _isClosing = true;
        try
        {
            Close();
        }
        catch (InvalidOperationException ex)
        {
            // Защита от повторного Close во время оконных сообщений активации.
            App.Log("Tray network popup close race ignored.", ex);
        }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            CloseSafely();
    }
}
