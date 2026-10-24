using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VpnMonitor.Client.Services;

/// <summary>
/// Best-effort helper that fills only the username field in the native
/// Radmin authentication dialog. No password is read, stored or injected.
/// </summary>
public sealed class RadminLoginAutofillService
{
    private const uint WmSetText = 0x000C;
    private const uint WmNextDlgCtl = 0x0028;
    private const uint EmGetPasswordChar = 0x00D2;
    private const int GwlStyle = -16;
    private const long EsPassword = 0x0020;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public async Task<bool> TryFillUsernameAsync(
        Process launchedProcess,
        string executablePath,
        string username,
        CancellationToken cancellationToken = default)
    {
        username = username.Trim();
        if (username.Length == 0)
            return false;

        var processName = Path.GetFileNameWithoutExtension(executablePath);
        var launchedPid = SafeGetProcessId(launchedProcess);

        // Radmin can either keep the process we started or hand the connection
        // to an already running Viewer instance. Therefore we scan the launched
        // PID first and also other Radmin.exe processes for a short period.
        for (var attempt = 0; attempt < 50; attempt++) // ~10 seconds
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidatePids = GetCandidateProcessIds(processName, launchedPid);
            if (candidatePids.Count > 0 && TryFillAnyAuthenticationWindow(candidatePids, username))
                return true;

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static int? SafeGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return null;
        }
    }

    private static HashSet<int> GetCandidateProcessIds(string processName, int? launchedPid)
    {
        var result = new HashSet<int>();
        if (launchedPid is > 0)
            result.Add(launchedPid.Value);

        if (string.IsNullOrWhiteSpace(processName))
            return result;

        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        result.Add(process.Id);
                    }
                    catch
                    {
                        // Process may disappear between enumeration and access.
                    }
                }
            }
        }
        catch
        {
            // Best effort only. The launched PID may still be enough.
        }

        return result;
    }

    private static bool TryFillAnyAuthenticationWindow(HashSet<int> candidatePids, string username)
    {
        var filled = false;

        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window))
                return true;

            GetWindowThreadProcessId(window, out var pid);
            if (!candidatePids.Contains(unchecked((int)pid)))
                return true;

            if (!TryFillAuthenticationWindow(window, username))
                return true;

            filled = true;
            return false;
        }, IntPtr.Zero);

        return filled;
    }

    private static bool TryFillAuthenticationWindow(IntPtr topWindow, string username)
    {
        var edits = new List<EditControl>();

        EnumChildWindows(topWindow, (child, _) =>
        {
            if (!IsWindowVisible(child) || !IsWindowEnabled(child))
                return true;

            var className = GetClassName(child);
            if (!className.Contains("Edit", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!GetWindowRect(child, out var rect))
                rect = default;

            edits.Add(new EditControl(child, IsPasswordEdit(child), rect));
            return true;
        }, IntPtr.Zero);

        // This is the key guard that prevents us from changing e.g. the address
        // box in the main Viewer window. We only touch a window that actually
        // contains a password edit control.
        var passwordEdit = edits.FirstOrDefault(edit => edit.IsPassword);
        if (passwordEdit.Handle == IntPtr.Zero)
            return false;

        var usernameEdit = edits
            .Where(edit => !edit.IsPassword)
            .OrderBy(edit => UsernameCandidateDistance(edit.Rect, passwordEdit.Rect))
            .FirstOrDefault();

        if (usernameEdit.Handle == IntPtr.Zero)
            return false;

        var setResult = SendMessageString(usernameEdit.Handle, WmSetText, IntPtr.Zero, username);
        if (setResult == IntPtr.Zero && !string.Equals(GetWindowText(usernameEdit.Handle), username, StringComparison.Ordinal))
            return false;

        // Put the caret into the password field when possible, so after Admin is
        // inserted the user can immediately type the password and press Enter.
        SetForegroundWindow(topWindow);
        SendMessage(topWindow, WmNextDlgCtl, passwordEdit.Handle, new IntPtr(1));
        return true;
    }

    private static long UsernameCandidateDistance(Rect rect, Rect passwordRect)
    {
        // Prefer the edit immediately above the password box. This is more
        // reliable than blindly taking the first edit if the dialog has extras.
        if (rect.Bottom <= passwordRect.Top)
            return passwordRect.Top - rect.Bottom;

        return 100_000L + Math.Abs(rect.Top - passwordRect.Top);
    }

    private static bool IsPasswordEdit(IntPtr handle)
    {
        try
        {
            var style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
            if ((style & EsPassword) != 0)
                return true;

            return SendMessage(handle, EmGetPasswordChar, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    private static string GetClassName(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        _ = GetClassNameW(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetWindowText(IntPtr handle)
    {
        var length = GetWindowTextLengthW(handle);
        if (length <= 0)
            return string.Empty;

        var builder = new StringBuilder(length + 1);
        _ = GetWindowTextW(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, index)
            : new IntPtr(GetWindowLong32(hWnd, index));

    private readonly record struct EditControl(IntPtr Handle, bool IsPassword, Rect Rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern IntPtr SendMessageString(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
