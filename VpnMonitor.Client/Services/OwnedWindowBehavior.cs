using System.Windows;
using System.Windows.Threading;

namespace VpnMonitor.Client.Services;

public static class OwnedWindowBehavior
{
    public static T Attach<T>(T child, Window owner) where T : Window
    {
        child.Owner = owner;
        child.ShowInTaskbar = false;
        var restore = false;
        child.Closing += (_, e) => restore = !e.Cancel && child.IsActive && CanRestore(owner);
        child.Closed += (_, _) =>
        {
            if (!restore) return;
            owner.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (CanRestore(owner)) owner.Activate();
            }));
        };
        return child;
    }

    private static bool CanRestore(Window owner)
    {
        var root = owner;
        while (root.Owner is not null) root = root.Owner;
        return owner.IsVisible && owner.WindowState != WindowState.Minimized
            && (root is not MainWindow main || !main.IsExiting);
    }
}
