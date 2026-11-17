param(
    [Parameter(Mandatory=$true)][string]$SdkDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$Compiler = 'cl.exe'
)
$ErrorActionPreference = 'Stop'
$sdk = [IO.Path]::GetFullPath($SdkDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $sdk 'nvsdk_ngx_d.lib'))) { throw 'Pinned NVIDIA NGX SDK headers and nvsdk_ngx_d.lib are required. See bridge-manifest.json in the optional SDK cache.' }
if (-not (Get-Command $Compiler -ErrorAction SilentlyContinue)) { throw 'Run from a Visual Studio x64 native-tools environment, or supply its cl.exe and matching INCLUDE/LIB environment. NGX uses the Microsoft C++ runtime ABI.' }
[void][IO.Directory]::CreateDirectory($output)
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../native/Mmd.Modern.Dlss/mmd_dlss.cpp'))
$library = Join-Path $sdk 'nvsdk_ngx_d.lib'
$dll = Join-Path $output 'mmd_dlss.dll'
$arguments = @('/nologo','/LD','/MD','/O2','/EHsc','/std:c++17',('/I'+$sdk),$source,('/Fo'+(Join-Path $output 'mmd_dlss.obj')),'/link',('/OUT:'+$dll),('/IMPLIB:'+(Join-Path $output 'mmd_dlss.lib')),$library,'advapi32.lib','user32.lib','shell32.lib','ole32.lib','version.lib','wintrust.lib','crypt32.lib')
& $Compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "DLSS bridge compiler failed: $LASTEXITCODE" }
$manifest = [ordered]@{source=$source;sourceSha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant();sdkLibrary=$library;sdkLibrarySha256=(Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash.ToLowerInvariant();sdkCommit='9981c6350dc58c0ce21f024fd65e31e851c31689';compiler=$Compiler;arguments=$arguments;output=$dll;sha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $output 'build-manifest.json')
