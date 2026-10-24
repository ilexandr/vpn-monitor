$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$fixture=Join-Path $root 'artifacts\installer\resume-fixture.ps1'
@'
param([string]$Script,[string]$Scenario,[string]$Role)
$global:launched=$null
function Get-Process {
 param($Id,$ErrorAction)
 if($Scenario -eq 'StillRunning'){
  $p=[pscustomobject]@{}
  $p | Add-Member ScriptMethod WaitForExit {param($timeout) return $false}
  return $p
 }
}
function Start-Sleep {param($Milliseconds)}
function Get-Content {
 param($LiteralPath,[switch]$Raw)
 $phase=if($Scenario -eq 'Failed'){'Failed'}elseif($Scenario -eq 'Crashed'){'Installing'}else{'Completed'}
 @{Id=('a'*32);Phase=$phase;WorkerPid=999999;InstallerPid=0} | ConvertTo-Json
}
function Get-ItemProperty {param($Path) [pscustomobject]@{InstallRoot='E:\Apps\VPN Monitor'}}
function Test-Path {param($LiteralPath) return $true}
function Start-Process {param($FilePath,$WorkingDirectory) $global:launched=$FilePath}
& $Script -UpdateId ('a'*32) -Role $Role -OriginalPid 999998
$code=$LASTEXITCODE
if($Scenario -eq 'StillRunning'){
 if($global:launched -or $code -ne 1){throw 'Duplicate instance would be launched'}
}else{
 $name=if($Role -eq 'Client'){'VpnMonitor.Client.exe'}else{'AwgPrototype.Manager.exe'}
 if($global:launched -ne ('E:\Apps\VPN Monitor\Client\'+$name)){throw 'Wrong restart target'}
}
exit 0
'@ | Set-Content -LiteralPath $fixture -Encoding UTF8
foreach($scenario in @('Completed','Failed','Crashed','StillRunning')){
 foreach($role in @('Client','Manager')){
  & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $fixture -Script (Join-Path $PSScriptRoot 'Resume-AfterUpdate.ps1') -Scenario $scenario -Role $role
  if($LASTEXITCODE -ne 0){throw "Resume failed: $scenario/$role"}
 }
}
Write-Output 'PASS: client and manager resume on success, failure, worker crash; existing process prevents duplicate launch.'
