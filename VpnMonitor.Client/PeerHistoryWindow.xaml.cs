using System.Windows;
using System.Windows.Data;
using VpnMonitor.Client.Services;
namespace VpnMonitor.Client;
public partial class PeerHistoryWindow : Window
{
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
        var view = new ListCollectionView(history.Entries) { Filter = item => item is PeerHistoryEntry entry && entry.PeerId == peerId };
        HistoryGrid.ItemsSource = view;
        Closed += (_, _) => { HistoryGrid.ItemsSource = null; view.DetachFromSourceCollection(); };
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}