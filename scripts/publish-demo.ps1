param([string]$Output,[string]$CacheRoot)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspaceRoot=[IO.Path]::GetFullPath((Join-Path $projectRoot '..'))
$demoOutput=if($Output){[IO.Path]::GetFullPath($Output)}else{Join-Path $workspaceRoot 'deliverables/EngineDemo'}
$demoCache=if($CacheRoot){[IO.Path]::GetFullPath($CacheRoot)}else{[IO.Path]::GetFullPath((Join-Path $projectRoot '../.dev-cache/native-lights-20261019/root'))}
New-Item -ItemType Directory -Force -Path $demoOutput | Out-Null
& (Join-Path $PSScriptRoot 'dotnet.ps1') -DotNetArguments @('publish','src/Mmd.Modern.Demo','-c','Release',"-p:ModernCacheRoot=$demoCache",'--self-contained','false','-o',$demoOutput)
if($LASTEXITCODE -ne 0){throw 'Demo publish failed.'}
$compilerDestination=Join-Path $demoOutput 'tools/dxc'
New-Item -ItemType Directory -Force -Path $compilerDestination|Out-Null
$compilerManifest=Get-Content -LiteralPath (Join-Path $projectRoot 'tools/dxc/manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json
$compilerRoot=@((Join-Path $workspaceRoot '.dev-cache/modern-dotnet/dxc'),(Join-Path $workspaceRoot '.dev-cache/ray-shader-toolchain-20261018/tools/dxc/v1.8.2505.1'))|Where-Object{Test-Path -LiteralPath (Join-Path $_ 'bin/x64/dxc.exe')}|Select-Object -First 1
if(-not $compilerRoot){throw 'Pinned DXC cache is missing. Run scripts/setup-dxc.ps1.'}
foreach($entry in $compilerManifest.files){
 $source=if($entry.path.StartsWith('bin/')){Join-Path $compilerRoot $entry.path}else{Join-Path $projectRoot ('tools/dxc/licenses/'+$entry.path)}
 if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256){throw "Pinned compiler hash mismatch: $($entry.path)"}
 Copy-Item -LiteralPath $source -Destination (Join-Path $compilerDestination ([IO.Path]::GetFileName($entry.path)))
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'tools/dxc/manifest.json') -Destination (Join-Path $compilerDestination 'upstream-manifest.json')
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $demoOutput 'THIRD_PARTY_NOTICES.md')
$demoReadme=@'
MMD Modern Engine Demo

Double-click Mmd.Modern.Demo.exe. Windows x64 and .NET Runtime 10 are required.
This machine already has .NET 10. Keep all files in this folder together.

Original built-in room; no user model or original MMD assets are included.
Open PMX: choose a local model and its textures. Return to the built-in room at any time.
Left drag: orbit camera. Middle/right drag on empty space: pan. Mouse wheel: zoom.
Settings: ambient/specular strength, outline width, opacity, shadow switches and SMAA.
GI settings: original lighting (off), GPU software BVH or hardware DXR, strength, samples and spatial denoising.
Opacity below 1 disables GI because partial transmission is not implemented.
Ray-tracing showcase: resets and freezes physics; DXR if available, GPU BVH fallback.
Physics: real Bepu falling bodies and spring constraints; GI is off while moving.
Optional light inspector: select a light; left drag marker/axis, right drag marker to rotate.
Screenshots exclude editor gizmos. Local lights currently affect direct toon lighting/shadows, not GI.
No DLSS, FSR, XeSS or fully compatible Bullet simulation is claimed.

Build: scripts/publish-demo.ps1. Source and verification remain in the project.
'@
[IO.File]::WriteAllText((Join-Path $demoOutput 'README.txt'),$demoReadme,[Text.UTF8Encoding]::new($false))
Get-ChildItem -LiteralPath $demoOutput -Recurse -File|Where-Object{$_.Name -ne 'manifest.json'}|ForEach-Object{
 [pscustomobject]@{path=$_.FullName.Substring($demoOutput.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $demoOutput 'manifest.json') -Encoding UTF8
Write-Output (Join-Path $demoOutput 'Mmd.Modern.Demo.exe')
