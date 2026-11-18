using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Mmd.Modern.Rendering.D3D12;

public sealed partial class D3D12OriginalStyleRenderer
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CharacterOutlineVertex(Vector3 Expanded, Vector3 Position, Vector3 Normal, Vector2 Uv, Vector2 AdditionalUv);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CharacterOutlineConstants(Matrix4x4 WorldViewProjection, Vector4 Color, Vector4 Options);
    private readonly record struct CharacterOutlinePipelineKey(OriginalTextureSampling Sampling, OriginalTextureSampling ShadowSampling,
        Format Depth, OriginalAlphaBlendMode Blend, bool WriteDepth, bool Stencil, FrameAlphaOutput AlphaOutput, bool Simple, bool SubTexture,
        bool FrontCounterClockwise);
    private sealed record CharacterOutlinePipeline(ID3D12RootSignature Root, ID3D12PipelineState State);
    private readonly Dictionary<CharacterOutlinePipelineKey, CharacterOutlinePipeline> characterOutlinePipelines = [];
    private readonly Dictionary<bool, (byte[] Vertex, byte[] Pixel)> characterOutlineShaders = [];
    private SceneSnapshot? characterOutlineScene;
    private SceneAdditionalUvs? characterOutlineAdditionalUvs;
    private D3D12ExpandedPositions? characterOutlinePositions;
    private CharacterOutlineVertex[]? characterOutlineVertices;
    internal long CharacterOutlineStreamBuilds {get;private set;}

    /// <summary>Modern-only textured hull; borrows the active shared descriptors and target.
    /// No submission, wait, readback, source material changes or per-frame permanent resources.</summary>
    // 自动描边颜色：原有为“底色×0.55＋0.025”（zw 为 0 时保持不变）。四个画风家族的普通材质改为
    // 按画风压暗并提饱和，描边贴着底色走，避免灰黑硬线。z 为压暗系数，w 为饱和倍数。
    private static Vector4 OutlineOptions(CharacterOutlineStyle style, float scale, D3D12SurfaceStyleMap? surfaceStyles, int material)
    {
        if (style.Color is not null) return new(0, scale, 0, 0);
        var shading = surfaceStyles is not null && material < surfaceStyles.Count ? surfaceStyles.EffectiveStylizedShading(material) : null;
        return shading is null ? new(1, scale, 0, 0) : new(1, scale, shading.OutlineValue, shading.OutlineSaturation);
    }

    private int RecordCharacterOutline(OriginalFrameScene frame, int width, int height, ID3D12Resource sharedSurfaceBuffer, ID3D12Resource sharedIndexBuffer,
        ID3D12DescriptorHeap srvHeap, int srvStride, int slotBase, Format depthFormat,
        D3D12RecordingContext context, D3D12GpuCache cache, int firstMaterial = 0, int materialCount = int.MaxValue,
        D3D12SurfaceStyleMap? surfaceStyles = null)
    {
        using var outlineCpu=MeasureSceneCpu(SceneCpuStage.ModernOutline);
        if (!frame.Materials.Any(flags => flags.CharacterOutlineStyle is not null)) return 0;
        var scene = frame.Surface.Snapshot; var options = frame.Options;
        bool deferred = frame.Outline?.DeferredSurface is not null || scene.HasDeferredDeformation;
        var positions = deferred ? null : GetOutlinePositions(frame);
        var deferredMask = deferred ? GetDeferredMaterialMask(scene) : null;
        int vertexCount = frame.Outline!.VertexCount;
        ID3D12Resource? buffer = null;
        ID3D12Resource? scaleSource = null;
        var commands = context.Commands;
        var pipeline = GetCharacterOutlinePipeline(new(frame.Surface.Profile.TextureSampling,
            options?.SelfShadow?.Sampling ?? OriginalTextureSampling.LinearClamp, depthFormat,
            options?.BlendMode ?? OriginalAlphaBlendMode.Normal, options?.WriteDepth ?? true,
            options?.GroundShadow?.UseStencil == true, options?.AlphaOutput ?? FrameAlphaOutput.LegacyMax,
            options is null, HasSubTexture(frame), D3D12RasterWinding.FrontCounterClockwise(frame)));
        commands.SetGraphicsRootSignature(pipeline.Root); commands.SetPipelineState(pipeline.State);
        commands.SetDescriptorHeaps(srvHeap); commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.IASetIndexBuffer(new IndexBufferView(sharedIndexBuffer.GPUVirtualAddress, checked(scene.Indices.Length * 4), Format.R32_UInt));
        var camera = options?.CameraOverride ?? scene.Camera;
        var viewProjection = camera.ViewProjection(width, height); var wvp = scene.World * viewProjection;
        Matrix4x4.Invert(scene.World, out var inverse);
        var common = new FrameConstants(scene.World, Matrix4x4.Transpose(inverse), viewProjection,
            options?.SphereRotationOverride ?? frame.Surface.Profile.SphereRotation,
            options?.SelfShadow?.LightViewProjection ?? Matrix4x4.Identity,
            Vector4.One, Vector4.Zero, new(Vector3.Normalize(scene.LightDirection), 0), new(camera.Eye, 1),
            Vector4.One, Vector4.Zero, new(0, options?.SelfShadow?.PartialFar == true ? 1 : 0, options?.SelfShadow is null ? 0 : 1, 0), Vector4.Zero);
        int draws = 0;
        int endMaterial = (int)Math.Min(frame.Materials.Count, (long)firstMaterial + materialCount);
        bool needWidthSource = false;
        if (deferred)
            for (int k = firstMaterial; k < endMaterial; k++)
                if (frame.Materials[k].CharacterOutlineStyle is not null && frame.Materials[k].OutlineWidthScale != 1) { needWidthSource = true; break; }
        for (int i = firstMaterial; i < endMaterial; i++)
        {
            var flags = frame.Materials[i]; var style = flags.CharacterOutlineStyle;
            if (style is null) continue;
            var edge = CharacterOutlineMath.ResolveMaterial(frame.Outline!.Materials[i], style);
            var material = scene.Materials[i]; var binding = frame.Surface.Materials[i];
            float scale = flags.OutlineWidthScale;
            if (!edge.DrawEnabled || scale == 0 || material.IndexCount == 0
                || (deferred ? !ShouldDrawOutlineDeferred(frame, i, deferredMask!, wvp) : !ShouldDrawOutline(frame, i, positions!, wvp))) continue;
            if(buffer is null)
            {
                if (deferred)
                {
                    var gpuModern = cache.GpuSkinning.GetOutlineStreams(frame, sharedSurfaceBuffer, modern: true, needWidthSource: needWidthSource);
                    commands.SetPipelineState(pipeline.State);
                    buffer = gpuModern.Stream;
                    scaleSource = gpuModern.WidthSource;
                }
                else
                {
                    buffer=GetCharacterOutlineBuffer(frame,cache,positions!);
                }
                commands.IASetVertexBuffers(0,new VertexBufferView(buffer.GPUVirtualAddress,checked(vertexCount*52),52));
            }
            var source = scale == 1 ? sharedSurfaceBuffer : (scaleSource ??= D3D12OutlineDrawScale.SourceBuffer(frame.Outline!, cache));
            int sourceStride = scale == 1 ? 32 : 16;
            commands.IASetVertexBuffers(1, new VertexBufferView(source.GPUVirtualAddress, checked(vertexCount * sourceStride), sourceStride));
            var host = OriginalStyleMath.ResolveHostColors(material, frame.Surface.Profile);
            commands.SetGraphicsRootConstantBufferView(0, context.UploadConstants(common with {
                EgColor = host.EgColor, SpecularPower = host.SpecularPower, ToonShadowColor = SurfaceToon(binding),
                Options = new(material.AlphaCutoff, SurfaceMode(binding), material.DiffuseTexture >= 0 ? 1 : 0, options?.Transp == true ? 1 : 0) }));
            commands.SetGraphicsRootConstantBufferView(4, context.UploadConstants(new TextureMorphConstants(binding.TextureFactors)));
            commands.SetGraphicsRootConstantBufferView(5, context.UploadConstants(new CharacterOutlineConstants(wvp,
                style.Color ?? Vector4.One, OutlineOptions(style, scale, surfaceStyles, i))));
            commands.SetGraphicsRootDescriptorTable(1, srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase + (material.DiffuseTexture < 0 ? 0 : material.DiffuseTexture + 2), srvStride));
            commands.SetGraphicsRootDescriptorTable(2, srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase + (binding.SphereTexture < 0 ? 0 : binding.SphereTexture + 2), srvStride));
            int shadow = options is null ? slotBase : slotBase + scene.Textures.Count + 2 + (flags.ReceiveSelfShadow ? 1 : 0);
            commands.SetGraphicsRootDescriptorTable(3, srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(shadow, srvStride));
            commands.DrawIndexedInstanced(material.IndexCount, 1, material.StartIndex, 0, 0); draws++;
        }
        return draws;
    }

    private CharacterOutlinePipeline GetCharacterOutlinePipeline(CharacterOutlinePipelineKey key)
    {
        using var pipelineCpu=MeasureSceneCpu(SceneCpuStage.Pipelines);
        if (characterOutlinePipelines.TryGetValue(key, out var found)) return found;
        StaticSamplerDescription Sampler(OriginalTextureSampling sampling, int register) => new(ShaderVisibility.Pixel, register, 0) {
            Filter = sampling is OriginalTextureSampling.LinearClamp or OriginalTextureSampling.LinearWrap ? Filter.MinMagMipLinear : Filter.MinMagMipPoint,
            AddressU = sampling is OriginalTextureSampling.LinearWrap or OriginalTextureSampling.PointWrap ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
            AddressV = sampling is OriginalTextureSampling.LinearWrap or OriginalTextureSampling.PointWrap ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp };
        RootParameter[] parameters = [new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)), ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 1)), ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 2)), ShaderVisibility.Pixel),
            new(RootParameterType.ConstantBufferView, new RootDescriptor(3, 0), ShaderVisibility.Pixel),
            new(RootParameterType.ConstantBufferView, new RootDescriptor(4, 0), ShaderVisibility.All)];
        var root = device.CreateRootSignature<ID3D12RootSignature>(new RootSignatureDescription(
            RootSignatureFlags.AllowInputAssemblerInputLayout, parameters, [Sampler(key.Sampling, 0), Sampler(key.ShadowSampling, 1)]), RootSignatureVersion.Version10);
        try
        {
            if (!characterOutlineShaders.TryGetValue(key.SubTexture, out var shader))
            {
                shader = OriginalFrameShaders.CompileCharacterOutline(key.SubTexture);
                characterOutlineShaders.Add(key.SubTexture, shader); runtime.ShaderCompilations += 2;
            }
            var raster = RasterizerDescription.CullNone; raster.CullMode = CullMode.Front; raster.FrontCounterClockwise = key.FrontCounterClockwise;
            raster = CharacterOutlineRasterization.Biased(raster);
            var depth = DepthStencilDescription.Default;
            depth.DepthFunc = key.Simple ? ComparisonFunction.Less : ComparisonFunction.LessEqual;
            depth.DepthWriteMask = key.WriteDepth ? DepthWriteMask.All : DepthWriteMask.Zero;
            if (key.Stencil)
            {
                depth.StencilEnable = true; depth.StencilReadMask = 255; depth.StencilWriteMask = 255;
                depth.FrontFace.StencilFunc = ComparisonFunction.Always; depth.FrontFace.StencilPassOp = StencilOperation.Replace;
                depth.BackFace = depth.FrontFace;
            }
            var state = device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
                RootSignature = root, VertexShader = shader.Vertex, PixelShader = shader.Pixel,
                InputLayout = new InputLayoutDescription([
                    new("POSITION", 0, Format.R32G32B32_Float, 0, 0), new("POSITION", 1, Format.R32G32B32_Float, 12, 0),
                    new("NORMAL", 0, Format.R32G32B32_Float, 24, 0), new("TEXCOORD", 0, Format.R32G32_Float, 36, 0),
                    new("TEXCOORD", 1, Format.R32G32_Float, 44, 0), new("TEXCOORD", 2, Format.R32G32B32A32_Float, 0, 1)]),
                BlendState = key.Simple ? BlendDescription.Opaque : FrameBlend(key.Blend, key.AlphaOutput), SampleMask = uint.MaxValue,
                RasterizerState = raster, DepthStencilState = depth, PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = [key.AlphaOutput == FrameAlphaOutput.Straight ? Format.R16G16B16A16_Float : Format.R8G8B8A8_UNorm],
                DepthStencilFormat = key.Depth, SampleDescription = new(1, 0) });
            runtime.PipelineCreations++;
            var result = new CharacterOutlinePipeline(root, state); characterOutlinePipelines.Add(key, result); return result;
        }
        catch { root.Dispose(); throw; }
    }

    private void DisposeCharacterOutlinePipelines()
    {
        foreach (var value in characterOutlinePipelines.Values) { value.State.Dispose(); value.Root.Dispose(); }
        characterOutlinePipelines.Clear(); characterOutlineShaders.Clear();
        characterOutlineScene = null; characterOutlineAdditionalUvs=null;characterOutlinePositions = null; characterOutlineVertices = null;
    }
}
