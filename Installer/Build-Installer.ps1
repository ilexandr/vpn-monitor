param([string]$Compiler,[string]$NativeDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
if(!$NativeDirectory){$NativeDirectory=Join-Path $root 'AwgPrototype\bin'}
if(!$Compiler){
 $candidates=@((Join-Path $root 'tools\InnoSetup\ISCC.exe'),(Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Inno Setup 6\ISCC.exe'))
 $Compiler=$candidates | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
}
if(!$Compiler){throw 'Install Inno Setup 6 and pass -Compiler path'}
[xml]$project=Get-Content (Join-Path $root 'VpnMonitor.Client\VpnMonitor.Client.csproj')
$version=$project.Project.PropertyGroup.Version
if(!$version -or $version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid version'}
$stage=Join-Path $root "artifacts\installer\$version\stage"
$release=Join-Path $root "artifacts\releases\$version"
New-Item -ItemType Directory -Force -Path $stage,$release | Out-Null
foreach($entry in @(@('VpnMonitor.Client','Client'),@('AwgPrototype\Host','AwgPrototype'),@('AwgPrototype\Manager','AwgPrototype'))){
 & dotnet publish (Join-Path $root $entry[0]) -c Release -r win-x64 --self-contained true -p:DebugType=None -o (Join-Path $stage $entry[1])
 if($LASTEXITCODE -ne 0){throw 'Publish failed'}
}
foreach($name in @('tunnel.dll','wintun.dll')){
 $path=Join-Path $NativeDirectory $name
 if(!(Test-Path -LiteralPath $path)){throw "Native component missing: $name"}
 Copy-Item -LiteralPath $path -Destination (Join-Path $stage "AwgPrototype\$name")
}
# Merge identical runtime files once; reject any conflicting dependency versions.
$app=Join-Path $stage 'App'
$app=[IO.Path]::GetFullPath($app)
if(!$app.StartsWith([IO.Path]::GetFullPath($stage)+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid staging path'}
if(Test-Path -LiteralPath $app){
 if((Get-Item -LiteralPath $app -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse point in staging'}
 Remove-Item -LiteralPath $app -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $app | Out-Null
foreach($component in @('Client','AwgPrototype')){
 $source=Join-Path $stage $component
 foreach($file in Get-ChildItem -LiteralPath $source -Recurse -File){
  $relative=$file.FullName.Substring($source.Length+1)
  $target=Join-Path $app $relative
  if(Test-Path -LiteralPath $target){
   if((Get-FileHash $file.FullName).Hash -ne (Get-FileHash $target).Hash){throw "Conflicting shared dependency: $relative"}
  }else{
   New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
   Copy-Item -LiteralPath $file.FullName -Destination $target
  }
 }
}
$legacyCleanup=Join-Path $stage 'legacy-cleanup.iss'
$legacyEntries=@()
foreach($file in Get-ChildItem (Join-Path $stage 'AwgPrototype') -Recurse -File){
 $relative=$file.FullName.Substring((Join-Path $stage 'AwgPrototype').Length+1)
 $legacyEntries+='Type: files; Name: "{app}\AwgPrototype\'+$relative+'"'
}
$legacyEntries+='Type: dirifempty; Name: "{app}\AwgPrototype"'
[IO.File]::WriteAllLines($legacyCleanup,$legacyEntries)
if((Get-AuthenticodeSignature (Join-Path $stage 'AwgPrototype\wintun.dll')).Status -ne 'Valid'){throw 'Wintun signature invalid'}
$licenses=Join-Path $stage 'Licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Licenses\Wintun-Prebuilt-License.txt') $licenses
$dotnetRoot=Split-Path (Get-Command dotnet).Source
Copy-Item (Join-Path $dotnetRoot 'LICENSE.txt') (Join-Path $licenses 'DotNet-LICENSE.txt')
Copy-Item (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') (Join-Path $licenses 'DotNet-ThirdPartyNotices.txt')
$engine=Join-Path $root 'AwgPrototype\engine'
Copy-Item (Join-Path $engine 'README.md') (Join-Path $licenses 'AmneziaWG-Windows-MIT.md')
$tools=Join-Path $root 'AwgPrototype\toolchain'
Copy-Item (Join-Path $tools 'go\LICENSE') (Join-Path $licenses 'Go-LICENSE')
$env:GOPATH=Join-Path $tools 'gopath';$env:GOTOOLCHAIN='local'
Push-Location $engine
try{
 $modules=& (Join-Path $tools 'go\bin\go.exe') list -m -f '{{.Path}}|{{.Version}}|{{.Dir}}' all
 if($LASTEXITCODE -ne 0){throw 'Cannot enumerate Go modules'}
}finally{Pop-Location}
$moduleNames=@()
foreach($module in $modules){
 $parts=$module.Split('|');$moduleNames+=($parts[0]+'@'+$parts[1])
 if($parts.Count -lt 3 -or !$parts[2]){continue}
 $safe=$parts[0] -replace '[^A-Za-z0-9._-]','_'
 foreach($file in Get-ChildItem -LiteralPath $parts[2] -File | Where-Object Name -match '^(LICENSE|COPYING|NOTICE|PATENTS)'){
  Copy-Item -Force -LiteralPath $file.FullName -Destination (Join-Path $licenses ($safe+'-'+$file.Name))
 }
}
$moduleNames | Set-Content (Join-Path $licenses 'Go-Modules.txt')
$forbidden=Get-ChildItem $stage -Recurse -File | Where-Object { $_.Extension -in @('.conf','.dpapi','.csv','.pfx','.pem','.key','.log') -or $_.Name -match '^(settings|profiles)\.json$' }
if($forbidden){throw 'Forbidden personal data in installer staging'}
$info=@{Version=$version;SourceCommit=(git -C $root rev-parse HEAD);EngineCommit='e90531d15802cb976773f3b63443bc281f738ca3';Native=@{}}
foreach($name in @('tunnel.dll','wintun.dll')){$info.Native[$name]=(Get-FileHash (Join-Path $stage "AwgPrototype\$name")).Hash}
$info | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $stage 'Licenses\build-info.json')
# File-level update inventory. The current updater still installs the verified full package.
$files=@()
foreach($component in @(@('App','Client'),@('Licenses','Licenses'))){
 $source=Join-Path $stage $component[0]
 foreach($file in Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object Name -ne 'file-manifest.json'){
  $relative=$file.FullName.Substring($source.Length+1).Replace('\','/')
  $files += [ordered]@{Path=$component[1]+'/'+$relative;Size=$file.Length;Sha256=(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()}
 }
}
foreach($name in @('Setup-Components.ps1','Update-Application.ps1','Resume-AfterUpdate.ps1','Service-Recovery.ps1')){
 $file=Get-Item -LiteralPath (Join-Path $PSScriptRoot $name)
 $files += [ordered]@{Path='Setup/'+$name;Size=$file.Length;Sha256=(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()}
}
$manifest=[ordered]@{Schema=1;Version=$version;SourceCommit=$info.SourceCommit;Files=@($files | Sort-Object { $_.Path })} | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $licenses 'file-manifest.json'),$manifest,(New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText((Join-Path $release 'file-manifest.json'),$manifest,(New-Object Text.UTF8Encoding($false)))
& $Compiler "/DAppVersion=$version" "/DStageDir=$stage" "/DReleaseDir=$release" "/DLegacyCleanup=$legacyCleanup" (Join-Path $PSScriptRoot 'VpnMonitor.iss')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed'}
$exe=Join-Path $release "VpnMonitor-Setup-$version-win-x64.exe"
$metadata=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
$numericVersion=[version]::new($metadata.FileMajorPart,$metadata.FileMinorPart,$metadata.FileBuildPart)
if($numericVersion -ne [version]$version){throw "Installer numeric FileVersion mismatch: $numericVersion"}
$hash=(Get-FileHash $exe).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $release 'SHA256SUMS'),"$hash  "+[IO.Path]::GetFileName($exe)+[Environment]::NewLine)
Write-Output "Installer: $exe"
