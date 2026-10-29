using System.Net;
namespace VpnMonitor.Client.Models;
public sealed record HttpProxyOptions
{
    public string Mode { get; init; } = "direct";
    public string Address { get; init; } = "";
    public bool ForServerApi { get; init; }
    public void Validate()
    {
        if(Address is null || Address.Length>2048)throw new ArgumentException("Некорректный адрес прокси.");
        if(Mode is not ("direct" or "http"))throw new ArgumentException("Выберите режим прокси.");
        if((Mode=="http" || Address.Length>0) && (!Uri.TryCreate(Address,UriKind.Absolute,out var uri)||uri.Scheme!="http"||string.IsNullOrWhiteSpace(uri.Host)||uri.Port is <1 or >65535||uri.UserInfo.Length>0||uri.AbsolutePath!="/"||uri.Query.Length>0||uri.Fragment.Length>0||Address.Any(char.IsControl)))
            throw new ArgumentException("Введите адрес HTTP-прокси http://host:port без логина, пароля и пути.");
    }
    public string CanonicalAddress { get {Validate();return Mode=="http"?new Uri(Address).AbsoluteUri:"";} }
}
