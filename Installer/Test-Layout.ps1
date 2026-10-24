param([string]$StageDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$fixture=Join-Path $root 'artifacts\installer\path-fixture.ps1'
@'
param([string]$SetupScript,[string]$Target,[string]$Mode)
function Get-ItemProperty { param($Path,$ErrorAction) [pscustomobject]@{InstallRoot='E:\Old VPN Monitor';InstallLocation='E:\Old VPN Monitor'} }
function Get-Service { param($Name,$ErrorAction) [pscustomobject]@{Status='Stopped'} }
function Get-CimInstance {
 param($ClassName,$Filter)
 if($ClassName -eq 'Win32_Service'){
  $path='"E:\Old VPN Monitor\Client\AwgPrototype.Host.exe" --run-service "'+$env:ProgramData+'\VpnMonitor\AwgPrototype\profile.dpapi"'
  if($Mode -eq 'ForeignService'){$path='"C:\Other\service.exe"'}
  [pscustomobject]@{PathName=$path;StartName='LocalSystem'}
 }
}
function Test-Path { param($LiteralPath) $false }
function Get-Item { param($LiteralPath,[switch]$Force) [pscustomobject]@{Attributes=[IO.FileAttributes]::Directory} }
& $SetupScript -Phase Preflight -Root $Target
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath $fixture -Encoding UTF8
$ps=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
foreach($case in @(@('E:\Apps\VPN Monitor Test','Owned',0),@('E:\Apps\VPN Monitor Test','ForeignService',20),@($env:WINDIR,'Owned',20),@('E:\','Owned',20))){
 $ErrorActionPreference='Continue'
 & $ps -NoProfile -NonInteractive -File $fixture -SetupScript (Join-Path $PSScriptRoot 'Setup-Components.ps1') -Target $case[0] -Mode $case[1] *> (Join-Path $root 'artifacts\installer\path-fixture-output.txt')
 $code=$LASTEXITCODE
 $ErrorActionPreference='Stop'
 if($code -ne $case[2]){throw "Unexpected preflight result for $($case[0]), $($case[1]): $LASTEXITCODE"}
}
if($StageDirectory){
 foreach($name in @('VpnMonitor.Client.exe','AwgPrototype.Manager.exe','AwgPrototype.Host.exe','tunnel.dll','wintun.dll')){
  if(!(Test-Path (Join-Path $StageDirectory $name))){throw "Missing component $name"}
 }
 & (Join-Path $StageDirectory 'AwgPrototype.Host.exe') --validate (Join-Path $root 'TEST.conf')
 if($LASTEXITCODE -ne 0){throw 'Combined runtime validation failed'}
}
Write-Output 'PASS: custom path with spaces, foreign service rejection, reserved paths, shared native runtime.'
