using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VpnMonitor.Client.Services;

public sealed class IncidentArchive(string directory)
{
    private sealed record Document(int Version, string Scope, Incident[] Entries);
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _revisions = [];
    private long _floor;
    public Incident[] Load(string scope, DateTimeOffset now)
    {
        lock (_gate)
        {
            var path = PathFor(scope); if (!File.Exists(path)) return [];
            if (new FileInfo(path).Length > 4_000_000) throw new InvalidDataException("Incident archive exceeds limit.");
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), Encoding.ASCII.GetBytes(scope), DataProtectionScope.CurrentUser);
            try
            {
                var doc = JsonSerializer.Deserialize<Document>(clear);
                if (doc is null || doc.Version != 1 || doc.Scope != scope || doc.Entries is null) throw new InvalidDataException("Invalid incident archive.");
                return Retain(doc.Entries, now);
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
    }
    public void Save(string scope, Incident[] entries, long revision, DateTimeOffset now)
    {
        lock (_gate)
        {
            var path = PathFor(scope);
            if (revision <= _floor || revision <= _revisions.GetValueOrDefault(scope)) return;
            var clear = JsonSerializer.SerializeToUtf8Bytes(new Document(1, scope, Retain(entries, now)));
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(clear, Encoding.ASCII.GetBytes(scope), DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(clear); }
            if (encrypted.Length > 4_000_000) throw new InvalidDataException("Incident archive exceeds limit.");
            Directory.CreateDirectory(directory); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path, true); _revisions[scope] = revision; }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            var other = Files().Where(f => f.FullName != path).OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            for (var i = 0; i < other.Length; i++) if (i >= 19 || other[i].LastWriteTimeUtc < now.UtcDateTime.AddDays(-7)) other[i].Delete();
        }
    }
    public void ClearAll(long revision) { lock (_gate) { _floor = Math.Max(_floor, revision); foreach (var file in Files()) file.Delete(); } }
    private FileInfo[] Files() => Directory.Exists(directory) ? new DirectoryInfo(directory).GetFiles("*.dpapi").Where(f => MonitoringConfiguration.IsScope(Path.GetFileNameWithoutExtension(f.Name))).ToArray() : [];
    private string PathFor(string scope) => MonitoringConfiguration.IsScope(scope) ? Path.Combine(directory, scope + ".dpapi") : throw new ArgumentException("Invalid incident scope.");
    private static Incident[] Retain(IEnumerable<Incident> entries, DateTimeOffset now) => entries.Where(e => e is not null &&
        Guid.TryParseExact(e.Id, "N", out _) && e.Key is { Length: > 0 and <= 600 } && e.Title is { Length: > 0 and <= 800 } &&
        e.Level is { Length: > 0 and <= 80 } && e.Result is { Length: <= 100 } && e.PeerId?.Length is not > 512 &&
        e.StartedAt <= e.LastObservedAt && e.LastObservedAt >= now.AddDays(-7) && e.LastObservedAt <= now.AddMinutes(5) &&
        (e.EndedAt is null || e.EndedAt >= e.StartedAt && e.EndedAt <= e.LastObservedAt))
        .OrderByDescending(e => e.StartedAt).DistinctBy(e => e.Id).Take(2000).ToArray();
}
