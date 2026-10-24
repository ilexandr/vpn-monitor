namespace VpnMonitor.Client.Models;

public sealed record PingSample(DateTime Time, long? Milliseconds);

public sealed class PingHistory
{
    private readonly Queue<PingSample> _recent = new();
    private double _sum;
    public const int HistoryLimit = 200;
    public long Sent { get; private set; }
    public long Received { get; private set; }
    public long Lost => Sent - Received;
    public double LossPercent => Sent == 0 ? 0 : 100d * Lost / Sent;
    public long? Minimum { get; private set; }
    public long? Maximum { get; private set; }
    public double? Average => Received == 0 ? null : _sum / Received;
    public IReadOnlyList<PingSample> Recent => _recent.ToArray();

    public void Add(long? milliseconds)
    {
        if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        Sent++;
        if (milliseconds is long value)
        {
            Received++;
            _sum += value;
            Minimum = Minimum is long min ? Math.Min(min, value) : value;
            Maximum = Maximum is long max ? Math.Max(max, value) : value;
        }
        _recent.Enqueue(new(DateTime.Now, milliseconds));
        while (_recent.Count > HistoryLimit) _recent.Dequeue();
    }

    public void Clear()
    {
        _recent.Clear();
        Sent = Received = 0;
        _sum = 0;
        Minimum = Maximum = null;
    }
}
