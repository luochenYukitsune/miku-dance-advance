param([Parameter(ValueFromRemainingArguments=$true)][string[]]$DotNetArguments)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspaceCache = [IO.Path]::GetFullPath((Join-Path $projectRoot '../.dev-cache'))
# Cache migrations retain the old entry as a junction. Use its physical location for both
# test temporary assets and MSBuild paths; portable-project guards correctly reject redirects.
if (Test-Path -LiteralPath $workspaceCache) {
    $cacheEntry = Get-Item -LiteralPath $workspaceCache -Force
    if ($cacheEntry.LinkType -in @('Junction', 'SymbolicLink')) {
        $cacheTarget = @($cacheEntry.Target)[0]
        if ($cacheTarget) {
            $workspaceCache = if ([IO.Path]::IsPathRooted($cacheTarget)) { [IO.Path]::GetFullPath($cacheTarget) }
                else { [IO.Path]::GetFullPath((Join-Path (Split-Path $workspaceCache -Parent) $cacheTarget)) }
        }
    }
}
$taskCache = Join-Path $workspaceCache 'modern-dotnet'
if (-not $env:ModernCacheRoot) { $env:ModernCacheRoot = $taskCache }
foreach ($directory in 'packages','http','home','tmp','bin','obj') {
    New-Item -ItemType Directory -Path (Join-Path $taskCache $directory) -Force | Out-Null
}
$env:NUGET_PACKAGES = Join-Path $taskCache 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $taskCache 'http'
$env:DOTNET_CLI_HOME = Join-Path $taskCache 'home'
$env:TEMP = Join-Path $taskCache 'tmp'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_NOLOGO = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:NUGET_XMLDOC_MODE = 'skip'
$dotnetPath = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'C:/Program Files/dotnet/dotnet.exe' } else { 'dotnet' }
Push-Location $projectRoot
try {
    # Preserve xUnit/native stderr and its complete result before reporting a failed exit.
    $ErrorActionPreference = 'Continue'
    & $dotnetPath @DotNetArguments
    $dotnetExitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($dotnetExitCode -ne 0) { throw "dotnet failed with exit code $dotnetExitCode" }
} finally { $ErrorActionPreference = 'Stop'; Pop-Location }
