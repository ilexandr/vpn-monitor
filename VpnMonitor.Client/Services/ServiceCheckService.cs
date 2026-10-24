using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VpnMonitor.Client.Services;

public sealed class ServiceCheckResult
{
    public bool PingReachable { get; init; }
    public long? PingMilliseconds { get; init; }
    public bool RdpOpen { get; init; }
    public bool SmbOpen { get; init; }
    public bool RadminOpen { get; init; }
}

public sealed class ServiceCheckService
{
    private const int PingTimeoutMs = 1400;
    private static readonly TimeSpan TcpTimeout = TimeSpan.FromMilliseconds(1600);

    public async Task<ServiceCheckResult> CheckAsync(
        string host,
        int radminPort,
        CancellationToken cancellationToken = default)
    {
        var pingTask = CheckPingAsync(host, cancellationToken);
        var rdpTask = CheckTcpPortAsync(host, 3389, cancellationToken);
        var smbTask = CheckTcpPortAsync(host, 445, cancellationToken);
        var radminTask = CheckTcpPortAsync(host, radminPort, cancellationToken);

        await Task.WhenAll(pingTask, rdpTask, smbTask, radminTask);

        var ping = await pingTask;
        return new ServiceCheckResult
        {
            PingReachable = ping.Success,
            PingMilliseconds = ping.RoundtripTime,
            RdpOpen = await rdpTask,
            SmbOpen = await smbTask,
            RadminOpen = await radminTask
        };
    }

    private static async Task<(bool Success, long? RoundtripTime)> CheckPingAsync(
        string host,
        CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, PingTimeoutMs).WaitAsync(cancellationToken);
            return reply.Status == IPStatus.Success
                ? (true, reply.RoundtripTime)
                : (false, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (false, null);
        }
    }

    private static async Task<bool> CheckTcpPortAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TcpTimeout);
            await client.ConnectAsync(host, port, timeoutCts.Token);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
