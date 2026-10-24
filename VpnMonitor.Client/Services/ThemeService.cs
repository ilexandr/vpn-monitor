using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VpnMonitor.Client.Services;

public static class ThemeService
{
    public const string Light = "light";
    public const string Dark = "dark";

    // Windows 10 20H1+ uses attribute 20.  Older Windows 10 builds used 19.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    public static string CurrentTheme { get; private set; } = Light;

    public static string Normalize(string? theme) =>
        string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase) ? Dark : Light;

    public static void Apply(string? theme)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
            return;

        var normalized = Normalize(theme);
        CurrentTheme = normalized;

        var themeFile = normalized == Dark ? "Dark.xaml" : "Light.xaml";
        var source = new Uri(
            $"/VpnMonitor.Client;component/Themes/{themeFile}",
            UriKind.RelativeOrAbsolute);

        var dictionaries = app.Resources.MergedDictionaries;
        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            var existing = dictionaries[i].Source?.OriginalString ?? "";
            if (existing.Contains("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                existing.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase))
            {
                dictionaries.RemoveAt(i);
            }
        }

        dictionaries.Insert(0, new ResourceDictionary { Source = source });

        // Update the native Windows caption/title bar on already-created windows too.
        foreach (Window window in app.Windows)
            ApplyWindowChrome(window, normalized);
    }

    public static void ApplyWindowChrome(Window window, string? theme = null)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            var enabled = Normalize(theme ?? CurrentTheme) == Dark ? 1 : 0;
            var size = Marshal.SizeOf<int>();

            // Try the current attribute first and fall back for older Windows 10 builds.
            var result = DwmSetWindowAttribute(
                hwnd,
                DwmwaUseImmersiveDarkMode,
                ref enabled,
                size);

            if (result != 0)
            {
                DwmSetWindowAttribute(
                    hwnd,
                    DwmwaUseImmersiveDarkModeBefore20H1,
                    ref enabled,
                    size);
            }
        }
        catch
        {
            // Native title-bar theming is cosmetic and must never stop the app.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
}
