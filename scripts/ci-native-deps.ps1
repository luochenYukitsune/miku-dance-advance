#Requires -Version 5.1
<#
.SYNOPSIS
CI 用：编译测试和发布都需要的原生依赖（Bullet 刚体、Bullet 软体、MikkTSpace），并取得固定版本的 DXC。
.DESCRIPTION
输出目录结构：
  <Output>/rigid/mmd_bullet.dll
  <Output>/soft/mmd_bullet_soft.dll
  <Output>/mikk/mmd_mikktspace.dll
  <Output>/dxc/dxc.exe、dxcompiler.dll、dxil.dll（逐个核对 tools/dxc/manifest.json 里的 SHA-256）
在 GitHub Actions 中运行时，会把 MSBuild 参数和 MMD_DXC_PATH 写入 GITHUB_ENV，供后续步骤使用。
#>
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $root | Out-Null
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

python -m pip install --disable-pip-version-check ziglang==0.16.0
if ($LASTEXITCODE) { throw 'Zig 安装失败。' }
$zig = python -c "import pathlib,ziglang; print(pathlib.Path(ziglang.__file__).parent/'zig.exe')"
python (Join-Path $repo 'scripts/build-bullet.py') --compiler $zig --output "$root/rigid/mmd_bullet.dll"
if ($LASTEXITCODE) { throw 'Bullet 刚体库编译失败。' }
python (Join-Path $repo 'scripts/build-bullet-soft.py') --compiler $zig --output "$root/soft/mmd_bullet_soft.dll"
if ($LASTEXITCODE) { throw 'Bullet 软体库编译失败。' }
& (Join-Path $repo 'native/Mmd.Modern.Mikktspace/build.ps1') -UseCMake -OutputDirectory "$root/mikk" -BuildDirectory "$root/mikk-build"
$mikk = Get-ChildItem -LiteralPath "$root/mikk" -Recurse -Filter mmd_mikktspace.dll | Select-Object -First 1
if (-not $mikk) { throw 'MikkTSpace 编译没有产出 mmd_mikktspace.dll。' }

$pin = Get-Content -LiteralPath (Join-Path $repo 'tools/dxc/manifest.json') -Raw | ConvertFrom-Json
$zip = Join-Path $root 'dxc.zip'
Invoke-WebRequest -Uri $pin.assetUrl -OutFile $zip -UseBasicParsing
if ((Get-FileHash -LiteralPath $zip).Hash -ine $pin.assetSha256) { throw 'DXC 压缩包哈希不符。' }
Expand-Archive -LiteralPath $zip -DestinationPath "$root/dxc-archive"
New-Item -ItemType Directory -Force -Path "$root/dxc" | Out-Null
foreach ($file in $pin.files) {
    $path = Join-Path "$root/dxc-archive" $file.path
    if ((Get-FileHash -LiteralPath $path).Hash -ine $file.sha256) { throw "DXC 文件哈希不符：$($file.path)" }
    if ($file.path -like 'bin/x64/*') { Copy-Item -LiteralPath $path -Destination "$root/dxc" }
}

$properties = "-p:BulletNativeLibrary=$root\rigid\mmd_bullet.dll -p:SoftBodyNativeLibrary=$root\soft\mmd_bullet_soft.dll -p:MikktspaceNativeLibrary=$($mikk.FullName)"
if ($env:GITHUB_ENV) {
    "MDA_NATIVE_PROPERTIES=$properties" >> $env:GITHUB_ENV
    "MMD_DXC_PATH=$root\dxc\dxc.exe" >> $env:GITHUB_ENV
}
Write-Output $properties
