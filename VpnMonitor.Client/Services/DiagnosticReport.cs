using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace VpnMonitor.Client.Services;

public sealed record DiagnosticState(string Server, bool ApiCurrent, bool ApiBusy, string? ApiError, DateTimeOffset? ApiAttempt,
    DateTimeOffset? ApiSuccess, string Role, int Clients, bool ActiveChecks, int ActiveCheckSeconds,
    bool HistorySaved, string TunnelHealth, int ConsecutiveFailures, int TransportRetries, DateTimeOffset? RecoveredAt);

public static class DiagnosticReport
{
    public static string Create(DiagnosticState state)
    {
        var text = new StringBuilder();
        void Add(string label, object? value) => text.Append(label).Append(": ").AppendLine(value?.ToString() ?? "—");
        Add("VPN Monitor — диагностика", DateTimeOffset.Now.ToString("O"));
        Add("Версия", UpdateService.CurrentVersion);
        Add("Сборка", typeof(DiagnosticReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        Add("ОС", RuntimeInformation.OSDescription);
        Add("Архитектура ОС / процесса", $"{RuntimeInformation.OSArchitecture} / {RuntimeInformation.ProcessArchitecture}");
        Add(".NET", RuntimeInformation.FrameworkDescription);
        using var identity = WindowsIdentity.GetCurrent();
        Add("Права процесса", new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ? "Администратор" : "Обычный пользователь");
        using var process = Process.GetCurrentProcess();
        Add("Сеанс Windows", process.SessionId);
        text.AppendLine("\nПОДКЛЮЧЕНИЕ (снимок текущих проверок, без новых сетевых запросов)");
        Add("Сервер", ServerOrigin(state.Server));
        Add("Данные AWG из API", state.ApiCurrent ? "Актуальны" : "Нет актуального снимка");
        Add("Обновление данных", state.ApiBusy ? "Выполняется" : "Не выполняется");
        Add("Последняя попытка API", state.ApiAttempt?.ToString("O"));
        Add("Последний успешный ответ API", state.ApiSuccess?.ToString("O"));
        Add("Ошибка API", state.ApiError);
        Add("Подряд неудачных обновлений", state.ConsecutiveFailures);
        Add("Повторов GET после разрыва соединения", state.TransportRetries);
        Add("Последнее восстановление API", state.RecoveredAt?.ToString("O"));
        Add("Режим доступа", state.Role);
        Add("Клиентов в разрешённом списке", state.Clients);
        Add("Активные проверки", state.ActiveChecks ? $"Включены, каждые {state.ActiveCheckSeconds} сек." : "Отключены");
        Add("Сохранение истории", state.HistorySaved ? "Включено" : "Отключено");
        text.AppendLine("\nОБЩИЙ ТУННЕЛЬ");
        Add("Имя службы", AwgServiceStatus.ServiceName);
        try
        {
            var service = AwgServiceStatus.Read();
            Add("Служба", $"{service.Text}, код завершения {service.ExitCode}");
        }
        catch (Exception ex) { Add("Состояние службы", "Недоступно: " + ex.Message); }
        text.AppendLine(state.TunnelHealth);
        try
        {
            var profiles = AwgProfileCatalogReader.Read();
            Add("Профилей в публичном каталоге", profiles.Profiles.Count);
            Add("Профиль выбран", profiles.Profiles.Any(p => p.Id == profiles.SelectedId) ? "Да" : "Нет");
        }
        catch (Exception ex) { Add("Публичный каталог", "Недоступен: " + ex.Message); }
        text.AppendLine("\nПОСЛЕДНЕЕ ОБНОВЛЕНИЕ КОМПЬЮТЕРА");
        try
        {
            var update = UpdateJournalSummary.Read();
            if (update is null) text.AppendLine("Сохранённых результатов пока нет.");
            else
            {
                Add("Фаза", update.PhaseText);
                Add("Запись обновлена", update.UpdatedAt.ToString("O"));
                Add("Сообщение", update.Message);
                Add("Идентификатор", update.Id);
                if (update.Phase is not ("Completed" or "Failed"))
                    text.AppendLine("Это сохранённая фаза; она не подтверждает, что процесс обновления ещё выполняется.");
                text.AppendLine($"Подробные журналы (доступ администратора): %PROGRAMDATA%\\VpnMonitor\\Updates\\{update.Id}");
            }
        }
        catch (Exception ex) { Add("Сводка обновления", "Недоступна: " + ex.Message); }
        text.AppendLine("\nВ отчёт не добавляются настройки, VPN-профили, ключи и история клиентов.\nАдрес сервера и сведения о системе остаются в отчёте. Журналы добавляются отдельно по выбору.");
        return text.ToString();
    }

    public static string ServerOrigin(string server)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "Не настроен или адрес некорректен";
        return new UriBuilder(uri.Scheme, uri.Host, uri.Port).Uri.GetLeftPart(UriPartial.Authority);
    }
}
