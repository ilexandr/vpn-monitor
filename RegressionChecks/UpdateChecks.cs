using System.IO.Pipes;
using System.IO;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using VpnMonitor.Client.Services;
internal static class UpdateChecks
{
    public static async Task RunAdminIpcAsync(bool removing = false)
    {
        var prepared=false;
        var received=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var participant=new UpdateParticipant("Client",()=>received.TrySetResult(),_=>prepared=true);
        var script=Path.Combine(Environment.CurrentDirectory,"artifacts","installer","update-ipc-admin.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script,"""
param([int]$TargetPid,[switch]$Removing)
$ErrorActionPreference='Stop'
$pipe=[IO.Pipes.NamedPipeClientStream]::new('.','VpnMonitor.Update.'+$TargetPid,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous,[Security.Principal.TokenImpersonationLevel]::Impersonation)
try{
 $pipe.Connect(10000)
 $writer=[IO.StreamWriter]::new($pipe);$writer.AutoFlush=$true
 $reader=[IO.StreamReader]::new($pipe)
 $request=[Guid]::NewGuid().ToString('N'); if($Removing){$request='remove|'+$request}; $writer.WriteLine($request)
 if($reader.ReadLine() -ne 'ready'){exit 2}
}finally{$pipe.Dispose()}
exit 0
""");
        var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")) {
            UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,
            Arguments=$"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -TargetPid {Environment.ProcessId} {(removing ? "-Removing" : "")}"
        };
        using var child=Process.Start(info) ?? throw new Exception("IPC test could not start");
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if(child.ExitCode!=0 || prepared == removing) throw new Exception("Elevated IPC failed");
        Console.WriteLine("PASS: elevated cross-token pipe authentication, resume preparation and shutdown acknowledgement (no application or tunnel stopped).");
    }
    public static async Task RunAsync()
    {
        string Release(string tag="v9.8.7",bool draft=false,bool prerelease=false,string? digest=null,string? url=null) => JsonSerializer.Serialize(new {
            id=123L,tag_name=tag,draft,prerelease,body="Примечания к выпуску",
            assets=new[] { new { name="VpnMonitor-Setup-9.8.7-win-x64.exe",digest=digest ?? "sha256:"+new string('a',64),browser_download_url=url ?? "https://github.com/ilexandr/vpn-monitor/releases/download/v9.8.7/VpnMonitor-Setup-9.8.7-win-x64.exe" } }
        });
        var release=UpdateService.Parse(Release());
        if(release.Version!=new Version(9,8,7) || release.Id!=123 || !release.Notes.Contains("выпуску")) throw new Exception("Release parsing failed");
        void Reject(string json)
        {
            try { UpdateService.Parse(json); }
            catch(Exception ex) when(ex is InvalidDataException or FormatException or InvalidOperationException) { return; }
            throw new Exception("Unsafe release accepted");
        }
        Reject(Release(draft:true)); Reject(Release(prerelease:true)); Reject(Release(tag:"v9.8.7-beta"));
        Reject(Release(digest:"sha256:bad")); Reject(Release(url:"https://example.com/setup.exe"));
        Reject(Release(url:"http://github.com/ilexandr/vpn-monitor/releases/download/v9.8.7/setup.exe"));
        Reject(Release(url:"https://github.com/another/repository/releases/download/v9.8.7/setup.exe"));
        Console.WriteLine("PASS: stable release version, Unicode notes, draft/prerelease rejection, checksum and repository URL validation.");
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            var shutdown=false;
            using var participant=new UpdateParticipant("Client",()=>shutdown=true);
            using var pipe=new NamedPipeClientStream(".",$"VpnMonitor.Update.{Environment.ProcessId}",PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Impersonation);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await pipe.ConnectAsync(timeout.Token);
            try
            {
                using var writer=new StreamWriter(pipe,leaveOpen:true) { AutoFlush=true };
                await writer.WriteLineAsync(Guid.NewGuid().ToString("N"));
                using var reader=new StreamReader(pipe,leaveOpen:true);
                var response=await reader.ReadLineAsync(timeout.Token);
                if(response=="ready") throw new Exception("Unelevated update request was accepted");
            }
            catch(IOException) { }
            if(shutdown) throw new Exception("Unelevated request closed the client");
            Console.WriteLine("PASS: unelevated IPC shutdown rejected; no restart helper launched.");
        }
    }
}
