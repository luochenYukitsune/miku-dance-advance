param([Parameter(ValueFromRemainingArguments=$true)][string[]]$DotNetArguments)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspaceRoot = Split-Path $projectRoot -Parent
$workspaceCache = Join-Path $workspaceRoot '.dev-cache'
$taskCache = if ($env:ModernCacheRoot) {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($env:ModernCacheRoot)
} else {
    Join-Path $workspaceCache 'modern-dotnet'
}
# Cache migrations retain the old entry as a junction. Use its physical location for both
# test temporary assets and MSBuild paths; portable-project guards correctly reject redirects.
if (Test-Path -LiteralPath $workspaceCache) {
    $cacheEntry = Get-Item -LiteralPath $workspaceCache -Force
    if ($cacheEntry.LinkType -in @('Junction', 'SymbolicLink')) {
        $cacheTarget = @($cacheEntry.Target)[0]
        if ($cacheTarget) {
            $cachePrefix = $workspaceCache + [IO.Path]::DirectorySeparatorChar
            $comparison = if ($env:OS -eq 'Windows_NT') { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
            if ($taskCache.StartsWith($cachePrefix, $comparison)) {
                $physicalRoot = [IO.Path]::GetFullPath([IO.Path]::Combine($workspaceRoot, $cacheTarget))
                $taskCache = Join-Path $physicalRoot $taskCache.Substring($cachePrefix.Length)
            }
        }
    }
}
$env:ModernCacheRoot = $taskCache
foreach ($directory in 'packages','http','plugins','home','tmp','bin','obj') {
    New-Item -ItemType Directory -Path (Join-Path $taskCache $directory) -Force | Out-Null
}
$env:NUGET_PACKAGES = Join-Path $taskCache 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $taskCache 'http'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $taskCache 'plugins'
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
$executable = if ($env:OS -eq 'Windows_NT') { 'dotnet.exe' } else { 'dotnet' }
$portable = Join-Path $workspaceRoot ".tools/dotnet/$executable"
$dotnetPath = if ($env:MDA_DOTNET_EXE) { $env:MDA_DOTNET_EXE }
    elseif (Test-Path -LiteralPath $portable -PathType Leaf) { $portable }
    elseif (Get-Command $executable -CommandType Application -ErrorAction SilentlyContinue) { $executable }
    elseif ($env:OS -eq 'Windows_NT') { Join-Path $env:ProgramFiles "dotnet/$executable" }
    else { $executable }
Push-Location $projectRoot
try {
    # Preserve xUnit/native stderr and its complete result before reporting a failed exit.
    $ErrorActionPreference = 'Continue'
    & $dotnetPath @DotNetArguments
    $dotnetExitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($dotnetExitCode -ne 0) { throw "dotnet failed with exit code $dotnetExitCode" }
} finally { $ErrorActionPreference = 'Stop'; Pop-Location }
