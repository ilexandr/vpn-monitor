using System.Windows;
using System.Windows.Threading;
using VpnMonitor.Client.Services;
namespace VpnMonitor.Client;
public partial class AwgStatusWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Func<string>? _health;
    public AwgStatusWindow() : this(null) { }
    public AwgStatusWindow(Func<string>? health)
    {
        _health = health;
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            Close();
        };
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this);
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        _timer.Tick += (_, _) => Refresh();
        Closed += (_, _) => _timer.Stop();
    }
    private void Refresh()
    {
        try
        {
            var status = AwgServiceStatus.Read();
            ServiceStatus.Text = status.Text;
            HealthText.Text = _health?.Invoke() ?? "Проверка доступности выполняется в основном окне приложения.";
            var catalog = AwgProfileCatalogReader.Read();
            var selected = catalog.Profiles.FirstOrDefault(p => p.Id == catalog.SelectedId);
            SelectedProfileText.Text = selected is null ? "Профиль не выбран" : $"Выбран для подключения: {selected.Name}";
            ProfilesList.ItemsSource = catalog.Profiles;
            DetailsText.Text = status.ExitCode != 0 ? $"Код последнего завершения: {status.ExitCode}"
                : status.State == 0 ? "Компонент службы ещё не установлен администратором." : AwgServiceStatus.ServiceName;
        }
        catch (Exception ex) { ServiceStatus.Text = "Состояние недоступно"; DetailsText.Text = ex.Message; }
    }
    private void ManagementInfo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = AwgProfileCatalogReader.ManagerPath;
            if (!System.IO.File.Exists(path))
            {
                System.Windows.MessageBox.Show(this, "Панель управления AWG не установлена. Установите компоненты от администратора.",
                    "AWG", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var startInfo = new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(path)!
            };
            startInfo.ArgumentList.Add("--theme");
            startInfo.ArgumentList.Add(ThemeService.CurrentTheme);
            using var process = System.Diagnostics.Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            DetailsText.Text = "Запрос запуска панели отменён.";
        }
        catch (Exception ex)
        {
            DetailsText.Text = $"Не удалось открыть панель AWG: {ex.Message}";
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
