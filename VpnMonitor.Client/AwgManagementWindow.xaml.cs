using System.Net;
using System.Net.NetworkInformation;
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
    private readonly Func<AwgStartupSnapshot> _readStartup;
    private AwgStartupSnapshot _startup = new(0);
    private readonly Func<(AwgProfileCatalog Catalog, uint State, string Text)> _read;
    private readonly Func<string, string>? _readConfig;
    private readonly Func<string, string, string, Task<int>>? _saveConfig;
    private readonly CancellationTokenSource _stop = new();
    private string _backup = "";
    private string _pingText = "Узел: ещё не проверялся.";
    private DateTimeOffset _pingAt;
    private bool _probing;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private AwgProfileCatalog _catalog = new();
    private bool _busy;
    private bool _closeRequested;
    private uint _state;
    public AwgManagementWindow(Func<string, string, Task<int>> execute,
        Func<(AwgProfileCatalog Catalog, uint State, string Text)>? read = null,
        Func<string, AwgConnectionOptions>? readOptions = null,
        Func<string, string>? readConfig = null, Func<string, string, string, Task<int>>? saveConfig = null, Func<AwgStartupSnapshot>? readStartup = null)
    {
        _readStartup = readStartup ?? AwgServiceStartup.Read;
        _execute = execute; _readConfig = readConfig; _saveConfig = saveConfig;
        _readOptions = readOptions ?? (_ => new());
        _read = read ?? (() => { var status = AwgServiceStatus.Read(); return (AwgProfileCatalogReader.Read(), status.State, status.Text); });
        InitializeComponent();
        ThemeBox.SelectedIndex = ThemeService.CurrentTheme == ThemeService.Dark ? 1 : 0;
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this);
        Loaded += (_, _) => { RefreshState(); _timer.Start(); };
        _timer.Tick += (_, _) => RefreshState();
        Closing += (_, e) => { if (_busy) { _closeRequested = true; e.Cancel = true; ResultText.Text = "Окно закроется после завершения операции."; } };
        Closed += (_, _) => { _timer.Stop(); _stop.Cancel(); };
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
            if (ProfileList.ItemsSource is not IEnumerable<AwgProfileInfo> shown || !shown.SequenceEqual(_catalog.Profiles))
            {
                ProfileList.ItemsSource = _catalog.Profiles;
                ProfileList.SelectedItem = _catalog.Profiles.FirstOrDefault(p => p.Id == current) ?? _catalog.Profiles.FirstOrDefault(p => p.Id == _catalog.SelectedId);
            }
            try {_startup=_readStartup();}catch{_startup=new(0);}
            StartupText.Text=_startup.Text;
            StartupButton.Content=_startup.Automatic?"Отключить автозапуск":"Включить автозапуск";
            UpdateButtons();
            RefreshProfileDetails();
        }
        catch { ServiceText.Text = "Состояние недоступно"; ResultText.Text = "Не удалось прочитать общие настройки."; }
    }
    private void UpdateButtons()
    {
        StartupButton.IsEnabled = !_busy && _startup.Mode is 2 or 3 or 4 && (_startup.Automatic || _catalog.SelectedId is not null);
        var connection = _state == 4 ? AwgConnectionState.Read() : null;
        TestBackupButton.IsEnabled = connection is { SupportsManualTest: true, ManualTestAvailable: true, Healthy: true, State: "Connected", IsManualTestActive: false } &&
            ProfileList.SelectedItem is AwgProfileInfo selected && selected.Id == _catalog.SelectedId;
        EditProfileButton.IsEnabled = _readConfig is not null && ProfileList.SelectedItem is AwgProfileInfo;
        ConnectionSettingsButton.IsEnabled = _state == 1 && ProfileList.SelectedItem is AwgProfileInfo;
        SelectButton.IsEnabled = _state == 1 && ProfileList.SelectedItem is AwgProfileInfo;
        RemoveButton.IsEnabled = ProfileList.SelectedItem is AwgProfileInfo p && (p.Id != _catalog.SelectedId || _state == 1);
        StartButton.IsEnabled = _state == 1 && _catalog.SelectedId is not null;
        StopButton.IsEnabled = _state == 4;
    }
    private void Profile_Changed(object sender, SelectionChangedEventArgs e) { if (StartButton is not null) { UpdateButtons(); RefreshProfileDetails(); } }
    private void RefreshProfileDetails()
    {
        if (PrimaryEndpointText is null) return;
        if (ProfileList.SelectedItem is not AwgProfileInfo profile)
        { PrimaryEndpointText.Text = "Выберите профиль для просмотра Endpoint."; BackupEndpointText.Text = BackupAvailabilityText.Text = ""; return; }
        try
        {
            var options = _readOptions(profile.Id);
            var endpoints = _readConfig is null ? "" : AwgEditableConfig.Endpoints(_readConfig(profile.Id));
            PrimaryEndpointText.Text = "Основной Endpoint: " + (endpoints.Length == 0 ? "не указан" : endpoints);
            BackupEndpointText.Text = "Резервный Endpoint: " + (options.BackupEndpoint.Length == 0 ? "не задан" : options.BackupEndpoint) +
                (!options.Enabled && options.BackupEndpoint.Length > 0 ? " · автовосстановление отключено" : "");
            if (_backup != options.BackupEndpoint) { _backup = options.BackupEndpoint; _pingAt = default; _pingText = "Узел: ещё не проверялся."; }
            var connection = profile.Id == _catalog.SelectedId && _state == 4 ? AwgConnectionState.Read() : null;
            BackupAvailabilityText.Text = options.BackupEndpoint.Length == 0 ? "Задайте резерв в «Связь и резерв…»." :
                AwgEndpointHealth.Describe(options.BackupEndpoint, connection) + "\n" + _pingText + (connection?.ManualText is { Length: > 0 } manual ? "\n" + manual : "");
            if (_backup.Length > 0 && !_probing && DateTimeOffset.Now - _pingAt > TimeSpan.FromSeconds(30)) _ = ProbeBackupAsync(_backup);
        }
        catch { PrimaryEndpointText.Text = "Не удалось прочитать параметры профиля."; BackupEndpointText.Text = BackupAvailabilityText.Text = ""; }
    }
    private async Task ProbeBackupAsync(string endpoint)
    {
        _probing = true;
        var result = "нет ответа ICMP (это не доказывает недоступность AWG)";
        try
        {
            if (IPEndPoint.TryParse(endpoint, out var target))
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(target.Address, TimeSpan.FromMilliseconds(1500), cancellationToken: _stop.Token);
                if (reply.Status == IPStatus.Success) result = "отвечает на ICMP, " + reply.RoundtripTime + " мс";
            }
        }
        catch (OperationCanceledException) { return; }
        catch { result = "ICMP-проверка недоступна"; }
        finally { _probing = false; }
        if (_stop.IsCancellationRequested || _backup != endpoint) return;
        _pingAt = DateTimeOffset.Now; _pingText = "Узел: " + result + " · " + _pingAt.ToString("HH:mm:ss") + ". Проверка каждые 30 с.";
        RefreshProfileDetails();
    }
    private void Theme_Changed(object sender, SelectionChangedEventArgs e) => ThemeService.Apply(ThemeBox.SelectedIndex == 1 ? ThemeService.Dark : ThemeService.Light);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Command_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string action }) return;
        if (action == "startup")
        {
            try {
                var startup=_readStartup();if(startup.Mode is not (2 or 3 or 4))return;
                var automatic=!startup.Automatic;
                if(automatic&&_catalog.SelectedId is null)return;
                var startupMessage=automatic?"Запускать выбранный общий туннель после загрузки Windows, без входа пользователей? Текущее соединение не прерывается.":"Отключить запуск туннеля после загрузки Windows? Текущее соединение продолжит работу; подключать туннель можно вручную.";
                if(ThemedMessageBox.Show(this,startupMessage,"Автозапуск общего туннеля",MessageBoxButton.OKCancel,MessageBoxImage.Question,MessageBoxResult.Cancel,automatic?"Включить":"Отключить")==MessageBoxResult.OK)
                    await ExecuteCommandAsync(action,automatic?"auto":"manual");
            }catch{ResultText.Text="Не удалось прочитать режим запуска службы.";}
            return;
        }
        if (action == "connection-history") { OwnedWindowBehavior.Attach(new AwgConnectionHistoryWindow(), this).ShowDialog(); return; }
        if (action == "test-backup")
        {
            if (!TestBackupButton.IsEnabled || ProfileList.SelectedItem is not AwgProfileInfo testProfile) return;
            if (ThemedMessageBox.Show(this, "Временно переключить общий VPN на резервный Endpoint и проверить поступление трафика? Служба сама вернётся к основному после успеха или таймаута. Связь у всех пользователей кратковременно прервётся; закрытие менеджера не отменяет возврат.", "Проверка резерва", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel, "Проверить") == MessageBoxResult.OK)
                await ExecuteCommandAsync(action, testProfile.Id);
            return;
        }
        if (action == "edit")
        {
            if (ProfileList.SelectedItem is not AwgProfileInfo selected || _readConfig is null) return;
            try
            {
                var original = _readConfig(selected.Id);
                var dialog = OwnedWindowBehavior.Attach(new AwgProfileEditorWindow(selected.Name, original, _state == 1 && _saveConfig is not null), this);
                if (dialog.ShowDialog() == true && dialog.Configuration is { } config)
                    await ExecuteOperationAsync("edit", selected.Id, () => _saveConfig!(selected.Id, original, config));
            }
            catch { ResultText.Text = "Не удалось открыть конфигурацию. Содержимое и ключи скрыты."; }
            return;
        }
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
    private Task ExecuteCommandAsync(string action, string argument) => ExecuteOperationAsync(action, argument);
    private async Task ExecuteOperationAsync(string action, string argument, Func<Task<int>>? operation = null)
    {
        if (_busy) return;
        _busy = true; StartupButton.IsEnabled = false; Commands.IsEnabled = ProfileList.IsEnabled = false; ResultText.Text = "Выполнение…";
        try
        {
            var code = await (operation is null ? _execute(action, argument) : operation());
            ResultText.Text = code switch
            {
                0 => action == "test-backup" ? "Запрос передан службе. Ход и результат — в статусе и истории связи." : "Готово.", 12 => "Сначала остановите туннель; профиль не выбран или служба занята.",
                21 => "Не удалось настроить автозапуск. Проверьте выбранный профиль и состояние службы; изменение могло примениться частично.",
                13 => "Сначала выберите другой профиль.", 14 => "Конфликт маршрутов: подключение не запущено.",
                15 => "Файл занят, повторите позже.", 16 => "Проверьте конфигурацию и совместимость с настройками связи. Изменения не сохранены.", 20 => "Проверка сейчас недоступна: нужен работающий основной Endpoint, заданный резерв и свободный бюджет попыток.", 19 => "Не удалось восстановить активную копию профиля. Не запускайте службу вручную; выберите профиль заново в менеджере.", 18 => "Профиль изменён в другом окне. Откройте конфигурацию заново; изменения не сохранены.", 17 => "Служба не достигла нужного состояния. Проверьте журнал.",
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
