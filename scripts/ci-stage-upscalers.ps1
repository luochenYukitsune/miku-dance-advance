#Requires -Version 5.1
<#
.SYNOPSIS
Adds the optional FSR, XeSS and DLSS upscalers to a published Windows build (CI).
.DESCRIPTION
1. Fetches the pinned vendor runtimes and the NGX SDK (scripts/fetch-upscalers.ps1).
2. Builds the DLSS bridge mmd_dlss.dll with the MSVC toolset found by vswhere.
3. Copies runtimes into <Published>/tools/upscaling/{fsr,xess,dlss} and licenses into
   <Published>/tools/upscaling/licenses/{fsr,xess,dlss}.
4. Stages the pinned Microsoft VC runtime beside each provider (scripts/stage-editor-crt.ps1).
5. Writes tools/upscaling/installed.json listing every staged file and hash.
Whether a provider actually works is decided at run time on the user's GPU; this script
only installs verified components. A failure here fails the build instead of shipping a gap.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishedDirectory,
    [Parameter(Mandatory = $true)][string]$WorkDirectory,
    [ValidateSet('fsr', 'xess', 'dlss')][string[]]$Providers = @('fsr', 'xess', 'dlss')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$published = (Resolve-Path -LiteralPath $PublishedDirectory).Path.TrimEnd('\', '/')
New-Item -ItemType Directory -Force -Path $WorkDirectory | Out-Null
$work = (Resolve-Path -LiteralPath $WorkDirectory).Path
$fetched = Join-Path $work 'upscalers'
& (Join-Path $PSScriptRoot 'fetch-upscalers.ps1') -Destination $fetched -Providers $Providers
if (-not $?) { throw 'Fetching upscaler runtimes failed.' }

if ($Providers -contains 'dlss') {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'vswhere.exe not found; the DLSS bridge needs the MSVC toolset.' }
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'No Visual Studio installation with the x64 C++ toolset was found.' }
    $vcvars = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
    # Import the x64 developer environment into this process so cl.exe links against the matching CRT.
    $lines = cmd /c "`"$vcvars`" >nul && set"
    foreach ($line in $lines) { if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2]) } }
    $bridge = Join-Path $work 'dlss-bridge'
    & (Join-Path $PSScriptRoot 'build-dlss.ps1') -SdkDirectory (Join-Path $fetched 'sdk/dlss') -OutputDirectory $bridge
    if (-not (Test-Path -LiteralPath (Join-Path $bridge 'mmd_dlss.dll'))) { throw 'DLSS bridge build produced no mmd_dlss.dll.' }
}

$root = Join-Path $published 'tools/upscaling'
foreach ($provider in $Providers) {
    $target = Join-Path $root $provider
    $licenses = Join-Path $root "licenses/$provider"
    New-Item -ItemType Directory -Force -Path $target, $licenses | Out-Null
    Copy-Item -Path (Join-Path $fetched "runtime/$provider/*") -Destination $target
    Copy-Item -Path (Join-Path $fetched "licenses/$provider/*") -Destination $licenses
    if ($provider -eq 'dlss') {
        Copy-Item -LiteralPath (Join-Path $bridge 'mmd_dlss.dll') -Destination $target
        Copy-Item -LiteralPath (Join-Path $bridge 'build-manifest.json') -Destination (Join-Path $licenses 'mmd_dlss-build-manifest.json')
    }
}

# The vendor DLLs and the bridge use the dynamic VC runtime; stage the pinned, signed copies beside them.
$vc = $null
try { $vc = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '../tools/upscaling/manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json).vcRuntime } catch { }
if (-not $vc) { throw 'tools/upscaling/manifest.json lacks the vcRuntime download pins.' }
$redist = Join-Path $work 'vc-redist.vsix'
$license = Join-Path $work 'Visual-C-V14-Runtime-2026-License.docx'
Invoke-WebRequest -Uri $vc.redistUrl -OutFile $redist -UseBasicParsing
Invoke-WebRequest -Uri $vc.licenseUrl -OutFile $license -UseBasicParsing
if ((Get-FileHash -LiteralPath $redist -Algorithm SHA256).Hash -ine $vc.redistSha256) { throw 'VC runtime archive hash mismatch.' }
if ((Get-FileHash -LiteralPath $license -Algorithm SHA256).Hash -ine $vc.licenseSha256) { throw 'VC runtime license hash mismatch.' }
if ($Providers.Count -eq 3) {
    & (Join-Path $PSScriptRoot 'stage-editor-crt.ps1') -PublishDirectory $published -RedistArchivePath $redist -LicensePath $license
} else {
    Write-Warning 'stage-editor-crt.ps1 stages all three providers; skipped because only a subset was installed.'
}

$installed = @(Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($published.Length + 1).Replace('\', '/'); bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[IO.File]::WriteAllText((Join-Path $root 'installed.json'),
    ([ordered]@{ schemaVersion = 1; providers = $Providers; files = $installed } | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Output ("Staged {0} upscaler files for: {1}" -f $installed.Count, ($Providers -join ', '))
