param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskWorkspaceRoot = [IO.Path]::GetFullPath((Join-Path $taskProjectRoot '..'))
$taskOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskWorkspaceRoot ('deliverables/GUI/' + (Get-Date -Format 'yyyyMMdd')) }
$taskProbeRoot = Join-Path $taskWorkspaceRoot '.dev-cache/gui-shell-verification'
New-Item -ItemType Directory -Force -Path $taskProbeRoot,$taskOutput | Out-Null
$taskAppProject = [Security.SecurityElement]::Escape((Join-Path $taskProjectRoot 'src/Mmd.Modern.App/Mmd.Modern.App.csproj'))
$taskXml = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="$taskAppProject" /></ItemGroup>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $taskProbeRoot 'Probe.csproj'),$taskXml)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'probes/EditorShellProbe.cs') -Destination (Join-Path $taskProbeRoot 'Program.cs') -Force
& (Join-Path $PSScriptRoot 'dotnet.ps1') -DotNetArguments @('run','--project',(Join-Path $taskProbeRoot 'Probe.csproj'),'-c','Release','--',$taskOutput)
