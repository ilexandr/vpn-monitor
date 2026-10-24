function Restore-MonitorTunnel([bool]$wasRunning,[string]$expectedBinary,[string]$logPath){
 if(!$wasRunning){return}
 $lastFailure=''
 for($attempt=1;$attempt -le 3;$attempt++){
  try{
   $snapshot=Get-CimInstance Win32_Service -Filter "Name='VpnMonitor.AwgPrototype'"
   if(!$snapshot -or $snapshot.PathName -ne $expectedBinary -or $snapshot.StartName -ne 'LocalSystem'){throw 'Unexpected service configuration during restoration'}
   if($snapshot.State -eq 'Stop Pending'){
    (Get-Service VpnMonitor.AwgPrototype).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(25))
    $snapshot=Get-CimInstance Win32_Service -Filter "Name='VpnMonitor.AwgPrototype'"
   }
   if($snapshot.State -eq 'Stopped'){
    $result=Invoke-CimMethod -InputObject $snapshot -MethodName StartService
    if($result.ReturnValue -notin @(0,10)){throw ('StartService returned '+$result.ReturnValue)}
   }
   (Get-Service VpnMonitor.AwgPrototype).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
   Start-Sleep -Seconds 2
   if((Get-Service VpnMonitor.AwgPrototype).Status -ne 'Running'){throw 'Service stopped immediately after startup'}
   @{Attempt=$attempt;Result='Running';Time=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json -Compress | Add-Content -LiteralPath $logPath -Encoding UTF8
   return
  }catch{
   $lastFailure=$_.Exception.Message
   $snapshot=Get-CimInstance Win32_Service -Filter "Name='VpnMonitor.AwgPrototype'" -ErrorAction SilentlyContinue
   $details=@{Attempt=$attempt;Error=$lastFailure;Time=[DateTime]::UtcNow.ToString('o');State=$snapshot.State;StartMode=$snapshot.StartMode;ExitCode=$snapshot.ExitCode;ServiceSpecificExitCode=$snapshot.ServiceSpecificExitCode}
   $details | ConvertTo-Json -Compress | Add-Content -LiteralPath $logPath -Encoding UTF8
   if($snapshot -and ($snapshot.PathName -ne $expectedBinary -or $snapshot.StartName -ne 'LocalSystem')){throw $lastFailure}
   if($attempt -lt 3){Start-Sleep -Seconds (2*$attempt)}
  }
 }
 throw ('Cannot restore tunnel after 3 attempts: '+$lastFailure+'; Win32Exit='+$snapshot.ExitCode+'; ServiceExit='+$snapshot.ServiceSpecificExitCode+'; diagnostics: '+$logPath)
}
