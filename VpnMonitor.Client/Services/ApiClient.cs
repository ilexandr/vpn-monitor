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

        _http = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(12)
        };

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VpnMonitor.Client/0.1.64");
    }

    public async Task<MeResponse> GetMeAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/v1/me", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<MeResponse>(JsonOptions, cancellationToken)
               ?? new MeResponse();
    }

    public async Task<PeersResponse> GetPeersAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/v1/peers", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<PeersResponse>(JsonOptions, cancellationToken)
               ?? new PeersResponse();
    }

    public async Task<GroupsResponse> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/v1/groups", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<GroupsResponse>(JsonOptions, cancellationToken)
               ?? new GroupsResponse();
    }

    public async Task<SummaryResponse> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/v1/summary", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<SummaryResponse>(JsonOptions, cancellationToken)
               ?? new SummaryResponse();
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
