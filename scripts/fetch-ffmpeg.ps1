param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $PSScriptRoot '../tools/ffmpeg/manifest.json'
$pin = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$root = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $root) { throw "Destination must be new: $root" }
New-Item -ItemType Directory -Path $root | Out-Null
$archive = Join-Path $root 'ffmpeg.zip'
Invoke-WebRequest -Uri $pin.assetUrl -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $pin.assetSha256) { throw 'FFmpeg download SHA256 mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $root 'upstream')
$upstream = Join-Path $root ('upstream/' + $pin.archiveRoot)
$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Path $bin | Out-Null
foreach ($entry in $pin.files.PSObject.Properties) {
    $source = Join-Path $upstream ('bin/' + $entry.Name)
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $entry.Value) { throw "FFmpeg executable hash mismatch: $($entry.Name)" }
    Copy-Item -LiteralPath $source -Destination $bin
}
Copy-Item -LiteralPath (Join-Path $upstream 'LICENSE.txt') -Destination (Join-Path $root 'LICENSE')
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $root 'manifest.json')
Write-Output $bin
