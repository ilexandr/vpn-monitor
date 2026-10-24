using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using VpnMonitor.Client.Services;

namespace VpnMonitor.Client;

public partial class App : System.Windows.Application
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VpnMonitorClient",
        "logs");

    private static readonly string LogPath = Path.Combine(LogDirectory, "client.log");

    public static string ClientLogPath => LogPath;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            Log("Application starting.");
            base.OnStartup(e);

            var startupSettings = new SettingsService().Load();
            ThemeService.Apply(startupSettings.Theme);

            var window = new MainWindow();
            MainWindow = window;
            window.Show();

            Log("MainWindow shown.");
        }
        catch (Exception ex)
        {
            Log("Fatal startup exception.", ex);
            ShowFatalError(ex);
            Shutdown(-1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Unhandled WPF dispatcher exception.", e.Exception);
        ShowFatalError(e.Exception);
        e.Handled = true;
        Shutdown(-1);
    }

    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Log("Unhandled AppDomain exception.", ex);
        else
            Log($"Unhandled AppDomain exception: {e.ExceptionObject}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }

    public static void Log(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);

            var text = new StringBuilder()
                .Append('[')
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append("] ")
                .AppendLine(message);

            if (exception is not null)
                text.AppendLine(exception.ToString());

            File.AppendAllText(LogPath, text.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // Логирование само по себе никогда не должно завершать приложение.
        }
    }

    private static void ShowFatalError(Exception ex)
    {
        try
        {
            System.Windows.MessageBox.Show(
                $"VPN Monitor не смог запуститься.\n\n{ex.Message}\n\nЛог:\n{LogPath}",
                "VPN Monitor — ошибка запуска",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // На очень раннем этапе WPF MessageBox тоже может быть недоступен.
        }
    }
}
