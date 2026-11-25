param(
    [switch]$SkipGpu,
    [string]$ArtifactsDirectory = 'artifacts'
)
$ErrorActionPreference = 'Stop'
$invoke = Join-Path $PSScriptRoot 'dotnet.ps1'
$projectRoot = Split-Path $PSScriptRoot -Parent
$solutionPath = Join-Path $projectRoot 'Mmd.Modern.slnx'
& $invoke -DotNetArguments @('restore', $solutionPath, '--locked-mode')
& $invoke -DotNetArguments @('build', $solutionPath, '-c', 'Release', '--no-restore')

# Preserve report names consumed by existing tooling. New test projects use their project name.
$reportNames = @{
    'Mmd.Modern.Tests' = 'full'
    'Mmd.Modern.Scheduling.Tests' = 'scheduling'
    'Mmd.Modern.Formats.Tests' = 'formats'
    'Mmd.Modern.Application.Tests' = 'application'
    'Mmd.Modern.ViewOutput.Tests' = 'view-output'
    'Mmd.Modern.ViewOutput.FFmpeg.Tests' = 'view-ffmpeg'
}
$ArtifactsDirectory = [IO.Path]::GetFullPath([IO.Path]::Combine($projectRoot, $ArtifactsDirectory))
$resultsDirectory = Join-Path $ArtifactsDirectory 'green'
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
$failures = [Collections.Generic.List[string]]::new()
function Invoke-Check([string]$name, [string[]]$arguments, [string]$logPath) {
    try {
        & $invoke -DotNetArguments $arguments 2>&1 | Tee-Object -FilePath $logPath
    } catch {
        $failure = "${name}: $($_.Exception.Message) (log: $logPath)"
        $failures.Add($failure)
        Add-Content -LiteralPath $logPath -Value $failure
        Write-Warning $failure
    }
}
$suiteCount = 0
$solution = [xml](Get-Content -LiteralPath $solutionPath -Raw -Encoding UTF8)
foreach ($project in $solution.SelectNodes('//Project[@Path]')) {
    $projectPath = Join-Path $projectRoot $project.GetAttribute('Path')
    $definition = [xml](Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8)
    if ($null -eq $definition.SelectSingleNode('/Project/PropertyGroup/IsTestProject[text()="true"]')) { continue }

    $name = [IO.Path]::GetFileNameWithoutExtension($projectPath)
    $report = if ($reportNames.ContainsKey($name)) { $reportNames[$name] } else { $name }
    $testArguments = @('test', $projectPath, '-c', 'Release', '--no-build', '--no-restore',
        '--logger', "trx;LogFileName=$report.trx", '--results-directory', $resultsDirectory)
    if ($SkipGpu) { $testArguments += @('--filter', 'Category!=Gpu') }
    $suiteCount++
    Invoke-Check $name $testArguments (Join-Path $resultsDirectory "$report.log")
}
if ($suiteCount -eq 0) { throw "No test projects discovered in $solutionPath." }
if (!$SkipGpu) {
    $gpuDirectory = Join-Path $ArtifactsDirectory 'gpu'
    New-Item -ItemType Directory -Path $gpuDirectory -Force | Out-Null
    $smokeArguments = @('run', '--project', 'src/Mmd.Modern.Smoke', '-c', 'Release', '--no-build', '--no-restore', '--', $gpuDirectory)
    Invoke-Check 'Mmd.Modern.Smoke' $smokeArguments (Join-Path $gpuDirectory 'smoke.log')
}
if ($failures.Count -gt 0) {
    throw "Verification failed:`n$($failures -join [Environment]::NewLine)"
}
