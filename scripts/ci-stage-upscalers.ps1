#Requires -Version 5.1
<#
.SYNOPSIS
CI 用：把可选的 FSR、XeSS、DLSS 超分加入已发布的 Windows 构建。
.DESCRIPTION
1. 下载固定版本的厂商运行库和 NGX SDK（scripts/fetch-upscalers.ps1）。
2. 用 vswhere 找到的 MSVC 编译 DLSS 桥接库 mmd_dlss.dll。
3. 运行库放进 <Published>/tools/upscaling/{fsr,xess,dlss}，许可证放进
   <Published>/tools/upscaling/licenses/{fsr,xess,dlss}。
4. 在每个超分旁放好固定版本的微软 VC 运行库（scripts/stage-editor-crt.ps1）。
5. 写出 tools/upscaling/installed.json，列出每个文件及其哈希。
能否实际使用由用户的显卡在运行时决定；本脚本只安装校验过的组件。任何一步失败都会让构建失败，不会悄悄缺件。
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
if (-not $?) { throw '下载超分运行库失败。' }

if ($Providers -contains 'dlss') {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw '找不到 vswhere.exe；编译 DLSS 桥接库需要 MSVC。' }
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw '没有找到带 x64 C++ 工具集的 Visual Studio。' }
    $vcvars = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
    # 把 x64 开发环境导入当前进程，让 cl.exe 链接到匹配的 CRT。
    $lines = cmd /c "`"$vcvars`" >nul && set"
    foreach ($line in $lines) { if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2]) } }
    $bridge = Join-Path $work 'dlss-bridge'
    & (Join-Path $PSScriptRoot 'build-dlss.ps1') -SdkDirectory (Join-Path $fetched 'sdk/dlss') -OutputDirectory $bridge
    if (-not (Test-Path -LiteralPath (Join-Path $bridge 'mmd_dlss.dll'))) { throw 'DLSS 桥接库编译没有产出 mmd_dlss.dll。' }
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

# 厂商 DLL 和桥接库依赖动态 VC 运行库；把固定版本、带签名的副本放在旁边。
$vc = $null
try { $vc = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '../tools/upscaling/manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json).vcRuntime } catch { }
if (-not $vc) { throw 'tools/upscaling/manifest.json 缺少 vcRuntime 下载信息。' }
$redist = Join-Path $work 'vc-redist.vsix'
$license = Join-Path $work 'Visual-C-V14-Runtime-2026-License.docx'
foreach ($download in @(
    @{ Url = $vc.redistUrl; Out = $redist; Label = 'VC 运行库压缩包' },
    @{ Url = $vc.licenseUrl; Out = $license; Label = 'VC 运行库许可证' }
)) {
    for ($attempt = 1; ; $attempt++) {
        try {
            Invoke-WebRequest -Uri $download.Url -OutFile $download.Out -UseBasicParsing
            break
        } catch {
            Remove-Item -LiteralPath $download.Out -Force -ErrorAction SilentlyContinue
            if ($attempt -ge 3) { throw }
            Write-Warning "下载$($download.Label)失败（第 $attempt 次），$(5 * $attempt) 秒后重试：$($_.Exception.Message)"
            Start-Sleep -Seconds (5 * $attempt)
        }
    }
}
if ((Get-FileHash -LiteralPath $redist -Algorithm SHA256).Hash -ine $vc.redistSha256) { throw 'VC 运行库压缩包哈希不符。' }
if ((Get-FileHash -LiteralPath $license -Algorithm SHA256).Hash -ine $vc.licenseSha256) { throw 'VC 运行库许可证哈希不符。' }
if ($Providers.Count -eq 3) {
    & (Join-Path $PSScriptRoot 'stage-editor-crt.ps1') -PublishDirectory $published -RedistArchivePath $redist -LicensePath $license
} else {
    Write-Warning 'stage-editor-crt.ps1 要求三种超分齐全；只装了部分，已跳过。'
}

$installed = @(Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($published.Length + 1).Replace('\', '/'); bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[IO.File]::WriteAllText((Join-Path $root 'installed.json'),
    ([ordered]@{ schemaVersion = 1; providers = $Providers; files = $installed } | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Output ("已放入 {0} 个超分文件：{1}" -f $installed.Count, ($Providers -join '、'))
