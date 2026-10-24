using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using VpnMonitor.Client.Services;

namespace VpnMonitor.Client;

public partial class TraceRouteWindow : Window
{
    private readonly IPAddress _target;
    private readonly TraceRouteSession _session = new();
    private readonly ObservableCollection<TraceHop> _hops = new();
    private CancellationTokenSource? _cancellation;
    private Task _run = Task.CompletedTask;
    private bool _closed;

    public TraceRouteWindow(string name, string address)
    {
        _target = IPAddress.Parse(address);
        InitializeComponent();
        PeerTitle.Text = name;
        TargetText.Text = address;
        HopsGrid.ItemsSource = _hops;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) => ThemeService.ApplyWindowChrome(this);
    private void Window_Loaded(object sender, RoutedEventArgs e) => Start();
    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _cancellation?.Cancel();
    }
    private void Start_Click(object sender, RoutedEventArgs e) => Start();
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        StopButton.IsEnabled = false;
        RunStatus.Text = "Остановка...";
    }

    private void Start()
    {
        if (_closed || !_run.IsCompleted) return;
        if (!int.TryParse(MaxHopsBox.Text, out var maximum) || maximum is < 1 or > 64)
        {
            RunStatus.Text = "Укажите число узлов от 1 до 64.";
            return;
        }
        _hops.Clear();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        StartButton.IsEnabled = MaxHopsBox.IsEnabled = false;
        StopButton.IsEnabled = true;
        RunStatus.Text = "Трассировка выполняется...";
        _run = RunAsync(maximum, cancellation);
    }

    private async Task RunAsync(int maximum, CancellationTokenSource cancellation)
    {
        try
        {
            var result = await _session.RunAsync(_target, maximum, hop =>
            {
                if (_closed) return;
                _hops.Add(hop);
                HopsGrid.ScrollIntoView(hop);
                RunStatus.Text = $"Проверено узлов: {hop.Number} из {maximum}";
            }, cancellation.Token);
            if (!_closed)
                RunStatus.Text = result switch
                {
                    TraceCompletion.Reached => "Цель достигнута.",
                    TraceCompletion.Unreachable => "Получено сообщение о недоступности или ошибке ICMP.",
                    _ => "Достигнут предел узлов. Цель не ответила; возможно, ICMP фильтруется."
                };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_closed) RunStatus.Text = "Трассировка остановлена.";
        }
        catch (Exception ex)
        {
            App.Log("Traceroute failed.", ex);
            if (!_closed) RunStatus.Text = "Ошибка трассировки. Можно повторить.";
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose();
            if (!_closed)
            {
                StartButton.IsEnabled = MaxHopsBox.IsEnabled = true;
                StopButton.IsEnabled = false;
            }
        }
    }
}