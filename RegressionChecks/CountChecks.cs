using System.Net;
using System.Net.Sockets;
using System.Text;
using VpnMonitor.Client.ViewModels;
internal static class CountChecks
{
    public static async Task RunAsync()
    {
        using var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        using var stop=new CancellationTokenSource();
        var fail=false;
        var server=Task.Run(async ()=>
        {
            try
            {
                while(!stop.IsCancellationRequested)
                {
                    using var client=await listener.AcceptTcpClientAsync(stop.Token);
                    using var stream=client.GetStream();var buffer=new byte[8192];
                    var count=await stream.ReadAsync(buffer,stop.Token);
                    var request=Encoding.ASCII.GetString(buffer,0,count);
                    var json=request.Contains("/peers ") ? "{\"items\":[{\"id\":\"p1\",\"status\":\"running\"},{\"id\":\"p2\",\"status\":\"offline\"}]}"
                        : request.Contains("/groups ") ? "{\"items\":[{\"name\":\"A\",\"peer_ids\":[\"p1\"]},{\"name\":\"B\",\"peer_ids\":[\"p2\"]}]}"
                        : "{\"username\":\"test\",\"is_admin\":true}";
                    var bytes=Encoding.UTF8.GetBytes($"HTTP/1.1 {(fail ? "503 Unavailable" : "200 OK")}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(json)}\r\nConnection: close\r\n\r\n{json}");
                    await stream.WriteAsync(bytes,stop.Token);
                }
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        });
        try
        {
            using var model=new MainViewModel();
            model.Configure($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}","test","test");
            await model.RefreshAsync();
            if(model.OnlineCount!=1 || model.OfflineCount!=1 || model.OnlineCountText!="1" || model.Groups[0].CountText!="1/1") throw new Exception("Initial counters wrong");
            var group=model.Groups[0];var notified=false;
            group.PropertyChanged+=(_,e)=>notified |= e.PropertyName=="CountText";
            fail=true;await model.RefreshAsync();
            if(model.OnlineCount!=0 || model.OfflineCount!=0 || model.OnlineCountText!="—" || model.Groups.Any(g=>g.CountText!="—/1") || !notified || model.Peers.Count!=2) throw new Exception("Stale counters retained online/offline values");
            fail=false;await model.RefreshAsync();
            if(model.OnlineCountText!="1" || model.Groups[0].CountText!="1/1" || model.Groups[1].CountText!="0/1") throw new Exception("Counters did not recover");
            model.MarkAwgDataStale();
            if(model.OnlineCountText!="—" || model.Groups.Any(g=>g.IsCurrent)) throw new Exception("Explicit reference loss did not invalidate counters");
            Console.WriteLine("PASS: global and group counters become unknown on connection loss, notify bindings, retain list and recover from fresh API data.");
        }
        finally {stop.Cancel();await server;}
    }
}
