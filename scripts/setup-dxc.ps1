param(
    [string]$InstallDirectory,
    [string]$ExistingDirectory,
    [switch]$ForceInstall
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..'))
$taskCache = Join-Path $workspaceRoot '.dev-cache/ray-shader-toolchain-20261018'
$manifestPath = Join-Path $projectRoot 'tools/dxc/manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (!$InstallDirectory) { $InstallDirectory = Join-Path $taskCache "tools/dxc/$($manifest.releaseTag)" }
if (!$ExistingDirectory) { $ExistingDirectory = Join-Path $workspaceRoot '.dev-cache/modern-dotnet/dxc' }

function Assert-ControlledDestination([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $allowedRoots = @((Join-Path $workspaceRoot '.dev-cache'), (Join-Path $projectRoot 'tools'))
    $inside = $false
    foreach ($allowed in $allowedRoots) {
        $prefix = [IO.Path]::GetFullPath($allowed).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
        if ($absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $inside = $true }
    }
    if (!$inside) { throw "DXC destination must be inside this workspace's .dev-cache or project tools: $absolute" }
    return $absolute
}

function Test-PinnedCompiler([string]$Directory) {
    foreach ($file in $manifest.files) {
        $path = Join-Path $Directory $file.path
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $file.sha256) { return $false }
    }
    return $true
}

function Get-CompilerRecord([string]$Directory, [string]$Acquisition) {
    $compiler = Join-Path ([IO.Path]::GetFullPath($Directory)) 'bin/x64/dxc.exe'
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $compiler
    $start.Arguments = '--version'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['TEMP'] = Join-Path $taskCache 'tmp'
    $start.EnvironmentVariables['TMP'] = Join-Path $taskCache 'tmp'
    $process = New-Object Diagnostics.Process
    try {
        $process.StartInfo = $start
        [void]$process.Start()
        $version = $process.StandardOutput.ReadToEnd()
        $errors = $process.StandardError.ReadToEnd()
        if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'DXC --version timed out.' }
        if ($process.ExitCode -ne 0) { throw "DXC --version failed: $errors" }
        $version = ($version + $errors).Trim()
        if ($version -cne $manifest.versionOutput) { throw "DXC version does not match the pinned release: $version" }
        return [pscustomobject]@{
            CompilerPath = $compiler
            ReleaseTag = $manifest.releaseTag
            AssetUrl = $manifest.assetUrl
            AssetSha256 = $manifest.assetSha256
            Version = $version
            Acquisition = $Acquisition
            Manifest = $manifestPath
        }
    } finally { $process.Dispose() }
}

$taskCache = Assert-ControlledDestination $taskCache
$InstallDirectory = Assert-ControlledDestination $InstallDirectory
New-Item -ItemType Directory -Path (Join-Path $taskCache 'tmp') -Force | Out-Null
if (!$ForceInstall -and (Test-PinnedCompiler $ExistingDirectory)) {
    Get-CompilerRecord $ExistingDirectory 'verified-existing'
    return
}
if (Test-PinnedCompiler $InstallDirectory) {
    Get-CompilerRecord $InstallDirectory 'verified-existing'
    return
}
if (Test-Path -LiteralPath $InstallDirectory) {
    throw "Refusing to overwrite an unverified DXC directory. Select a new controlled -InstallDirectory: $InstallDirectory"
}

$archive = Join-Path $taskCache $manifest.assetName
$existingArchive = Join-Path $ExistingDirectory 'dxc.zip'
if (!(Test-Path -LiteralPath $archive)) {
    if ((Test-Path -LiteralPath $existingArchive) -and
        (Get-FileHash -LiteralPath $existingArchive -Algorithm SHA256).Hash -ieq $manifest.assetSha256) {
        Copy-Item -LiteralPath $existingArchive -Destination $archive
    } else {
        Invoke-WebRequest -Uri $manifest.assetUrl -OutFile $archive -UseBasicParsing
    }
}
if ((Get-Item -LiteralPath $archive).Length -ne $manifest.assetSize -or
    (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $manifest.assetSha256) {
    throw "DXC archive failed the pinned official size/SHA-256 check: $archive"
}

# Validate every archive target before extraction; no recursive delete/move or existing overwrite.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $prefix = $InstallDirectory.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    foreach ($entry in $zip.Entries) {
        $target = [IO.Path]::GetFullPath((Join-Path $InstallDirectory $entry.FullName))
        if (!$target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Archive entry escapes the verified DXC destination: $($entry.FullName)"
        }
    }
} finally { $zip.Dispose() }
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $InstallDirectory)
if (!(Test-PinnedCompiler $InstallDirectory)) { throw "Extracted DXC files failed their pinned SHA-256 checks: $InstallDirectory" }
Get-CompilerRecord $InstallDirectory 'verified-archive-extraction'
