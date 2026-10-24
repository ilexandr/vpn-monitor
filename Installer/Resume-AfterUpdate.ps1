param(
 [Parameter(Mandatory=$true)][ValidatePattern('^[a-f0-9]{32}$')][string]$UpdateId,
 [Parameter(Mandatory=$true)][ValidateSet('Client','Manager')][string]$Role,
 [Parameter(Mandatory=$true)][int]$OriginalPid
)
$ErrorActionPreference='Stop'
$statePath=Join-Path $env:ProgramData ('VpnMonitor\AwgStatus\update-'+$UpdateId+'.json')
try{
 $original=Get-Process -Id $OriginalPid -ErrorAction SilentlyContinue
 if($original -and !$original.WaitForExit(90000)){exit 1}
 $deadline=[DateTime]::UtcNow.AddMinutes(30)
 do{
  Start-Sleep -Milliseconds 500
  try{$state=Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json}catch{continue}
  if($state.Id -ne $UpdateId){continue}
  if($state.Phase -in @('Completed','Failed')){break}
  # If the updater crashed, wait until its installer has also exited.
  if(!(Get-Process -Id $state.WorkerPid -ErrorAction SilentlyContinue) -and
     (!$state.InstallerPid -or !(Get-Process -Id $state.InstallerPid -ErrorAction SilentlyContinue))){break}
 }while([DateTime]::UtcNow -lt $deadline)
 if([DateTime]::UtcNow -ge $deadline){exit 2}
 $root=(Get-ItemProperty 'HKLM:\Software\VpnMonitor').InstallRoot
 $name=if($Role -eq 'Client'){'VpnMonitor.Client.exe'}else{'AwgPrototype.Manager.exe'}
 $exe=Join-Path $root ('Client\'+$name)
 if(Test-Path -LiteralPath $exe){Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)}
}catch{exit 3}
