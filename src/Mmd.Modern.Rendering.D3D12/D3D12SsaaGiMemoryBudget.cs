using Mmd.Modern.Core;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Preflight for the newly supported multi-sample GI path. This is a conservative
/// reservation, not an allocation guarantee or permission to lower the requested quality.</summary>
internal static class D3D12SsaaGiMemoryBudget
{
    internal const int MaximumInFlightFrames = 3; // D3D12GpuRuntime's three recording slots.
    internal const ulong FallbackReservationBytes = 256UL * 1024 * 1024;
    private const ulong MetadataHeadroomBytes = 8UL * 1024 * 1024;

    internal readonly record struct Estimate(ulong PerSampleBytes, ulong PerFrameBytes, ulong InFlightBytes)
    {
        internal int InFlightFrames { get; init; } = MaximumInFlightFrames;
        internal int ReceiverWorkspaces { get; init; }
    }
    internal readonly record struct Budget(ulong BudgetBytes, ulong CurrentUsageBytes, bool FromDxgi)
    {
        // Leave half of the OS budget for meshes, textures, acceleration structures, other views
        // and driver allocations. Existing process usage also includes resources of older slots;
        // counting the full three-frame window again deliberately errs on the conservative side.
        internal ulong AvailableBytes => FromDxgi
            ? Math.Min(BudgetBytes / 2, BudgetBytes > CurrentUsageBytes ? BudgetBytes - CurrentUsageBytes : 0)
            : FallbackReservationBytes;
    }

    internal static Estimate FromResourceSizes(ulong perSampleBytes, ulong workspaceBytes, int samples,
        bool reuseReceiverWorkspace = false, int inFlightFrames = MaximumInFlightFrames, ulong perSampleDiagnosticBytes = 0)
    {
        if (samples is < 2 or > 32) throw new ArgumentOutOfRangeException(nameof(samples));
        return FromMeasuredSizes(perSampleBytes, workspaceBytes, samples, reuseReceiverWorkspace, inFlightFrames, perSampleDiagnosticBytes);
    }

    private static Estimate FromMeasuredSizes(ulong perSampleBytes, ulong workspaceBytes, int samples,
        bool reuseReceiverWorkspace, int inFlightFrames, ulong perSampleDiagnosticBytes)
    {
        if (samples is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(samples));
        if (inFlightFrames is < 1 or > MaximumInFlightFrames) throw new ArgumentOutOfRangeException(nameof(inFlightFrames));
        int receivers = reuseReceiverWorkspace ? 1 : samples;
        ulong frame = checked(perSampleBytes * (ulong)receivers + workspaceBytes + perSampleDiagnosticBytes * (ulong)samples);
        return new(perSampleBytes, frame, checked(frame * (ulong)inFlightFrames))
            { InFlightFrames = inFlightFrames, ReceiverWorkspaces = receivers };
    }

    internal static void RequireFits(Estimate estimate, Budget budget, int width, int height, int samples)
    {
        if (estimate.InFlightBytes <= budget.AvailableBytes) return;
        string source = budget.FromDxgi ? "DXGI local-memory budget" : "conservative fallback budget (DXGI unavailable)";
        throw new D3D12GiMemoryBudgetExceededException($"GI with {samples} spatial samples at {width}x{height} requires an estimated "
            + $"{estimate.PerFrameBytes / (1024d * 1024):F1} MiB per frame / {estimate.InFlightBytes / (1024d * 1024):F1} MiB "
            + $"for {estimate.InFlightFrames} in-flight frames; only {budget.AvailableBytes / (1024d * 1024):F1} MiB fits the application GI reservation using the {source}. "
            + "Choose fewer spatial samples or a lower render resolution. Quality was not changed and no GPU frame was begun.",
            estimate.InFlightBytes, budget.AvailableBytes, samples);
    }

    internal static Budget Query(D3D12GpuRuntime runtime)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
            using var adapter = factory.EnumAdapterByLuid<IDXGIAdapter3>(Vortice.Luid.FromInt64(runtime.Device.AdapterLuid));
            var memory = adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
            if (memory.Budget > 0) return new(memory.Budget, memory.CurrentUsage, true);
        }
        catch (Exception)
        {
            // The optional residency interface can be unavailable on older drivers / WARP.
            // Failure to query is not evidence of unlimited memory: use the bounded fallback.
        }
        return new(0, 0, false);
    }

    internal static Estimate Measure(D3D12GpuRuntime runtime, int width, int height, int displayWidth, int displayHeight,
        int samples, RayGiOptions options, bool capture, bool smaa, bool upscale, bool straightAlpha=false,
        OriginalFrameScene? frame=null, bool reuseReceiverWorkspace=false, int inFlightFrames=MaximumInFlightFrames,
        bool temporalUpscaling=false)
    {
        var device = runtime.Device;
        ulong Allocation(ResourceDescription description)
        {
            // Committed allocations use the driver's resource alignment / tiling requirements,
            // including the minimum 64 KiB for small buffers and render/depth targets.
            ulong bytes = device.GetResourceAllocationInfo(0, [description]).SizeInBytes;
            if (bytes == 0 || bytes == ulong.MaxValue)
                throw new NotSupportedException("The device could not estimate the requested GI/SSAA resource allocation.");
            return bytes;
        }
        ResourceDescription Texture(Format format, ResourceFlags flags, int w, int h)
            => ResourceDescription.Texture2D(format, (uint)w, (uint)h, mipLevels: 1, flags: flags);
        ulong Buffer(ulong bytes, ResourceFlags flags = ResourceFlags.None) => Allocation(ResourceDescription.Buffer(bytes, flags));
        ulong Readback(ResourceDescription image)
        {
            device.GetCopyableFootprints(image, 0, 1, 0, new PlacedSubresourceFootPrint[1], new int[1], new ulong[1], out ulong bytes);
            return Buffer(bytes); // Includes the row-pitched copy footprint and committed-resource alignment.
        }
        var depth = Texture(Format.D32_Float, ResourceFlags.AllowDepthStencil, width, height);
        var color = Texture(straightAlpha?Format.R16G16B16A16_Float:Format.R8G8B8A8_UNorm, ResourceFlags.AllowRenderTarget, width, height);
        var display = Texture(Format.R8G8B8A8_UNorm, ResourceFlags.AllowRenderTarget, displayWidth, displayHeight);
        ulong indirectBytes = checked((ulong)width * (ulong)height * 16);
        bool denoise = D3D12RayGiDenoiser.IsEnabled(options);
        bool halfRes = options.PreviewAcceleration && width >= 2 && height >= 2;
        int traceWidth = halfRes ? (width + 1) / 2 : width;
        int traceHeight = halfRes ? (height + 1) / 2 : height;
        ulong diagnosticBytes=0;
        ulong ReceiverBytes(int drawWidth,int drawHeight)
        {
            var receiverImage=Texture(Format.R32G32B32A32_Float,ResourceFlags.AllowRenderTarget,drawWidth,drawHeight);
            var receiverDepth=Texture(Format.D32_Float,ResourceFlags.AllowDepthStencil,drawWidth,drawHeight);
            ulong drawIndirectBytes=checked((ulong)drawWidth*(ulong)drawHeight*16);
            ulong bytes=checked(2*Allocation(receiverImage)+Allocation(receiverDepth)
                +Buffer(drawIndirectBytes,ResourceFlags.AllowUnorderedAccess)
                +2*Buffer(16)); // GPU diagnostic counter and its zero upload allocation.
            if(halfRes)
            {
                var halfImage=Texture(Format.R32G32B32A32_Float,ResourceFlags.AllowUnorderedAccess,traceWidth,traceHeight);
                ulong halfIndirectBytes=checked((ulong)traceWidth*(ulong)traceHeight*16);
                bytes=checked(bytes+2*Allocation(halfImage)+(denoise?2UL:1UL)*Buffer(halfIndirectBytes,ResourceFlags.AllowUnorderedAccess));
            }
            else if(denoise)
                bytes=checked(bytes+Buffer(drawIndirectBytes,ResourceFlags.AllowUnorderedAccess));
            if(capture&&Environment.GetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS") is not null)
                diagnosticBytes=checked(diagnosticBytes+2*Readback(receiverImage)+(denoise?2UL:1UL)*Buffer(drawIndirectBytes)+Buffer(16));
            if(capture&&!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MMD_RAYGI_TIMING")))
                diagnosticBytes=checked(diagnosticBytes+Buffer(16));
            return bytes;
        }
        ulong sample=ReceiverBytes(width,height);

        // Common frame: color/depth, float accumulation and resolved RGBA8. The fixed headroom
        // covers descriptor/query heaps and reusable constant upload pages; cached scene assets
        // are excluded here and protected by the half-budget policy and current process usage.
        var surfaceDepth = frame?.Options?.GroundShadow?.UseStencil==true
            ? Texture(Format.D24_UNorm_S8_UInt, ResourceFlags.AllowDepthStencil, width, height) : depth;
        ulong workspace = checked(2 * Allocation(color) + Allocation(surfaceDepth)
            + Buffer(indirectBytes, ResourceFlags.AllowUnorderedAccess) + MetadataHeadroomBytes);
        // RecordFrame shares its prepared white/shadow resources across all spatial samples.
        // Reserve each committed image once per frame, using the same formats and dimensions
        // as its actual recorder rather than hiding potentially large maps in fixed headroom.
        if(frame is {Options:not null} && !frame.Surface.Snapshot.IsEmpty)
        {
            workspace=checked(workspace+Allocation(Texture(Format.R32_Float, ResourceFlags.AllowRenderTarget, 1, 1)));
            if(frame.Options.SelfShadow is {Generate:true} shadow)
                workspace=checked(workspace
                    +Allocation(Texture(Format.R32_Float, ResourceFlags.AllowRenderTarget, shadow.Width, shadow.Height))
                    +Allocation(Texture(Format.D24_UNorm_S8_UInt, ResourceFlags.AllowDepthStencil, shadow.Width, shadow.Height)));
            var nativeAtlas=NativeLightShadowAtlasLayout.Create(frame.Lighting);
            if(nativeAtlas.FaceCount>0)
            {
                int atlasWidth=nativeAtlas.Width;
                int atlasHeight=nativeAtlas.Height;
                workspace=checked(workspace
                    +Allocation(Texture(Format.R32_Float, ResourceFlags.AllowRenderTarget, atlasWidth, atlasHeight))
                    +Allocation(Texture(Format.D32_Float, ResourceFlags.AllowDepthStencil, atlasWidth, atlasHeight)));
            }
        }
        if (options.Mode == LightingMode.HardwareRayTracing && frame is not null && !frame.Surface.Snapshot.IsEmpty)
        {
            int triangles = frame.Surface.Snapshot.Indices.Length / 3;
            bool dynamicGeometry = frame.Surface.Snapshot.HasDynamicVertices;
            var acceleration = D3D12HardwareRayGi.PrebuildResources(device, triangles, dynamicGeometry);
            ulong pair = checked(Buffer(acceleration.Bottom, ResourceFlags.AllowUnorderedAccess)
                + Buffer(acceleration.Top, ResourceFlags.AllowUnorderedAccess));
            // One AS operation per geometry/frame, shared by all SSAA samples. Out-of-place
            // updates also lease the prior pair; conservatively reserve it even on a cold build.
            // Allocation() applies committed-resource alignment to actual driver prebuild sizes.
            workspace = checked(workspace + pair * (dynamicGeometry ? 2UL : 1UL)
                + Buffer(acceleration.BottomScratch, ResourceFlags.AllowUnorderedAccess)
                + Buffer(acceleration.TopScratch, ResourceFlags.AllowUnorderedAccess)
                + 2 * Buffer(checked((ulong)triangles * 36))
                + Buffer((ulong)System.Runtime.InteropServices.Marshal.SizeOf<RaytracingInstanceDescription>()));
        }
        if (smaa) workspace = checked(workspace + 2 * Allocation(Texture(Format.R8G8B8A8_UNorm, ResourceFlags.AllowRenderTarget, width, height)) + Allocation(color));
        if (straightAlpha) workspace = checked(workspace + Allocation(display));
        if (upscale) workspace = checked(workspace + Allocation(
            Texture(Format.R32G32B32A32_Float, ResourceFlags.AllowRenderTarget, displayWidth, displayHeight))
            + Allocation(display)); // Worst-case FP32 EASU intermediate plus optional RGBA8 RCAS.
        if (capture) workspace = checked(workspace + Readback(display));
        if (temporalUpscaling) workspace = checked(workspace
            + D3D12TemporalGiMemoryBudget.MeasureTemporalUpscalingWorkspace(runtime,width,height,displayWidth,displayHeight,frame));
        // Readbacks and timestamp results preserve every sample's output even when the GPU
        // receiver resources themselves are reused. They remain alive through CPU capture.
        return FromMeasuredSizes(sample, workspace, samples, reuseReceiverWorkspace, inFlightFrames, diagnosticBytes);
    }

    internal static Estimate WithAdditionalReservation(Estimate estimate,ulong bytes)
        =>estimate with {InFlightBytes=checked(estimate.InFlightBytes+bytes)};

    internal static ulong MeasureEndfieldSeeds(D3D12GpuRuntime runtime,SceneSnapshot scene,bool active)
    {
        if(!active || scene.VertexCount==0)return 0;
        ulong size=runtime.Device.GetResourceAllocationInfo(0,[ResourceDescription.Buffer(checked((ulong)scene.VertexCount*12))]).SizeInBytes;
        if(size==0 || size==ulong.MaxValue)throw new NotSupportedException("Device cannot estimate radial seed resources.");
        // Current cache owner + three fence-leased old owners + three staging uploads.
        return checked(size*(MaximumInFlightFrames*2UL+1));
    }

    internal static void Validate(D3D12GpuRuntime runtime, int width, int height, int displayWidth, int displayHeight,
        int samples, RayGiOptions options, bool capture, bool smaa, bool upscale, bool straightAlpha=false,
        OriginalFrameScene? frame=null,ulong additionalInFlightBytes=0, bool reuseReceiverWorkspace=false,
        int inFlightFrames=MaximumInFlightFrames, bool temporalUpscaling=false)
        => RequireFits(WithAdditionalReservation(Measure(runtime, width, height, displayWidth, displayHeight, samples, options, capture, smaa, upscale, straightAlpha, frame,
                reuseReceiverWorkspace,inFlightFrames,temporalUpscaling),additionalInFlightBytes),
            Query(runtime), width, height, samples);
}
