#Requires -Version 5.1
<#
.SYNOPSIS
下载固定版本的 FSR、XeSS、DLSS 运行库，以及编译 DLSS 桥接库所需的 NGX SDK。
.DESCRIPTION
tools/upscaling/manifest.json 中的每个文件都来自厂商官方仓库的固定提交，SHA-256 一致才保留，不做任何修改。输出结构：
  <Destination>/runtime/{fsr,xess,dlss}/...   应用目录 tools/upscaling 下的布局
  <Destination>/licenses/{fsr,xess,dlss}/...  厂商许可证
  <Destination>/sdk/dlss/            供 scripts/build-dlss.ps1 使用的 NGX 头文件和 nvsdk_ngx_d.lib
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
if (Test-Path -LiteralPath $root) { throw "输出目录必须是新目录：$root" }
New-Item -ItemType Directory -Path $root | Out-Null

foreach ($provider in $Providers) {
    $entry = $pin.providers.$provider
    if (-not $entry) { throw "清单中没有这个超分：$provider" }
    foreach ($file in $entry.files) {
        $folder = switch ($file.role) {
            'runtime' { "runtime/$provider" }
            'license' { "licenses/$provider" }
            'sdk' { "sdk/$provider" }
            default { throw "未知的文件类型：$($file.role)" }
        }
        $target = Join-Path $root (Join-Path $folder $file.path)
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        for ($attempt = 1; ; $attempt++) {
            try { Invoke-WebRequest -Uri $file.url -OutFile $target -UseBasicParsing; break }
            catch { if ($attempt -ge 3) { throw }; Start-Sleep -Seconds (5 * $attempt) }
        }
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($actual -ine $file.sha256) { throw "SHA-256 不符：$provider/$($file.path)（实际 $actual）" }
    }
    # 厂商 DLL 都带 Authenticode 签名；这里列出签名结果，供人工核对。
    foreach ($dll in Get-ChildItem -LiteralPath (Join-Path $root "runtime/$provider") -Filter *.dll -ErrorAction SilentlyContinue) {
        $signature = Get-AuthenticodeSignature -LiteralPath $dll.FullName
        Write-Output ("{0}/{1}：签名 {2}（{3}）" -f $provider, $dll.Name, $signature.Status, $signature.SignerCertificate.Subject)
    }
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $root 'manifest.json')
Write-Output $root
