using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VpnMonitor.Client;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;
using VpnMonitor.Client.ViewModels;

internal static class InterfaceChecks
{
    public static async Task RunAsync()
    {
        if (System.Windows.Application.Current is null) { var app = new App(false); app.InitializeComponent(); }
        System.Windows.Application.Current!.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var service = new SettingsService(); // Process-wide test directory was configured before WPF startup.
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ThemeService.Apply(theme);
            var settings = new SettingsWindow(service, new ClientSettings { Theme = theme });
            settings.ShowInTaskbar = false;
            settings.ShowActivated = false;
            settings.Left = SystemParameters.VirtualScreenLeft - 3000;
            settings.Show();
            try
            {
                await Layout();
                var navigation = (ListBox)settings.FindName("SettingsNavigation");
                var scroll = (ScrollViewer)settings.FindName("SectionScroll");
                var sections = (StackPanel)settings.FindName("SettingsSections");
                Require(Enumerable.Range(0, 7).All(i => ((FrameworkElement)settings.FindName("SettingsPage" + i)).IsVisible), "All settings sections must remain in a single visible ribbon.");
                var username = (TextBox)settings.FindName("UsernameBox");
                username.Text = "unsaved-test-value";
                navigation.SelectedIndex = 4;
                await Layout();
                var page4 = (FrameworkElement)settings.FindName("SettingsPage4");
                Require(Math.Abs(scroll.VerticalOffset - page4.TranslatePoint(new Point(), sections).Y) < 3 && navigation.SelectedIndex == 4,
                    "Clicking a navigation item must scroll to its section without feedback jumps.");
                var page2 = (FrameworkElement)settings.FindName("SettingsPage2");
                scroll.ScrollToVerticalOffset(page2.TranslatePoint(new Point(), sections).Y + 50);
                await Layout();
                Require(navigation.SelectedIndex == 2, "Manual scrolling must track the current settings section.");
                navigation.SelectedIndex = 6;
                await Layout();
                Require(navigation.SelectedIndex == 6, "The final short settings section must be reachable and selected.");
                settings.Width = 780; settings.Height = 500;
                await Layout();
                Require(username.Text == "unsaved-test-value" && navigation.SelectedIndex >= 0, "Resize/navigation must preserve unsaved fields and an active section.");
                ((TextBox)settings.FindName("SettingsSearchBox")).Text = "Резервный TCP-порт";
                var results = (ListBox)settings.FindName("SettingsSearchResults");
                Require(results.Items.Count > 0, "Settings search must cover the continuous ribbon.");
                results.SelectedIndex = 0;
                await Layout();
                Require(navigation.SelectedIndex == 4, "Search must scroll to the target and synchronize the sidebar.");
                ((TextBox)settings.FindName("SettingsSearchBox")).Text = "Светлая";
                results.SelectedIndex = 0;
                await Layout();
                Require(navigation.SelectedIndex == 1, "Searching a closed dropdown item must navigate safely to its containing section.");
                ((TextBox)settings.FindName("SettingsSearchBox")).Text = "";
                settings.Width = 940; settings.Height = 740;
                navigation.SelectedIndex = 0;
                await Layout();
                Render(settings.Content as FrameworkElement ?? throw new Exception("Settings content missing"), "settings-" + theme);
                navigation.SelectedIndex = 3;
                await Layout();
                Render((FrameworkElement)settings.Content, "settings-notifications-" + theme);
                var input = (TextBox)settings.FindName("ServerUrlBox"); input.ApplyTemplate();
                Require(input.Template.FindName("PART_ContentHost", input) is ScrollViewer, "Themed text input must retain its editable content host.");
                var password = (PasswordBox)settings.FindName("PasswordBox"); password.Password = "test-only"; password.ApplyTemplate();
                Require(password.Template.FindName("PART_ContentHost", password) is ScrollViewer && password.Password == "test-only", "Themed password input must retain its content host and value.");
            }
            finally { settings.Close(); }

            var main = new MainWindow(service, new PeerHistoryArchive(Path.Combine(service.SettingsDirectory, "ui-history")));
            try
            {
                ThemeService.Apply(theme);
                var model = (MainViewModel)main.DataContext;
                model.Peers.Add(new Peer { Id = "sample-a", Name = "Office_Workstation", VpnIp = "10.20.0.25", Groups = ["Office"], Status = "running" });
                model.Peers.Add(new Peer { Id = "sample-b", Name = "Office_Server", VpnIp = "10.20.0.26", Groups = ["Office"], Status = "stopped" });
                typeof(MainWindow).GetMethod("ApplyColumnPreset", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [false]);
                var content = (FrameworkElement)main.Content;
                content.Measure(new Size(1160, 660)); content.Arrange(new Rect(0, 0, 1160, 660)); content.UpdateLayout();
                Render(content, "main-" + theme); // Never Show(): no working-client startup or network timers.
                content.Measure(new Size(700, 440)); content.Arrange(new Rect(0, 0, 700, 440)); content.UpdateLayout();
                Render(content, "main-narrow-" + theme);
                var more = (Button)main.FindName("MoreButton");
                Require(more.ActualWidth > 30 && more.TransformToAncestor(content).Transform(new Point(more.ActualWidth, 0)).X <= content.ActualWidth,
                    "Main header actions must fit the minimum window width.");
            }
            finally { main.Close(); }
            var actionsWindow = new CustomActionsWindow([new() { Name = "My tool", Group = "Подключение", Executable = @"C:\tools\example.exe" }]);
            try
            {
                var content = (FrameworkElement)actionsWindow.Content;
                content.Measure(new Size(920, 620)); content.Arrange(new Rect(0, 0, 920, 620)); content.UpdateLayout();
                Render(content, "actions-" + theme);
            }
            finally { actionsWindow.Close(); }
        }
        CheckActions();
        Console.WriteLine("PASS: continuous settings, scroll tracking, navigation/search/resize, unsaved fields, themed input hosts, narrow header and unified action catalog.");
    }

    private static void CheckActions()
    {
        var defaults = ClientActionCatalog.Resolve([]);
        Require(defaults.Count == 13 && defaults.Select(a => a.Id).Distinct().Count() == 13 && defaults.All(a => a.Enabled), "Old settings must receive every built-in action once.");
        var source = new ClientSettings
        {
            BuiltinActions = [new() { Id = "ping", Name = "ICMP", Group = "Tools", Enabled = false }, new() { Id = "rdp", Name = "Remote", Group = "Tools" }],
            CustomActions = [new() { Name = "Custom", Group = "tools", Executable = @"C:\tools\example.exe" }]
        };
        var invoked = 0;
        var menu = ClientActionMenu.Create(source, a => new MenuItem { Header = a.Name }, _ => invoked++, false);
        var tools = menu.Items.Cast<MenuItem>().Single(a => (string)a.Header == "Tools");
        Require(tools.Items.Count == 2 && (string)((MenuItem)tools.Items[0]).Header == "Remote" && !((MenuItem)tools.Items[1]).IsEnabled,
            "Built-in and custom commands must share a group; hidden commands stay absent and IP actions disable without a target.");
        menu = ClientActionMenu.Create(source, a => new MenuItem { Header = a.Name }, _ => invoked++, true);
        tools = menu.Items.Cast<MenuItem>().Single(a => (string)a.Header == "Tools");
        ((MenuItem)tools.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(invoked == 1, "A grouped custom command must execute exactly once.");
        var copy = SettingsTransfer.Import(SettingsTransfer.Export(source));
        Require(copy.BuiltinActions[0].Id == "ping" && !copy.BuiltinActions[0].Enabled, "Action visibility/order/names/groups must survive settings transfer.");
        var disabledCustom = JsonSerializer.Deserialize<CustomAction>("{\"Enabled\":false}")!;
        Require(!disabledCustom.Enabled && JsonSerializer.Deserialize<CustomAction>("{}")!.Enabled, "Existing custom commands remain enabled by default.");
        var editor = new CustomActionsWindow(source.CustomActions, source.BuiltinActions);
        var list = (ListBox)editor.FindName("ActionsList");
        var first = (CustomAction)list.Items[0];
        Require(first.BuiltinId == "ping" && ((StackPanel)editor.FindName("ProgramEditor")).Visibility == Visibility.Collapsed,
            "Built-ins must not expose executable arguments.");
        ((TextBox)editor.FindName("NameBox")).Text = "Changed only in editor";
        list.SelectedIndex = 1; // Commits only to the private editor snapshot.
        Require(editor.BuiltinActions[0].Name == "Changed only in editor" && source.BuiltinActions[0].Name == "ICMP", "Cancelling the editor must preserve original built-ins.");
        editor.Close();
        var saveEditor = new CustomActionsWindow(source.CustomActions, source.BuiltinActions)
        { ShowInTaskbar = false, Left = SystemParameters.VirtualScreenLeft - 3000, Top = SystemParameters.VirtualScreenTop };
        saveEditor.Dispatcher.BeginInvoke(new Action(() =>
        {
            var items = (ListBox)saveEditor.FindName("ActionsList");
            items.SelectedItem = saveEditor.Actions[0];
            ((TextBox)saveEditor.FindName("GroupBox")).Text = "New group";
            ((CheckBox)saveEditor.FindName("EnabledBox")).IsChecked = false;
            typeof(CustomActionsWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(saveEditor, [saveEditor, new RoutedEventArgs()]);
        }));
        Require(saveEditor.ShowDialog() == true && !saveEditor.Actions[0].Enabled && saveEditor.Actions[0].Group == "New group" &&
            source.CustomActions[0].Enabled && source.CustomActions[0].Group == "tools", "Saving must return edited commands while preserving the caller's snapshot until settings are applied.");
        foreach (var invalid in new[] { new BuiltinActionOptions { Id = "unknown", Name = "x" }, new BuiltinActionOptions { Id = "rdp", Name = "x\n" } })
        {
            try { ClientActionCatalog.Validate([invalid]); throw new Exception("Invalid built-in accepted."); }
            catch (ArgumentException) { }
        }
        var allHidden = new ClientSettings { BuiltinActions = defaults.Select(a => new BuiltinActionOptions { Id = a.Id, Name = a.Name, Group = a.Group, Enabled = false }).ToList() };
        Require(ClientActionMenu.Create(allHidden, a => new MenuItem(), _ => { }, true).Items.Count == 1, "An empty menu must explain where actions were hidden.");
    }
    private static async Task Layout() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static void Render(FrameworkElement element, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "regression", "ui"); Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
