using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using VpnMonitor.Client.Models;

namespace VpnMonitor.Client.Services;

public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private int _transportRetries;
    public int TransportRetryCount => Volatile.Read(ref _transportRetries);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ApiClient(string serverUrl, string username, string password)
    {
        var normalized = serverUrl.Trim().TrimEnd('/') + "/";

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Некорректный адрес сервера.", nameof(serverUrl));
        }

        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        {
            BaseAddress = baseUri,
            Timeout = Timeout.InfiniteTimeSpan
        };

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VpnMonitor.Client/0.1.72");
    }

    public Task<MeResponse> GetMeAsync(CancellationToken cancellationToken = default) => GetAsync<MeResponse>("api/v1/me", cancellationToken);
    public Task<PeersResponse> GetPeersAsync(CancellationToken cancellationToken = default) => GetAsync<PeersResponse>("api/v1/peers", cancellationToken);
    public Task<GroupsResponse> GetGroupsAsync(CancellationToken cancellationToken = default) => GetAsync<GroupsResponse>("api/v1/groups", cancellationToken);
    public Task<SummaryResponse> GetSummaryAsync(CancellationToken cancellationToken = default) => GetAsync<SummaryResponse>("api/v1/summary", cancellationToken);

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken) where T : new()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await _http.GetAsync(path, timeout.Token);
                await EnsureSuccessAsync(response, timeout.Token);
                return await response.Content.ReadFromJsonAsync<T>(JsonOptions, timeout.Token) ?? new T();
            }
            catch (Exception ex) when (attempt == 0 && !timeout.IsCancellationRequested && ConnectionFailure.IsRetryable(ex))
            {
                Interlocked.Increment(ref _transportRetries);
                await Task.Delay(150, timeout.Token);
            }
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        var message = response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Неверный логин или пароль.",
            System.Net.HttpStatusCode.Forbidden => "Доступ запрещён сервером.",
            System.Net.HttpStatusCode.NotFound => "API endpoint не найден. Проверьте версию VPN Monitor.",
            _ => $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}"
        };

        throw new HttpRequestException(message, null, response.StatusCode);
    }

    public void Dispose() => _http.Dispose();
}
