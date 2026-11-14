[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PublishedDirectory,
    [string]$OutputDirectory,
    [ValidateSet('Fsr','XeSs','Dlss')][string[]]$RequiredProviders = @()
)
$ErrorActionPreference = 'Stop'
$taskProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskWorkspaceRoot = [IO.Path]::GetFullPath((Join-Path $taskProjectRoot '..'))
$taskRunId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$taskProbeRoot = Join-Path $taskWorkspaceRoot ('.dev-cache/editor-optional-runtime-verification/' + $taskRunId)
$taskOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else {
    Join-Path $taskWorkspaceRoot ('deliverables/GUI/' + (Get-Date -Format 'yyyyMMdd') + '/OptionalRuntimes-' + $taskRunId)
}
$taskPublished = (Resolve-Path -LiteralPath $PublishedDirectory).Path.TrimEnd('\','/')
if (-not (Test-Path -LiteralPath $taskPublished -PathType Container)) { throw 'PublishedDirectory must be a directory.' }
foreach ($taskName in @('Mmd.Modern.App.dll','Mmd.Modern.App.deps.json','Mmd.Modern.App.runtimeconfig.json','System.Private.CoreLib.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskPublished $taskName) -PathType Leaf)) { throw "Self-contained published payload is missing $taskName" }
}
foreach ($taskItem in @(Get-Item -LiteralPath $taskPublished -Force) + @(Get-ChildItem -LiteralPath $taskPublished -Recurse -Force)) {
    if (($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Published reparse point is not accepted: $($taskItem.FullName)" }
}
if ($taskOutput.Equals($taskPublished, [StringComparison]::OrdinalIgnoreCase) -or
    $taskOutput.StartsWith($taskPublished + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be outside PublishedDirectory.'
}
function Write-RuntimeJson($Value, [string]$Path) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
}
function Get-RuntimeManifest([string]$Directory) {
    @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ path = $_.FullName.Substring($Directory.Length + 1).Replace('\','/'); bytes = $_.Length;
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}
function Assert-RuntimeManifest($Expected, [string]$Directory, [switch]$AllowHarness) {
    $taskActual = @(Get-RuntimeManifest $Directory)
    $taskByPath = @{}
    foreach ($taskFile in $taskActual) { $taskByPath[$taskFile.path] = $taskFile }
    foreach ($taskFile in $Expected) {
        if (-not $taskByPath.ContainsKey($taskFile.path) -or $taskByPath[$taskFile.path].sha256 -ne $taskFile.sha256) {
            throw "Published payload changed or is missing: $Directory/$($taskFile.path)"
        }
        $taskByPath.Remove($taskFile.path)
    }
    foreach ($taskExtra in $taskByPath.Keys) {
        if (-not $AllowHarness -or $taskExtra -ne 'EditorPublishedRuntimeProbe.dll') { throw "Unexpected published payload file: $Directory/$taskExtra" }
    }
}
New-Item -ItemType Directory -Force -Path $taskProbeRoot,$taskOutput | Out-Null
$taskManifest = @(Get-RuntimeManifest $taskPublished)
Write-RuntimeJson @($taskManifest) (Join-Path $taskOutput 'published-payload-before.json')
$taskPayload = Join-Path $taskProbeRoot 'published-copy'
$taskBuildOutput = Join-Path $taskProbeRoot 'harness-bin'
$taskProject = Join-Path $taskProbeRoot 'Probe.csproj'
foreach ($taskSource in 'EditorOptionalRuntimeProbe.cs','EditorPublishedRuntimeProbe.cs') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('probes/' + $taskSource)) -Destination (Join-Path $taskProbeRoot $taskSource)
}
# Every managed reference comes from the exact publish. No product ProjectReference and no implicit SDK reference pack.
$taskReferences = @(foreach ($taskFile in Get-ChildItem -LiteralPath $taskPublished -File -Filter '*.dll' | Sort-Object Name) {
    try { $taskIdentity = [Reflection.AssemblyName]::GetAssemblyName($taskFile.FullName) }
    catch [BadImageFormatException] { continue }
    $taskReferenceName = [Security.SecurityElement]::Escape($taskIdentity.Name)
    $taskReferencePath = [Security.SecurityElement]::Escape($taskFile.FullName)
    "    <Reference Include=`"$taskReferenceName`"><HintPath>$taskReferencePath</HintPath><Private>false</Private></Reference>"
})
$taskBuildOutputXml = [Security.SecurityElement]::Escape($taskBuildOutput + [IO.Path]::DirectorySeparatorChar)
$taskXml = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>EditorPublishedRuntimeProbe</AssemblyName>
    <StartupObject>EditorPublishedRuntimeProbe</StartupObject><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><LangVersion>14.0</LangVersion>
    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences><NoStdLib>true</NoStdLib><UseSharedCompilation>false</UseSharedCompilation>
    <UseAppHost>false</UseAppHost><GenerateDependencyFile>false</GenerateDependencyFile><GenerateRuntimeConfigurationFiles>false</GenerateRuntimeConfigurationFiles>
    <OutputPath>$taskBuildOutputXml</OutputPath><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>
  <ItemGroup>
$($taskReferences -join [Environment]::NewLine)
  </ItemGroup>
</Project>
"@
[IO.File]::WriteAllText($taskProject,$taskXml)
$taskReportPath = Join-Path $taskOutput 'published-runtime-harness-report.json'
$taskReport = [ordered]@{ schemaVersion = 1; publishedDirectory = $taskPublished; isolatedPayloadDirectory = $taskPayload;
    mode = 'exact published DLL references'; harnessProject = $taskProject; productProjectsEvaluated = $false; productAssembliesRebuilt = $false;
    managedReferenceCount = $taskReferences.Count; requiredProviders = $RequiredProviders; sdkEnvironmentOverrides = 'four overrides removed from child process only';
    sourcePayloadUnchanged = $false; copiedPayloadMatches = $false; copiedPayloadUnchangedAfterRun = $false;
    scope = 'app-local QueryInstalled plus actual capture frames and NativeAA when supported; normal EXE and HWND presentation remain separate';
    dotnetHost = $null; provesSdkIndependentExeStartup = $false; exitCode = $null; success = $false; failure = $null }
try {
    & (Join-Path $PSScriptRoot 'dotnet.ps1') -DotNetArguments @('build',$taskProject,'-c','Release','--nologo')
    Assert-RuntimeManifest $taskManifest $taskPublished
    New-Item -ItemType Directory -Path $taskPayload | Out-Null
    foreach ($taskFile in $taskManifest) {
        $taskDestination = Join-Path $taskPayload $taskFile.path
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($taskDestination)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $taskPublished $taskFile.path) -Destination $taskDestination
    }
    Assert-RuntimeManifest $taskManifest $taskPayload
    $taskReport.copiedPayloadMatches = $true
    $taskHarness = Join-Path $taskPayload 'EditorPublishedRuntimeProbe.dll'
    if (Test-Path -LiteralPath $taskHarness) { throw 'Payload already includes this diagnostic harness.' }
    Copy-Item -LiteralPath (Join-Path $taskBuildOutput 'EditorPublishedRuntimeProbe.dll') -Destination $taskHarness
    $taskDotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
    $taskReport.dotnetHost = $taskDotnet
    $taskStart = New-Object Diagnostics.ProcessStartInfo
    $taskStart.FileName = $taskDotnet
    $taskStart.WorkingDirectory = $taskPayload
    $taskStart.UseShellExecute = $false
    $taskStart.CreateNoWindow = $true
    $taskStart.RedirectStandardOutput = $true
    $taskStart.RedirectStandardError = $true
    foreach ($taskVariable in 'MMD_FSR_LOADER_PATH','MMD_XESS_RUNTIME_PATH','MMD_DLSS_BRIDGE_PATH','MMD_DLSS_SDK_DIRECTORY') { $taskStart.EnvironmentVariables.Remove($taskVariable) }
    # ProcessStartInfo.ArgumentList is unavailable in Windows PowerShell's .NET Framework.
    $taskArguments = @('exec','--runtimeconfig',(Join-Path $taskPayload 'Mmd.Modern.App.runtimeconfig.json'),'--depsfile',
        (Join-Path $taskPayload 'Mmd.Modern.App.deps.json'),$taskHarness,$taskOutput,$taskPayload) + $RequiredProviders
    foreach ($taskArgument in $taskArguments) { if ($taskArgument.Contains('"')) { throw 'Double quotes are not accepted in diagnostic paths.' } }
    $taskStart.Arguments = ($taskArguments | ForEach-Object { '"' + $_.TrimEnd('\') + '"' }) -join ' '
    $taskProcess = [Diagnostics.Process]::Start($taskStart)
    try {
        $taskStdout = $taskProcess.StandardOutput.ReadToEndAsync()
        $taskStderr = $taskProcess.StandardError.ReadToEndAsync()
        if (-not $taskProcess.WaitForExit(600000)) { $taskProcess.Kill(); throw 'Published runtime verification exceeded ten minutes.' }
        $taskProcess.WaitForExit()
        [IO.File]::WriteAllText((Join-Path $taskOutput 'probe.stdout.log'),$taskStdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $taskOutput 'probe.stderr.log'),$taskStderr.GetAwaiter().GetResult())
        $taskReport.exitCode = $taskProcess.ExitCode
        if ($taskProcess.ExitCode -ne 0) { throw "Published runtime verification failed with exit code $($taskProcess.ExitCode). See $taskOutput" }
    } finally { $taskProcess.Dispose() }
    $taskReport.success = $true
} catch {
    $taskReport.failure = $_.ToString()
    throw
} finally {
    try { Assert-RuntimeManifest $taskManifest $taskPublished; $taskReport.sourcePayloadUnchanged = $true }
    catch { $taskReport.success = $false; $taskReport.failure = $_.ToString(); Write-RuntimeJson $taskReport $taskReportPath; throw }
    if (Test-Path -LiteralPath $taskPayload) {
        try { Assert-RuntimeManifest $taskManifest $taskPayload -AllowHarness; $taskReport.copiedPayloadUnchangedAfterRun = $true }
        catch { $taskReport.success = $false; $taskReport.failure = $_.ToString(); Write-RuntimeJson $taskReport $taskReportPath; throw }
    }
    Write-RuntimeJson $taskReport $taskReportPath
}
Write-Host "PASS published optional runtimes: $taskOutput"
