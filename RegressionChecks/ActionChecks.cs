using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using VpnMonitor.Client;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;

internal static class ActionChecks
{
    public static async Task RunAsync()
    {
        if (System.Windows.Application.Current is null)
        {
            var application = new App();
            application.InitializeComponent();
        }
        System.Windows.Application.Current!.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var customAction = new CustomAction
        {
            Name = "Example", Executable = @"C:\Program Files\Example\client.exe",
            Arguments = ["/target:{ip}", "literal with spaces", "{ip}", "& echo literal"]
        };
        var customInfo = CustomActionService.CreateStartInfo(customAction, "10.20.0.25");
        Require(customInfo.ArgumentList.SequenceEqual(new[] { "/target:10.20.0.25", "literal with spaces", "10.20.0.25", "& echo literal" }),
            "Custom actions must substitute IP and preserve argument boundaries.");
        Require(customInfo.FileName == customAction.Executable && customInfo.UseShellExecute, "Custom actions must launch the selected executable directly.");
        Require(CustomActionService.CreateStartInfo(customAction, "2001:db8::1").ArgumentList[2] == "2001:db8::1", "Custom actions must support IPv6.");
        try { CustomActionService.CreateStartInfo(customAction, "10.0.0.1 & bad"); throw new Exception("Hostile target accepted."); }
        catch (ArgumentException) { }
        foreach (var executablePath in new[] { "relative.exe", @"C:\tool.cmd", "" })
        {
            try { CustomActionService.Validate(new CustomAction { Name = "Test", Executable = executablePath }); throw new Exception("Invalid executable accepted."); }
            catch (ArgumentException) { }
        }
        var restored = JsonSerializer.Deserialize<ClientSettings>(JsonSerializer.Serialize(new ClientSettings { CustomActions = [customAction] }))!;
        Require(restored.CustomActions[0].Arguments.SequenceEqual(customAction.Arguments), "Custom actions must round-trip settings.");
        Require(JsonSerializer.Deserialize<ClientSettings>("{}")!.CustomActions.Count == 0, "Old settings must default to no custom actions.");
        var editor = new CustomActionsWindow([customAction]);
        editor.Actions[0].Name = "Edited";
        editor.Actions[0].Arguments.Add("new argument");
        Require(customAction.Name == "Example" && customAction.Arguments.Count == 4, "Editor cancellation must leave original actions untouched.");
        editor.Close();
        Console.WriteLine("PASS: custom action IP substitution, argument boundaries, IPv6, validation, persistence and isolated editing.");
        var invoked = new List<CustomAction>();
        var groupedActions = new[]
        {
            new CustomAction { Name = "One", Group = " Tools " },
            new CustomAction { Name = "Two", Group = "tools" },
            new CustomAction { Name = "Standalone" },
            new CustomAction { Name = "Third", Group = "Other" }
        };
        var grouped = CustomActionMenu.Create(groupedActions, invoked.Add, true);
        Require(grouped.Items.Count == 3, "Groups must merge trimmed names ignoring case while preserving standalone actions.");
        var firstGroup = (MenuItem)grouped.Items[0];
        Require(firstGroup.Items.Count == 2 && (string)firstGroup.Header == "Tools", "Named actions must form a submenu.");
        firstGroup.ApplyTemplate();
        Require(firstGroup.Template.FindName("PART_Popup", firstGroup) is Popup, "Nested action groups must support popups.");
        ((MenuItem)firstGroup.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ((MenuItem)grouped.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(invoked.SequenceEqual(new[] { groupedActions[1], groupedActions[2] }), "Grouped action callbacks must retain their own definitions.");
        Require(!CustomActionMenu.Create(groupedActions, _ => { }, false).IsEnabled, "Invalid IP must disable the complete actions menu.");
        var savedGroup = JsonSerializer.Deserialize<CustomAction>(JsonSerializer.Serialize(groupedActions[0]))!;
        Require(savedGroup.Group == " Tools " && JsonSerializer.Deserialize<CustomAction>("{}")!.Group == "", "Groups must persist and old actions must remain ungrouped.");
        var groupEditor = new CustomActionsWindow(groupedActions);
        Require(groupEditor.Actions[0].Group == " Tools ", "Editor must preserve groups.");
        groupEditor.Actions[0].Group = "Changed";
        Require(groupedActions[0].Group == " Tools ", "Cancelled group edits must not alter settings.");
        groupEditor.Close();

        var history = new PeerHistory(4);
        history.Observe("a", "AWG", "Online");
        history.Observe("a", "AWG", "Online");
        Require(history.Entries.Count == 1 && history.Entries[0].Previous == "—", "History must record initial state once.");
        history.Observe("a", "AWG", "Неизвестно");
        history.Observe("a", "AWG", "Online");
        Require(history.Entries.Count == 3 && history.Entries.All(e => e.Current != "Offline"), "Connection loss must produce unknown rather than offline.");
        history.Observe("a", "Endpoint", "10.1.1.1:123");
        history.Observe("a", "Endpoint", "10.1.1.1:124");
        Require(history.Entries.Count == 4 && history.Entries[0].Previous == "10.1.1.1:123", "History must record endpoint changes and cap retained entries.");
        history.Observe("b", "AWG", "Offline");
        history.RetainPeers(["b"]);
        Require(history.Entries.Count == 1 && history.Entries[0].PeerId == "b", "Removed or inaccessible peers must be purged from history.");
        history.Reset();
        history.Observe("b", "AWG", "Offline");
        Require(history.Entries.Count == 1 && history.Entries[0].Previous == "—", "Changing account must reset history baselines.");
        var historyWindow = new PeerHistoryWindow("Example", history, "b");
        var historySearch = (TextBox)historyWindow.FindName("SearchBox");
        var filteredHistory = (DataGrid)historyWindow.FindName("HistoryGrid");
        historySearch.Text = "AWG online";
        Require(filteredHistory.Items.Count == 0, "History search must apply AND matching in the actual window.");
        history.Observe("b", "AWG", "Online");
        Require(filteredHistory.Items.Count == 1, "Matching events must appear live while history search is active.");
        history.Observe("other", "AWG", "Online");
        Require(filteredHistory.Items.Count == 1, "Another peer must stay hidden in the actual history window.");
        historySearch.Text = "";
        Require(filteredHistory.Items.Count == 2, "Clearing history search must restore only this peer's events.");
        historyWindow.Close();
        Console.WriteLine("PASS: action groups, nested popups, callbacks, settings compatibility, isolated editing; bounded history, deduplication, unknown states, endpoint changes and scope reset.");
        var entryEditor = new CustomActionsWindow([
            new CustomAction { Name = "First", Group = "Existing" },
            new CustomAction { Name = "Second" }
        ]);
        var groupInput = (TextBox)entryEditor.FindName("GroupBox");
        var groupChoices = (ComboBox)entryEditor.FindName("GroupChoices");
        var actionList = (ListBox)entryEditor.FindName("ActionsList");
        groupInput.Text = "Новая группа";
        actionList.SelectedIndex = 1;
        Require(entryEditor.Actions[0].Group == "Новая группа", "New group text must be committed on action change.");
        groupChoices.SelectedItem = "Новая группа";
        Require(groupInput.Text == "Новая группа", "Choosing an existing group must fill the text field.");
        actionList.SelectedIndex = 0;
        Require(entryEditor.Actions[1].Group == "Новая группа", "Selected existing group must be committed.");
        groupInput.Text = "";
        actionList.SelectedIndex = 1;
        Require(entryEditor.Actions[0].Group == "", "Clearing group text must remove grouping.");
        entryEditor.Close();

        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ThemeService.Apply(theme);
            var themedHistory = new PeerHistoryWindow("Theme check", history, "b");
            var historyGrid = (DataGrid)themedHistory.FindName("HistoryGrid");
            var row = new DataGridRow { Style = historyGrid.RowStyle };
            Require(Equals(row.Foreground, System.Windows.Application.Current.FindResource("TextBrush")), "History row text must use current theme.");
            Require(Equals(row.Background, System.Windows.Application.Current.FindResource("RowBackgroundBrush")), "History row background must use current theme.");
            row.IsSelected = true;
            Require(Equals(row.Foreground, System.Windows.Application.Current.FindResource("SelectedRowTextBrush")), "Selected history row text must use current theme.");
            Require(Equals(row.Background, System.Windows.Application.Current.FindResource("SelectedRowBrush")), "Selected history row background must use current theme.");
            Require(historyGrid.CellStyle is not null && historyGrid.FontSize == 13, "History cells must have explicit readable styling.");
            themedHistory.Close();
        }
        Console.WriteLine("PASS: new group input, existing group selection, clearing groups; history row and selection colors in both themes.");
        var navigationSettings = new ClientSettings { ServerUrl = "http://10.20.0.1", RadminPort = 4899 };
        var settingsWindow = new SettingsWindow(new SettingsService(), navigationSettings);
        var navigation = (ListBox)settingsWindow.FindName("SettingsNavigation");
        var serverBox = (TextBox)settingsWindow.FindName("ServerUrlBox");
        var checkBox = (TextBox)settingsWindow.FindName("ActiveCheckSecondsBox");
        Require(navigation.Items.Count == 7 && settingsWindow.MinWidth >= 760, "Settings must expose seven sections with sufficient width.");
        serverBox.Text = "http://example.test";
        checkBox.Text = "17";
        for (var section = 0; section < 7; section++)
        {
            navigation.SelectedIndex = section;
            for (var pageIndex = 0; pageIndex < 7; pageIndex++)
                Require(((StackPanel)settingsWindow.FindName("SettingsPage" + pageIndex)).Visibility ==
                    (section == pageIndex ? Visibility.Visible : Visibility.Collapsed), "Only the selected settings page must be visible.");
        }
        Require(serverBox.Text == "http://example.test" && checkBox.Text == "17", "Section changes must retain unsaved fields.");
        Require(navigationSettings.ServerUrl == "http://10.20.0.1" && navigationSettings.ActiveCheckSeconds == 5, "Navigation must not apply unsaved values.");
        var themeSelector = (ComboBox)settingsWindow.FindName("ThemeComboBox");
        Require(ReferenceEquals(((Border)((StackPanel)((StackPanel)themeSelector.Parent).Parent).Parent).Parent, settingsWindow.FindName("SettingsPage1")), "Theme must belong to appearance.");
        var settingsSearch = (TextBox)settingsWindow.FindName("SettingsSearchBox");
        var settingsResults = (ListBox)settingsWindow.FindName("SettingsSearchResults");
        settingsSearch.Text = "TCP порт";
        Require(settingsResults.Items.Count > 0, "Settings search must match multiple words.");
        settingsResults.SelectedIndex = 0;
        Require(navigation.SelectedIndex == 4, "Search selection must navigate to availability checks.");
        Require(serverBox.Text == "http://example.test" && checkBox.Text == "17", "Search navigation must retain unsaved values.");
        settingsSearch.Text = "несуществующий-параметр";
        Require(settingsResults.Items.Count == 0 && ((TextBlock)settingsWindow.FindName("SearchHint")).Text == "Ничего не найдено", "Empty results must have an explicit message.");
        settingsSearch.Text = " ";
        Require(settingsResults.Visibility == Visibility.Collapsed, "Clearing search must hide results.");
        Console.WriteLine("PASS: settings search by all words, navigation to results, no-result message and retained unsaved values.");
        var closingMethod = typeof(SettingsWindow).GetMethod("SettingsWindow_Closing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        settingsWindow.Closing -= (System.ComponentModel.CancelEventHandler)Delegate.CreateDelegate(typeof(System.ComponentModel.CancelEventHandler), settingsWindow, closingMethod);
        settingsWindow.Close();
        Console.WriteLine("PASS: seven settings sections, visibility switching, unsaved values retained, appearance placement, no settings writes.");
        var transferOriginal = new ClientSettings
        {
            ProtectedPassword = "secret-marker", Username = "User",
            CustomActions = [new CustomAction { Name = "Tool", Group = "Tools", Executable = @"C:\Tools\app.exe", Arguments = ["{ip}", "with spaces"] }],
            FavoritePeerIds = ["peer-a"], StatusNotificationPeerIds = ["peer-b"]
        };
        var exportedSettings = SettingsTransfer.Export(transferOriginal);
        Require(!exportedSettings.Contains("secret-marker") && !exportedSettings.Contains("ProtectedPassword"), "Export must omit stored credentials.");
        var importedSettings = SettingsTransfer.Import(exportedSettings);
        Require(importedSettings.CustomActions[0].Group == "Tools" && importedSettings.CustomActions[0].Arguments[1] == "with spaces" &&
            importedSettings.FavoritePeerIds.SequenceEqual(["peer-a"]) && importedSettings.ProtectedPassword == "", "Transfer must preserve action groups and lists without secrets.");
        Require(transferOriginal.ProtectedPassword == "secret-marker", "Export must not alter saved settings.");
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(exportedSettings)!;
        envelope["Settings"]!["ProtectedPassword"] = "untrusted secret";
        Require(SettingsTransfer.Import(envelope.ToJsonString()).ProtectedPassword == "", "Import must ignore credentials from files.");
        envelope["Version"] = 999;
        try { SettingsTransfer.Import(envelope.ToJsonString()); throw new Exception("Unsupported version accepted."); }
        catch (ArgumentException) { }
        envelope["Version"] = 1;
        envelope["Settings"]!["RefreshSeconds"] = 0;
        try { SettingsTransfer.Import(envelope.ToJsonString()); throw new Exception("Invalid interval accepted."); }
        catch (ArgumentException) { }
        var copied = new ClientSettings();
        SettingsTransfer.Copy(importedSettings, copied);
        copied.CustomActions[0].Arguments.Add("changed");
        Require(importedSettings.CustomActions[0].Arguments.Count == 2, "Applying settings must deep-copy mutable lists.");
        var styledActions = new CustomActionsWindow(transferOriginal.CustomActions);
        var styledList = (ListBox)styledActions.FindName("ActionsList");
        Require(styledList.ItemTemplate is not null && styledList.ItemContainerStyle is not null, "Action list must explicitly style text, groups and selection.");
        styledActions.Close();
        Console.WriteLine("PASS: settings transfer without credentials, groups/lists round-trip, invalid import rejection, independent copies and action-list styling.");
        var csvPeers = new[]
        {
            new Peer { Name = "ПК;\"Имя\"", VpnIp = "10.0.0.2", Notes = "строка1\nстрока2", Status = "running" },
            new Peer { Name = "=SUM(A1)", VpnIp = "10.0.0.1", Notes = " @command" }
        };
        csvPeers[0].AwgState.IsCurrent = false;
        var csv = PeerCsvExport.Create(csvPeers);
        Require(csv.Contains("\"ПК;\"\"Имя\"\"\"") && csv.Contains("\"строка1\nстрока2\""), "CSV must preserve delimiters, quotes, Unicode and multiline notes.");
        Require(csv.Contains("\"'=SUM(A1)\"") && csv.Contains("\"' @command\""), "CSV must neutralize spreadsheet formulas.");
        Require(csv.Contains("\"Неизвестно\"") && csv.IndexOf("10.0.0.2", StringComparison.Ordinal) < csv.IndexOf("10.0.0.1", StringComparison.Ordinal), "CSV must preserve supplied order and stale AWG state.");
        Require(PeerCsvExport.Create([]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length == 1, "Empty export must still include headers.");
        Console.WriteLine("PASS: CSV Unicode/quoting/multiline, formula neutralization, supplied order, unknown AWG and empty export.");
        var logTestPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vpnmonitor-log-test-{Guid.NewGuid():N}.log");
        try
        {
            Require(!(await LogReader.ReadAsync(logTestPath)).Exists, "Missing log must not be an error.");
            var entryOne = "[2026-10-22 12:00:00.000] Ошибка проверки\nSystem.Exception: marker\n   at Example.Method()\n";
            var entryTwo = "[2026-10-22 12:00:01.000] Recovered\n";
            await System.IO.File.WriteAllTextAsync(logTestPath, entryOne + entryTwo, new System.Text.UTF8Encoding(false));
            using (var writer = new System.IO.FileStream(logTestPath, System.IO.FileMode.Open, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite))
            {
                var logSnapshot = await LogReader.ReadAsync(logTestPath);
                Require(logSnapshot.Text == entryOne + entryTwo && !logSnapshot.Truncated, "Reader must share files with an active writer and preserve Unicode.");
                var filteredLog = LogReader.Filter(logSnapshot.Text, "MARKER");
                Require(filteredLog == entryOne && filteredLog.Contains("at Example.Method()"), "Search must retain complete exception entries.");
            }
            await System.IO.File.WriteAllTextAsync(logTestPath, new string('x', LogReader.MaxBytes + 100) + "\n" + entryOne + entryTwo);
            var tail = await LogReader.ReadAsync(logTestPath);
            Require(tail.Truncated && tail.Text == entryOne + entryTwo, "Large logs must be bounded and discard partial leading entries.");
            await System.IO.File.WriteAllTextAsync(logTestPath, entryTwo);
            Require((await LogReader.ReadAsync(logTestPath)).Text == entryTwo, "Reader must handle file truncation.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await LogReader.ReadAsync(logTestPath, cancelled.Token); throw new Exception("Cancelled read continued."); }
            catch (OperationCanceledException) { }
        }
        finally { if (System.IO.File.Exists(logTestPath)) System.IO.File.Delete(logTestPath); }
        var logViewer = new LogWindow();
        Require(((TextBox)logViewer.FindName("LogText")).IsReadOnly, "Log viewer must not allow edits.");
        logViewer.Close();
        Console.WriteLine("PASS: log shared reads, Unicode, complete-entry search, bounded tail, truncation, cancellation, missing file and read-only viewer.");
        using (var searchModel = new VpnMonitor.Client.ViewModels.MainViewModel())
        {
            foreach (var name in new[] { "PrestigBH_Inna", "PrestigBH_Kassa2", "PrestigBH_Sasha", "PrestigBH_srv",
                "PrestigStroyka_pc", "PrestigStroyka_srv", "PrestigStroyka_upr" })
                searchModel.Peers.Add(new Peer { Id = name, Name = name, Groups = ["Office"], VpnIp = "10.20.0.25" });
            searchModel.SearchText = "Prestig BH";
            Require(searchModel.PeersView.Cast<Peer>().Select(p => p.Name).Order().SequenceEqual(
                new[] { "PrestigBH_Inna", "PrestigBH_Kassa2", "PrestigBH_Sasha", "PrestigBH_srv" }.Order()), "Space search must match all four BH clients only.");
            searchModel.SearchText = "  bh\tPRESTIG  ";
            Require(searchModel.PeersView.Cast<Peer>().Count() == 4, "Search must ignore case, word order and repeated whitespace.");
            searchModel.SearchText = "Prestig BH srv";
            Require(searchModel.PeersView.Cast<Peer>().Single().Name == "PrestigBH_srv", "Additional search terms must narrow results.");
            searchModel.SearchText = "BH Office 10.20";
            Require(searchModel.PeersView.Cast<Peer>().Count() == 4, "Search words may match different existing searchable fields.");
            searchModel.SearchText = "BH Missing";
            Require(!searchModel.PeersView.Cast<Peer>().Any(), "Every term must match.");
            searchModel.SearchText = " ";
            Require(searchModel.PeersView.Cast<Peer>().Count() == 7, "Blank search must retain all clients.");
        }
        Console.WriteLine("PASS: multiword client search, requested example, whitespace/case/order, cross-field matches and AND semantics.");
        var testKey = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
        var profileText = $"[Interface]\nPrivateKey={testKey}\nAddress=10.99.0.2/32\nI1=<secret-value>\n[Peer]\nPublicKey={testKey}\nAllowedIPs=0.0.0.0/0\nEndpoint=example.com:51820\n";
        var profilePreview = AwgConfigPreview.Parse(profileText);
        Require(!profilePreview.Contains(testKey) && !profilePreview.Contains("<secret-value>") && profilePreview.Contains("всего трафика"), "Profile preview must hide keys/additional values and warn about default routes.");
        Require(AwgConfigPreview.Parse(profileText.Replace("I1=<secret-value>", "PreUp=never-run-this")).Contains("не выполнялись"), "Profile scripts must be reported, never executed.");
        foreach (var broken in new[] { profileText.Replace("10.99.0.2/32", "10.99.0.2/99"), profileText.Replace(testKey, "secret-invalid"), "[Peer]\nPublicKey=secret" })
        {
            try { AwgConfigPreview.Parse(broken); throw new Exception("Malformed profile accepted."); }
            catch (ArgumentException ex) { Require(!ex.Message.Contains("secret-invalid"), "Parser errors must not expose values."); }
        }
        var realProfilePath = System.IO.Path.Combine(Environment.CurrentDirectory, "TEST.conf");
        if (System.IO.File.Exists(realProfilePath))
        {
            var realText = await System.IO.File.ReadAllTextAsync(realProfilePath);
            var realPreview = AwgConfigPreview.Parse(realText);
            foreach (var line in realText.Split('\n').Where(l => l.TrimStart().StartsWith("PrivateKey", StringComparison.OrdinalIgnoreCase)))
            {
                var secretValue = line[(line.IndexOf('=') + 1)..].Trim();
                Require(!realPreview.Contains(secretValue), "Actual TEST.conf private key must not appear in preview.");
            }
            Console.WriteLine("PASS: actual TEST.conf structural preview; private key omitted.");
        }
        Console.WriteLine("PASS: AWG structural parsing, hidden values, script/default-route warnings and sanitized errors.");
        Require(new AwgServiceSnapshot(4, 0).Text == "Работает" && new AwgServiceSnapshot(0, 0).Text == "Служба не установлена",
            "AWG status labels must distinguish running and missing service.");
        var liveService = AwgServiceStatus.Read();
        Require(liveService.State is >= 0 and <= 7, "Read-only SCM query must return a valid state without elevation.");
        var awgWindow = new AwgStatusWindow();
        awgWindow.Close();
        Console.WriteLine("PASS: read-only shared AWG status, unelevated SCM query and window creation.");
        foreach (System.Windows.Window closeWindow in new System.Windows.Window[]
        {
            new AboutWindow(), new AwgStatusWindow(), new LogWindow(), new PeerHistoryWindow("Close test", new PeerHistory(), "test")
        })
        {
            var closed = false;
            closeWindow.Closed += (_, _) => closed = true;
            ((System.Windows.Controls.Button)closeWindow.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Require(closed, "Modeless Close button must explicitly close its window.");
        }
        Console.WriteLine("PASS: explicit Close buttons in AWG status, log and peer history.");
        var profileCatalog=AwgProfileCatalogReader.Read();
        Require(profileCatalog.Version==1 && profileCatalog.Profiles.All(p=>Guid.TryParseExact(p.Id,"N",out _)),
            "Shared profile catalog must expose valid identifiers without secrets.");
        Require(profileCatalog.SelectedId is null || profileCatalog.Profiles.Any(p=>p.Id==profileCatalog.SelectedId),
            "Selected shared profile must exist in catalog.");
        var catalogText=System.Text.Json.JsonSerializer.Serialize(profileCatalog);
        Require(!catalogText.Contains("PrivateKey")&&!catalogText.Contains("PresharedKey"),"Public catalog must not contain keys.");
        Console.WriteLine("PASS: shared profile catalog, valid selected profile and secret-free public metadata.");
        var opened = new List<string?>();
        var custom = 0;
        var submenu = NetworkShareMenu.CreateSubmenu(opened.Add, () => custom++, true);
        Require(submenu.HasItems && submenu.Items.Count == 4, "Shares must be grouped into a four-item submenu.");
        submenu.ApplyTemplate();
        Require(submenu.Template.FindName("PART_Popup", submenu) is Popup, "Custom menu template must support submenu popups.");
        foreach (MenuItem item in submenu.Items) item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(opened.SequenceEqual(new string?[] { null, "C$", "ADMIN$" }) && custom == 1, "Submenu actions must preserve all share callbacks.");
        var buttonMenu = NetworkShareMenu.CreateContextMenu(opened.Add, () => custom++);
        Require(buttonMenu.Items.Count == 4, "Card dropdown must contain the same actions.");
        Require(!NetworkShareMenu.CreateSubmenu(_ => { }, () => { }, false).IsEnabled, "Invalid targets must disable the group.");

        const string executable = @"C:\Windows\System32\OpenSSH\ssh.exe";
        var ssh = SshService.CreateStartInfo(executable, "10.20.0.25", "admin", 2222);
        Require(ssh.ArgumentList.SequenceEqual(new[] { "-p", "2222", "-l", "admin", "10.20.0.25" })
            && ssh.UseShellExecute && ssh.WindowStyle == ProcessWindowStyle.Normal, "SSH must receive separate login/port/target arguments in an interactive console.");
        Require(SshService.CreateStartInfo(executable, "2001:db8::1", @"DOMAIN\user", 22).ArgumentList[^1] == "2001:db8::1",
            "IPv6 and domain logins must remain literal.");
        foreach (var user in new[] { "", " ", "-oProxyCommand", "user -o x", "a&b", "a\"b", "a\nb" })
        {
            try { SshService.CreateStartInfo(executable, "10.20.0.25", user, 22); throw new Exception("Invalid SSH login accepted."); }
            catch (ArgumentException) { }
        }
        foreach (var port in new[] { 0, -1, 65536 })
        {
            try { SshService.CreateStartInfo(executable, "10.20.0.25", "user", port); throw new Exception("Invalid SSH port accepted."); }
            catch (ArgumentException) { }
        }
        try { SshService.CreateStartInfo(executable, "host & cmd", "user", 22); throw new Exception("Invalid SSH target accepted."); }
        catch (ArgumentException) { }
        Require(JsonSerializer.Deserialize<ClientSettings>("{}")!.SshPort == 22, "Old settings must load SSH defaults.");
        var installed = SshService.ResolveExecutable();
        if (installed is not null)
        {
            var info = new ProcessStartInfo(installed)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
            };
            info.ArgumentList.Add("-V");
            using var process = Process.Start(info)!;
            var output = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
            Require(process.ExitCode == 0 && (await output).Contains("OpenSSH"), "Installed SSH executable must respond to -V.");
        }
        Console.WriteLine("PASS: submenu popup/template/actions, SSH argument validation/defaults, installed OpenSSH -V.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
