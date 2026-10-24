namespace VpnMonitor.Client.Models;

public sealed record ReachabilityResult(bool? IsReachable, long? RoundtripMilliseconds,
    int Failures, string Detail, DateTime? CheckedAt)
{
    public static ReachabilityResult Unknown(string detail = "Не проверялся") => new(null, null, 0, detail, null);
    public string Text => Failures is > 0 and < 3 ? $"Проверка {Failures}/3"
        : IsReachable == true ? RoundtripMilliseconds is long ms ? $"{ms} мс" : "Доступен (TCP)"
        : IsReachable == false ? "Не отвечает" : Detail;
    public string ToolTip => $"{Detail}\n{(CheckedAt is DateTime at ? $"Проверено: {at:HH:mm:ss}" : "Проверка не выполнялась")}\nОтсутствие ответа не доказывает, что компьютер выключен.";
}
