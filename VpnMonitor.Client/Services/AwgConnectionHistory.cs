using System.IO;
using System.Text.Json;
namespace VpnMonitor.Client.Services;
public sealed record AwgConnectionEvent(DateTimeOffset At, string State, string Endpoint, string Reason, string RequestId, int ProcessId)
{
    public string LocalTime => At.LocalDateTime.ToString("dd.MM HH:mm:ss");
    public string Description => Reason switch {
        "EndpointUpdateFailed" => "Движок не применил новый Endpoint; успех не подтверждён", "DeviceUpFailed" => "Движок не смог поднять интерфейс",
        "ServiceStarted" => "Служба запущена", "ServiceStopped" => "Служба остановлена вручную или обновлением",
        "AutomaticReconnect" => "Автовосстановление / попытка возврата", "ManualTesting" => "Начата ручная проверка резерва",
        "ManualReturning" => "Возврат к основному Endpoint", "BackupPassed" => "Резерв подтвердил входящий трафик",
        "BackupFailed" => "Резерв не подтвердил связь за отведённое время", "ManualPassed" => "Резерв проверен, основной восстановлен",
        "ManualFailed" => "Резерв не прошёл проверку; основной восстановлен", "ManualReturnFailed" => "Основной не восстановился; включено автовосстановление",
        "ManualRejected" => "Проверка отклонена: состояние изменилось или исчерпан бюджет попыток", _ => State switch {
            "Connected" => "Основной Endpoint работает", "Backup" => "Резервный Endpoint работает", "Confirming" => "Подтверждение потери связи",
            "Backoff" => "Пауза / ограничение попыток", "Disabled" => "Автовосстановление отключено", "TestingBackup" => "Проверяется связь через резерв",
            "VerifyingPrimary" => "Проверяется возврат основного", "Reconnect" or "Reconnecting" => "Переподключение", _ => "Наблюдение соединения" }
    };
}
public static class AwgConnectionHistory
{
    public static AwgConnectionEvent[] Read(string? path = null)
    {
        try {
            path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VpnMonitor", "AwgPrototype", "connection-events.json");
            if(new FileInfo(path).Length > 256*1024) return [];
            return (JsonSerializer.Deserialize<AwgConnectionEvent[]>(File.ReadAllText(path)) ?? [])
                .Where(e => e is not null && e.At <= DateTimeOffset.Now.AddMinutes(1) && e.At > DateTimeOffset.UnixEpoch &&
                    e.State is { Length: <= 64 } && e.Reason is { Length: <= 64 } && e.Endpoint is { Length: <= 256 } && e.RequestId is { Length: <= 64 })
                .TakeLast(200).Reverse().ToArray();
        } catch { return []; }
    }
}
