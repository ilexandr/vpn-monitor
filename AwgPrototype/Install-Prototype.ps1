param(
 [Parameter(Mandatory=$true)][string]$ProfilePath,
 [switch]$CheckOnly
)
$ErrorActionPreference='Stop'
$serviceName='VpnMonitor.AwgPrototype'
$installRoot=Join-Path $env:ProgramFiles 'VpnMonitor\AwgPrototype'
$dataRoot=Join-Path $env:ProgramData 'VpnMonitor\AwgPrototype'
$source=Join-Path $PSScriptRoot 'bin'
$profile=(Resolve-Path -LiteralPath $ProfilePath).Path
if((Get-Item -LiteralPath $profile).Length -gt 262144) { throw 'Profile exceeds 256 KB' }
& (Join-Path $source 'AwgPrototype.Host.exe') --validate $profile
if($LASTEXITCODE -ne 0) { throw 'Profile validation failed' }
if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw 'Prototype service already exists; refusing overwrite' }
if(Test-Path -LiteralPath $installRoot) { throw 'Install directory already exists; refusing overwrite' }
if(Test-Path -LiteralPath $dataRoot) { throw 'Data directory already exists; refusing overwrite' }
foreach($base in @($env:ProgramFiles,$env:ProgramData)) {
 $parent=Join-Path $base 'VpnMonitor'
 if((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse-point parent refused' }
}
if($CheckOnly) {
 Write-Output 'PASS: profile accepted; new service and protected deployment paths available. Installation would be demand-start, no network change.'
 exit 0
}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(!([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
 throw 'Installation requires an elevated administrator PowerShell.'
}
function Protect-Directory([string]$path) {
 New-Item -ItemType Directory -Force -Path $path | Out-Null
 $acl=[Security.AccessControl.DirectorySecurity]::new()
 $acl.SetAccessRuleProtection($true,$false)
 foreach($sidText in @('S-1-5-18','S-1-5-32-544')) {
  $sid=[Security.Principal.SecurityIdentifier]::new($sidText)
  $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
 }
 $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
 Set-Acl -LiteralPath $path -AclObject $acl
}
foreach($base in @($env:ProgramFiles,$env:ProgramData)) {
 $parent=Join-Path $base 'VpnMonitor'
 if(Test-Path -LiteralPath $parent) {
  foreach($rule in (Get-Acl -LiteralPath $parent).Access) {
   if($rule.AccessControlType -ne 'Allow') { continue }
   $sid=$rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
   $writeMask=[Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
   if($sid -notin @('S-1-5-18','S-1-5-32-544') -and ($rule.FileSystemRights -band $writeMask)) { throw 'Writable deployment parent refused' }
  }
 } else { Protect-Directory $parent }
}
Protect-Directory $installRoot
Protect-Directory $dataRoot
$allowed=@('AwgPrototype.Host.exe','AwgPrototype.Host.dll','AwgPrototype.Host.deps.json','AwgPrototype.Host.runtimeconfig.json','tunnel.dll','wintun.dll')
foreach($file in $allowed) {
 $from=Join-Path $source $file
 if(!(Test-Path -LiteralPath $from)) { throw "Missing build file $file" }
 Copy-Item -LiteralPath $from -Destination (Join-Path $installRoot $file)
 if((Get-FileHash $from).Hash -ne (Get-FileHash (Join-Path $installRoot $file)).Hash) { throw 'Installed file hash mismatch' }
}
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
$bytes=[IO.File]::ReadAllBytes($profile)
try {
 $encrypted=[Security.Cryptography.ProtectedData]::Protect($bytes,$null,[Security.Cryptography.DataProtectionScope]::LocalMachine)
 [IO.File]::WriteAllBytes((Join-Path $dataRoot 'profile.dpapi'),$encrypted)
} finally { [Array]::Clear($bytes,0,$bytes.Length) }
$exe=Join-Path $installRoot 'AwgPrototype.Host.exe'
$protectedProfile=Join-Path $dataRoot 'profile.dpapi'
$binary='"'+$exe+'" --run-service "'+$protectedProfile+'"'
New-Service -Name $serviceName -DisplayName 'VPN Monitor AWG Prototype' -BinaryPathName $binary -StartupType Manual -Description 'Shared machine tunnel prototype; administrators only; no automatic startup.' | Out-Null
& sc.exe sidtype $serviceName unrestricted
if($LASTEXITCODE -ne 0) { throw 'Service SID configuration failed' }
# Administrators/System control the service; authenticated users can only query it.
& sc.exe sdset $serviceName 'D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;LC;;;AU)'
if($LASTEXITCODE -ne 0) { throw 'Service ACL configuration failed' }
Write-Output 'INSTALLED: demand-start, STOPPED. No adapter or route created.'
Get-Service -Name $serviceName | Select-Object Name,Status,StartType