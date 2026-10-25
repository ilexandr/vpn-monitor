function Test-MonitorTunnelConnection([int]$processId,[int]$timeoutSeconds=120){
 if($processId -le 0){return $false}
 $path=Join-Path $env:ProgramData 'VpnMonitor\AwgStatus\connection.json'
 $deadline=[DateTime]::UtcNow.AddSeconds($timeoutSeconds)
 do {
  try {
   $connection=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
   $age=([DateTimeOffset]::UtcNow-[DateTimeOffset]::Parse($connection.CheckedAt)).TotalSeconds
   if($connection.ProcessId -eq $processId -and $age -ge -5 -and $age -lt 30 -and $connection.State -ne 'Stopped' -and
       $connection.Healthy){return $true}
  } catch { }
  if([DateTime]::UtcNow -ge $deadline){break}
  if(('System.Windows.Forms.Application' -as [type])){[System.Windows.Forms.Application]::DoEvents()}
  Start-Sleep -Seconds 2
 } while($true)
 return $false
}
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
   $healthySnapshot=Get-CimInstance Win32_Service -Filter "Name='VpnMonitor.AwgPrototype'"
   if(!(Test-MonitorTunnelConnection $healthySnapshot.ProcessId)){throw 'Service is Running but VPN connectivity/handshake was not confirmed'}
   @{Attempt=$attempt;Result='Connected';Time=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json -Compress | Add-Content -LiteralPath $logPath -Encoding UTF8
   return
  }catch{
   $lastFailure=$_.Exception.Message
   $snapshot=Get-CimInstance Win32_Service -Filter "Name='VpnMonitor.AwgPrototype'" -ErrorAction SilentlyContinue
   $details=@{Attempt=$attempt;Error=$lastFailure;Time=[DateTime]::UtcNow.ToString('o');State=$snapshot.State;StartMode=$snapshot.StartMode;ExitCode=$snapshot.ExitCode;ServiceSpecificExitCode=$snapshot.ServiceSpecificExitCode}
   $details | ConvertTo-Json -Compress | Add-Content -LiteralPath $logPath -Encoding UTF8
   if($snapshot -and ($snapshot.PathName -ne $expectedBinary -or $snapshot.StartName -ne 'LocalSystem')){throw $lastFailure}
   if($attempt -lt 3){
    if($snapshot.State -eq 'Running'){
     Stop-Service -Name VpnMonitor.AwgPrototype
     (Get-Service VpnMonitor.AwgPrototype).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(40))
     if($snapshot.ProcessId){$oldHost=Get-Process -Id $snapshot.ProcessId -ErrorAction SilentlyContinue;if($oldHost -and !$oldHost.WaitForExit(10000)){throw 'Previous tunnel process did not exit'}}
    }
    Start-Sleep -Seconds (2*$attempt)
   }
  }
 }
 throw ('Cannot restore tunnel after 3 attempts: '+$lastFailure+'; Win32Exit='+$snapshot.ExitCode+'; ServiceExit='+$snapshot.ServiceSpecificExitCode+'; diagnostics: '+$logPath)
}
