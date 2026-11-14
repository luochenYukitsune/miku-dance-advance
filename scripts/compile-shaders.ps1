param([string]$DxcPath = (Join-Path $PSScriptRoot '../../.dev-cache/modern-dotnet/dxc/bin/x64/dxc.exe'))
$ErrorActionPreference = 'Stop'
$shaderDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/Mmd.Modern.Rendering.Veldrid/Shaders'))
if (!(Test-Path -LiteralPath $DxcPath)) { throw 'Provide DXC 1.8.2505.1 via -DxcPath; see docs/dependencies.md.' }
foreach ($stage in @(@('vs_6_0','VSMain','model.vert.spv'), @('ps_6_0','PSMain','model.frag.spv'))) {
    & $DxcPath '-spirv' '-fspv-target-env=vulkan1.0' '-fvk-use-dx-layout' '-T' $stage[0] '-E' $stage[1] (Join-Path $shaderDirectory 'model.hlsl') '-Fo' (Join-Path $shaderDirectory $stage[2])
    if ($LASTEXITCODE -ne 0) { throw 'HLSL to SPIR-V compilation failed.' }
}
