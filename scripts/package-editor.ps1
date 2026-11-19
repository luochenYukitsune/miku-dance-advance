<#
.SYNOPSIS
Stages an already published self-contained Windows x64 editor. Does not build, publish or run tests.
.DESCRIPTION
The default output is an internal, new .dev-cache directory. Only -CompileInstaller invokes Inno
Setup, after a completed release evidence file is supplied. No existing directory is removed or
overwritten. User projects, originals and LocalApplicationData settings are never modified.
Classic is omitted unless both explicit Classic input directories are supplied. Its app-local
Microsoft.DXSDK.D3DX 9.29.952.8 files and full terms are hash-pinned. A stage without Classic
release evidence remains unavailable at startup; compiling that component requires four passed
App composition/deployment checks bound to the exact App and native DLL.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version = '0.1.0',
    [string]$StageDirectory,
    [string]$DxcDirectory,
    [string]$FfmpegDirectory,
    [string]$FfmpegEvidenceDirectory,
    [string]$FfmpegSourceDirectory,
    [string]$NugetPackages,
    [string]$ClassicNativeBuildDirectory,
    [string]$ClassicDirectXPackageDirectory,
    [string]$ClassicReleaseEvidence,
    [switch]$CompileInstaller,
    [string]$ReleaseEvidence,
    [string]$IsccPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..'))
$published = (Resolve-Path -LiteralPath $PublishDirectory).Path.TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $published -PathType Container)) { throw 'PublishDirectory must be a directory.' }

function Write-JsonFile($Value, [string]$Path) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 15), [Text.UTF8Encoding]::new($false))
}
function Copy-One([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "Required file missing: $Source" }
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($Destination)) | Out-Null
    if (Test-Path -LiteralPath $Destination) { throw "Refusing to overwrite: $Destination" }
    Copy-Item -LiteralPath $Source -Destination $Destination
}
function Find-PayloadFile([string]$Name) {
    $candidates = @((Join-Path $published $Name), (Join-Path $published "runtimes/win-x64/native/$Name"))
    return @($candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)
}
function Test-ExcludedPublishFile([string]$RelativePath) {
    return $RelativePath -match '(^|[\\/])(fixtures|tests|artifacts)([\\/]|$)' -or
        $RelativePath -match '(^|[\\/])tools[\\/]classic([\\/]|$)' -or
        [IO.Path]::GetFileName($RelativePath) -in @('Mmd.Modern.D3D9.Compatibility.dll', 'd3dx9_43.dll', 'd3dcompiler_43.dll') -or
        [IO.Path]::GetExtension($RelativePath) -eq '.pdb' -or
        [IO.Path]::GetFileName($RelativePath) -match '^Mmd\.Modern\.(Demo|Smoke)\.'
}
function Assert-ReleasePayloadBinding([object[]]$BoundFiles) {
    $publishFiles = @{}
    $requiredBindings = @{}
    foreach ($file in Get-ChildItem -LiteralPath $published -Recurse -File) {
        $relative = $file.FullName.Substring($published.Length + 1).Replace('\', '/')
        $publishFiles[$relative] = $file
        if (-not (Test-ExcludedPublishFile $relative)) { $requiredBindings[$relative] = $true }
    }
    $seenBindings = @{}
    $normalizedBindings = [Collections.Generic.List[object]]::new()
    foreach ($identity in $BoundFiles) {
        if ($null -eq $identity -or $identity.PSObject.Properties.Name -notcontains 'path' -or
            $identity.PSObject.Properties.Name -notcontains 'sha256' -or
            $identity.path -isnot [string] -or [string]::IsNullOrWhiteSpace($identity.path) -or
            $identity.sha256 -isnot [string] -or $identity.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
            [IO.Path]::IsPathRooted($identity.path)) {
            throw 'Release evidence payload binding has an invalid relative path/SHA256.'
        }
        $boundPath = [IO.Path]::GetFullPath((Join-Path $published $identity.path))
        if (-not $boundPath.StartsWith($published + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Release evidence payload binding escapes PublishDirectory: $($identity.path)"
        }
        $relative = $boundPath.Substring($published.Length + 1).Replace('\', '/')
        if ($seenBindings.ContainsKey($relative)) { throw "Release evidence payload binding has a duplicate path: $relative" }
        if (-not $publishFiles.ContainsKey($relative) -or
            (Get-FileHash -LiteralPath $boundPath -Algorithm SHA256).Hash -ne $identity.sha256) {
            throw "Release evidence no longer matches this publish: $($identity.path)"
        }
        $seenBindings[$relative] = $true
        $requiredBindings.Remove($relative)
        $normalizedBindings.Add([pscustomobject]@{ path = $relative; sha256 = $identity.sha256.ToLowerInvariant() })
    }
    if ($requiredBindings.Count -ne 0) {
        throw "Release evidence payload binding is missing published files: $(@($requiredBindings.Keys | Sort-Object) -join ', ')"
    }
    return $normalizedBindings.ToArray()
}

# A normal framework-dependent bin folder is intentionally rejected. RID-specific publishing is
# necessary for Avalonia/Skia native assets; a runtimeconfig alone does not establish portability.
$required = @('Mmd.Modern.App.exe', 'Mmd.Modern.App.dll', 'Mmd.Modern.App.deps.json',
    'Mmd.Modern.App.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
    'System.Private.CoreLib.dll', 'Mmd.Modern.Rendering.D3D12.dll', 'Mmd.Modern.ViewOutput.D3D12.dll',
    'mmd_bullet.dll', 'mmd_bullet_soft.dll', 'mmd_mikktspace.dll',
    'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'av_libglesv2.dll')
foreach ($name in $required) {
    if (@(Find-PayloadFile $name).Count -eq 0) { throw "Not a complete win-x64 self-contained publish: missing $name" }
}
& (Join-Path $PSScriptRoot 'verify-bundled-plugins.ps1') -AppDirectory $published -SkipRuntimeLoad
# These vendor DLLs use the dynamic VC runtime. The explicit DLL_LOAD_DIR loader policy
# resolves dependencies beside each entry DLL; an installed developer runtime is not a
# deployable prerequisite. Keep this check separate from the self-contained .NET check.
$crtProviders = @{
    fsr = 'amd_fidelityfx_loader_dx12.dll'
    xess = 'libxess.dll'
    dlss = 'mmd_dlss.dll'
}
$includedCrtProviders = @($crtProviders.Keys | Where-Object {
    Test-Path -LiteralPath (Join-Path $published ('tools/upscaling/' + $_ + '/' + $crtProviders[$_])) -PathType Leaf
})
if ($includedCrtProviders.Count -gt 0) {
    $crtNoticeRoot = Join-Path $published 'tools/upscaling/licenses/vc-runtime'
    $crtProvenancePath = Join-Path $crtNoticeRoot 'provenance.json'
    if (-not (Test-Path -LiteralPath $crtProvenancePath -PathType Leaf)) {
        throw 'Vendor runtime payload needs the VC runtime provenance and license. Run scripts/stage-editor-crt.ps1 first.'
    }
    $crtProvenance = Get-Content -LiteralPath $crtProvenancePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($crtProvenance.schemaVersion -ne 1 -or $crtProvenance.unmodified -ne $true -or
        $crtProvenance.architecture -ne 'AMD64' -or
        [version]$crtProvenance.fileVersion -lt [version]'14.44.35229.0') { throw 'Invalid or outdated VC runtime provenance.' }
    $crtLicensePath = Join-Path $crtNoticeRoot 'Visual-C-V14-Runtime-2026-License.docx'
    if (-not (Test-Path -LiteralPath $crtLicensePath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $crtLicensePath -Algorithm SHA256).Hash -ne $crtProvenance.license.sha256) {
        throw 'The complete VC runtime license is missing or differs from provenance.'
    }
    foreach ($crtName in 'msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll') {
        $crtIdentity = @($crtProvenance.files | Where-Object { $_.name -eq $crtName })
        if ($crtIdentity.Count -ne 1 -or $crtIdentity[0].sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
            throw "VC runtime provenance needs one identity: $crtName"
        }
        foreach ($provider in $includedCrtProviders) {
            $crtPath = Join-Path $published ('tools/upscaling/' + $provider + '/' + $crtName)
            if (-not (Test-Path -LiteralPath $crtPath -PathType Leaf) -or
                (Get-FileHash -LiteralPath $crtPath -Algorithm SHA256).Hash -ne $crtIdentity[0].sha256) {
                throw "Vendor runtime dependency is missing or changed: $provider/$crtName"
            }
        }
    }
}
$verifiedNative = @{
    'mmd_bullet.dll' = '.dev-cache/bullet-cluster-bounds-20261114/rigid/mmd_bullet.dll'
    'mmd_bullet_soft.dll' = '.dev-cache/bullet-cluster-bounds-20261114/soft/mmd_bullet_soft.dll'
    'mmd_mikktspace.dll' = '.dev-cache/mikktspace-native/mmd_mikktspace.dll'
}
foreach ($name in $verifiedNative.Keys) {
    $verified = Join-Path $workspaceRoot $verifiedNative[$name]
    if (-not (Test-Path -LiteralPath $verified -PathType Leaf) -or
        (Get-FileHash -LiteralPath (Find-PayloadFile $name) -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $verified -Algorithm SHA256).Hash) {
        throw "Native publish does not match the current verified runtime: $name ($verified). Do not package the old shared Bullet DLL."
    }
}
$runtime = Get-Content -LiteralPath (Join-Path $published 'Mmd.Modern.App.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($runtime.runtimeOptions.PSObject.Properties.Name -contains 'framework' -or
    $runtime.runtimeOptions.PSObject.Properties.Name -contains 'frameworks') {
    throw 'Framework-dependent runtimeconfig is not accepted. Publish with --self-contained true.'
}
$deps = Get-Content -LiteralPath (Join-Path $published 'Mmd.Modern.App.deps.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($deps.runtimeTarget.name -notmatch '/win-x64$') { throw 'The publish runtime target must be win-x64.' }

# Optional Classic inputs are assembled independently of the common managed adapters. Only
# this allowlist is copied: the project DLL statically contains the pinned MIT host; the two
# unmodified Microsoft release runtimes come from Microsoft.DXSDK.D3DX 9.29.952.8.
$classicFiles = [Collections.Generic.List[object]]::new()
$classicProvenance = $null
$classicReady = $false
$classicNativeIdentity = $null
if ([bool]$ClassicNativeBuildDirectory -ne [bool]$ClassicDirectXPackageDirectory) {
    throw 'Supply both -ClassicNativeBuildDirectory and -ClassicDirectXPackageDirectory, or neither.'
}
if ($ClassicReleaseEvidence -and -not $ClassicNativeBuildDirectory) { throw 'Classic release evidence requires its explicit runtime inputs.' }
if ($ClassicNativeBuildDirectory) {
    $classicBuild = (Resolve-Path -LiteralPath $ClassicNativeBuildDirectory).Path
    $classicPackage = (Resolve-Path -LiteralPath $ClassicDirectXPackageDirectory).Path
    $classicSummary = Get-Content -LiteralPath (Join-Path $classicBuild 'summary.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $classicNative = Join-Path $classicBuild 'Mmd.Modern.D3D9.Compatibility.dll'
    $classicNativeIdentity = (Get-FileHash -LiteralPath $classicNative -Algorithm SHA256).Hash.ToLowerInvariant()
    $hostManifestHash = (Get-FileHash -LiteralPath (Join-Path $projectRoot 'compatibility/d3d9-mme-host/manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($classicSummary.schemaVersion -ne 1 -or $classicSummary.status -ne 'dll_linked_not_executed' -or
        $classicSummary.dllSha256 -ne $classicNativeIdentity -or $classicSummary.hostManifestSha256 -ne $hostManifestHash -or
        $classicSummary.hostArchiveSha256 -notmatch '^[a-f0-9]{64}$') { throw 'Classic native build identity or pinned host differs from its summary.' }
    $sourceIdentities = [Collections.Generic.List[object]]::new()
    $seenNativeSources = @{}
    foreach ($identity in $classicSummary.sourceInputs) {
        $name = [IO.Path]::GetFileName([string]$identity.path)
        $source = Join-Path $projectRoot ('native/Mmd.Modern.D3D9.Compatibility/' + $name)
        if ($seenNativeSources.ContainsKey($name) -or -not (Test-Path -LiteralPath $source -PathType Leaf) -or
            (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $identity.sha256) {
            throw "Classic native build no longer matches current source: $name"
        }
        $seenNativeSources[$name] = $true
        $sourceIdentities.Add([pscustomobject]@{ path = 'native/Mmd.Modern.D3D9.Compatibility/' + $name; sha256 = $identity.sha256 })
    }
    foreach ($name in @('mda_d3d9_session.h', 'session.cpp', 'frame_packet.hpp', 'visual_packet.hpp', 'surface.fx', 'build.py')) {
        if (-not $seenNativeSources.ContainsKey($name)) { throw "Classic build summary lacks current source identity: $name" }
    }
    $classicFiles.Add([pscustomobject]@{ source = $classicNative; path = 'Mmd.Modern.D3D9.Compatibility.dll'; sha256 = $classicNativeIdentity })
    $directXInputs = @(
        @{ source = 'build/native/release/bin/x64/D3DX9_43.dll'; path = 'd3dx9_43.dll'; sha256 = '36b84dfa0ef9d3ff3549a7ad54d2a8032bd22c879219ee1a959137c4ef8786c4' },
        @{ source = 'build/native/release/bin/x64/D3DCompiler_43.dll'; path = 'd3dcompiler_43.dll'; sha256 = 'ffedb064adc25328b24dcf145b04045a867a5574c931516d7845babf2a08937f' },
        @{ source = 'LICENSE.txt'; path = 'licenses/Microsoft.DXSDK.D3DX/LICENSE.txt'; sha256 = 'd287af0e16fbcee5c96fba66a3030e2cb09e3a782b80553a27fc0e19c76f02fb' },
        @{ source = 'NOTICE.md'; path = 'licenses/Microsoft.DXSDK.D3DX/NOTICE.md'; sha256 = '4345a681ab4edb11118c2c60394b3cb1aba92d8921020cc1771f50143904d768' },
        @{ source = 'README.md'; path = 'licenses/Microsoft.DXSDK.D3DX/README.md'; sha256 = '75ca088678de1da6b1852c866c1c1b957e10635d6b4990312e12963b23156959' },
        @{ source = 'Microsoft.DXSDK.D3DX.nuspec'; path = 'licenses/Microsoft.DXSDK.D3DX/Microsoft.DXSDK.D3DX.nuspec'; sha256 = '33382aa602447726502b8c39da4e63158ebd116065463bf6adf26d49172cf464' }
    )
    foreach ($identity in $directXInputs) {
        $source = Join-Path $classicPackage $identity.source
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $identity.sha256) {
            throw "Classic DirectX input differs from the pinned official NuGet package: $($identity.source)"
        }
        $classicFiles.Add([pscustomobject]@{ source = $source; path = $identity.path; sha256 = $identity.sha256 })
    }
    $hostLicense = Join-Path $projectRoot 'compatibility/d3d9-mme-host/upstream/LICENSE'
    $hostLicenseHash = 'd808f870fd8fe49e6d1e34f763d9f2590bb619a10f009f934257e2ff7c968dfc'
    if ((Get-FileHash -LiteralPath $hostLicense -Algorithm SHA256).Hash -ne $hostLicenseHash) { throw 'The fixed MIT host license differs from its pin.' }
    $classicFiles.Add([pscustomobject]@{ source = $hostLicense; path = 'licenses/MikuDanceStudio/LICENSE'; sha256 = $hostLicenseHash })
    if ($ClassicReleaseEvidence) {
        $classicAcceptance = Get-Content -LiteralPath $ClassicReleaseEvidence -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($classicAcceptance.schemaVersion -ne 1 -or $classicAcceptance.status -ne 'passed' -or
            $classicAcceptance.payload.appSha256 -ne (Get-FileHash -LiteralPath (Join-Path $published 'Mmd.Modern.App.dll') -Algorithm SHA256).Hash -or
            $classicAcceptance.payload.nativeSha256 -ne $classicNativeIdentity) { throw 'Classic acceptance is missing or bound to a different App/native payload.' }
        # portableProject covers the new App import/binding/save/open composition; existing
        # schema3 FX and X persistence evidence is referenced, not rerun by this packaging step.
        foreach ($id in @('previewExport', 'realProject', 'portableProject', 'runtimeDeployment')) {
            if (@($classicAcceptance.checks | Where-Object { $_.id -eq $id -and $_.status -eq 'passed' -and $_.evidence }).Count -ne 1) {
                throw "Classic acceptance needs one passed check with evidence: $id"
            }
        }
        $classicReady = $true
    }
    if ($CompileInstaller -and -not $classicReady) { throw 'Classic installer compilation requires -ClassicReleaseEvidence for this exact App/native payload.' }
    $classicProvenance = [ordered]@{ schemaVersion = 1; component = 'MDA.Classic'; originalMmdOrMmeBinariesIncluded = $false;
        host = @{ repository = 'https://github.com/jstzwj/MikuDanceStudio'; revision = 'bfb6297aa63a37770efcd3f7a599de5dd39be12b';
            license = 'MIT'; linkage = 'static in project native DLL'; manifestSha256 = $hostManifestHash; archiveSha256 = $classicSummary.hostArchiveSha256 };
        native = @{ sha256 = $classicNativeIdentity; sourceInputs = $sourceIdentities.ToArray(); toolSha256 = $classicSummary.toolSha256 };
        microsoft = @{ id = 'Microsoft.DXSDK.D3DX'; version = '9.29.952.8';
            source = 'https://api.nuget.org/v3-flatcontainer/microsoft.dxsdk.d3dx/9.29.952.8/microsoft.dxsdk.d3dx.9.29.952.8.nupkg';
            packageSha256 = 'ead0906ae8a26c18a7525da7490127a2110f7c58f18293738283e30e97c6ea4b';
            deployment = 'unmodified release x64, Windows application-local'; license = 'Microsoft proprietary; excluded from project AGPL license';
            licenseAcceptanceRequired = $true };
        acceptance = if ($classicReady) { @{ sha256 = (Get-FileHash -LiteralPath $ClassicReleaseEvidence -Algorithm SHA256).Hash.ToLowerInvariant();
            checks = @($classicAcceptance.checks | ForEach-Object { @{ id = $_.id; status = $_.status } }) } } else { $null } }
}

$releasePayloadIdentities = @()
if ($CompileInstaller) {
    if (-not $ReleaseEvidence) { throw 'Complete GUI_V1_COMPLETION.md and supply -ReleaseEvidence before compiling the installer.' }
    $acceptance = Get-Content -LiteralPath $ReleaseEvidence -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($acceptance.schemaVersion -ne 1 -or $acceptance.status -ne 'passed') { throw 'Release evidence must have schemaVersion 1 and status passed.' }
    foreach ($id in @('editorWorkflow', 'gpuAndVisual', 'saveReopen', 'imageAndVideoExport', 'localizationAndKeyboard', 'outsideWorkspace')) {
        $check = @($acceptance.checks | Where-Object { $_.id -eq $id -and $_.status -eq 'passed' -and $_.evidence })
        if ($check.Count -ne 1) { throw "Release evidence needs one passed check with evidence: $id" }
    }
    # Bind every included publish file, including the app host/configuration, CLR, managed and
    # native/vendor runtimes. Stage-generated notices stay outside this publish binding;
    # the separately bundled DXC/FFmpeg tools are checked against their fixed identities.
    if ($acceptance.PSObject.Properties.Name -notcontains 'payload') { throw 'Release evidence needs a complete payload.files identity binding before installer compilation.' }
    $binding = $acceptance.payload
    if ($null -eq $binding -or $binding.PSObject.Properties.Name -notcontains 'files') { throw 'Release evidence payload binding needs a files array of path/sha256 values.' }
    $releasePayloadIdentities = @(Assert-ReleasePayloadBinding -BoundFiles @($binding.files))
    if (-not $IsccPath) {
        $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($compiler) { $IsccPath = $compiler.Source }
        else {
            $IsccPath = @('C:/Program Files (x86)/Inno Setup 6/ISCC.exe', 'C:/Program Files/Inno Setup 6/ISCC.exe',
                'C:/Program Files (x86)/Inno Setup 7/ISCC.exe', 'C:/Program Files/Inno Setup 7/ISCC.exe') |
                Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        }
    }
    if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
        throw 'Inno Setup is not installed. See docs/GUI_RELEASE_PACKAGING.md; this script never installs it.'
    }
}

$dxcManifestPath = Join-Path $projectRoot 'tools/dxc/manifest.json'
$dxcManifest = Get-Content -LiteralPath $dxcManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $DxcDirectory) {
    $DxcDirectory = @((Join-Path $workspaceRoot '.dev-cache/modern-dotnet/dxc/bin/x64'),
        (Join-Path $workspaceRoot '.dev-cache/ray-shader-toolchain-20261018/tools/dxc/v1.8.2505.1/bin/x64')) |
        Where-Object { Test-Path -LiteralPath (Join-Path $_ 'dxc.exe') -PathType Leaf } | Select-Object -First 1
}
if (-not $DxcDirectory) { throw 'Pinned DXC missing. Prepare it separately with scripts/setup-dxc.ps1.' }
$dxcFiles = foreach ($entry in $dxcManifest.files) {
    $source = if ($entry.path.StartsWith('bin/')) { Join-Path $DxcDirectory ([IO.Path]::GetFileName($entry.path)) }
        else { Join-Path $projectRoot ('tools/dxc/licenses/' + $entry.path) }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or
        (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) {
        throw "Pinned DXC hash mismatch or missing file: $($entry.path)"
    }
    [pscustomobject]@{ Source = $source; Name = [IO.Path]::GetFileName($entry.path) }
}

$ffmpegEvidence = if ($FfmpegEvidenceDirectory) { (Resolve-Path -LiteralPath $FfmpegEvidenceDirectory).Path }
    else { Join-Path $projectRoot 'artifacts/stage-20261022-view-output/ffmpeg/cli-baseline' }
$ffmpegProvenance = Get-Content -LiteralPath (Join-Path $ffmpegEvidence 'provenance.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $FfmpegDirectory) { $FfmpegDirectory = [IO.Path]::GetDirectoryName($ffmpegProvenance.'ffmpeg.exe'.path) }
foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) {
    $file = Join-Path $FfmpegDirectory $name
    $identity = $ffmpegProvenance.PSObject.Properties[$name].Value
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $identity.sha256) {
        throw "FFmpeg differs from the verified pair: $name. Record and verify an updated toolchain before packaging it."
    }
}

if (-not $StageDirectory) {
    $stageName = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $StageDirectory = Join-Path $workspaceRoot ('.dev-cache/editor-release/' + $stageName)
}
$stage = [IO.Path]::GetFullPath($StageDirectory).TrimEnd('\', '/')
if (Test-Path -LiteralPath $stage) { throw "StageDirectory must be new: $stage" }
if ($stage.StartsWith($published + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $stage.Equals($published, [StringComparison]::OrdinalIgnoreCase)) { throw 'StageDirectory cannot be inside PublishDirectory.' }
foreach ($directory in Get-ChildItem -LiteralPath $published -Recurse -Directory) {
    if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Publish reparse point is not accepted: $($directory.FullName)" }
}
if ($FfmpegSourceDirectory) {
    $sourceRoot = (Resolve-Path -LiteralPath $FfmpegSourceDirectory).Path.TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container) -or
        $stage.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $sourceRoot.StartsWith($stage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $sourceRoot.Equals($stage, [StringComparison]::OrdinalIgnoreCase)) { throw 'FFmpeg sources and staging must be independent directories.' }
    foreach ($item in Get-ChildItem -LiteralPath $sourceRoot -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "FFmpeg source reparse point is not accepted: $($item.FullName)" }
    }
}
New-Item -ItemType Directory -Path $stage | Out-Null
$payload = Join-Path $stage 'payload'
New-Item -ItemType Directory -Path $payload | Out-Null
$excluded = [Collections.Generic.List[string]]::new()
foreach ($file in Get-ChildItem -LiteralPath $published -Recurse -File) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Publish reparse point is not accepted: $($file.FullName)" }
    $relative = $file.FullName.Substring($published.Length + 1)
    if (Test-ExcludedPublishFile $relative) { $excluded.Add($relative); continue }
    if ($file.Extension -match '^\.(pmx|pmd|pmm|pma|vmd|vpd|mp4|avi)$') { throw "Unexpected model, project or media in publish: $relative" }
    Copy-One $file.FullName (Join-Path $payload $relative)
}
foreach ($file in $dxcFiles) { Copy-One $file.Source (Join-Path $payload ('tools/dxc/' + $file.Name)) }
if ($classicFiles.Count -ne 0) {
    $classicDestination = Join-Path $payload 'tools/classic'
    foreach ($file in $classicFiles) {
        if ((Get-FileHash -LiteralPath $file.source -Algorithm SHA256).Hash -ne $file.sha256) { throw "Classic source changed during staging: $($file.path)" }
        Copy-One $file.source (Join-Path $classicDestination $file.path)
    }
    Write-JsonFile $classicProvenance (Join-Path $classicDestination 'provenance.json')
    $classicIdentities = @(Get-ChildItem -LiteralPath $classicDestination -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ path = $_.FullName.Substring($classicDestination.Length + 1).Replace('\', '/'); bytes = $_.Length;
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    Write-JsonFile ([ordered]@{ schemaVersion = 1; component = 'MDA.Classic'; architecture = 'win-x64'; abiVersion = 1;
        hostRevision = 'bfb6297aa63a37770efcd3f7a599de5dd39be12b'; productionReady = $classicReady;
        appSha256 = (Get-FileHash -LiteralPath (Join-Path $published 'Mmd.Modern.App.dll') -Algorithm SHA256).Hash.ToLowerInvariant();
        files = $classicIdentities }) (Join-Path $classicDestination 'classic-runtime.json')
}
Copy-One $dxcManifestPath (Join-Path $payload 'tools/dxc/upstream-manifest.json')
foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) { Copy-One (Join-Path $FfmpegDirectory $name) (Join-Path $payload ('tools/ffmpeg/' + $name)) }
foreach ($name in @('provenance.json', 'version.txt', 'buildconf.txt', 'license.txt')) {
    Copy-One (Join-Path $ffmpegEvidence $name) (Join-Path $payload ('licenses/FFmpeg/' + $name))
}
$ffmpegFullLicense = Join-Path ([IO.Path]::GetDirectoryName($FfmpegDirectory)) 'LICENSE'
if (Test-Path -LiteralPath $ffmpegFullLicense -PathType Leaf) { Copy-One $ffmpegFullLicense (Join-Path $payload 'licenses/FFmpeg/COPYING.GPLv3') }
$ffmpegSourceMaterialFileCount = 0
if ($FfmpegSourceDirectory) {
    $ffmpegSources = (Resolve-Path -LiteralPath $FfmpegSourceDirectory).Path.TrimEnd('\', '/')
    foreach ($file in Get-ChildItem -LiteralPath $ffmpegSources -Recurse -File) {
        Copy-One $file.FullName (Join-Path $payload ('licenses/FFmpeg/corresponding-source/' + $file.FullName.Substring($ffmpegSources.Length + 1)))
        $ffmpegSourceMaterialFileCount++
    }
}

Copy-One (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') (Join-Path $payload 'THIRD_PARTY_NOTICES.md')
$notices = @('third_party/smaa/LICENSE.txt', 'third_party/fsr1/LICENSE.txt', 'third_party/fsr-api/LICENSE.md',
    'third_party/xess-api/LICENSE.txt', 'third_party/dlss-api/LICENSE.txt', 'third_party/saba/LICENCE.txt',
    'third_party/nanoem-pmm/LICENSE.MIT', 'third_party/bullet-2.75/BulletLicense.txt',
    'src/Mmd.Modern.Formats/LICENSE.unicode.txt', 'src/Mmd.Modern.Formats/Resources/README.md',
    'src/Mmd.Modern.App/Assets/Icons/LICENSE-Phosphor.txt', 'src/Mmd.Modern.App/Assets/Icons/LICENSE-SukiUI.txt')
foreach ($relative in $notices) { Copy-One (Join-Path $projectRoot $relative) (Join-Path $payload ('licenses/source/' + $relative)) }

# Keep actual license/notice texts separate from package metadata. A nuspec with an SPDX
# expression or license URL does not count as collecting the corresponding full license text.
# The published deps inventory also includes RID-specific packages absent from the base lock group.
# This material collection does not establish an exhaustive distribution compliance review.
if (-not $NugetPackages) { $NugetPackages = Join-Path $workspaceRoot '.dev-cache/modern-dotnet/packages' }
$NugetPackages = [IO.Path]::GetFullPath($NugetPackages)
$packageNotices = foreach ($entry in $deps.libraries.PSObject.Properties | Sort-Object Name) {
    if ($entry.Value.type -notin @('package', 'runtimepack')) { continue }
    $separator = $entry.Name.LastIndexOf('/')
    if ($separator -lt 1) { throw "Invalid published package identity: $($entry.Name)" }
    $packageId = $entry.Name.Substring(0, $separator) -replace '^runtimepack\.', ''
    $packageVersion = $entry.Name.Substring($separator + 1)
    $directory = Join-Path $NugetPackages ($packageId.ToLowerInvariant() + '/' + $packageVersion)
    $licenses = @()
    $notices = @()
    $metadata = @()
    $declaredLicenseType = $null
    $declaredLicenseValue = $null
    if (Test-Path -LiteralPath $directory -PathType Container) {
        $files = @(Get-ChildItem -LiteralPath $directory -File -Recurse)
        $licenses = @($files | Where-Object { $_.Name -match '^(licen[cs]e|copying)' -and $_.Extension -ne '.nuspec' })
        $notices = @($files | Where-Object { $_.Name -match '^(notice|third[._ -]?party)' -and $_.Extension -ne '.nuspec' })
        $metadata = @($files | Where-Object { $_.Extension -eq '.nuspec' })
        foreach ($file in $metadata) {
            [xml]$specification = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
            $licenseElement = $specification.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="license"]')
            if ($licenseElement) {
                $declaredLicenseType = $licenseElement.GetAttribute('type')
                $declaredLicenseValue = $licenseElement.InnerText
                if ($declaredLicenseType -eq 'file') {
                    $licensePath = [IO.Path]::GetFullPath((Join-Path $directory $declaredLicenseValue))
                    if ($licensePath.StartsWith($directory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
                        (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
                        $licenses = @($licenses) + @(Get-Item -LiteralPath $licensePath)
                    }
                }
            }
            Copy-One $file.FullName (Join-Path $payload ('licenses/NuGet/' + $packageId + '/' + $packageVersion + '/metadata/' + $file.FullName.Substring($directory.Length + 1)))
        }
        $licenses = @($licenses | Sort-Object FullName -Unique)
        $licensePaths = @($licenses | ForEach-Object { $_.FullName })
        $notices = @($notices | Sort-Object FullName -Unique | Where-Object { $_.FullName -notin $licensePaths })
        foreach ($file in @($licenses) + @($notices)) {
            Copy-One $file.FullName (Join-Path $payload ('licenses/NuGet/' + $packageId + '/' + $packageVersion + '/texts/' + $file.FullName.Substring($directory.Length + 1)))
        }
    }
    $contentHash = if ($entry.Value.PSObject.Properties.Name -contains 'sha512') { $entry.Value.sha512 } else { $null }
    [pscustomobject]@{ id = $packageId; version = $packageVersion; publishedLibraryType = $entry.Value.type;
        inventorySource = 'Mmd.Modern.App.deps.json'; runtimeTarget = $deps.runtimeTarget.name; contentHash = $contentHash;
        collectedLicenseFiles = $licenses.Count; collectedNoticeFiles = $notices.Count; collectedMetadataFiles = $metadata.Count;
        missingLicenseText = ($licenses.Count -eq 0); packageCacheMissing = -not (Test-Path -LiteralPath $directory -PathType Container);
        declaredLicenseType = $declaredLicenseType; declaredLicenseValue = $declaredLicenseValue;
        licenseFiles = @($licenses | ForEach-Object { $_.FullName.Substring($directory.Length + 1).Replace('\', '/') });
        noticeFiles = @($notices | ForEach-Object { $_.FullName.Substring($directory.Length + 1).Replace('\', '/') });
        metadataFiles = @($metadata | ForEach-Object { $_.FullName.Substring($directory.Length + 1).Replace('\', '/') }) }
}
New-Item -ItemType Directory -Force -Path (Join-Path $payload 'licenses/NuGet') | Out-Null
Write-JsonFile @($packageNotices) (Join-Path $payload 'licenses/NuGet/package-notices.json')
Copy-One (Join-Path $projectRoot 'src/Mmd.Modern.App/packages.lock.json') (Join-Path $payload 'licenses/NuGet/packages.lock.json')
# Self-contained runtime publications may carry these independently of NuGet's package texts.
$runtimeNotices = @(Get-ChildItem -LiteralPath $published -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.txt|\.md)?$' })
New-Item -ItemType Directory -Force -Path (Join-Path $payload 'licenses/dotnet-runtime') | Out-Null
foreach ($file in $runtimeNotices) { Copy-One $file.FullName (Join-Path $payload ('licenses/dotnet-runtime/' + $file.Name)) }
Write-JsonFile ([ordered]@{ inventorySource = 'published directory'; collectedFiles = @($runtimeNotices | ForEach-Object { $_.Name });
    missingPublishedRuntimeNotice = (@($runtimeNotices | Where-Object { $_.Name -match '^THIRD-PARTY-NOTICES(\.|$)' }).Count -eq 0);
    missingPublishedRuntimeLicense = (@($runtimeNotices | Where-Object { $_.Name -match '^LICENSE(\.|$)' }).Count -eq 0);
    complianceReviewComplete = $false }) (Join-Path $payload 'licenses/dotnet-runtime/collection.json')
Copy-One (Join-Path $projectRoot 'docs/RELEASE_NOTES_V1.md') (Join-Path $payload 'RELEASE_NOTES.md')
if ($CompileInstaller) { Copy-One (Resolve-Path -LiteralPath $ReleaseEvidence).Path (Join-Path $payload 'release-acceptance.json') }
$files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ path = $_.FullName.Substring($payload.Length + 1).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
Write-JsonFile ([ordered]@{ schemaVersion = 1; product = 'MDA'; version = $Version; runtime = 'win-x64'; selfContained = $true;
    installerCompiled = $false; generatedUtc = [DateTime]::UtcNow.ToString('O'); ffmpegLicense = 'GPL-3.0-or-later';
    releaseEvidencePayloadBound = ($releasePayloadIdentities.Count -ne 0);
    classicComponentIncluded = ($classicFiles.Count -ne 0); classicProductionReady = $classicReady;
    # Copying a supplied folder proves transport only. This script does not yet verify
    # exact FFmpeg/static-dependency revisions, modifications and build materials.
    ffmpegSourceMaterialIncluded = ($ffmpegSourceMaterialFileCount -gt 0);
    ffmpegSourceMaterialFileCount = $ffmpegSourceMaterialFileCount;
    ffmpegCorrespondingSourceIncluded = $false; ffmpegCorrespondingSourceStatus = 'not-verified';
    excludedDevelopmentFiles = @($excluded); files = $files }) (Join-Path $payload 'package-manifest.json')

if ($CompileInstaller) {
    # Re-enumerate as well as re-hash, so files added after the acceptance check cannot slip in.
    $null = @(Assert-ReleasePayloadBinding -BoundFiles $releasePayloadIdentities)
    foreach ($identity in $releasePayloadIdentities) {
        if (Test-ExcludedPublishFile $identity.path) { continue }
        $boundPath = Join-Path $payload $identity.path
        if (-not (Test-Path -LiteralPath $boundPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $boundPath -Algorithm SHA256).Hash -ne $identity.sha256) {
            throw "Release evidence payload changed during staging: $payload/$($identity.path)"
        }
    }
    $installerRoot = Join-Path $workspaceRoot 'deliverables/Installer'
    $installerOutput = Join-Path $installerRoot ([IO.Path]::GetFileName($stage))
    if (Test-Path -LiteralPath $installerOutput) { throw "Installer output must be new: $installerOutput" }
    & $IsccPath ('/DStageDir=' + $payload) ('/DMdaVersion=' + $Version) ('/DInstallerOutput=' + $installerOutput) (Join-Path $projectRoot 'installer/MDA.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }
    Write-Output "Installer compiled for validation: $installerOutput"
    Write-Output 'Installation, upgrade and uninstall validation remain necessary before delivery.'
}
Write-Output "Staged payload: $payload"
Write-Output 'Staging and a manifest do not constitute software acceptance. See docs/GUI_V1_COMPLETION.md.'
