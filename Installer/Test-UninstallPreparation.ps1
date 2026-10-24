$ErrorActionPreference='Stop'
$fixture=Join-Path (Split-Path $PSScriptRoot) 'artifacts\installer\uninstall-fixture.ps1'
@'
param([string]$Script,[string]$Scenario)
$global:stops=0;$global:state='Running'
function Get-ItemProperty {param($Path,$ErrorAction) [pscustomobject]@{InstallRoot='E:\Apps\VPN Monitor'}}
function Get-CimInstance {
 param($ClassName,$Filter,$ErrorAction)
 if($ClassName -eq 'Win32_Service' -and $Scenario -ne 'Missing'){
  $path='"E:\Apps\VPN Monitor\Client\AwgPrototype.Host.exe" --run-service "'+$env:ProgramData+'\VpnMonitor\AwgPrototype\profile.dpapi"'
  if($Scenario -eq 'Foreign'){$path='"C:\Other\service.exe"'}
  [pscustomobject]@{PathName=$path;StartName='LocalSystem';ProcessId=0}
 }
}
function Get-Service {
 param($Name,$ErrorAction)
 if($Scenario -eq 'Missing'){return}
 $service=[pscustomobject]@{Status=$global:state}
 $service | Add-Member ScriptMethod WaitForStatus {param($target,$timeout) if($global:state -ne $target){throw 'Wrong service state'}}
 return $service
}
function Stop-Service {param($Name) $global:stops++;$global:state='Stopped'}
function Test-Path {param($LiteralPath) return $false}
& $Script -Phase PrepareRemove -Root 'E:\Apps\VPN Monitor'
$code=$LASTEXITCODE
if($Scenario -eq 'Running'){
 if($code -ne 0 -or $global:stops -ne 1 -or $global:state -ne 'Stopped'){throw 'Uninstall did not stop its tunnel'}
}elseif($Scenario -eq 'Missing'){
 if($code -ne 0 -or $global:stops){throw 'Missing service was not handled'}
}else{
 if($code -ne 20 -or $global:stops){throw 'Foreign service was touched'}
}
exit 0
'@ | Set-Content -LiteralPath $fixture -Encoding UTF8
foreach($scenario in @('Running','Missing','Foreign')){
 $ErrorActionPreference='Continue'
 & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $fixture -Script (Join-Path $PSScriptRoot 'Setup-Components.ps1') -Scenario $scenario *> ($fixture+'.out')
 $code=$LASTEXITCODE;$ErrorActionPreference='Stop'
 if($code -ne 0){throw "Uninstall preparation failed: $scenario"}
}
Write-Output 'PASS: uninstall automatically stops its tunnel, tolerates missing service, rejects foreign service.'
