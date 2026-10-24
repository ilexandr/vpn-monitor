using System.Globalization;
using System.ComponentModel;
using System.Net;
using System.Text.Json.Serialization;

namespace VpnMonitor.Client.Models;

public sealed class Peer : INotifyPropertyChanged
{
    [JsonIgnore]
    public AwgDataState AwgState { get; set; } = new() { IsCurrent = true };
    private ReachabilityResult _reachability = ReachabilityResult.Unknown();
    [JsonIgnore]
    public ReachabilityResult Reachability
    {
        get => _reachability;
        set
        {
            if (_reachability == value) return;
            _reachability = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Reachability)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("vpn_ip")]
    public string? VpnIp { get; set; }

    [JsonPropertyName("allowed_ip")]
    public string? AllowedIp { get; set; }

    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("latest_handshake")]
    public string? LatestHandshake { get; set; }

    [JsonPropertyName("receive")]
    public double? Receive { get; set; }

    [JsonPropertyName("sent")]
    public double? Sent { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("groups")]
    public List<string> Groups { get; set; } = [];

    [JsonIgnore]
    public bool IsFavorite { get; set; }

    [JsonIgnore]
    public bool IsOnline => string.Equals(Status, "running", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string StatusText => IsOnline ? "Online" : "Offline";

    [JsonIgnore]
    public string GroupsText => string.Join(", ", Groups.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase));

    [JsonIgnore]
    public string ReceiveText => FormatTraffic(Receive);

    [JsonIgnore]
    public string SentText => FormatTraffic(Sent);

    [JsonIgnore]
    public string TrafficText => $"↓ {ReceiveText}   ↑ {SentText}";

    [JsonIgnore]
    public double TrafficTotal => (Receive ?? 0d) + (Sent ?? 0d);

    [JsonIgnore]
    public double HandshakeSortKey => TryHandshakeSeconds(LatestHandshake, out var seconds)
        ? seconds
        : double.PositiveInfinity;

    [JsonIgnore]
    public string HandshakeText
    {
        get
        {
            if (!TryHandshakeSeconds(LatestHandshake, out var seconds))
                return "никогда";

            if (seconds < 60)
                return $"{Math.Floor(seconds)} сек";

            if (seconds < 3600)
                return $"{Math.Floor(seconds / 60)} мин";

            if (seconds < 86400)
            {
                var hours = (int)(seconds / 3600);
                var minutes = (int)((seconds % 3600) / 60);
                return $"{hours} ч {minutes} мин";
            }

            var days = (int)(seconds / 86400);
            var remainderHours = (int)((seconds % 86400) / 3600);
            return $"{days} д {remainderHours} ч";
        }
    }

    [JsonIgnore]
    public long VpnIpSortKey
    {
        get
        {
            if (!IPAddress.TryParse(VpnIp, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return long.MaxValue;

            var bytes = address.GetAddressBytes();
            return ((long)bytes[0] << 24) |
                   ((long)bytes[1] << 16) |
                   ((long)bytes[2] << 8) |
                   bytes[3];
        }
    }

    [JsonIgnore]
    public string? EndpointHost => ExtractEndpointHost(Endpoint);

    private static string FormatTraffic(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            return "—";

        var n = value.Value;
        if (n < 0.001)
            return $"{n * 1024 * 1024:0.0} KB";
        if (n < 1)
            return $"{n * 1024:0.0} MB";
        return $"{n:0.00} GB";
    }

    private static bool TryHandshakeSeconds(string? value, out double seconds)
    {
        seconds = 0;

        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "No Handshake", StringComparison.OrdinalIgnoreCase))
            return false;

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span))
        {
            seconds = span.TotalSeconds;
            return true;
        }

        // Python timedelta can be rendered as: "1 day, 2:03:04" or "2 days, 1:02:03".
        var comma = value.IndexOf(',');
        if (comma > 0)
        {
            var dayPart = value[..comma].Trim();
            var timePart = value[(comma + 1)..].Trim();
            var dayToken = dayPart.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            if (int.TryParse(dayToken, out var days) &&
                TimeSpan.TryParse(timePart, CultureInfo.InvariantCulture, out var dayTime))
            {
                seconds = TimeSpan.FromDays(days).TotalSeconds + dayTime.TotalSeconds;
                return true;
            }
        }

        return false;
    }

    private static string? ExtractEndpointHost(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint == "(none)")
            return null;

        endpoint = endpoint.Trim();

        if (endpoint.StartsWith('['))
        {
            var close = endpoint.IndexOf(']');
            return close > 1 ? endpoint[1..close] : endpoint;
        }

        var firstColon = endpoint.IndexOf(':');
        var lastColon = endpoint.LastIndexOf(':');
        return firstColon >= 0 && firstColon == lastColon
            ? endpoint[..lastColon]
            : endpoint;
    }
}
