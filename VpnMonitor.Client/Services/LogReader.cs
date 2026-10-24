using System.IO;
using System.Text;
using System.Text.RegularExpressions;
namespace VpnMonitor.Client.Services;

public sealed record LogSnapshot(string Text, bool Truncated, bool Exists);
public static class LogReader
{
    public const int MaxBytes = 512 * 1024;
    public static async Task<LogSnapshot> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            var length = stream.Length;
            var start = Math.Max(0, length - MaxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var bytes = new byte[(int)(length - start)];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(read), cancellationToken);
                if (count == 0) break;
                read += count;
            }
            var text = Encoding.UTF8.GetString(bytes, 0, read);
            if (start > 0)
            {
                // Discard incomplete UTF-8/line prefix at the tail boundary.
                var newline = text.IndexOf('\n');
                text = newline >= 0 ? text[(newline + 1)..] : "";
                var entry = Regex.Match(text, @"(?m)^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]");
                text = entry.Success ? text[entry.Index..] : "";
            }
            return new(text, start > 0, true);
        }
        catch (FileNotFoundException) { return new("", false, false); }
        catch (DirectoryNotFoundException) { return new("", false, false); }
    }

    public static string Filter(string text, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return text;
        var entries = Regex.Split(text, @"(?m)(?=^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\])");
        return string.Concat(entries.Where(entry => entry.Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase)));
    }
}