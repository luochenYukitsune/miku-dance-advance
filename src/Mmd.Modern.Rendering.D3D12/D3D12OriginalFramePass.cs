using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Mmd.Modern.Core;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
namespace Mmd.Modern.Rendering.D3D12;

public sealed partial class D3D12OriginalStyleRenderer
{
    private readonly Dictionary<FramePipelineKey, FramePipelines> framePipelines = new();

    private readonly record struct FramePipelineKey(
        OriginalTextureSampling SurfaceSampling,
        OriginalTextureSampling ShadowSampling,
        Format Depth,
        OriginalAlphaBlendMode Blend,
        bool WriteDepth,
        bool GroundBlend,
        bool Stencil,
        OriginalDepthComparison ShadowComparison,
        bool Gi = false,
        bool NativeLights = false,
        bool TextureMorph = false,
        bool SubTexture = false,
        FrameAlphaOutput AlphaOutput = FrameAlphaOutput.LegacyMax,
        bool CharacterToon = false,
        bool HasPluginSpace2 = false,
        bool FrontCounterClockwise = true,
        bool CharacterDirectPalette = false);

    private sealed record FramePipelines(
        ID3D12RootSignature Root,
        ID3D12PipelineState SurfaceBack,
        ID3D12PipelineState SurfaceBoth,
        ID3D12PipelineState ShadowBack,
        ID3D12PipelineState ShadowBoth,
        ID3D12PipelineState Ground,
        OutlinePipeline Outline,
        ID3D12PipelineState? NativeShadowBack = null,
        ID3D12PipelineState? NativeShadowBoth = null,
        int TextureMorphRoot = -1,
        int PluginCbvRoot = -1,
        int PluginSrvTableRoot = -1,
        int PluginStructuredRoot = -1,
        FramePipelineKey Key = default);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FrameConstants(
        Matrix4x4 World,
        Matrix4x4 NormalWorld,
        Matrix4x4 ViewProjection,
        Matrix4x4 SphereRotation,
        Matrix4x4 LightViewProjection,
        Vector4 EgColor,
        Vector4 SpecularPower,
        Vector4 IncomingLight,
        Vector4 EyePosition,
        Vector4 ToonShadowColor,
        Vector4 Options,
        Vector4 ShadowOptions,
        Vector4 GroundDirection,
        Vector4 PmxMaterialTreatmentParams = default,
        Vector4 StylizedShadeParams = default,
        Vector4 StylizedTintParams = default,
        Vector4 StylizedWarmParams = default,
        Vector4 StylizedGradeParams = default,
        Vector4 ExtraSurfaceParams = default);

    internal static int[]? ComputePluginTextureTableSlots(
        D3D12SurfaceStyleMap? surfaceStyles,
        int pluginBaseSlot,
        out int totalPluginDescriptors)
    {
        if (surfaceStyles is null || !surfaceStyles.HasAnyPluginSpace2)
        {
            totalPluginDescriptors = 0;
            return null;
        }
        int dummySlot = pluginBaseSlot;
        int nextSlot = checked(pluginBaseSlot + D3D12PluginResourceLimits.MaxTextureSlots);
        var slots = new int[surfaceStyles.Count];
        for (int i = 0; i < surfaceStyles.Count; i++)
        {
            if (surfaceStyles.GetPluginDraw(i)?.TextureBindings is { Count: > 0 })
            {
                slots[i] = nextSlot;
                nextSlot = checked(nextSlot + D3D12PluginResourceLimits.MaxTextureSlots);
            }
            else
            {
                slots[i] = dummySlot;
            }
        }
        totalPluginDescriptors = nextSlot - pluginBaseSlot;
        return slots;
    }

    internal sealed record PreparedFrame(ID3D12Resource White, ID3D12Resource Shadow, D3D12GpuCache Cache)
    {
        internal PreparedNativeLights? Native { get; set; }
    }

    // The caller retains every resource through the recording context's fence. This
    // preparation records the current primary map once, before mode3 GI receivers,
    // and the ordinary colour recorder reuses exactly the same pair (including SSAA).
    private int PreparePrimaryShadow(D3D12RecordingContext context, FramePipelines pipeline, OriginalFrameScene frame,
        ID3D12Resource vertexBuffer, ID3D12Resource indexBuffer, FrameConstants common, D3D12GpuCache cache,
        ref PreparedFrame? prepared)
    {
        using var shadowCpu = MeasureSceneCpu(SceneCpuStage.PrimaryShadow);
        if (prepared is not null) return 0;
        BeginVisibility(frame);
        var commands = context.Commands; var scene = frame.Surface.Snapshot; var options = frame.Options!; int draws = 0;
        var white = D3D12ShadowTargets.White(context, cache);
        ID3D12Resource shadow = white;
        if (options.SelfShadow is { Generate: true } s)
        {
            var targets = D3D12ShadowTargets.Get(context, cache, "original-primary-shadow", s.Width, s.Height, Format.D24_UNorm_S8_UInt);
            shadow = targets.Color; targets.Begin(context);
            commands.RSSetViewport(0, 0, s.Width, s.Height, 0, 1); commands.RSSetScissorRect(new RawRect(0, 0, s.Width, s.Height));
            commands.SetGraphicsRootSignature(pipeline.Root); commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            commands.IASetVertexBuffers(0, new VertexBufferView(vertexBuffer.GPUVirtualAddress, checked(scene.VertexCount * 32), 32));
            commands.IASetIndexBuffer(new IndexBufferView(indexBuffer.GPUVirtualAddress, checked(scene.Indices.Length * 4), Format.R32_UInt));
            commands.SetGraphicsRootConstantBufferView(0, context.UploadConstants(common));
            for (int i = 0; i < scene.Materials.Count; i++)
            {
                var material = scene.Materials[i]; var flags = frame.Materials[i];
                if (!flags.CastSelfShadow || material.IndexCount == 0 || material.Diffuse.W == .98f) continue;
                bool shadowBoth = flags.ShadowDoubleSided || (flags.UseSceneLighting && frame.Surface.Materials[i].DoubleSided);
                commands.SetPipelineState(shadowBoth ? pipeline.ShadowBoth : pipeline.ShadowBack);
                commands.DrawIndexedInstanced(material.IndexCount, 1, material.StartIndex, 0, 0); draws++; shadowDrawn++;
            }
            targets.End(context);
        }
        prepared = new(white, shadow, cache);
        return draws;
    }

    internal int PrepareGiFrame(D3D12RecordingContext context, OriginalFrameScene frame, Format depthFormat,
        ID3D12Resource vb, ID3D12Resource ib, D3D12GpuCache cache, ref PreparedFrame? prepared)
    {
        if (prepared is not null) return 0;
        var scene = frame.Surface.Snapshot; var options = frame.Options!; var camera = options.CameraOverride ?? scene.Camera;
        Matrix4x4.Invert(scene.World, out var inverse);
        var common = new FrameConstants(scene.World, Matrix4x4.Transpose(inverse), Matrix4x4.Identity,
            options.SphereRotationOverride ?? frame.Surface.Profile.SphereRotation,
            options.SelfShadow?.LightViewProjection ?? Matrix4x4.Identity, Vector4.One, Vector4.Zero,
            new(Vector3.Normalize(scene.LightDirection), 0), new(camera.Eye, 1), Vector4.One, Vector4.Zero,
            new(0, options.SelfShadow?.PartialFar == true ? 1 : 0, options.SelfShadow is null ? 0 : 1, 0), Vector4.Zero);
        return PreparePrimaryShadow(context, GetFramePipelines(frame, depthFormat, true), frame, vb, ib, common, cache, ref prepared);
    }

    /// <summary>Borrowed recorder. Does not clear caller targets, reset/close lists, submit, wait, or read back.
    /// Caller owns shared heap, targets and all retained resources until its fence completes.</summary>
    private int RecordFrame(ID3D12Device device, ID3D12GraphicsCommandList commands, FramePipelines pipeline, OriginalFrameScene frame,
        int width, int height, ID3D12Resource vertexBuffer, ID3D12Resource indexBuffer, ID3D12DescriptorHeap srvHeap, int srvStride,
        CpuDescriptorHandle rtv, CpuDescriptorHandle dsv, Format depthFormat, FrameBufferUpload upload, Func<ID3D12Resource, ID3D12Resource> own, Action<IDisposable> retain, ref PreparedFrame? prepared, int slotBase, D3D12RecordingContext context, D3D12GpuCache cache, D3D12RayGiRenderer.Prepared? gi = null, D3D12SurfaceStyleMap? surfaceStyles = null)
    {
        var options = frame.Options!; var style = frame.Surface; var scene = style.Snapshot;
        if (options.GroundShadow is { UseStencil: true } && depthFormat != Format.D24_UNorm_S8_UInt) throw new ArgumentException("Stencil ground requires D24S8 target.");
        var camera = options.CameraOverride ?? scene.Camera;
        Matrix4x4.Invert(scene.World, out var inverse);
        var common = new FrameConstants(scene.World, Matrix4x4.Transpose(inverse), camera.ViewProjection(width, height), options.SphereRotationOverride ?? style.Profile.SphereRotation,
            options.SelfShadow?.LightViewProjection ?? Matrix4x4.Identity, Vector4.One, Vector4.Zero, new(Vector3.Normalize(scene.LightDirection), 0), new(camera.Eye, 1),
            Vector4.One, Vector4.Zero, new(0, options.SelfShadow?.PartialFar == true ? 1 : 0, options.SelfShadow is null ? 0 : 1, 0), new(options.GroundShadow?.ProjectionDirection ?? Vector3.UnitY, 0));
        void Constants(FrameConstants value)
        {
            commands.SetGraphicsRootConstantBufferView(0, context.UploadConstants(value));
        }
        void Geometry(ID3D12Resource vb, int stride, int count)
        {
            commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            commands.IASetVertexBuffers(0, new VertexBufferView(vb.GPUVirtualAddress, checked(count * stride), stride));
            commands.IASetIndexBuffer(new IndexBufferView(indexBuffer.GPUVirtualAddress, checked(scene.Indices.Length * 4), Format.R32_UInt));
        }
        int draws = 0, whiteSlot = slotBase + scene.Textures.Count + 2, realSlot = whiteSlot + 1;
        draws += PreparePrimaryShadow(context, pipeline, frame, vertexBuffer, indexBuffer, common, cache, ref prepared);
        var ready = prepared!;
        device.CreateShaderResourceView(ready.White, null, srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(whiteSlot, srvStride));
        device.CreateShaderResourceView(ready.Shadow, null, srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(realSlot, srvStride));
        bool nativeLights = HasNativeLights(frame);
        int nativeRoot = gi is null ? 4 : 7;
        if (nativeLights)
        {
            using var nativeCpu = MeasureSceneCpu(SceneCpuStage.NativeLights);
            bool firstPreparation = ready.Native is null;
            ready.Native ??= RecordNativeLights(context, cache, pipeline, frame, vertexBuffer, indexBuffer, srvHeap, srvStride, slotBase, ready.White, common);
            SetNativeGiParticipation(gi is not null);
            if (firstPreparation) draws += LastNativeLightStatistics?.ShadowDrawCalls ?? 0;
            device.CreateShaderResourceView(ready.Native.Atlas, null, srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(realSlot + 1, srvStride));
            device.CreateConstantBufferView(new ConstantBufferViewDescription
            {
                BufferLocation = ready.Native.FrameAddress,
                SizeInBytes = (Marshal.SizeOf<NativeFrameConstants>() + 255) & ~255
            }, srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(realSlot + 2, srvStride));
        }
        commands.OMSetRenderTargets(rtv, dsv); commands.RSSetViewport(0, 0, width, height, 0, 1); commands.RSSetScissorRect(new RawRect(0, 0, width, height));
        commands.SetGraphicsRootSignature(pipeline.Root); commands.SetDescriptorHeaps(srvHeap);
        if (options.GroundShadow is { } ground && ground.ProjectionDirection.Y < 0)
        {
            using var groundCpu = MeasureSceneCpu(SceneCpuStage.Ground);
            if (frame.Outline is null || frame.OutlineOptions is null) throw new ArgumentException("Ground shadow requires the independent edge position stream and explicit outline geometry inputs.");
            ID3D12Resource groundVb;
            int groundCount;
            if (frame.Outline.DeferredSurface is not null || scene.HasDeferredDeformation)
            {
                groundVb = cache.GpuSkinning.GetOutlineStreams(frame, vertexBuffer, modern: false, needWidthSource: false).Stream;
                groundCount = frame.Outline.VertexCount;
            }
            else
            {
                var positions = GetEdgePositions(frame);
                groundVb = upload(positions);
                groundCount = positions.Length;
            }
            Geometry(groundVb, 16, groundCount); commands.SetPipelineState(pipeline.Ground); commands.OMSetStencilRef(2); Constants(common with { EgColor = ground.Color });
            for (int i = 0; i < scene.Materials.Count; i++)
            {
                var material = scene.Materials[i]; if (!frame.Materials[i].CastGroundShadow || material.IndexCount == 0) continue;
                commands.DrawIndexedInstanced(material.IndexCount, 1, material.StartIndex, 0, 0); draws++; groundDrawn++;
            }
        }
        commands.OMSetStencilRef(1); Geometry(vertexBuffer, 32, scene.VertexCount);
        if (gi is not null)
        {
            commands.SetGraphicsRootShaderResourceView(5, gi.Indirect.GPUVirtualAddress);
            commands.SetGraphicsRootDescriptorTable(6, gi.Inputs);
        }
        if (nativeLights)
        {
            commands.SetGraphicsRootDescriptorTable(nativeRoot + 1, srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(realSlot + 1, srvStride));
        }

        int pluginBaseSlot = slotBase + scene.Textures.Count + 4 + (nativeLights ? 2 : 0);
        int[]? pluginSlots = ComputePluginTextureTableSlots(surfaceStyles, pluginBaseSlot, out _);
        ulong dummyPluginCbv = 0;
        ulong dummyStructuredBuffer = 0;
        Span<byte> pluginCbvStaging = pipeline.PluginCbvRoot >= 0
            ? stackalloc byte[D3D12PluginResourceLimits.MaxConstantBufferBytes]
            : default;
        if (pipeline.PluginCbvRoot >= 0)
        {
            pluginCbvStaging[..256].Clear();
            dummyPluginCbv = context.UploadConstants(pluginCbvStaging[..256]);
            dummyStructuredBuffer = cache.BufferByIdentity("plugin-dummy-structured-v1", new byte[256], ResourceStates.PixelShaderResource, "plugin-dummy-structured-v1").GPUVirtualAddress;
        }
        bool hasFrameSubTexture = HasSubTexture(frame);
        bool hasPrimaryLight = HasPrimaryLight(style.Profile);

        using (var surfaceCpu = MeasureSceneCpu(SceneCpuStage.SurfaceLoop))
        {
            foreach (int i in SurfaceDrawOrderPlanner.Plan(frame))
            {
                var material = scene.Materials[i]; var binding = style.Materials[i]; var flags = frame.Materials[i];
                if (!flags.SurfaceEnabled || material.IndexCount == 0) continue;
                if (!ShouldDrawSurface(frame, i, common.ViewProjection)) continue;
                var host = OriginalStyleMath.ResolveHostColors(material, style.Profile, flags.UseSceneLighting);
                bool both = binding.DoubleSided || (flags.CullTransparentSurface && host.EgColor.W < 1);
                bool isPluginStyle = surfaceStyles is not null && !surfaceStyles.IsDefaultStyle(i);
                var drawData = isPluginStyle ? surfaceStyles!.GetPluginDraw(i) : null;
                var response = flags.CharacterToonResponse;
                if (isPluginStyle)
                {
                    var module = surfaceStyles!.ResolveShaderModule(i)!;
                    var effectiveHooks = surfaceStyles.EffectiveHookMask(i);
                    var effectiveGeom = D3D12StyleShaderAssembler.ResolveEffectiveGeometry(module, hasFrameSubTexture);
                    var pluginReg = surfaceStyles.ResolvePlugin(i);
                    response ??= surfaceStyles.CharacterMaterials[i].ToonResponse
                        ?? pluginReg?.DefaultToonResponse
                        ?? (effectiveHooks.HasFlag(D3D12StyleShaderHookMask.AllowStylizedShadingWrapper)
                            || effectiveHooks.HasFlag(D3D12StyleShaderHookMask.LightResponse)
                            ? PluginStylizedDefaultResponse
                            : null);
                    response = surfaceStyles.EffectiveToonResponse(i, response);
                    bool characterToon = response is not null || pipeline.Key.CharacterToon;
                    bool preserveDirect = hasPrimaryLight && surfaceStyles.EffectivePreserveDirectLightPalette(i, flags.PreserveDirectLightPalette);
                    var selected = GetPluginSurfacePipelines(pipeline, surfaceStyles.StyleIds[i], module, effectiveHooks, effectiveGeom, characterToon, preserveDirect);
                    commands.SetPipelineState(both ? selected.Both : selected.Back);
                }
                else
                {
                    commands.SetPipelineState(both ? pipeline.SurfaceBoth : pipeline.SurfaceBack);
                }

                var materialTreatment = isPluginStyle ? surfaceStyles!.EffectivePmxMaterialTreatment(i) : null;
                var stylizedShading = isPluginStyle ? surfaceStyles!.EffectiveStylizedShading(i) : null;
                var materialToon = SurfaceToon(binding, flags.UseSceneLighting);
                if (materialTreatment is not null) materialToon = materialTreatment.TransformShade(materialToon);

                Constants(common with
                {
                    EgColor = host.EgColor,
                    SpecularPower = host.SpecularPower,
                    ToonShadowColor = materialToon,
                    EyePosition = new(camera.Eye, isPluginStyle ? surfaceStyles!.EffectiveNormalShadeStrength(i) : 1),
                    GroundDirection = isPluginStyle
                        ? new(common.GroundDirection.X, common.GroundDirection.Y, common.GroundDirection.Z, surfaceStyles!.EffectiveGeometricShadowStrength(i))
                        : common.GroundDirection,
                    PmxMaterialTreatmentParams = new(materialTreatment?.Saturation ?? 1, materialTreatment?.ValueContrast ?? 0,
                        materialTreatment?.HairHighlightStrength ?? 0, materialTreatment?.HairHighlightWidth ?? .12f),
                    StylizedShadeParams = stylizedShading?.Shade ?? default,
                    StylizedTintParams = stylizedShading?.Tint ?? default,
                    StylizedWarmParams = stylizedShading?.Warm ?? default,
                    StylizedGradeParams = stylizedShading?.Grade ?? default,
                    ExtraSurfaceParams = drawData?.ExtraSurfaceParams ?? default,
                    IncomingLight = new(common.IncomingLight.X, common.IncomingLight.Y, common.IncomingLight.Z, response?.TransitionWidth ?? 0),
                    ShadowOptions = new(common.ShadowOptions.X, common.ShadowOptions.Y, common.ShadowOptions.Z, response?.Threshold ?? 0),
                    Options = new(material.AlphaCutoff, SurfaceMode(binding), material.DiffuseTexture >= 0 ? 1 : 0, options.Transp ? 1 : 0),
                });

                if (pipeline.TextureMorphRoot >= 0)
                    commands.SetGraphicsRootConstantBufferView(pipeline.TextureMorphRoot, context.UploadConstants(new TextureMorphConstants(binding.TextureFactors)));

                if (pipeline.PluginCbvRoot >= 0)
                {
                    if (drawData?.ConstantBufferData is { IsEmpty: false } cb)
                    {
                        int paddedLen = (cb.Length + 255) & ~255;
                        var staged = pluginCbvStaging[..paddedLen];
                        cb.Span.CopyTo(staged);
                        staged[cb.Length..].Clear();
                        commands.SetGraphicsRootConstantBufferView(pipeline.PluginCbvRoot, context.UploadConstants(staged));
                    }
                    else
                    {
                        commands.SetGraphicsRootConstantBufferView(pipeline.PluginCbvRoot, dummyPluginCbv);
                    }

                    int tableSlot = pluginSlots is not null ? pluginSlots[i] : pluginBaseSlot;
                    commands.SetGraphicsRootDescriptorTable(pipeline.PluginSrvTableRoot,
                        srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(tableSlot, srvStride));

                    for (int slot = 0; slot < D3D12PluginResourceLimits.MaxStructuredBufferSlots; slot++)
                    {
                        ulong bufferAddr = dummyStructuredBuffer;
                        if (drawData?.BufferBindings is { Count: > 0 } buffers)
                        {
                            D3D12PluginBufferBinding? foundBuffer = null;
                            for (int b = 0; b < buffers.Count; b++)
                            {
                                if (buffers[b].SlotIndex == slot) { foundBuffer = buffers[b]; break; }
                            }
                            if (foundBuffer is not null)
                                bufferAddr = ResolvePluginStructuredBufferAddress(foundBuffer, scene, i, cache, dummyStructuredBuffer);
                        }
                        commands.SetGraphicsRootShaderResourceView(pipeline.PluginStructuredRoot + slot, bufferAddr);
                    }
                }

                if (gi is not null)
                {
                    var giResponse = flags.GiToonResponseOverride ?? gi.ToonResponse;
                    float compositionTag = gi.Composition == RayGiComposition.ToonPalette && giResponse == RayGiToonResponse.PreservePaletteBands ? 2 : (float)gi.Composition;
                    commands.SetGraphicsRootConstantBufferView(4, context.UploadConstants(new GiSurfaceConstants((uint)(material.StartIndex / 3), (uint)width, gi.Strength, gi.OcclusionStrength, new(OriginalStyleMath.ResolveAmbientShare(material, style.Profile, flags.UseSceneLighting), compositionTag))));
                }
                if (nativeLights)
                {
                    var info = new NativeMaterialInfo((uint)ready.Native!.Count, ready.Native.ReceiverMasks[i],
                        flags.UseSceneLighting ? 1u : 0u, flags.ReceiveSelfShadow ? 1u : 0u);
                    var diffuse = material.Diffuse;
                    bool preserveDirect = hasPrimaryLight && (isPluginStyle
                        ? surfaceStyles!.EffectivePreserveDirectLightPalette(i, flags.PreserveDirectLightPalette)
                        : flags.PreserveDirectLightPalette);
                    diffuse.W = preserveDirect ? 1 : 0;
                    commands.SetGraphicsRootConstantBufferView(nativeRoot, context.UploadConstants(new NativeMaterialConstants(info, diffuse)));
                }
                commands.SetGraphicsRootDescriptorTable(1, srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase + (material.DiffuseTexture < 0 ? 0 : material.DiffuseTexture + 2), srvStride));
                commands.SetGraphicsRootDescriptorTable(2, srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase + (binding.SphereTexture < 0 ? 0 : binding.SphereTexture + 2), srvStride));
                commands.SetGraphicsRootDescriptorTable(3, srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(flags.ReceiveSelfShadow ? realSlot : whiteSlot, srvStride));
                commands.DrawIndexedInstanced(material.IndexCount, 1, material.StartIndex, 0, 0); draws++;
            }
        }
        if (frame.Outline is not null && frame.OutlineOptions is { Eligible: true, ModelWidth: > 0 })
            draws += RecordCombinedOutline(device, commands, pipeline.Outline, frame, width, height, vertexBuffer, indexBuffer, upload, own, context,
                srvHeap, srvStride, slotBase, depthFormat, cache, surfaceStyles);
        return draws;
    }

    private static ulong ResolvePluginStructuredBufferAddress(
        D3D12PluginBufferBinding binding,
        SceneSnapshot scene,
        int materialIndex,
        D3D12GpuCache cache,
        ulong dummyAddress)
    {
        if (binding.Kind == D3D12PluginBufferKind.MikkTangentCorners)
        {
            if (!binding.Data.IsEmpty)
            {
                string identity = binding.DataKey ?? Convert.ToHexString(SHA256.HashData(binding.Data.Span));
                return cache.BufferByIdentity($"plugin-mikk-raw-{materialIndex}-s{binding.SlotIndex}",
                    binding.Data.Span, ResourceStates.PixelShaderResource, identity).GPUVirtualAddress;
            }
            int uvChannel = D3D12TangentCornerGeometry.ResolveUvChannel(binding);
            var data = D3D12TangentCornerGeometry.Build(scene, materialIndex, uvChannel);
            if (data.Corners.IsEmpty) return dummyAddress;
            return cache.BufferByIdentity($"plugin-mikk-corners-{materialIndex}-s{binding.SlotIndex}-uv{uvChannel}",
                MemoryMarshal.AsBytes(data.Corners), ResourceStates.PixelShaderResource, data.Fingerprint).GPUVirtualAddress;
        }

        if (!binding.Data.IsEmpty)
        {
            string identity = $"{binding.DataKey}:{binding.ElementStrideBytes}:{binding.ElementCount}:{Convert.ToHexString(SHA256.HashData(binding.Data.Span))}";
            return cache.BufferByIdentity($"plugin-struct:{binding.DataKey}",
                binding.Data.Span, ResourceStates.PixelShaderResource, identity).GPUVirtualAddress;
        }

        int byteCount = checked(binding.ElementStrideBytes * binding.ElementCount);
        return cache.BufferByIdentity($"plugin-struct:{binding.DataKey}",
            new byte[byteCount], ResourceStates.PixelShaderResource, $"{binding.DataKey}:zero:{byteCount}").GPUVirtualAddress;
    }

    private FramePipelines GetFramePipelines(OriginalFrameScene frame, Format depthFormat, bool gi = false, bool hasPluginSpace2 = false)
    {
        using var pipelineCpu = MeasureSceneCpu(SceneCpuStage.Pipelines);
        bool subTexture = HasSubTexture(frame);
        bool textureMorph = subTexture || frame.Surface.Materials.Any(binding => !binding.TextureFactors.IsIdentity);
        var o = frame.Options!;
        var key = new FramePipelineKey(
            frame.Surface.Profile.TextureSampling,
            o.SelfShadow?.Sampling ?? OriginalTextureSampling.LinearClamp,
            depthFormat,
            o.BlendMode,
            o.WriteDepth,
            o.GroundShadow?.AlphaBlend == true,
            o.GroundShadow?.UseStencil == true,
            o.SelfShadow?.DepthComparison ?? OriginalDepthComparison.LessEqual,
            gi,
            HasNativeLights(frame),
            textureMorph,
            subTexture,
            o.AlphaOutput,
            frame.Materials.Any(flags => flags.CharacterToonResponse is not null),
            hasPluginSpace2,
            D3D12RasterWinding.FrontCounterClockwise(frame),
            HasNativeLights(frame) && HasPrimaryLight(frame.Surface.Profile) && frame.Materials.Any(flags => flags.PreserveDirectLightPalette));
        if (framePipelines.TryGetValue(key, out var existing)) return existing;

        RootParameter[] parameters =
        [
            new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)), ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 1)), ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 2)), ShaderVisibility.Pixel),
        ];
        if (gi)
            parameters =
            [
                ..parameters,
                new(RootParameterType.ConstantBufferView, new RootDescriptor(1, 0), ShaderVisibility.Pixel),
                new(RootParameterType.ShaderResourceView, new RootDescriptor(3, 0), ShaderVisibility.Pixel),
                new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 2, 4)), ShaderVisibility.Pixel),
            ];
        if (key.NativeLights)
            parameters =
            [
                ..parameters,
                new(RootParameterType.ConstantBufferView, new RootDescriptor(2, 0), ShaderVisibility.Pixel),
                new(new RootDescriptorTable(
                    new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 6),
                    new DescriptorRange(DescriptorRangeType.ConstantBufferView, 1, 0, 1)), ShaderVisibility.Pixel),
            ];
        int textureMorphRoot = key.TextureMorph ? parameters.Length : -1;
        if (key.TextureMorph)
            parameters = [..parameters, new(RootParameterType.ConstantBufferView, new RootDescriptor(3, 0), ShaderVisibility.Pixel)];

        int pluginCbvRoot = -1, pluginSrvTableRoot = -1, pluginStructuredRoot = -1;
        if (key.HasPluginSpace2)
        {
            pluginCbvRoot = parameters.Length;
            pluginSrvTableRoot = parameters.Length + 1;
            pluginStructuredRoot = parameters.Length + 2;
            parameters =
            [
                ..parameters,
                new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 2), ShaderVisibility.All),
                new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, D3D12PluginResourceLimits.MaxTextureSlots, 0, 2)), ShaderVisibility.Pixel),
                new(RootParameterType.ShaderResourceView, new RootDescriptor(12, 2), ShaderVisibility.Pixel),
                new(RootParameterType.ShaderResourceView, new RootDescriptor(13, 2), ShaderVisibility.Pixel),
                new(RootParameterType.ShaderResourceView, new RootDescriptor(14, 2), ShaderVisibility.Pixel),
                new(RootParameterType.ShaderResourceView, new RootDescriptor(15, 2), ShaderVisibility.Pixel),
            ];
        }

        StaticSamplerDescription Sampler(OriginalTextureSampling sampling, int register, int space = 0) => new(ShaderVisibility.Pixel, register, space)
        {
            Filter = sampling is OriginalTextureSampling.LinearClamp or OriginalTextureSampling.LinearWrap ? Filter.MinMagMipLinear : Filter.MinMagMipPoint,
            AddressU = sampling is OriginalTextureSampling.PointWrap or OriginalTextureSampling.LinearWrap ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
            AddressV = sampling is OriginalTextureSampling.PointWrap or OriginalTextureSampling.LinearWrap ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
            AddressW = sampling is OriginalTextureSampling.PointWrap or OriginalTextureSampling.LinearWrap ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
            MinLOD = 0f,
            MaxLOD = float.MaxValue,
        };
        StaticSamplerDescription[] samplers = [Sampler(key.SurfaceSampling, 0), Sampler(key.ShadowSampling, 1)];
        if (key.HasPluginSpace2)
        {
            samplers =
            [
                ..samplers,
                Sampler(OriginalTextureSampling.LinearWrap, 0, 2),
                Sampler(OriginalTextureSampling.LinearClamp, 1, 2),
                Sampler(OriginalTextureSampling.PointWrap, 2, 2),
                Sampler(OriginalTextureSampling.PointClamp, 3, 2),
            ];
        }

        var root = runtime.CreateRootSignature(new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout, parameters, samplers), RootSignatureVersion.Version10);
        var surfaceFormat = o.AlphaOutput == FrameAlphaOutput.Straight ? Format.R16G16B16A16_Float : Format.R8G8B8A8_UNorm;
        var created = new List<IDisposable> { root };
        try
        {
            var layout = new InputLayoutDescription(SurfaceLayout(key.SubTexture));
            var positionLayout = new InputLayoutDescription([new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0)]);
            var both = RasterizerDescription.CullNone; var back = both; back.CullMode = CullMode.Back; back.FrontCounterClockwise = key.FrontCounterClockwise;
            var front = back; front.CullMode = CullMode.Front;
            var shadowBackRaster = both; shadowBackRaster.CullMode = CullMode.Back; shadowBackRaster.FrontCounterClockwise = true;
            var depth = DepthStencilDescription.Default; depth.DepthFunc = ComparisonFunction.LessEqual; depth.DepthWriteMask = o.WriteDepth ? DepthWriteMask.All : DepthWriteMask.Zero;
            if (key.Stencil)
            {
                depth.StencilEnable = true; depth.StencilReadMask = 255; depth.StencilWriteMask = 255;
                depth.FrontFace.StencilFunc = ComparisonFunction.Always; depth.FrontFace.StencilPassOp = StencilOperation.Replace; depth.BackFace = depth.FrontFace;
            }
            var shadowDepth = DepthStencilDescription.Default; shadowDepth.DepthFunc = key.ShadowComparison == OriginalDepthComparison.Less ? ComparisonFunction.Less : ComparisonFunction.LessEqual; shadowDepth.DepthWriteMask = depth.DepthWriteMask;
            var groundDepth = depth; groundDepth.DepthFunc = ComparisonFunction.Less;
            if (key.Stencil) { groundDepth.FrontFace.StencilFunc = ComparisonFunction.Greater; groundDepth.BackFace = groundDepth.FrontFace; }

            ID3D12PipelineState Pso(string vs, string ps, InputLayoutDescription input, RasterizerDescription raster, DepthStencilDescription ds, BlendDescription blend, Format colorFormat, Format dsFormat)
            {
                runtime.PipelineCreations++;
                bool nativeShader = ps.Contains("Native", StringComparison.Ordinal);
                var state = runtime.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
                {
                    RootSignature = root,
                    VertexShader = key.SubTexture && (vs is "VSSurface" or "VSNativeDepth") ? GetSubTextureFrameShader(vs, true, key.NativeLights, gi) : vs == "VSNativeDepth" ? GetNativeFrameShader(vs, true) : GetFrameShader(vs, true),
                    PixelShader = key.CharacterDirectPalette && ps.StartsWith("PSSurface", StringComparison.Ordinal) ? GetCharacterDirectPaletteShader(ps, gi, key.TextureMorph, key.SubTexture, key.CharacterToon) : key.CharacterToon && ps.StartsWith("PSSurface", StringComparison.Ordinal) ? GetCharacterToonFrameShader(ps, key.NativeLights, gi, key.TextureMorph, key.SubTexture) : key.SubTexture && (ps.StartsWith("PSSurface", StringComparison.Ordinal) || ps == "PSNativeDepth") ? GetSubTextureFrameShader(ps, false, key.NativeLights, gi) : key.TextureMorph && ps.StartsWith("PSSurface", StringComparison.Ordinal) ? GetTextureMorphFrameShader(ps, key.NativeLights, gi) : nativeShader ? GetNativeFrameShader(ps, false) : gi && ps == "PSSurfaceGi" ? GetGiFrameShader(ps, false) : GetFrameShader(ps, false),
                    InputLayout = input,
                    BlendState = blend,
                    SampleMask = uint.MaxValue,
                    RasterizerState = raster,
                    DepthStencilState = ds,
                    PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                    RenderTargetFormats = [colorFormat],
                    DepthStencilFormat = dsFormat,
                    SampleDescription = new(1, 0),
                });
                created.Add(state); return state;
            }
            string surfacePixel = key.NativeLights ? (gi ? "PSSurfaceGiNative" : "PSSurfaceNative") : (gi ? "PSSurfaceGi" : "PSSurface");
            var surfaceBack = Pso("VSSurface", surfacePixel, layout, back, depth, FrameBlend(o.BlendMode, o.AlphaOutput), surfaceFormat, depthFormat);
            var surfaceBoth = Pso("VSSurface", surfacePixel, layout, both, depth, FrameBlend(o.BlendMode, o.AlphaOutput), surfaceFormat, depthFormat);
            var shadowBack = Pso("VSDepth", "PSDepth", positionLayout, shadowBackRaster, shadowDepth, BlendDescription.Opaque, Format.R32_Float, Format.D24_UNorm_S8_UInt);
            var shadowBoth = Pso("VSDepth", "PSDepth", positionLayout, both, shadowDepth, BlendDescription.Opaque, Format.R32_Float, Format.D24_UNorm_S8_UInt);
            var ground = Pso("VSGround", "PSColor", positionLayout, both, groundDepth, key.GroundBlend || o.AlphaOutput == FrameAlphaOutput.Straight ? FrameBlend(OriginalAlphaBlendMode.Normal, o.AlphaOutput) : BlendDescription.Opaque, surfaceFormat, depthFormat);
            ID3D12PipelineState? nativeShadowBack = null, nativeShadowBoth = null;
            if (key.NativeLights)
            {
                nativeShadowBack = Pso("VSNativeDepth", "PSNativeDepth", layout, shadowBackRaster, DepthStencilDescription.Default, BlendDescription.Opaque, Format.R32_Float, Format.D32_Float);
                nativeShadowBoth = Pso("VSNativeDepth", "PSNativeDepth", layout, both, DepthStencilDescription.Default, BlendDescription.Opaque, Format.R32_Float, Format.D32_Float);
            }
            var outlineRoot = runtime.CreateRootSignature(new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout, [new RootParameter(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All)]), RootSignatureVersion.Version10); created.Add(outlineRoot);
            var outlineShader = GetOutlineShaders(); runtime.PipelineCreations++;
            var outlineLayout = new InputLayoutDescription([new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new("TEXCOORD", 2, Format.R32G32B32A32_Float, 0, 1)]);
            var outline = runtime.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription { RootSignature = outlineRoot, VertexShader = outlineShader.Vertex, PixelShader = outlineShader.Pixel, InputLayout = outlineLayout, BlendState = FrameBlend(o.BlendMode, o.AlphaOutput), SampleMask = uint.MaxValue, RasterizerState = front, DepthStencilState = depth, PrimitiveTopologyType = PrimitiveTopologyType.Triangle, RenderTargetFormats = [surfaceFormat], DepthStencilFormat = depthFormat, SampleDescription = new(1, 0) }); created.Add(outline);
            var result = new FramePipelines(root, surfaceBack, surfaceBoth, shadowBack, shadowBoth, ground, new(outlineRoot, outline), nativeShadowBack, nativeShadowBoth, textureMorphRoot, pluginCbvRoot, pluginSrvTableRoot, pluginStructuredRoot, key);
            framePipelines.Add(key, result);
            return result;
        }
        catch { for (int i = created.Count - 1; i >= 0; i--) created[i].Dispose(); throw; }
    }

    private static BlendDescription FrameBlend(OriginalAlphaBlendMode mode, FrameAlphaOutput alpha = FrameAlphaOutput.LegacyMax)
    {
        var blend = BlendDescription.Opaque; blend.RenderTarget = (RenderTargetBlendDescription[])blend.RenderTarget.Clone(); var target = blend.RenderTarget[0]; target.BlendEnable = true;
        target.SrcBlend = Blend.SourceAlpha; target.DestBlend = mode == OriginalAlphaBlendMode.Additive ? Blend.One : Blend.InverseSourceAlpha; target.BlendOp = BlendOperation.Add;
        target.SrcBlendAlpha = Blend.One; target.DestBlendAlpha = alpha == FrameAlphaOutput.Straight ? Blend.InverseSourceAlpha : Blend.One;
        target.BlendOpAlpha = alpha == FrameAlphaOutput.Straight ? BlendOperation.Add : BlendOperation.Max; blend.RenderTarget[0] = target; return blend;
    }

    private void DisposeFramePipelines()
    {
        DisposePluginSurfacePipelines();
        foreach (var p in framePipelines.Values)
        {
            p.NativeShadowBoth?.Dispose(); p.NativeShadowBack?.Dispose(); p.Outline.State.Dispose(); p.Outline.Root.Dispose();
            p.Ground.Dispose(); p.ShadowBoth.Dispose(); p.ShadowBack.Dispose(); p.SurfaceBoth.Dispose(); p.SurfaceBack.Dispose(); p.Root.Dispose();
        }
        framePipelines.Clear();
    }
}
