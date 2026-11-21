param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$libraries = [ordered]@{
    'rigid/mmd_bullet.dll' = 'build-bullet.py'
    'soft/mmd_bullet_soft.dll' = 'build-bullet-soft.py'
}
$cache = $env:MDA_BULLET_CACHE_DIR
$restore = $cache -and $env:MDA_BULLET_CACHE_HIT -eq 'true'
if (!$restore) {
    python -m pip install --disable-pip-version-check ziglang==0.16.0
    if ($LASTEXITCODE) { throw 'Zig installation failed.' }
    $zig = python -c "import pathlib,ziglang; print(pathlib.Path(ziglang.__file__).parent/'zig.exe')"
    if ($LASTEXITCODE) { throw 'Zig executable lookup failed.' }
}
foreach ($entry in $libraries.GetEnumerator()) {
    $file = Join-Path $OutputDirectory $entry.Key
    New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null
    if ($restore) {
        Copy-Item -LiteralPath (Join-Path $cache $entry.Key) -Destination $file
    } else {
        python (Join-Path $PSScriptRoot $entry.Value) --compiler $zig --output $file
        if ($LASTEXITCODE) { throw "Failed to build $($entry.Key)." }
        if ($cache) {
            $cached = Join-Path $cache $entry.Key
            New-Item -ItemType Directory -Force (Split-Path $cached) | Out-Null
            Copy-Item -LiteralPath $file -Destination $cached -Force
        }
    }
}
