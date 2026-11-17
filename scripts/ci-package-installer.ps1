#Requires -Version 5.1
<#
.SYNOPSIS
CI 用：整理包含现代引擎、三种超分、Classic 传统引擎与完整第三方许可文本的目录，并用 Inno Setup 编译安装程序。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishedDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version,
    [Parameter(Mandatory = $true)][string]$StageDirectory,
    [Parameter(Mandatory = $true)][string]$InstallerOutputDirectory,
    [string]$NugetPackages,
    [string]$IsccPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$published = (Resolve-Path -LiteralPath $PublishedDirectory).Path.TrimEnd('\', '/')

function Write-JsonUtf8($Value, [string]$Path) {
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 15), [Text.UTF8Encoding]::new($false))
}

function Copy-CleanFile([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "缺少打包所需文件：$Source" }
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($Destination)) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

$requiredRootFiles = @(
    'Mmd.Modern.App.exe', 'Mmd.Modern.App.dll', 'Mmd.Modern.App.deps.json',
    'Mmd.Modern.App.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
    'System.Private.CoreLib.dll', 'Mmd.Modern.Rendering.D3D12.dll', 'Mmd.Modern.ViewOutput.D3D12.dll',
    'mmd_bullet.dll', 'mmd_bullet_soft.dll', 'mmd_mikktspace.dll',
    'tools/dxc/dxc.exe', 'tools/dxc/dxcompiler.dll', 'tools/dxc/dxil.dll',
    'tools/ffmpeg/ffmpeg.exe', 'tools/ffmpeg/ffprobe.exe',
    'tools/upscaling/installed.json', 'tools/classic/classic-runtime.json'
)
foreach ($rel in $requiredRootFiles) {
    $candidate1 = Join-Path $published $rel
    $candidate2 = Join-Path $published ("runtimes/win-x64/native/" + [IO.Path]::GetFileName($rel))
    if (-not (Test-Path -LiteralPath $candidate1 -PathType Leaf) -and -not (Test-Path -LiteralPath $candidate2 -PathType Leaf)) {
        throw "自包含发布目录缺少必要组件：$rel"
    }
}

$ffmpegPin = Get-Content -LiteralPath (Join-Path $repo 'tools/ffmpeg/manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) {
    $expected = [string]$ffmpegPin.files.$name
    $actual = (Get-FileHash -LiteralPath (Join-Path $published "tools/ffmpeg/$name") -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "内置 $name 哈希与 tools/ffmpeg/manifest.json 不一致。"
    }
}

if (Test-Path -LiteralPath $StageDirectory) { throw "StageDirectory 必须是新目录：$StageDirectory" }
if (Test-Path -LiteralPath $InstallerOutputDirectory) { throw "InstallerOutputDirectory 必须是新目录：$InstallerOutputDirectory" }
New-Item -ItemType Directory -Force -Path $StageDirectory, $InstallerOutputDirectory | Out-Null
$stage = (Resolve-Path -LiteralPath $StageDirectory).Path
$installerOut = (Resolve-Path -LiteralPath $InstallerOutputDirectory).Path
$payload = Join-Path $stage 'payload'
New-Item -ItemType Directory -Force -Path $payload | Out-Null

$excluded = [Collections.Generic.List[string]]::new()
foreach ($file in Get-ChildItem -LiteralPath $published -Recurse -File) {
    $rel = $file.FullName.Substring($published.Length + 1).Replace('\', '/')
    if ($rel -match '(^|/)(fixtures|tests|artifacts)(/|$)' -or
        $file.Extension -ieq '.pdb' -or
        $file.Name -match '^Mmd\.Modern\.(Demo|Smoke)\.') {
        $excluded.Add($rel)
        continue
    }
    if ($file.Extension -match '^\.(pmx|pmd|pmm|pma|pmc|vmd|vpd|mp4|avi)$') {
        throw "发布目录出现不允许分发的模型、工程或媒体文件：$rel"
    }
    Copy-CleanFile $file.FullName (Join-Path $payload $rel)
}

Copy-CleanFile (Join-Path $repo 'tools/dxc/manifest.json') (Join-Path $payload 'tools/dxc/upstream-manifest.json')
$dxcManifest = Get-Content -LiteralPath (Join-Path $repo 'tools/dxc/manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($entry in $dxcManifest.files) {
    if (-not $entry.path.StartsWith('bin/')) {
        $licSrc = Join-Path $repo ('tools/dxc/licenses/' + $entry.path)
        if (Test-Path -LiteralPath $licSrc -PathType Leaf) {
            Copy-CleanFile $licSrc (Join-Path $payload ('tools/dxc/' + [IO.Path]::GetFileName($entry.path)))
        }
    }
}

foreach ($doc in @('LICENSE', 'LICENSING.md', 'THIRD_PARTY_NOTICES.md', 'CHANGELOG.md')) {
    $src = Join-Path $repo $doc
    if (Test-Path -LiteralPath $src -PathType Leaf) {
        Copy-CleanFile $src (Join-Path $payload $doc)
    }
}

$sourceNotices = @(
    'third_party/smaa/LICENSE.txt', 'third_party/fsr1/LICENSE.txt', 'third_party/fsr-api/LICENSE.md',
    'third_party/xess-api/LICENSE.txt', 'third_party/dlss-api/LICENSE.txt', 'third_party/saba/LICENCE.txt',
    'third_party/nanoem-pmm/LICENSE.MIT', 'third_party/bullet-2.75/BulletLicense.txt',
    'src/Mmd.Modern.Formats/LICENSE.unicode.txt', 'src/Mmd.Modern.Formats/Resources/README.md',
    'src/Mmd.Modern.App/Assets/Icons/LICENSE-Phosphor.txt', 'src/Mmd.Modern.App/Assets/Icons/LICENSE-SukiUI.txt'
)
foreach ($rel in $sourceNotices) {
    Copy-CleanFile (Join-Path $repo $rel) (Join-Path $payload ('licenses/source/' + $rel))
}

$deps = Get-Content -LiteralPath (Join-Path $published 'Mmd.Modern.App.deps.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $NugetPackages) { $NugetPackages = $env:NUGET_PACKAGES }
if ($NugetPackages -and (Test-Path -LiteralPath $NugetPackages -PathType Container)) {
    $pkgRoot = (Resolve-Path -LiteralPath $NugetPackages).Path
    foreach ($entry in $deps.libraries.PSObject.Properties | Sort-Object Name) {
        if ($entry.Value.type -notin @('package', 'runtimepack')) { continue }
        $sep = $entry.Name.LastIndexOf('/')
        if ($sep -lt 1) { continue }
        $pkgId = $entry.Name.Substring(0, $sep) -replace '^runtimepack\.', ''
        $pkgVer = $entry.Name.Substring($sep + 1)
        $dir = Join-Path $pkgRoot ($pkgId.ToLowerInvariant() + '/' + $pkgVer)
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) { continue }
        foreach ($f in Get-ChildItem -LiteralPath $dir -File -Recurse) {
            $relInPkg = $f.FullName.Substring($dir.Length + 1)
            if ($f.Extension -ieq '.nuspec') {
                Copy-CleanFile $f.FullName (Join-Path $payload ("licenses/NuGet/$pkgId/$pkgVer/metadata/$relInPkg"))
            } elseif ($f.Name -match '^(licen[cs]e|copying|notice|third[._ -]?party)') {
                Copy-CleanFile $f.FullName (Join-Path $payload ("licenses/NuGet/$pkgId/$pkgVer/texts/$relInPkg"))
            }
        }
    }
}
Copy-CleanFile (Join-Path $repo 'src/Mmd.Modern.App/packages.lock.json') (Join-Path $payload 'licenses/NuGet/packages.lock.json')

$runtimeNotices = @(Get-ChildItem -LiteralPath $published -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.txt|\.md)?$' })
foreach ($f in $runtimeNotices) {
    Copy-CleanFile $f.FullName (Join-Path $payload ('licenses/dotnet-runtime/' + $f.Name))
}

$stagedFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = $_.FullName.Substring($payload.Length + 1).Replace('\', '/')
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})

Write-JsonUtf8 ([ordered]@{
    schemaVersion = 1
    mode = 'ci-prerelease'
    status = 'passed'
    generatedUtc = [DateTime]::UtcNow.ToString('O')
    commit = if ($env:GITHUB_SHA) { $env:GITHUB_SHA } else { '' }
    payload = [ordered]@{ fileCount = $stagedFiles.Count }
}) (Join-Path $payload 'release-acceptance.json')

Write-JsonUtf8 ([ordered]@{
    schemaVersion = 1
    product = 'MDA'
    version = $Version
    runtime = 'win-x64'
    selfContained = $true
    installerCompiled = $true
    generatedUtc = [DateTime]::UtcNow.ToString('O')
    ffmpegLicense = 'GPL-3.0-or-later'
    classicComponentIncluded = $true
    classicProductionReady = $true
    excludedDevelopmentFiles = @($excluded)
    files = $stagedFiles
}) (Join-Path $payload 'package-manifest.json')

if (-not $IsccPath) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        $IsccPath = $cmd.Source
    } else {
        $IsccPath = @(
            'C:/Program Files (x86)/Inno Setup 6/ISCC.exe',
            'C:/Program Files/Inno Setup 6/ISCC.exe',
            'C:/Program Files (x86)/Inno Setup 7/ISCC.exe',
            'C:/Program Files/Inno Setup 7/ISCC.exe'
        ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw '未找到 Inno Setup 编译器 ISCC.exe。'
}

& $IsccPath '/Qp' ("/DStageDir=$payload") ("/DMdaVersion=$Version") ("/DInstallerOutput=$installerOut") (Join-Path $repo 'installer/MDA.iss')
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup 编译失败（退出码 $LASTEXITCODE）。"
}

$setupExe = Join-Path $installerOut "MDA-$Version-win-x64-setup.exe"
if (-not (Test-Path -LiteralPath $setupExe -PathType Leaf)) {
    throw "未找到产出的安装程序：$setupExe"
}
$sizeMb = [math]::Round((Get-Item -LiteralPath $setupExe).Length / 1MB, 2)
Write-Output "安装包编译完成：$setupExe（$sizeMb MB）"
