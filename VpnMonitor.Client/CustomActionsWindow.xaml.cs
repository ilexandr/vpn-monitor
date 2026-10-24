using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;
namespace VpnMonitor.Client;
public partial class CustomActionsWindow : Window
{
    public ObservableCollection<CustomAction> Actions { get; }
    private CustomAction? _editing;
    public CustomActionsWindow(IEnumerable<CustomAction> actions)
    {
        InitializeComponent();
        Actions = new(actions.Select(a => new CustomAction { Name = a.Name, Group = a.Group, Executable = a.Executable, Arguments = a.Arguments?.ToList() ?? [] }));
        ActionsList.ItemsSource = Actions;
        ActionsList.SelectedIndex = Actions.Count > 0 ? 0 : -1;
        Editor.IsEnabled = _editing is not null;
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this);
    }
    private void Commit()
    {
        if (_editing is null) return;
        _editing.Name = NameBox.Text.Trim();
        _editing.Group = GroupBox.Text.Trim();
        _editing.Executable = ExecutableBox.Text.Trim();
        _editing.Arguments = ArgumentsBox.Text.Replace("\r", "").Split('\n').Where(s => s.Length > 0).ToList();
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        Commit();
        _editing = ActionsList.SelectedItem as CustomAction;
        Editor.IsEnabled = _editing is not null;
        NameBox.Text = _editing?.Name ?? "";
        GroupChoices.ItemsSource = Actions.Select(a => a.Group?.Trim() ?? "").Where(g => g.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(g => g).ToArray();
        GroupChoices.SelectedIndex = -1;
        GroupBox.Text = _editing?.Group ?? "";
        ExecutableBox.Text = _editing?.Executable ?? "";
        ArgumentsBox.Text = string.Join(Environment.NewLine, _editing?.Arguments ?? []);
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
        ActionsList.SelectedItem = action;
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ActionsList.SelectedItem is CustomAction action) Actions.Remove(action);
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
            foreach (var action in Actions) CustomActionService.Validate(action);
            DialogResult = true;
        }
        catch (ArgumentException ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "VPN Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}