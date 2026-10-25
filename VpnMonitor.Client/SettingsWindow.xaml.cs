using System.IO;
using System.Windows;
using System.Windows.Controls;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MessageBox = System.Windows.MessageBox;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;

namespace VpnMonitor.Client;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly ClientSettings _settings;
    private readonly AutostartService _autostartService = new();
    private readonly RadminService _radminService = new();
    private readonly Action? _testNotification;
    private readonly Action<Window, int>? _openMonitoring;
    private bool _syncingNavigation;
    private StackPanel[] Sections => [SettingsPage0, SettingsPage1, SettingsPage2, SettingsPage3, SettingsPage4, SettingsPage5, SettingsPage6];

    public SettingsWindow(SettingsService settingsService, ClientSettings settings, Action? testNotification = null, Action<Window, int>? openMonitoring = null)
    {
        _testNotification = testNotification;
        _openMonitoring = openMonitoring;
        InitializeComponent();
        TestNotificationButton.IsEnabled = testNotification is not null;
        MonitoringProfilesButton.IsEnabled = MonitoringRulesButton.IsEnabled = openMonitoring is not null;
        _settingsService = settingsService;
        _settings = settings;
        SettingsNavigation.SelectedIndex = 0;

        LoadForm(settings);
        RestoreWindowSize();
        Closing += SettingsWindow_Closing;
        BuildSettingsSearchIndex();
        Loaded += (_, _) => SyncNavigation();
    }

    private void TestNotification_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _testNotification?.Invoke();
            TestNotificationText.Text = "Запрос передан Windows. Если сообщения нет, проверьте разрешения уведомлений и режим «Не беспокоить».";
        }
        catch (Exception ex) { TestNotificationText.Text = "Не удалось отправить тестовое уведомление: " + ex.Message; }
    }
    private void MonitoringProfiles_Click(object sender, RoutedEventArgs e) => _openMonitoring?.Invoke(this, 1);
    private void MonitoringRules_Click(object sender, RoutedEventArgs e) => _openMonitoring?.Invoke(this, 2);

    private void LoadForm(ClientSettings settings)
    {
        ServerUrlBox.Text = settings.ServerUrl;
        UsernameBox.Text = settings.Username;
        PasswordBox.Password = _settingsService.UnprotectPassword(settings.ProtectedPassword);
        RefreshSecondsBox.Text = Math.Clamp(settings.RefreshSeconds, 2, 300).ToString();

        var theme = ThemeService.Normalize(settings.Theme);
        ThemeComboBox.SelectedIndex = theme == ThemeService.Dark ? 1 : 0;

        ShowStatusColumnCheckBox.IsChecked = settings.ShowStatusColumn;
        ShowComputerColumnCheckBox.IsChecked = settings.ShowComputerColumn;
        ShowVpnIpColumnCheckBox.IsChecked = settings.ShowVpnIpColumn;
        ShowHandshakeColumnCheckBox.IsChecked = settings.ShowHandshakeColumn;
        ShowTrafficColumnCheckBox.IsChecked = settings.ShowTrafficColumn;
        ShowGroupsColumnCheckBox.IsChecked = settings.ShowGroupsColumn;

        var detectedRadmin = _radminService.ResolveExecutable(settings.RadminViewerPath);
        RadminPathBox.Text = detectedRadmin ?? settings.RadminViewerPath ?? "";
        RadminPortBox.Text = (settings.RadminPort is >= 1 and <= 65535
            ? settings.RadminPort
            : RadminService.DefaultPort).ToString();
        RadminUsernameBox.Text = string.IsNullOrWhiteSpace(settings.RadminUsername) ? "Admin" : settings.RadminUsername;
        RadminAutofillUsernameCheckBox.IsChecked = settings.RadminAutofillUsername;

        try
        {
            AutostartCheckBox.IsChecked = settings.StartWithWindows || _autostartService.IsEnabledForCurrentExecutable();
        }
        catch
        {
            AutostartCheckBox.IsChecked = settings.StartWithWindows;
        }

        StartMinimizedCheckBox.IsChecked = settings.StartMinimized;
        StatusNotificationsEnabledCheckBox.IsChecked = settings.StatusNotificationsEnabled;
        NotifyWhenOnlineCheckBox.IsChecked = settings.NotifyWhenOnline;
        NotifyWhenOfflineCheckBox.IsChecked = settings.NotifyWhenOffline;
        NotificationSourceBox.SelectedIndex = settings.StatusNotificationSource == "active" ? 0 : 1;
        ActiveChecksEnabledBox.IsChecked = settings.ActiveChecksEnabled;
        SavePeerHistoryBox.IsChecked = settings.SavePeerHistory;
        ActiveChecksWatchedOnlyBox.IsChecked = settings.ActiveChecksWatchedOnly;
        ActiveCheckSecondsBox.Text = Math.Clamp(settings.ActiveCheckSeconds, 2, 300).ToString();
        ActiveCheckTcpFallbackBox.IsChecked = settings.ActiveCheckTcpFallback;
        ActiveCheckTcpPortBox.Text = Math.Clamp(settings.ActiveCheckTcpPort, 1, 65535).ToString();

    }

    private sealed record SettingsSearchEntry(string Title, int Page, FrameworkElement Target);
    private readonly List<SettingsSearchEntry> _settingsSearchIndex = [];

    private void BuildSettingsSearchIndex()
    {
        var pages = new[] { SettingsPage0, SettingsPage1, SettingsPage2, SettingsPage3, SettingsPage4, SettingsPage5, SettingsPage6 };
        for (var page = 0; page < pages.Length; page++)
        {
            var title = ((ListBoxItem)SettingsNavigation.Items[page]).Content?.ToString() ?? "";
            foreach (var element in Descendants(pages[page]))
            {
                var text = element switch
                {
                    TextBlock block => block.Text,
                    System.Windows.Controls.ContentControl control when control.Content is string content => content,
                    _ => ""
                };
                if (!string.IsNullOrWhiteSpace(text))
                    _settingsSearchIndex.Add(new($"{title} · {text}", page, element));
            }
        }
    }

    private static IEnumerable<FrameworkElement> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
            if (child is FrameworkElement element)
            {
                yield return element;
                foreach (var descendant in Descendants(element)) yield return descendant;
            }
    }

    private void SettingsSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (SettingsSearchResults is null) return;
        var words = SettingsSearchBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var results = words.Length == 0 ? [] : _settingsSearchIndex
            .Where(entry => words.All(word => entry.Title.Contains(word, StringComparison.CurrentCultureIgnoreCase)))
            .ToArray();
        SettingsSearchResults.ItemsSource = results;
        SettingsSearchResults.Visibility = results.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Text = words.Length == 0 ? "Поиск настроек" : results.Length == 0 ? "Ничего не найдено" : $"Найдено: {results.Length}";
    }

    private void SearchResult_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, SettingsSearchResults) ||
            SettingsSearchResults.SelectedItem is not SettingsSearchEntry entry) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded) return;
            var target = entry.Target;
            while (!target.IsVisible && LogicalTreeHelper.GetParent(target) is FrameworkElement parent) target = parent;
            double y;
            try { y = target.TranslatePoint(new System.Windows.Point(), SettingsSections).Y; }
            catch (InvalidOperationException) { y = Sections[entry.Page].TranslatePoint(new System.Windows.Point(), SettingsSections).Y; }
            SectionScroll.ScrollToVerticalOffset(Math.Max(0, y - 24));
        }));
    }

    private void SettingsNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // ComboBox selection events bubble through the same visual tree.
        if (!ReferenceEquals(e.Source, SettingsNavigation) || _syncingNavigation || !IsLoaded) return;
        if (SettingsNavigation.SelectedIndex < 0) return;
        var section = Sections[SettingsNavigation.SelectedIndex];
        SectionScroll.ScrollToVerticalOffset(section.TranslatePoint(new System.Windows.Point(), SettingsSections).Y);
    }

    private void SectionScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SectionScroll) && IsLoaded) SyncNavigation();
    }

    private void SyncNavigation()
    {
        var index = 0;
        for (var i = 0; i < Sections.Length; i++)
            if (Sections[i].TranslatePoint(new System.Windows.Point(), SettingsSections).Y <= SectionScroll.VerticalOffset + 28) index = i;
        _syncingNavigation = true;
        try { SettingsNavigation.SelectedIndex = index; }
        finally { _syncingNavigation = false; }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        ThemeService.ApplyWindowChrome(this);
    }

    private void RestoreWindowSize()
    {
        var workArea = SystemParameters.WorkArea;

        if (_settings.SettingsWindowWidth is > 0)
            Width = Math.Clamp(_settings.SettingsWindowWidth.Value, MinWidth, Math.Max(MinWidth, workArea.Width));

        if (_settings.SettingsWindowHeight is > 0)
            Height = Math.Clamp(_settings.SettingsWindowHeight.Value, MinHeight, Math.Max(MinHeight, workArea.Height));
    }

    private void SettingsWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            var width = WindowState == WindowState.Normal ? ActualWidth : RestoreBounds.Width;
            var height = WindowState == WindowState.Normal ? ActualHeight : RestoreBounds.Height;

            if (!double.IsFinite(width) || width <= 0)
                width = Width;
            if (!double.IsFinite(height) || height <= 0)
                height = Height;

            _settings.SettingsWindowWidth = Math.Max(MinWidth, width);
            _settings.SettingsWindowHeight = Math.Max(MinHeight, height);
            _settingsService.Save(_settings);

            App.Log($"Settings window size saved: {_settings.SettingsWindowWidth:0}x{_settings.SettingsWindowHeight:0}.");
        }
        catch (Exception ex)
        {
            App.Log("Failed to save settings window size.", ex);
        }
    }

    private bool TryReadForm(out string serverUrl, out string username, out string password, out int refreshSeconds)
    {
        serverUrl = ServerUrlBox.Text.Trim().TrimEnd('/');
        username = UsernameBox.Text.Trim();
        password = PasswordBox.Password;
        refreshSeconds = 5;

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ThemedMessageBox.Show(this, "Введите корректный адрес вида http://10.20.0.1 или https://vpn.example.com.",
                "AWG Dash Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            ThemedMessageBox.Show(this, "Логин и пароль не должны быть пустыми.",
                "AWG Dash Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!int.TryParse(RefreshSecondsBox.Text, out refreshSeconds) || refreshSeconds is < 2 or > 300)
        {
            ThemedMessageBox.Show(this, "Интервал обновления должен быть от 2 до 300 секунд.",
                "AWG Dash Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var anyColumn = ShowStatusColumnCheckBox.IsChecked == true ||
                        ShowComputerColumnCheckBox.IsChecked == true ||
                        ShowVpnIpColumnCheckBox.IsChecked == true ||
                        ShowHandshakeColumnCheckBox.IsChecked == true ||
                        ShowTrafficColumnCheckBox.IsChecked == true ||
                        ShowGroupsColumnCheckBox.IsChecked == true;

        if (!anyColumn)
        {
            ThemedMessageBox.Show(this, "Оставьте включенной хотя бы одну колонку таблицы.",
                "AWG Dash Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadForm(out var serverUrl, out var username, out var password, out _))
            return;

        TestStatus.Text = "Проверка соединения...";

        try
        {
            using var api = new ApiClient(serverUrl, username, password);
            var summary = await api.GetSummaryAsync();
            TestStatus.Text = $"Соединение успешно. {summary.Configuration}: Online {summary.Online}/{summary.Total}, групп {summary.Groups}.";
        }
        catch (Exception ex)
        {
            TestStatus.Text = $"Ошибка: {ex.Message}";
        }
    }

    private void RadminBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите Radmin Viewer",
            Filter = "Radmin Viewer (Radmin.exe)|Radmin.exe|Исполняемые файлы (*.exe)|*.exe|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(RadminPathBox.Text) && File.Exists(RadminPathBox.Text))
            dialog.InitialDirectory = Path.GetDirectoryName(RadminPathBox.Text);

        if (dialog.ShowDialog(this) == true)
            RadminPathBox.Text = dialog.FileName;
    }

    private ClientSettings? _pendingImport;
    private List<CustomAction>? _customActions;

    private void AwgManage_Click(object sender, RoutedEventArgs e)
    {
        OwnedWindowBehavior.Attach(new AwgStatusWindow(), this).ShowDialog();
    }
    private async void AwgPreview_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Профиль AmneziaWG (*.conf)|*.conf", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 256 * 1024) throw new ArgumentException("Профиль превышает 256 КБ.");
            var preview = AwgConfigPreview.Parse(await File.ReadAllTextAsync(dialog.FileName));
            OwnedWindowBehavior.Attach(new AwgPreviewWindow(preview), this).ShowDialog();
        }
        catch (ArgumentException ex) { ThemedMessageBox.Show(this, ex.Message, "Профиль AWG", MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch { ThemedMessageBox.Show(this, "Не удалось прочитать профиль AWG.", "Профиль AWG", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Настройки AWG Dash Monitor (*.json)|*.json", FileName = "VpnMonitor-settings.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, SettingsTransfer.Export(_settings));
            TestStatus.Text = "Экспортированы сохранённые настройки без пароля. Изменения формы сначала сохраните.";
        }
        catch (Exception ex) { ThemedMessageBox.Show(this, ex.Message, "Экспорт настроек", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Настройки AWG Dash Monitor (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 2_000_000) throw new ArgumentException("Файл настроек слишком большой.");
            var imported = SettingsTransfer.Import(File.ReadAllText(dialog.FileName));
            var message = $"Сервер: {imported.ServerUrl}\nПользователь: {imported.Username}\nТема: {imported.Theme}\nДействий: {imported.CustomActions.Count}\n\nНастройки заменят значения формы. Пароль не импортируется. Команды не запускаются.\nИзменения применятся после «Сохранить»; «Отмена» отменит импорт.";
            if (ThemedMessageBox.Show(this, message, "Предпросмотр импорта", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            var sameAccount = string.Equals(imported.ServerUrl.TrimEnd('/'), _settings.ServerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
                imported.Username == _settings.Username;
            imported.ProtectedPassword = sameAccount ? _settings.ProtectedPassword : "";
            _pendingImport = imported;
            _customActions = imported.CustomActions;
            _builtinActions = imported.BuiltinActions;
            LoadForm(imported);
            SettingsNavigation.SelectedIndex = 0;
            SectionScroll.ScrollToTop();
            TestStatus.Text = "Импорт подготовлен. Проверьте значения и пароль, затем нажмите «Сохранить».";
        }
        catch (Exception ex) { ThemedMessageBox.Show(this, ex.Message, "Импорт настроек", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void CustomActions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = OwnedWindowBehavior.Attach(new CustomActionsWindow(_customActions ?? _settings.CustomActions ?? [],
            _builtinActions ?? _pendingImport?.BuiltinActions ?? _settings.BuiltinActions), this);
        if (dialog.ShowDialog() == true)
        {
            _customActions = dialog.Actions.ToList();
            _builtinActions = dialog.BuiltinActions;
        }
    }
    private List<BuiltinActionOptions>? _builtinActions;
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadForm(out var serverUrl, out var username, out var password, out var refreshSeconds))
            return;

        if (!int.TryParse(RadminPortBox.Text, out var radminPort) || radminPort is < 1 or > 65535)
        {
            ThemedMessageBox.Show(this, "Порт Radmin Server должен быть от 1 до 65535.",
                "AWG Dash Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var radminUsername = RadminUsernameBox.Text.Trim();
        if (RadminAutofillUsernameCheckBox.IsChecked == true && string.IsNullOrWhiteSpace(radminUsername))
        {
            ThemedMessageBox.Show(this, "Укажите логин Radmin или отключите автоматическую подстановку логина.",
                "AWG Dash Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(ActiveCheckSecondsBox.Text, out var activeSeconds) || activeSeconds is < 2 or > 300 ||
            !int.TryParse(ActiveCheckTcpPortBox.Text, out var activePort) || activePort is < 1 or > 65535)
        {
            ThemedMessageBox.Show(this, "Укажите интервал проверки от 2 до 300 секунд и TCP-порт от 1 до 65535.",
                "AWG Dash Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (StatusNotificationsEnabledCheckBox.IsChecked == true && NotificationSourceBox.SelectedIndex == 0 && ActiveChecksEnabledBox.IsChecked != true)
        {
            ThemedMessageBox.Show(this, "Для активных уведомлений включите проверки доступности или выберите источник «Статус AWG».",
                "AWG Dash Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_pendingImport is not null)
        {
            try
            {
                Directory.CreateDirectory(_settingsService.SettingsDirectory);
                var backup = Path.Combine(_settingsService.SettingsDirectory, $"settings-backup-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
                File.WriteAllText(backup, System.Text.Json.JsonSerializer.Serialize(_settings));
                SettingsTransfer.Copy(_pendingImport, _settings);
                App.Log($"Settings backup created before import: {backup}");
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show(this, $"Импорт не применён: {ex.Message}", "AWG Dash Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        if (_customActions is not null) _settings.CustomActions = _customActions;
        if (_builtinActions is not null) _settings.BuiltinActions = _builtinActions;
        _settings.StatusNotificationSource = NotificationSourceBox.SelectedIndex == 0 ? "active" : "awg";
        _settings.ActiveChecksEnabled = ActiveChecksEnabledBox.IsChecked == true;
        _settings.SavePeerHistory = SavePeerHistoryBox.IsChecked == true;
        _settings.ActiveChecksWatchedOnly = ActiveChecksWatchedOnlyBox.IsChecked == true;
        _settings.ActiveCheckSeconds = activeSeconds;
        _settings.ActiveCheckTcpFallback = ActiveCheckTcpFallbackBox.IsChecked == true;
        _settings.ActiveCheckTcpPort = activePort;
        _settings.ServerUrl = serverUrl;
        _settings.Username = username;
        _settings.ProtectedPassword = _settingsService.ProtectPassword(password);
        _settings.RefreshSeconds = refreshSeconds;
        _settings.StartWithWindows = AutostartCheckBox.IsChecked == true;
        _settings.StartMinimized = StartMinimizedCheckBox.IsChecked == true;
        _settings.StatusNotificationsEnabled = StatusNotificationsEnabledCheckBox.IsChecked == true;
        _settings.NotifyWhenOnline = NotifyWhenOnlineCheckBox.IsChecked == true;
        _settings.NotifyWhenOffline = NotifyWhenOfflineCheckBox.IsChecked == true;
        _settings.Theme = ThemeComboBox.SelectedItem is ComboBoxItem { Tag: string theme }
            ? ThemeService.Normalize(theme)
            : ThemeService.Light;
        _settings.RadminViewerPath = RadminPathBox.Text.Trim();
        _settings.RadminPort = radminPort;
        _settings.RadminUsername = radminUsername;
        _settings.RadminAutofillUsername = RadminAutofillUsernameCheckBox.IsChecked == true;

        _settings.ShowStatusColumn = ShowStatusColumnCheckBox.IsChecked == true;
        _settings.ShowComputerColumn = ShowComputerColumnCheckBox.IsChecked == true;
        _settings.ShowVpnIpColumn = ShowVpnIpColumnCheckBox.IsChecked == true;
        _settings.ShowHandshakeColumn = ShowHandshakeColumnCheckBox.IsChecked == true;
        _settings.ShowTrafficColumn = ShowTrafficColumnCheckBox.IsChecked == true;
        _settings.ShowGroupsColumn = ShowGroupsColumnCheckBox.IsChecked == true;

        try
        {
            _autostartService.SetEnabled(_settings.StartWithWindows);
            _settingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(this,
                $"Не удалось сохранить настройки.\n\n{ex.Message}",
                "AWG Dash Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
