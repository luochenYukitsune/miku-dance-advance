#Requires -Version 5.1
<#
.SYNOPSIS
Downloads the pinned FSR, XeSS and DLSS runtimes (and the NGX SDK used to build the DLSS bridge).
.DESCRIPTION
Every file in tools/upscaling/manifest.json comes from the vendor's own repository at a fixed
commit and must match its SHA-256 before it is kept. Nothing is modified. The result is laid out as:
  <Destination>/runtime/{fsr,xess,dlss}/...   app-local layout under tools/upscaling
  <Destination>/licenses/{fsr,xess,dlss}/...  vendor license texts
  <Destination>/sdk/dlss/            NGX headers and nvsdk_ngx_d.lib for scripts/build-dlss.ps1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Destination,
    [ValidateSet('fsr', 'xess', 'dlss')][string[]]$Providers = @('fsr', 'xess', 'dlss')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$manifestPath = Join-Path $PSScriptRoot '../tools/upscaling/manifest.json'
$pin = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$root = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $root) { throw "Destination must be new: $root" }
New-Item -ItemType Directory -Path $root | Out-Null

foreach ($provider in $Providers) {
    $entry = $pin.providers.$provider
    if (-not $entry) { throw "Provider missing from manifest: $provider" }
    foreach ($file in $entry.files) {
        $folder = switch ($file.role) {
            'runtime' { "runtime/$provider" }
            'license' { "licenses/$provider" }
            'sdk' { "sdk/$provider" }
            default { throw "Unknown role: $($file.role)" }
        }
        $target = Join-Path $root (Join-Path $folder $file.path)
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        for ($attempt = 1; ; $attempt++) {
            try { Invoke-WebRequest -Uri $file.url -OutFile $target -UseBasicParsing; break }
            catch { if ($attempt -ge 3) { throw }; Start-Sleep -Seconds (5 * $attempt) }
        }
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($actual -ine $file.sha256) { throw "SHA256 mismatch for $provider/$($file.path): $actual" }
    }
    # The loader and runtime DLLs ship Authenticode-signed by their vendor; report, never trust blindly.
    foreach ($dll in Get-ChildItem -LiteralPath (Join-Path $root "runtime/$provider") -Filter *.dll -ErrorAction SilentlyContinue) {
        $signature = Get-AuthenticodeSignature -LiteralPath $dll.FullName
        Write-Output ("{0}/{1}: signature {2} ({3})" -f $provider, $dll.Name, $signature.Status, $signature.SignerCertificate.Subject)
    }
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $root 'manifest.json')
Write-Output $root
