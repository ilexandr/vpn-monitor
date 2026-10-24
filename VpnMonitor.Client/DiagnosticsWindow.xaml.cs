using System.Windows;
using VpnMonitor.Client.Services;

namespace VpnMonitor.Client;

public partial class DiagnosticsWindow : Window
{
    private readonly Func<Task<DiagnosticCapture>> _capture;
    private readonly Action? _openLog;
    private readonly CancellationTokenSource _stop = new();
    private DiagnosticCapture? _snapshot;
    private bool _busy, _closed;
    public DiagnosticsWindow(Func<Task<DiagnosticCapture>> capture, Action? openLog = null)
    {
        _capture = capture;
        _openLog = openLog;
        InitializeComponent();
        LogButton.IsEnabled = openLog is not null;
        CopyButton.IsEnabled = ExportButton.IsEnabled = false;
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this);
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { _closed = true; _stop.Cancel(); };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
    }
    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        IncludeLogsBox.IsEnabled = !busy;
        CopyButton.IsEnabled = ExportButton.IsEnabled = !busy && _snapshot is not null;
    }
    private async Task RefreshAsync()
    {
        if (_busy || _closed) return;
        SetBusy(true);
        StatusText.Text = "Сбор сводки...";
        try
        {
            var result = await _capture();
            if (_closed) return;
            _snapshot = result;
            ReportText.Text = result.Report;
            StatusText.Text = $"Сводка обновлена: {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { if (!_closed) StatusText.Text = "Не удалось собрать сводку: " + ex.Message; }
        finally { if (!_closed) SetBusy(false); }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Log_Click(object sender, RoutedEventArgs e) => _openLog?.Invoke();
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null) return;
        try { System.Windows.Clipboard.SetText(_snapshot.Report); StatusText.Text = "Сводка скопирована."; }
        catch (Exception ex) { StatusText.Text = "Не удалось скопировать: " + ex.Message; }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null || _busy) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Диагностический отчёт ZIP (*.zip)|*.zip", FileName = $"VpnMonitor-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
        if (dialog.ShowDialog(this) != true) return;
        SetBusy(true);
        StatusText.Text = "Сохранение отчёта...";
        try
        {
            var snapshot = _snapshot;
            var includeLogs = IncludeLogsBox.IsChecked == true;
            await Task.Run(() => SupportPackage.WriteAsync(dialog.FileName, snapshot, includeLogs, App.ClientLogPath, _stop.Token));
            if (!_closed) StatusText.Text = "ZIP сохранён. Перед отправкой можно просмотреть его содержимое.";
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { if (!_closed) StatusText.Text = "Не удалось сохранить отчёт: " + ex.Message; }
        finally { if (!_closed) SetBusy(false); }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
