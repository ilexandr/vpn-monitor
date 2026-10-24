using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VpnMonitor.Client;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;

internal static class OwnedWindowChecks
{
    public static async Task RunAsync()
    {
        Require(!System.Windows.Application.Current.Windows.OfType<MainWindow>().Any(), "Test resource application must not start a real client window.");
        using var icon = (IDisposable)typeof(MainWindow).GetMethod("LoadTrayIcon", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null)!;
        Require((int)icon.GetType().GetProperty("Width")!.GetValue(icon)! > 0, "Tray icon must resolve from the client assembly rather than the test entry assembly.");
        var owner = new Window { Width = 300, Height = 180, Left = SystemParameters.VirtualScreenLeft - 2500,
            Top = SystemParameters.VirtualScreenTop, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            owner.Show();
            var child = OwnedWindowBehavior.Attach(new Window { Width = 200, Height = 100, Left = owner.Left, Top = owner.Top }, owner);
            Require(!child.ShowInTaskbar && child.Owner == owner, "Owned tools must not create independent taskbar buttons.");
            child.Show(); child.Activate();
            var active = child.IsActive;
            child.Close();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Require(owner.IsVisible && owner.WindowState == WindowState.Normal && (!active || owner.IsActive), "Closing an active child must leave its owner visible and restore focus when permitted by Windows.");
            var nestedOwner = OwnedWindowBehavior.Attach(new Window { Width = 200, Height = 100, Left = owner.Left, Top = owner.Top }, owner);
            nestedOwner.Show();
            var nested = OwnedWindowBehavior.Attach(new Window { Width = 160, Height = 80, Left = owner.Left, Top = owner.Top }, nestedOwner);
            nested.Show(); nested.Close(); nestedOwner.Close();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Require(owner.IsVisible && owner.WindowState == WindowState.Normal, "Nested tools must preserve the root window.");
            var hiddenChild = OwnedWindowBehavior.Attach(new Window { Width = 160, Height = 80, Left = owner.Left, Top = owner.Top }, owner);
            hiddenChild.Show(); owner.Hide(); hiddenChild.Close();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Require(!owner.IsVisible, "Closing a child must not reopen an intentionally hidden owner.");
            var settings = new SettingsWindow(new SettingsService(), new ClientSettings(), () => { });
            var requested = 0;
            settings.Close();
            settings = new SettingsWindow(new SettingsService(), new ClientSettings(), () => requested++);
            ((Button)settings.FindName("TestNotificationButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(requested == 1 && ((TextBlock)settings.FindName("TestNotificationText")).Text.Contains("Запрос передан"), "Notification test must invoke one request without claiming confirmed delivery.");
            settings.Close();
            Console.WriteLine("PASS: owned-window taskbar/focus, nested close, intentionally hidden owner, isolated application startup and notification test routing.");
        }
        finally { owner.Close(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
