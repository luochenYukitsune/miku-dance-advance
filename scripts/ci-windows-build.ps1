param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$WorkDirectory,
    [string]$Version = '',
    [string]$CommitSha = ''
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Build output must be new.' }
New-Item -ItemType Directory -Force -Path $WorkDirectory | Out-Null
$work = [IO.Path]::GetFullPath($WorkDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$normalizedVersion = ''
$publishVersionArgs = @()
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $normalizedVersion = $Version.Trim().TrimStart('v', 'V')
    if ($normalizedVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
        throw "Version 格式非法：$Version"
    }
    $shortSha = $CommitSha.Trim()
    if ($shortSha.Length -gt 12) { $shortSha = $shortSha.Substring(0, 12) }
    $infoVersion = if ($shortSha) { "$normalizedVersion+$shortSha" } else { $normalizedVersion }
    $publishVersionArgs = @(
        "-p:MdaAppVersion=$normalizedVersion",
        "-p:MdaAppInformationalVersion=$infoVersion"
    )
}
python -m pip install ziglang==0.16.0
if ($LASTEXITCODE) { throw 'Zig installation failed.' }
$zig = python -c "import pathlib,ziglang; print(pathlib.Path(ziglang.__file__).parent/'zig.exe')"
python scripts/build-bullet.py --compiler $zig --output "$work/native/rigid/mmd_bullet.dll"
if ($LASTEXITCODE) { throw 'Bullet build failed.' }
python scripts/build-bullet-soft.py --compiler $zig --output "$work/native/soft/mmd_bullet_soft.dll"
if ($LASTEXITCODE) { throw 'Soft-body build failed.' }
./native/Mmd.Modern.Mikktspace/build.ps1 -UseCMake -OutputDirectory "$work/native/mikk" -BuildDirectory "$work/mikk-build"
dotnet publish src/Mmd.Modern.App -c Release -r win-x64 --self-contained true -o $output -p:PublishReadyToRun=false -p:PublishSingleFile=false "-p:BulletNativeLibrary=$work/native/rigid/mmd_bullet.dll" "-p:SoftBodyNativeLibrary=$work/native/soft/mmd_bullet_soft.dll" "-p:MikktspaceNativeLibrary=$work/native/mikk/mmd_mikktspace.dll" @publishVersionArgs
if ($LASTEXITCODE) { throw 'Windows publish failed.' }
& (Join-Path $PSScriptRoot 'verify-bundled-plugins.ps1') -AppDirectory $output -WorkDirectory "$work/plugin-probe" -ExpectedVersion $normalizedVersion
./scripts/fetch-ffmpeg.ps1 -Destination "$work/ffmpeg"
New-Item -ItemType Directory -Path "$output/tools/ffmpeg","$output/licenses/FFmpeg" -Force | Out-Null
Copy-Item "$work/ffmpeg/bin/*" "$output/tools/ffmpeg"
Copy-Item "$work/ffmpeg/LICENSE","$work/ffmpeg/manifest.json" "$output/licenses/FFmpeg"
$pin = Get-Content tools/dxc/manifest.json -Raw | ConvertFrom-Json
$dxcZip = "$work/dxc.zip"
for ($attempt = 1; ; $attempt++) {
    try {
        Invoke-WebRequest $pin.assetUrl -OutFile $dxcZip -UseBasicParsing
        break
    } catch {
        Remove-Item -LiteralPath $dxcZip -Force -ErrorAction SilentlyContinue
        if ($attempt -ge 3) { throw }
        Write-Warning "下载 DXC 失败（第 $attempt 次），$(5 * $attempt) 秒后重试：$($_.Exception.Message)"
        Start-Sleep -Seconds (5 * $attempt)
    }
}
if ((Get-FileHash $dxcZip).Hash -ine $pin.assetSha256) { throw 'DXC archive hash mismatch' }
Expand-Archive $dxcZip "$work/dxc"
New-Item -ItemType Directory "$output/tools/dxc" -Force | Out-Null
foreach ($file in $pin.files) {
    $path = Join-Path "$work/dxc" $file.path
    if ((Get-FileHash $path).Hash -ine $file.sha256) { throw "DXC file hash mismatch: $($file.path)" }
    Copy-Item $path "$output/tools/dxc"
}
# The SDK copies a synthetic fixture by default; development packages don't need it.
$fixturePath = Join-Path $output 'fixtures'
if (Test-Path -LiteralPath $fixturePath) { Remove-Item -LiteralPath $fixturePath -Recurse -Force }
Copy-Item tools/ffmpeg/README.md "$output/licenses/FFmpeg/README.md"
