$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'dotnet.ps1') -DotNetArguments @('run', '--project', 'src/Mmd.Modern.App', '-c', 'Release')
