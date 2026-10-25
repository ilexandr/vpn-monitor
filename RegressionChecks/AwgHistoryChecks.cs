using System.IO;
using System.Text.Json;
using VpnMonitor.Client.Services;
internal static class AwgHistoryChecks
{
    public static void Run()
    {
        var file=Path.Combine(Path.GetTempPath(),"AwgHistory-"+Guid.NewGuid().ToString("N")+".json");
        try {
            var now=DateTimeOffset.Now;
            var rows=Enumerable.Range(0,250).Select(i=>new AwgConnectionEvent(now.AddSeconds(i-250),"Connected","192.0.2.1:51820","Observed","",i)).ToArray();
            File.WriteAllText(file,JsonSerializer.Serialize(rows));var history=AwgConnectionHistory.Read(file);
            if(history.Length!=200 || history[0].ProcessId!=249 || history[^1].ProcessId!=50)throw new Exception("History must remain bounded and newest first.");
            File.WriteAllText(file,"invalid");if(AwgConnectionHistory.Read(file).Length!=0)throw new Exception("Broken history must not crash the manager.");
            var result=new AwgConnectionEvent(now,"BackupTestResult","192.0.2.2:51820","BackupPassed","",1);
            if(!result.Description.Contains("входящий трафик"))throw new Exception("History must distinguish verified traffic from a switch attempt.");
            var legacy=JsonSerializer.Deserialize<AwgConnectionState>("{\"State\":\"Connected\",\"Healthy\":true}")!;
            if(legacy.SupportsManualTest || legacy.IsManualTestActive)throw new Exception("An old engine must never enable the manual test command.");
        } finally {File.Delete(file);}
        Console.WriteLine("PASS: bounded persistent AWG history, malformed data, verified backup result and old-engine capability safety.");
    }
}
