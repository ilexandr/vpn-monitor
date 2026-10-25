using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace VpnMonitor.Client.Services;

public sealed record UpdateRelease(long Id, Version Version, string Notes);
public static class UpdateService
{
    public const string Repository = "https://github.com/ilexandr/vpn-monitor";
    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version is { } v ? new(v.Major,v.Minor,v.Build) : new(0,0,0);
    public static UpdateRelease Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        if (r.GetProperty("draft").GetBoolean() || r.GetProperty("prerelease").GetBoolean() || !Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$"))
            throw new InvalidDataException("Релиз не является стабильной версией.");
        var version = Version.Parse(tag.TrimStart('v'));
        var name = $"VpnMonitor-Setup-{version}-win-x64.exe";
        var asset = r.GetProperty("assets").EnumerateArray().SingleOrDefault(a => a.GetProperty("name").GetString() == name);
        if (asset.ValueKind == JsonValueKind.Undefined || !asset.TryGetProperty("digest",out var digest) ||
            !Regex.IsMatch(digest.GetString() ?? "", @"^sha256:[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("У релиза нет установщика Windows x64 с контрольной суммой SHA-256.");
        var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
        if (url.Scheme != "https" || url.Host != "github.com" || !url.AbsolutePath.StartsWith("/ilexandr/vpn-monitor/releases/download/",StringComparison.Ordinal))
            throw new InvalidDataException("Некорректный источник обновления.");
        return new(r.GetProperty("id").GetInt64(),version,r.TryGetProperty("body",out var body) ? body.GetString() ?? "" : "");
    }
    public static async Task<UpdateRelease?> CheckAsync(CancellationToken cancellation)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 2*1024*1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"VpnMonitor/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.GetAsync("https://api.github.com/repos/ilexandr/vpn-monitor/releases/latest", cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancellation));
    }
    public static async Task<UpdateFilePlan> GetFilePlanAsync(UpdateRelease expected, string installRoot, CancellationToken cancellation, HttpMessageHandler? handler = null)
    {
        using var http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 2*1024*1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"VpnMonitor/{CurrentVersion}");
        var json=await http.GetStringAsync($"https://api.github.com/repos/ilexandr/vpn-monitor/releases/{expected.Id}",cancellation);
        using var document=JsonDocument.Parse(json);var root=document.RootElement;
        var release=Parse(json);
        if(release.Id!=expected.Id || release.Version!=expected.Version) throw new InvalidDataException("Релиз изменился. Повторите проверку обновлений.");
        var assets=root.GetProperty("assets").EnumerateArray().Where(a=>a.GetProperty("name").GetString()=="file-manifest.json" && a.GetProperty("state").GetString()=="uploaded").ToArray();
        if(assets.Length!=1) throw new InvalidDataException("У этой версии ещё нет файлового манифеста.");
        var asset=assets[0];var size=asset.GetProperty("size").GetInt64();var digest=asset.GetProperty("digest").GetString()??"";
        var uri=new Uri(asset.GetProperty("browser_download_url").GetString()!);
        var path=$"/ilexandr/vpn-monitor/releases/download/{Uri.EscapeDataString(root.GetProperty("tag_name").GetString()!)}/file-manifest.json";
        if(size is <1 or >2097152 || !Regex.IsMatch(digest,"^sha256:[a-f0-9]{64}$") || uri.Scheme!="https" || uri.Host!="github.com" || uri.AbsolutePath!=path || uri.Query.Length!=0 || uri.Fragment.Length!=0)
            throw new InvalidDataException("Не удалось проверить источник манифеста.");
        var bytes=await http.GetByteArrayAsync(uri,cancellation);
        if(bytes.Length!=size || !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).Equals(digest[7..],StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Контрольная сумма манифеста не совпадает.");
        var manifest=UpdateFileManifest.Parse(System.Text.Encoding.UTF8.GetString(bytes),expected.Version);
        if(root.GetProperty("target_commitish").GetString()!=manifest.SourceCommit) throw new InvalidDataException("Манифест относится к другой сборке.");
        return await manifest.CompareAsync(installRoot,cancellation);
    }
    public static void Start(long releaseId)
    {
        if (releaseId <= 0) throw new ArgumentOutOfRangeException(nameof(releaseId));
        var script = Path.Combine(UpdateParticipant.InstallRoot,"Setup","Update-Application.ps1");
        if (!File.Exists(script)) throw new InvalidOperationException("Для обновления установите версию с поддержкой автономных обновлений.");
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe"))
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -ReleaseId {releaseId}";
        using var process = Process.Start(info);
    }
}
