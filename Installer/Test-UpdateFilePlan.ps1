$ErrorActionPreference='Stop'
$directory=Join-Path ([IO.Path]::GetTempPath()) ('VpnMonitor-file-plan-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $directory 'Client') -Force | Out-Null
$script=Join-Path $PSScriptRoot 'Get-UpdateFilePlan.ps1'
$ps=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
try{
 $file=Join-Path $directory 'Client\fixture.dll';[IO.File]::WriteAllText($file,'first')
 $entry=@{Path='Client/fixture.dll';Size=5;Sha256=(Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()}
 $manifest=Join-Path $directory 'manifest.json'
 function Save-Manifest($entries){[IO.File]::WriteAllText($manifest,(@{Schema=1;Version='0.1.74';Files=@($entries)} | ConvertTo-Json -Depth 5))}
 Save-Manifest @($entry)
 $plan=(& $script -Manifest $manifest -InstallRoot $directory) | ConvertFrom-Json
 if($plan.UnchangedFiles -ne 1 -or $plan.ChangedBytes -ne 0){throw 'Identical files must be reused'}
 [IO.File]::WriteAllText($file,'other')
 $plan=(& $script -Manifest $manifest -InstallRoot $directory) | ConvertFrom-Json
 if($plan.ChangedFiles.Count -ne 1 -or $plan.ChangedBytes -ne 5){throw 'Same-size corruption must be detected by SHA256'}
 Remove-Item -LiteralPath $file
 $plan=(& $script -Manifest $manifest -InstallRoot $directory) | ConvertFrom-Json
 if($plan.ChangedBytes -ne 5){throw 'Missing files must be downloaded'}
 foreach($invalid in @('../escape','Client/../escape','Client/C:escape','Client\escape','Client/CON','Client/trailing.','Client//empty','Client/file:stream')){
  $entry.Path=$invalid;Save-Manifest @($entry)
  $ErrorActionPreference='Continue'; & $ps -NoProfile -NonInteractive -File $script -Manifest $manifest -InstallRoot $directory *> $null; $code=$LASTEXITCODE; $ErrorActionPreference='Stop'
  if($code -eq 0){throw "Unsafe path accepted: $invalid"}
 }
 $entry.Path='Client/fixture.dll';Save-Manifest @($entry,$entry)
 $ErrorActionPreference='Continue'; & $ps -NoProfile -NonInteractive -File $script -Manifest $manifest -InstallRoot $directory *> $null; $code=$LASTEXITCODE; $ErrorActionPreference='Stop'
 if($code -eq 0){throw 'Duplicate Windows paths accepted'}
 Write-Output 'PASS: file-update plan reuses matching hashes, detects corruption/missing files and rejects traversal, streams, reserved names and duplicates.'
}finally{
 # Only known fixture files; no recursive operation or computed recursive deletion.
 foreach($name in @('manifest.json','Client\fixture.dll')){ $path=Join-Path $directory $name; if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path} }
 [IO.Directory]::Delete((Join-Path $directory 'Client')); [IO.Directory]::Delete($directory)
}
