using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
namespace VpnMonitor.Client.Services;

// Each GUI starts its own resume process before exiting, preserving its user and RDP session.
public sealed class UpdateParticipant : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    public static string InstallRoot
    {
        get
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\VpnMonitor");
            return key?.GetValue("InstallRoot") as string ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"VpnMonitor");
        }
    }
    public UpdateParticipant(string role, Action shutdown, Action<Guid>? prepareResume = null)
    {
        if (role is not ("Client" or "Manager")) throw new ArgumentException(nameof(role));
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var security = new PipeSecurity();
                    foreach(var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null) })
                        security.AddAccessRule(new PipeAccessRule(sid,PipeAccessRights.FullControl,AccessControlType.Allow));
                    using var pipe = NamedPipeServerStreamAcl.Create($"VpnMonitor.Update.{Environment.ProcessId}",PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,security);
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    var administrator = false;
                    pipe.RunAsClient(() => administrator = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));
                    if (!administrator) continue;
                    using var reader = new StreamReader(pipe,leaveOpen:true);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var line = await reader.ReadLineAsync(timeout.Token);
                    if (!Guid.TryParseExact(line,"N",out var id)) continue;
                    if (prepareResume is not null) prepareResume(id);
                    else
                    {
                    var script = Path.Combine(InstallRoot,"Setup","Resume-AfterUpdate.ps1");
                    if (!File.Exists(script)) throw new FileNotFoundException("Resume helper missing");
                    var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe"))
                    { UseShellExecute=false, CreateNoWindow=true, Arguments=$"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -UpdateId {id:N} -Role {role} -OriginalPid {Environment.ProcessId}" };
                    using var resume = Process.Start(info) ?? throw new IOException("Cannot start resume helper");
                    }
                    using var writer = new StreamWriter(pipe,leaveOpen:true) { AutoFlush=true };
                    await writer.WriteLineAsync("ready");
                    shutdown();
                    return;
                }
                catch (OperationCanceledException) { return; }
                catch { try { await Task.Delay(1000,_stop.Token); } catch (OperationCanceledException) { return; } }
            }
        });
    }
    public void Dispose() => _stop.Cancel();
}
