using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;
using Button = System.Windows.Controls.Button;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using MessageBox = System.Windows.MessageBox;

namespace VpnMonitor.Client;

public sealed class MonitoringContext
{
    public required Func<MonitoringSettings> Read { get; init; }
    public required Action<MonitoringSettings> Save { get; init; }
    public required Func<bool> Authorized { get; init; }
    public required Func<Peer[]> Peers { get; init; }
    public required Func<string[]> Groups { get; init; }
    public required IncidentTracker Incidents { get; init; }
    public required NotificationPolicy Policy { get; init; }
    public required Action ClearCompleted { get; init; }
    public required Func<SavedClientView[]> Views { get; init; }
    public required Action<string> SaveView { get; init; }
    public required Func<SavedClientView, bool> ApplyView { get; init; }
    public required Action<SavedClientView> DeleteView { get; init; }
    public int RadminPort { get; init; } = 4899;
}

public partial class MonitoringWindow : Window
{
    private sealed record Choice(string Name, string Value);
    private sealed record MaintenanceRow(GroupMaintenance Item)
    { public string Text => $"{Item.Group} · до {Item.Until.LocalDateTime:dd.MM HH:mm}"; }
    private readonly MonitoringContext _context;
    private readonly MonitoringSettings _draft;
    private readonly ICollectionView _incidents;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private ServiceProfile? _editing;
    private bool _loading = true;
    public MonitoringWindow(MonitoringContext context)
    {
        _context = context;
        _draft = JsonSerializer.Deserialize<MonitoringSettings>(JsonSerializer.Serialize(context.Read()))!;
        InitializeComponent();
        _incidents = CollectionViewSource.GetDefaultView(context.Incidents.Entries);
        _incidents.Filter = IncidentVisible; IncidentsGrid.ItemsSource = _incidents;
        DecisionsList.ItemsSource = context.Policy.Decisions;
        ServicesEnabledBox.IsChecked = _draft.ServiceChecksEnabled; ParallelBox.Text = _draft.ParallelChecks.ToString();
        ServiceNotificationsBox.IsChecked = _draft.ServiceNotifications;
        DelayBox.Text = _draft.Notifications.OfflineDelaySeconds.ToString(); AggregateBox.Text = _draft.Notifications.AggregateThreshold.ToString();
        QuietBox.IsChecked = _draft.Notifications.QuietHoursEnabled;
        QuietStartBox.Text = FormatTime(_draft.Notifications.QuietStartMinute); QuietEndBox.Text = FormatTime(_draft.Notifications.QuietEndMinute);
        MaintenanceGroupBox.ItemsSource = context.Groups();
        RefreshMaintenance(); RefreshProfiles(); RefreshViews();
        _loading = false; Navigation.SelectedIndex = 0;
        if (ProfilesList.Items.Count > 0) ProfilesList.SelectedIndex = 0;
        else ProfileEditor.IsEnabled = false;
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this);
        Loaded += (_, _) => { RefreshState(); _timer.Start(); };
        _timer.Tick += (_, _) => RefreshState();
        Closed += (_, _) => { _timer.Stop(); _incidents.Filter = null; };
        StatusText.Text = "Изменения профилей и правил применяются общей кнопкой. Представления сохраняются отдельно.";
    }
    private void RefreshState()
    {
        _incidents.Refresh();
        var all = _context.Incidents.Entries;
        var duration = TimeSpan.FromTicks(_incidents.Cast<Incident>().Sum(i => i.Duration.Ticks));
        IncidentSummary.Text = $"Открыто: {all.Count(i => i.EndedAt is null)} · Завершено: {all.Count(i => i.EndedAt is not null)} · Показано: {IncidentsGrid.Items.Count} · Длительность видимых событий: {(int)duration.TotalDays} д. {duration:hh\\:mm\\:ss}";
        ApplyButton.IsEnabled = _context.Authorized();
    }
    private bool IncidentVisible(object item)
    {
        if (item is not Incident incident || OpenOnlyBox.IsChecked == true && incident.EndedAt is not null) return false;
        var text = incident.Title + " " + incident.Level + " " + incident.Result;
        return IncidentSearchBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(w => text.Contains(w, StringComparison.CurrentCultureIgnoreCase));
    }
    private void IncidentSearch_Changed(object sender, TextChangedEventArgs e) { if (!_loading) RefreshState(); }
    private void IncidentFilter_Changed(object sender, RoutedEventArgs e) { if (!_loading) RefreshState(); }
    private void Navigation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !ReferenceEquals(e.Source, Navigation)) return;
        FrameworkElement[] pages = [IncidentsPage, ProfilesPage, NotificationsPage, ViewsPage];
        for (var i = 0; i < pages.Length; i++) pages[i].Visibility = i == Navigation.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
    }
    private bool ProfileVisible(ServiceProfile p) => p.TargetKind == "all" || p.TargetKind == "group" && _context.Groups().Contains(p.Target, StringComparer.OrdinalIgnoreCase) ||
        p.TargetKind == "peer" && _context.Peers().Any(peer => peer.Id == p.Target);
    private void RefreshProfiles() => ProfilesList.ItemsSource = _draft.Profiles.Where(ProfileVisible).ToArray();
    private bool CommitProfile()
    {
        if (_editing is null) return true;
        try
        {
            if (!int.TryParse(ProfileIntervalBox.Text, out var interval)) throw new ArgumentException("Укажите интервал профиля числом.");
            var kind = (TargetKindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
            var next = new ServiceProfile
            {
                Id = _editing.Id, Name = ProfileNameBox.Text.Trim(), Enabled = ProfileEnabledBox.IsChecked == true,
                TargetKind = kind, Target = kind == "all" ? "" : TargetBox.SelectedValue as string ?? "",
                IntervalSeconds = interval, Ports = ParsePorts(PortsBox.Text)
            };
            MonitoringConfiguration.Validate(new() { Profiles = [next] });
            _editing.Name = next.Name; _editing.Enabled = next.Enabled; _editing.TargetKind = next.TargetKind;
            _editing.Target = next.Target; _editing.IntervalSeconds = next.IntervalSeconds; _editing.Ports = next.Ports;
            ProfilesList.Items.Refresh();
            return true;
        }
        catch (ArgumentException ex) { StatusText.Text = ex.Message; return false; }
    }
    public static List<ServicePort> ParsePorts(string text)
    {
        if (text.Length > 4096) throw new ArgumentException("Слишком длинный список портов.");
        var ports = new List<ServicePort>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.LastIndexOf(':'); var number = colon >= 0 ? line[(colon + 1)..] : line;
            if (!int.TryParse(number, out var port)) throw new ArgumentException("Сервис укажите как название:порт, например RDP:3389.");
            ports.Add(new(colon >= 0 ? line[..colon].Trim() : "TCP " + port, port));
        }
        return ports;
    }
    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !ReferenceEquals(e.Source, ProfilesList)) return;
        var selection = ProfilesList.SelectedItem as ServiceProfile;
        if (!CommitProfile())
        { _loading = true; ProfilesList.SelectedItem = _editing; _loading = false; return; }
        _editing = selection;
        _loading = true;
        ProfileEditor.IsEnabled = selection is not null;
        ProfileNameBox.Text = selection?.Name ?? "";
        ProfileEnabledBox.IsChecked = selection?.Enabled ?? false;
        TargetKindBox.SelectedIndex = selection?.TargetKind switch { "group" => 1, "peer" => 2, _ => 0 };
        LoadTargets(selection?.Target);
        ProfileIntervalBox.Text = (selection?.IntervalSeconds ?? 30).ToString();
        PortsBox.Text = string.Join(Environment.NewLine, selection?.Ports.Select(p => p.Name + ":" + p.Port) ?? []);
        _loading = false;
    }
    private void LoadTargets(string? current = null)
    {
        var kind = (TargetKindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        TargetBox.ItemsSource = kind switch
        {
            "group" => _context.Groups().Select(g => new Choice(g, g)).ToArray(),
            "peer" => _context.Peers().Select(p => new Choice(p.Name, p.Id)).OrderBy(p => p.Name).ToArray(),
            _ => []
        };
        TargetBox.IsEnabled = kind != "all";
        TargetBox.SelectedValue = current; if (TargetBox.SelectedIndex < 0 && TargetBox.Items.Count > 0) TargetBox.SelectedIndex = 0;
    }
    private void TargetKind_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading) LoadTargets(); }
    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitProfile()) return;
        var profile = new ServiceProfile { Ports = [new("RDP", 3389)] }; _draft.Profiles.Add(profile);
        _editing = null; RefreshProfiles(); ProfilesList.SelectedItem = profile;
    }
    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null) return;
        _draft.Profiles.Remove(_editing); _editing = null; RefreshProfiles();
        ProfilesList.SelectedIndex = ProfilesList.Items.Count > 0 ? 0 : -1; ProfileEditor.IsEnabled = _editing is not null;
    }
    private void MoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null || !CommitProfile() || sender is not Button { Tag: string direction }) return;
        var selected = _editing; var index = _draft.Profiles.IndexOf(selected); var delta = int.Parse(direction);
        for (var next = index + delta; next >= 0 && next < _draft.Profiles.Count; next += delta)
            if (ProfileVisible(_draft.Profiles[next]))
            {
                _draft.Profiles.RemoveAt(index); _draft.Profiles.Insert(next, selected);
                _editing = null; RefreshProfiles(); ProfilesList.SelectedItem = selected; break;
            }
    }
    private void AddPort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string port })
            PortsBox.Text = (PortsBox.Text.TrimEnd() + Environment.NewLine + (port == "Radmin" ? "Radmin:" + _context.RadminPort : port)).Trim();
    }
    private static string FormatTime(int minute) => $"{minute / 60:00}:{minute % 60:00}";
    private static int ParseTime(string text)
    {
        if (!TimeOnly.TryParseExact(text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)) throw new ArgumentException("Время укажите в формате ЧЧ:ММ.");
        return value.Hour * 60 + value.Minute;
    }
    private void RefreshMaintenance() => MaintenanceList.ItemsSource = _draft.Notifications.Maintenance
        .Where(m => _context.Groups().Contains(m.Group)).Select(m => new MaintenanceRow(m)).ToArray();
    private void AddMaintenance_Click(object sender, RoutedEventArgs e)
    {
        if (MaintenanceGroupBox.SelectedItem is not string group || !int.TryParse(MaintenanceMinutesBox.Text, out var minutes) || minutes is < 1 or > 10080)
        { StatusText.Text = "Выберите группу и срок обслуживания 1–10080 минут."; return; }
        _draft.Notifications.Maintenance.RemoveAll(m => m.Group == group);
        _draft.Notifications.Maintenance.Add(new(group, DateTimeOffset.Now.AddMinutes(minutes))); RefreshMaintenance();
        StatusText.Text = "Обслуживание подготовлено. Нажмите «Применить профили и правила».";
    }
    private void RemoveMaintenance_Click(object sender, RoutedEventArgs e)
    { if (MaintenanceList.SelectedItem is MaintenanceRow row) { _draft.Notifications.Maintenance.Remove(row.Item); RefreshMaintenance(); } }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_context.Authorized()) throw new ArgumentException("Сначала дождитесь актуальных данных сервера.");
            if (!CommitProfile()) return;
            if (!int.TryParse(ParallelBox.Text, out var parallel) || !int.TryParse(DelayBox.Text, out var delay) || !int.TryParse(AggregateBox.Text, out var aggregate))
                throw new ArgumentException("Параллельность, задержку и порог объединения укажите числами.");
            _draft.ServiceChecksEnabled = ServicesEnabledBox.IsChecked == true; _draft.ParallelChecks = parallel;
            _draft.ServiceNotifications = ServiceNotificationsBox.IsChecked == true;
            var n = _draft.Notifications; n.OfflineDelaySeconds = delay; n.AggregateThreshold = aggregate;
            n.QuietHoursEnabled = QuietBox.IsChecked == true; n.QuietStartMinute = ParseTime(QuietStartBox.Text); n.QuietEndMinute = ParseTime(QuietEndBox.Text);
            n.Maintenance.RemoveAll(m => m.Until <= DateTimeOffset.Now);
            MonitoringConfiguration.Validate(_draft);
            _context.Save(JsonSerializer.Deserialize<MonitoringSettings>(JsonSerializer.Serialize(_draft))!);
            var current = _editing?.Id; _editing = null; RefreshProfiles(); ProfilesList.SelectedItem = _draft.Profiles.FirstOrDefault(p => p.Id == current);
            StatusText.Text = "Профили и правила сохранены. Общий туннель не перезапускается.";
        }
        catch (Exception ex) { StatusText.Text = "Не сохранено: " + ex.Message; }
    }
    private void RefreshViews() => ViewsList.ItemsSource = _context.Views();
    private void SaveView_Click(object sender, RoutedEventArgs e)
    {
        try { _context.SaveView(ViewNameBox.Text); RefreshViews(); StatusText.Text = "Текущее представление сохранено."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void ApplyView_Click(object sender, RoutedEventArgs e)
    { if (ViewsList.SelectedItem is SavedClientView view) StatusText.Text = _context.ApplyView(view) ? "Представление применено в основном окне." : "Представление недоступно: проверьте соединение и права."; }
    private void DeleteView_Click(object sender, RoutedEventArgs e)
    {
        if (ViewsList.SelectedItem is not SavedClientView view) return;
        try { _context.DeleteView(view); RefreshViews(); StatusText.Text = "Представление удалено."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void ExportIncidents_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "VpnMonitor-incidents.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, IncidentExport.Create(_incidents.Cast<Incident>()), new UTF8Encoding(true)); StatusText.Text = "Видимые инциденты экспортированы."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void ClearIncidents_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Очистить завершённые инциденты текущего аккаунта? Открытые останутся.", "Мониторинг", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _context.ClearCompleted(); RefreshState();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
