<#
.SYNOPSIS
    按交错顺序（ABBA）运行 PerfBench 性能矩阵、收集机器信息、生成跨轮汇总与对照表。
.DESCRIPTION
    每个场景配置使用独立子进程；冷打开测试使用 MDA_WORK_ROOT / OutDir 下全新干净缓存目录，
    绝不删除或修改工作区共享的 .dev-cache\dxc-shader-cache。
#>
[CmdletBinding()]
param(
    [string]$BaselineDll,
    [string]$BaselineRepo,
    [string]$BaselineName = 'baseline',
    [string]$CandidateDll,
    [string]$CandidateRepo,
    [string]$CandidateName = 'candidate',
    [string]$OutDir,
    [Parameter(Mandatory)][string[]]$Models,
    [string]$Stage,
    [string[]]$Vmd = @(),
    [int[]]$Characters = @(1, 3, 5),
    [string[]]$Gi = @('off', 'on'),
    [string[]]$Sizes = @('960x544', '1920x1080'),
    [string]$Physics = 'independent',
    [ValidateSet('play', 'open-cold', 'open-warm', 'export')]
    [string[]]$Modes = @('play', 'open-cold', 'open-warm', 'export'),
    [int]$Rounds = 2,
    [int]$Warmup = 60,
    [int]$Frames = 300,
    [int]$SerialFrames = 60,
    [int]$Seeks = 3,
    [int]$ExportFrames = 60,
    [string]$Ffmpeg = $env:MMD_FFMPEG_PATH,
    [string]$Ffprobe = $env:MMD_FFPROBE_PATH,
    [ValidateSet('', '0', '1')]
    [string]$TieredCompilation = '',
    [string[]]$Switch = @(),
    [string[]]$BaselineSwitch = @(),
    [string[]]$CandidateSwitch = @(),
    [switch]$Smoke
)

$ErrorActionPreference = 'Stop'

if ($Smoke) {
    if (-not $PSBoundParameters.ContainsKey('Characters')) { $Characters = @(1) }
    if (-not $PSBoundParameters.ContainsKey('Gi')) { $Gi = @('off') }
    if (-not $PSBoundParameters.ContainsKey('Sizes')) { $Sizes = @('960x544') }
    if (-not $PSBoundParameters.ContainsKey('Warmup')) { $Warmup = 2 }
    if (-not $PSBoundParameters.ContainsKey('Frames')) { $Frames = 6 }
    if (-not $PSBoundParameters.ContainsKey('SerialFrames')) { $SerialFrames = 2 }
    if (-not $PSBoundParameters.ContainsKey('Seeks')) { $Seeks = 1 }
    if (-not $PSBoundParameters.ContainsKey('ExportFrames')) { $ExportFrames = 4 }
    if (-not $PSBoundParameters.ContainsKey('Rounds')) { $Rounds = 1 }
}

if ($Rounds -lt 1) { throw "Rounds must be >= 1, got $Rounds." }

$dotnet = if ($env:MDA_DOTNET_EXE) { $env:MDA_DOTNET_EXE } else { 'dotnet' }
$repoDefault = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$workspace = if ($env:MDA_WORKSPACE) { [IO.Path]::GetFullPath($env:MDA_WORKSPACE) } else { [IO.Path]::GetFullPath((Join-Path $repoDefault '..')) }

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $workRoot = if ($env:MDA_WORK_ROOT) { $env:MDA_WORK_ROOT } else { Join-Path $workspace 'work\mda-work\perf\bench' }
    $OutDir = Join-Path $workRoot ("matrix-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutDir = [IO.Path]::GetFullPath($OutDir)
if (-not $OutDir.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutDir must stay inside workspace '$workspace', got '$OutDir'."
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$resolvedModels = @($Models | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
$resolvedStage = if ([string]::IsNullOrWhiteSpace($Stage)) { $null } else { (Resolve-Path -LiteralPath $Stage).Path }
$resolvedVmds = @($Vmd | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })

if ($Modes -contains 'export') {
    if ([string]::IsNullOrWhiteSpace($Ffmpeg) -or -not (Test-Path -LiteralPath $Ffmpeg)) {
        throw "Export mode requires valid FFmpeg path (--Ffmpeg or MMD_FFMPEG_PATH)."
    }
    if ([string]::IsNullOrWhiteSpace($Ffprobe) -or -not (Test-Path -LiteralPath $Ffprobe)) {
        throw "Export mode requires valid FFprobe path (--Ffprobe or MMD_FFPROBE_PATH)."
    }
}

function Format-ProcessArgument {
    param([string]$Value)
    if ($null -eq $Value -or $Value.Length -eq 0) { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    $sb = [System.Text.StringBuilder]::new('"')
    $backslashes = 0
    foreach ($ch in $Value.ToCharArray()) {
        if ($ch -eq '\') { $backslashes++ }
        elseif ($ch -eq '"') {
            [void]$sb.Append('\', ($backslashes * 2 + 1))
            [void]$sb.Append('"')
            $backslashes = 0
        }
        else {
            if ($backslashes -gt 0) { [void]$sb.Append('\', $backslashes); $backslashes = 0 }
            [void]$sb.Append($ch)
        }
    }
    if ($backslashes -gt 0) { [void]$sb.Append('\', ($backslashes * 2)) }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Invoke-ProcessClean {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [hashtable]$EnvOverrides = @{},
        [int[]]$AllowedExitCodes = @(0)
    )
    $psi = [System.Diagnostics.ProcessStartInfo]::new($FilePath)
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $psi.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
    $psi.Arguments = (@($Arguments | ForEach-Object { Format-ProcessArgument $_ })) -join ' '
    foreach ($entry in $EnvOverrides.GetEnumerator()) {
        if ($null -eq $entry.Value) { [void]$psi.EnvironmentVariables.Remove([string]$entry.Key) }
        else { $psi.EnvironmentVariables[[string]$entry.Key] = [string]$entry.Value }
    }
    $proc = [System.Diagnostics.Process]::Start($psi)
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    $proc.WaitForExit()
    $stdout = $outTask.GetAwaiter().GetResult()
    $stderr = $errTask.GetAwaiter().GetResult()
    if ($stdout) { Write-Host $stdout.TrimEnd() }
    if ($stderr) { Write-Warning $stderr.TrimEnd() }
    if ($AllowedExitCodes -notcontains $proc.ExitCode) {
        throw "Command '$FilePath $($psi.Arguments)' exited with code $($proc.ExitCode)."
    }
    return $proc.ExitCode
}

function Resolve-BuildDll {
    param([string]$Name, [string]$Dll, [string]$Repo)
    if (-not [string]::IsNullOrWhiteSpace($Dll)) {
        return (Resolve-Path -LiteralPath $Dll).Path
    }
    $repoRoot = if ([string]::IsNullOrWhiteSpace($Repo)) { $repoDefault } else { (Resolve-Path -LiteralPath $Repo).Path }
    $localProj = Join-Path $repoRoot 'tools\perf\bench\PerfBench.csproj'
    $proj = if (Test-Path -LiteralPath $localProj) { $localProj } else { Join-Path $PSScriptRoot 'PerfBench.csproj' }
    $srcRoot = Join-Path $repoRoot 'src'
    $cacheParent = if ($env:ModernCacheRoot) { $env:ModernCacheRoot } else { Join-Path $workspace '.dev-cache' }
    $cacheRoot = Join-Path $cacheParent "perf-bench-$Name"
    $tmpRoot = Join-Path $cacheRoot 'tmp'
    New-Item -ItemType Directory -Force -Path $cacheRoot, $tmpRoot | Out-Null
    $envMap = @{
        ModernCacheRoot = $cacheRoot
        TEMP = $tmpRoot
        TMP = $tmpRoot
    }
    Write-Host "[$Name] Building $proj against $srcRoot ..."
    Invoke-ProcessClean -FilePath $dotnet -Arguments @('restore', $proj, '--locked-mode', "-p:MdaSrcRoot=$srcRoot", "-p:MdaRepoRoot=$repoRoot") -EnvOverrides $envMap | Out-Null
    Invoke-ProcessClean -FilePath $dotnet -Arguments @('build', $proj, '-c', 'Release', '--no-restore', '-m:3', "-p:MdaSrcRoot=$srcRoot", "-p:MdaRepoRoot=$repoRoot") -EnvOverrides $envMap | Out-Null
    $built = Join-Path $cacheRoot 'bin\PerfBench\Release\net10.0\PerfBench.dll'
    if (-not (Test-Path -LiteralPath $built)) { throw "Built PerfBench.dll not found at '$built'." }
    return $built
}

function New-IsolatedCacheEnv {
    param([string]$Root, [string]$Tiered)
    $dirs = @('dxc', 'local', 'roaming', 'shader', 'fxc', 'pso', 'tmp')
    foreach ($d in $dirs) { New-Item -ItemType Directory -Force -Path (Join-Path $Root $d) | Out-Null }
    $map = @{
        MMD_DXC_CACHE = (Join-Path $Root 'dxc')
        LOCALAPPDATA = (Join-Path $Root 'local')
        APPDATA = (Join-Path $Root 'roaming')
        MMD_SHADER_CACHE = (Join-Path $Root 'shader')
        MMD_FXC_CACHE = (Join-Path $Root 'fxc')
        MMD_PSO_CACHE = (Join-Path $Root 'pso')
        TEMP = (Join-Path $Root 'tmp')
        TMP = (Join-Path $Root 'tmp')
    }
    if (-not [string]::IsNullOrWhiteSpace($Tiered)) {
        $map['DOTNET_TieredCompilation'] = $Tiered
    }
    return $map
}

function New-Variant {
    param([string]$Name, [string]$Dll, [string[]]$ExtraSwitches)
    $dir = Join-Path $OutDir $Name
    $runsDir = Join-Path $dir 'runs'
    $workCache = Join-Path $OutDir "_cache\work\$Name"
    $warmCache = Join-Path $OutDir "_cache\warm\$Name"
    New-Item -ItemType Directory -Force -Path $runsDir, $workCache, $warmCache | Out-Null
    return [pscustomobject]@{
        Name = $Name
        Dll = $Dll
        Dir = $dir
        RunsDir = $runsDir
        WorkCache = $workCache
        WarmCache = $warmCache
        ExtraSwitches = @($Switch + $ExtraSwitches)
    }
}

$baseDllResolved = Resolve-BuildDll -Name $BaselineName -Dll $BaselineDll -Repo $BaselineRepo
$variants = [System.Collections.Generic.List[object]]::new()
$variants.Add((New-Variant -Name $BaselineName -Dll $baseDllResolved -ExtraSwitches $BaselineSwitch))

$hasCandidate = (-not [string]::IsNullOrWhiteSpace($CandidateDll)) -or (-not [string]::IsNullOrWhiteSpace($CandidateRepo))
if ($hasCandidate) {
    if ($BaselineName -eq $CandidateName) { throw "BaselineName and CandidateName must differ." }
    $candDllResolved = Resolve-BuildDll -Name $CandidateName -Dll $CandidateDll -Repo $CandidateRepo
    $variants.Add((New-Variant -Name $CandidateName -Dll $candDllResolved -ExtraSwitches $CandidateSwitch))
}

foreach ($v in $variants) {
    $machinePath = Join-Path $v.Dir 'machine.json'
    $cacheEnv = New-IsolatedCacheEnv -Root $v.WorkCache -Tiered $TieredCompilation
    Write-Host "[$($v.Name)] Collecting machine info -> $machinePath"
    Invoke-ProcessClean -FilePath $dotnet -Arguments @($v.Dll, 'machine', '--out', $machinePath) -EnvOverrides $cacheEnv | Out-Null
}

function Build-BenchArgs {
    param([object]$Variant, [string]$Kind, [int]$Chars, [string]$GiMode, [string]$Size, [string]$OutPath, [hashtable]$Tags)
    $a = [System.Collections.Generic.List[string]]::new()
    [void]$a.Add($Variant.Dll)
    [void]$a.Add($Kind)
    foreach ($m in $resolvedModels) { [void]$a.Add('--model'); [void]$a.Add($m) }
    [void]$a.Add('--characters'); [void]$a.Add($Chars.ToString())
    if ($resolvedStage) { [void]$a.Add('--stage'); [void]$a.Add($resolvedStage) }
    foreach ($vmdItem in $resolvedVmds) { [void]$a.Add('--vmd'); [void]$a.Add($vmdItem) }
    [void]$a.Add('--gi'); [void]$a.Add($GiMode)
    [void]$a.Add('--size'); [void]$a.Add($Size)
    [void]$a.Add('--physics'); [void]$a.Add($Physics)
    [void]$a.Add('--warmup'); [void]$a.Add($Warmup.ToString())
    [void]$a.Add('--frames'); [void]$a.Add($Frames.ToString())
    [void]$a.Add('--serial-frames'); [void]$a.Add($SerialFrames.ToString())
    [void]$a.Add('--seeks'); [void]$a.Add($Seeks.ToString())
    [void]$a.Add('--export-frames'); [void]$a.Add($ExportFrames.ToString())
    if ($Kind -eq 'export') {
        [void]$a.Add('--ffmpeg'); [void]$a.Add($Ffmpeg)
        [void]$a.Add('--ffprobe'); [void]$a.Add($Ffprobe)
    }
    foreach ($sw in $Variant.ExtraSwitches) {
        if (-not [string]::IsNullOrWhiteSpace($sw)) { [void]$a.Add('--switch'); [void]$a.Add($sw) }
    }
    foreach ($kv in $Tags.GetEnumerator()) {
        [void]$a.Add('--tag'); [void]$a.Add("$($kv.Key)=$($kv.Value)")
    }
    [void]$a.Add('--out'); [void]$a.Add($OutPath)
    return $a.ToArray()
}

$warmSeeded = @{}

function Invoke-ScenarioRun {
    param([object]$Variant, [string]$Mode, [int]$Chars, [string]$GiMode, [string]$Size, [int]$Round)
    $stageTag = if ($resolvedStage) { '+stage' } else { '' }
    $key = "c$Chars$stageTag-gi$GiMode-$Size-$Physics"
    $benchKind = switch ($Mode) {
        'open-cold' { 'open' }
        'open-warm' { 'open' }
        default { $Mode }
    }
    $tags = @{ build = $Variant.Name; round = $Round.ToString() }
    if ($Mode -eq 'open-cold') { $tags['cache'] = 'cold' }
    elseif ($Mode -eq 'open-warm') { $tags['cache'] = 'warm' }

    if ($Mode -eq 'open-warm') {
        $seedKey = "$($Variant.Name)|$key"
        if (-not $warmSeeded.ContainsKey($seedKey)) {
            $seedOut = Join-Path $Variant.WarmCache "seed-$key.json"
            $seedEnv = New-IsolatedCacheEnv -Root $Variant.WarmCache -Tiered $TieredCompilation
            $seedArgs = Build-BenchArgs -Variant $Variant -Kind 'open' -Chars $Chars -GiMode $GiMode -Size $Size -OutPath $seedOut -Tags @{ build = $Variant.Name; cache = 'warm-seed' }
            Write-Host "[$($Variant.Name)] Seeding warm cache: $key"
            [void](Invoke-ProcessClean -FilePath $dotnet -Arguments $seedArgs -EnvOverrides $seedEnv -AllowedExitCodes @(0, 2))
            $warmSeeded[$seedKey] = $true
        }
        $cacheRoot = $Variant.WarmCache
    }
    elseif ($Mode -eq 'open-cold') {
        $cacheRoot = Join-Path $OutDir "_cache\cold\$($Variant.Name)\$key\r$Round"
        if (Test-Path -LiteralPath $cacheRoot) { Remove-Item -LiteralPath $cacheRoot -Recurse -Force }
    }
    else {
        $cacheRoot = $Variant.WorkCache
    }

    $cacheEnv = New-IsolatedCacheEnv -Root $cacheRoot -Tiered $TieredCompilation
    $outFile = Join-Path $Variant.RunsDir "$Mode-$key-r$Round.json"
    $benchArgs = Build-BenchArgs -Variant $Variant -Kind $benchKind -Chars $Chars -GiMode $GiMode -Size $Size -OutPath $outFile -Tags $tags
    Write-Host "[$($Variant.Name)] Round $Round | $Mode | $key"
    $code = Invoke-ProcessClean -FilePath $dotnet -Arguments $benchArgs -EnvOverrides $cacheEnv -AllowedExitCodes @(0, 2)
    if ($code -eq 2) { Write-Warning "[$($Variant.Name)] Scenario failed (status=failed recorded): $outFile" }
}

foreach ($chars in $Characters) {
    foreach ($giMode in $Gi) {
        foreach ($size in $Sizes) {
            foreach ($mode in $Modes) {
                for ($r = 0; $r -lt $Rounds; $r++) {
                    if ($hasCandidate) {
                        $order = if (($r % 2) -eq 0) { @($variants[0], $variants[1]) } else { @($variants[1], $variants[0]) }
                        foreach ($v in $order) {
                            Invoke-ScenarioRun -Variant $v -Mode $mode -Chars $chars -GiMode $giMode -Size $size -Round $r
                        }
                    }
                    else {
                        Invoke-ScenarioRun -Variant $variants[0] -Mode $mode -Chars $chars -GiMode $giMode -Size $size -Round $r
                    }
                }
            }
        }
    }
}

foreach ($v in $variants) {
    $sumOut = Join-Path $v.Dir 'summary'
    $cacheEnv = New-IsolatedCacheEnv -Root $v.WorkCache -Tiered $TieredCompilation
    Write-Host "[$($v.Name)] Summarizing -> $sumOut.md"
    Invoke-ProcessClean -FilePath $dotnet -Arguments @($v.Dll, 'summarize', '--dir', $v.Dir, '--out', $sumOut) -EnvOverrides $cacheEnv | Out-Null
}

if ($hasCandidate) {
    $cmpOut = Join-Path $OutDir 'compare'
    $cacheEnv = New-IsolatedCacheEnv -Root $variants[0].WorkCache -Tiered $TieredCompilation
    Write-Host "Comparing $($variants[0].Name) vs $($variants[1].Name) -> $cmpOut.md"
    Invoke-ProcessClean -FilePath $dotnet -Arguments @($variants[0].Dll, '--compare', $variants[0].Dir, $variants[1].Dir, '--out', $cmpOut) -EnvOverrides $cacheEnv | Out-Null
}

Write-Host "Done. Output directory: $OutDir"
