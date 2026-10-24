using System.Windows;
using System.Windows.Data;
using VpnMonitor.Client.Services;
namespace VpnMonitor.Client;
public partial class PeerHistoryWindow : Window
{
    private readonly ListCollectionView _view;
    public PeerHistoryWindow(string name, PeerHistory history, string peerId)
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            Close();
        };
        Title = $"История — {name}";
        _view = new ListCollectionView(history.Entries) { Filter = item => item is PeerHistoryEntry entry && PeerHistoryExport.Matches(entry, peerId, SearchBox.Text) };
        HistoryGrid.ItemsSource = _view;
        Closed += (_, _) => { HistoryGrid.ItemsSource = null; _view.DetachFromSourceCollection(); };
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Search_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => _view?.Refresh();
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "История клиента CSV (*.csv)|*.csv",
            FileName = $"VpnMonitor-history-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        ExportButton.IsEnabled = false;
        try
        {
            var csv = PeerHistoryExport.Create(_view.Cast<PeerHistoryEntry>().ToArray());
            await System.IO.File.WriteAllTextAsync(dialog.FileName, csv, new System.Text.UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Не удалось сохранить историю: {ex.Message}", "История клиента", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ExportButton.IsEnabled = true; }
    }
}
