$ErrorActionPreference='Stop'
$tools=Join-Path $PSScriptRoot 'toolchain'
New-Item -ItemType Directory -Force -Path $tools | Out-Null
$dependencies=Get-Content (Join-Path $PSScriptRoot 'DEPENDENCIES.json') -Raw | ConvertFrom-Json
foreach($item in $dependencies){
 $archive=Join-Path $tools $item.Name
 $valid=(Test-Path -LiteralPath $archive) -and (Get-FileHash $archive).Hash -eq $item.Sha256
 if(!$valid){
  & curl.exe -4 --http1.1 -fL --retry 3 --connect-timeout 20 --max-time 300 -o $archive $item.Url
  if($LASTEXITCODE -ne 0){throw "Download failed: $($item.Name)"}
 }
 if((Get-FileHash $archive).Hash -ne $item.Sha256){throw 'Dependency hash mismatch'}
 Expand-Archive -LiteralPath $archive -DestinationPath $tools -Force
}
Write-Output 'Pinned toolchain verified and extracted.'