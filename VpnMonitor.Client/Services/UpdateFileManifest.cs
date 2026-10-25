using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace VpnMonitor.Client.Services;
public sealed record UpdateFileEntry(string Path, long Size, string Sha256);
public sealed record UpdateFilePlan(UpdateFileEntry[] Changed, int Unchanged, long TotalBytes)
{
    public long ChangedBytes => Changed.Sum(e => e.Size);
}
public sealed record UpdateFileManifest(Version Version, string SourceCommit, UpdateFileEntry[] Files)
{
    public static UpdateFileManifest Parse(string json, Version expected)
    {
        if(json.Length>2*1024*1024) throw new InvalidDataException("Манифест превышает допустимый размер.");
        using var document=JsonDocument.Parse(json); var root=document.RootElement;
        if(root.GetProperty("Schema").GetInt32()!=1 || root.GetProperty("Version").GetString()!=expected.ToString()) throw new InvalidDataException("Версия манифеста не совпадает с релизом.");
        var commit=root.GetProperty("SourceCommit").GetString()??"";
        if(!Regex.IsMatch(commit,"^[a-f0-9]{40}$")) throw new InvalidDataException("Некорректная версия исходников манифеста.");
        var entries=root.GetProperty("Files");
        if(entries.ValueKind!=JsonValueKind.Array || entries.GetArrayLength() is <1 or >4096) throw new InvalidDataException("Некорректный список файлов.");
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var files=new List<UpdateFileEntry>();long total=0;
        foreach(var entry in entries.EnumerateArray()) {
            var path=entry.GetProperty("Path").GetString()??""; var hash=entry.GetProperty("Sha256").GetString()??"";var size=entry.GetProperty("Size").GetInt64();
            if(!IsSafePath(path) || !paths.Add(path) || size is <0 or >1073741824 || !Regex.IsMatch(hash,"^[a-f0-9]{64}$")) throw new InvalidDataException("Небезопасная или повреждённая запись манифеста.");
            total+=size;if(total>2L*1024*1024*1024) throw new InvalidDataException("Превышен размер состава релиза.");
            files.Add(new(path,size,hash));
        }
        return new(expected,commit,files.ToArray());
    }
    public static bool IsSafePath(string path) {
        if(path.Length>240 || path.Contains('\\') || path.Any(c=>char.IsControl(c)||"<>:\"|?*".Contains(c))) return false;
        var parts=path.Split('/');
        return parts.Length>=2 && parts[0] is "Client" or "Licenses" or "Setup" && parts.All(p=>p.Length>0 && p is not ("." or "..") &&
            !p.EndsWith('.') && !p.EndsWith(' ') && !Regex.IsMatch(p,@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)",RegexOptions.IgnoreCase));
    }
    public Task<UpdateFilePlan> CompareAsync(string installRoot,CancellationToken cancellation) => Task.Run(async () => {
        var root=Path.GetFullPath(installRoot);var changed=new List<UpdateFileEntry>();var unchanged=0;
        RejectLink(root);
        foreach(var file in Files) {
            cancellation.ThrowIfCancellationRequested();if(!IsSafePath(file.Path)) throw new InvalidDataException("Небезопасный путь файла.");var path=root;
            foreach(var part in file.Path.Split('/')) {path=Path.Combine(path,part);RejectLink(path);}
            bool same=false;
            try {
                if(new FileInfo(path).Length==file.Size) {
                    await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,65536,true);
                    same=Convert.ToHexString(await SHA256.HashDataAsync(stream,cancellation)).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase);
                }
            } catch(FileNotFoundException) {} catch(DirectoryNotFoundException) {}
            if(same) unchanged++;else changed.Add(file);
        }
        return new UpdateFilePlan(changed.ToArray(),unchanged,Files.Sum(e=>e.Size));
    },cancellation);
    private static void RejectLink(string path) {
        try {if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Оценка через ссылки и точки подключения запрещена.");}
        catch(FileNotFoundException) {} catch(DirectoryNotFoundException) {}
    }
}
