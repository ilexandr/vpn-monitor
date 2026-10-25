using System.Windows;
using System.Windows.Controls;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
namespace VpnMonitor.Client;

public sealed class AwgConnectionSettingsWindow : Window
{
    public AwgConnectionOptions? Options { get; private set; }
    public AwgConnectionSettingsWindow(string profile, AwgConnectionOptions options)
    {
        Title = "Связь и резервный Endpoint — " + profile; Width = 620; Height = 680; MinWidth = 500; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; SetResourceReference(StyleProperty, typeof(Window));
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this);
        var root = new DockPanel { Margin = new Thickness(22) };
        var footer = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new(0, 16, 0, 0) };
        var save = new Button { Content = "Сохранить", Padding = new(14, 8, 14, 8), IsDefault = true }; save.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        footer.Children.Add(save); footer.Children.Add(new Button { Content = "Отмена", IsCancel = true, Padding = new(14, 8, 14, 8), Margin = new(8, 0, 0, 0) });
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var panel = new StackPanel(); root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text = "Контроль общего соединения", FontSize = 21, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Основной Endpoint берётся из профиля. Настройки общие для всех Windows/RDP-сеансов и применяются при следующем подключении. Ручное отключение не запускает восстановление.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 14) });
        var enabled = new CheckBox { Content = "Переподключаться при подтверждённом сбое", IsChecked = options.Enabled, Margin = new(0, 0, 0, 10) }; panel.Children.Add(enabled);
        var physical = new CheckBox { Content = "Направлять Endpoint через физическую сеть (обход других VPN)", IsChecked = options.PreferPhysical, Margin = new(0, 0, 0, 10) }; panel.Children.Add(physical);
        TextBox Field(string label, string value)
        {
            panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 4) });
            var box = new TextBox { Text = value, Padding = new(8, 6, 8, 6) }; panel.Children.Add(box); return box;
        }
        var backup = Field("Резервный Endpoint (IP:порт; пусто — только основной)", options.BackupEndpoint);
        var host = Field("Контрольный IPv4 внутри VPN (пусто — адрес .1 из маршрута профиля)", options.ProbeHost);
        var port = Field("TCP-порт контрольного сервиса", options.ProbePort.ToString());
        var interval = Field("Интервал проверки, с", options.IntervalSeconds.ToString());
        var failure = Field("Подтверждать потерю связи, с", options.FailureSeconds.ToString());
        var cooldown = Field("Минимальная пауза между попытками, с", options.CooldownSeconds.ToString());
        var failback = new CheckBox { Content = "Автоматически пробовать вернуться к основному Endpoint", IsChecked = options.ReturnToPrimary, Margin = new(0, 14, 0, 0) }; panel.Children.Add(failback);
        var retry = Field("Период попытки возврата, с (короткое переподключение)", options.PrimaryRetrySeconds.ToString());
        panel.Children.Add(new TextBlock { Text = "Не более 3 переподключений за 10 минут. Проверяются TCP, ICMP и поступление входящего трафика: остановка одного API-сервиса не должна вызывать переподключение. Для профилей без подходящего контрольного IPv4 автовосстановление ожидает явного адреса.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 14, 0, 0) });
        save.Click += (_, _) =>
        {
            try
            {
                int Number(TextBox b) => int.TryParse(b.Text, out var n) ? n : throw new ArgumentException("Параметры времени и порт должны быть числами.");
                var value = new AwgConnectionOptions { Enabled = enabled.IsChecked == true, PreferPhysical = physical.IsChecked == true,
                    BackupEndpoint = backup.Text.Trim(), ProbeHost = host.Text.Trim(), ProbePort = Number(port), IntervalSeconds = Number(interval),
                    FailureSeconds = Number(failure), CooldownSeconds = Number(cooldown), ReturnToPrimary = failback.IsChecked == true, PrimaryRetrySeconds = Number(retry) };
                value.Validate(); Options = value; DialogResult = true;
            }
            catch (ArgumentException ex) { ThemedMessageBox.Show(this, ex.Message, "Настройки соединения", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        Content = root;
    }
}
