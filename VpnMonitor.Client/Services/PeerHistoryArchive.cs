using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VpnMonitor.Client.Services;

/// <summary>Per-Windows-user archives. Never load until a fresh authorized peer list is available.</summary>
public sealed class PeerHistoryArchive
{
    private sealed record Archive(int Version, string Scope, PeerHistoryEntry[] Entries);
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _revisions = [];
    private long _writeFloor;
    private readonly Func<DateTimeOffset> _now;
    public PeerHistoryArchive(string? directory = null, Func<DateTimeOffset>? now = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VpnMonitorClient", "history");
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public static string Scope(string server, string username)
    {
        var uri = new Uri(server.Trim(), UriKind.Absolute);
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Unsupported history server address.");
        var account = username.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri.TrimEnd('/') + "\n" + account)));
    }

    public PeerHistoryEntry[] Load(string scope)
    {
        lock (_gate)
        {
            var path = GetPath(scope);
            if (!File.Exists(path)) return [];
            if (new FileInfo(path).Length > 4_000_000) throw new InvalidDataException("History archive exceeds its size limit.");
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), Encoding.ASCII.GetBytes(scope), DataProtectionScope.CurrentUser);
            try
            {
                var archive = JsonSerializer.Deserialize<Archive>(clear);
                if (archive is null || archive.Version != 1 || archive.Scope != scope || archive.Entries is null)
                    throw new InvalidDataException("Invalid history archive.");
                return Retain(archive.Entries);
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
    }

    public void Save(string scope, IEnumerable<PeerHistoryEntry> entries, long revision)
    {
        lock (_gate)
        {
            var path = GetPath(scope);
            // An older queued autosave must not overwrite a final save or resurrect cleared history.
            if (revision <= _writeFloor || revision <= _revisions.GetValueOrDefault(scope)) return;
            var clear = JsonSerializer.SerializeToUtf8Bytes(new Archive(1, scope, Retain(entries)));
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(clear, Encoding.ASCII.GetBytes(scope), DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(clear); }
            if (encrypted.Length > 4_000_000) throw new InvalidDataException("History archive exceeds its size limit.");
            Directory.CreateDirectory(_directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, encrypted);
                File.Move(temporary, path, true);
                _revisions[scope] = revision;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            // Bound account archives as well as individual event lists. No recursive deletion.
            var files = Files().Where(f => !string.Equals(f.FullName, path, StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            for (var i = 0; i < files.Length; i++)
                if (i >= 19 || files[i].LastWriteTimeUtc < _now().UtcDateTime.AddDays(-7)) files[i].Delete();
            foreach (var file in new DirectoryInfo(_directory).GetFiles("*.tmp"))
            {
                var parts = file.Name.Split('.');
                if (parts.Length == 3 && IsScope(parts[0]) && Guid.TryParseExact(parts[1], "N", out _)
                    && file.LastWriteTimeUtc < _now().UtcDateTime.AddDays(-1)) file.Delete();
            }
        }
    }

    public void ClearAll(long revision)
    {
        lock (_gate)
        {
            _writeFloor = Math.Max(_writeFloor, revision);
            foreach (var file in Files()) file.Delete();
        }
    }

    private FileInfo[] Files() => Directory.Exists(_directory)
        ? new DirectoryInfo(_directory).GetFiles("*.dpapi").Where(f => IsScope(Path.GetFileNameWithoutExtension(f.Name))).ToArray() : [];
    private string GetPath(string scope) => IsScope(scope) ? Path.Combine(_directory, scope + ".dpapi") : throw new ArgumentException("Invalid history scope.");
    private static bool IsScope(string scope) => scope.Length == 64 && scope.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private PeerHistoryEntry[] Retain(IEnumerable<PeerHistoryEntry> entries)
    {
        var now = _now();
        return entries.Where(e => e is not null && e.Time >= now.AddDays(-7) && e.Time <= now.AddMinutes(5)
                && e.PeerId is { Length: > 0 and <= 512 } && e.Source is { Length: > 0 and <= 64 }
                && e.Previous is { Length: <= 2048 } && e.Current is { Length: <= 2048 })
            .OrderByDescending(e => e.Time).Take(2000).ToArray();
    }
}
