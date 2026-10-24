using System.Net;
using System.Net.Sockets;
using System.Text;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.ViewModels;

internal static class AccessChecks
{
    public static async Task RunAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var restricted = false;
        var denied = false;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    using var stream = client.GetStream();
                    using var reader = new System.IO.StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    var request = await reader.ReadLineAsync(stop.Token) ?? "";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                    var json = request.Contains("/me ") ?
                        restricted ? """{"username":"Иван","is_admin":false,"group":"Иван"}""" : """{"username":"admin","is_admin":true}""" :
                        request.Contains("/groups ") ?
                        restricted ? """{"items":[{"name":"Иван","peer_ids":["own"]}]}""" : """{"items":[{"name":"AdminGroup","peer_ids":["all"]}]}""" :
                        restricted ? """{"items":[{"id":"own","name":"Own","groups":["Иван"],"status":"running"}]}""" :
                        """{"items":[{"id":"all","name":"AdminPeer","groups":["AdminGroup"],"status":"running"}]}""";
                    var body = Encoding.UTF8.GetBytes(denied ? """{"detail":"Forbidden"}""" : json);
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(denied ? "403 Forbidden" : "200 OK")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, stop.Token);
                    await stream.WriteAsync(body, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            using var model = new MainViewModel();
            var url = $"http://127.0.0.1:{port}";
            model.Configure(url, "admin", "test");
            await model.RefreshAsync();
            Check(model.IsAdmin && model.Peers.Single().Id == "all", "Admin snapshot missing.");
            restricted = true;
            model.Configure(url, "Иван", "test");
            Check(model.Peers.Count == 0 && model.Groups.Count == 0 && !model.IsAdmin, "Old account data must clear immediately.");
            await model.RefreshAsync();
            Check(!model.HasError && model.IsRestrictedUser && model.RestrictedGroup == "Иван", "Restricted Unicode account missing.");
            Check(model.Peers.Single().Id == "own" && model.PeersView.Cast<Peer>().Single().Id == "own", "Server-scoped peers must populate restricted view.");
            Check(model.SelectedGroup == "Иван" && model.Groups.All(g => g.Name == "Иван"), "Restricted group must be selected.");
            denied = true;
            model.Configure(url, "denied", "test");
            await model.RefreshAsync();
            Check(model.HasError && model.Peers.Count == 0 && !model.AwgState.IsCurrent, "Denied new account must not retain prior data.");
        }
        finally { stop.Cancel(); await server; }
        Console.WriteLine("PASS: admin/restricted snapshots, Unicode account, immediate scope clearing, group selection and denied access.");
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}