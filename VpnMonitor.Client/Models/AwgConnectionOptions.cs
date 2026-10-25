using System.Net;
using System.Text.RegularExpressions;
namespace VpnMonitor.Client.Models;

public sealed class AwgConnectionOptions
{
    public bool Enabled { get; set; } = true;
    public bool PreferPhysical { get; set; } = true;
    public string ProbeHost { get; set; } = "";
    public int ProbePort { get; set; } = 80;
    public int IntervalSeconds { get; set; } = 10;
    public int FailureSeconds { get; set; } = 30;
    public int CooldownSeconds { get; set; } = 60;
    public string BackupEndpoint { get; set; } = "";
    public bool ReturnToPrimary { get; set; }
    public int PrimaryRetrySeconds { get; set; } = 300;
    public void Validate(string? config = null)
    {
        if (ProbeHost is null || BackupEndpoint is null) throw new ArgumentException("Адрес не может быть null.");
        if (ProbePort is < 1 or > 65535 || IntervalSeconds is < 5 or > 300 || FailureSeconds is < 15 or > 600 ||
            CooldownSeconds is < 30 or > 3600 || PrimaryRetrySeconds is < 120 or > 86400)
            throw new ArgumentException("Порт: 1–65535; интервал: 5–300 с; подтверждение сбоя: 15–600 с; пауза: 30–3600 с; возврат: 120–86400 с.");
        if (BackupEndpoint.Length > 100 || BackupEndpoint.Any(char.IsControl) ||
            BackupEndpoint.Length > 0 && !IPEndPoint.TryParse(BackupEndpoint, out _))
            throw new ArgumentException("Резервный Endpoint: IP:порт, например 192.0.2.2:51820.");
        if (BackupEndpoint.Length > 0 && IPEndPoint.Parse(BackupEndpoint).Port == 0)
            throw new ArgumentException("Порт резервного Endpoint не может быть нулевым.");
        if (BackupEndpoint.Length > 0) BackupEndpoint = IPEndPoint.Parse(BackupEndpoint).ToString();
        if (ProbeHost.Length > 0 && (!IPAddress.TryParse(ProbeHost, out var target) || target.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
            throw new ArgumentException("Контрольный адрес должен быть IPv4 внутри маршрутов профиля.");
        if (config is null) return;
        if (BackupEndpoint.Length > 0 && Regex.Matches(config, @"(?im)^\s*\[Peer\]\s*$").Count != 1)
            throw new ArgumentException("Резервный Endpoint поддерживается для профиля с одним Peer.");
        if (ProbeHost.Length > 0)
        {
            var targetBytes = IPAddress.Parse(ProbeHost).GetAddressBytes();
            var allowed = Regex.Matches(config, @"(?im)^\s*AllowedIPs\s*=\s*([^\r\n]+)").SelectMany(m => m.Groups[1].Value.Split(','));
            bool Contains(string value)
            {
                var parts = value.Trim().Split('/');
                if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || !int.TryParse(parts[1], out var bits) || bits is < 0 or > 32) return false;
                var bytes = ip.GetAddressBytes(); if (bytes.Length != 4) return false;
                for (var i = 0; i < bits; i++) if ((bytes[i / 8] & (128 >> (i % 8))) != (targetBytes[i / 8] & (128 >> (i % 8)))) return false;
                return true;
            }
            if (!allowed.Any(Contains)) throw new ArgumentException("Контрольный адрес находится вне маршрутов профиля.");
            if (Regex.Matches(config, @"(?im)^\s*Address\s*=\s*([^\r\n]+)").SelectMany(m => m.Groups[1].Value.Split(',')).Any(a => a.Trim().Split('/')[0] == ProbeHost))
                throw new ArgumentException("Контрольный адрес не должен совпадать с адресом самого клиента.");
        }
    }
}
public sealed record AwgConnectionRequest(string ProfileId, AwgConnectionOptions Options);
