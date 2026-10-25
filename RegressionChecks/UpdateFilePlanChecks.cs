using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VpnMonitor.Client.Services;
internal static class UpdateFilePlanChecks
{
    public static async Task RunAsync()
    {
        var directory=Path.Combine(Path.GetTempPath(),"UpdatePlan-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(directory,"Client"));
        try {
            var path=Path.Combine(directory,"Client","fixture.dll");await File.WriteAllTextAsync(path,"first");
            var entry=new UpdateFileEntry("Client/fixture.dll",5,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("first"))).ToLowerInvariant());
            var version=new Version(0,1,75);var commit=new string('a',40);
            string Json(UpdateFileEntry[] files)=>JsonSerializer.Serialize(new {Schema=1,Version=version.ToString(),SourceCommit=commit,Files=files});
            var manifestJson=Json([entry]);var manifest=UpdateFileManifest.Parse(manifestJson,version);
            var plan=await manifest.CompareAsync(directory,default);Require(plan.Unchanged==1&&plan.ChangedBytes==0,"Matching local files must be reused.");
            await File.WriteAllTextAsync(path,"other");plan=await manifest.CompareAsync(directory,default);Require(plan.ChangedBytes==5&&plan.Unchanged==0,"Same-size corruption must change the plan.");
            File.Delete(path);Require((await manifest.CompareAsync(directory,default)).Changed.Length==1,"Missing files must change the plan.");
            foreach(var value in new[]{"../escape","Client/../escape","Client/C:escape","Client\\escape","Client/CON","Client/file:stream","Client/trailing.","Client//empty","Setup/LPT1.txt","Client/\nfile"})
                Require(!UpdateFileManifest.IsSafePath(value),"Unsafe manifest path accepted: "+value);
            foreach(var invalid in new[]{Json([entry,entry with{Path="Client/FIXTURE.dll"}]),Json([entry with{Size=-1}]),Json([entry with{Sha256="bad"}]),Json([entry with{Path="Client/../escape"}])}) {
                try { UpdateFileManifest.Parse(invalid,version);throw new Exception("Malformed manifest accepted"); }catch(InvalidDataException){}
            }
            using(var cancel=new CancellationTokenSource()){cancel.Cancel();try{await manifest.CompareAsync(directory,cancel.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}}
            var bytes=Encoding.UTF8.GetBytes(manifestJson);var hash="sha256:"+Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string Release(string digest,string url,string source)=>JsonSerializer.Serialize(new {id=123,tag_name="v0.1.75",draft=false,prerelease=false,target_commitish=source,
                assets=new object[]{new {name="VpnMonitor-Setup-0.1.75-win-x64.exe",digest="sha256:"+new string('0',64),browser_download_url="https://github.com/ilexandr/vpn-monitor/releases/download/v0.1.75/VpnMonitor-Setup-0.1.75-win-x64.exe"},
                new {name="file-manifest.json",state="uploaded",digest,size=bytes.Length,browser_download_url=url}}});
            const string uri="https://github.com/ilexandr/vpn-monitor/releases/download/v0.1.75/file-manifest.json";
            var release=new UpdateRelease(123,version,"");
            plan=await UpdateService.GetFilePlanAsync(release,directory,default,new Handler(Release(hash,uri,commit),bytes));Require(plan.Changed.Length==1,"A trusted manifest must produce a read-only plan.");
            foreach(var json in new[]{Release("sha256:"+new string('0',64),uri,commit),Release(hash,"https://example.test/file-manifest.json",commit),Release(hash,uri,new string('b',40)),Release(hash,uri.Replace("v0.1.75","v0.1.74"),commit)}) {
                try{await UpdateService.GetFilePlanAsync(release,directory,default,new Handler(json,bytes));throw new Exception("Untrusted manifest accepted");}catch(InvalidDataException){}
            }
            Require(!Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories).Any(),"The planner must not create or install files.");
        } finally {foreach(var file in Directory.GetFiles(Path.Combine(directory,"Client")))File.Delete(file);Directory.Delete(Path.Combine(directory,"Client"));Directory.Delete(directory);}
        Console.WriteLine("PASS: authenticated GitHub manifest source/version/digest, safe paths, local corruption/missing files, cancellation and read-only update plans.");
    }
    public static async Task RunLiveAsync()
    {
        var release=await UpdateService.CheckAsync(default) ?? throw new Exception("No live release");
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"VpnMonitor");
        var plan=await UpdateService.GetFilePlanAsync(release,root,default);
        Console.WriteLine($"PASS: live GitHub manifest {release.Version}, verified size/digest/commit; matching={plan.Unchanged}, changed={plan.Changed.Length}; no files modified.");
    }
    private sealed class Handler(string release,byte[] bytes):HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation) {
            Require(request.RequestUri?.Host is "api.github.com" or "github.com","Unexpected manifest request host.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=request.RequestUri!.Host=="api.github.com"?new StringContent(release):new ByteArrayContent(bytes)});
        }
    }
    private static void Require(bool value,string message){if(!value)throw new Exception(message);}
}
