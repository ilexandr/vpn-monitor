$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$install=Join-Path $env:ProgramFiles 'VpnMonitor\AwgPrototype'
$data=Join-Path $env:ProgramData 'VpnMonitor\AwgPrototype'
$public=Join-Path $env:ProgramData 'VpnMonitor\AwgStatus'
if(!(Test-Path $install) -or !(Test-Path $data)) { throw 'Install service components first' }
$files=@('AwgPrototype.Manager.exe','AwgPrototype.Manager.dll','AwgPrototype.Manager.deps.json','AwgPrototype.Manager.runtimeconfig.json')
foreach($file in $files) {
 Copy-Item -LiteralPath (Join-Path $root "manager-bin\$file") -Destination (Join-Path $install $file) -Force
 if((Get-FileHash (Join-Path $root "manager-bin\$file")).Hash -ne (Get-FileHash (Join-Path $install $file)).Hash) { throw 'Manager copy mismatch' }
}
if(!(Test-Path -LiteralPath $public)) {
 New-Item -ItemType Directory -Path $public | Out-Null
 $acl=[Security.AccessControl.DirectorySecurity]::new()
 $acl.SetAccessRuleProtection($true,$false)
 foreach($sidText in @('S-1-5-18','S-1-5-32-544')) {
  $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sidText),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
 }
 $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-11'),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
 $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
 Set-Acl -LiteralPath $public -AclObject $acl
}
$catalog=Join-Path $public 'profiles.json'
if(!(Test-Path -LiteralPath $catalog)) { [IO.File]::WriteAllText($catalog,'{"Version":1,"SelectedId":null,"Profiles":[]}') }
$existing=Get-Content $catalog -Raw | ConvertFrom-Json
if($existing.Profiles.Count -eq 0) {
 & (Join-Path $install 'AwgPrototype.Manager.exe') adopt '-'
 if($LASTEXITCODE -ne 0) { throw "Profile adoption failed: $LASTEXITCODE" }
}
@{
 Catalog=(Get-Content $catalog -Raw | ConvertFrom-Json)
 Service=(Get-Service 'VpnMonitor.AwgPrototype').Status.ToString()
 PublicAcl=(Get-Acl -LiteralPath $public).Sddl
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $root 'MANAGER_INSTALL.json')