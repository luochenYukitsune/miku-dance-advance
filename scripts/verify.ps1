param([switch]$SkipGpu)
$ErrorActionPreference = 'Stop'
$invoke = Join-Path $PSScriptRoot 'dotnet.ps1'
& $invoke -DotNetArguments @('restore', 'Mmd.Modern.slnx', '--locked-mode')
& $invoke -DotNetArguments @('build', 'Mmd.Modern.slnx', '-c', 'Release', '--no-restore')
$testArguments = @('test', 'tests/Mmd.Modern.Tests', '-c', 'Release', '--no-build', '--no-restore', '--logger', 'trx;LogFileName=full.trx', '--results-directory', 'artifacts/green')
if ($SkipGpu) { $testArguments += @('--filter', 'Category!=Gpu') }
& $invoke -DotNetArguments $testArguments
# Every test project in the solution must actually run; a test project that only exists on disk
# is how a compatibility invariant (fixed 1/60 step) silently lost its coverage before.
& $invoke -DotNetArguments @('test', 'tests/Mmd.Modern.Scheduling.Tests', '-c', 'Release', '--no-build', '--no-restore', '--logger', 'trx;LogFileName=scheduling.trx', '--results-directory', 'artifacts/green')
& $invoke -DotNetArguments @('test', 'tests/Mmd.Modern.Formats.Tests', '-c', 'Release', '--no-build', '--no-restore', '--logger', 'trx;LogFileName=formats.trx', '--results-directory', 'artifacts/green')
& $invoke -DotNetArguments @('test', 'tests/Mmd.Modern.Application.Tests', '-c', 'Release', '--no-build', '--no-restore', '--logger', 'trx;LogFileName=application.trx', '--results-directory', 'artifacts/green')
& $invoke -DotNetArguments @('test', 'tests/Mmd.Modern.ViewOutput.Tests', '-c', 'Release', '--no-build', '--no-restore', '--logger', 'trx;LogFileName=view-output.trx', '--results-directory', 'artifacts/green')
# Real codec cases require explicit MMD_FFMPEG_PATH and MMD_FFPROBE_PATH; missing tools are reported as skips.
& $invoke -DotNetArguments @('test', 'tests/Mmd.Modern.ViewOutput.FFmpeg.Tests', '-c', 'Release', '--no-build', '--no-restore', '--logger', 'trx;LogFileName=view-ffmpeg.trx', '--results-directory', 'artifacts/green')
& $invoke -DotNetArguments @('test', 'tests/Mmd.Modern.Plugins.Tests', '-c', 'Release', '--no-build', '--no-restore', '--logger', 'trx;LogFileName=plugins.trx', '--results-directory', 'artifacts/green')
& $invoke -DotNetArguments @('test', 'tests/Mmd.Modern.Rendering.D3D9.Compatibility.Tests', '-c', 'Release', '--no-build', '--no-restore', '--logger', 'trx;LogFileName=rendering-d3d9.trx', '--results-directory', 'artifacts/green')
& $invoke -DotNetArguments @('test', 'tests/Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests', '-c', 'Release', '--no-build', '--no-restore', '--logger', 'trx;LogFileName=view-output-d3d9.trx', '--results-directory', 'artifacts/green')
# Guard against a test project that exists in the solution but is never run here.
$ran = @('Mmd.Modern.Tests', 'Mmd.Modern.Scheduling.Tests', 'Mmd.Modern.Formats.Tests', 'Mmd.Modern.Application.Tests',
    'Mmd.Modern.ViewOutput.Tests', 'Mmd.Modern.ViewOutput.FFmpeg.Tests', 'Mmd.Modern.Plugins.Tests',
    'Mmd.Modern.Rendering.D3D9.Compatibility.Tests', 'Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests')
$solution = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Mmd.Modern.slnx') -Raw
$declared = [regex]::Matches($solution, 'tests/([^/"]+\.Tests)/[^"]+\.csproj') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
$missing = @($declared | Where-Object { $_ -notin $ran })
if ($missing.Count) { throw "verify.ps1 does not run these solution test projects: $($missing -join ', ')" }
if (!$SkipGpu) {
    & $invoke -DotNetArguments @('run', '--project', 'src/Mmd.Modern.Smoke', '-c', 'Release', '--no-build', '--no-restore', '--', 'artifacts/gpu')
}
