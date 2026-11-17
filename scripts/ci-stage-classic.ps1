#Requires -Version 5.1
<#
.SYNOPSIS
CI 用：编译传统（Classic）D3D9/MME 兼容层并放入已发布的 Windows 构建目录。
.DESCRIPTION
1. 校验并使用固定版本 Zig 0.16.0（SHA-256 与 compatibility/d3d9-mme-host/UPSTREAM.json 一致）。
2. 调用 compatibility/d3d9-mme-host/build.py 校验 62 个输入源码并编译 MMEffectResearch.a。
3. 编译 native/Mmd.Modern.D3D9.Compatibility 生成 Mmd.Modern.D3D9.Compatibility.dll。
4. 下载并按固定 SHA-256 校验官方 Microsoft.DXSDK.D3DX 9.29.952.8 运行库与许可文件。
5. 写入 tools/classic/provenance.json 与 classic-runtime.json，并核对全部 9 项文件哈希与主程序绑定。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishedDirectory,
    [Parameter(Mandatory = $true)][string]$WorkDirectory,
    [string]$ZigPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$published = (Resolve-Path -LiteralPath $PublishedDirectory).Path.TrimEnd('\', '/')
$appDll = Join-Path $published 'Mmd.Modern.App.dll'
if (-not (Test-Path -LiteralPath $appDll -PathType Leaf)) {
    throw "未找到已发布的 Mmd.Modern.App.dll：$appDll"
}

if (Test-Path -LiteralPath $WorkDirectory) {
    Remove-Item -LiteralPath $WorkDirectory -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $WorkDirectory | Out-Null
$work = (Resolve-Path -LiteralPath $WorkDirectory).Path

$upstreamConfig = Get-Content -LiteralPath (Join-Path $repo 'compatibility/d3d9-mme-host/UPSTREAM.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$expectedZigHash = [string]$upstreamConfig.build.zigSha256

$zig = $null
if ($ZigPath -and (Test-Path -LiteralPath $ZigPath -PathType Leaf)) {
    if ((Get-FileHash -LiteralPath $ZigPath -Algorithm SHA256).Hash -ieq $expectedZigHash) {
        $zig = (Resolve-Path -LiteralPath $ZigPath).Path
    }
}
if (-not $zig) {
    $pyZig = $null
    try {
        $pyZig = (python -c "import pathlib,ziglang; p=pathlib.Path(ziglang.__file__).parent/'zig.exe'; print(p if p.is_file() else '')" 2>$null).Trim()
    } catch { }
    if ($pyZig -and (Test-Path -LiteralPath $pyZig -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $pyZig -Algorithm SHA256).Hash -ieq $expectedZigHash)) {
        $zig = $pyZig
    }
}
if (-not $zig) {
    $zigZip = Join-Path $work 'zig-0.16.0.zip'
    Invoke-WebRequest -Uri 'https://ziglang.org/download/0.16.0/zig-x86_64-windows-0.16.0.zip' -OutFile $zigZip -UseBasicParsing
    Expand-Archive -LiteralPath $zigZip -DestinationPath (Join-Path $work 'zig-dist')
    $downloadedZig = Get-ChildItem -LiteralPath (Join-Path $work 'zig-dist') -Recurse -Filter 'zig.exe' | Select-Object -First 1
    if (-not $downloadedZig -or (Get-FileHash -LiteralPath $downloadedZig.FullName -Algorithm SHA256).Hash -ine $expectedZigHash) {
        throw '下载的 Zig 0.16.0 可执行文件哈希与固定版本不符。'
    }
    $zig = $downloadedZig.FullName
}

$hostOut = Join-Path $work 'host'
python -X utf8 (Join-Path $repo 'compatibility/d3d9-mme-host/build.py') --zig $zig --output $hostOut
if ($LASTEXITCODE -ne 0) { throw 'Classic MME 宿主静态库编译失败。' }

$dllOut = Join-Path $work 'session'
New-Item -ItemType Directory -Force -Path $dllOut | Out-Null

$compileScript = @'
import hashlib, json, os, pathlib, subprocess, sys

repo = pathlib.Path(sys.argv[1]).resolve()
zig = pathlib.Path(sys.argv[2]).resolve()
host_build = pathlib.Path(sys.argv[3]).resolve()
out_dir = pathlib.Path(sys.argv[4]).resolve()

host_dir = repo / 'compatibility' / 'd3d9-mme-host'
native_dir = repo / 'native' / 'Mmd.Modern.D3D9.Compatibility'
pinned_manifest = json.loads((native_dir / 'UPSTREAM.json').read_text(encoding='utf-8'))['host']['manifestSha256']

def sha256(p: pathlib.Path) -> str:
    h = hashlib.sha256()
    with p.open('rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()

if sha256(host_dir / 'manifest.json') != pinned_manifest:
    raise SystemExit('Host manifest hash mismatch')

summary = json.loads((host_build / 'summary.json').read_text(encoding='utf-8'))
archive = host_build / 'MMEffectResearch.a'
if (summary.get('status') != 'archive_created_not_linked_or_run'
        or summary.get('manifestSha256') != pinned_manifest
        or summary.get('archiveMemberCount') != 23
        or not archive.is_file()):
    raise SystemExit('Host static archive summary check failed')

cfg = json.loads((host_dir / 'UPSTREAM.json').read_text(encoding='utf-8'))
if sha256(zig) != cfg['build']['zigSha256']:
    raise SystemExit('Zig hash mismatch')

gen_dir = out_dir / 'generated'
gen_dir.mkdir(parents=True, exist_ok=True)
shader = (native_dir / 'surface.fx').read_text(encoding='utf-8-sig')
delim = 'MDA9_SURFACE'
if ')' + delim + '"' in shader:
    raise SystemExit('Shader delimiter collision')
(gen_dir / 'surface_source.hpp').write_text(
    f'#pragma once\nstatic constexpr char kSurfaceSource[] = R"{delim}({shader}){delim}";\n',
    encoding='utf-8')

env = os.environ.copy()
env['ZIG_LOCAL_CACHE_DIR'] = str(out_dir / 'cache' / 'local')
env['ZIG_GLOBAL_CACHE_DIR'] = str(out_dir / 'cache' / 'global')
pathlib.Path(env['ZIG_LOCAL_CACHE_DIR']).mkdir(parents=True, exist_ok=True)
pathlib.Path(env['ZIG_GLOBAL_CACHE_DIR']).mkdir(parents=True, exist_ok=True)

base = host_build / 'src' / 'third_party' / 'mmeffect'
includes = [native_dir, gen_dir, base / 'include', base / 'src', base / 'src' / 'mmhack', base / 'src' / 'mmeffect', host_build / 'src' / 'include']
inc_flags = [x for d in includes for x in ('-I', str(d))]
flags = cfg['build']['flags']

def run_zig(argv: list[str], log_name: str) -> None:
    log_path = out_dir / f'{log_name}.log'
    with log_path.open('wb') as log_stream:
        res = subprocess.run(argv, cwd=out_dir, env=env, stdout=log_stream, stderr=subprocess.STDOUT)
    if res.returncode != 0:
        sys.stderr.write(log_path.read_text(encoding='utf-8', errors='replace')[-4000:] + '\n')
        raise SystemExit(f'{log_name} failed with exit code {res.returncode}')

objs = []
for src_name in ('session.cpp', 'dependency.cpp', 'dependency_roots.cpp'):
    obj = out_dir / (src_name[:-4] + '.o')
    run_zig([str(zig), 'c++', *flags, *inc_flags, '-c', str(native_dir / src_name), '-o', str(obj)], src_name[:-4])
    objs.append(str(obj))

dll = out_dir / 'Mmd.Modern.D3D9.Compatibility.dll'
run_zig([
    str(zig), 'c++', *flags, '-shared', *objs,
    '-Wl,--whole-archive', str(archive), '-Wl,--no-whole-archive',
    '-ld3dx9_43', '-ld3d9', '-luser32', '-lgdi32', '-lgdiplus',
    '-lshell32', '-lole32', '-lwinmm', '-lshlwapi', '-ladvapi32',
    '-o', str(dll)
], 'link')
print(sha256(dll))
'@
$compileScriptPath = Join-Path $work 'build_classic_dll.py'
[IO.File]::WriteAllText($compileScriptPath, $compileScript, [Text.UTF8Encoding]::new($false))
$nativeHash = (python -X utf8 $compileScriptPath $repo $zig $hostOut $dllOut).Trim()
if ($LASTEXITCODE -ne 0 -or $nativeHash -notmatch '^[0-9a-f]{64}$') {
    throw 'Mmd.Modern.D3D9.Compatibility.dll 编译失败。'
}

$dxZip = Join-Path $work 'dxsdk-d3dx.zip'
$dxUrl = 'https://api.nuget.org/v3-flatcontainer/microsoft.dxsdk.d3dx/9.29.952.8/microsoft.dxsdk.d3dx.9.29.952.8.nupkg'
$expectedDxPkgHash = 'ead0906ae8a26c18a7525da7490127a2110f7c58f18293738283e30e97c6ea4b'
Invoke-WebRequest -Uri $dxUrl -OutFile $dxZip -UseBasicParsing
if ((Get-FileHash -LiteralPath $dxZip -Algorithm SHA256).Hash -ine $expectedDxPkgHash) {
    throw 'Microsoft.DXSDK.D3DX 9.29.952.8 NuGet 包哈希校验失败。'
}
$dxDir = Join-Path $work 'dxsdk'
Expand-Archive -LiteralPath $dxZip -DestinationPath $dxDir

$classicDest = Join-Path $published 'tools/classic'
if (Test-Path -LiteralPath $classicDest) {
    Remove-Item -LiteralPath $classicDest -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $classicDest | Out-Null

$copyEntries = @(
    @{ Source = (Join-Path $dllOut 'Mmd.Modern.D3D9.Compatibility.dll'); Target = 'Mmd.Modern.D3D9.Compatibility.dll'; Sha256 = $nativeHash },
    @{ Source = (Join-Path $dxDir 'build/native/release/bin/x64/D3DX9_43.dll'); Target = 'd3dx9_43.dll'; Sha256 = '36b84dfa0ef9d3ff3549a7ad54d2a8032bd22c879219ee1a959137c4ef8786c4' },
    @{ Source = (Join-Path $dxDir 'build/native/release/bin/x64/D3DCompiler_43.dll'); Target = 'd3dcompiler_43.dll'; Sha256 = 'ffedb064adc25328b24dcf145b04045a867a5574c931516d7845babf2a08937f' },
    @{ Source = (Join-Path $dxDir 'LICENSE.txt'); Target = 'licenses/Microsoft.DXSDK.D3DX/LICENSE.txt'; Sha256 = 'd287af0e16fbcee5c96fba66a3030e2cb09e3a782b80553a27fc0e19c76f02fb' },
    @{ Source = (Join-Path $dxDir 'NOTICE.md'); Target = 'licenses/Microsoft.DXSDK.D3DX/NOTICE.md'; Sha256 = '4345a681ab4edb11118c2c60394b3cb1aba92d8921020cc1771f50143904d768' },
    @{ Source = (Join-Path $dxDir 'README.md'); Target = 'licenses/Microsoft.DXSDK.D3DX/README.md'; Sha256 = '75ca088678de1da6b1852c866c1c1b957e10635d6b4990312e12963b23156959' },
    @{ Source = (Join-Path $dxDir 'Microsoft.DXSDK.D3DX.nuspec'); Target = 'licenses/Microsoft.DXSDK.D3DX/Microsoft.DXSDK.D3DX.nuspec'; Sha256 = '33382aa602447726502b8c39da4e63158ebd116065463bf6adf26d49172cf464' },
    @{ Source = (Join-Path $repo 'compatibility/d3d9-mme-host/upstream/LICENSE'); Target = 'licenses/MikuDanceStudio/LICENSE'; Sha256 = 'd808f870fd8fe49e6d1e34f763d9f2590bb619a10f009f934257e2ff7c968dfc' }
)

foreach ($entry in $copyEntries) {
    if (-not (Test-Path -LiteralPath $entry.Source -PathType Leaf)) {
        throw "缺少 Classic 组件文件：$($entry.Source)"
    }
    $actualHash = (Get-FileHash -LiteralPath $entry.Source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $entry.Sha256) {
        throw "Classic 组件文件哈希不符：$($entry.Target)（实际 $actualHash，期望 $($entry.Sha256)）"
    }
    $destPath = Join-Path $classicDest $entry.Target
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destPath)) | Out-Null
    Copy-Item -LiteralPath $entry.Source -Destination $destPath
}

$provenance = [ordered]@{
    schemaVersion = 1
    component = 'MDA.Classic'
    originalMmdOrMmeBinariesIncluded = $false
    host = [ordered]@{
        repository = 'https://github.com/jstzwj/MikuDanceStudio'
        revision = 'bfb6297aa63a37770efcd3f7a599de5dd39be12b'
        license = 'MIT'
        linkage = 'static in project native DLL'
    }
    native = [ordered]@{
        sha256 = $nativeHash
        zigSha256 = $expectedZigHash
    }
    microsoft = [ordered]@{
        id = 'Microsoft.DXSDK.D3DX'
        version = '9.29.952.8'
        packageSha256 = $expectedDxPkgHash
        license = 'Microsoft proprietary; excluded from project AGPL license'
        licenseAcceptanceRequired = $true
    }
}
[IO.File]::WriteAllText(
    (Join-Path $classicDest 'provenance.json'),
    ($provenance | ConvertTo-Json -Depth 8),
    [Text.UTF8Encoding]::new($false))

$classicFiles = @(Get-ChildItem -LiteralPath $classicDest -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = $_.FullName.Substring($classicDest.Length + 1).Replace('\', '/')
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
if ($classicFiles.Count -ne 9) {
    throw "Classic 运行库清单文件数量异常：$($classicFiles.Count)（期望 9 项）。"
}

$appSha256 = (Get-FileHash -LiteralPath $appDll -Algorithm SHA256).Hash.ToLowerInvariant()
$runtimeManifest = [ordered]@{
    schemaVersion = 1
    component = 'MDA.Classic'
    architecture = 'win-x64'
    abiVersion = 1
    hostRevision = 'bfb6297aa63a37770efcd3f7a599de5dd39be12b'
    productionReady = $true
    appSha256 = $appSha256
    files = $classicFiles
}
[IO.File]::WriteAllText(
    (Join-Path $classicDest 'classic-runtime.json'),
    ($runtimeManifest | ConvertTo-Json -Depth 8),
    [Text.UTF8Encoding]::new($false))

Write-Output ("已放入 Classic 传统引擎组件（DLL SHA-256 前 16 位：{0}…）" -f $nativeHash.Substring(0, 16))
