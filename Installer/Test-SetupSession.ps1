$ErrorActionPreference='Stop'
$fixture=Join-Path (Split-Path $PSScriptRoot) 'artifacts\installer\setup-session-fixture'
New-Item -ItemType Directory -Force -Path $fixture | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Setup-Components.ps1') -Destination $fixture
@'
function Restore-MonitorTunnel([bool]$wasRunning,[string]$expectedBinary,[string]$logPath){
 if($wasRunning){$global:restores++;$global:serviceState='Running'}
}
'@ | Set-Content -LiteralPath (Join-Path $fixture 'Service-Recovery.ps1') -Encoding UTF8
@'
param([string]$Fixture,[string]$Scenario)
$ErrorActionPreference='Stop'
$env:ProgramData=Join-Path $Fixture $Scenario
$global:stops=0;$global:restores=0
$global:serviceState=if($Scenario -eq 'Stopped'){'Stopped'}else{'Running'}
function Get-ItemProperty {param($Path,$ErrorAction) [pscustomobject]@{InstallRoot='E:\Apps\VPN Monitor'}}
function Set-Acl {param($LiteralPath,$AclObject)}
function Get-Process {param($Id,$ErrorAction) [pscustomobject]@{SessionId=7}}
function Get-CimInstance {
 param($ClassName,$Filter,$ErrorAction)
 if($ClassName -eq 'Win32_Service'){
  $path='"E:\Apps\VPN Monitor\Client\AwgPrototype.Host.exe" --run-service "'+$env:ProgramData+'\VpnMonitor\AwgPrototype\profile.dpapi"'
  if($Scenario -eq 'Foreign'){$path='"C:\Other\service.exe"'}
  [pscustomobject]@{PathName=$path;StartName='LocalSystem';ProcessId=0}
 }
}
function Get-Service {
 param($Name,$ErrorAction)
 $s=[pscustomobject]@{Status=$global:serviceState}
 $s | Add-Member ScriptMethod WaitForStatus {param($target,$timeout) if($global:serviceState -ne $target){throw 'Wrong state'}}
 $s
}
function Stop-Service {param($Name) $global:stops++;$global:serviceState='Stopped'}
$script=Join-Path $Fixture 'Setup-Components.ps1'
$id=[Guid]::NewGuid().ToString('N')
$launch=Join-Path $env:ProgramData 'launch.ini'
& $script -Phase PrepareInstall -Root 'E:\Apps\VPN Monitor' -SessionId $id -SetupPid 12345 -LaunchStatePath $launch
if($Scenario -eq 'Foreign'){
 if($LASTEXITCODE -ne 20 -or $global:stops -or $global:restores){throw 'Foreign service touched'}
 exit 0
}
if($LASTEXITCODE -ne 0){throw 'Prepare failed'}
$journal=Join-Path $env:ProgramData ('VpnMonitor\AwgStatus\update-'+$id+'.json')
if($Scenario -in @('SameSession','OtherSession')){
 $state=Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
 $state.ClientSessions=@($(if($Scenario -eq 'SameSession'){7}else{8}))
 [IO.File]::WriteAllText($journal,($state | ConvertTo-Json))
}
# Re-entering the preparation page must retain the original running state.
& $script -Phase PrepareInstall -Root 'E:\Apps\VPN Monitor' -SessionId $id -SetupPid 12345 -LaunchStatePath $launch
if($LASTEXITCODE -ne 0){throw 'Repeated preparation failed'}
$launchExpected=if($Scenario -eq 'SameSession'){'RestartClient=1'}else{'RestartClient=0'}
if((Get-Content -LiteralPath $launch -Raw) -notmatch $launchExpected){throw 'Incorrect finish-page launch offer'}
& $script -Phase RestoreInstall -Root 'E:\Apps\VPN Monitor' -SessionId $id -SetupPid 12345
if($LASTEXITCODE -ne 0){throw 'Restore failed'}
& $script -Phase RestoreInstall -Root 'E:\Apps\VPN Monitor' -SessionId $id -SetupPid 12345
if($LASTEXITCODE -ne 0){throw 'Repeated restore failed'}
$expected=if($Scenario -eq 'Stopped'){0}else{1}
if($global:stops -ne $expected -or $global:restores -ne $expected){throw 'Incorrect stop/restore count'}
$state=Get-Content -LiteralPath (Join-Path $env:ProgramData ('VpnMonitor\AwgStatus\update-'+$id+'.json')) -Raw | ConvertFrom-Json
if($state.Phase -ne 'Completed'){throw 'Participants cannot resume'}
exit 0
'@ | Set-Content -LiteralPath (Join-Path $fixture 'check.ps1') -Encoding UTF8
foreach($scenario in @('Running','Stopped','Foreign','SameSession','OtherSession')){
 $ErrorActionPreference='Continue'
 & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixture 'check.ps1') -Fixture $fixture -Scenario $scenario *> (Join-Path $fixture ($scenario+'.log'))
 $ErrorActionPreference='Stop'
 if($LASTEXITCODE -ne 0){throw ('Setup session failed: '+$scenario+'; '+(Get-Content (Join-Path $fixture ($scenario+'.log')) -Raw))}
}
Write-Output 'PASS: standalone setup stops/restores running tunnel, retains stopped state, preserves snapshot on retry, restores once, rejects foreign service.'
