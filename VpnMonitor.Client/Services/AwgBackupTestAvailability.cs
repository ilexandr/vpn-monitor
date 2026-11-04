using VpnMonitor.Client.Models;
namespace VpnMonitor.Client.Services;
public sealed record AwgBackupAvailability(bool Available,string Explanation);
public static class AwgBackupTestAvailability
{
    public static AwgBackupAvailability Evaluate(uint serviceState,string? selectedId,string? activeId,AwgConnectionOptions? options,AwgConnectionState? connection,DateTimeOffset now)
    {
        string? reason=selectedId is null?"Выберите профиль в списке.":
            serviceState!=4?"Подключите общий туннель, затем дождитесь подтверждения связи.":
            selectedId!=activeId?"Проверка выполняется только для текущего подключённого профиля.":
            options is null?"Не удалось прочитать настройки связи выбранного профиля.":
            !options.Enabled?"Включите автовосстановление в «Связь и резерв…».":
            string.IsNullOrWhiteSpace(options.BackupEndpoint)?"Задайте резервный Endpoint в «Связь и резерв…».":
            connection is null?"Нет свежего состояния общей службы. Дождитесь обновления статуса.":
            !connection.SupportsManualTest?"Установленный движок не поддерживает ручную проверку. Обновите приложение.":
            connection.IsManualTestActive?"Проверка уже выполняется. Дождитесь результата и возврата основного Endpoint.":
            connection.State=="Backup"?"Туннель работает через резерв. Ручная проверка доступна только с исправного основного Endpoint.":
            !connection.Healthy||connection.State!="Connected"?"Дождитесь подтверждённой связи через основной Endpoint.":
            now>=connection.StartedAt&&now-connection.StartedAt<TimeSpan.FromSeconds(30)?"В первые 30 секунд после запуска службы проверка недоступна.":
            !connection.ManualTestAvailable?"Служба пока не разрешает проверку: пауза/бюджет попыток или ограничение профиля. Нужны один Peer и контрольный IPv4.":null;
        return reason is null?new(true,"Резерв можно проверить с возвратом к основному. Связь кратковременно прервётся."):new(false,reason);
    }
}
