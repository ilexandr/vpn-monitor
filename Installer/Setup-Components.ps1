param(
 [Parameter(Mandatory=$true)][ValidateSet('Preflight','Install','Remove')][string]$Phase,
 [Parameter(Mandatory=$true)][string]$Root
)
$ErrorActionPreference='Stop'
$serviceName='VpnMonitor.AwgPrototype'
$expected=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'VpnMonitor')).TrimEnd('\')
if([IO.Path]::GetFullPath($Root).TrimEnd('\') -ne $expected){throw 'Unsupported installation directory'}
$serviceRoot=Join-Path $expected 'AwgPrototype'
$dataRoot=Join-Path $env:ProgramData 'VpnMonitor\AwgPrototype'
$publicRoot=Join-Path $env:ProgramData 'VpnMonitor\AwgStatus'
function Assert-NoReparse([string]$path){
 $current=$path
 while($current){
  if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse point in installation path'}
  $next=Split-Path $current -Parent
  if($next -eq $current){break};$current=$next
 }
}
function Assert-Idle{
 $service=Get-Service -Name $serviceName -ErrorAction SilentlyContinue
 if($service -and $service.Status -ne 'Stopped'){throw 'Disconnect the shared AWG tunnel before setup; all sessions are affected.'}
 $busy=Get-CimInstance Win32_Process | Where-Object {
  $_.ExecutablePath -and $_.ExecutablePath.StartsWith($expected+'\',[StringComparison]::OrdinalIgnoreCase) -and
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
 Assert-NoReparse $expected;Assert-NoReparse $dataRoot;Assert-NoReparse $publicRoot
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
 Protect-Directory (Join-Path $expected 'Client') $true
 Protect-Directory $serviceRoot $true
 Protect-Directory (Join-Path $env:ProgramData 'VpnMonitor') $false
 Protect-Directory $dataRoot $false
 Protect-Directory $publicRoot $true
 $catalog=Join-Path $publicRoot 'profiles.json'
 if(!(Test-Path -LiteralPath $catalog)){
  [IO.File]::WriteAllText($catalog,'{"Version":1,"SelectedId":null,"Profiles":[]}')
 }
 $binary='"'+(Join-Path $serviceRoot 'AwgPrototype.Host.exe')+'" --run-service "'+(Join-Path $dataRoot 'profile.dpapi')+'"'
 if(Get-Service $serviceName -ErrorAction SilentlyContinue){
  $existing=Get-CimInstance Win32_Service -Filter "Name='VpnMonitor.AwgPrototype'"
  if($existing.PathName -ne $binary -or $existing.StartName -ne 'LocalSystem'){throw 'Existing service configuration differs; refusing replacement'}
  Set-Service -Name $serviceName -StartupType Manual
 }else{
  New-Service -Name $serviceName -BinaryPathName $binary -DisplayName 'VPN Monitor AWG' -StartupType Manual |
   Out-Null
 }
 & sc.exe sidtype $serviceName unrestricted | Out-Null
 if($LASTEXITCODE -ne 0){throw 'Cannot configure service SID'}
 & sc.exe sdset $serviceName 'D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;LC;;;AU)' | Out-Null
 if($LASTEXITCODE -ne 0){throw 'Cannot configure service permissions'}
 Write-Output 'Installed. Shared tunnel remains stopped.'
 exit 0
}catch{
 Write-Error $_.Exception.Message -ErrorAction Continue
 exit 20
}