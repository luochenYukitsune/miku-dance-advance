#Requires -Version 5.1
<#
.SYNOPSIS
校验已发布或已安装的 MDA 目录中 4 套随附官方画风插件的布局、清单与可选真实无头激活。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppDirectory,
    [string]$WorkDirectory,
    [string]$ExpectedVersion,
    [switch]$SkipRuntimeLoad
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$appDir = (Resolve-Path -LiteralPath $AppDirectory).Path.TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $appDir -PathType Container)) {
    throw "AppDirectory 不是有效目录：$AppDirectory"
}

foreach ($hostFile in @('Mmd.Modern.App.dll', 'Mmd.Modern.App.deps.json', 'Mmd.Modern.App.runtimeconfig.json', 'Mmd.Modern.Plugins.Contracts.dll', 'Mmd.Modern.Plugins.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $appDir $hostFile) -PathType Leaf)) {
        throw "应用目录缺少核心宿主程序集/配置：$hostFile"
    }
}

$contractsVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $appDir 'Mmd.Modern.Plugins.Contracts.dll')).Version
if ($contractsVersion -ne [version]'1.0.0.0') {
    throw "Mmd.Modern.Plugins.Contracts.dll 版本被意外修改为 $contractsVersion（必须保持 1.0.0.0，否则外部插件将不兼容）。"
}

if ($ExpectedVersion) {
    $verMatch = [regex]::Match($ExpectedVersion.Trim(), '^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:-[0-9A-Za-z][0-9A-Za-z._-]*)?$')
    if (-not $verMatch.Success) {
        throw "ExpectedVersion 格式非法：$ExpectedVersion"
    }
    $expectedNorm = [version]::new(
        [int]$verMatch.Groups[1].Value,
        [int]$verMatch.Groups[2].Value,
        [int]$verMatch.Groups[3].Value,
        $(if ($verMatch.Groups[4].Success) { [int]$verMatch.Groups[4].Value } else { 0 }))
    $appDll = Join-Path $appDir 'Mmd.Modern.App.dll'
    $actualAsmVer = [Reflection.AssemblyName]::GetAssemblyName($appDll).Version
    $actualInfoVer = ([Diagnostics.FileVersionInfo]::GetVersionInfo($appDll).ProductVersion).Trim()
    if ($actualAsmVer -ne $expectedNorm) {
        throw "Mmd.Modern.App.dll 程序集版本不匹配：实际 $actualAsmVer，期望 $expectedNorm"
    }
    if (-not ($actualInfoVer -eq $ExpectedVersion -or $actualInfoVer.StartsWith($ExpectedVersion + '+'))) {
        throw "Mmd.Modern.App.dll ProductVersion 未正确注入发布版本号：实际 '$actualInfoVer'，期望以 '$ExpectedVersion' 开头"
    }
}

$pluginsRoot = Join-Path $appDir 'plugins'
if (-not (Test-Path -LiteralPath $pluginsRoot -PathType Container)) {
    throw "应用目录缺少随附插件根目录：$pluginsRoot"
}

$strayFiles = @(Get-ChildItem -LiteralPath $pluginsRoot -Recurse -File -Force | Where-Object {
    $_.Extension -ieq '.pdb' -or $_.Name -ieq 'Mmd.Modern.Plugins.Contracts.dll'
})
if ($strayFiles.Count -gt 0) {
    $names = ($strayFiles | ForEach-Object { $_.FullName.Substring($appDir.Length + 1).Replace('\', '/') }) -join ', '
    throw "plugins/ 目录不得包含调试符号或重复契约库：$names"
}

$bundled = @(
    @{ Id = 'mda.style.genshin'; Project = 'Mda.StylePlugin.Genshin'; Style = 'genshin' },
    @{ Id = 'mda.style.star-rail'; Project = 'Mda.StylePlugin.StarRail'; Style = 'star-rail' },
    @{ Id = 'mda.style.wuthering-waves'; Project = 'Mda.StylePlugin.WutheringWaves'; Style = 'wuthering-waves' },
    @{ Id = 'mda.style.endfield'; Project = 'Mda.StylePlugin.Endfield'; Style = 'endfield' }
)

foreach ($item in $bundled) {
    $dir = Join-Path $pluginsRoot $item.Id
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        throw "缺少随附画风插件目录：plugins/$($item.Id)"
    }
    $dllPath = Join-Path $dir "$($item.Project).dll"
    $depsPath = Join-Path $dir "$($item.Project).deps.json"
    $manifestPath = Join-Path $dir 'mda-plugin.json'
    foreach ($req in @($dllPath, $depsPath, $manifestPath)) {
        if (-not (Test-Path -LiteralPath $req -PathType Leaf)) {
            throw "随附画风插件缺失文件：$req"
        }
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or
        $manifest.id -cne $item.Id -or
        $manifest.entryAssembly -cne "$($item.Project).dll" -or
        $manifest.apiVersion -ne 1 -or
        @($manifest.capabilities) -notcontains 'rendering.styles' -or
        @($manifest.providesStyles) -notcontains $item.Style) {
        throw "随附画风插件清单校验失败：plugins/$($item.Id)/mda-plugin.json"
    }
}

if ($SkipRuntimeLoad) {
    Write-Output ("PASS 随附画风插件静态校验通过（共 {0} 套，contracts={1}）：{2}" -f $bundled.Count, $contractsVersion, $appDir)
    return
}

$autoWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($autoWork) {
    Join-Path ([IO.Path]::GetTempPath()) ('mda-plugin-probe-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
} else {
    [IO.Path]::GetFullPath($WorkDirectory)
}
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    $probeSource = Join-Path $PSScriptRoot 'probes/EditorBundledPluginsProbe.cs'
    if (-not (Test-Path -LiteralPath $probeSource -PathType Leaf)) {
        throw "缺少插件探针源码：$probeSource"
    }
    $probeSrcCopy = Join-Path $work 'EditorBundledPluginsProbe.cs'
    Copy-Item -LiteralPath $probeSource -Destination $probeSrcCopy -Force

    $binOut = Join-Path $work 'probe-bin'
    $objOut = Join-Path $work 'probe-obj'
    New-Item -ItemType Directory -Force -Path $binOut, $objOut | Out-Null

    $refs = @(foreach ($dll in Get-ChildItem -LiteralPath $appDir -File -Filter '*.dll' | Sort-Object Name) {
        try { $asmName = [Reflection.AssemblyName]::GetAssemblyName($dll.FullName) }
        catch [BadImageFormatException] { continue }
        $escName = [Security.SecurityElement]::Escape($asmName.Name)
        $escPath = [Security.SecurityElement]::Escape($dll.FullName)
        "    <Reference Include=`"$escName`"><HintPath>$escPath</HintPath><Private>false</Private></Reference>"
    })

    $binXml = [Security.SecurityElement]::Escape($binOut + [IO.Path]::DirectorySeparatorChar)
    $objXml = [Security.SecurityElement]::Escape($objOut + [IO.Path]::DirectorySeparatorChar)
    $dirProps = @"
<Project>
  <PropertyGroup>
    <BaseIntermediateOutputPath>$objXml</BaseIntermediateOutputPath>
    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
  </PropertyGroup>
</Project>
"@
    [IO.File]::WriteAllText((Join-Path $work 'Directory.Build.props'), $dirProps, [Text.UTF8Encoding]::new($false))

    $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>EditorBundledPluginsProbe</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>14.0</LangVersion>
    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>
    <NoStdLib>true</NoStdLib>
    <UseSharedCompilation>false</UseSharedCompilation>
    <UseAppHost>false</UseAppHost>
    <GenerateDependencyFile>false</GenerateDependencyFile>
    <GenerateRuntimeConfigurationFiles>false</GenerateRuntimeConfigurationFiles>
    <OutputPath>$binXml</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>
  <ItemGroup>
$($refs -join [Environment]::NewLine)
  </ItemGroup>
</Project>
"@
    $projPath = Join-Path $work 'Probe.csproj'
    [IO.File]::WriteAllText($projPath, $csproj, [Text.UTF8Encoding]::new($false))

    $dotnetCmd = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $dotnetCmd) {
        $fallbackDotnet = 'C:/Program Files/dotnet/dotnet.exe'
        if (-not (Test-Path -LiteralPath $fallbackDotnet -PathType Leaf)) {
            throw '未找到 dotnet.exe，无法运行随附插件加载探针。'
        }
        $dotnetExe = $fallbackDotnet
    } else {
        $dotnetExe = $dotnetCmd.Source
    }

    & $dotnetExe build $projPath -c Release --nologo -p:MSBuildEnableWorkloadResolver=false -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw "编译随附画风插件探针失败（退出码 $LASTEXITCODE）。"
    }

    $probeDll = Join-Path $binOut 'EditorBundledPluginsProbe.dll'
    if (-not (Test-Path -LiteralPath $probeDll -PathType Leaf)) {
        throw "未生成插件探针程序集：$probeDll"
    }

    $expectedArgs = @($bundled | ForEach-Object { "$($_.Id)=$($_.Style)" })
    & $dotnetExe exec `
        --runtimeconfig (Join-Path $appDir 'Mmd.Modern.App.runtimeconfig.json') `
        --depsfile (Join-Path $appDir 'Mmd.Modern.App.deps.json') `
        --additionalprobingpath $appDir `
        $probeDll `
        $appDir `
        @expectedArgs
    if ($LASTEXITCODE -ne 0) {
        throw "随附画风插件真实加载/激活校验失败（退出码 $LASTEXITCODE）。"
    }
}
finally {
    if ($autoWork -and (Test-Path -LiteralPath $work)) {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
