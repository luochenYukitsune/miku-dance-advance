using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Observed statistics for a recorded full multi-bounce path-tracing frame.</summary>
public sealed record D3D12PathTracingStatistics(
    bool Active,
    bool UsedHardwareRayTracing,
    string BackendIdentity,
    PathTracingShadingMode ShadingMode,
    int RequestedSamplesPerFrame,
    int EffectiveSamplesPerFrame,
    int MaxBounces,
    bool ProgressiveAccumulation,
    int AccumulatedFrames,
    int MaxAccumulatedFrames,
    bool HistoryReset,
    string HistoryResetReason,
    float FireflyClamp,
    RayGiDenoiser Denoiser,
    int Width,
    int Height,
    int DispatchCount);

/// <summary>Full multi-bounce Monte Carlo path tracer supporting both DXR RayQuery 1.1 hardware
/// ray tracing and Software BVH Compute fallback, with progressive accumulation, firefly clamping,
/// geometry-guided denoising, and ToonHybrid / Physical shading modes.</summary>
internal sealed class D3D12PathTracingRenderer(D3D12GpuRuntime runtime, D3D12GpuCache cache) : IDisposable
{
    private static readonly RayGiBvhNodeGpu[] DummyBvhNodes = [new(new(float.PositiveInfinity), new(float.NegativeInfinity), new(0, 0, 1, 1))];
    private static readonly uint[] DummyTriangleOrder = [0u];
    private static readonly NativeRayGiLight[] DummyNativeLights = [default];
    private static readonly RayGiDiffuseTextureFactorGpu[] DummyDiffuseFactors = [RayGiDiffuseTextureFactorGpu.Identity];
    private static readonly RayGiSubTextureUvGpu[] DummySubTextureUvs = [new(Vector4.Zero, Vector4.Zero)];
    private static readonly object DummyIdentity = new();

    private sealed class PersistentHistoryBuffers : IDisposable
    {
        internal readonly int Width, Height;
        internal readonly ID3D12Resource Accumulation;
        internal readonly ID3D12Resource GuidePosDepth;
        internal readonly ID3D12Resource GuideNormalMat;
        internal readonly ID3D12Resource GuideAlbedo;
        internal readonly ID3D12Resource Denoised;
        internal readonly ID3D12Resource Diagnostics;
        internal ResourceStates AccumulationState = ResourceStates.UnorderedAccess;
        internal ResourceStates GuidePosDepthState = ResourceStates.UnorderedAccess;
        internal ResourceStates GuideNormalMatState = ResourceStates.UnorderedAccess;
        internal ResourceStates GuideAlbedoState = ResourceStates.UnorderedAccess;
        internal ResourceStates DenoisedState = ResourceStates.UnorderedAccess;

        internal PersistentHistoryBuffers(ID3D12Device device, int width, int height)
        {
            Width = width;
            Height = height;
            ulong pixelBytes = checked((ulong)width * (ulong)height * 16UL);
            var created = new List<ID3D12Resource>(6);
            ID3D12Resource CreateBuf(ulong bytes)
            {
                var r = device.CreateCommittedResource(HeapType.Default,
                    ResourceDescription.Buffer(bytes, ResourceFlags.AllowUnorderedAccess),
                    ResourceStates.UnorderedAccess);
                created.Add(r);
                return r;
            }
            try
            {
                Accumulation = CreateBuf(pixelBytes);
                GuidePosDepth = CreateBuf(pixelBytes);
                GuideNormalMat = CreateBuf(pixelBytes);
                GuideAlbedo = CreateBuf(pixelBytes);
                Denoised = CreateBuf(pixelBytes);
                Diagnostics = CreateBuf(16);
            }
            catch
            {
                foreach (var r in created) r.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Accumulation.Dispose();
            GuidePosDepth.Dispose();
            GuideNormalMat.Dispose();
            GuideAlbedo.Dispose();
            Denoised.Dispose();
            Diagnostics.Dispose();
        }
    }

    private sealed record TracePipeline(ID3D12RootSignature Root, ID3D12PipelineState State);
    private sealed record DenoisePipeline(ID3D12RootSignature Root, ID3D12PipelineState State);
    private sealed record ResolvePipeline(ID3D12RootSignature Root, ID3D12PipelineState State);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PtMaterialExtraGpu(
        Vector4 EgColor,
        Vector4 ToonShadowColor,
        Vector4 SpecularPower,
        Vector4 Flags,
        Vector4 RoughnessMetallic);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PtConstantsGpu(
        RayGiUInt4 Dispatch,
        RayGiUInt4 Counts,
        RayGiUInt4 Config,
        Vector4 LightDirDist,
        Vector4 LightColorEnv,
        Vector4 Tuning,
        Vector4 Background,
        Vector4 CameraEye,
        Matrix4x4 InvViewProj,
        Matrix4x4 ViewProj,
        Matrix4x4 SphereRotation);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DenoiseConstantsGpu(RayGiUInt4 Dimensions, Vector4 Weights);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ResolveConstantsGpu(RayGiUInt4 Dimensions);

    private readonly Dictionary<(bool Hardware, bool TextureMorph, bool SubTexture), TracePipeline> tracePipelines = [];
    private DenoisePipeline? denoisePipeline;
    private readonly Dictionary<(Format ColorFormat, Format DepthFormat), ResolvePipeline> resolvePipelines = [];
    private (byte[] Vertex, byte[] Pixel)? resolveShaders;
    private DxcShaderCompiler? compiler;
    private D3D12RayGiCapabilities? capabilities;

    private D3D12RetainedResource? historyOwner;
    private PersistentHistoryBuffers? historyBuffers;
    private PathTracingHistoryKey? committedKey;
    private int committedAccumulatedFrames;
    private PathTracingHistoryKey? pendingKey;
    private int pendingAccumulatedFrames;
    private bool hasPendingCommit;
    private string invalidationReason = "FirstFrame";
    private bool disposed;

    internal D3D12PathTracingStatistics? LastStatistics { get; private set; }

    internal D3D12RayGiCapabilities QueryCapabilities()
    {
        if (capabilities is not null) return capabilities;
        bool available;
        try { _ = DxcShaderCompiler.ResolveCompilerPath(); available = true; }
        catch { available = false; }
        return capabilities = D3D12HardwareRayGi.Inspect(runtime, available);
    }

    internal void BeginFrame() => LastStatistics = null;

    internal void CommitFrame()
    {
        if (!hasPendingCommit) return;
        committedKey = pendingKey;
        committedAccumulatedFrames = pendingAccumulatedFrames;
        pendingKey = null;
        pendingAccumulatedFrames = 0;
        hasPendingCommit = false;
        invalidationReason = "FirstFrame";
    }

    internal void AbortFrame()
    {
        pendingKey = null;
        pendingAccumulatedFrames = 0;
        hasPendingCommit = false;
    }

    internal void InvalidateHistory(string reason = "ModeChanged")
    {
        AbortFrame();
        committedKey = null;
        committedAccumulatedFrames = 0;
        invalidationReason = reason;
    }

    internal void Record(
        D3D12RecordingContext context,
        OriginalFrameScene frame,
        D3D12GpuTarget output,
        D3D12GpuTarget depth,
        ID3D12Resource? vertexBuffer,
        ID3D12Resource? indexBuffer,
        bool capture)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(frame);
        var options = frame.Options?.PathTracing ?? PathTracingOptions.Default;
        options.Validate();

        int width = output.Width, height = output.Height;
        var sceneSnapshot = frame.Surface.Snapshot;
        bool straightAlpha = frame.Options?.AlphaOutput == FrameAlphaOutput.Straight;
        var background = frame.Options?.BackgroundColor ?? new Vector4(20f / 255f, 24f / 255f, 32f / 255f, 1f);

        var caps = QueryCapabilities();
        bool useHardware = options.UseHardwareRayTracing && caps.HardwareAvailable;
        if (!useHardware && (!caps.Compute || caps.ShaderModel < 60 || !caps.CompilerAvailable))
            throw new NotSupportedException(caps.UnavailableReason ?? "Path tracing requires Shader Model 6.0 compute and DXC.");

        var currentKey = PathTracingHistoryKey.Create(frame, width, height, options);
        string? resetReason = committedKey is null && !options.ResetHistory && options.ProgressiveAccumulation
            ? invalidationReason
            : PathTracingHistoryKey.EvaluateResetReason(committedKey, currentKey, options.ResetHistory);
        bool resetHistory = resetReason is not null;

        bool interactiveMotion = !capture && (options.Interactive
            || (options.ProgressiveAccumulation && resetReason is "CameraChanged" or "SceneGeometryChanged" or "SceneMaterialChanged" or "LightingChanged"));
        int effectiveSpp = interactiveMotion
            ? Math.Min(options.SamplesPerFrame, options.InteractiveSamplesPerFrame)
            : options.EffectiveSamplesPerFrame;

        if (sceneSnapshot.Indices.Length == 0)
        {
            var clearColor = straightAlpha
                ? new Color4(background.X * background.W, background.Y * background.W, background.Z * background.W, background.W)
                : new Color4(background.X, background.Y, background.Z, background.W);
            context.Transition(output, ResourceStates.RenderTarget);
            context.Transition(depth, ResourceStates.DepthWrite);
            context.Commands.ClearRenderTargetView(output.Handle, clearColor);
            context.Commands.ClearDepthStencilView(depth.Handle, ClearFlags.Depth | (depth.Format == Format.D24_UNorm_S8_UInt ? ClearFlags.Stencil : 0), 1f, 0);
            output.Initialized = true;
            depth.Initialized = true;
            pendingKey = currentKey;
            pendingAccumulatedFrames = 1;
            hasPendingCommit = true;
            LastStatistics = new D3D12PathTracingStatistics(
                Active: true,
                UsedHardwareRayTracing: useHardware,
                BackendIdentity: useHardware ? "PathTracing-DXR" : (runtime.IsWarp ? "PathTracing-SoftwareBVH-WARP" : "PathTracing-SoftwareBVH"),
                ShadingMode: options.ShadingMode,
                RequestedSamplesPerFrame: options.SamplesPerFrame,
                EffectiveSamplesPerFrame: effectiveSpp,
                MaxBounces: options.MaxBounces,
                ProgressiveAccumulation: options.ProgressiveAccumulation,
                AccumulatedFrames: 1,
                MaxAccumulatedFrames: options.MaxAccumulatedFrames,
                HistoryReset: true,
                HistoryResetReason: "EmptyScene",
                FireflyClamp: options.FireflyClamp,
                Denoiser: options.Denoiser,
                Width: width,
                Height: height,
                DispatchCount: 0);
            return;
        }

        var buffers = AcquireHistoryBuffers(context, width, height);
        int previousAccumulated = resetHistory ? 0 : committedAccumulatedFrames;
        bool alreadyConverged = !resetHistory && options.ProgressiveAccumulation && previousAccumulated >= options.MaxAccumulatedFrames;
        int nextAccumulated = alreadyConverged ? previousAccumulated : Math.Min(previousAccumulated + 1, options.MaxAccumulatedFrames);

        string backendIdentity = runtime.IsWarp ? "PathTracing-SoftwareBVH-WARP" : "PathTracing-SoftwareBVH";
        int dispatches = 0;

        if (!alreadyConverged)
        {
            bool dynamicGeometry = sceneSnapshot.HasDynamicVertices;
            bool gpuDeferredTriangles = useHardware && sceneSnapshot.HasDeferredDeformation && vertexBuffer is not null && indexBuffer is not null;
            var ptScene = gpuDeferredTriangles ? RayGiScene.FromOriginalForGpu(frame) : RayGiScene.FromOriginal(frame);
            RayGiBvh? bvh = useHardware ? null : ptScene.Bvh;

            ID3D12Resource Upload<T>(string name, ReadOnlySpan<T> data, object identity) where T : unmanaged
                => cache.BufferByIdentity("pt:" + name, MemoryMarshal.AsBytes(data), ResourceStates.NonPixelShaderResource, identity);

            ID3D12Resource triangles;
            ID3D12Resource? gpuBlasPositions = null;
            if (gpuDeferredTriangles)
            {
                var built = cache.GpuSkinning.BuildRayGiTriangles(frame, vertexBuffer!, indexBuffer!);
                triangles = built.Triangles;
                gpuBlasPositions = built.BlasPositions;
            }
            else
            {
                triangles = Upload("triangles", ptScene.Triangles, ptScene.GeometryIdentity);
            }

            var materials = Upload("materials", ptScene.Materials, ptScene.MaterialIdentity);
            var texels = Upload("texels", ptScene.Texels, ptScene.TexelIdentity);

            bool hasTextureMorph = !ptScene.DiffuseTextureFactors.IsEmpty;
            var diffuseFactors = hasTextureMorph
                ? Upload("diffuse-factors", ptScene.DiffuseTextureFactors, ptScene.MaterialIdentity)
                : Upload<RayGiDiffuseTextureFactorGpu>("diffuse-factors-dummy", DummyDiffuseFactors, DummyIdentity);

            bool hasSubTexture = !ptScene.SubTextureUvs.IsEmpty;
            var subTextureUvs = hasSubTexture
                ? Upload("subtexture-uv1", ptScene.SubTextureUvs, ptScene.SubTextureUvIdentity)
                : Upload<RayGiSubTextureUvGpu>("subtexture-uv1-dummy", DummySubTextureUvs, DummyIdentity);

            var extraMaterials = BuildMaterialExtras(frame);
            var extraMatBuffer = context.Own(UploadFrameBuffer(extraMaterials));

            var nativeLights = NativeRayGiLights.From(frame);
            var nativeLightBuffer = nativeLights.UploadRows.Length > 0
                ? Upload<NativeRayGiLight>("native-lights", nativeLights.UploadRows, nativeLights)
                : Upload<NativeRayGiLight>("native-lights-dummy", DummyNativeLights, DummyIdentity);

            ReadOnlySpan<RayGiBvhNodeGpu> nodes = useHardware ? [] : bvh!.Nodes;
            var gpuNodes = useHardware
                ? Upload<RayGiBvhNodeGpu>("nodes-dummy", DummyBvhNodes, DummyIdentity)
                : Upload("nodes", nodes.Length == 0 ? DummyBvhNodes : nodes, ptScene.GeometryIdentity);
            var gpuOrder = useHardware
                ? Upload<uint>("order-dummy", DummyTriangleOrder, DummyIdentity)
                : Upload("order", bvh!.TriangleOrder.Length == 0 ? DummyTriangleOrder : bvh.TriangleOrder, ptScene.GeometryIdentity);

            compiler ??= runtime.GetOrCreateDxcCompiler();
            D3D12HardwareRayGi.AccelerationStructure? acceleration = null;
            if (useHardware)
            {
                acceleration = D3D12HardwareRayGi.GetAccelerationStructure(
                    context, cache, ptScene, dynamicGeometry,
                    triangleBuffer: triangles, compiler: compiler,
                    positionBuffer: gpuBlasPositions,
                    deferredSnapshot: gpuDeferredTriangles ? sceneSnapshot : null);
                backendIdentity = "PathTracing-" + acceleration.BackendIdentity;
            }

            var camera = frame.Options?.CameraOverride ?? sceneSnapshot.Camera;
            var viewProj = camera.ViewProjection(width, height);
            if (!Matrix4x4.Invert(viewProj, out var invViewProj))
                throw new InvalidOperationException("Camera view-projection matrix must be invertible for path tracing.");

            var lightDir = Vector3.Normalize(sceneSnapshot.LightDirection);
            var primaryColor = Vector3.Max(frame.Surface.Profile.LightAmbient, Vector3.Zero);
            bool hasPrimaryLight = D3D12OriginalStyleRenderer.HasPrimaryLight(frame.Surface.Profile);
            uint flags = (options.ProgressiveAccumulation ? 1u : 0u)
                | (resetHistory ? 2u : 0u)
                | (nativeLights.HasReceiverMasks ? 4u : 0u)
                | (straightAlpha ? 8u : 0u)
                | (camera.LeftHanded ? 16u : 0u)
                | (hasPrimaryLight ? 32u : 0u);

            uint frameSeed = unchecked((uint)previousAccumulated * 0x9e3779b9u ^ 0x517cc1b7u);
            var constants = new PtConstantsGpu(
                Dispatch: new((uint)width, (uint)height, (uint)effectiveSpp, frameSeed),
                Counts: new((uint)ptScene.TriangleCount, (uint)ptScene.Materials.Length, (uint)nodes.Length, (uint)nativeLights.Values.Length),
                Config: new((uint)options.MaxBounces, (uint)options.ShadingMode, (uint)previousAccumulated, flags),
                LightDirDist: new(lightDir, 1000f),
                LightColorEnv: new(primaryColor, nativeLights.EnvironmentStrength),
                Tuning: new(0.002f, options.FireflyClamp, 0.026f, options.MaxAccumulatedFrames),
                Background: background,
                CameraEye: new(camera.Eye, camera.VerticalFovDegrees is null ? 1f : 0f),
                InvViewProj: invViewProj,
                ViewProj: viewProj,
                SphereRotation: ptScene.SphereRotation);

            var tracePipe = GetTracePipeline(useHardware, hasTextureMorph, hasSubTexture);
            var commands = context.Commands;
            TransitionBuffer(commands, buffers.Accumulation, ref buffers.AccumulationState, ResourceStates.UnorderedAccess);
            TransitionBuffer(commands, buffers.GuidePosDepth, ref buffers.GuidePosDepthState, ResourceStates.UnorderedAccess);
            TransitionBuffer(commands, buffers.GuideNormalMat, ref buffers.GuideNormalMatState, ResourceStates.UnorderedAccess);
            TransitionBuffer(commands, buffers.GuideAlbedo, ref buffers.GuideAlbedoState, ResourceStates.UnorderedAccess);

            commands.SetComputeRootSignature(tracePipe.Root);
            commands.SetPipelineState(tracePipe.State);
            commands.SetComputeRootConstantBufferView(0, context.UploadConstants(constants));
            commands.SetComputeRootShaderResourceView(1, triangles.GPUVirtualAddress);
            commands.SetComputeRootShaderResourceView(2, materials.GPUVirtualAddress);
            commands.SetComputeRootShaderResourceView(3, texels.GPUVirtualAddress);
            commands.SetComputeRootShaderResourceView(4, gpuNodes.GPUVirtualAddress);
            commands.SetComputeRootShaderResourceView(5, gpuOrder.GPUVirtualAddress);
            commands.SetComputeRootShaderResourceView(6, extraMatBuffer.GPUVirtualAddress);
            commands.SetComputeRootShaderResourceView(7, nativeLightBuffer.GPUVirtualAddress);
            commands.SetComputeRootShaderResourceView(8, diffuseFactors.GPUVirtualAddress);
            commands.SetComputeRootShaderResourceView(9, subTextureUvs.GPUVirtualAddress);
            int uavBase = 10;
            if (useHardware)
            {
                commands.SetComputeRootShaderResourceView(10, acceleration!.TopLevel.GPUVirtualAddress);
                uavBase = 11;
            }
            commands.SetComputeRootUnorderedAccessView(uavBase, buffers.Accumulation.GPUVirtualAddress);
            commands.SetComputeRootUnorderedAccessView(uavBase + 1, buffers.GuidePosDepth.GPUVirtualAddress);
            commands.SetComputeRootUnorderedAccessView(uavBase + 2, buffers.GuideNormalMat.GPUVirtualAddress);
            commands.SetComputeRootUnorderedAccessView(uavBase + 3, buffers.GuideAlbedo.GPUVirtualAddress);
            commands.SetComputeRootUnorderedAccessView(uavBase + 4, buffers.Diagnostics.GPUVirtualAddress);

            commands.Dispatch((width + 7) / 8, (height + 7) / 8, 1);
            commands.ResourceBarrierUnorderedAccessView(buffers.Accumulation);
            commands.ResourceBarrierUnorderedAccessView(buffers.GuidePosDepth);
            commands.ResourceBarrierUnorderedAccessView(buffers.GuideNormalMat);
            commands.ResourceBarrierUnorderedAccessView(buffers.GuideAlbedo);
            dispatches++;
        }
        else if (useHardware)
        {
            backendIdentity = "PathTracing-DXR-Converged";
        }

        // Optional geometry-guided spatial / temporal-spatial denoiser pass.
        var cmds = context.Commands;
        ID3D12Resource resolvedRadiance = buffers.Accumulation;
        if (options.Denoiser is RayGiDenoiser.Spatial or RayGiDenoiser.TemporalSpatial)
        {
            compiler ??= runtime.GetOrCreateDxcCompiler();
            var denoisePipe = GetDenoisePipeline();
            TransitionBuffer(cmds, buffers.Accumulation, ref buffers.AccumulationState, ResourceStates.NonPixelShaderResource);
            TransitionBuffer(cmds, buffers.GuidePosDepth, ref buffers.GuidePosDepthState, ResourceStates.NonPixelShaderResource);
            TransitionBuffer(cmds, buffers.GuideNormalMat, ref buffers.GuideNormalMatState, ResourceStates.NonPixelShaderResource);
            TransitionBuffer(cmds, buffers.GuideAlbedo, ref buffers.GuideAlbedoState, ResourceStates.NonPixelShaderResource);
            TransitionBuffer(cmds, buffers.Denoised, ref buffers.DenoisedState, ResourceStates.UnorderedAccess);

            var denoiseCb = new DenoiseConstantsGpu(
                new((uint)width, (uint)height, (uint)nextAccumulated, options.Denoiser == RayGiDenoiser.TemporalSpatial ? 2u : 1u),
                new(1.0f, 32.0f, 0.25f, 0.08f));
            cmds.SetComputeRootSignature(denoisePipe.Root);
            cmds.SetPipelineState(denoisePipe.State);
            cmds.SetComputeRootConstantBufferView(0, context.UploadConstants(denoiseCb));
            cmds.SetComputeRootShaderResourceView(1, buffers.Accumulation.GPUVirtualAddress);
            cmds.SetComputeRootShaderResourceView(2, buffers.GuidePosDepth.GPUVirtualAddress);
            cmds.SetComputeRootShaderResourceView(3, buffers.GuideNormalMat.GPUVirtualAddress);
            cmds.SetComputeRootShaderResourceView(4, buffers.GuideAlbedo.GPUVirtualAddress);
            cmds.SetComputeRootUnorderedAccessView(5, buffers.Denoised.GPUVirtualAddress);
            cmds.Dispatch((width + 7) / 8, (height + 7) / 8, 1);
            cmds.ResourceBarrierUnorderedAccessView(buffers.Denoised);
            TransitionBuffer(cmds, buffers.Denoised, ref buffers.DenoisedState, ResourceStates.PixelShaderResource);
            resolvedRadiance = buffers.Denoised;
            dispatches++;
        }
        else
        {
            TransitionBuffer(cmds, buffers.Accumulation, ref buffers.AccumulationState, ResourceStates.PixelShaderResource);
        }

        TransitionBuffer(cmds, buffers.GuidePosDepth, ref buffers.GuidePosDepthState, ResourceStates.PixelShaderResource);

        // Fullscreen resolve to the frame's output RTV + primary DSV (enables ToonHybrid authored outlines).
        var resolvePipe = GetResolvePipeline(output.Format, depth.Format);
        cmds.UnsetRenderTargets();
        context.Transition(output, ResourceStates.RenderTarget);
        context.Transition(depth, ResourceStates.DepthWrite);
        cmds.OMSetRenderTargets(output.Handle, depth.Handle);
        cmds.RSSetViewport(0, 0, width, height, 0, 1);
        cmds.RSSetScissorRect(new RawRect(0, 0, width, height));
        cmds.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmds.SetGraphicsRootSignature(resolvePipe.Root);
        cmds.SetPipelineState(resolvePipe.State);
        cmds.SetGraphicsRootConstantBufferView(0, context.UploadConstants(new ResolveConstantsGpu(
            new((uint)width, (uint)height, (uint)options.ShadingMode, straightAlpha ? 1u : 0u))));
        cmds.SetGraphicsRootShaderResourceView(1, resolvedRadiance.GPUVirtualAddress);
        cmds.SetGraphicsRootShaderResourceView(2, buffers.GuidePosDepth.GPUVirtualAddress);
        cmds.DrawInstanced(3, 1, 0, 0);
        output.Initialized = true;
        depth.Initialized = true;

        pendingKey = currentKey;
        pendingAccumulatedFrames = nextAccumulated;
        hasPendingCommit = true;

        LastStatistics = new D3D12PathTracingStatistics(
            Active: true,
            UsedHardwareRayTracing: useHardware,
            BackendIdentity: backendIdentity,
            ShadingMode: options.ShadingMode,
            RequestedSamplesPerFrame: options.SamplesPerFrame,
            EffectiveSamplesPerFrame: effectiveSpp,
            MaxBounces: options.MaxBounces,
            ProgressiveAccumulation: options.ProgressiveAccumulation,
            AccumulatedFrames: nextAccumulated,
            MaxAccumulatedFrames: options.MaxAccumulatedFrames,
            HistoryReset: resetHistory,
            HistoryResetReason: resetReason ?? "None",
            FireflyClamp: options.FireflyClamp,
            Denoiser: options.Denoiser,
            Width: width,
            Height: height,
            DispatchCount: dispatches);
    }

    private PersistentHistoryBuffers AcquireHistoryBuffers(D3D12RecordingContext context, int width, int height)
    {
        if (historyBuffers is not null && historyBuffers.Width == width && historyBuffers.Height == height && historyOwner is not null)
        {
            context.Own(historyOwner.Lease());
            return historyBuffers;
        }
        historyOwner?.Dispose();
        historyOwner = null;
        historyBuffers = null;
        var created = new PersistentHistoryBuffers(runtime.Device, width, height);
        var owner = new D3D12RetainedResource(new object(), created);
        historyBuffers = created;
        historyOwner = owner;
        context.Own(owner.Lease());
        return created;
    }

    private ID3D12Resource UploadFrameBuffer<T>(ReadOnlySpan<T> data) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(data);
        int byteCount = Math.Max(bytes.Length, 16);
        var resource = runtime.Device.CreateCommittedResource(HeapType.Upload,
            ResourceDescription.Buffer((ulong)byteCount), ResourceStates.GenericRead);
        var mapped = resource.Map<byte>(0, byteCount);
        try { bytes.CopyTo(mapped); }
        finally { resource.Unmap(0); }
        runtime.BufferUploads++;
        return resource;
    }

    private static PtMaterialExtraGpu[] BuildMaterialExtras(OriginalFrameScene frame)
    {
        var scene = frame.Surface.Snapshot;
        var profile = frame.Surface.Profile;
        var extras = new PtMaterialExtraGpu[scene.Materials.Count];
        for (int i = 0; i < scene.Materials.Count; i++)
        {
            var mat = scene.Materials[i];
            var binding = frame.Surface.Materials[i];
            var flags = frame.Materials[i];
            var host = OriginalStyleMath.ResolveHostColors(mat, profile, flags.UseSceneLighting);
            var toon = flags.UseSceneLighting ? Vector3.Zero : D3D12OriginalStyleRenderer.ResolveTextureMorphToon(binding);
            float sphereMode = binding.SphereMode == OriginalSphereMode.SubTexture && binding.SubTextureAdditive
                ? 4f : (float)binding.SphereMode;
            float roughness = Math.Clamp(MathF.Sqrt(2f / (MathF.Max(mat.Shininess, 0f) + 2f)), 0.05f, 1f);
            float metallic = 0f;
            extras[i] = new PtMaterialExtraGpu(
                EgColor: host.EgColor,
                ToonShadowColor: new(toon, 1f),
                SpecularPower: host.SpecularPower,
                Flags: new(flags.UseSceneLighting ? 1f : 0f, sphereMode, flags.ReceiveSelfShadow ? 1f : 0f, mat.DiffuseTexture >= 0 ? 1f : 0f),
                RoughnessMetallic: new(roughness, metallic, 0f, 0f));
        }
        return extras;
    }

    private static void TransitionBuffer(ID3D12GraphicsCommandList commands, ID3D12Resource resource, ref ResourceStates current, ResourceStates target)
    {
        if (current == target) return;
        commands.ResourceBarrierTransition(resource, current, target);
        current = target;
    }

    private TracePipeline GetTracePipeline(bool hardware, bool textureMorph, bool subTexture)
    {
        var key = (hardware, textureMorph, subTexture);
        if (tracePipelines.TryGetValue(key, out var found)) return found;
        compiler ??= runtime.GetOrCreateDxcCompiler();
        var defines = new Dictionary<string, string?>();
        if (hardware) defines.Add("PT_HARDWARE", "1");
        string source = D3D12PathTracingShaders.TraceShaderSource(hardware, textureMorph, subTexture);
        var shader = compiler.CompileCompute(source, "CSPathTrace", hardware ? "cs_6_5" : "cs_6_0", defines);

        var parameters = new List<RootParameter>
        {
            new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
        };
        for (int t = 0; t <= 8; t++)
            parameters.Add(new(RootParameterType.ShaderResourceView, new RootDescriptor(t, 0), ShaderVisibility.All));
        if (hardware)
            parameters.Add(new(RootParameterType.ShaderResourceView, new RootDescriptor(9, 0), ShaderVisibility.All));
        for (int u = 0; u <= 4; u++)
            parameters.Add(new(RootParameterType.UnorderedAccessView, new RootDescriptor(u, 0), ShaderVisibility.All));

        var root = runtime.CreateRootSignature(new RootSignatureDescription(RootSignatureFlags.None, parameters.ToArray()), RootSignatureVersion.Version10);
        try
        {
            runtime.ShaderCompilations++;
            runtime.PipelineCreations++;
            var state = runtime.CreateComputePipelineState(new ComputePipelineStateDescription
            {
                RootSignature = root,
                ComputeShader = shader.Dxil,
            });
            var created = new TracePipeline(root, state);
            tracePipelines.Add(key, created);
            return created;
        }
        catch { root.Dispose(); throw; }
    }

    private DenoisePipeline GetDenoisePipeline()
    {
        if (denoisePipeline is not null) return denoisePipeline;
        compiler ??= runtime.GetOrCreateDxcCompiler();
        var shader = compiler.CompileCompute(D3D12PathTracingShaders.DenoiserSource, "CSDenoise", "cs_6_0");
        RootParameter[] parameters =
        [
            new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(1, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(2, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(3, 0), ShaderVisibility.All),
            new(RootParameterType.UnorderedAccessView, new RootDescriptor(0, 0), ShaderVisibility.All),
        ];
        var root = runtime.CreateRootSignature(new RootSignatureDescription(RootSignatureFlags.None, parameters), RootSignatureVersion.Version10);
        try
        {
            runtime.ShaderCompilations++;
            runtime.PipelineCreations++;
            var state = runtime.CreateComputePipelineState(new ComputePipelineStateDescription
            {
                RootSignature = root,
                ComputeShader = shader.Dxil,
            });
            return denoisePipeline = new DenoisePipeline(root, state);
        }
        catch { root.Dispose(); throw; }
    }

    private ResolvePipeline GetResolvePipeline(Format colorFormat, Format depthFormat)
    {
        var key = (colorFormat, depthFormat);
        if (resolvePipelines.TryGetValue(key, out var found)) return found;
        if (resolveShaders is null)
        {
            const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness;
            resolveShaders = (
                D3D12FxcShaderCache.Compile(D3D12PathTracingShaders.ResolveSource, "VSResolve", "pathtrace-resolve.hlsl", "vs_5_1", flags),
                D3D12FxcShaderCache.Compile(D3D12PathTracingShaders.ResolveSource, "PSResolve", "pathtrace-resolve.hlsl", "ps_5_1", flags));
            runtime.ShaderCompilations += 2;
        }
        RootParameter[] parameters =
        [
            new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.Pixel),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(0, 0), ShaderVisibility.Pixel),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(1, 0), ShaderVisibility.Pixel),
        ];
        var root = runtime.CreateRootSignature(new RootSignatureDescription(RootSignatureFlags.None, parameters), RootSignatureVersion.Version10);
        try
        {
            runtime.PipelineCreations++;
            var depthState = DepthStencilDescription.Default;
            depthState.DepthEnable = true;
            depthState.DepthFunc = ComparisonFunction.Always;
            depthState.DepthWriteMask = DepthWriteMask.All;
            var state = runtime.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
            {
                RootSignature = root,
                VertexShader = resolveShaders.Value.Vertex,
                PixelShader = resolveShaders.Value.Pixel,
                BlendState = BlendDescription.Opaque,
                SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone,
                DepthStencilState = depthState,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = [colorFormat],
                DepthStencilFormat = depthFormat,
                SampleDescription = new(1, 0),
            });
            var created = new ResolvePipeline(root, state);
            resolvePipelines.Add(key, created);
            return created;
        }
        catch { root.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        historyOwner?.Dispose();
        historyOwner = null;
        historyBuffers = null;
        foreach (var p in tracePipelines.Values) { p.State.Dispose(); p.Root.Dispose(); }
        tracePipelines.Clear();
        denoisePipeline?.State.Dispose();
        denoisePipeline?.Root.Dispose();
        denoisePipeline = null;
        foreach (var p in resolvePipelines.Values) { p.State.Dispose(); p.Root.Dispose(); }
        resolvePipelines.Clear();
    }
}
