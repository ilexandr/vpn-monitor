$ErrorActionPreference='Stop'
$prototypeRoot=$PSScriptRoot
$engineRoot=Join-Path $prototypeRoot 'engine'
$toolsRoot=Join-Path $prototypeRoot 'toolchain'
$goRoot=Join-Path $toolsRoot 'go'
$compiler=Get-ChildItem $toolsRoot -Recurse -Filter 'x86_64-w64-mingw32-gcc.exe' | Select-Object -First 1
if(!$compiler) { throw 'Compiler missing' }
$env:GOROOT=$goRoot
$env:GOPATH=Join-Path $toolsRoot 'gopath'
$env:GOCACHE=Join-Path $toolsRoot 'gocache'
$env:GOTOOLCHAIN='local'
$env:GOOS='windows'; $env:GOARCH='amd64'; $env:CGO_ENABLED='1'
$env:CC=$compiler.FullName
$env:PATH=$compiler.DirectoryName+';'+(Join-Path $goRoot 'bin')+';'+$env:PATH
$out=Join-Path $prototypeRoot 'bin'
New-Item -ItemType Directory -Force -Path $out | Out-Null
Push-Location $engineRoot
try {
 & (Join-Path $goRoot 'bin\go.exe') build -buildmode c-shared -trimpath -o (Join-Path $out 'tunnel.dll')
 if($LASTEXITCODE -ne 0) { throw 'Engine build failed' }
} finally { Pop-Location }
$wintun=Get-ChildItem (Join-Path $toolsRoot 'wintun') -Recurse -Filter 'wintun.dll' | Where-Object { $_.DirectoryName -match 'amd64' } | Select-Object -First 1
if (!$wintun) { $wintun = Get-Item (Join-Path $out 'wintun.dll') -ErrorAction Stop }
if($wintun.FullName -ne (Join-Path $out 'wintun.dll')) { Copy-Item -LiteralPath $wintun.FullName -Destination (Join-Path $out 'wintun.dll') }
Get-FileHash (Join-Path $out 'tunnel.dll'),(Join-Path $out 'wintun.dll') | Format-List