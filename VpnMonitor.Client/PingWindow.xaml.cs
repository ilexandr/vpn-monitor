using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfPoint = System.Windows.Point;

namespace VpnMonitor.Client;

public partial class PingWindow : Window
{
    private readonly IPAddress _address;
    private readonly PingHistory _history = new();
    private readonly PingSession _session = new();
    private readonly ObservableCollection<string> _replies = new();
    private CancellationTokenSource? _cancellation;
    private Task _run = Task.CompletedTask;
    private bool _closed;

    public PingWindow(string name, string address)
    {
        _address = IPAddress.Parse(address);
        InitializeComponent();
        PeerTitle.Text = name;
        TargetText.Text = address;
        RepliesList.ItemsSource = _replies;
        UpdateStatistics();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) => ThemeService.ApplyWindowChrome(this);
    private void Window_Loaded(object sender, RoutedEventArgs e) => Start();
    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _cancellation?.Cancel();
    }

    private void Start()
    {
        if (_closed || !_run.IsCompleted) return;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        PauseButton.Content = "Пауза";
        RunStatus.Text = "ICMP каждые 1 сек. • Таймаут 800 мс • Красные отметки — нет ответа";
        _run = RunAsync(cancellation);
    }

    private async Task RunAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await _session.RunAsync(_address, reply =>
            {
                if (_closed) return;
                _history.Add(reply.Success ? reply.Milliseconds : null);
                var sample = _history.Recent[^1];
                var text = $"{sample.Time:HH:mm:ss}   {(reply.Success ? $"Ответ: {reply.Milliseconds} мс" : reply.Detail)}";
                _replies.Add(text);
                while (_replies.Count > PingHistory.HistoryLimit) _replies.RemoveAt(0);
                RepliesList.ScrollIntoView(text);
                UpdateStatistics();
            }, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            App.Log("Continuous ping failed.", ex);
            if (!_closed) RunStatus.Text = "Ошибка проверки. Можно запустить снова.";
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose();
            if (!_closed) PauseButton.Content = "Продолжить";
        }
    }

    private async Task StopAsync()
    {
        _cancellation?.Cancel();
        await _run;
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = ResetButton.IsEnabled = false;
        if (_run.IsCompleted) Start();
        else
        {
            await StopAsync();
            if (!_closed) RunStatus.Text = "Пауза • Статистика сохранена";
        }
        if (!_closed) PauseButton.IsEnabled = ResetButton.IsEnabled = true;
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = ResetButton.IsEnabled = false;
        var wasRunning = !_run.IsCompleted;
        await StopAsync();
        if (_closed) return;
        _history.Clear();
        _replies.Clear();
        UpdateStatistics();
        if (wasRunning) Start();
        PauseButton.IsEnabled = ResetButton.IsEnabled = true;
    }

    private void UpdateStatistics()
    {
        StatisticsText.Text = $"Min: {_history.Minimum?.ToString() ?? "—"} мс    Avg: {_history.Average?.ToString("0.0") ?? "—"} мс    Max: {_history.Maximum?.ToString() ?? "—"} мс";
        PacketsText.Text = $"Отправлено: {_history.Sent}   Получено: {_history.Received}   Потеряно: {_history.Lost} ({_history.LossPercent:0.0}%)";
        DrawGraph();
    }

    private void Graph_SizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();
    private WpfBrush Brush(string key) => TryFindResource(key) as WpfBrush ?? System.Windows.Media.Brushes.Gray;

    private void DrawGraph()
    {
        if (Graph is null || ScaleText is null) return;
        Graph.Children.Clear();
        var samples = _history.Recent;
        var width = Graph.ActualWidth;
        var height = Graph.ActualHeight - 6;
        var maximum = Math.Max(10d, samples.Where(s => s.Milliseconds.HasValue).Select(s => (double)s.Milliseconds!.Value).DefaultIfEmpty(0).Max() * 1.15);
        ScaleText.Text = $"0–{maximum:0} мс • последние {samples.Count}/{PingHistory.HistoryLimit} проверок";
        if (width <= 0 || height <= 0) return;
        for (var row = 0; row <= 2; row++)
            Graph.Children.Add(new Line { X1 = 0, X2 = width, Y1 = row * height / 2, Y2 = row * height / 2, Stroke = Brush("BorderBrush") });
        WpfPoint? previous = null;
        for (var index = 0; index < samples.Count; index++)
        {
            var x = samples.Count > 1 ? index * (width - 4) / (samples.Count - 1) + 2 : 2;
            if (samples[index].Milliseconds is not long milliseconds)
            {
                Graph.Children.Add(new Line { X1 = x, X2 = x, Y1 = height - 8, Y2 = height, StrokeThickness = 2, Stroke = Brush("OfflineBrush") });
                previous = null;
                continue;
            }
            var point = new WpfPoint(x, height - milliseconds / maximum * height);
            if (previous is WpfPoint before)
                Graph.Children.Add(new Line { X1 = before.X, Y1 = before.Y, X2 = point.X, Y2 = point.Y, StrokeThickness = 2, Stroke = Brush("AccentCyanBrush") });
            var dot = new Ellipse { Width = 3, Height = 3, Fill = Brush("AccentCyanBrush") };
            Canvas.SetLeft(dot, point.X - 1.5);
            Canvas.SetTop(dot, point.Y - 1.5);
            Graph.Children.Add(dot);
            previous = point;
        }
    }
}
