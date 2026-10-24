using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace VpnMonitor.Client.Services;

public interface ISmbPortProbe
{
    Task<bool> IsAvailableAsync(string address, CancellationToken token);
}
public interface INetworkShareConnector
{
    bool IsConnected(string remote);
    int Connect(IntPtr owner, string remote, bool prompt);
}

public sealed class SmbPortProbe : ISmbPortProbe
{
    public async Task<bool> IsAvailableAsync(string address, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Parse(address), 445, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }
}

public sealed class WindowsNetworkShareConnector : INetworkShareConnector
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope, Type, DisplayType, Usage;
        public string? LocalName, RemoteName, Comment, Provider;
    }

    [DllImport("mpr.dll", EntryPoint = "WNetAddConnection3W", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AddConnection(IntPtr owner, ref NetResource resource, string? password, string? username, int flags);

    [DllImport("mpr.dll", EntryPoint = "WNetGetUserW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetUser(string remote, StringBuilder username, ref int length);

    public bool IsConnected(string remote)
    {
        var length = 512;
        var code = GetUser(remote, new StringBuilder(length), ref length);
        return code is 0 or 234; // ERROR_MORE_DATA also proves a connection exists.
    }

    public int Connect(IntPtr owner, string remote, bool prompt)
    {
        var resource = new NetResource
        {
            Scope = 0, Type = 0, DisplayType = 0, Usage = 0,
            LocalName = null, RemoteName = remote, Comment = null, Provider = null
        };
        const int interactive = 0x08;
        const int promptCredentials = 0x10;
        // Credentials are handled entirely by the Windows provider. No drive letter or persistent profile.
        return AddConnection(owner, ref resource, null, null, interactive | (prompt ? promptCredentials : 0));
    }
}

public sealed class NetworkShareService(ISmbPortProbe? probe = null, INetworkShareConnector? connector = null)
{
    private readonly ISmbPortProbe _probe = probe ?? new SmbPortProbe();
    private readonly INetworkShareConnector _connector = connector ?? new WindowsNetworkShareConnector();

    public async Task<bool> ConnectAsync(string address, string? share, IntPtr owner,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var requested = NetworkSharePath.Create(address, share);
        var authenticationTarget = NetworkSharePath.Create(address, "IPC$");
        token.ThrowIfCancellationRequested();
        progress?.Report($"Проверка SMB: {address}:445...");
        if (!await _probe.IsAvailableAsync(address, token))
            throw new IOException("SMB-порт 445 недоступен. Проверьте туннель, службу общего доступа и брандмауэр удалённого компьютера.");

        return await OnStaThread(() =>
        {
            token.ThrowIfCancellationRequested();
            if (_connector.IsConnected(authenticationTarget) || _connector.IsConnected(requested))
            {
                progress?.Report("Используется существующее сетевое подключение Windows.");
                return true;
            }
            token.ThrowIfCancellationRequested();
            progress?.Report("Подключение SMB: ожидайте стандартное окно учётных данных Windows...");
            var code = _connector.Connect(owner, authenticationTarget, prompt: true);
            token.ThrowIfCancellationRequested();
            if (code == 1223) return false;
            if (code != 0) throw new Win32Exception(code, DescribeError(code));
            return true;
        });
    }

    private static Task<T> OnStaThread<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "SMB authentication" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public static string DescribeError(int code) => code switch
    {
        5 => "Windows отказала в доступе. Для C$ и ADMIN$ обычно нужны права администратора удалённого компьютера.",
        86 or 1326 => "Windows не приняла сетевые учётные данные. Используйте учётную запись удалённого компьютера или домена.",
        1219 => "К этому компьютеру уже есть подключение с другой учётной записью. Завершите ненужное подключение средствами Windows и повторите. Приложение не разрывает существующие подключения автоматически.",
        53 or 67 => "Сетевой путь или общая папка недоступны. Проверьте VPN и имя общей папки.",
        _ => new Win32Exception(code).Message
    };
}