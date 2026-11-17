param(
    [string]$Model,
    [string]$Project,
    [string]$Motion,
    [switch]$PlayMotion,
    [string]$ToonDirectory,
    [switch]$SelfTest,
    [ValidateRange(300,3000)][int]$SelfTestFrames = 320,
    [string]$Output,
    [string]$CacheRoot,
    [switch]$DemoRoom,
    [ValidateSet('legacy','fit')][string]$ShadowProjection = 'legacy',
    [ValidateSet('additive','toon')][string]$GiComposition = 'additive',
    [ValidateSet('off','spatial')][string]$GiDenoiser = 'off',
    [ValidateSet('off','smaa')][string]$Antialiasing = 'off',
    [ValidateRange(0,1)][double]$RoomEmission = 0
)
$ErrorActionPreference = 'Stop'
$arguments = @('run', '--project', 'src/Mmd.Modern.Demo', '-c', 'Release')
if ($CacheRoot) { $arguments += "-p:ModernCacheRoot=$([IO.Path]::GetFullPath($CacheRoot))/" }
$arguments += @('--', '--frame-viewer')
$arguments += @('--shadow-projection', $ShadowProjection)
$arguments += @('--gi-composition', $GiComposition)
$arguments += @('--gi-denoiser', $GiDenoiser)
$arguments += @('--antialiasing', $Antialiasing)
$arguments += @('--room-emission', $RoomEmission.ToString('R', [Globalization.CultureInfo]::InvariantCulture))
if ($DemoRoom) { $arguments += '--demo-room' }
if ($Model) { $arguments += @('--model', [IO.Path]::GetFullPath($Model)) }
if ($Project) { $arguments += @('--project', [IO.Path]::GetFullPath($Project)) }
if ($Motion) { $arguments += @('--motion', [IO.Path]::GetFullPath($Motion)) }
if ($PlayMotion) { $arguments += '--play-motion' }
if ($ToonDirectory) { $arguments += @('--toon-dir', [IO.Path]::GetFullPath($ToonDirectory)) }
if ($SelfTest) { $arguments += @('--viewport-self-test', '--self-test-frames', "$SelfTestFrames") }
if ($Output) { $arguments += @('--output', [IO.Path]::GetFullPath($Output)) }
& (Join-Path $PSScriptRoot 'dotnet.ps1') -DotNetArguments $arguments
