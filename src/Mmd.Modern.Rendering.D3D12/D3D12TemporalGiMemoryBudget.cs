using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Pre-submit reservation for GI-only temporal history banks and the complete spatial-sample workspace.
/// Committed sizes come from the driver; the existing DXGI policy protects cached scene assets and SDK internals.</summary>
internal static class D3D12TemporalGiMemoryBudget
{
    internal const int HistoryBufferCount = 4;
    internal const int HistoryBytesPerPixel = HistoryBufferCount * 16;
    internal const int MaximumHistoryGenerations = D3D12SsaaGiMemoryBudget.MaximumInFlightFrames + 1;

    internal readonly record struct Estimate(ulong HistoryBytes, ulong HistoryWindowBytes, ulong PerFrameBytes, ulong InFlightBytes)
    {
        internal int InFlightFrames { get; init; } = D3D12SsaaGiMemoryBudget.MaximumInFlightFrames;
        internal int HistoryGenerations => InFlightFrames + 1;
    }

    internal readonly record struct Admission(Estimate Reservation, D3D12SsaaGiMemoryBudget.Budget Budget,
        double RetirementMilliseconds)
    {
        internal bool BoundedFlight => Reservation.InFlightFrames == 1;
    }

    internal static Estimate FromResourceSizes(ulong historyBufferBytes, ulong workspaceBytes, ulong previousTrianglesBytes = 0,
        int spatialSamples = 1)
    {
        if (spatialSamples is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(spatialSamples));
        if (historyBufferBytes == 0) throw new ArgumentOutOfRangeException(nameof(historyBufferBytes));
        ulong history = checked(historyBufferBytes * HistoryBufferCount * (ulong)spatialSamples);
        // The current owner plus all three frame slots can retain different generations, including
        // every independent bank. Spatial samples of a frame share its immutable triangle buffer,
        // so allow for it once per history generation, not once per sample.
        ulong retained = checked((history + previousTrianglesBytes) * MaximumHistoryGenerations);
        return new(history, retained, workspaceBytes,
            checked(retained + workspaceBytes * D3D12SsaaGiMemoryBudget.MaximumInFlightFrames));
    }

    internal static void RequireFits(Estimate estimate, D3D12SsaaGiMemoryBudget.Budget budget, int width, int height,
        int spatialSamples = 1)
    {
        if (estimate.InFlightBytes <= budget.AvailableBytes) return;
        string source = budget.FromDxgi ? "DXGI local-memory budget" : "conservative fallback budget (DXGI unavailable)";
        throw new D3D12GiMemoryBudgetExceededException($"Temporal GI at {width}x{height} with {spatialSamples} spatial samples requires an estimated "
            + $"{estimate.HistoryBytes / (1024d * 1024):F1} MiB per history set / {estimate.InFlightBytes / (1024d * 1024):F1} MiB "
            + $"for {estimate.HistoryGenerations} history generations and {estimate.InFlightFrames} in-flight frames; "
            + $"only {budget.AvailableBytes / (1024d * 1024):F1} MiB fits the application GI reservation using the {source}. "
            + "Choose fewer spatial samples, a lower render resolution or explicitly select Spatial denoising. Quality was not changed and no GPU frame was begun.",
            estimate.InFlightBytes, budget.AvailableBytes, spatialSamples);
    }

    internal static Estimate ForBoundedFlight(Estimate estimate)
    {
        if (estimate.InFlightFrames != D3D12SsaaGiMemoryBudget.MaximumInFlightFrames)
            throw new ArgumentException("Bounded-flight admission requires the complete three-slot estimate.", nameof(estimate));
        // WaitForIdle retires every slot's new/old-history leases and frame workspace. Only
        // the current history owner survives. The next recording owns a new set and consumes
        // that previous owner: two complete history generations and one recording workspace.
        ulong retained = checked(estimate.HistoryWindowBytes / MaximumHistoryGenerations * 2);
        // Other reservations have their own cache/slot lifetime policy. Keep their complete
        // original window unchanged rather than assuming this GI optimization shortens it.
        ulong additional = checked(estimate.InFlightBytes - estimate.HistoryWindowBytes
            - estimate.PerFrameBytes * D3D12SsaaGiMemoryBudget.MaximumInFlightFrames);
        return new(estimate.HistoryBytes, retained, estimate.PerFrameBytes,
            checked(retained + estimate.PerFrameBytes + additional)) { InFlightFrames = 1 };
    }

    internal static Admission Admit(Estimate estimate, Func<D3D12SsaaGiMemoryBudget.Budget> queryBudget,
        Action retireFrames, int width, int height, int spatialSamples = 1, bool allowBoundedFlight = false,
        bool forceBoundedFlight = false)
    {
        ArgumentNullException.ThrowIfNull(queryBudget);
        ArgumentNullException.ThrowIfNull(retireFrames);
        if (forceBoundedFlight)
        {
            if (!allowBoundedFlight) throw new ArgumentException("Forced bounded flight must be explicitly allowed.", nameof(forceBoundedFlight));
            var selected = ForBoundedFlight(estimate);
            var retirement = System.Diagnostics.Stopwatch.StartNew();
            retireFrames();
            double milliseconds = retirement.Elapsed.TotalMilliseconds;
            var available = queryBudget();
            RequireFits(selected, available, width, height, spatialSamples);
            return new(selected, available, milliseconds);
        }
        var budget = queryBudget();
        if (estimate.InFlightBytes <= budget.AvailableBytes) return new(estimate, budget, 0);
        if (!allowBoundedFlight) RequireFits(estimate, budget, width, height, spatialSamples);
        var bounded = ForBoundedFlight(estimate);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        // This must happen on EVERY request admitted with the smaller window, even when the
        // previous bounded request has already completed. Completed slots still own resources
        // until retirement. Fence-complete retirement follows the MiniEngine allocator pattern.
        retireFrames();
        double retirementMilliseconds = timer.Elapsed.TotalMilliseconds;
        budget = queryBudget(); // The former slot allocations must no longer consume DXGI headroom.
        // CurrentUsage can still include the surviving history and reusable caches. Comparing
        // the whole two-generation window against additional headroom counts that owner again;
        // this remains conservative and never credits unknown/driver-private allocations.
        RequireFits(bounded, budget, width, height, spatialSamples);
        return new(bounded, budget, retirementMilliseconds);
    }

    internal static Estimate Measure(D3D12GpuRuntime runtime, int width, int height, int displayWidth, int displayHeight,
        RayGiOptions options, bool capture, bool straightAlpha = false, OriginalFrameScene? frame = null, bool temporalUpscaling = false,
        int spatialSamples = 1, bool reuseReceiverWorkspace = false)
    {
        if (spatialSamples is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(spatialSamples));
        if (width is < 1 or > 4096 || height is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(width));
        if (displayWidth is < 1 or > 4096 || displayHeight is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(displayWidth));
        ulong Allocation(ResourceDescription description)
        {
            ulong bytes = runtime.Device.GetResourceAllocationInfo(0, [description]).SizeInBytes;
            if (bytes == 0 || bytes == ulong.MaxValue)
                throw new NotSupportedException("The device could not estimate the requested temporal GI resource allocation.");
            return bytes;
        }
        ulong Buffer(ulong bytes, ResourceFlags flags = ResourceFlags.None) => Allocation(ResourceDescription.Buffer(bytes, flags));
        bool halfRes = options.PreviewAcceleration && width >= 2 && height >= 2;
        int traceWidth = halfRes ? (width + 1) / 2 : width;
        int traceHeight = halfRes ? (height + 1) / 2 : height;
        ulong signalBytes = checked((ulong)traceWidth * (ulong)traceHeight * (HistoryBytesPerPixel / HistoryBufferCount));
        ulong historyBuffer = Buffer(signalBytes, ResourceFlags.AllowUnorderedAccess);
        bool smaa = frame?.Options?.Antialiasing == FrameAntialiasing.Smaa1X;
        bool spatialUpscaling = !temporalUpscaling && (width != displayWidth || height != displayHeight);
        // Reuse the common receiver/shadow/AS preflight so its driver measurements stay authoritative.
        // The common estimator accepts the exact sample count. Shared shadows,
        // acceleration structures, output and accumulation are already counted once by this estimator.
        // The 1x path deliberately retains the float SSAA accumulation and extra color image (safe excess).
        var common = D3D12SsaaGiMemoryBudget.Measure(runtime, width, height, displayWidth, displayHeight, spatialSamples,
            options with { Denoiser = RayGiDenoiser.Spatial }, capture, smaa, spatialUpscaling, straightAlpha, frame,reuseReceiverWorkspace,
            temporalUpscaling:temporalUpscaling);
        ulong workspace = common.PerFrameBytes;
        if (capture && Environment.GetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS") is not null)
            workspace = checked(workspace + 2UL * (ulong)spatialSamples * Buffer(signalBytes)); // Signal and moments readbacks per bank.
        ulong previousTriangles = 0;
        if (frame is not null && !frame.Surface.Snapshot.IsEmpty)
        {
            previousTriangles = Buffer(checked((ulong)(frame.Surface.Snapshot.Indices.Length / 3) * (ulong)Marshal.SizeOf<RayGiTriangleGpu>()));
            // History now references the existing scene triangle upload. Keep the former staging
            // allowance as a conservative workspace reserve until peak memory is measured.
            workspace = checked(workspace + previousTriangles);
        }
        // Scene geometry/material/texture caches and vendor-private persistent allocations are
        // excluded from this workspace; DXGI CurrentUsage and the half-budget reserve cover them.
        return FromResourceSizes(historyBuffer, workspace, previousTriangles, spatialSamples);
    }

    /// <summary>Frame-owned temporal-AA coverage, motion, transfer and SDK output workspace.
    /// Applies to either GI denoiser; GI-only history banks and vendor-private persistent memory are separate.</summary>
    internal static ulong MeasureTemporalUpscalingWorkspace(D3D12GpuRuntime runtime,int width,int height,
        int displayWidth,int displayHeight,OriginalFrameScene? frame)
    {
        ulong Allocation(ResourceDescription description)
        {
            ulong bytes=runtime.Device.GetResourceAllocationInfo(0,[description]).SizeInBytes;
            if(bytes is 0 or ulong.MaxValue)throw new NotSupportedException("Device cannot estimate temporal reconstruction workspace.");
            return bytes;
        }
        ulong Buffer(ulong bytes)=>Allocation(ResourceDescription.Buffer(bytes));
        ulong Image(Format format,ResourceFlags flags,int w,int h)
            =>Allocation(ResourceDescription.Texture2D(format,(uint)w,(uint)h,mipLevels:1,flags:flags));
        var depthFormat=frame?.Options?.GroundShadow?.UseStencil==true?Format.D24_UNorm_S8_UInt:Format.D32_Float;
        ulong workspace=checked(Image(Format.R32_Float,ResourceFlags.AllowRenderTarget,width,height)
            +Image(Format.R32G32_Float,ResourceFlags.AllowRenderTarget,width,height)
            +Image(Format.R8_UNorm,ResourceFlags.AllowRenderTarget,width,height)
            +2*Image(depthFormat,ResourceFlags.AllowDepthStencil,width,height)
            +Image(Format.R16G16_Float,ResourceFlags.AllowRenderTarget,width,height) // XeSS motion conversion (conservative for FSR).
            +Image(Format.R16G16B16A16_Float,ResourceFlags.AllowRenderTarget,width,height)
            +Image(Format.R16G16B16A16_Float,ResourceFlags.AllowUnorderedAccess,displayWidth,displayHeight)
            +Image(Format.R8G8B8A8_UNorm,ResourceFlags.AllowRenderTarget,displayWidth,displayHeight));
        if(frame is not null&&!frame.Surface.Snapshot.IsEmpty)
        {
            var scene=frame.Surface.Snapshot;
            workspace=checked(workspace+2*Buffer(checked((ulong)scene.VertexCount*44))
                +2*Buffer(checked((ulong)scene.Indices.Length*4)));
            if(frame.Outline is not null)
                workspace=checked(workspace+2*Buffer(checked((ulong)frame.Outline.VertexCount*44)));
        }
        return workspace;
    }

    internal static Estimate WithAdditionalReservation(Estimate estimate,ulong bytes)
        =>estimate with {InFlightBytes=checked(estimate.InFlightBytes+bytes)};

    internal static Admission Validate(D3D12GpuRuntime runtime, int width, int height, int displayWidth, int displayHeight,
        RayGiOptions options, bool capture, bool straightAlpha = false, OriginalFrameScene? frame = null, bool temporalUpscaling = false,
        int spatialSamples = 1,ulong additionalInFlightBytes=0, bool allowBoundedFlight = false,
        bool forceBoundedFlight = false, bool reuseReceiverWorkspace = false)
        => Admit(WithAdditionalReservation(Measure(runtime, width, height, displayWidth, displayHeight, options, capture, straightAlpha, frame, temporalUpscaling,
            spatialSamples,reuseReceiverWorkspace),additionalInFlightBytes), () => D3D12SsaaGiMemoryBudget.Query(runtime), runtime.WaitForIdle,
            width, height, spatialSamples, allowBoundedFlight,forceBoundedFlight);
}
