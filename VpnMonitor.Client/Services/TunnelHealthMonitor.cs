using System.Net.Sockets;
namespace VpnMonitor.Client.Services;

public sealed class TunnelHealthMonitor
{
    private int _failures;
    public bool IsDegraded { get; private set; }
    public string Text { get; private set; } = "Доступность VPN ещё не проверена";
    public bool Observe(bool running, bool? reachable)
    {
        var wasDegraded = IsDegraded;
        if (!running) { _failures = 0; IsDegraded = false; Text = "Туннель отключён"; }
        else if (reachable is null) { _failures = 0; IsDegraded = false; Text = "Доступность VPN неизвестна"; }
        else if (reachable == true) { _failures = 0; IsDegraded = false; Text = "VPN-сервер доступен"; }
        else
        {
            _failures = Math.Min(3, _failures + 1);
            IsDegraded = _failures >= 3;
            Text = IsDegraded ? "Нет связи с VPN-сервером: возможен сбой туннеля или сервера" : $"Проверка связи с VPN-сервером ({_failures}/3)";
        }
        return IsDegraded != wasDegraded;
    }
    public static async Task<bool> ProbeAsync(Uri server, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var client = new TcpClient();
        try { await client.ConnectAsync(server.DnsSafeHost, server.Port, timeout.Token); return true; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }
}
