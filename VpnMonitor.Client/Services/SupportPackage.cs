using System.IO;
using System.IO.Compression;
using System.Text;

namespace VpnMonitor.Client.Services;

public sealed class DiagnosticCapture
{
    public string Report { get; }
    public DiagnosticRedactor Redactor { get; }
    public DiagnosticCapture(string report, DiagnosticRedactor redactor)
    {
        Redactor = redactor;
        var clean = redactor.Redact(report);
        Report = clean.Length > 64000 ? clean[..64000] + "\n[Отчёт сокращён]" : clean;
    }
}

public static class SupportPackage
{
    public static async Task WriteAsync(string destination, DiagnosticCapture capture, bool includeLogs, string logPath, CancellationToken cancellation)
    {
        var temporary = Path.GetFullPath(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            cancellation.ThrowIfCancellationRequested();
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Create, true, Encoding.UTF8);
                await AddAsync(zip, "report.txt", capture.Report, cancellation);
                await AddAsync(zip, "README.txt", "VPN Monitor: локальный диагностический отчёт.\nОтчёт содержит адрес сервера, сведения о системе и состояния компонентов.\nНастройки, профили VPN, ключи и история клиентов не добавляются.\nЖурналы добавлены только по выбору пользователя; известные пароли и типовые секреты маскируются.\nПеред отправкой просмотрите содержимое отчёта.\n", cancellation);
                if (includeLogs)
                {
                    foreach (var suffix in new[] { "", ".1" })
                    {
                        var snapshot = await LogReader.ReadAsync(logPath + suffix, cancellation);
                        if (!snapshot.Exists) continue;
                        var text = (snapshot.Truncated ? "[Последние 512 КБ журнала]\n" : "") + capture.Redactor.Redact(snapshot.Text);
                        await AddAsync(zip, suffix.Length == 0 ? "client.log.txt" : "client.previous.log.txt", text, cancellation);
                    }
                }
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, Path.GetFullPath(destination), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task AddAsync(ZipArchive zip, string name, string text, CancellationToken cancellation)
    {
        await using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        await writer.WriteAsync(text.AsMemory(), cancellation);
    }
}
