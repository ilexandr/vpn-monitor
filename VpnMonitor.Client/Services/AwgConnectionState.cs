using System.IO;
using System.Text.Json;
namespace VpnMonitor.Client.Services;

public sealed record AwgConnectionState(string State, bool TcpReachable, long HandshakeUnix, DateTimeOffset StartedAt, DateTimeOffset CheckedAt, int ProcessId, string Endpoint, bool IcmpReachable = false, bool Healthy = false)
{
    public string Text => State switch
    {
        "Connected" => "Связь установлена", "Backup" => "Работает резервный Endpoint",
        "Confirming" => "Подтверждение потери связи", "Reconnect" or "Reconnecting" => "Автоматическое переподключение",
        "Backoff" => "Пауза перед следующей попыткой восстановления", "Disabled" => "Автовосстановление отключено",
        "Stopped" => "Контроль соединения остановлен", _ => "Проверка соединения"
    };
    public string Details => Text + "\nEndpoint: " + Endpoint + "\nПроверено: " + CheckedAt.LocalDateTime.ToString("HH:mm:ss") +
        "\nКонтрольный TCP-сервис: " + (TcpReachable ? "доступен" : "нет ответа") + "\nКонтрольный ICMP: " + (IcmpReachable ? "доступен" : "нет ответа / не проверялся") + "\nHandshake: " +
        (HandshakeUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(HandshakeUnix).LocalDateTime.ToString("dd.MM HH:mm:ss") : "ещё не было");
    public static AwgConnectionState? Read()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VpnMonitor", "AwgStatus", "connection.json");
            if (new FileInfo(path).Length > 16384) return null;
            var state = JsonSerializer.Deserialize<AwgConnectionState>(File.ReadAllText(path));
            if (state is null || state.HandshakeUnix < 0 || state.HandshakeUnix > DateTimeOffset.MaxValue.ToUnixTimeSeconds() ||
                DateTimeOffset.Now - state.CheckedAt > TimeSpan.FromMinutes(6) || state.CheckedAt > DateTimeOffset.Now.AddMinutes(1)) return null;
            var service = AwgServiceStatus.Read();
            if(service.State != 4 || service.ProcessId != state.ProcessId) return null;
            return state;
        }
        catch { return null; }
    }
}
