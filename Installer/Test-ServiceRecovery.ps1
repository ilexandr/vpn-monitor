$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Service-Recovery.ps1')
$log=Join-Path (Split-Path $PSScriptRoot) 'artifacts\installer\recovery-test.jsonl'
$expected='"E:\Apps\VPN Monitor\Client\AwgPrototype.Host.exe" --run-service "C:\ProgramData\VpnMonitor\AwgPrototype\profile.dpapi"'
function Start-Sleep {param($Seconds)}
function Get-CimInstance {
 param($ClassName,$Filter,$ErrorAction)
 [pscustomobject]@{PathName=$global:binary;StartName='LocalSystem';State=$global:serviceState;StartMode='Manual';ExitCode=1067;ServiceSpecificExitCode=7}
}
function Invoke-CimMethod {
 param($InputObject,$MethodName)
 $global:attempts++
 if($global:attempts -le $global:failures){return [pscustomobject]@{ReturnValue=7}}
 $global:serviceState='Running'
 [pscustomobject]@{ReturnValue=0}
}
function Get-Service {
 param($Name)
 $service=[pscustomobject]@{Status=$global:serviceState}
 $service | Add-Member ScriptMethod WaitForStatus {param($status,$timeout) if($global:serviceState -ne $status){throw 'Unexpected service state'}}
 return $service
}
function Test-MonitorTunnelConnection {param($processId) return !$global:connectionFailed}
function Stop-Service {param($Name) $global:serviceState='Stopped';$global:stops++}
$global:connectionFailed=$false;$global:stops=0
$global:binary=$expected;$global:serviceState='Stopped';$global:attempts=0;$global:failures=0
Restore-MonitorTunnel $false $expected $log
if($global:attempts){throw 'Previously stopped service was started'}
$global:failures=1
Restore-MonitorTunnel $true $expected $log
if($global:attempts -ne 2 -or $global:serviceState -ne 'Running'){throw 'Transient failure did not recover'}
$global:serviceState='Stopped';$global:attempts=0;$global:failures=9
try{Restore-MonitorTunnel $true $expected $log;throw 'Persistent failure was swallowed'}catch{
 if($_.Exception.Message -notlike '*Win32Exit=1067*ServiceExit=7*'){throw}
}
if($global:attempts -ne 3){throw 'Unexpected retry count'}
$global:attempts=0;$global:binary='"C:\Other\service.exe"'
try{Restore-MonitorTunnel $true $expected $log;throw 'Foreign service accepted'}catch{
 if($_.Exception.Message -notlike '*Unexpected service configuration*'){throw}
}
if($global:attempts){throw 'Foreign service was started'}
$global:binary=$expected;$global:serviceState='Running';$global:attempts=0;$global:failures=0;$global:connectionFailed=$true
try{Restore-MonitorTunnel $true $expected $log;throw 'False Running accepted'}catch{if($_.Exception.Message -notlike '*connectivity/handshake*'){throw}}
if($global:stops -ne 2){throw 'Unhealthy Running service was not retried with bounded restarts'}
Write-Output 'PASS: stopped service preserved, transient retry, bounded permanent failure with exit codes, foreign service rejected.'
