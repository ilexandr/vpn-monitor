using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VpnMonitor.Client.Services;
namespace VpnMonitor.Client;

public partial class LogWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource _lifetime = new();
    private string _text = "";
    private bool _reading;
    private bool _closed;
    private readonly Action? _diagnostics;
    private string SelectedLogPath => App.ClientLogPath + (LogChoiceBox.SelectedIndex > 0 ? "." + LogChoiceBox.SelectedIndex : "");
    public LogWindow() : this(null) { }
    public LogWindow(Action? diagnostics)
    {
        _diagnostics = diagnostics;
        InitializeComponent();
        DiagnosticsButton.IsEnabled = diagnostics is not null;
        LogChoiceBox.SelectedIndex = 0;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            Close();
        };
        LogPathText.Text = App.ClientLogPath;
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this);
        Loaded += async (_, _) => { _timer.Start(); await RefreshAsync(); };
        _timer.Tick += async (_, _) => { if (AutoRefreshBox.IsChecked == true) await RefreshAsync(); };
        Closed += (_, _) => { _closed = true; _timer.Stop(); _lifetime.Cancel(); if (!_reading) _lifetime.Dispose(); };
    }
    private async Task RefreshAsync()
    {
        if (_reading || _closed) return;
        _reading = true;
        var path = SelectedLogPath;
        try
        {
            var snapshot = await LogReader.ReadAsync(path, _lifetime.Token);
            if (_closed || path != SelectedLogPath) return;
            LogPathText.Text = path;
            _text = snapshot.Text;
            ApplyFilter();
            StatusText.Text = !snapshot.Exists ? "Файл журнала ещё не создан."
                : $"{(snapshot.Truncated ? "Последние 512 КБ. " : "")}Обновлено: {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed) StatusText.Text = $"Не удалось прочитать журнал: {ex.Message}";
        }
        finally
        {
            _reading = false;
            if (_closed) _lifetime.Dispose();
            else if (path != SelectedLogPath) _ = RefreshAsync();
        }
    }
    private void ApplyFilter()
    {
        var filtered = LogReader.Filter(_text, SearchBox.Text);
        if (LogText.Text == filtered) return;
        var offset = LogText.VerticalOffset;
        var atBottom = offset >= LogText.ExtentHeight - LogText.ViewportHeight - 2;
        LogText.Text = filtered;
        if (atBottom) LogText.ScrollToEnd(); else LogText.ScrollToVerticalOffset(offset);
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (LogText is not null) ApplyFilter(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = LogText.SelectionLength > 0 ? LogText.SelectedText : LogText.Text;
            if (text.Length > 0) System.Windows.Clipboard.SetText(text);
            StatusText.Text = text.Length > 0 ? "Текст скопирован." : "Нет записей для копирования.";
        }
        catch (Exception ex) { StatusText.Text = $"Не удалось скопировать: {ex.Message}"; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void LogChoice_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) await RefreshAsync(); }
    private void Diagnostics_Click(object sender, RoutedEventArgs e) => _diagnostics?.Invoke();
    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(App.ClientLogPath)!;
            System.IO.Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText.Text = "Не удалось открыть папку: " + ex.Message; }
    }
}
