using System.Windows;
using System.Windows.Threading;
using VpnMonitor.Client.Services;
namespace VpnMonitor.Client;
public partial class AwgConnectionHistoryWindow : Window
{
    private readonly Func<AwgConnectionEvent[]> _read;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    public AwgConnectionHistoryWindow(Func<AwgConnectionEvent[]>? read = null)
    {
        _read = read ?? (() => AwgConnectionHistory.Read()); InitializeComponent();
        SourceInitialized += (_,_) => ThemeService.ApplyWindowChrome(this);
        Loaded += (_,_) => { Refresh(); _timer.Start(); }; _timer.Tick += (_,_) => Refresh(); Closed += (_,_) => _timer.Stop();
    }
    public void Refresh()
    {
        var rows = _read();
        if(HistoryGrid.ItemsSource is not IEnumerable<AwgConnectionEvent> old || !old.SequenceEqual(rows)) {
            var selected = HistoryGrid.SelectedItem as AwgConnectionEvent;
            HistoryGrid.ItemsSource = rows; HistoryGrid.SelectedItem = rows.FirstOrDefault(e=>e==selected);
        }
        SummaryText.Text = rows.Length == 0 ? "История появится после запуска службы версии 0.1.75 или новее." : $"Событий: {rows.Length}. Обновление каждые 2 с.";
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
