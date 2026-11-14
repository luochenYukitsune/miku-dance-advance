[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskWorkspaceRoot = [IO.Path]::GetFullPath((Join-Path $taskProjectRoot '..'))
$taskRunId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$taskProbeRoot = Join-Path $taskWorkspaceRoot ('.dev-cache/gui-texture-verification/' + $taskRunId)
$taskOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskProbeRoot 'evidence' }
New-Item -ItemType Directory -Force -Path $taskProbeRoot,$taskOutput | Out-Null
$taskAppProject = [Security.SecurityElement]::Escape((Join-Path $taskProjectRoot 'src/Mmd.Modern.App/Mmd.Modern.App.csproj'))
$taskBinaryDirectory = Join-Path $taskProbeRoot 'bin'
$taskBinaryXml = [Security.SecurityElement]::Escape($taskBinaryDirectory + [IO.Path]::DirectorySeparatorChar)
$taskProject = Join-Path $taskProbeRoot 'TextureProbe.csproj'
$taskXml = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>EditorTextureProbe</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><UseSharedCompilation>false</UseSharedCompilation>
    <OutputPath>$taskBinaryXml</OutputPath><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="$taskAppProject" /></ItemGroup>
</Project>
"@
[IO.File]::WriteAllText($taskProject, $taskXml)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'probes/EditorTextureProbe.cs') -Destination (Join-Path $taskProbeRoot 'Program.cs')
& (Join-Path $PSScriptRoot 'dotnet.ps1') -DotNetArguments @('build',$taskProject,'-c','Release','--nologo')
$taskDotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
Push-Location $taskProbeRoot
try {
    & $taskDotnet (Join-Path $taskBinaryDirectory 'EditorTextureProbe.dll') $taskOutput
    if ($LASTEXITCODE -ne 0) { throw "Editor texture verification failed with exit code $LASTEXITCODE" }
} finally { Pop-Location }
