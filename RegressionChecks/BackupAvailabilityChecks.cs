using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;
internal static class BackupAvailabilityChecks
{
    public static void Run()
    {
        var now=DateTimeOffset.UtcNow;var options=new AwgConnectionOptions{BackupEndpoint="192.0.2.2:51820"};var connection=new AwgConnectionState("Connected",true,0,now.AddMinutes(-1),now,1,"192.0.2.1:51820",Healthy:true,SupportsManualTest:true,ManualTestAvailable:true);
        AwgBackupAvailability Evaluate(uint state=4,string? selected="active",string? active="active",AwgConnectionOptions? policy=null,AwgConnectionState? status=null)=>AwgBackupTestAvailability.Evaluate(state,selected,active,policy??options,status??connection,now);
        void Reason(AwgBackupAvailability value,string expected){if(value.Available||!value.Explanation.Contains(expected))throw new Exception("Wrong backup test reason: "+value.Explanation);}
        if(!Evaluate().Available)throw new Exception("Healthy backup test unavailable");Reason(Evaluate(selected:null),"Выберите");Reason(Evaluate(state:1),"Подключите");Reason(Evaluate(selected:"other"),"текущего");Reason(Evaluate(policy:new AwgConnectionOptions{Enabled=false,BackupEndpoint=options.BackupEndpoint}),"автовосстановление");Reason(Evaluate(policy:new AwgConnectionOptions{BackupEndpoint=""}),"Endpoint");Reason(Evaluate(status:connection with{SupportsManualTest=false}),"движок");Reason(Evaluate(status:connection with{ManualTest="Returning"}),"уже выполняется");Reason(Evaluate(status:connection with{State="Backup"}),"через резерв");Reason(Evaluate(status:connection with{Healthy=false}),"подтверждённой");Reason(Evaluate(status:connection with{StartedAt=now.AddSeconds(-5)}),"30 секунд");Reason(Evaluate(status:connection with{ManualTestAvailable=false}),"бюджет");
        Reason(AwgBackupTestAvailability.Evaluate(4,"active","active",options,null,now),"свежего");
        Console.WriteLine("PASS: backup test explanations for selection, service, configuration, stale/old engine, active test, reserve, health, warm-up and budget.");
    }
}
