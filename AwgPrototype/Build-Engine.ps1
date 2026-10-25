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
# Reapply our small, tracked overlay to the pinned upstream checkout.
foreach($name in @('vpnmonitor_validate.go','vpnmonitor_guard.go','vpnmonitor_guard_policy.go','vpnmonitor_guard_policy_test.go')){
 Copy-Item -LiteralPath (Join-Path $prototypeRoot ('engine-patches\'+$name)) -Destination (Join-Path $engineRoot $name) -Force
}
$serviceFile=Join-Path $engineRoot 'service.go'
$serviceSource=[IO.File]::ReadAllText($serviceFile)
if(!$serviceSource.Contains('stopConnectionGuard')){
 $serviceSource=$serviceSource.Replace('var dev *device.Device',"var stopConnectionGuard func()`n`tvar endpointRoutes map[string]guardRoute`n`tvar dev *device.Device")
 $serviceSource=$serviceSource.Replace('svcSpecificEC, exitCode = services.DetermineErrorCode',"if stopConnectionGuard != nil { stopConnectionGuard() } else { for _, route := range endpointRoutes { route.remove() } }`n`t`tsvcSpecificEC, exitCode = services.DetermineErrorCode")
 $serviceSource=$serviceSource.Replace('log.Println("Dropping privileges")',"endpointRoutes = make(map[string]guardRoute)`n`tguardSettings := readGuardPolicy(config)`n`tendpoints := []string{}`n`tfor _, peer := range config.Peers { endpoints = append(endpoints, peer.Endpoint.String()) }`n`tif guardSettings.BackupEndpoint != `"`" { endpoints = append(endpoints, guardSettings.BackupEndpoint) }`n`tmaintainEndpointRoutes(endpoints, guardSettings.PreferPhysical, endpointRoutes)`n`tlog.Println(`"Dropping privileges`")")
 $serviceSource=$serviceSource.Replace('log.Println("Startup complete")',"log.Println(`"Startup complete`")`n`tstopConnectionGuard = startVpnMonitorGuard(dev, config, endpointRoutes)")
 [IO.File]::WriteAllText($serviceFile,$serviceSource,[Text.UTF8Encoding]::new($false))
}
# Restore missing entries without flushing an active Wintun interface.
$serviceSource=[IO.File]::ReadAllText($serviceFile)
$serviceSource=[regex]::Replace($serviceSource,'(?m)^\s*stopConnectionGuard = startVpnMonitorGuard[^\r\n]+',"`tstopConnectionGuard = startVpnMonitorGuard(dev, config, endpointRoutes, func() { repairVpnMonitorInterface(config, nativeTun) })")
[IO.File]::WriteAllText($serviceFile,$serviceSource,[Text.UTF8Encoding]::new($false))
Push-Location $engineRoot
try {
 & (Join-Path $goRoot 'bin\go.exe') test -run 'TestGuard' .
 if($LASTEXITCODE -ne 0) { throw 'Connection guard tests failed' }
 & (Join-Path $goRoot 'bin\go.exe') build -buildmode c-shared -trimpath -o (Join-Path $out 'tunnel.dll')
 if($LASTEXITCODE -ne 0) { throw 'Engine build failed' }
} finally { Pop-Location }
$wintun=Get-ChildItem (Join-Path $toolsRoot 'wintun') -Recurse -Filter 'wintun.dll' | Where-Object { $_.DirectoryName -match 'amd64' } | Select-Object -First 1
if (!$wintun) { $wintun = Get-Item (Join-Path $out 'wintun.dll') -ErrorAction Stop }
if($wintun.FullName -ne (Join-Path $out 'wintun.dll')) { Copy-Item -LiteralPath $wintun.FullName -Destination (Join-Path $out 'wintun.dll') }
Get-FileHash (Join-Path $out 'tunnel.dll'),(Join-Path $out 'wintun.dll') | Format-List