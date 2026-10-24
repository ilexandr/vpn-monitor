using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using VpnMonitor.Client.Services;
using VpnMonitor.Client.ViewModels;

internal static class ConnectionRecoveryChecks
{
    public static async Task RunAsync()
    {
        Require(ConnectionFailure.IsRetryable(new HttpRequestException("reset", new SocketException((int)SocketError.ConnectionReset))), "Socket reset must permit a single safe GET retry.");
        Require(!ConnectionFailure.IsRetryable(new HttpRequestException("denied", null, HttpStatusCode.Unauthorized)), "Authentication failures must not be retried immediately.");
        Require(ConnectionFailure.From(new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused))).Kind == "refused", "Refused connections need a useful diagnosis.");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var stop = new CancellationTokenSource();
        var resets = 1;
        var status = 200;
        var requests = 0;
        var hold = false;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    var path = await reader.ReadLineAsync(stop.Token) ?? "";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                    Interlocked.Increment(ref requests);
                    if (hold) { blocked.TrySetResult(); await Task.Delay(Timeout.Infinite, stop.Token); }
                    if (resets > 0) { Interlocked.Decrement(ref resets); client.Client.LingerState = new LingerOption(true, 0); continue; }
                    var body = path.Contains("/me ") ? "{\"username\":\"test\",\"is_admin\":true}" : "{\"items\":[]}";
                    var response = Encoding.UTF8.GetBytes($"HTTP/1.1 {status} Status\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
                    await stream.WriteAsync(response, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            using var api = new ApiClient(url, "test", "fake");
            var me = await api.GetMeAsync();
            Require(me.Username == "test" && requests >= 2 && api.TransportRetryCount <= 1, "One transient reset must recover through a bounded GET retry.");
            status = 401;
            var before = requests;
            try { await api.GetMeAsync(); throw new Exception("Unauthorized response passed."); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized) { }
            Require(requests == before + 1, "401 must perform one request only.");
            using var model = new MainViewModel();
            model.Configure(url, "test", "fake");
            status = 503;
            var logBefore = File.Exists(VpnMonitor.Client.App.ClientLogPath) ? File.ReadAllText(VpnMonitor.Client.App.ClientLogPath).Length : 0;
            await model.RefreshAsync(); await model.RefreshAsync();
            Require(model.ConsecutiveFailures == 2 && model.ErrorMessage!.Contains("503") && !model.AwgState.IsCurrent, "Failures must remain visible even when repeated logs are suppressed.");
            var newLogs = File.ReadAllText(VpnMonitor.Client.App.ClientLogPath)[logBefore..];
            Require(newLogs.Split("API connection failure (http-503)").Length == 2, "Repeated failures of the same kind must produce one detailed log entry per interval.");
            status = 200; await model.RefreshAsync();
            Require(model.ConsecutiveFailures == 0 && model.LastRecoveredAt is not null && !model.HasError, "Successful recovery must reset failure count and expose a recovery timestamp.");
            model.Configure(url, "other", "fake");
            Require(model.LastRecoveredAt is null && model.TransportRetryCount == 0, "Account changes must clear connection diagnostics.");
            hold = true;
            var pending = model.RefreshAsync();
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            model.Dispose();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Require(model.ConsecutiveFailures == 0, "Shutdown during a request must not generate a false timeout or release a disposed semaphore.");
            Console.WriteLine("PASS: real TCP reset recovery, bounded GET retry, no auth retry, visible error streak, deduplicated logs and recovery/account reset.");
        }
        finally { stop.Cancel(); await server; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
