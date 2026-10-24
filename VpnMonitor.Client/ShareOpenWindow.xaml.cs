using System.Windows;
using VpnMonitor.Client.Services;

namespace VpnMonitor.Client;

public partial class ShareOpenWindow : Window
{
    private readonly string _address;
    public string ShareName { get; private set; } = "";

    public ShareOpenWindow(string name, string address)
    {
        _address = address;
        InitializeComponent();
        TargetText.Text = $"{name} — {address}";
        Loaded += (_, _) => { ShareNameBox.Focus(); ShareNameBox.SelectAll(); };
    }
    private void Window_SourceInitialized(object? sender, EventArgs e) => ThemeService.ApplyWindowChrome(this);
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShareName = ShareNameBox.Text.Trim();
            _ = NetworkSharePath.Create(_address, ShareName);
            DialogResult = true;
        }
        catch (ArgumentException)
        {
            ErrorText.Text = "Введите имя общей папки (C$, ADMIN$, D$ или Backup), без пути и специальных символов.";
            ShareNameBox.Focus();
        }
    }
}