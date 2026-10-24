using System.Windows;
namespace VpnMonitor.Client;
public partial class AwgPreviewWindow : Window
{
    public AwgPreviewWindow(string preview)
    {
        InitializeComponent();
        PreviewText.Text = preview;
        SourceInitialized += (_, _) => Services.ThemeService.ApplyWindowChrome(this);
    }
}