using Mmd.Modern.Core;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Mmd.Modern.Rendering.D3D12;

public sealed partial class D3D12OriginalStyleRenderer
{
    private static readonly CharacterToonResponse PluginStylizedDefaultResponse = new();

    private sealed record PluginSurfacePipelines(ID3D12PipelineState Back, ID3D12PipelineState Both);

    private readonly record struct PluginSurfacePipelineKey(
        string StyleId,
        string ModuleId,
        string ContentHash,
        D3D12StyleShaderHookMask EffectiveHooks,
        D3D12StyleGeometryRequirements EffectiveGeometry,
        bool CharacterToon,
        bool PreserveDirectLightPalette,
        FramePipelineKey Frame);

    private readonly Dictionary<PluginSurfacePipelineKey, PluginSurfacePipelines> pluginSurfacePipelines = [];

    private readonly Dictionary<(string StyleId, string ModuleId, string ContentHash, D3D12StyleShaderHookMask Hooks, D3D12StyleGeometryRequirements Geom, string Entry, bool Native, bool Gi, bool Morph, bool Sub, bool Toon, bool DirectPalette, bool Clustered), byte[]> pluginSurfacePixelShaders = [];
    private readonly Dictionary<(D3D12StyleGeometryRequirements Geom, bool Native, bool Gi, bool Sub), byte[]> pluginSurfaceVertexShaders = [];

    private byte[] GetPluginSurfaceVertexShader(
        D3D12PluginShaderModule module,
        D3D12StyleGeometryRequirements effectiveGeometry,
        FramePipelineKey key)
    {
        var maskedGeom = effectiveGeometry & (D3D12StyleGeometryRequirements.SubTextureUv
            | D3D12StyleGeometryRequirements.RawRadialSeeds
            | D3D12StyleGeometryRequirements.ModelSpacePosition
            | D3D12StyleGeometryRequirements.ModelSpaceNormal);
        var variant = (maskedGeom, key.NativeLights, key.Gi, key.SubTexture);
        if (pluginSurfaceVertexShaders.TryGetValue(variant, out var found)) return found;
        var compiled = D3D12StyleShaderAssembler.CompileVertex(module, effectiveGeometry, key.NativeLights, key.Gi, key.SubTexture);
        runtime.ShaderCompilations++;
        pluginSurfaceVertexShaders.Add(variant, compiled);
        return compiled;
    }

    private byte[] GetPluginSurfacePixelShader(
        string styleId,
        string entry,
        D3D12PluginShaderModule module,
        D3D12StyleShaderHookMask effectiveHooks,
        D3D12StyleGeometryRequirements effectiveGeometry,
        FramePipelineKey key,
        bool characterToon,
        bool preserveDirectLightPalette)
    {
        var shaderKey = (styleId, module.ModuleId, module.EffectiveContentHash, effectiveHooks, effectiveGeometry,
            entry, key.NativeLights, key.Gi, key.TextureMorph, key.SubTexture, characterToon, preserveDirectLightPalette, key.ClusteredLights);
        if (pluginSurfacePixelShaders.TryGetValue(shaderKey, out var found)) return found;
        var compiled = D3D12StyleShaderAssembler.CompilePixel(
            styleId, entry, module, effectiveHooks, effectiveGeometry,
            key.NativeLights, key.Gi, key.TextureMorph, key.SubTexture,
            characterToon, preserveDirectLightPalette, key.ClusteredLights);
        runtime.ShaderCompilations++;
        pluginSurfacePixelShaders.Add(shaderKey, compiled);
        return compiled;
    }

    private PluginSurfacePipelines GetPluginSurfacePipelines(
        FramePipelines common,
        string styleId,
        D3D12PluginShaderModule module,
        D3D12StyleShaderHookMask effectiveHooks,
        D3D12StyleGeometryRequirements effectiveGeometry,
        bool characterToon,
        bool preserveDirectLightPalette)
    {
        var key = common.Key;
        if (key.SubTexture) effectiveGeometry |= D3D12StyleGeometryRequirements.SubTextureUv;
        var cacheKey = new PluginSurfacePipelineKey(
            styleId,
            module.ModuleId,
            module.EffectiveContentHash,
            effectiveHooks,
            effectiveGeometry,
            characterToon,
            preserveDirectLightPalette,
            key);
        if (pluginSurfacePipelines.TryGetValue(cacheKey, out var found)) return found;

        bool hasSubTexture = key.SubTexture || effectiveGeometry.HasFlag(D3D12StyleGeometryRequirements.SubTextureUv);
        bool hasRadialSeeds = effectiveGeometry.HasFlag(D3D12StyleGeometryRequirements.RawRadialSeeds);
        bool hasFrontFace = effectiveGeometry.HasFlag(D3D12StyleGeometryRequirements.FrontFace);

        var both = RasterizerDescription.CullNone;
        both.MultisampleEnable = key.SampleCount > 1;
        if (hasFrontFace) both.FrontCounterClockwise = key.FrontCounterClockwise;
        var back = both;
        back.CullMode = CullMode.Back;
        back.FrontCounterClockwise = key.FrontCounterClockwise;

        var depth = DepthStencilDescription.Default;
        depth.DepthFunc = ComparisonFunction.LessEqual;
        depth.DepthWriteMask = key.WriteDepth ? DepthWriteMask.All : DepthWriteMask.Zero;
        if (key.Stencil)
        {
            depth.StencilEnable = true;
            depth.StencilReadMask = 255;
            depth.StencilWriteMask = 255;
            depth.FrontFace.StencilFunc = ComparisonFunction.Always;
            depth.FrontFace.StencilPassOp = StencilOperation.Replace;
            depth.BackFace = depth.FrontFace;
        }

        string entry = key.NativeLights
            ? (key.Gi ? "PSSurfaceGiNative" : "PSSurfaceNative")
            : (key.Gi ? "PSSurfaceGi" : "PSSurface");

        var vertex = GetPluginSurfaceVertexShader(module, effectiveGeometry, key);
        var pixel = GetPluginSurfacePixelShader(styleId, entry, module, effectiveHooks, effectiveGeometry, key, characterToon, preserveDirectLightPalette);

        InputElementDescription[] layoutElements = SurfaceLayout(hasSubTexture);
        if (hasRadialSeeds)
            layoutElements = [.. layoutElements, new InputElementDescription("RAW_RADIAL", 0, Format.R32G32B32_Float, 0, 2)];

        ID3D12PipelineState Create(RasterizerDescription raster)
        {
            runtime.PipelineCreations++;
            return runtime.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
            {
                RootSignature = common.Root,
                VertexShader = vertex,
                PixelShader = pixel,
                InputLayout = new InputLayoutDescription(layoutElements),
                BlendState = FrameBlend(key.Blend, key.AlphaOutput),
                SampleMask = uint.MaxValue,
                RasterizerState = raster,
                DepthStencilState = depth,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = [key.AlphaOutput == FrameAlphaOutput.Straight ? Format.R16G16B16A16_Float : Format.R8G8B8A8_UNorm],
                DepthStencilFormat = key.Depth,
                SampleDescription = new(key.SampleCount, 0),
            });
        }

        var surfaceBack = Create(back);
        try
        {
            var result = new PluginSurfacePipelines(surfaceBack, Create(both));
            pluginSurfacePipelines.Add(cacheKey, result);
            return result;
        }
        catch
        {
            surfaceBack.Dispose();
            throw;
        }
    }

    private void DisposePluginSurfacePipelines()
    {
        foreach (var pipeline in pluginSurfacePipelines.Values)
        {
            pipeline.Both.Dispose();
            pipeline.Back.Dispose();
        }
        pluginSurfacePipelines.Clear();
        pluginSurfacePixelShaders.Clear();
        pluginSurfaceVertexShaders.Clear();
    }
}
