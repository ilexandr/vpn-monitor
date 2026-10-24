using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VpnMonitor.Client.Models;

namespace VpnMonitor.Client.Services;

public sealed class SettingsService
{
    private static string? _defaultDirectory;
    public static void ConfigureDefaultDirectory(string directory)
    {
        if (System.Windows.Application.Current is not null) throw new InvalidOperationException("Configure settings before application startup.");
        _defaultDirectory = Path.GetFullPath(directory);
    }
    private const int CryptProtectUiForbidden = 0x1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public string SettingsDirectory { get; }

    public SettingsService(string? directory = null) => SettingsDirectory = directory ?? _defaultDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VpnMonitorClient");

    public string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
    public string PreviousSettingsPath => Path.Combine(SettingsDirectory, "settings.previous.json");

    public ClientSettings Load()
    {
        if (TryLoad(SettingsPath, out var settings)) return settings!;
        if (TryLoad(PreviousSettingsPath, out settings))
        {
            App.Log("Primary settings unavailable; loaded the previous valid settings snapshot.");
            return settings!;
        }
        return new ClientSettings();
    }

    public void Save(ClientSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var temporary = Path.Combine(SettingsDirectory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            if (TryLoad(SettingsPath, out _)) File.Replace(temporary, SettingsPath, PreviousSettingsPath, true);
            else File.Move(temporary, SettingsPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool TryLoad(string path, out ClientSettings? settings)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            settings = JsonSerializer.Deserialize<ClientSettings>(input, JsonOptions);
            return settings is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { settings = null; return false; }
    }

    public string ProtectPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            return "";

        var clear = Encoding.UTF8.GetBytes(password);
        var input = BytesToBlob(clear);
        DataBlob output = default;

        try
        {
            if (!CryptProtectData(
                    ref input,
                    "VPN Monitor password",
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output))
            {
                throw new InvalidOperationException(
                    $"Не удалось защитить пароль через Windows DPAPI. Win32 error: {Marshal.GetLastWin32Error()}");
            }

            return Convert.ToBase64String(BlobToBytes(output));
        }
        finally
        {
            FreeInputBlob(input);
            FreeOutputBlob(output);
            Array.Clear(clear, 0, clear.Length);
        }
    }

    public string UnprotectPassword(string protectedPassword)
    {
        if (string.IsNullOrWhiteSpace(protectedPassword))
            return "";

        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(protectedPassword);
        }
        catch
        {
            return "";
        }

        var input = BytesToBlob(encrypted);
        DataBlob output = default;

        try
        {
            if (!CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output))
            {
                return "";
            }

            var clear = BlobToBytes(output);
            try
            {
                return Encoding.UTF8.GetString(clear);
            }
            finally
            {
                Array.Clear(clear, 0, clear.Length);
            }
        }
        catch
        {
            return "";
        }
        finally
        {
            FreeInputBlob(input);
            FreeOutputBlob(output);
            Array.Clear(encrypted, 0, encrypted.Length);
        }
    }

    private static DataBlob BytesToBlob(byte[] data)
    {
        if (data.Length == 0)
            return default;

        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);

        return new DataBlob
        {
            cbData = data.Length,
            pbData = ptr
        };
    }

    private static byte[] BlobToBytes(DataBlob blob)
    {
        if (blob.pbData == IntPtr.Zero || blob.cbData <= 0)
            return Array.Empty<byte>();

        var data = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, data, 0, blob.cbData);
        return data;
    }

    private static void FreeInputBlob(DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero)
            Marshal.FreeHGlobal(blob.pbData);
    }

    private static void FreeOutputBlob(DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero)
            _ = LocalFree(blob.pbData);
    }
}
