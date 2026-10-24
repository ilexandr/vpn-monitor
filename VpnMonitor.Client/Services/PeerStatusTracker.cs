using VpnMonitor.Client.Models;

namespace VpnMonitor.Client.Services;

/// <summary>Compares successful API snapshots. Failed refreshes must not be supplied.</summary>
public sealed class PeerStatusTracker
{
    private readonly Dictionary<string, bool> _previous = new(StringComparer.Ordinal);
    private bool _ready;

    public void Reset()
    {
        _previous.Clear();
        _ready = false;
    }

    public IReadOnlyList<Peer> Update(IEnumerable<Peer> peers, ClientSettings settings, Func<Peer, bool?>? getStatus = null)
    {
        getStatus ??= peer => peer.IsOnline;
        var current = new Dictionary<string, Peer>(StringComparer.Ordinal);
        foreach (var peer in peers)
            if (!string.IsNullOrWhiteSpace(peer.Id) && getStatus(peer).HasValue) current[peer.Id] = peer;

        var changed = new List<Peer>();
        if (_ready && settings.StatusNotificationsEnabled)
        {
            var watched = new HashSet<string>(settings.StatusNotificationPeerIds ?? [], StringComparer.Ordinal);
            foreach (var peer in current.Values)
                if (watched.Contains(peer.Id) &&
                    _previous.TryGetValue(peer.Id, out var wasOnline) && wasOnline != getStatus(peer)!.Value &&
                    (getStatus(peer)!.Value ? settings.NotifyWhenOnline : settings.NotifyWhenOffline))
                    changed.Add(peer);
        }

        _previous.Clear();
        foreach (var pair in current) _previous[pair.Key] = getStatus(pair.Value)!.Value;
        _ready = true;
        return changed;
    }
}
