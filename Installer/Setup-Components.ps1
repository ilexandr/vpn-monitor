param(
 [Parameter(Mandatory=$true)][ValidateSet('Preflight','Install','PrepareInstall','RestoreInstall','PrepareRemove','Remove')][string]$Phase,
 [Parameter(Mandatory=$true)][string]$Root,
 [string]$DiagnosticPath,
 [string]$SetupSource,
 [ValidatePattern('^[a-f0-9]{32}$')][string]$SessionId,
 [int]$SetupPid,
 [string]$LaunchStatePath
)
$ErrorActionPreference='Stop'
$serviceName='VpnMonitor.AwgPrototype'
$expected=[IO.Path]::GetFullPath($Root).TrimEnd('\')
$registered=Get-ItemProperty 'HKLM:\Software\VpnMonitor' -ErrorAction SilentlyContinue
$previous=$registered.InstallRoot
if(!$previous){
 $uninstall=Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{3A7D1DC4-7D80-4B3B-B17D-0A69BFD956CC}_is1' -ErrorAction SilentlyContinue
 $previous=$uninstall.InstallLocation
}
if(!$previous){$previous=Join-Path $env:ProgramFiles 'VpnMonitor'}
$previous=[IO.Path]::GetFullPath($previous).TrimEnd('\')
$serviceRoot=Join-Path $expected 'Client'
$dataRoot=Join-Path $env:ProgramData 'VpnMonitor\AwgPrototype'
$publicRoot=Join-Path $env:ProgramData 'VpnMonitor\AwgStatus'
$session=$null
function Write-SetupSession([string]$phase){
 $session.Phase=$phase
 $path=Join-Path $publicRoot ('update-'+$SessionId+'.json')
 [IO.File]::WriteAllText(($path+'.tmp'),($session | ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
 Move-Item -LiteralPath ($path+'.tmp') -Destination $path -Force
}
function Assert-NoReparse([string]$path){
 $current=$path
 while($current){
  if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse point in installation path'}
  $next=Split-Path $current -Parent
  if($next -eq $current){break};$current=$next
 }
}
function Assert-Idle{
 $service=Get-Service -Name $serviceName -ErrorAction SilentlyContinue
 if($service -and $service.Status -ne 'Stopped'){throw 'Disconnect the shared AWG tunnel before setup; all sessions are affected.'}
 $busy=Get-CimInstance Win32_Process | Where-Object {
  $_.Name -in @('VpnMonitor.Client.exe','AwgPrototype.Manager.exe','AwgPrototype.Host.exe')
 }
 if($busy){throw 'Close VPN Monitor and AWG Manager in all Windows sessions before setup.'}
}
function Protect-Directory([string]$path,[bool]$readUsers){
 Assert-NoReparse $path
 New-Item -ItemType Directory -Force -Path $path | Out-Null
 $acl=[Security.AccessControl.DirectorySecurity]::new()
 $acl.SetAccessRuleProtection($true,$false)
 foreach($sid in @('S-1-5-18','S-1-5-32-544')){
  $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
 }
 if($readUsers){
  $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-11'),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
 }
 $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
 Set-Acl -LiteralPath $path -AclObject $acl
}
try{
 $drive=[IO.DriveInfo]::new([IO.Path]::GetPathRoot($expected))
 if($drive.DriveType -ne 'Fixed' -or $expected -eq $drive.RootDirectory.FullName.TrimEnd('\')){throw 'Choose a subfolder on a local fixed drive'}
 foreach($reserved in @($env:WINDIR,$env:ProgramData,$env:ProgramFiles,${env:ProgramFiles(x86)})){
  if($expected -eq $reserved -or $expected.StartsWith($env:WINDIR+'\',[StringComparison]::OrdinalIgnoreCase) -or $expected.StartsWith($env:ProgramData+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Choose an application folder outside Windows and ProgramData'}
 }
 $existing=Get-CimInstance Win32_Service -Filter "Name='VpnMonitor.AwgPrototype'"
 if($existing){
  $accepted=@()
  foreach($base in @($previous,$expected,(Join-Path $env:ProgramFiles 'VpnMonitor'))){
   foreach($folder in @('AwgPrototype','Client')){
    $accepted+='"'+(Join-Path $base ($folder+'\AwgPrototype.Host.exe'))+'" --run-service "'+(Join-Path $dataRoot 'profile.dpapi')+'"'
   }
  }
  if($existing.StartName -ne 'LocalSystem' -or $existing.PathName -notin $accepted){throw 'Existing service configuration differs; refusing replacement'}
 }
 Assert-NoReparse $expected;Assert-NoReparse $dataRoot;Assert-NoReparse $publicRoot
 if($Phase -eq 'RestoreInstall'){
  if(!$SessionId){throw 'Missing setup session'}
  $path=Join-Path $publicRoot ('update-'+$SessionId+'.json')
  if(!(Test-Path -LiteralPath $path)){exit 0}
  $session=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
  if($session.Id -ne $SessionId -or $session.WorkerPid -ne $SetupPid){throw 'Setup session mismatch'}
  if($session.Phase -in @('Completed','Failed')){exit 0}
  try{
   . (Join-Path $PSScriptRoot 'Service-Recovery.ps1')
   Restore-MonitorTunnel ([bool]$session.ServiceWasRunning) $existing.PathName (Join-Path $dataRoot ('setup-'+$SessionId+'.jsonl'))
   Write-SetupSession 'Completed'
  }catch{Write-SetupSession 'Failed';throw}
  exit 0
 }
 if($Phase -in @('PrepareRemove','PrepareInstall')){
  if($Phase -eq 'PrepareInstall'){
   if(!$SessionId -or $SetupPid -le 0){throw 'Missing setup session identity'}
   Protect-Directory (Join-Path $env:ProgramData 'VpnMonitor') $false
   Protect-Directory $dataRoot $false
   Protect-Directory $publicRoot $true
   $sessionPath=Join-Path $publicRoot ('update-'+$SessionId+'.json')
   if(Test-Path -LiteralPath $sessionPath){
    $session=Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json
    if($session.Id -ne $SessionId -or $session.WorkerPid -ne $SetupPid -or $session.Phase -ne 'Preparing'){throw 'Setup session cannot be reused'}
   }else{
    $running=(Get-Service -Name $serviceName -ErrorAction SilentlyContinue)
    if($running -and $running.Status -notin @('Running','Stopped')){throw 'Wait until the tunnel finishes changing state'}
    $session=@{Id=$SessionId;Phase='Preparing';Message='Standalone setup';WorkerPid=$SetupPid;InstallerPid=$SetupPid;ClientSessions=@();ServiceWasRunning=[bool]($running -and $running.Status -eq 'Running')}
    Write-SetupSession 'Preparing'
   }
   $setupSession=(Get-Process -Id $SetupPid -ErrorAction Stop).SessionId
   if($LaunchStatePath){[IO.File]::WriteAllText($LaunchStatePath,('[Launch]'+[Environment]::NewLine+'RestartClient='+[int]($setupSession -in $session.ClientSessions)),[Text.Encoding]::ASCII)}
  }
  $processes=@(Get-CimInstance Win32_Process | Where-Object {
   $_.ExecutablePath -and ($_.ExecutablePath.StartsWith($expected+'\',[StringComparison]::OrdinalIgnoreCase) -or $_.ExecutablePath.StartsWith($previous+'\',[StringComparison]::OrdinalIgnoreCase)) -and
   $_.Name -in @('VpnMonitor.Client.exe','AwgPrototype.Manager.exe')
  })
  foreach($process in $processes){
   if(!(Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue)){continue}
   $pipe=[IO.Pipes.NamedPipeClientStream]::new('.','VpnMonitor.Update.'+$process.ProcessId,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous,[Security.Principal.TokenImpersonationLevel]::Impersonation)
   try{
    $pipe.Connect(10000)
    $writer=[IO.StreamWriter]::new($pipe);$writer.AutoFlush=$true
    $reader=[IO.StreamReader]::new($pipe)
    if($Phase -eq 'PrepareInstall'){$writer.WriteLine($SessionId)}else{$writer.WriteLine('remove|'+[Guid]::NewGuid().ToString('N'))}
    $ack=$reader.ReadLineAsync()
    if(!$ack.Wait(15000) -or $ack.Result -ne 'ready'){throw 'Cannot prepare application for uninstall'}
    if($Phase -eq 'PrepareInstall' -and $process.Name -eq 'VpnMonitor.Client.exe'){
     $session.ClientSessions=@($session.ClientSessions)+@($process.SessionId) | Select-Object -Unique
     Write-SetupSession 'Preparing'
     if($LaunchStatePath -and $process.SessionId -eq $setupSession){[IO.File]::WriteAllText($LaunchStatePath,('[Launch]'+[Environment]::NewLine+'RestartClient=1'),[Text.Encoding]::ASCII)}
    }
   }finally{$pipe.Dispose()}
   $old=Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
   if($old -and !$old.WaitForExit(20000)){throw 'Application is busy; uninstall cancelled'}
  }
  $service=Get-Service -Name $serviceName -ErrorAction SilentlyContinue
  if($service -and $service.Status -ne 'Stopped'){
   Stop-Service -Name $serviceName
   (Get-Service $serviceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(40))
  }
  if($existing -and $existing.ProcessId){
   $hostProcess=Get-Process -Id $existing.ProcessId -ErrorAction SilentlyContinue
   if($hostProcess -and !$hostProcess.WaitForExit(10000)){throw 'Tunnel host has not exited'}
  }
  Assert-Idle
  exit 0
 }
 Assert-Idle
 if($Phase -eq 'Preflight'){Write-Output 'Preflight passed';exit 0}
 if($Phase -eq 'Remove'){
  if(Get-Service $serviceName -ErrorAction SilentlyContinue){
   & sc.exe delete $serviceName | Out-Null
   if($LASTEXITCODE -ne 0){throw 'Cannot remove service'}
  }
  # Deliberately retain encrypted machine profiles and every user's settings.
  exit 0
 }
 Protect-Directory $expected $true
 Protect-Directory $serviceRoot $true
 Protect-Directory (Join-Path $env:ProgramData 'VpnMonitor') $false
 Protect-Directory $dataRoot $false
 Protect-Directory $publicRoot $true
 $catalog=Join-Path $publicRoot 'profiles.json'
 if(!(Test-Path -LiteralPath $catalog)){
  [IO.File]::WriteAllText($catalog,'{"Version":1,"SelectedId":null,"Profiles":[]}')
 }
 $binary='"'+(Join-Path $serviceRoot 'AwgPrototype.Host.exe')+'" --run-service "'+(Join-Path $dataRoot 'profile.dpapi')+'"'
 $startupKind='new'
 if(Get-Service $serviceName -ErrorAction SilentlyContinue){
  $startupKind='existing'
  $existing=Get-CimInstance Win32_Service -Filter "Name='VpnMonitor.AwgPrototype'"
  $change=Invoke-CimMethod -InputObject $existing -MethodName Change -Arguments @{PathName=$binary}
  if($change.ReturnValue -ne 0){throw ('Cannot update service location: '+$change.ReturnValue)}
 }else{
  New-Service -Name $serviceName -BinaryPathName $binary -DisplayName 'VPN Monitor AWG' -StartupType Manual |
   Out-Null
 }
 & sc.exe sidtype $serviceName unrestricted | Out-Null
 if($LASTEXITCODE -ne 0){throw 'Cannot configure service SID'}
 & sc.exe sdset $serviceName 'D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;LC;;;AU)' | Out-Null
 if($LASTEXITCODE -ne 0){throw 'Cannot configure service permissions'}
 # Migrate pre-0.1.77 profiles once; later upgrades preserve the chosen SCM start mode.
 $startup=Start-Process -FilePath (Join-Path $serviceRoot 'AwgPrototype.Manager.exe') -ArgumentList @('startup-setup',$startupKind) -WindowStyle Hidden -Wait -PassThru
 if($startup.ExitCode -ne 0){throw ('Cannot configure AWG startup/recovery: '+$startup.ExitCode)}
 # Preserve an existing key: New-Item -Force can delete it while setup holds it open.
 $baseKey=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
 try{
  $installKey=$baseKey.CreateSubKey('Software\VpnMonitor',$true)
  try{$installKey.SetValue('InstallRoot',$expected,[Microsoft.Win32.RegistryValueKind]::String)}finally{$installKey.Dispose()}
 }finally{$baseKey.Dispose()}
 # Bind recovery to the actual source EXE in an administrator-only update stage.
 # Elevation can reparent setup processes, so process ancestry alone is unreliable.
 foreach($journal in Get-ChildItem -LiteralPath $publicRoot -Filter 'update-*.json' -ErrorAction SilentlyContinue){
  try{
   $update=Get-Content -LiteralPath $journal.FullName -Raw | ConvertFrom-Json
   $id=[Guid]::Empty
   if(!$SetupSource -or $update.Phase -ne 'Installing' -or !$update.ServiceWasRunning -or !$update.InstallerPid -or ![Guid]::TryParseExact($update.Id,'N',[ref]$id)){continue}
   $updateStage=Join-Path $env:ProgramData ('VpnMonitor\Updates\'+$id.ToString('N'))
   if(!(Test-Path -LiteralPath $updateStage)){continue}
   $version=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $serviceRoot 'VpnMonitor.Client.exe'))
   $expectedSource=Join-Path $updateStage ('VpnMonitor-Setup-'+$version.FileMajorPart+'.'+$version.FileMinorPart+'.'+$version.FileBuildPart+'-win-x64.exe')
   if([IO.Path]::GetFullPath($SetupSource) -ne $expectedSource -or !(Get-Process -Id $update.InstallerPid -ErrorAction SilentlyContinue) -or !(Get-Process -Id $update.WorkerPid -ErrorAction SilentlyContinue)){continue}
   . (Join-Path $PSScriptRoot 'Service-Recovery.ps1')
   Restore-MonitorTunnel $true $binary (Join-Path $updateStage 'installer-service-recovery.jsonl')
  }catch{Write-Warning ('Tunnel recovery diagnostics: '+$_.Exception.Message)}
 }
 Write-Output 'Installed. User profiles retained.'
 exit 0
}catch{
 if($DiagnosticPath){[IO.File]::WriteAllText($DiagnosticPath, $_.Exception.Message, [Text.Encoding]::UTF8)}
 Write-Error $_.Exception.Message -ErrorAction Continue
 exit 20
}
