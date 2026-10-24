$ErrorActionPreference='Stop'
$commit='e90531d15802cb976773f3b63443bc281f738ca3'
$engineRoot=Join-Path $PSScriptRoot 'engine'
if(!(Test-Path -LiteralPath $engineRoot)) {
 git clone https://github.com/amnezia-vpn/amneziawg-windows.git $engineRoot
 if($LASTEXITCODE -ne 0){throw 'Engine clone failed'}
 git -C $engineRoot checkout --detach $commit
 if($LASTEXITCODE -ne 0){throw 'Pinned checkout failed'}
}
$actual=git -C $engineRoot rev-parse HEAD
if($actual -ne $commit){throw 'Engine commit does not match pinned dependency'}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'engine-patches\vpnmonitor_validate.go') -Destination (Join-Path $engineRoot 'vpnmonitor_validate.go')
Write-Output "Engine prepared at $commit"