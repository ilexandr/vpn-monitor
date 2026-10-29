$ErrorActionPreference='Stop'
$raw=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Update-Application.ps1'))
$begin=$raw.IndexOf('# Explicit transport policy')
$end=$raw.IndexOf('Add-Type -AssemblyName System.Windows.Forms')
if($begin -lt 0 -or $end -le $begin){throw 'Proxy policy block missing'}
$configure=[scriptblock]::Create($raw.Substring($begin,$end-$begin))
foreach($ProxyUrl in @('', 'http://127.0.0.1:10809/')){
 [Net.WebRequest]::DefaultWebProxy=[Net.WebProxy]::new('http://127.0.0.1:1')
 . $configure
 if(!$ProxyUrl -and $null -ne [Net.WebRequest]::DefaultWebProxy){throw 'Direct mode retained system proxy'}
 if($ProxyUrl -and [Net.WebRequest]::DefaultWebProxy.GetProxy([uri]'https://github.com').AbsoluteUri -ne $ProxyUrl){throw 'Explicit proxy not applied'}
 $web=[Net.WebClient]::new()
 try{$web.Proxy=$updateProxy;if(!$ProxyUrl -and $null -ne $web.Proxy){throw 'Asset download retained default proxy'}}finally{$web.Dispose()}
}
foreach($ProxyUrl in @('socks5://localhost:1080','http://user:secret@localhost:8080','http://localhost/path','http://localhost?x=1',"http://localhost:8080`n")){
 $rejected=$false;try{. $configure}catch{$rejected=$true};if(!$rejected){throw 'Invalid proxy accepted'}
}
Write-Output 'PASS: Windows PowerShell updater clears stale system proxy, applies explicit HTTP proxy to metadata/assets and rejects unsafe addresses.'
