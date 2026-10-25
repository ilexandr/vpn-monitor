using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using VpnMonitor.Client.Services;

namespace VpnMonitor.Client;

public partial class App : System.Windows.Application
{
    private static string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VpnMonitorClient", "logs", "client.log");
    private static string LogDirectory => Path.GetDirectoryName(LogPath)!;
    private readonly bool _startClient;
    public App() : this(true) { }
    public App(bool startClient) => _startClient = startClient;

    public static void ConfigureLogDirectory(string directory)
    {
        if (Current is not null) throw new InvalidOperationException("Configure logging before application startup.");
        LogPath = Path.Combine(Path.GetFullPath(directory), "client.log");
    }

    public static string ClientLogPath => LogPath;

    private UpdateParticipant? _updateParticipant;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!_startClient) { base.OnStartup(e); return; }
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
            _updateParticipant = new UpdateParticipant("Client", () => Dispatcher.BeginInvoke(new Action(() => window.ShutdownForUpdate())));

            Log("MainWindow shown.");
        }
        catch (Exception ex)
        {
            Log("Fatal startup exception.", ex);
            ShowFatalError(ex);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _updateParticipant?.Dispose();
        base.OnExit(e);
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

            RollingLog.Append(LogPath, text.ToString());
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
                $"AWG Dash Monitor не смог запуститься.\n\n{ex.Message}\n\nЛог:\n{LogPath}",
                "AWG Dash Monitor — ошибка запуска",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // На очень раннем этапе WPF MessageBox тоже может быть недоступен.
        }
    }
}
