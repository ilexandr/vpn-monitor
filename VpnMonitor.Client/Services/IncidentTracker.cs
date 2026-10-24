using System.Collections.ObjectModel;

namespace VpnMonitor.Client.Services;

public sealed record Incident(string Id, string Key, string Level, string Title, string? PeerId, DateTimeOffset StartedAt,
    DateTimeOffset LastObservedAt, DateTimeOffset? EndedAt, string Result)
{
    public string StartedText => StartedAt.LocalDateTime.ToString("dd.MM HH:mm:ss");
    public string EndedText => EndedAt?.LocalDateTime.ToString("dd.MM HH:mm:ss") ?? "Продолжается";
    public TimeSpan Duration => (EndedAt ?? LastObservedAt) - StartedAt;
    public string DurationText => Duration.TotalDays >= 1 ? $"{(int)Duration.TotalDays} д. {Duration:hh\\:mm\\:ss}" : Duration.ToString(@"hh\:mm\:ss");
}
public sealed class IncidentTracker
{
    public ObservableCollection<Incident> Entries { get; } = [];
    private readonly Dictionary<string, Incident> _open = new(StringComparer.Ordinal);
    public bool Dirty { get; private set; }
    public void Reset() { Entries.Clear(); _open.Clear(); Dirty = false; }
    public void Observe(string key, string level, string title, string? peerId, bool? healthy, DateTimeOffset now)
    {
        if (_open.TryGetValue(key, out var current))
        {
            var next = current with { LastObservedAt = now >= current.LastObservedAt ? now : current.LastObservedAt,
                Level = healthy == false ? level : current.Level, Title = healthy == false ? title : current.Title };
            if (healthy != false)
            {
                // Unknown observations do not add unobserved downtime to a confirmed incident.
                next = next with { EndedAt = healthy == true ? next.LastObservedAt : current.LastObservedAt,
                    Result = healthy == true ? "Восстановлено" : "Наблюдение прервано" };
                _open.Remove(key);
            }
            Replace(current, next);
        }
        else if (healthy == false)
        {
            var entry = new Incident(Guid.NewGuid().ToString("N"), key, level, title, peerId, now, now, null, "Продолжается");
            Entries.Insert(0, entry); _open[key] = entry; Dirty = true;
        }
        Prune(now);
    }
    public void RetainPeers(ISet<string> ids)
    {
        foreach (var entry in Entries.Where(e => e.PeerId is not null && !ids.Contains(e.PeerId)).ToArray())
        { Entries.Remove(entry); _open.Remove(entry.Key); Dirty = true; }
    }
    public void InterruptClients(DateTimeOffset now)
    {
        foreach (var entry in _open.Values.Where(e => e.PeerId is not null).ToArray()) Observe(entry.Key, entry.Level, entry.Title, entry.PeerId, null, now);
    }
    public void Restore(IEnumerable<Incident> entries, ISet<string> ids, DateTimeOffset now)
    {
        var present = Entries.Select(e => e.Id).ToHashSet();
        foreach (var entry in entries.Where(e => !present.Contains(e.Id) && (e.PeerId is null || ids.Contains(e.PeerId))))
            Entries.Add(entry.EndedAt is null ? entry with { EndedAt = entry.LastObservedAt, Result = "Наблюдение прервано" } : entry);
        var ordered = Entries.OrderByDescending(e => e.StartedAt).ToArray(); Entries.Clear(); foreach (var entry in ordered) Entries.Add(entry);
        Dirty = true; Prune(now);
    }
    public void Clear() { Entries.Clear(); _open.Clear(); Dirty = true; }
    public void ClearCompleted()
    { foreach (var entry in Entries.Where(e => e.EndedAt is not null).ToArray()) Entries.Remove(entry); Dirty = true; }
    public void MarkSaved() => Dirty = false;
    public void MarkDirty() => Dirty = true;
    private void Replace(Incident old, Incident next)
    {
        if (old == next) return;
        var index = Entries.IndexOf(old); if (index >= 0) Entries[index] = next;
        if (_open.ContainsKey(old.Key)) _open[old.Key] = next;
        Dirty = true;
    }
    private void Prune(DateTimeOffset now)
    {
        foreach (var entry in Entries.Where(e => e.LastObservedAt < now.AddDays(-7)).Concat(Entries.Skip(2000)).Distinct().ToArray())
        { Entries.Remove(entry); if (_open.GetValueOrDefault(entry.Key)?.Id == entry.Id) _open.Remove(entry.Key); Dirty = true; }
    }
}
