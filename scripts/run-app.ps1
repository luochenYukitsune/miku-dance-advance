param(
    [switch]$LegacyPreview,
    [string]$Model,
    [string]$Project,
    [string]$Motion,
    [switch]$PlayMotion,
    [string]$ToonDirectory,
    [switch]$DemoRoom,
    [ValidateSet('legacy','fit')][string]$ShadowProjection = 'legacy',
    [ValidateSet('additive','toon')][string]$GiComposition = 'additive',
    [ValidateSet('off','spatial')][string]$GiDenoiser = 'off',
    [ValidateSet('off','smaa')][string]$Antialiasing = 'off',
    [ValidateRange(0,1)][double]$RoomEmission = 0
)
$ErrorActionPreference = 'Stop'
if ($LegacyPreview) {
    & (Join-Path $PSScriptRoot 'dotnet.ps1') -DotNetArguments @('run', '--project', 'src/Mmd.Modern.Demo', '-c', 'Release', '--', '--legacy-preview')
} else {
    & (Join-Path $PSScriptRoot 'run-frame-viewer.ps1') -Model $Model -Project $Project -Motion $Motion -PlayMotion:$PlayMotion -ToonDirectory $ToonDirectory -DemoRoom:$DemoRoom -ShadowProjection $ShadowProjection -GiComposition $GiComposition -GiDenoiser $GiDenoiser -Antialiasing $Antialiasing -RoomEmission $RoomEmission
}
