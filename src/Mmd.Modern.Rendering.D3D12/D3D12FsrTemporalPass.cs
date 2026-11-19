using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>
/// Optional FSR SDK 2.3 / upscaler API 4.1.1 GPU recording slice. Keeps the runtime-selected provider and
/// its temporal history alive. Only finite non-inverted perspective depth, unjittered backward pixel motion,
/// and linear, opaque LDR input are accepted by this slice. The caller owns scene coverage validation.
/// This pass changes command-list state; subsequent passes must bind their own heaps/root signatures.
/// </summary>
internal sealed class D3D12FsrTemporalPass : IDisposable
{
    private readonly D3D12GpuRuntime runtime;
    private readonly int renderWidth, renderHeight, outputWidth, outputHeight;
    private readonly NativeMemory createMemory = new();
    private readonly DispatchDelegate dispatch;
    private readonly DestroyDelegate destroy;
    private nint loader, upscaler, nativeContext;
    private long lastRecordedFrame = -1;
    private bool needsReset = true, disposed;
    internal ulong ProviderId { get; }
    internal string ProviderName { get; } = "";
    internal long RecordedDispatches { get; private set; }
    internal bool LastReset { get; private set; }
    internal bool RequiresRecreation { get; private set; }

    internal D3D12FsrTemporalPass(D3D12GpuRuntime runtime, string loaderPath,
        int renderWidth, int renderHeight, int outputWidth, int outputHeight)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(loaderPath);
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) throw new PlatformNotSupportedException("FSR SDK requires Windows x64.");
        if (!Path.IsPathFullyQualified(loaderPath) || !string.Equals(Path.GetFileName(loaderPath), "amd_fidelityfx_loader_dx12.dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An absolute amd_fidelityfx_loader_dx12.dll path is required.", nameof(loaderPath));
        if (renderWidth <= 0 || renderHeight <= 0 || outputWidth < renderWidth || outputHeight < renderHeight)
            throw new ArgumentOutOfRangeException(nameof(renderWidth), "Positive render dimensions and output dimensions at least as large are required.");
        if (runtime.IsRecording) throw new InvalidOperationException("Create FSR history outside a recording frame.");
        this.runtime = runtime; this.renderWidth = renderWidth; this.renderHeight = renderHeight;
        this.outputWidth = outputWidth; this.outputHeight = outputHeight;
        ValidateAbi();
        lock (D3D12FsrNativeLifetime.Gate)
        {
            try
            {
                var flags = DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32;
                string path = Path.GetFullPath(loaderPath);
                upscaler = NativeLibrary.Load(Path.Combine(Path.GetDirectoryName(path)!, "amd_fidelityfx_upscaler_dx12.dll"), typeof(D3D12FsrTemporalPass).Assembly, flags);
                loader = NativeLibrary.Load(path, typeof(D3D12FsrTemporalPass).Assembly, flags);
                var create = Export<CreateDelegate>("ffxCreateContext");
                var query = Export<QueryDelegate>("ffxQuery");
                dispatch = Export<DispatchDelegate>("ffxDispatch"); destroy = Export<DestroyDelegate>("ffxDestroyContext");
                // ffxCreateContext explicitly requires the entire description chain to remain alive until destruction.
                nint version = createMemory.Store(new UpscaleVersion { Header = new(0x1000b), Version = (4u << 22) | (1u << 12) | 1u });
                nint backend = createMemory.Store(new Backend { Header = new(2, version), Device = runtime.Device.NativePointer });
                nint description = createMemory.Store(new UpscaleCreate { Header = new(0x10000, backend), Flags = 1u << 5,
                    RenderWidth = (uint)renderWidth, RenderHeight = (uint)renderHeight, OutputWidth = (uint)outputWidth, OutputHeight = (uint)outputHeight });
                Check(create(ref nativeContext, description, 0), "ffxCreateContext");
                if (nativeContext == 0) throw new InvalidOperationException("FSR returned an empty context.");
                using var queryMemory = new NativeMemory();
                nint providerPointer = queryMemory.Store(new ProviderVersion { Header = new(6) });
                Check(query(ref nativeContext, providerPointer), "ffxQuery(GetProviderVersion)");
                var provider = Marshal.PtrToStructure<ProviderVersion>(providerPointer);
                ProviderId = provider.VersionId; ProviderName = Marshal.PtrToStringUTF8(provider.VersionName) ?? "";
                if (ProviderId == 0 || ProviderName.Length == 0) throw new InvalidOperationException("FSR did not identify its actual provider.");
            }
            catch
            {
                if (nativeContext != 0 && destroy is not null) destroy(ref nativeContext, 0);
                createMemory.Dispose(); FreeLibraries(); throw;
            }
        }
    }

    /// <summary>Output and all input resources survive via the current frame fence. No submission or CPU readback occurs here.</summary>
    internal D3D12GpuTarget Record(D3D12RecordingContext context, D3D12GpuTarget color, D3D12TemporalInputs temporal,
        Vector2 jitterPixels, float frameTimeMilliseconds, bool reset, float cameraNear, float cameraFar, float verticalFovRadians)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (RequiresRecreation) throw new InvalidOperationException("FSR recording was aborted or failed. Dispose and recreate its native history outside a recording frame before reuse.");
        if (!ReferenceEquals(context.Runtime, runtime) || !runtime.IsRecording) throw new InvalidOperationException("FSR requires its owning runtime's open recording context.");
        if (runtime.FrameId == lastRecordedFrame) throw new InvalidOperationException("A temporal context may be dispatched only once per submitted frame.");
        if (!color.Initialized || color.Depth || color.Width != renderWidth || color.Height != renderHeight || color.Format is not (Format.R8G8B8A8_UNorm or Format.R16G16B16A16_Float or Format.R32G32B32A32_Float)
            || temporal.Width != renderWidth || temporal.Height != renderHeight)
            throw new NotSupportedException("FSR requires initialized linear RGBA8 or floating point color and matching render-resolution depth/motion inputs.");
        if (!float.IsFinite(jitterPixels.X) || !float.IsFinite(jitterPixels.Y) || !float.IsFinite(frameTimeMilliseconds) || frameTimeMilliseconds <= 0
            || !float.IsFinite(cameraNear) || !float.IsFinite(cameraFar) || cameraNear <= 0 || cameraFar <= cameraNear
            || !float.IsFinite(verticalFovRadians) || verticalFovRadians <= 0 || verticalFovRadians >= MathF.PI)
            throw new ArgumentOutOfRangeException(nameof(verticalFovRadians), "Finite perspective camera values and positive frame delta are required.");
        context.Commands.UnsetRenderTargets();
        context.Transition(color, D3D12TemporalInputs.ShaderReadState);
        var resource = context.Own(context.Device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Texture2D(color.Format, (uint)outputWidth, (uint)outputHeight, mipLevels: 1, flags: ResourceFlags.AllowUnorderedAccess),
            ResourceStates.UnorderedAccess));
        var result = new D3D12GpuTarget("$fsr-temporal", resource, default, outputWidth, outputHeight, color.Format, false)
            { State = ResourceStates.UnorderedAccess };
        bool actualReset = reset || needsReset;
        var description = new UpscaleDispatch
        {
            Header = new(0x10001), CommandList = context.Commands.NativePointer,
            Color = Wrap(color.Resource, 12), Depth = Wrap(temporal.Depth, 12), MotionVectors = Wrap(temporal.MotionVectors, 12), Output = Wrap(resource, 2),
            Reactive = temporal.ReactiveMask is {} reactive ? Wrap(reactive, 12) : default,
            Jitter = jitterPixels, MotionScale = Vector2.One,
            RenderWidth = (uint)renderWidth, RenderHeight = (uint)renderHeight, OutputWidth = (uint)outputWidth, OutputHeight = (uint)outputHeight,
            FrameTimeDelta = frameTimeMilliseconds, PreExposure = 1, Reset = actualReset ? (byte)1 : (byte)0,
            CameraNear = cameraNear, CameraFar = cameraFar, CameraFov = verticalFovRadians, ViewSpaceToMetersFactor = 1
        };
        using var memory = new NativeMemory();
        uint code = dispatch(ref nativeContext, memory.Store(description));
        if (code != 0) { RequiresRecreation = true; Check(code, "ffxDispatch(Upscale)"); }
        // The SDK restores registered external resources to their incoming states when unregistering them.
        result.Initialized = true; LastReset = actualReset; needsReset = false;
        RecordedDispatches++; lastRecordedFrame = runtime.FrameId;
        // ffxDispatch can advance native initialization flags and CPU-side barrier/history bookkeeping.
        // A reset flag cannot repair GPU initialization commands that never reached the queue.
        runtime.OnAbort(() => RequiresRecreation = true);
        return result;
    }

    public void Dispose()
    {
        if (disposed) return;
        if (runtime.IsRecording) throw new InvalidOperationException("Submit or abort the open GPU frame before destroying FSR history.");
        runtime.WaitForIdleForTeardown(); // Native history/resources and loaded shader code can still be referenced by submitted work.
        lock (D3D12FsrNativeLifetime.Gate)
        {
            if (disposed) return;
            uint code = nativeContext == 0 ? 0 : destroy(ref nativeContext, 0);
            disposed = true; createMemory.Dispose(); FreeLibraries(); Check(code, "ffxDestroyContext");
        }
    }

    private void FreeLibraries()
    { if (loader != 0) { NativeLibrary.Free(loader); loader = 0; } if (upscaler != 0) { NativeLibrary.Free(upscaler); upscaler = 0; } }
    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(loader, name));
    private static void Check(uint code, string operation)
    { if (code != 0) throw new InvalidOperationException($"{operation} failed with FSR SDK return code {code}."); }

    // Minimal C ABI bindings from the pinned AMD MIT headers, commit 60f4ea81909200d8542eca14dccb2628b763a9a3:
    // Kits/FidelityFX/{api/include/ffx_api_types.h,api/include/dx12/ffx_api_dx12.h,upscalers/include/ffx_upscale.h}.
    // See references/rendering-upscale-20261022/fidelityfx-current and its docs/license.md outside this source tree.
    private static ApiResource Wrap(ID3D12Resource resource, uint state)
    {
        var desc = resource.Description;
        uint format = desc.Format switch
        {
            Format.R8G8B8A8_UNorm => 10,
            Format.R16G16B16A16_Float => 4,
            Format.R32G32B32A32_Float => 3,
            Format.R8_UNorm => 25,
            Format.R32_Float => 28,
            Format.R32G32_Float => 6,
            _ => throw new NotSupportedException($"FSR ABI resource format {desc.Format} is outside this slice.")
        };
        return new() { Resource = resource.NativePointer, Description = new() { Type = 2, Format = format, Width = checked((uint)desc.Width),
            Height = checked((uint)desc.Height), Depth = desc.DepthOrArraySize, MipCount = desc.MipLevels, Usage = (desc.Flags & ResourceFlags.AllowUnorderedAccess) != 0 ? 2u : 0u }, State = state };
    }
    private static void ValidateAbi()
    {
        if (Marshal.SizeOf<ApiResourceDescription>() != 32 || Marshal.SizeOf<ApiResource>() != 48 || Marshal.SizeOf<UpscaleDispatch>() != 432
            || Marshal.OffsetOf<UpscaleDispatch>(nameof(UpscaleDispatch.Jitter)).ToInt32() != 360
            || Marshal.OffsetOf<UpscaleDispatch>(nameof(UpscaleDispatch.Reset)).ToInt32() != 408)
            throw new PlatformNotSupportedException("Unexpected FSR SDK C ABI packing.");
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint CreateDelegate(ref nint context, nint description, nint callbacks);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint DestroyDelegate(ref nint context, nint callbacks);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint QueryDelegate(ref nint context, nint description);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint DispatchDelegate(ref nint context, nint description);
    [StructLayout(LayoutKind.Sequential)] private struct Header(ulong type, nint next = 0) { public ulong Type = type; public nint Next = next; }
    [StructLayout(LayoutKind.Sequential)] private struct Backend { public Header Header; public nint Device; }
    [StructLayout(LayoutKind.Sequential)] private struct UpscaleVersion { public Header Header; public uint Version; }
    [StructLayout(LayoutKind.Sequential)] private struct UpscaleCreate { public Header Header; public uint Flags, RenderWidth, RenderHeight, OutputWidth, OutputHeight; public nint Message; }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderVersion { public Header Header; public ulong VersionId; public nint VersionName; }
    [StructLayout(LayoutKind.Sequential)] private struct ApiResourceDescription { public uint Type, Format, Width, Height, Depth, MipCount, Flags, Usage; }
    [StructLayout(LayoutKind.Sequential)] private struct ApiResource { public nint Resource; public ApiResourceDescription Description; public uint State; }
    [StructLayout(LayoutKind.Sequential)] private struct UpscaleDispatch
    {
        public Header Header; public nint CommandList;
        public ApiResource Color, Depth, MotionVectors, Exposure, Reactive, Transparency, Output;
        public Vector2 Jitter, MotionScale;
        public uint RenderWidth, RenderHeight, OutputWidth, OutputHeight;
        public byte EnableSharpening; public float Sharpness, FrameTimeDelta, PreExposure;
        public byte Reset; public float CameraNear, CameraFar, CameraFov, ViewSpaceToMetersFactor; public uint Flags;
    }
    private sealed class NativeMemory : IDisposable
    {
        private readonly List<nint> pointers = [];
        internal nint Store<T>(T value) where T : struct
        { nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>()); pointers.Add(pointer); Marshal.StructureToPtr(value, pointer, false); return pointer; }
        public void Dispose() { foreach (nint pointer in pointers) Marshal.FreeHGlobal(pointer); pointers.Clear(); }
    }
}
