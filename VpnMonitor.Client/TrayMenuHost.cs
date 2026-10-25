using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
namespace VpnMonitor.Client;

// A foreground anchor gives a real WPF ContextMenu normal tray focus/dismissal
// without showing the hidden main window. The native menu only forwards its Opening event.
public sealed class TrayMenuHost : Window
{
    private readonly ContextMenu _menu;
    private bool _closing;
    public TrayMenuHost(ContextMenu menu)
    {
        _menu = menu; Width = Height = 1; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; AllowsTransparency = true; Background = System.Windows.Media.Brushes.Transparent; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Loaded += (_, _) =>
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, new IntPtr(-1), cursor.X, cursor.Y, 1, 1, 0);
            SetForegroundWindow(handle);
            _menu.PlacementTarget = this; _menu.Placement = PlacementMode.RelativePoint;
            _menu.HorizontalOffset = 0; _menu.VerticalOffset = 0; _menu.IsOpen = true;
        };
        _menu.Closed += (_, _) => { if (!_closing) Close(); };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (!_closing && !IsActive && !_menu.IsKeyboardFocusWithin) Close(); }));
        Closing += (_, _) => { _closing = true; _menu.IsOpen = false; };
    }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
