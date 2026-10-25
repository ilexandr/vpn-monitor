using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;

namespace VpnMonitor.Client;

public partial class AwgManagementWindow : Window
{
    private readonly Func<string, string, Task<int>> _execute;
    private readonly Func<string, AwgConnectionOptions> _readOptions;
    private readonly Func<(AwgProfileCatalog Catalog, uint State, string Text)> _read;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private AwgProfileCatalog _catalog = new();
    private bool _busy;
    private bool _closeRequested;
    private uint _state;
    public AwgManagementWindow(Func<string, string, Task<int>> execute,
        Func<(AwgProfileCatalog Catalog, uint State, string Text)>? read = null,
        Func<string, AwgConnectionOptions>? readOptions = null)
    {
        _execute = execute;
        _readOptions = readOptions ?? (_ => new());
        _read = read ?? (() => { var status = AwgServiceStatus.Read(); return (AwgProfileCatalogReader.Read(), status.State, status.Text); });
        InitializeComponent();
        ThemeBox.SelectedIndex = ThemeService.CurrentTheme == ThemeService.Dark ? 1 : 0;
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this);
        Loaded += (_, _) => { RefreshState(); _timer.Start(); };
        _timer.Tick += (_, _) => RefreshState();
        Closing += (_, e) => { if (_busy) { _closeRequested = true; e.Cancel = true; ResultText.Text = "Окно закроется после завершения операции."; } };
        Closed += (_, _) => _timer.Stop();
    }
    public void RefreshState()
    {
        if (_busy) return;
        try
        {
            var current = (ProfileList.SelectedItem as AwgProfileInfo)?.Id;
            var snapshot = _read(); _catalog = snapshot.Catalog; _state = snapshot.State;
            var connection = AwgConnectionState.Read();
            ServiceText.Text = snapshot.Text + (snapshot.State == 4 && connection is not null ? " · " + connection.Text : "");
            ServiceText.ToolTip = connection?.Details;
            SelectedText.Text = "Выбран для подключения: " + (_catalog.Profiles.FirstOrDefault(p => p.Id == _catalog.SelectedId)?.Name ?? "не выбран");
            ProfileList.ItemsSource = _catalog.Profiles;
            ProfileList.SelectedItem = _catalog.Profiles.FirstOrDefault(p => p.Id == (current ?? _catalog.SelectedId));
            UpdateButtons();
        }
        catch { ServiceText.Text = "Состояние недоступно"; ResultText.Text = "Не удалось прочитать общие настройки."; }
    }
    private void UpdateButtons()
    {
        ConnectionSettingsButton.IsEnabled = _state == 1 && ProfileList.SelectedItem is AwgProfileInfo;
        SelectButton.IsEnabled = _state == 1 && ProfileList.SelectedItem is AwgProfileInfo;
        RemoveButton.IsEnabled = ProfileList.SelectedItem is AwgProfileInfo p && (p.Id != _catalog.SelectedId || _state == 1);
        StartButton.IsEnabled = _state == 1 && _catalog.SelectedId is not null;
        StopButton.IsEnabled = _state == 4;
    }
    private void Profile_Changed(object sender, SelectionChangedEventArgs e) { if (StartButton is not null) UpdateButtons(); }
    private void Theme_Changed(object sender, SelectionChangedEventArgs e) => ThemeService.Apply(ThemeBox.SelectedIndex == 1 ? ThemeService.Dark : ThemeService.Light);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Command_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string action }) return;
        if (action == "configure")
        {
            if (_state != 1 || ProfileList.SelectedItem is not AwgProfileInfo selected) return;
            try
            {
                var dialog = OwnedWindowBehavior.Attach(new AwgConnectionSettingsWindow(selected.Name, _readOptions(selected.Id)), this);
                if (dialog.ShowDialog() == true && dialog.Options is { } options)
                    await ExecuteCommandAsync(action, System.Text.Json.JsonSerializer.Serialize(new AwgConnectionRequest(selected.Id, options)));
            }
            catch { ResultText.Text = "Не удалось прочитать настройки соединения."; }
            return;
        }
        var argument = (ProfileList.SelectedItem as AwgProfileInfo)?.Id ?? "-";
        if (action == "import")
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Профили AWG (*.conf)|*.conf", CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return; argument = dialog.FileName;
        }
        var name = (ProfileList.SelectedItem as AwgProfileInfo)?.Name ?? "";
        var message = action switch
        {
            "import" => "Добавить общий профиль из выбранного файла?",
            "select" => $"Выбрать «{name}» для подключения?",
            "remove" => $"Удалить «{name}»?",
            "start" => "Подключить общий VPN для всех пользователей?",
            _ => "Отключить VPN у всех пользователей компьютера?"
        };
        if (ThemedMessageBox.Show(this, message, "Общий туннель", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel,
            action switch { "start" => "Подключить", "stop" => "Отключить", "remove" => "Удалить", "select" => "Выбрать", _ => "Добавить" }) != MessageBoxResult.OK) return;
        await ExecuteCommandAsync(action, argument);
    }
    private async Task ExecuteCommandAsync(string action, string argument)
    {
        if (_busy) return;
        _busy = true; Commands.IsEnabled = ProfileList.IsEnabled = false; ResultText.Text = "Выполнение…";
        try
        {
            var code = await _execute(action, argument);
            ResultText.Text = code switch
            {
                0 => "Готово.", 12 => "Сначала остановите туннель; профиль не выбран или служба занята.",
                13 => "Сначала выберите другой профиль.", 14 => "Конфликт маршрутов: подключение не запущено.",
                15 => "Файл занят, повторите позже.", 16 => "Проверьте файл профиля.", 17 => "Служба не достигла нужного состояния. Проверьте журнал.",
                _ => $"Запрос не выполнен (код {code})."
            };
        }
        catch { ResultText.Text = "Ошибка операции. Профиль и ключи не выводятся."; }
        finally
        {
            _busy = false; Commands.IsEnabled = ProfileList.IsEnabled = true; RefreshState();
            if (_closeRequested) Close();
        }
    }
}
