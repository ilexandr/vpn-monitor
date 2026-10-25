param(
 [Parameter(Mandatory=$true)][string]$Manifest,
 [Parameter(Mandatory=$true)][string]$InstallRoot
)
# Read-only planning. This does not authorize or apply files from a downloaded manifest.
$ErrorActionPreference='Stop'
if((Get-Item -LiteralPath $Manifest).Length -gt 2MB){throw 'Manifest too large'}
$document=[IO.File]::ReadAllText($Manifest) | ConvertFrom-Json
if($document.Schema -ne 1 -or $document.Version -notmatch '^\d+\.\d+\.\d+$' -or $document.Files.Count -gt 4096){throw 'Invalid manifest'}
$base=[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$paths=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$changed=@();$unchanged=0L;$total=0L;$download=0L
foreach($entry in $document.Files){
 $relative=[string]$entry.Path
 if($relative -notmatch '^(Client|Licenses|Setup)/' -or $relative -match '[\\:\x00-\x1f]' -or $relative.Length -gt 240){throw 'Unsafe manifest path'}
 foreach($part in $relative.Split('/')){
  if(!$part -or $part -in @('.','..') -or $part -match '[. ]$' -or $part -match '[<>"|?*]' -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)'){throw 'Unsafe manifest segment'}
 }
 if(!$paths.Add($relative) -or [string]$entry.Sha256 -notmatch '^[a-f0-9]{64}$' -or $entry.Size -isnot [ValueType] -or [decimal]$entry.Size -lt 0 -or [decimal]$entry.Size -gt 1GB -or [decimal]$entry.Size -ne [long]$entry.Size){throw 'Invalid manifest entry'}
 $path=$base
 # Reject links at the installation root and at every existing ancestor, including the file itself.
 if((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse point in install path'}
 foreach($part in $relative.Split('/')){
  $path=Join-Path $path $part
  if((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse point in install path'}
 }
 $same=$false
 if(Test-Path -LiteralPath $path -PathType Leaf){
  $same=(Get-Item -LiteralPath $path).Length -eq $entry.Size -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $entry.Sha256
 }
 $total += [long]$entry.Size
 if($same){$unchanged++}else{$changed += $relative;$download += [long]$entry.Size}
}
[pscustomobject]@{Version=$document.Version;ChangedFiles=@($changed);UnchangedFiles=$unchanged;ChangedBytes=$download;TotalBytes=$total;Mode='PlanOnly';CompressedDownloadBytes=$null} | ConvertTo-Json -Depth 4
