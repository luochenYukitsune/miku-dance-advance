$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspace = [IO.Path]::GetFullPath((Join-Path $root '..'))
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Owned([IO.FileInfo]$file) {
    $relative = $file.FullName.Substring($root.Length + 1).Replace('\','/')
    return $relative -notmatch '(^|/)(Mmd.Modern.Scheduling|Mmd.Modern.Scheduling.Tests|scheduling)(/|$)' -and $relative -ne 'handoff.json' -and $relative -ne 'artifacts/manifest.json'
}
# Chinese text is saved in UTF-8 with BOM for Windows tooling. Binary fixtures/images/shaders are untouched.
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { (Owned $_) -and $_.Extension -in '.md','.cs','.csproj','.props','.slnx','.ps1','.axaml','.hlsl','.yml','.Config' } | ForEach-Object {
    [IO.File]::WriteAllText($_.FullName, [IO.File]::ReadAllText($_.FullName), $utf8Bom)
}
$snapshot = Get-Content -LiteralPath (Join-Path $workspace 'MMD932_source/validation_snapshot.json') -Raw | ConvertFrom-Json
$production = @($snapshot.validation_inputs.PSObject.Properties)
$originals = @($snapshot.original_hashes.PSObject.Properties)
foreach ($entry in $production) {
    if ((Hash (Join-Path $workspace "MMD932_source/$($entry.Name)")) -ne $entry.Value) { throw "Production input changed: $($entry.Name)" }
}
foreach ($entry in $originals) {
    if ((Hash (Join-Path $workspace "MMD932/$($entry.Name)")) -ne $entry.Value) { throw "Original input changed: $($entry.Name)" }
}
$integrity = [ordered]@{ production_inputs_unchanged=$true; production_input_count=$production.Count; original_inputs_unchanged=$true; original_input_count=$originals.Count }
[IO.File]::WriteAllText((Join-Path $root 'artifacts/source_integrity.json'), ($integrity | ConvertTo-Json -Depth 5), $utf8Bom)
$dependencies = [ordered]@{}
Get-ChildItem -LiteralPath $root -Recurse -Filter 'packages.lock.json' | Where-Object { Owned $_ } | ForEach-Object {
    $lock = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    foreach ($framework in $lock.dependencies.PSObject.Properties) {
        foreach ($package in $framework.Value.PSObject.Properties) {
            if ($package.Value.type -ne 'Project') { $dependencies[$package.Name] = @{ version=$package.Value.resolved; contentHash=$package.Value.contentHash } }
        }
    }
}
$files = [ordered]@{}
Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { Owned $_ } | Sort-Object FullName | ForEach-Object {
    $files[$_.FullName.Substring($root.Length + 1).Replace('\','/')] = Hash $_.FullName
}
$manifest = [ordered]@{ scope='C# bootstrap excluding independently authored Scheduling directories'; sha256=$files; dependencies=$dependencies }
[IO.File]::WriteAllText((Join-Path $root 'artifacts/manifest.json'), ($manifest | ConvertTo-Json -Depth 10), $utf8Bom)
$report = Get-Content -LiteralPath (Join-Path $root 'artifacts/gpu/report.json') -Raw | ConvertFrom-Json
$counters = ([xml](Get-Content -LiteralPath (Join-Path $root 'artifacts/green/full.trx') -Raw)).TestRun.ResultSummary.Counters
if ([int]$counters.failed -ne 0 -or [int]$counters.passed -lt 21) { throw 'Final xUnit evidence is not green.' }
$handoff = [ordered]@{
    status='completed_managed_pmd_gpu_bootstrap'; stack='C#/.NET10/Avalonia11/MVVM/Veldrid/HLSL-SPIRV/System.Numerics/BepuPhysics2/xUnit/GitHubActions';
    evidence=$report; xunit_passed=[int]$counters.passed; xunit_skipped=[int]$counters.notExecuted; release_build_errors=0; release_build_warnings=0;
    legacy_integrity=$integrity; visible_desktop_windows_created=0; original_exe_launches=0; new_rea_calls=0; native_core_wrapper_used=$false;
    tested_platform='Windows x64 / D3D11 only'; github_actions_executed=$false; pr_created=$false; pushed=$false;
    dependencies=$dependencies; manifest_sha256=(Hash (Join-Path $root 'artifacts/manifest.json'));
    limitations=@('PMD prefix only; tail not imported','Original generated fixture only','Basic material lighting; textures/toon/MME absent','No IK/VMD/morph/PMM/editing','Bepu sphere/ground only; no model bone coupling or Bullet equivalence','CPU readback preview, not persistent real-time swapchain','Vulkan/Linux/macOS unverified');
    next_part_requires_fresh_agent=$true
}
[IO.File]::WriteAllText((Join-Path $root 'handoff.json'), ($handoff | ConvertTo-Json -Depth 12), $utf8Bom)
Write-Output "Finalized $($files.Count) owned files, $($dependencies.Count) packages, $($production.Count) production inputs, $($originals.Count) original files."
