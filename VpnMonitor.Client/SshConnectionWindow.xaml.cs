using System.Windows;
using VpnMonitor.Client.Services;

namespace VpnMonitor.Client;

public partial class SshConnectionWindow : Window
{
    private readonly string _executable;
    private readonly string _address;
    public string Username { get; private set; } = "";
    public int Port { get; private set; } = 22;

    public SshConnectionWindow(string name, string address, string executable, string username, int port)
    {
        _address = address;
        _executable = executable;
        InitializeComponent();
        TargetText.Text = $"{name} — {address}";
        UsernameBox.Text = username;
        PortBox.Text = Math.Clamp(port, 1, 65535).ToString();
        Loaded += (_, _) => { UsernameBox.Focus(); UsernameBox.SelectAll(); };
    }
    private void Window_SourceInitialized(object? sender, EventArgs e) => ThemeService.ApplyWindowChrome(this);
    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out var port))
        {
            ErrorText.Text = "Введите порт SSH от 1 до 65535.";
            return;
        }
        try
        {
            Username = UsernameBox.Text.Trim();
            Port = port;
            _ = SshService.CreateStartInfo(_executable, _address, Username, Port);
            DialogResult = true;
        }
        catch (ArgumentException ex) { ErrorText.Text = ex.Message; }
    }
}