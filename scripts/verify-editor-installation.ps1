<#
.SYNOPSIS
Verifies an internal MDA Inno install, version upgrade, uninstall and reinstall, serially.
.DESCRIPTION
Run only with two reviewed local MDA installers. This script does not build or compile anything.
It refuses an existing MDA AppId registration and existing target/shortcut files. The default
install target is a new task directory, not the user's normal installation directory.
Existing preferences are read and hashed, never backed up over or restored. Only this task's
unique AppData marker is removed. No user project is opened or changed.
The installed, unmodified EXE is started hidden without diagnostic arguments. Its PID, image
and app-local CLR are inspected, then only that verified process is force-stopped. This is a
startup check, not an HWND, visual, interaction, clean-machine or release-acceptance claim.
Use -SkipApplicationStartup for installer-only lifecycle verification without starting the app;
each application startup check is reported as skipped, including the app-local CLR observation.
Use -SkipReinstall to stop after the upgraded application is uninstalled. It cannot be combined
with -RemoveReinstalledApplication. -UpgradeProjectAssociations selects the PMA/PMC association
tasks on upgrade and verifies their HKCU values, relocation targets and uninstall cleanup.
Use -TaskDirectory for a new owned working directory outside a shared/reparse build cache.
Optional -UpgradeInstallDirectory requests a different /DIR during upgrade. For this option,
the base install, upgrade install and evidence directories must be distinct siblings under one
new shared task directory. Both installation trees are inventoried; old files are never deleted
by this script, and remaining files in either tree fail the uninstall check.
Classic is omitted by default. Select it independently for the base/upgrade installer with
-BaseClassicComponent/-UpgradeClassicComponent, and explicitly use -AcceptClassicLicense after
reviewing its included terms. -VerifyClassicLicenseRejection checks the unacknowledged silent
branch first; it does not prove the interactive checkbox branch.
Official references (no copied implementation):
https://jrsoftware.org/ishelp/topic_setupcmdline.htm
https://jrsoftware.org/ishelp/topic_setupexitcodes.htm
https://jrsoftware.org/ishelp/topic_uninstcmdline.htm
https://jrsoftware.org/ishelp/topic_setup_appid.htm
.EXAMPLE
./scripts/verify-editor-installation.ps1 -BaseInstaller C:/internal/MDA-0.1.0-win-x64-setup.exe `
    -UpgradeInstaller C:/internal/MDA-0.1.1-win-x64-setup.exe -RemoveReinstalledApplication
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BaseInstaller,
    [Parameter(Mandatory = $true)][string]$UpgradeInstaller,
    [string]$InstallDirectory,
    [string]$OutputDirectory,
    [switch]$BaseClassicComponent,
    [switch]$UpgradeClassicComponent,
    [switch]$AcceptClassicLicense,
    [switch]$VerifyClassicLicenseRejection,
    [ValidateRange(10, 120)][int]$StartupTimeoutSeconds = 30,
    [ValidateRange(60, 3600)][int]$InstallerTimeoutSeconds = 900,
    [switch]$RemoveReinstalledApplication,
    [switch]$SkipReinstall,
    [switch]$UpgradeProjectAssociations,
    [switch]$SkipApplicationStartup,
    [string]$TaskDirectory,
    [ValidateNotNullOrEmpty()][string]$UpgradeInstallDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($SkipReinstall -and $RemoveReinstalledApplication) { throw '-SkipReinstall cannot be combined with -RemoveReinstalledApplication.' }
$taskWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskRunId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
if ($TaskDirectory -and -not [IO.Path]::IsPathRooted($TaskDirectory)) { throw '-TaskDirectory must be an absolute new owned directory.' }
$taskRoot = if ($TaskDirectory) { [IO.Path]::GetFullPath($TaskDirectory).TrimEnd('\', '/') }
    else { Join-Path $taskWorkspace ('.dev-cache/editor-installation/' + $taskRunId) }
$taskInstall = if ($InstallDirectory) { [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\', '/') } else { Join-Path $taskRoot 'app' }
$taskBaseInstall = $taskInstall
$taskDirectoryChangeRequested = $PSBoundParameters.ContainsKey('UpgradeInstallDirectory')
$taskUpgradeInstall = if ($taskDirectoryChangeRequested) { [IO.Path]::GetFullPath($UpgradeInstallDirectory).TrimEnd('\', '/') } else { $taskInstall }
$taskOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/') } else { Join-Path $taskRoot 'evidence' }
$taskAppId = 'MDA.MikuDanceAdvance'
$taskRegistryPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $taskAppId + '_is1'
$taskAppData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MDA'
$taskAppMarker = Join-Path $taskAppData ('.installation-verification-' + $taskRunId + '.txt')
$taskProjectMarker = Join-Path $taskRoot 'user-project-artifact/retention-marker.txt'
$taskStartShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'MDA/MDA.lnk'
$taskDesktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'MDA.lnk'
$taskSteps = [Collections.Generic.List[object]]::new()
$taskReport = [ordered]@{
    schemaVersion = 1; runId = $taskRunId; startedUtc = [DateTime]::UtcNow.ToString('O')
    appId = $taskAppId; taskDirectory = $taskRoot; installDirectory = $taskInstall; outputDirectory = $taskOutput
    activeInstallDirectory = $taskInstall; upgradeInstallDirectory = $taskUpgradeInstall
    scope = $(if ($SkipApplicationStartup) { 'internal Inno lifecycle only; application process/app-local CLR startup checks skipped' }
        else { 'internal Inno lifecycle and unmodified EXE process/app-local CLR startup only' })
    provesVisualOrInteractionAcceptance = $false; provesCleanMachine = $false; releaseAcceptance = $false
    provesInteractiveClassicLicenseAcceptance = $false; skipApplicationStartup = [bool]$SkipApplicationStartup
    upgradeDirectoryChange = [ordered]@{ requested = $taskDirectoryChangeRequested; attempted = $false; targetDirectoryVerified = $false
        bothDirectoriesEmptyAfterUninstall = $false
        ownershipRoot = $null; originalDirectory = $taskBaseInstall; requestedDirectory = $taskUpgradeInstall
        beforeUpgrade = @(); afterUpgrade = @(); oldDirectoryFilesRemainAfterUpgrade = $null }
    finalInstallationTrees = @()
    removeReinstalledApplication = [bool]$RemoveReinstalledApplication; skipReinstall = [bool]$SkipReinstall
    upgradeProjectAssociations = [bool]$UpgradeProjectAssociations; projectAssociationsBefore = @()
    baseClassicComponent = [bool]$BaseClassicComponent; upgradeClassicComponent = [bool]$UpgradeClassicComponent
    acceptClassicLicense = [bool]$AcceptClassicLicense; verifyClassicLicenseRejection = [bool]$VerifyClassicLicenseRejection
    baseInstaller = $null; upgradeInstaller = $null; preferencesBefore = @(); preferencesAfter = @()
    expectedStepOrder = @('preflight') + $(if ($VerifyClassicLicenseRejection) { @('classic-license-rejection') } else { @() }) +
        @('fresh-install', 'version-upgrade', 'uninstall') + $(if ($SkipReinstall) { @() } else { @('reinstall') }) +
        $(if ($RemoveReinstalledApplication) { @('final-uninstall') } else { @() })
    steps = $taskSteps; appDataMarkerRemoved = $false; finalRegistrations = @()
    success = $false; failure = $null; completedUtc = $null
}

function Write-InstallationJson($Value, [string]$Path) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 25), [Text.UTF8Encoding]::new($false))
}
function Test-SamePath([string]$Left, [string]$Right) {
    [IO.Path]::GetFullPath($Left).TrimEnd('\', '/').Equals(
        [IO.Path]::GetFullPath($Right).TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)
}
function Test-ChildPath([string]$Child, [string]$Parent) {
    [IO.Path]::GetFullPath($Child).StartsWith([IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}
function Assert-NoReparseAncestor([string]$Path) {
    $taskAncestor = [IO.Path]::GetFullPath($Path)
    while ($taskAncestor) {
        if (Test-Path -LiteralPath $taskAncestor) {
            if (((Get-Item -LiteralPath $taskAncestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a reparse point: $taskAncestor"
            }
        }
        $taskAncestor = [IO.Path]::GetDirectoryName($taskAncestor)
    }
}
function Get-FileIdentity([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file missing: $Path" }
    $taskFile = Get-Item -LiteralPath $Path -Force
    [pscustomobject]@{ path = $taskFile.FullName; bytes = $taskFile.Length
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
function Get-InstallationTree([string]$Path) {
    Assert-NoReparseAncestor $Path
    $taskTreeExists = Test-Path -LiteralPath $Path
    $taskTreeFiles = @()
    if ($taskTreeExists) {
        if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "Installation tree is not a directory: $Path" }
        try { $taskTreeItems = @(Get-ChildItem -LiteralPath $Path -Recurse -Force) }
        catch [System.Management.Automation.ItemNotFoundException] {
            if (Test-Path -LiteralPath $Path) { throw }
            # The uninstaller's post-exit helper may finish removing an empty tree.
            $taskTreeExists = $false
            $taskTreeItems = @()
        }
        foreach ($taskTreeItem in $taskTreeItems) {
            if (($taskTreeItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Installation tree contains a reparse point: $($taskTreeItem.FullName)"
            }
        }
        $taskTreeFiles = @($taskTreeItems | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName | ForEach-Object {
            [pscustomobject]@{ path = $_.FullName.Substring($Path.Length).TrimStart('\', '/'); bytes = $_.Length }
        })
    }
    [pscustomobject]@{ directory = $Path; exists = $taskTreeExists; fileCount = $taskTreeFiles.Count
        totalBytes = $(if ($taskTreeFiles.Count) { [long](($taskTreeFiles | Measure-Object -Property bytes -Sum).Sum) } else { 0L }); files = $taskTreeFiles
        scope = 'file paths and sizes; active installation manifest hashes are checked separately' }
}
function Get-InstallationTrees {
    Get-InstallationTree $taskBaseInstall
    if ($taskDirectoryChangeRequested) { Get-InstallationTree $taskUpgradeInstall }
}
function Get-InstallerIdentity([string]$Path) {
    $taskIdentity = Get-FileIdentity ((Resolve-Path -LiteralPath $Path).Path)
    $taskVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskIdentity.path)
    $taskVersionText = ([string]$taskVersionInfo.ProductVersion).Trim()
    if ($taskVersionText -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
        throw "Installer must have a numeric ProductVersion: $($taskIdentity.path) ($taskVersionText)"
    }
    if ($taskVersionInfo.ProductName -notmatch '^MDA(?:\s|$|\()') {
        throw "Installer ProductName is not MDA: $($taskIdentity.path) ($($taskVersionInfo.ProductName))"
    }
    [pscustomobject]@{ path = $taskIdentity.path; bytes = $taskIdentity.bytes; sha256 = $taskIdentity.sha256
        version = $taskVersionText; productName = $taskVersionInfo.ProductName }
}
function ConvertTo-NormalizedVersion([string]$Text) {
    $taskVersion = [version]$Text
    [version]::new($taskVersion.Major, $taskVersion.Minor, [Math]::Max(0, $taskVersion.Build), [Math]::Max(0, $taskVersion.Revision))
}
function Get-MdaRegistrations {
    foreach ($taskHive in [Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryHive]::LocalMachine) {
        foreach ($taskView in [Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32) {
            $taskBaseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($taskHive, $taskView)
            try {
                $taskKey = $taskBaseKey.OpenSubKey($taskRegistryPath, $false)
                if ($null -eq $taskKey) { continue }
                try {
                    [pscustomobject]@{ hive = $taskHive.ToString(); view = $taskView.ToString(); key = $taskRegistryPath
                        displayName = $taskKey.GetValue('DisplayName'); displayVersion = $taskKey.GetValue('DisplayVersion')
                        installLocation = $taskKey.GetValue('InstallLocation'); uninstallString = $taskKey.GetValue('UninstallString') }
                } finally { $taskKey.Dispose() }
            } finally { $taskBaseKey.Dispose() }
        }
    }
}
function Assert-Registration([string]$ExpectedVersion) {
    $taskEntries = @(Get-MdaRegistrations)
    # Some HKCU keys are shared between the two registry views. Preserve both observations,
    # but count their identical registration values once.
    $taskUnique = @($taskEntries | Group-Object { $_.hive + '|' + $_.displayVersion + '|' + $_.installLocation + '|' + $_.uninstallString })
    if ($taskUnique.Count -ne 1 -or $taskEntries[0].hive -ne 'CurrentUser') {
        throw "Expected one per-user MDA registration; observed $($taskUnique.Count) unique registrations."
    }
    foreach ($taskEntry in $taskEntries) {
        if (-not $taskEntry.installLocation -or -not (Test-SamePath $taskEntry.installLocation $taskInstall)) {
            throw "Registration points outside this task's installation: $($taskEntry.installLocation)"
        }
        if ((ConvertTo-NormalizedVersion $taskEntry.displayVersion) -ne (ConvertTo-NormalizedVersion $ExpectedVersion)) {
            throw "Registered version $($taskEntry.displayVersion) does not match installer $ExpectedVersion."
        }
    }
    return $taskEntries
}
function Get-ProjectAssociations {
    $taskClasses = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        foreach ($taskProject in @(
            @{ extension = '.pma'; progId = 'MDA.AdvanceProject'; description = 'MDA Advance project' },
            @{ extension = '.pmc'; progId = 'MDA.ClassicProject'; description = 'MDA Classic project' })) {
            $taskAssociation = [ordered]@{ extension = $taskProject.extension; progId = $taskProject.progId; description = $taskProject.description }
            foreach ($taskValue in @(
                @{ field = 'extensionDefault'; key = ('Software\Classes\' + $taskProject.extension); name = '' },
                @{ field = 'progIdDefault'; key = ('Software\Classes\' + $taskProject.progId); name = '' },
                @{ field = 'command'; key = ('Software\Classes\' + $taskProject.progId + '\shell\open\command'); name = '' },
                @{ field = 'defaultIcon'; key = ('Software\Classes\' + $taskProject.progId + '\DefaultIcon'); name = '' },
                @{ field = 'openWith'; key = ('Software\Classes\' + $taskProject.extension + '\OpenWithProgids'); name = $taskProject.progId },
                @{ field = 'supportedType'; key = 'Software\Classes\Applications\Mmd.Modern.App.exe\SupportedTypes'; name = $taskProject.extension })) {
                $taskKey = $taskClasses.OpenSubKey($taskValue.key, $false)
                try {
                    $taskValueExists = $null -ne $taskKey -and $taskKey.GetValueNames() -contains $taskValue.name
                    $taskAssociation[$taskValue.field] = [pscustomobject]@{ key = $taskValue.key; name = $taskValue.name
                        keyExists = $null -ne $taskKey; exists = $taskValueExists
                        value = $(if ($taskValueExists) { $taskKey.GetValue($taskValue.name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } else { $null })
                        kind = $(if ($taskValueExists) { $taskKey.GetValueKind($taskValue.name).ToString() } else { $null }) }
                } finally { if ($null -ne $taskKey) { $taskKey.Dispose() } }
            }
            [pscustomobject]$taskAssociation
        }
    } finally { $taskClasses.Dispose() }
}
function Assert-ProjectAssociations([bool]$Installed) {
    $taskAssociations = @(Get-ProjectAssociations)
    foreach ($taskAssociation in $taskAssociations) {
        $taskBefore = @($taskReport.projectAssociationsBefore | Where-Object { $_.extension -eq $taskAssociation.extension })[0]
        $taskExpectedDefault = if ($Installed -and -not $taskBefore.extensionDefault.exists) {
            [pscustomobject]@{ exists = $true; value = $taskAssociation.progId; kind = 'String' }
        } else { $taskBefore.extensionDefault }
        if ($taskAssociation.extensionDefault.exists -ne $taskExpectedDefault.exists -or
            $taskAssociation.extensionDefault.value -cne $taskExpectedDefault.value -or
            $taskAssociation.extensionDefault.kind -ne $taskExpectedDefault.kind) {
            throw "Project extension default was not preserved/cleaned as expected: $($taskAssociation.extension)"
        }
        if ($Installed) {
            $taskExpectedValues = @{
                progIdDefault = $taskAssociation.description
                command = ('"' + (Join-Path $taskInstall 'Mmd.Modern.App.exe') + '" --open "%1"')
                defaultIcon = ('"' + (Join-Path $taskInstall 'Mmd.Modern.App.exe') + '",0')
                openWith = ''; supportedType = ''
            }
            foreach ($taskField in $taskExpectedValues.Keys) {
                $taskValue = $taskAssociation.$taskField
                if (-not $taskValue.exists -or $taskValue.kind -ne 'String' -or $taskValue.value -cne $taskExpectedValues[$taskField]) {
                    throw "Project association value does not match the active installation: $($taskValue.key) [$($taskValue.name)]"
                }
            }
        } elseif ($taskAssociation.progIdDefault.keyExists -or $taskAssociation.openWith.exists -or $taskAssociation.supportedType.exists) {
            throw "Owned project association values remain: $($taskAssociation.progId)"
        }
    }
    return $taskAssociations
}
function Get-PreferencesSnapshot {
    foreach ($taskName in 'editor-preferences.json', 'editor-preview.json', 'editor-output.json') {
        $taskPath = Join-Path $taskAppData $taskName
        $taskExists = Test-Path -LiteralPath $taskPath -PathType Leaf
        [pscustomobject]@{ path = $taskPath; exists = $taskExists
            sha256 = $(if ($taskExists) { (Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }) }
    }
}
function Assert-PreferencesPreserved {
    $taskCurrent = @(Get-PreferencesSnapshot)
    foreach ($taskBefore in $taskReport.preferencesBefore) {
        $taskAfter = @($taskCurrent | Where-Object { $_.path -eq $taskBefore.path })[0]
        if ($taskBefore.exists -and (-not $taskAfter.exists -or $taskAfter.sha256 -ne $taskBefore.sha256)) {
            throw "An existing preference file changed; it will not be overwritten or restored: $($taskBefore.path)"
        }
    }
    return $taskCurrent
}
function Assert-MarkersPreserved {
    foreach ($taskMarker in $taskMarkerIdentities) {
        if ((Get-FileIdentity $taskMarker.path).sha256 -ne $taskMarker.sha256) {
            throw "Task retention marker changed: $($taskMarker.path)"
        }
    }
}
function Get-ShortcutIdentity([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Shortcut missing: $Path" }
    $taskShell = New-Object -ComObject WScript.Shell
    try {
        $taskShortcut = $taskShell.CreateShortcut($Path)
        try {
            [pscustomobject]@{ path = $Path; target = $taskShortcut.TargetPath; workingDirectory = $taskShortcut.WorkingDirectory }
        } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($taskShortcut) }
    } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($taskShell) }
}
function Assert-Shortcuts([bool]$DesktopExpected) {
    $taskPaths = @($taskStartShortcut)
    if ($DesktopExpected) { $taskPaths += $taskDesktopShortcut }
    elseif (Test-Path -LiteralPath $taskDesktopShortcut) { throw "Unselected desktop shortcut exists: $taskDesktopShortcut" }
    foreach ($taskPath in $taskPaths) {
        $taskLink = Get-ShortcutIdentity $taskPath
        if (-not (Test-SamePath $taskLink.target (Join-Path $taskInstall 'Mmd.Modern.App.exe')) -or
            -not (Test-SamePath $taskLink.workingDirectory $taskInstall)) { throw "Shortcut target/working directory mismatch: $taskPath" }
        $taskLink
    }
}
function Assert-InstalledPayload([string]$ExpectedVersion, [bool]$ClassicSelected = $false) {
    $taskManifestPath = Join-Path $taskInstall 'package-manifest.json'
    $taskManifest = Get-Content -LiteralPath $taskManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($taskManifest.schemaVersion -ne 1 -or $taskManifest.product -ne 'MDA' -or
        -not $taskManifest.selfContained -or $taskManifest.runtime -ne 'win-x64' -or
        (ConvertTo-NormalizedVersion $taskManifest.version) -ne (ConvertTo-NormalizedVersion $ExpectedVersion)) {
        throw 'Installed package manifest product/version/runtime does not match this lifecycle step.'
    }
    foreach ($taskItem in @(Get-Item -LiteralPath $taskInstall -Force) + @(Get-ChildItem -LiteralPath $taskInstall -Recurse -Force)) {
        if (($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Installed reparse point is not accepted: $($taskItem.FullName)" }
    }
    $taskClassicDirectory = Join-Path $taskInstall 'tools/classic'
    $taskClassicIncluded = $taskManifest.PSObject.Properties.Name -contains 'classicComponentIncluded' -and $taskManifest.classicComponentIncluded -eq $true
    if ($ClassicSelected -and -not $taskClassicIncluded) { throw 'Classic was selected but this installer manifest has no Classic component.' }
    if (-not $ClassicSelected -and (Test-Path -LiteralPath $taskClassicDirectory)) { throw 'Unselected Classic component is present in the installation.' }
    $taskSeen = @{}
    $taskInstalledSeen = @{}
    $taskClassicFiles = 0
    foreach ($taskFile in $taskManifest.files) {
        if ($taskFile.path -isnot [string] -or [IO.Path]::IsPathRooted($taskFile.path) -or
            $taskFile.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid installed manifest file identity.' }
        $taskPath = [IO.Path]::GetFullPath((Join-Path $taskInstall $taskFile.path))
        if (-not (Test-ChildPath $taskPath $taskInstall) -or $taskSeen.ContainsKey($taskPath)) { throw "Invalid/duplicate manifest path: $($taskFile.path)" }
        $taskSeen[$taskPath] = $true
        if (Test-ChildPath $taskPath $taskClassicDirectory) {
            $taskClassicFiles++
            if (-not $taskClassicIncluded) { throw 'Classic manifest files require classicComponentIncluded=true.' }
            if (-not $ClassicSelected) { continue }
        }
        $taskIdentity = Get-FileIdentity $taskPath
        if ($taskIdentity.sha256 -ne $taskFile.sha256 -or $taskIdentity.bytes -ne $taskFile.bytes) {
            throw "Installed payload hash/size mismatch: $($taskFile.path)"
        }
        $taskInstalledSeen[$taskPath] = $true
    }
    if ($taskClassicIncluded -and $taskClassicFiles -eq 0) { throw 'Classic component is declared but has no manifest-bound files.' }
    if ($ClassicSelected) {
        foreach ($taskRequired in 'classic-runtime.json', 'Mmd.Modern.D3D9.Compatibility.dll', 'd3dx9_43.dll', 'd3dcompiler_43.dll',
            'provenance.json', 'licenses/Microsoft.DXSDK.D3DX/LICENSE.txt', 'licenses/Microsoft.DXSDK.D3DX/NOTICE.md',
            'licenses/Microsoft.DXSDK.D3DX/README.md', 'licenses/Microsoft.DXSDK.D3DX/Microsoft.DXSDK.D3DX.nuspec',
            'licenses/MikuDanceStudio/LICENSE') {
            if (-not $taskInstalledSeen.ContainsKey([IO.Path]::GetFullPath((Join-Path $taskClassicDirectory $taskRequired)))) {
                throw "Required installed Classic file is not manifest-bound: $taskRequired"
            }
        }
    }
    foreach ($taskRequired in 'Mmd.Modern.App.exe', 'Mmd.Modern.App.dll', 'Mmd.Modern.App.runtimeconfig.json',
        'Mmd.Modern.App.deps.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll',
        'Mmd.Modern.Rendering.D3D12.dll', 'Mmd.Modern.ViewOutput.D3D12.dll', 'mmd_bullet.dll', 'mmd_bullet_soft.dll',
        'mmd_mikktspace.dll', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'av_libglesv2.dll',
        'tools/dxc/dxc.exe', 'tools/dxc/dxcompiler.dll', 'tools/ffmpeg/ffmpeg.exe', 'tools/ffmpeg/ffprobe.exe',
        'plugins/mda.style.genshin/Mda.StylePlugin.Genshin.dll',
        'plugins/mda.style.genshin/Mda.StylePlugin.Genshin.deps.json',
        'plugins/mda.style.genshin/mda-plugin.json',
        'plugins/mda.style.star-rail/Mda.StylePlugin.StarRail.dll',
        'plugins/mda.style.star-rail/Mda.StylePlugin.StarRail.deps.json',
        'plugins/mda.style.star-rail/mda-plugin.json',
        'plugins/mda.style.wuthering-waves/Mda.StylePlugin.WutheringWaves.dll',
        'plugins/mda.style.wuthering-waves/Mda.StylePlugin.WutheringWaves.deps.json',
        'plugins/mda.style.wuthering-waves/mda-plugin.json',
        'plugins/mda.style.endfield/Mda.StylePlugin.Endfield.dll',
        'plugins/mda.style.endfield/Mda.StylePlugin.Endfield.deps.json',
        'plugins/mda.style.endfield/mda-plugin.json') {
        if (-not $taskInstalledSeen.ContainsKey([IO.Path]::GetFullPath((Join-Path $taskInstall $taskRequired)))) {
            throw "Required installed file is not manifest-bound: $taskRequired"
        }
    }
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskInstall -Recurse -File -Force) {
        if ($taskInstalledSeen.ContainsKey($taskFile.FullName) -or (Test-SamePath $taskFile.FullName $taskManifestPath)) { continue }
        if ($taskFile.DirectoryName -ne $taskInstall -or $taskFile.Name -notmatch '^unins\d+\.(exe|dat|msg)$') {
            throw "Unexpected installed file outside manifest/Inno metadata: $($taskFile.FullName)"
        }
    }
    [pscustomobject]@{ manifest = Get-FileIdentity $taskManifestPath; version = $taskManifest.version; verifiedFiles = $taskInstalledSeen.Count
        classicComponentSelected = $ClassicSelected; classicComponentIncluded = $taskClassicIncluded
        omittedClassicFiles = $(if ($ClassicSelected) { 0 } else { $taskClassicFiles }) }
}
function Stop-VerifiedTaskProcess([Diagnostics.Process]$Process, [string]$ExpectedPath, [long]$ExpectedStartTicks) {
    $Process.Refresh()
    if ($Process.HasExited) { return }
    $taskLive = Get-Process -Id $Process.Id -ErrorAction Stop
    try {
        if (-not (Test-SamePath $taskLive.Path $ExpectedPath) -or $taskLive.StartTime.ToUniversalTime().Ticks -ne $ExpectedStartTicks) {
            throw "Refusing to stop PID $($Process.Id): process identity changed."
        }
        Stop-Process -Id $taskLive.Id -Force -ErrorAction Stop
        if (-not $Process.WaitForExit(10000)) { throw "Verified task PID $($Process.Id) did not stop." }
    } finally { $taskLive.Dispose() }
}
function Test-InstalledExeStartup {
    $taskExe = Join-Path $taskInstall 'Mmd.Modern.App.exe'
    $taskProcess = Start-Process -FilePath $taskExe -WorkingDirectory $taskInstall -WindowStyle Hidden -PassThru
    $taskStartTicks = $null
    $taskEvidence = [ordered]@{ status = 'running'; failure = $null; pid = $taskProcess.Id; expectedImage = $taskExe; image = $null; startedUtc = $null
        appLocalClr = $null; modules = @(); termination = 'not attempted'; visualVerified = $false }
    try {
        $taskStartTicks = $taskProcess.StartTime.ToUniversalTime().Ticks
        $taskEvidence.startedUtc = $taskProcess.StartTime.ToUniversalTime().ToString('O')
        $taskDeadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
        do {
            $taskProcess.Refresh()
            if ($taskProcess.HasExited) { throw "Installed EXE exited before startup evidence, code $($taskProcess.ExitCode)." }
            $taskEvidence.image = $taskProcess.Path
            if (-not (Test-SamePath $taskEvidence.image $taskExe)) { throw 'Started process image is not the installed EXE.' }
            $taskEvidence.modules = @($taskProcess.Modules | ForEach-Object { [pscustomobject]@{ name = $_.ModuleName; path = $_.FileName } })
            $taskClr = @($taskEvidence.modules | Where-Object { $_.name -ieq 'coreclr.dll' })
            if ($taskClr.Count -eq 1) {
                if (-not (Test-SamePath $taskClr[0].path (Join-Path $taskInstall 'coreclr.dll'))) { throw 'Installed EXE loaded CLR outside its self-contained installation.' }
                $taskEvidence.appLocalClr = Get-FileIdentity $taskClr[0].path
                break
            }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $taskDeadline)
        if ($null -eq $taskEvidence.appLocalClr) { throw 'Installed EXE did not load its app-local CLR before the startup timeout.' }
        # Allow an immediate managed startup failure to become visible; this is still no UI check.
        Start-Sleep -Seconds 2
        $taskProcess.Refresh()
        if ($taskProcess.HasExited) { throw "Installed EXE exited during startup observation, code $($taskProcess.ExitCode)." }
    } catch {
        $taskEvidence.status = 'failed'; $taskEvidence.failure = $_.ToString()
        throw
    } finally {
        try {
            if ($null -ne $taskStartTicks) {
                Stop-VerifiedTaskProcess $taskProcess $taskExe $taskStartTicks
                $taskEvidence.termination = 'only own verified PID force-stopped or already exited'
            }
            if ($taskEvidence.status -eq 'running') { $taskEvidence.status = 'passed' }
        } catch {
            $taskEvidence.status = 'failed'; $taskEvidence.failure = $_.ToString()
            throw
        } finally {
            Write-InstallationJson $taskEvidence (Join-Path $taskOutput ($taskActiveStep.id + '-exe-startup.json'))
            $taskProcess.Dispose()
        }
    }
    return [pscustomobject]$taskEvidence
}
function Invoke-Inno([string]$Executable, [string[]]$Arguments, [string]$LogName, [int]$ExpectedExitCode = 0) {
    $taskLog = Join-Path $taskOutput $LogName
    $taskArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') + $Arguments + @('/LOG="' + $taskLog + '"')
    $taskActiveStep.command = [pscustomobject]@{ executable = $Executable; arguments = $taskArguments; log = $taskLog; pid = $null; exitCode = $null }
    $taskProcess = Start-Process -FilePath $Executable -ArgumentList $taskArguments -WindowStyle Hidden -PassThru
    try {
        # Cache the process handle before waiting so Windows PowerShell retains ExitCode.
        [void]$taskProcess.Handle
        $taskActiveStep.command.pid = $taskProcess.Id
        if (-not $taskProcess.WaitForExit($InstallerTimeoutSeconds * 1000)) {
            throw "Inno exceeded the timeout; PID $($taskProcess.Id) is left running for diagnosis. No dependent step will run."
        }
        $taskActiveStep.command.exitCode = $taskProcess.ExitCode
        if ($taskProcess.ExitCode -ne $ExpectedExitCode) { throw "Inno returned $($taskProcess.ExitCode), expected $ExpectedExitCode; see $taskLog" }
        if (-not (Test-Path -LiteralPath $taskLog -PathType Leaf)) { throw "Inno did not create its requested log: $taskLog" }
    } finally { $taskProcess.Dispose() }
}
function Get-InstallArguments([bool]$DesktopIcon, [bool]$ClassicSelected, [bool]$LicenseAccepted, [bool]$ProjectAssociationsSelected = $false) {
    $taskTasks = if ($DesktopIcon) { 'desktopicon' } else { '!desktopicon' }
    if ($ProjectAssociationsSelected) { $taskTasks += ',associatepma,associatepmc' }
    $taskComponents = if ($ClassicSelected) { 'editor,d3d12,classic' } else { 'editor,d3d12' }
    @('/SP-', '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS', ('/DIR="' + $taskInstall + '"'),
        ('/TASKS="' + $taskTasks + '"'), ('/COMPONENTS="' + $taskComponents + '"'))
    if ($ClassicSelected -and $LicenseAccepted) { '/ACCEPTCLASSICLICENSE=yes' }
}
function Invoke-Install([object]$Installer, [bool]$DesktopIcon, [bool]$ClassicSelected, [bool]$ProjectAssociationsSelected = $false) {
    if ((Get-FileIdentity $Installer.path).sha256 -ne $Installer.sha256) { throw 'Installer changed since preflight.' }
    Invoke-Inno $Installer.path @(Get-InstallArguments $DesktopIcon $ClassicSelected ([bool]$AcceptClassicLicense) $ProjectAssociationsSelected) ($taskActiveStep.id + '-inno.log')
    $taskActiveStep.registrations = @(Assert-Registration $Installer.version)
    $taskActiveStep.payload = Assert-InstalledPayload $Installer.version $ClassicSelected
    $taskActiveStep.shortcuts = @(Assert-Shortcuts $DesktopIcon)
    if ($UpgradeProjectAssociations) { $taskActiveStep.projectAssociations = @(Assert-ProjectAssociations $ProjectAssociationsSelected) }
    if ($SkipApplicationStartup) {
        $taskActiveStep.startup = [pscustomobject]@{ status = 'skipped'; reason = '-SkipApplicationStartup requested; harness did not start an application process'
            appLocalClrVerified = $false; visualVerified = $false }
    } else {
        $taskActiveStep.startup = Test-InstalledExeStartup
    }
    Assert-MarkersPreserved
    $taskActiveStep.preferences = @(Assert-PreferencesPreserved)
}
function Test-ClassicLicenseRejection([object]$Installer) {
    if ((Get-FileIdentity $Installer.path).sha256 -ne $Installer.sha256) { throw 'Installer changed since preflight.' }
    $taskLogName = $taskActiveStep.id + '-inno.log'
    # PrepareToInstall rejects an unacknowledged license before file/registration changes.
    Invoke-Inno $Installer.path @(Get-InstallArguments $false $true $false) $taskLogName 7
    if (-not (Select-String -LiteralPath (Join-Path $taskOutput $taskLogName) -SimpleMatch '/ACCEPTCLASSICLICENSE=yes' -Quiet)) {
        throw 'Installer failed without the expected Classic license acknowledgement diagnostic.'
    }
    if (@(Get-MdaRegistrations).Count -ne 0 -or (Test-InstalledFilesRemain)) { throw 'License rejection changed installation files or registration.' }
    foreach ($taskPath in $taskStartShortcut, $taskDesktopShortcut) {
        if (Test-Path -LiteralPath $taskPath) { throw "License rejection created a shortcut: $taskPath" }
    }
    Assert-MarkersPreserved
    $taskActiveStep.preferences = @(Assert-PreferencesPreserved)
}
function Test-InstalledFilesRemain([string]$Directory = $taskInstall) {
    if (-not (Test-Path -LiteralPath $Directory)) { return $false }
    try {
        return @(Get-ChildItem -LiteralPath $Directory -Recurse -Force -File -ErrorAction Stop).Count -ne 0
    } catch [System.Management.Automation.ItemNotFoundException] {
        # Inno's post-exit helper may remove the directory between the existence
        # check and enumeration. A disappearing child while the root remains is
        # retried by the bounded caller; access and other errors still propagate.
        return Test-Path -LiteralPath $Directory
    }
}
function Invoke-Uninstall([string]$ExpectedVersion) {
    $taskRegistration = @(Assert-Registration $ExpectedVersion)[0]
    $taskUninstallText = [string]$taskRegistration.uninstallString
    if ($taskUninstallText -match '^"([^"\r\n]+\.exe)"(?:\s.*)?$') { $taskUninstaller = $Matches[1] }
    elseif ($taskUninstallText -match '^([^\s"\r\n]+\.exe)(?:\s.*)?$') { $taskUninstaller = $Matches[1] }
    else { throw "Cannot safely parse registered UninstallString: $taskUninstallText" }
    Assert-NoReparseAncestor $taskUninstaller
    if (-not (Test-ChildPath $taskUninstaller $taskInstall) -or
        [IO.Path]::GetFileName($taskUninstaller) -notmatch '^unins\d+\.exe$') { throw 'Registered uninstaller is outside this task installation.' }
    $taskActiveStep.uninstaller = Get-FileIdentity $taskUninstaller
    Invoke-Inno $taskUninstaller @() ($taskActiveStep.id + '-inno.log')
    # Inno may briefly retain its own executable while its post-exit deletion completes.
    $taskRemovalDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $taskFilesRemain = Test-InstalledFilesRemain
        if (-not $taskFilesRemain) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $taskRemovalDeadline)
    $taskActiveStep.installationTrees = @(Get-InstallationTrees)
    $taskRemaining = @(Get-MdaRegistrations)
    if ($taskRemaining.Count -ne 0) { throw 'MDA uninstall registration remains after uninstall.' }
    foreach ($taskPath in $taskStartShortcut, $taskDesktopShortcut, (Join-Path $taskInstall 'Mmd.Modern.App.exe')) {
        if (Test-Path -LiteralPath $taskPath) { throw "Tracked application/shortcut remains after uninstall: $taskPath" }
    }
    if (Test-InstalledFilesRemain) {
        throw 'Installed files remain after uninstall; no recursive deletion will be attempted.'
    }
    if ($taskDirectoryChangeRequested -and (Test-InstalledFilesRemain $taskBaseInstall)) {
        throw 'Files remain in the original installation directory after relocation/uninstall; no recursive deletion will be attempted.'
    }
    if ($taskDirectoryChangeRequested) { $taskReport.upgradeDirectoryChange.bothDirectoriesEmptyAfterUninstall = $true }
    if ($UpgradeProjectAssociations) { $taskActiveStep.projectAssociations = @(Assert-ProjectAssociations $false) }
    Assert-MarkersPreserved
    $taskActiveStep.retentionMarkers = $taskMarkerIdentities
    $taskActiveStep.preferences = @(Assert-PreferencesPreserved)
}
function Invoke-LifecycleStep([string]$Id, [scriptblock]$Action) {
    $script:taskActiveStep = [ordered]@{ id = $Id; status = 'running'; startedUtc = [DateTime]::UtcNow.ToString('O'); completedUtc = $null
        installDirectory = $taskInstall
        command = $null; registrations = @(); payload = $null; shortcuts = @(); startup = $null; preferences = @(); uninstaller = $null; retentionMarkers = @(); installationTrees = @(); projectAssociations = @(); failure = $null }
    $taskSteps.Add($taskActiveStep)
    try { & $Action; $taskActiveStep.status = 'passed' }
    catch { $taskActiveStep.status = 'failed'; $taskActiveStep.failure = $_.ToString(); throw }
    finally {
        $taskActiveStep.completedUtc = [DateTime]::UtcNow.ToString('O')
        Write-InstallationJson $taskReport (Join-Path $taskOutput 'installation-lifecycle-report.json')
    }
}

# All output paths are new. No installer, registry or user setting is touched before preflight.
$taskNewPaths = @($taskRoot, $taskOutput, $taskInstall)
if ($taskDirectoryChangeRequested) { $taskNewPaths += $taskUpgradeInstall }
foreach ($taskPath in $taskNewPaths) {
    if ($taskPath.Contains('"') -or $taskPath.IndexOfAny([char[]]"`r`n") -ge 0) { throw 'Quotes/newlines are not accepted in lifecycle paths.' }
    Assert-NoReparseAncestor $taskPath
    if (Test-Path -LiteralPath $taskPath) { throw "Lifecycle target must be new: $taskPath" }
}
if ((Test-SamePath $taskOutput $taskInstall) -or (Test-ChildPath $taskOutput $taskInstall) -or
    (Test-ChildPath $taskInstall $taskOutput) -or (Test-SamePath $taskInstall $taskAppData) -or
    (Test-ChildPath $taskInstall $taskAppData)) { throw 'Install/output paths must be independent and outside the MDA user-data directory.' }
if ($taskDirectoryChangeRequested) {
    # Only opt-in relocation gains this stricter ownership boundary. The common parent
    # must itself be new, so neither an existing installation nor an arbitrary sibling
    # of a pre-existing evidence directory can become the second installation target.
    $taskOwnershipRoot = [IO.Path]::GetDirectoryName($taskBaseInstall)
    if ((Test-SamePath $taskUpgradeInstall $taskBaseInstall) -or
        -not (Test-SamePath ([IO.Path]::GetDirectoryName($taskOutput)) $taskOwnershipRoot) -or
        -not (Test-SamePath ([IO.Path]::GetDirectoryName($taskUpgradeInstall)) $taskOwnershipRoot) -or
        (Test-SamePath $taskUpgradeInstall $taskOutput)) {
        throw '-UpgradeInstallDirectory requires distinct base/upgrade/evidence siblings under one new shared task directory.'
    }
    Assert-NoReparseAncestor $taskOwnershipRoot
    if ((Test-Path -LiteralPath $taskOwnershipRoot) -or (Test-SamePath $taskOwnershipRoot $taskAppData) -or
        (Test-ChildPath $taskOwnershipRoot $taskAppData)) {
        throw 'The relocation ownership directory must be new and outside MDA user data.'
    }
    $taskReport.upgradeDirectoryChange.ownershipRoot = $taskOwnershipRoot
}
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskMarkerIdentities = @()
try {
    Invoke-LifecycleStep 'preflight' {
        if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell for Windows x64 process/module inspection.' }
        if (($BaseClassicComponent -or $UpgradeClassicComponent) -and -not $AcceptClassicLicense) {
            throw 'Selected Classic lifecycle steps require explicit -AcceptClassicLicense after reviewing the included Microsoft terms.'
        }
        if ($VerifyClassicLicenseRejection -and -not ($BaseClassicComponent -or $UpgradeClassicComponent)) {
            throw '-VerifyClassicLicenseRejection requires a selected Classic-bearing base or upgrade installer.'
        }
        if ($BaseClassicComponent -and -not $UpgradeClassicComponent) {
            throw 'Classic deselection during upgrade needs a separate component-removal test; use a fresh lifecycle with Classic omitted instead.'
        }
        $taskExisting = @(Get-MdaRegistrations)
        if ($taskExisting.Count -ne 0) {
            $taskActiveStep.registrations = $taskExisting
            throw 'An MDA AppId registration already exists. It is not removed or upgraded by this verification. Use a clean test account.'
        }
        if ($UpgradeProjectAssociations) {
            $taskReport.projectAssociationsBefore = @(Get-ProjectAssociations)
            foreach ($taskAssociation in $taskReport.projectAssociationsBefore) {
                if ($taskAssociation.progIdDefault.keyExists -or $taskAssociation.openWith.exists -or $taskAssociation.supportedType.exists -or
                    ($taskAssociation.extensionDefault.exists -and $taskAssociation.extensionDefault.value -eq $taskAssociation.progId)) {
                    throw "An owned project association already exists; it will not be overwritten: $($taskAssociation.progId)"
                }
            }
        }
        foreach ($taskPath in $taskStartShortcut, $taskDesktopShortcut, $taskAppMarker, $taskProjectMarker) {
            if (Test-Path -LiteralPath $taskPath) { throw "Existing shortcut/marker will not be overwritten: $taskPath" }
            Assert-NoReparseAncestor $taskPath
        }
        $taskReport.baseInstaller = Get-InstallerIdentity $BaseInstaller
        $taskReport.upgradeInstaller = Get-InstallerIdentity $UpgradeInstaller
        if ($taskReport.baseInstaller.sha256 -eq $taskReport.upgradeInstaller.sha256 -or
            (ConvertTo-NormalizedVersion $taskReport.upgradeInstaller.version) -le (ConvertTo-NormalizedVersion $taskReport.baseInstaller.version)) {
            throw 'Upgrade requires a different installer SHA256 and a strictly higher ProductVersion; same-version repair is not an upgrade test.'
        }
        foreach ($taskInstaller in $taskReport.baseInstaller, $taskReport.upgradeInstaller) {
            Assert-NoReparseAncestor $taskInstaller.path
            if ((Test-ChildPath $taskInstaller.path $taskInstall) -or (Test-ChildPath $taskInstaller.path $taskOutput) -or
                (Test-ChildPath $taskInstaller.path $taskUpgradeInstall)) { throw 'Installer must be outside the installation/evidence directories.' }
        }
        $taskReport.preferencesBefore = @(Get-PreferencesSnapshot)
    }
    New-Item -ItemType Directory -Path $taskAppData -Force | Out-Null
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($taskProjectMarker)) -Force | Out-Null
    foreach ($taskPath in $taskAppMarker, $taskProjectMarker) {
        $taskBytes = [Text.UTF8Encoding]::new($false).GetBytes('MDA internal lifecycle retention marker ' + $taskRunId)
        $taskStream = [IO.File]::Open($taskPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try { $taskStream.Write($taskBytes, 0, $taskBytes.Length) } finally { $taskStream.Dispose() }
        $taskMarkerIdentities += Get-FileIdentity $taskPath
    }
    if ($VerifyClassicLicenseRejection) {
        $taskClassicInstaller = if ($UpgradeClassicComponent) { $taskReport.upgradeInstaller } else { $taskReport.baseInstaller }
        Invoke-LifecycleStep 'classic-license-rejection' { Test-ClassicLicenseRejection $taskClassicInstaller }
    }
    Invoke-LifecycleStep 'fresh-install' { Invoke-Install $taskReport.baseInstaller $false ([bool]$BaseClassicComponent) }
    Invoke-LifecycleStep 'version-upgrade' {
        if ($taskDirectoryChangeRequested) { $taskReport.upgradeDirectoryChange.beforeUpgrade = @(Get-InstallationTrees) }
        $script:taskInstall = $taskUpgradeInstall
        $taskReport.activeInstallDirectory = $taskInstall
        $taskActiveStep.installDirectory = $taskInstall
        try {
            Invoke-Install $taskReport.upgradeInstaller $true ([bool]$UpgradeClassicComponent) ([bool]$UpgradeProjectAssociations)
            $taskReport.upgradeDirectoryChange.targetDirectoryVerified = $taskDirectoryChangeRequested
        } finally {
            if ($taskDirectoryChangeRequested) {
                $taskReport.upgradeDirectoryChange.attempted = $null -ne $taskActiveStep.command -and $null -ne $taskActiveStep.command.pid
                $taskReport.upgradeDirectoryChange.afterUpgrade = @(Get-InstallationTrees)
                $taskReport.upgradeDirectoryChange.oldDirectoryFilesRemainAfterUpgrade = Test-InstalledFilesRemain $taskBaseInstall
            }
        }
    }
    Invoke-LifecycleStep 'uninstall' { Invoke-Uninstall $taskReport.upgradeInstaller.version }
    if (-not $SkipReinstall) {
        Invoke-LifecycleStep 'reinstall' { Invoke-Install $taskReport.upgradeInstaller $false ([bool]$UpgradeClassicComponent) ([bool]$UpgradeProjectAssociations) }
    }
    if ($RemoveReinstalledApplication) { Invoke-LifecycleStep 'final-uninstall' { Invoke-Uninstall $taskReport.upgradeInstaller.version } }
    $taskReport.success = $true
} catch {
    $taskReport.failure = $_.ToString()
    throw
} finally {
    $taskFinalFailures = [Collections.Generic.List[string]]::new()
    try {
        $taskReport.preferencesAfter = @(Get-PreferencesSnapshot)
        $null = @(Assert-PreferencesPreserved)
    } catch { $taskFinalFailures.Add($_.ToString()) }
    try {
        $taskReport.finalRegistrations = @(Get-MdaRegistrations)
    } catch { $taskFinalFailures.Add($_.ToString()) }
    try {
        $taskReport.finalInstallationTrees = @(Get-InstallationTrees)
    } catch { $taskFinalFailures.Add($_.ToString()) }
    try {
        $taskOwnedAppMarker = @($taskMarkerIdentities | Where-Object { $_.path -eq $taskAppMarker })
        if ($taskOwnedAppMarker.Count -eq 1 -and (Test-Path -LiteralPath $taskAppMarker -PathType Leaf)) {
            if ((Get-FileIdentity $taskAppMarker).sha256 -ne $taskOwnedAppMarker[0].sha256) { throw 'Own AppData marker changed; refusing to remove it.' }
            Remove-Item -LiteralPath $taskAppMarker -ErrorAction Stop
            $taskReport.appDataMarkerRemoved = -not (Test-Path -LiteralPath $taskAppMarker)
        }
    } catch { $taskFinalFailures.Add($_.ToString()) }
    if ($taskFinalFailures.Count -ne 0) {
        $taskReport.success = $false
        $taskReport.failure = (@($taskReport.failure) + @($taskFinalFailures) | Where-Object { $_ }) -join [Environment]::NewLine
        foreach ($taskFinalFailure in $taskFinalFailures) { Write-Warning $taskFinalFailure }
    }
    $taskReport.completedUtc = [DateTime]::UtcNow.ToString('O')
    Write-InstallationJson $taskReport (Join-Path $taskOutput 'installation-lifecycle-report.json')
}
if (-not $taskReport.success) { throw "Lifecycle verification failed; see $taskOutput/installation-lifecycle-report.json" }
Write-Output "PASS internal MDA installation lifecycle: $taskOutput"
if ($SkipApplicationStartup) { Write-Output 'Application startup and app-local CLR checks were skipped by request.' }
if ($SkipReinstall) { Write-Output 'Reinstall was skipped by request; the upgraded application was uninstalled.' }
if (-not $taskDirectoryChangeRequested) { Write-Output 'Upgrade directory relocation was not requested or verified.' }
Write-Output 'Formal visual/keyboard/clean-machine release acceptance remains separate.'
