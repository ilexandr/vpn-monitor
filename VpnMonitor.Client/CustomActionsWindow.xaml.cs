using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;
namespace VpnMonitor.Client;
public partial class CustomActionsWindow : Window
{
    public ObservableCollection<CustomAction> Actions { get; }
    private readonly ObservableCollection<CustomAction> _rows = [];
    public List<BuiltinActionOptions> BuiltinActions => _rows.Where(a => a.BuiltinId is not null)
        .Select(a => new BuiltinActionOptions { Id = a.BuiltinId!, Name = a.Name, Group = a.Group, Enabled = a.Enabled }).ToList();
    private CustomAction? _editing;
    public CustomActionsWindow(IEnumerable<CustomAction> actions, IEnumerable<BuiltinActionOptions>? builtinActions = null)
    {
        InitializeComponent();
        Actions = new(actions.Select(a => new CustomAction { Name = a.Name, Group = a.Group, Enabled = a.Enabled, Executable = a.Executable, Arguments = a.Arguments?.ToList() ?? [] }));
        foreach (var option in ClientActionCatalog.Resolve(builtinActions))
            _rows.Add(new() { BuiltinId = option.Id, Name = option.Name, Group = option.Group, Enabled = option.Enabled });
        foreach (var action in Actions) _rows.Add(action);
        ActionsList.ItemsSource = _rows;
        ActionsList.SelectedIndex = 0;
        Editor.IsEnabled = _editing is not null;
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this);
    }
    private void Commit()
    {
        if (_editing is null) return;
        _editing.Name = NameBox.Text.Trim();
        _editing.Group = GroupBox.Text.Trim();
        _editing.Enabled = EnabledBox.IsChecked == true;
        _editing.Executable = ExecutableBox.Text.Trim();
        _editing.Arguments = ArgumentsBox.Text.Replace("\r", "").Split('\n').Where(s => s.Length > 0).ToList();
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        Commit();
        _editing = ActionsList.SelectedItem as CustomAction;
        Editor.IsEnabled = _editing is not null;
        NameBox.Text = _editing?.Name ?? "";
        GroupChoices.ItemsSource = _rows.Select(a => a.Group?.Trim() ?? "").Where(g => g.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(g => g).ToArray();
        GroupChoices.SelectedIndex = -1;
        GroupBox.Text = _editing?.Group ?? "";
        ExecutableBox.Text = _editing?.Executable ?? "";
        ArgumentsBox.Text = string.Join(Environment.NewLine, _editing?.Arguments ?? []);
        EnabledBox.IsChecked = _editing?.Enabled ?? false;
        ProgramEditor.Visibility = _editing?.BuiltinId is null ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.IsEnabled = _editing is not null && _editing.BuiltinId is null;
        ActionKindText.Text = _editing?.BuiltinId is null
            ? "Собственная команда. Программа запускается только по вашему нажатию."
            : "Встроенная команда. Можно изменить название, группу и видимость; выполнение остаётся внутри приложения.";
    }
    private void GroupChoices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupChoices.SelectedItem is string group) GroupBox.Text = group;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        ActionsList.Items.Refresh();
        var action = new CustomAction { Name = "Новое действие" };
        Actions.Add(action);
        _rows.Add(action);
        ActionsList.SelectedItem = action;
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ActionsList.SelectedItem is CustomAction { BuiltinId: null } action) { _rows.Remove(action); Actions.Remove(action); }
    }
    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Move(int direction)
    {
        Commit();
        if (_editing is null) return;
        var from = _rows.IndexOf(_editing);
        for (var to = from + direction; to >= 0 && to < _rows.Count; to += direction)
            if ((_rows[to].BuiltinId is null) == (_editing.BuiltinId is null))
            {
                _rows.Move(from, to);
                ActionsList.ScrollIntoView(_editing);
                break;
            }
    }
    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        foreach (var row in _rows.Where(a => a.BuiltinId is not null).ToArray()) _rows.Remove(row);
        var index = 0;
        foreach (var option in ClientActionCatalog.Defaults())
            _rows.Insert(index++, new() { BuiltinId = option.Id, Name = option.Name, Group = option.Group, Enabled = true });
        ActionsList.SelectedIndex = 0;
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Программы (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) ExecutableBox.Text = dialog.FileName;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        try
        {
            var custom = _rows.Where(a => a.BuiltinId is null).ToArray();
            foreach (var action in custom) CustomActionService.Validate(action);
            ClientActionCatalog.Validate(BuiltinActions);
            Actions.Clear();
            foreach (var action in custom) Actions.Add(action);
            DialogResult = true;
        }
        catch (ArgumentException ex)
        {
            ThemedMessageBox.Show(this, ex.Message, "AWG Dash Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
