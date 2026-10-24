using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace VpnMonitor.Client.Services;

public sealed record ConnectionFailure(string Kind, string Message)
{
    public static ConnectionFailure From(Exception exception)
    {
        const string retry = " Подключение будет повторено автоматически.";
        if (exception is OperationCanceledException)
            return new("timeout", "Сервер не ответил за 12 секунд. Проверьте VPN-туннель." + retry);
        if (exception is HttpRequestException { StatusCode: { } status })
        {
            var message = status switch
            {
                HttpStatusCode.Unauthorized => "Неверный логин или пароль API.",
                HttpStatusCode.Forbidden => "Доступ запрещён сервером API.",
                HttpStatusCode.NotFound => "API не найден. Проверьте адрес и версию сервера.",
                _ => $"Сервер вернул HTTP {(int)status}." + retry
            };
            return new("http-" + (int)status, message);
        }
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is SocketException socket)
                return socket.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => new("dns", "Не удалось найти адрес сервера. Проверьте имя сервера и DNS." + retry),
                    SocketError.ConnectionRefused => new("refused", "Сервер отклонил подключение. Проверьте адрес, порт и работу API." + retry),
                    SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.NetworkReset => new("reset", "Соединение с сервером разорвано. Проверьте VPN и доступность сервера." + retry),
                    SocketError.NetworkDown or SocketError.NetworkUnreachable or SocketError.HostUnreachable => new("unreachable", "Сеть или сервер недоступны. Проверьте VPN-туннель." + retry),
                    _ => new("transport", "Не удалось связаться с сервером. Проверьте VPN-туннель." + retry)
                };
        if (exception is JsonException) return new("invalid-json", "Сервер прислал некорректные данные API." + retry);
        if (exception is HttpRequestException or HttpIOException) return new("transport", "Соединение с API прервано." + retry);
        return new(exception.GetType().Name, exception.Message);
    }

    public static bool IsRetryable(Exception exception)
    {
        if (exception is HttpRequestException { StatusCode: not null }) return false;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.NetworkReset }) return true;
            if (current is HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded }) return true;
            if (current is HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded }) return true;
        }
        return false;
    }
}
