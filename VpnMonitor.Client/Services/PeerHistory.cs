using System.Collections.ObjectModel;
namespace VpnMonitor.Client.Services;

public sealed record PeerHistoryEntry(DateTimeOffset Time, string PeerId, string Source, string Previous, string Current)
{
    public string LocalTime => Time.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss");
}

/// <summary>Bounded session history. Call from the UI dispatcher.</summary>
public sealed class PeerHistory
{
    private readonly Dictionary<(string Id, string Source), string> _last = [];
    public ObservableCollection<PeerHistoryEntry> Entries { get; } = [];
    private readonly int _capacity;
    public PeerHistory(int capacity = 2000) => _capacity = Math.Clamp(capacity, 1, 10000);

    public void Observe(string id, string source, string value)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var key = (id, source);
        if (_last.TryGetValue(key, out var previous) && previous == value) return;
        _last[key] = value;
        Entries.Insert(0, new(DateTimeOffset.Now, id, source, previous ?? "—", value));
        while (Entries.Count > _capacity) Entries.RemoveAt(Entries.Count - 1);
    }

    public void RetainPeers(IEnumerable<string> ids)
    {
        var allowed = ids.ToHashSet(StringComparer.Ordinal);
        foreach (var key in _last.Keys.Where(k => !allowed.Contains(k.Id)).ToArray()) _last.Remove(key);
        for (var i = Entries.Count - 1; i >= 0; i--)
            if (!allowed.Contains(Entries[i].PeerId)) Entries.RemoveAt(i);
    }

    public void Reset()
    {
        _last.Clear();
        Entries.Clear();
    }
}