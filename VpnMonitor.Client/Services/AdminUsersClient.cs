using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using VpnMonitor.Client.Models;
namespace VpnMonitor.Client.Services;
public interface IAdminUsersClient : IDisposable
{
    Task<AdminUsersSnapshot> ReadAsync(CancellationToken token);
    Task CreateAsync(string username,string password,bool enabled,CancellationToken token);
    Task ChangeAsync(string username,string? password,bool? enabled,CancellationToken token);
    Task DeleteAsync(string username,CancellationToken token);
}
public sealed class AdminUsersException(string message,HttpStatusCode? status=null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}
public sealed class AdminUsersClient : IAdminUsersClient
{
    private readonly HttpClient _http;
    private const string Endpoint="api/v1/admin/users";
    public AdminUsersClient(string server,string username,string password,HttpMessageHandler? handler=null)
    {
        if(!Uri.TryCreate(server.Trim().TrimEnd('/')+"/",UriKind.Absolute,out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length>0 || uri.Query.Length>0 || uri.Fragment.Length>0)
            throw new ArgumentException("Некорректный адрес сервера.");
        _http=new HttpClient(handler ?? new SocketsHttpHandler {AllowAutoRedirect=false,PooledConnectionIdleTimeout=TimeSpan.FromSeconds(3)}) {BaseAddress=uri,Timeout=TimeSpan.FromSeconds(12),MaxResponseContentBufferSize=2*1024*1024};
        _http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(username+":"+password)));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VpnMonitor.Client/0.1.76");
    }
    public async Task<AdminUsersSnapshot> ReadAsync(CancellationToken token)
    {
        using var response=await _http.GetAsync(Endpoint,token);Check(response);
        var snapshot=await response.Content.ReadFromJsonAsync<AdminUsersSnapshot>(cancellationToken:token) ?? throw new AdminUsersException("Сервер вернул пустой список.");
        if(snapshot.Items is null || snapshot.Groups is null || snapshot.Items.Length>10000 || string.IsNullOrWhiteSpace(snapshot.CsrfToken) || snapshot.CsrfToken.Length>256 || snapshot.CsrfToken.Any(char.IsControl) || snapshot.Items.Any(u=>u is null || string.IsNullOrWhiteSpace(u.Username)))
            throw new AdminUsersException("Сервер вернул некорректный список пользователей.");
        return snapshot;
    }
    public Task CreateAsync(string username,string password,bool enabled,CancellationToken token) => WriteAsync(HttpMethod.Post,username,new Dictionary<string,object>{{"username",username},{"password",password},{"enabled",enabled}},token);
    public Task ChangeAsync(string username,string? password,bool? enabled,CancellationToken token)
    {
        var payload=new Dictionary<string,object>();if(password is not null)payload["password"]=password;if(enabled.HasValue)payload["enabled"]=enabled.Value;
        return WriteAsync(HttpMethod.Patch,username,payload,token);
    }
    public Task DeleteAsync(string username,CancellationToken token) => WriteAsync(HttpMethod.Delete,username,null,token);
    private async Task WriteAsync(HttpMethod method,string username,Dictionary<string,object>? payload,CancellationToken token)
    {
        // Refresh privileges and CSRF before every mutation; never replay a write after a timeout.
        var current=await ReadAsync(token);
        if(current.Items.Any(u=>u.IsAdmin && Identity(u.Username)==Identity(username))) throw new AdminUsersException("Администратор управляется конфигурацией сервера.",HttpStatusCode.Forbidden);
        var path=method==HttpMethod.Post ? Endpoint : Endpoint+"/"+Uri.EscapeDataString(username);
        byte[]? bytes=null;
        try {
            using var request=new HttpRequestMessage(method,path);
            request.Headers.Add("X-VpnMonitor-CSRF",current.CsrfToken);
            if(payload is not null) {bytes=JsonSerializer.SerializeToUtf8Bytes(payload);request.Content=new ByteArrayContent(bytes);request.Content.Headers.ContentType=new("application/json");}
            using var response=await _http.SendAsync(request,token);Check(response);
        } finally {if(bytes is not null)Array.Clear(bytes);}
    }
    private static string Identity(string name)=>name.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    private static void Check(HttpResponseMessage response)
    {
        if(response.IsSuccessStatusCode)return;
        var message=response.StatusCode switch {
            HttpStatusCode.Unauthorized=>"Авторизация отклонена. Проверьте учётную запись в настройках.",
            HttpStatusCode.Forbidden=>"Операция запрещена. Обновите список и проверьте права администратора.",
            HttpStatusCode.NotFound=>"Аккаунт или API управления не найден. Обновите список; для старого сервера требуется обновление.",
            HttpStatusCode.Conflict=>"Пользователь с таким именем уже существует.",
            HttpStatusCode.UnprocessableEntity=>"Проверьте имя, пароль и параметры пользователя.",
            HttpStatusCode.ServiceUnavailable=>"Сервер не смог сохранить базу пользователей.",
            _=>$"Сервер отклонил запрос (HTTP {(int)response.StatusCode})."
        };
        // Server bodies can contain validation input. Never include them in UI errors or logs.
        throw new AdminUsersException(message,response.StatusCode);
    }
    public void Dispose()=>_http.Dispose();
}
