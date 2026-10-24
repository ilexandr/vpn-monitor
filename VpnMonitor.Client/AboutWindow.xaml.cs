using System.Diagnostics;
using System.Windows;
using VpnMonitor.Client.Services;
namespace VpnMonitor.Client;
public partial class AboutWindow : Window
{
    private readonly CancellationTokenSource _stop = new();
    private UpdateRelease? _release;
    private readonly Action? _diagnostics;
    public AboutWindow() : this(null) { }
    public AboutWindow(Action? diagnostics)
    {
        _diagnostics = diagnostics;
        InitializeComponent();
        DiagnosticsButton.IsEnabled = diagnostics is not null;
        VersionText.Text = $"Версия {UpdateService.CurrentVersion} • Windows x64";
        SourceInitialized += (_,_) => ThemeService.ApplyWindowChrome(this);
        Closed += (_,_) => _stop.Cancel();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
    }
    private async void Check_Click(object sender,RoutedEventArgs e)
    {
        CheckButton.IsEnabled=false; UpdateButton.IsEnabled=false; _release=null;
        StatusText.Text="Проверка GitHub Releases…";
        try
        {
            var release = await UpdateService.CheckAsync(_stop.Token);
            if (_stop.IsCancellationRequested) return;
            _release=release;
            NotesText.Text=release?.Notes ?? "";
            StatusText.Text=release is null ? "Опубликованных стабильных релизов пока нет." : release.Version > UpdateService.CurrentVersion ? $"Доступна версия {release.Version}." : "Установлена актуальная версия.";
            UpdateButton.IsEnabled=release is not null && release.Version > UpdateService.CurrentVersion;
        }
        catch(OperationCanceledException) { if (!_stop.IsCancellationRequested) StatusText.Text="Превышено время ожидания GitHub."; }
        catch(Exception ex) { StatusText.Text=$"Не удалось проверить обновления: {ex.Message}"; }
        finally { CheckButton.IsEnabled=true; }
    }
    private void Update_Click(object sender,RoutedEventArgs e)
    {
        if(_release is null) return;
        try { UpdateService.Start(_release.Id); UpdateButton.IsEnabled=false; StatusText.Text="Обновление запущено. Загрузка и проверка выполняются до закрытия программы; результат появится в отдельном окне."; }
        catch(System.ComponentModel.Win32Exception ex) when(ex.NativeErrorCode==1223) { StatusText.Text="Запрос обновления отменён."; }
        catch(Exception ex) { StatusText.Text=$"Не удалось начать обновление: {ex.Message}"; }
    }
    private void Repository_Click(object sender,RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(UpdateService.Repository) { UseShellExecute=true }); }
        catch (Exception ex) { StatusText.Text = "Не удалось открыть GitHub: " + ex.Message; }
    }
    private void Close_Click(object sender,RoutedEventArgs e) => Close();
    private void Diagnostics_Click(object sender,RoutedEventArgs e) => _diagnostics?.Invoke();
}
