param([Parameter(Mandatory=$true)][ValidateRange(1,[long]::MaxValue)][long]$ReleaseId)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$gate=$null;$locked=$false;$stage=$null;$serviceWasRunning=$false;$serviceStopped=$false;$installStarted=$false;$installed=$false
$state=$null;$form=$null;$failure=$null
$serviceName='VpnMonitor.AwgPrototype'
function Protect-UpdateDirectory([string]$path){
 if((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse point in update staging'}
 New-Item -ItemType Directory -Path $path -Force | Out-Null
 $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
 foreach($sid in @('S-1-5-18','S-1-5-32-544')){
  $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
 }
 $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
 Set-Acl -LiteralPath $path -AclObject $acl
}
function Write-State([string]$phase,[string]$message){
 $state.Phase=$phase;$state.Message=$message
 $temporary=$statePath+'.tmp'
 [IO.File]::WriteAllText($temporary,($state | ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
 Move-Item -LiteralPath $temporary -Destination $statePath -Force
 if($form){$label.Text=$message;[Windows.Forms.Application]::DoEvents()}
}
function Prepare-Process($process){
 if(!(Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue)){return}
 $pipe=[IO.Pipes.NamedPipeClientStream]::new('.','VpnMonitor.Update.'+$process.ProcessId,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous,[Security.Principal.TokenImpersonationLevel]::Impersonation)
 try{
  $pipe.Connect(10000)
  $writer=[IO.StreamWriter]::new($pipe);$writer.AutoFlush=$true
  $reader=[IO.StreamReader]::new($pipe)
  $writer.WriteLine($state.Id)
  $ack=$reader.ReadLineAsync()
  if(!$ack.Wait(15000) -or $ack.Result -ne 'ready'){throw 'Application did not prepare for restart'}
 }finally{$pipe.Dispose()}
 $old=Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
 if($old -and !$old.WaitForExit(20000)){throw 'Application is busy and could not close safely'}
}
try{
 if(!(New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator permissions required'}
 $gate=[Threading.Mutex]::new($false,'Global\VpnMonitor.Update')
 try{$locked=$gate.WaitOne(0)}catch [Threading.AbandonedMutexException]{$locked=$true}
 if(!$locked){throw 'Another VPN Monitor update is already running'}
 $root=[IO.Path]::GetFullPath((Get-ItemProperty 'HKLM:\Software\VpnMonitor').InstallRoot).TrimEnd('\')
 if([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') -ne (Join-Path $root 'Setup')){throw 'Updater must run from the installed application'}
 $client=Join-Path $root 'Client\VpnMonitor.Client.exe'
 $service=Get-CimInstance Win32_Service -Filter "Name='VpnMonitor.AwgPrototype'"
 $expected='"'+(Join-Path $root 'Client\AwgPrototype.Host.exe')+'" --run-service "'+(Join-Path $env:ProgramData 'VpnMonitor\AwgPrototype\profile.dpapi')+'"'
 if($service -and ($service.PathName -ne $expected -or $service.StartName -ne 'LocalSystem')){throw 'Unexpected shared service configuration'}
 if($service -and $service.State -notin @('Running','Stopped')){throw 'Wait until the tunnel finishes changing state'}

 $updateRoot=Join-Path $env:ProgramData 'VpnMonitor\Updates'
 Protect-UpdateDirectory $updateRoot
 $stage=Join-Path $updateRoot ([Guid]::NewGuid().ToString('N'))
 Protect-UpdateDirectory $stage
 $statePath=Join-Path $env:ProgramData ('VpnMonitor\AwgStatus\update-'+(Split-Path $stage -Leaf)+'.json')
 $state=@{Id=(Split-Path $stage -Leaf);Phase='';Message='';WorkerPid=$PID;InstallerPid=0;ServiceWasRunning=[bool]$serviceWasRunning}
 $form=[Windows.Forms.Form]::new();$form.Text='Обновление AWG Dash Monitor';$form.Width=540;$form.Height=160;$form.StartPosition='CenterScreen';$form.ControlBox=$false
 $label=[Windows.Forms.Label]::new();$label.Dock='Fill';$label.Padding=[Windows.Forms.Padding]::new(20);$label.Font=[Drawing.Font]::new('Segoe UI',11);$form.Controls.Add($label);$form.Show()
 Write-State 'Downloading' 'Загрузка и проверка обновления. Приложения и туннель пока работают…'
 [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
 $release=Invoke-RestMethod -Uri ('https://api.github.com/repos/ilexandr/vpn-monitor/releases/'+$ReleaseId) -Headers @{Accept='application/vnd.github+json'} -UserAgent 'VpnMonitor-Updater' -TimeoutSec 30
 if($release.draft -or $release.prerelease -or $release.tag_name -notmatch '^v?\d+\.\d+\.\d+$'){throw 'Invalid stable release'}
 $version=[version]$release.tag_name.TrimStart('v')
 $fileVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($client)
 $current=[version]::new($fileVersion.FileMajorPart,$fileVersion.FileMinorPart,$fileVersion.FileBuildPart)
 if($version -le $current){throw 'This release is not newer than the installed version'}
 $assetName='VpnMonitor-Setup-'+$version+'-win-x64.exe'
 $assets=@($release.assets | Where-Object {$_.name -eq $assetName -and $_.state -eq 'uploaded'})
 if($assets.Count -ne 1 -or $assets[0].digest -notmatch '^sha256:[a-fA-F0-9]{64}$'){throw 'Installer SHA-256 digest is missing'}
 $asset=$assets[0];$uri=[uri]$asset.browser_download_url
 if($uri.Scheme -ne 'https' -or $uri.Host -ne 'github.com' -or !$uri.AbsolutePath.StartsWith('/ilexandr/vpn-monitor/releases/download/')){throw 'Unexpected download source'}
 if($asset.size -lt 1 -or $asset.size -gt 268435456){throw 'Unexpected installer size'}
 $free=[IO.DriveInfo]::new([IO.Path]::GetPathRoot($stage)).AvailableFreeSpace
 $binarySize=(Get-ChildItem (Join-Path $root 'Client') -Recurse -File | Measure-Object Length -Sum).Sum
 if($free -lt ($binarySize*2+$asset.size+104857600)){throw 'Not enough free space for update and rollback copy'}
 $setup=Join-Path $stage $assetName
 $web=[Net.WebClient]::new();$web.Headers['User-Agent']='VpnMonitor-Updater'
 try{
  $download=$web.DownloadFileTaskAsync($uri,$setup)
  $deadline=[DateTime]::UtcNow.AddMinutes(15)
  while(!$download.IsCompleted){
   if([DateTime]::UtcNow -gt $deadline){$web.CancelAsync();throw 'Download timed out'}
   [Windows.Forms.Application]::DoEvents();Start-Sleep -Milliseconds 100
  }
  $download.GetAwaiter().GetResult()
 }finally{$web.Dispose()}
 if((Get-Item -LiteralPath $setup).Length -ne $asset.size -or (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne $asset.digest.Substring(7)){throw 'Installer checksum mismatch'}
 $installerVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($setup)
 if([version]::new($installerVersion.FileMajorPart,$installerVersion.FileMinorPart,$installerVersion.FileBuildPart) -ne $version){throw 'Installer version mismatch'}
 $service=Get-CimInstance Win32_Service -Filter "Name='VpnMonitor.AwgPrototype'"
 if($service -and ($service.PathName -ne $expected -or $service.StartName -ne 'LocalSystem' -or $service.State -notin @('Running','Stopped'))){throw 'Tunnel configuration changed during download'}
 $serviceWasRunning=$service -and $service.State -eq 'Running'
 $state.ServiceWasRunning=[bool]$serviceWasRunning
 Write-State 'Preparing' 'Сохранение состояния и закрытие приложений во всех сеансах…'
 $processes=@(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -and $_.ExecutablePath.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -and $_.Name -in @('VpnMonitor.Client.exe','AwgPrototype.Manager.exe')})
 $state.Processes=@($processes | Select-Object ProcessId,SessionId,Name)
 Write-State 'Preparing' 'Подготовка перезапуска приложений…'
 foreach($process in $processes){Prepare-Process $process}
 if($serviceWasRunning){
  Write-State 'Preparing' 'Остановка общего туннеля…'
  Stop-Service -Name $serviceName
  (Get-Service $serviceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(40));$serviceStopped=$true
  if($service.ProcessId){
   $hostProcess=Get-Process -Id $service.ProcessId -ErrorAction SilentlyContinue
   if($hostProcess -and !$hostProcess.WaitForExit(10000)){throw 'Tunnel host has not exited'}
  }
  Start-Sleep -Seconds 2
 }
 $backup=Join-Path $stage 'Backup';New-Item -ItemType Directory -Path $backup | Out-Null
 foreach($folder in @('Client','Setup','Licenses')){if(Test-Path (Join-Path $root $folder)){Copy-Item -LiteralPath (Join-Path $root $folder) -Destination $backup -Recurse -Force}}
 Write-State 'Installing' 'Установка новой версии…'
 $arguments='/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR="'+$root+'" /LOG="'+(Join-Path $stage 'setup.log')+'"'
 $installer=Start-Process -FilePath $setup -ArgumentList $arguments -PassThru
 $installStarted=$true;$state.InstallerPid=$installer.Id;Write-State 'Installing' 'Установка новой версии…'
 while(!$installer.HasExited){[Windows.Forms.Application]::DoEvents();Start-Sleep -Milliseconds 250;$installer.Refresh()}
 $installer.WaitForExit()
 if($installer.ExitCode -ne 0){throw ('Installer failed: '+$installer.ExitCode)}
 $resultVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($client)
 if([version]::new($resultVersion.FileMajorPart,$resultVersion.FileMinorPart,$resultVersion.FileBuildPart) -ne $version){throw 'Installed version verification failed'}
 $installed=$true
}catch{
 $failure=$_.Exception.Message
 if($installStarted -and !$installed -and $backup){
  try{
   foreach($folder in @('Client','Setup','Licenses')){
    $source=Join-Path $backup $folder
    if(Test-Path $source){Copy-Item -Path (Join-Path $source '*') -Destination (Join-Path $root $folder) -Recurse -Force}
   }
  }catch{$failure+='; rollback: '+$_.Exception.Message}
 }
}finally{
 if($state){
  try{
   Write-State 'Restoring' 'Восстановление работы туннеля и приложений…'
   . (Join-Path $PSScriptRoot 'Service-Recovery.ps1')
   Restore-MonitorTunnel $serviceWasRunning $expected (Join-Path $stage 'service-recovery.jsonl')
  }catch{$failure+='; tunnel restoration: '+$_.Exception.Message}
  try{if($failure){Write-State 'Failed' $failure}else{Write-State 'Completed' 'Обновление установлено. Работа приложений восстанавливается.'}}catch{$failure+='; state: '+$_.Exception.Message}
 }
 if($form){$form.Close();$form.Dispose()}
 if($locked){$gate.ReleaseMutex()};if($gate){$gate.Dispose()}
}
if($failure){[Windows.Forms.MessageBox]::Show(('Обновление не завершено: '+$failure+[Environment]::NewLine+'Журнал: '+$stage),'AWG Dash Monitor',[Windows.Forms.MessageBoxButtons]::OK,[Windows.Forms.MessageBoxIcon]::Error) | Out-Null;exit 1}
exit 0
