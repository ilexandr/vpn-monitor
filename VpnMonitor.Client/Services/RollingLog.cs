using System.IO;
using System.Text;

namespace VpnMonitor.Client.Services;

public static class RollingLog
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int Backups = 3;

    public static void Append(string path, string text, int maxBytes = MaxBytes)
    {
        maxBytes = Math.Clamp(maxBytes, 256, MaxBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var gate = Acquire(path + ".lock");
        var budget = Math.Min(16000, maxBytes / 4 - 32);
        if (text.Length > budget)
        {
            if (budget > 0 && char.IsHighSurrogate(text[budget - 1])) budget--;
            text = text[..budget] + "\n[Запись сокращена]\n";
        }
        var bytes = Encoding.UTF8.GetBytes(text);
        if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > maxBytes)
        {
            for (var i = Backups; i >= 1; i--)
            {
                var source = i == 1 ? path : path + "." + (i - 1);
                var target = path + "." + i;
                if (File.Exists(source)) File.Move(source, target, true);
            }
        }
        for (var i = 1; i <= Backups; i++) Trim(path + "." + i, maxBytes);
        using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        output.Write(bytes);
    }

    private static FileStream Acquire(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 5) { Thread.Sleep(10); }
        }
    }

    private static void Trim(string path, int maxBytes)
    {
        if (!File.Exists(path) || new FileInfo(path).Length <= maxBytes) return;
        byte[] bytes;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            input.Seek(-maxBytes, SeekOrigin.End);
            bytes = new byte[maxBytes];
            input.ReadExactly(bytes);
        }
        var newline = Array.IndexOf(bytes, (byte)'\n');
        var text = newline < 0 ? "" : Encoding.UTF8.GetString(bytes, newline + 1, bytes.Length - newline - 1);
        if (Encoding.UTF8.GetByteCount(text) > maxBytes) text = text[^Math.Min(text.Length, maxBytes / 4)..];
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        output.Write(Encoding.UTF8.GetBytes(text));
    }
}
