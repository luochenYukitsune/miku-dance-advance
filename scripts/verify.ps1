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
if (!$SkipGpu) {
    & $invoke -DotNetArguments @('run', '--project', 'src/Mmd.Modern.Smoke', '-c', 'Release', '--no-build', '--no-restore', '--', 'artifacts/gpu')
}
