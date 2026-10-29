using System.Net;
using System.Net.Http;
using VpnMonitor.Client.Models;
namespace VpnMonitor.Client.Services;
public static class HttpProxyPolicy
{
    private static HttpProxyOptions _current=new();
    public static HttpProxyOptions Current=>Volatile.Read(ref _current);
    public static void Configure(HttpProxyOptions? options)
    {
        options??=new();options.Validate();Volatile.Write(ref _current,options with {Address=options.CanonicalAddress});
    }
    public static SocketsHttpHandler CreateHandler(bool serverApi=false,HttpProxyOptions? options=null)
    {
        options??=Current;options.Validate();
        var use=options.Mode=="http"&&(!serverApi||options.ForServerApi);
        return new SocketsHttpHandler { UseProxy=use,Proxy=use?new WebProxy(options.CanonicalAddress):null,
            PooledConnectionIdleTimeout=TimeSpan.FromSeconds(3),PooledConnectionLifetime=TimeSpan.FromMinutes(2)};
    }
}
