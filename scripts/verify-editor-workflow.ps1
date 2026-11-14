[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$ModelPath,
    [string]$MotionPath,
    [string]$PmdPath,
    [string]$PublishedDirectory,
    [string]$IsolatedDirectory,
    [string]$FfmpegPath,
    [string]$FfprobePath,
    [switch]$AllowWarp
)
$ErrorActionPreference = 'Stop'
$taskProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskWorkspaceRoot = [IO.Path]::GetFullPath((Join-Path $taskProjectRoot '..'))
$taskRunId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$taskOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else {
    Join-Path $taskWorkspaceRoot ('deliverables/GUI/' + (Get-Date -Format 'yyyyMMdd') + '/Workflow-' + $taskRunId)
}
$taskProbeRoot = Join-Path $taskWorkspaceRoot ('.dev-cache/gui-workflow-verification/' + $taskRunId)

function Write-WorkflowJson($Value, [string]$Path) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 15), [Text.UTF8Encoding]::new($false))
}
function Get-WorkflowManifest([string]$Directory) {
    @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Published reparse point is not accepted: $($_.FullName)" }
        [pscustomobject]@{ path = $_.FullName.Substring($Directory.Length + 1).Replace('\', '/'); bytes = $_.Length;
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}
function Assert-WorkflowManifest($Expected, [string]$Directory, [switch]$AllowHarness) {
    $actual = @(Get-WorkflowManifest $Directory)
    $byPath = @{}
    foreach ($file in $actual) { $byPath[$file.path] = $file }
    foreach ($file in $Expected) {
        if (-not $byPath.ContainsKey($file.path) -or $byPath[$file.path].sha256 -ne $file.sha256) {
            throw "Published payload changed or is missing: $Directory/$($file.path)"
        }
        $byPath.Remove($file.path)
    }
    foreach ($extra in $byPath.Keys) {
        if (-not $AllowHarness -or $extra -ne 'EditorWorkflowPublishedProbe.dll') { throw "Unexpected published payload file: $Directory/$extra" }
    }
}

if ([bool]$FfmpegPath -ne [bool]$FfprobePath) { throw 'Specify FFmpeg and FFprobe together, or omit both to skip video.' }
if ($PublishedDirectory -and -not $PmdPath) { throw 'Published verification requires -PmdPath pointing to an absolute read-only acceptance asset; fixtures are not added to the product.' }
$taskPublished = $null
$taskPayload = $null
$taskManifest = @()
$taskReport = $null
if ($PublishedDirectory) {
    $taskPublished = (Resolve-Path -LiteralPath $PublishedDirectory).Path.TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $taskPublished -PathType Container)) { throw 'PublishedDirectory must be a directory.' }
    foreach ($taskName in @('Mmd.Modern.App.dll', 'Mmd.Modern.App.deps.json', 'Mmd.Modern.App.runtimeconfig.json', 'System.Private.CoreLib.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $taskPublished $taskName) -PathType Leaf)) { throw "Published payload is missing: $taskName" }
    }
    foreach ($taskItem in @(Get-Item -LiteralPath $taskPublished -Force) + @(Get-ChildItem -LiteralPath $taskPublished -Recurse -Force)) {
        if (($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Published reparse point is not accepted: $($taskItem.FullName)" }
    }
    if ($taskOutput.Equals($taskPublished, [StringComparison]::OrdinalIgnoreCase) -or
        $taskOutput.StartsWith($taskPublished + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OutputDirectory must be outside PublishedDirectory.'
    }
    $taskManifest = @(Get-WorkflowManifest $taskPublished)
    $taskPayload = if ($IsolatedDirectory) { [IO.Path]::GetFullPath($IsolatedDirectory).TrimEnd('\','/') } else { Join-Path $taskProbeRoot 'published-copy' }
    if (Test-Path -LiteralPath $taskPayload) { throw 'IsolatedDirectory must be new.' }
    if ($taskPayload.Equals($taskPublished, [StringComparison]::OrdinalIgnoreCase) -or
        $taskPayload.StartsWith($taskPublished + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $taskPublished.StartsWith($taskPayload + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'IsolatedDirectory must be independent of PublishedDirectory.'
    }
}

$taskProbeArguments = @($taskOutput)
foreach ($taskPair in @(@('pmx',$ModelPath),@('pmd',$PmdPath),@('vmd',$MotionPath),@('ffmpeg',$FfmpegPath),@('ffprobe',$FfprobePath))) {
    if ($taskPair[1]) {
        if ($taskPair[1] -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+)') { throw ($taskPair[0] + ' path must be absolute.') }
        if (-not (Test-Path -LiteralPath $taskPair[1] -PathType Leaf)) { throw ($taskPair[0] + ' file was not found.') }
        $taskProbeArguments += '--' + $taskPair[0] + '=' + [IO.Path]::GetFullPath($taskPair[1])
    }
}
if ($AllowWarp) { $taskProbeArguments += '--allow-warp' }
New-Item -ItemType Directory -Force -Path $taskProbeRoot,$taskOutput | Out-Null
$taskProgram = Join-Path $taskProbeRoot 'Program.cs'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'probes/EditorWorkflowProbe.cs') -Destination $taskProgram
$taskProject = Join-Path $taskProbeRoot 'Probe.csproj'
if (-not $taskPublished) {
    $taskAppProject = [Security.SecurityElement]::Escape((Join-Path $taskProjectRoot 'src/Mmd.Modern.App/Mmd.Modern.App.csproj'))
    $taskXml = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="$taskAppProject" /></ItemGroup>
</Project>
"@
    [IO.File]::WriteAllText($taskProject,$taskXml)
    & (Join-Path $PSScriptRoot 'dotnet.ps1') -DotNetArguments (@('run','--project',$taskProject,'-c','Release','--') + $taskProbeArguments)
    return
}

# Reference only the exact published managed assemblies. Disabling SDK framework references avoids
# accidentally compiling against another runtime's reference pack. No product project is evaluated.
$taskReferences = @(foreach ($taskFile in Get-ChildItem -LiteralPath $taskPublished -File -Filter '*.dll' | Sort-Object Name) {
    try { $taskIdentity = [Reflection.AssemblyName]::GetAssemblyName($taskFile.FullName) }
    catch [BadImageFormatException] { continue }
    $taskReferenceName = [Security.SecurityElement]::Escape($taskIdentity.Name)
    $taskReferencePath = [Security.SecurityElement]::Escape($taskFile.FullName)
    "    <Reference Include=`"$taskReferenceName`"><HintPath>$taskReferencePath</HintPath><Private>false</Private></Reference>"
})
if ($taskReferences.Count -eq 0) { throw 'No managed published assemblies were found.' }
$taskBuildOutput = Join-Path $taskProbeRoot 'harness-bin'
$taskBuildOutputXml = [Security.SecurityElement]::Escape($taskBuildOutput + [IO.Path]::DirectorySeparatorChar)
$taskXml = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>EditorWorkflowPublishedProbe</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><LangVersion>14.0</LangVersion>
    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences><NoStdLib>true</NoStdLib>
    <UseAppHost>false</UseAppHost><GenerateDependencyFile>false</GenerateDependencyFile><GenerateRuntimeConfigurationFiles>false</GenerateRuntimeConfigurationFiles>
    <OutputPath>$taskBuildOutputXml</OutputPath><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <UseSharedCompilation>false</UseSharedCompilation>
  </PropertyGroup>
  <ItemGroup>
$($taskReferences -join [Environment]::NewLine)
  </ItemGroup>
</Project>
"@
[IO.File]::WriteAllText($taskProject,$taskXml)
$taskManifestPath = Join-Path $taskOutput 'published-payload-before.json'
Write-WorkflowJson @($taskManifest) $taskManifestPath
$taskReportPath = Join-Path $taskOutput 'published-harness-report.json'
$taskReport = [ordered]@{ schemaVersion = 1; mode = 'published DLL references'; publishedDirectory = $taskPublished;
    isolatedPayloadDirectory = $taskPayload; harnessProject = $taskProject; managedReferenceCount = $taskReferences.Count;
    productProjectsEvaluated = $false; productAssembliesRebuilt = $false; requiresDotnetHost = $true; dotnetHost = $null;
    provesSdkIndependentStartup = $false; startupScope = 'dotnet exec with original published runtimeconfig/deps; normal product EXE startup must be checked separately';
    sharedRuntimeFilesOverwritten = @(); sourceManifest = $taskManifestPath; copiedPayloadMatches = $false;
    sourcePayloadUnchanged = $false; copiedPayloadUnchangedAfterRun = $false; success = $false; failure = $null }
try {
    & (Join-Path $PSScriptRoot 'dotnet.ps1') -DotNetArguments @('build',$taskProject,'-c','Release','--nologo')
    Assert-WorkflowManifest $taskManifest $taskPublished
    New-Item -ItemType Directory -Path $taskPayload | Out-Null
    foreach ($taskFile in $taskManifest) {
        $taskDestination = Join-Path $taskPayload $taskFile.path
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($taskDestination)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $taskPublished $taskFile.path) -Destination $taskDestination
    }
    Assert-WorkflowManifest $taskManifest $taskPayload
    $taskReport.copiedPayloadMatches = $true
    $taskHarness = Join-Path $taskPayload 'EditorWorkflowPublishedProbe.dll'
    if (Test-Path -LiteralPath $taskHarness) { throw 'Published directory already contains the diagnostic harness.' }
    Copy-Item -LiteralPath (Join-Path $taskBuildOutput 'EditorWorkflowPublishedProbe.dll') -Destination $taskHarness
    $taskExpectedApp = Join-Path $taskPayload 'Mmd.Modern.App.dll'
    $taskExpectedHash = ($taskManifest | Where-Object { $_.path -eq 'Mmd.Modern.App.dll' }).sha256
    $taskProbeArguments += @(('--expected-app=' + $taskExpectedApp), ('--expected-app-sha256=' + $taskExpectedHash), ('--expected-product-manifest=' + $taskManifestPath))
    if (-not $FfmpegPath -and (Test-Path -LiteralPath (Join-Path $taskPayload 'tools/ffmpeg/ffmpeg.exe')) -and
        (Test-Path -LiteralPath (Join-Path $taskPayload 'tools/ffmpeg/ffprobe.exe'))) {
        $taskProbeArguments += @(('--ffmpeg=' + (Join-Path $taskPayload 'tools/ffmpeg/ffmpeg.exe')), ('--ffprobe=' + (Join-Path $taskPayload 'tools/ffmpeg/ffprobe.exe')))
    }
    $taskDotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
    $taskReport.dotnetHost = $taskDotnet
    # The general dotnet wrapper changes cwd to the source checkout. Execute directly here so that
    # native tools, relative probes and AppContext.BaseDirectory all observe the isolated payload.
    Push-Location $taskPayload
    try {
        & $taskDotnet exec --runtimeconfig (Join-Path $taskPayload 'Mmd.Modern.App.runtimeconfig.json') --depsfile (Join-Path $taskPayload 'Mmd.Modern.App.deps.json') $taskHarness @taskProbeArguments
        if ($LASTEXITCODE -ne 0) { throw "Published editor harness failed with exit code $LASTEXITCODE" }
    } finally { Pop-Location }
    $taskReport.success = $true
} catch {
    $taskReport.failure = $_.ToString()
    throw
} finally {
    try { Assert-WorkflowManifest $taskManifest $taskPublished; $taskReport.sourcePayloadUnchanged = $true }
    catch { $taskReport.success = $false; $taskReport.failure = $_.ToString(); Write-WorkflowJson $taskReport $taskReportPath; throw }
    if (Test-Path -LiteralPath $taskPayload) {
        try { Assert-WorkflowManifest $taskManifest $taskPayload -AllowHarness; $taskReport.copiedPayloadUnchangedAfterRun = $true }
        catch { $taskReport.success = $false; $taskReport.failure = $_.ToString(); Write-WorkflowJson $taskReport $taskReportPath; throw }
    }
    Write-WorkflowJson $taskReport $taskReportPath
}
