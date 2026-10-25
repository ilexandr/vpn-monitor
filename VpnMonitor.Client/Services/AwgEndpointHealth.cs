using System.Net;
namespace VpnMonitor.Client.Services;

public static class AwgEndpointHealth
{
    public static string Describe(string backup, AwgConnectionState? connection)
    {
        var matches = connection is not null && IPEndPoint.TryParse(backup, out var expected) &&
            IPEndPoint.TryParse(connection.Endpoint, out var actual) && expected.Equals(actual);
        if (!matches) return "AWG на резерве: не подтверждён текущими данными. Ping не проверяет порт AWG.";
        return connection!.Healthy && connection.State == "Backup"
            ? "AWG на резерве: работает; подтверждено входящим трафиком · " + connection.CheckedAt.LocalDateTime.ToString("HH:mm:ss")
            : "AWG на резерве: связь пока не подтверждена · " + connection.CheckedAt.LocalDateTime.ToString("HH:mm:ss");
    }
}
