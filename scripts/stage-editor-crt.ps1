#Requires -Version 5.1
<#
.SYNOPSIS
Stages pinned, unmodified Microsoft x64 VC runtime dependencies beside each optional upscaler.
.DESCRIPTION
Downloads nothing. Supply the official VSIX and license document separately. All identities,
signatures, PE headers, versions and destination conflicts are checked before publish writes.
The signature preflight extracts only three known entries into a private peripheral cache.
Existing published files, including an earlier CRT staging, are never overwritten.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$RedistArchivePath,
    [Parameter(Mandatory = $true)][string]$LicensePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

$archiveSha256 = '24a1c8dba0376d9b008ac2ba430d13c06664fc6f163511fd7866485030e289af'
$archiveBytes = 3397395L
$archiveUrl = 'https://download.visualstudio.microsoft.com/download/pr/3984a6ef-2ffc-4c07-962e-bf9e9b862819/24a1c8dba0376d9b008ac2ba430d13c06664fc6f163511fd7866485030e289af/Microsoft.VC.14.51.CRT.Redist.X64.base.vsix'
$entryPrefix = 'Contents/VC/Redist/MSVC/14.51.36231/x64/Microsoft.VC145.CRT/'
$minimumRuntimeVersion = [Version]'14.44.35229.0'
$licenseSha256 = '08651651a7602fc7c0e2763de0fde1ff9f868df2780597cd1775ee9d6441c783'
$licenseBytes = 39553L
$licenseUrl = 'https://visualstudio.microsoft.com/wp-content/uploads/2025/10/Visual-C-V14-License-Redistributable_and_Runtime_ENU.docx'
$runtimeLicenseTermsUrl = 'https://visualstudio.microsoft.com/license-terms/vs2026-ga-visualcpp-v14-redist-runtime/'
$redistTermsUrl = 'https://learn.microsoft.com/visualstudio/releases/2026/redistribution'
$licenseName = 'Visual-C-V14-Runtime-2026-License.docx'
$dllSpecifications = @(
    [pscustomobject]@{ Name = 'MSVCP140.dll'; EntryName = 'msvcp140.dll'; Bytes = 643512L },
    [pscustomobject]@{ Name = 'VCRUNTIME140.dll'; EntryName = 'vcruntime140.dll'; Bytes = 178616L },
    [pscustomobject]@{ Name = 'VCRUNTIME140_1.dll'; EntryName = 'vcruntime140_1.dll'; Bytes = 50112L }
)
$providers = @('fsr', 'xess', 'dlss')

function Assert-NoReparsePath([string]$Path) {
    # Inspect existing ancestors too: checking only the leaf misses directory junctions.
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse points are not accepted: $cursor"
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ([string]::Equals($parent, $cursor, [StringComparison]::OrdinalIgnoreCase)) { break }
        $cursor = $parent
    }
}

function Get-ExistingFileSystemPath([string]$Path, [bool]$Directory) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSProvider.Name -ne 'FileSystem' -or $item.PSIsContainer -ne $Directory) {
        throw "Expected an existing $(if ($Directory) { 'directory' } else { 'file' }): $Path"
    }
    Assert-NoReparsePath $item.FullName
    return $item.FullName
}

function Get-ChildPublishPath([string]$RelativePath) {
    $candidate = [IO.Path]::GetFullPath((Join-Path $published $RelativePath))
    if (-not $candidate.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Destination escapes PublishDirectory: $RelativePath"
    }
    Assert-NoReparsePath $candidate
    return $candidate
}

function Assert-NewDestination([string]$Path) {
    Assert-NoReparsePath $Path
    if ($null -ne (Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue)) {
        throw "Refusing to overwrite an existing destination: $Path"
    }
    $cursor = [IO.Path]::GetDirectoryName($Path)
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and -not $item.PSIsContainer) {
            throw "A destination parent is an existing file: $cursor"
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

function Get-StreamSha256([IO.Stream]$Stream) {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hasher.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose() }
}

function Get-BytesSha256([byte[]]$Bytes) {
    $buffer = [IO.MemoryStream]::new($Bytes, $false)
    try { return (Get-StreamSha256 $buffer) }
    finally { $buffer.Dispose() }
}

function Read-StreamBytes([IO.Stream]$Stream) {
    $buffer = [IO.MemoryStream]::new()
    try { $Stream.CopyTo($buffer); return ,$buffer.ToArray() }
    finally { $buffer.Dispose() }
}

function Assert-X64Dll([byte[]]$Bytes, [string]$Name) {
    if ($Bytes.Length -lt 64 -or [BitConverter]::ToUInt16($Bytes, 0) -ne 0x5a4d) {
        throw "Invalid DOS header: $Name"
    }
    $pe = [BitConverter]::ToInt32($Bytes, 0x3c)
    if ($pe -lt 64 -or $pe -gt $Bytes.Length - 26 -or
        [BitConverter]::ToUInt32($Bytes, $pe) -ne 0x00004550 -or
        [BitConverter]::ToUInt16($Bytes, $pe + 4) -ne 0x8664 -or
        [BitConverter]::ToUInt16($Bytes, $pe + 24) -ne 0x020b -or
        ([BitConverter]::ToUInt16($Bytes, $pe + 22) -band 0x2000) -eq 0) {
        throw "Expected a PE32+ AMD64 DLL: $Name"
    }
}

function Write-NewBytes([string]$Path, [byte[]]$Bytes) {
    # CreateNew closes the preflight/write race without permitting replacement.
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush() }
    finally { $stream.Dispose() }
}

$publishPath = Get-ExistingFileSystemPath $PublishDirectory $true
$published = $publishPath.TrimEnd('\', '/')
if (-not $published -or [string]::Equals($published, ([IO.Path]::GetPathRoot($publishPath)).TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PublishDirectory must name a publish directory, not a drive/share root.'
}
$publishPrefix = $published + [IO.Path]::DirectorySeparatorChar
$archivePath = Get-ExistingFileSystemPath $RedistArchivePath $false
$licenseFile = Get-ExistingFileSystemPath $LicensePath $false
$destinations = [Collections.Generic.List[object]]::new()
foreach ($provider in $providers) {
    foreach ($specification in $dllSpecifications) {
        $relative = 'tools/upscaling/' + $provider + '/' + $specification.Name
        $destination = Get-ChildPublishPath $relative
        Assert-NewDestination $destination
        $destinations.Add([pscustomobject]@{ Path = $destination; RelativePath = $relative; Name = $specification.Name })
    }
}
$licenseRelative = 'tools/upscaling/licenses/vc-runtime/' + $licenseName
$provenanceRelative = 'tools/upscaling/licenses/vc-runtime/provenance.json'
$licenseDestination = Get-ChildPublishPath $licenseRelative
$provenanceDestination = Get-ChildPublishPath $provenanceRelative
Assert-NewDestination $licenseDestination
Assert-NewDestination $provenanceDestination

$archiveStream = $null
$licenseStream = $null
$zip = $null
$validationDirectory = $null
$temporaryFiles = [Collections.Generic.List[string]]::new()
try {
    # Keep read handles open so the validated input cannot be replaced while staging.
    $archiveStream = [IO.File]::Open($archivePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $licenseStream = [IO.File]::Open($licenseFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    if ($archiveStream.Length -ne $archiveBytes -or (Get-StreamSha256 $archiveStream) -ne $archiveSha256) {
        throw 'Redist archive does not match the pinned official Microsoft VSIX.'
    }
    if ($licenseStream.Length -ne $licenseBytes -or (Get-StreamSha256 $licenseStream) -ne $licenseSha256) {
        throw 'LicensePath does not match the pinned official Visual C++ v14 Runtime 2026 license document.'
    }
    $archiveStream.Position = 0
    $licenseStream.Position = 0
    $licenseContent = Read-StreamBytes $licenseStream
    $zip = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Read, $true)
    $runtimeFiles = [Collections.Generic.List[object]]::new()
    foreach ($specification in $dllSpecifications) {
        $entryMatches = @($zip.Entries | Where-Object {
            [string]::Equals([IO.Path]::GetFileName($_.FullName.Replace('\', '/')), $specification.EntryName, [StringComparison]::OrdinalIgnoreCase)
        })
        $expectedEntry = $entryPrefix + $specification.EntryName
        if ($entryMatches.Count -ne 1 -or -not [string]::Equals($entryMatches[0].FullName, $expectedEntry, [StringComparison]::Ordinal)) {
            throw "Archive must contain exactly one expected basename at $expectedEntry"
        }
        $entry = $entryMatches[0]
        if ($entry.Length -ne $specification.Bytes) { throw "Unexpected archive entry length: $expectedEntry" }
        $entryStream = $entry.Open()
        try { $content = Read-StreamBytes $entryStream }
        finally { $entryStream.Dispose() }
        if ($content.Length -ne $specification.Bytes) { throw "Incomplete archive entry: $expectedEntry" }
        Assert-X64Dll $content $specification.Name
        $runtimeFiles.Add([pscustomobject]@{ Name = $specification.Name; Entry = $expectedEntry; Content = $content;
            Sha256 = (Get-BytesSha256 $content); Version = $null; ProductVersion = $null; Signer = $null; Thumbprint = $null })
    }

    # Authenticode and native version resources require files. These are private temporary
    # files outside PublishDirectory; this is not a write to the release payload.
    $workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $validationRoot = Join-Path $workspace '.dev-cache/editor-crt-validation'
    Assert-NoReparsePath $validationRoot
    $validationDirectory = Join-Path $validationRoot ([Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $validationDirectory) { throw 'Temporary validation directory already exists.' }
    [IO.Directory]::CreateDirectory($validationDirectory) | Out-Null
    $commonVersion = $null
    foreach ($runtimeFile in $runtimeFiles) {
        $temporaryFile = Join-Path $validationDirectory $runtimeFile.Name
        Write-NewBytes $temporaryFile $runtimeFile.Content
        $temporaryFiles.Add($temporaryFile)
        $signature = Get-AuthenticodeSignature -LiteralPath $temporaryFile
        $knownPublisherNames = @('Microsoft Corporation', 'Microsoft Windows', 'Microsoft Windows Software Compatibility Publisher')
        if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
            $knownPublisherNames -cnotcontains $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -or
            $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(?=,|$)') {
            throw "Expected a valid Microsoft Corporation Authenticode signature: $($runtimeFile.Name) ($($signature.Status))"
        }
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($temporaryFile)
        $versionIdentity = '{0}.{1}.{2}.{3}' -f $version.FileMajorPart, $version.FileMinorPart, $version.FileBuildPart, $version.FilePrivatePart
        if ($version.FileMajorPart -le 0 -or ($null -ne $commonVersion -and $versionIdentity -cne $commonVersion)) {
            throw "CRT files must have one nonzero file version: $($runtimeFile.Name)"
        }
        if ([Version]$versionIdentity -lt $minimumRuntimeVersion) {
            throw "CRT file version $versionIdentity is older than required build tools $minimumRuntimeVersion ($($runtimeFile.Name))."
        }
        $commonVersion = $versionIdentity
        $runtimeFile.Version = $versionIdentity
        $runtimeFile.ProductVersion = $version.ProductVersion
        $runtimeFile.Signer = $signature.SignerCertificate.Subject
        $runtimeFile.Thumbprint = $signature.SignerCertificate.Thumbprint.ToLowerInvariant()
    }

    $provenance = [ordered]@{
        schemaVersion = 1
        component = 'Microsoft Visual C++ Runtime (x64, app-local)'
        fileVersion = $commonVersion
        minimumFileVersion = $minimumRuntimeVersion.ToString()
        architecture = 'AMD64'
        unmodified = $true
        source = [ordered]@{ url = $archiveUrl; sha256 = $archiveSha256; bytes = $archiveBytes; archiveEntryDirectory = $entryPrefix }
        license = [ordered]@{ name = 'Microsoft Visual C++ v14 Runtime 2026 proprietary license terms'; openSource = $false;
            url = $licenseUrl; termsUrl = $runtimeLicenseTermsUrl; redistributionListUrl = $redistTermsUrl;
            path = $licenseRelative; sha256 = $licenseSha256; bytes = $licenseBytes }
        placementReason = 'Each optional vendor DLL is loaded by full path with UseDllDirectoryForDependencies | System32; its CRT dependencies are placed in that same DLL directory.'
        files = @($runtimeFiles | ForEach-Object {
            [ordered]@{ name = $_.Name; archiveEntry = $_.Entry; bytes = $_.Content.Length; sha256 = $_.Sha256;
                fileVersion = $_.Version; productVersion = $_.ProductVersion; authenticodeStatus = 'Valid'; signer = $_.Signer;
                signerCertificateThumbprint = $_.Thumbprint; destinations = @() }
        })
    }
    # Resolve the per-file destination list outside the record-building pipeline.
    foreach ($fileRecord in $provenance.files) {
        $fileRecord.destinations = @($destinations | Where-Object { $_.Name -eq $fileRecord.name } | ForEach-Object { $_.RelativePath })
    }
    $provenanceContent = [Text.UTF8Encoding]::new($false).GetBytes(($provenance | ConvertTo-Json -Depth 10) + "`r`n")
    $writePlan = [Collections.Generic.List[object]]::new()
    foreach ($destination in $destinations) {
        $runtimeFile = @($runtimeFiles | Where-Object { $_.Name -eq $destination.Name })[0]
        $writePlan.Add([pscustomobject]@{ Path = $destination.Path; Content = $runtimeFile.Content; Sha256 = $runtimeFile.Sha256 })
    }
    $writePlan.Add([pscustomobject]@{ Path = $licenseDestination; Content = $licenseContent; Sha256 = $licenseSha256 })
    $writePlan.Add([pscustomobject]@{ Path = $provenanceDestination; Content = $provenanceContent; Sha256 = (Get-BytesSha256 $provenanceContent) })
    foreach ($write in $writePlan) { Assert-NewDestination $write.Path }

    # All preflight checks have passed. Only now create publish directories/files.
    foreach ($write in $writePlan) {
        Assert-NewDestination $write.Path
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($write.Path)) | Out-Null
        Assert-NoReparsePath $write.Path
        Write-NewBytes $write.Path $write.Content
    }
    Write-Output "Staged unmodified Microsoft CRT ${commonVersion}: 3 DLLs beside each of fsr/xess/dlss."
    Write-Output "License and provenance: $(Get-ChildPublishPath 'tools/upscaling/licenses/vc-runtime')"
}
finally {
    if ($null -ne $zip) { $zip.Dispose() }
    if ($null -ne $archiveStream) { $archiveStream.Dispose() }
    if ($null -ne $licenseStream) { $licenseStream.Dispose() }
    # Delete only the exact files this invocation created, and then its empty directory.
    # No recursive deletion or cleanup of any published/user file is performed.
    foreach ($temporaryFile in $temporaryFiles) {
        if (Test-Path -LiteralPath $temporaryFile -PathType Leaf) { Remove-Item -LiteralPath $temporaryFile -ErrorAction Continue }
    }
    if ($validationDirectory -and (Test-Path -LiteralPath $validationDirectory -PathType Container)) {
        Remove-Item -LiteralPath $validationDirectory -ErrorAction Continue
    }
}
