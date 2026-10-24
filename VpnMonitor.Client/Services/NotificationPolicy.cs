using System.Collections.ObjectModel;
using VpnMonitor.Client.Models;

namespace VpnMonitor.Client.Services;

public sealed record NotificationCandidate(string Key, string Title, string Message, string? PeerId, string[] Groups, bool Online);
public sealed record NotificationDecision(DateTimeOffset Time, string Title, string Reason)
{ public string Text => $"{Time.LocalDateTime:HH:mm:ss} · {Title} · {Reason}"; }
public sealed class NotificationPolicy
{
    private readonly Dictionary<string, (NotificationCandidate Item, DateTimeOffset Due)> _pending = new(StringComparer.Ordinal);
    public ObservableCollection<NotificationDecision> Decisions { get; } = [];
    public void Reset() { _pending.Clear(); Decisions.Clear(); }
    public static bool IsQuiet(NotificationRules rules, DateTimeOffset now)
    {
        if (!rules.QuietHoursEnabled) return false;
        var minute = now.LocalDateTime.Hour * 60 + now.LocalDateTime.Minute;
        return rules.QuietStartMinute == rules.QuietEndMinute || (rules.QuietStartMinute < rules.QuietEndMinute
            ? minute >= rules.QuietStartMinute && minute < rules.QuietEndMinute
            : minute >= rules.QuietStartMinute || minute < rules.QuietEndMinute);
    }
    public void Enqueue(NotificationCandidate item, NotificationRules rules, DateTimeOffset now)
    {
        if (item.Online && _pending.Remove(item.Key, out var old) && !old.Item.Online)
        { Record(now, item.Title, "Краткий сбой закончился до уведомления"); return; }
        _pending[item.Key] = (item, now.AddSeconds(item.Online ? 0 : rules.OfflineDelaySeconds));
    }
    public IReadOnlyList<NotificationCandidate> Drain(NotificationRules rules, DateTimeOffset now, Func<NotificationCandidate, bool?> state)
    {
        var ready = new List<NotificationCandidate>();
        foreach (var pair in _pending.ToArray())
        {
            var item = pair.Value.Item;
            var current = state(item);
            string? reason = current is null ? "Нет актуальных данных; уведомление отменено" : current != item.Online ? "Состояние уже изменилось" :
                IsQuiet(rules, now) ? "Расписание тишины" : rules.Maintenance.Any(m => m.Until > now && item.Groups.Contains(m.Group, StringComparer.OrdinalIgnoreCase))
                ? "Группа на обслуживании" : null;
            if (reason is not null) { _pending.Remove(pair.Key); Record(now, item.Title, reason); continue; }
            if (now < pair.Value.Due) continue;
            _pending.Remove(pair.Key); ready.Add(item); Record(now, item.Title, "Разрешено правилами");
        }
        return ready;
    }
    public void CancelPending(string reason, DateTimeOffset now)
    { foreach (var item in _pending.Values) Record(now, item.Item.Title, reason); _pending.Clear(); }
    public void CancelPeer(string id, DateTimeOffset now)
    {
        foreach (var pair in _pending.Where(p => p.Value.Item.PeerId == id).ToArray())
        { _pending.Remove(pair.Key); Record(now, pair.Value.Item.Title, "Клиент больше не отслеживается"); }
    }
    public void Record(DateTimeOffset now, string title, string reason)
    { Decisions.Insert(0, new(now, title, reason)); while (Decisions.Count > 100) Decisions.RemoveAt(Decisions.Count - 1); }
}
