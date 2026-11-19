using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Official NGX quality values. Availability and input resolution are queried from the installed runtime.</summary>
public enum D3D12DlssQuality { Performance, Balanced, Quality, UltraPerformance, UltraQuality, NativeAa }

public sealed record D3D12DlssDeviceQuery(string Adapter, D3D12DlssQuality Quality)
{
    public string? ComponentFileVersion { get; init; }
    public uint SdkApiVersion { get; init; }
    public uint InitResult { get; init; }
    public uint CapabilityResult { get; init; }
    public uint OptimalSettingsResult { get; init; }
    public uint CreateResult { get; init; }
    public bool Available { get; init; }
    public bool NeedsUpdatedDriver { get; init; }
    public string MinimumDriver { get; init; } = "";
    public uint FeatureInitResult { get; init; }
    public int InputWidth { get; init; }
    public int InputHeight { get; init; }
    public bool Initialized { get; init; }
    public string? Failure { get; init; }
}

public sealed class D3D12DlssException(string operation, uint code, D3D12DlssDeviceQuery query)
    : InvalidOperationException($"{operation} failed with NVIDIA NGX result 0x{code:X8}. Actual device query: {query}")
{
    public string Operation { get; } = operation;
    public uint Code { get; } = code;
    public D3D12DlssDeviceQuery DeviceQuery { get; } = query with { Failure = operation };
}

/// <summary>Optional official NGX SR slice. The tiny native bridge links the official NGX SDK static library;
/// the separately licensed nvngx_dlss.dll remains an optional external component. No frame-generation claims.
/// Input is linear LDR RGBA16F with non-inverted depth and unjittered current-to-previous pixel motion.</summary>
internal sealed class D3D12DlssTemporalPass : IDisposable
{
    private readonly D3D12GpuRuntime runtime;
    private readonly int outputWidth, outputHeight;
    private readonly EvaluateDelegate evaluate;
    private readonly CloseDelegate close;
    private nint library, nativeContext;
    private long lastRecordedFrame = -1;
    private bool needsReset = true, disposed;
    internal D3D12DlssDeviceQuery DeviceQuery { get; private set; }
    internal int InputWidth => DeviceQuery.InputWidth;
    internal int InputHeight => DeviceQuery.InputHeight;
    internal long RecordedDispatches { get; private set; }
    internal bool LastReset { get; private set; }
    internal bool RequiresRecreation { get; private set; }

    internal D3D12DlssTemporalPass(D3D12GpuRuntime runtime, string bridgePath, string sdkDirectory,
        int outputWidth, int outputHeight, D3D12DlssQuality quality = D3D12DlssQuality.Quality)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgePath); ArgumentException.ThrowIfNullOrWhiteSpace(sdkDirectory);
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) throw new PlatformNotSupportedException("DLSS NGX requires Windows x64.");
        if (!Path.IsPathFullyQualified(bridgePath) || !string.Equals(Path.GetFileName(bridgePath), "mmd_dlss.dll", StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathFullyQualified(sdkDirectory)) throw new ArgumentException("Absolute mmd_dlss.dll and optional NGX SDK directory paths are required.");
        if (outputWidth is < 1 or > 4096 || outputHeight is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(outputWidth));
        if (!Enum.IsDefined(quality)) throw new ArgumentOutOfRangeException(nameof(quality));
        if (runtime.IsRecording) throw new InvalidOperationException("Create DLSS history outside an open frame.");
        this.runtime = runtime; this.outputWidth = outputWidth; this.outputHeight = outputHeight;
        DeviceQuery = new(runtime.AdapterName, quality);
        ValidateAbi();
        try
        {
            string component = Path.Combine(Path.GetFullPath(sdkDirectory), "nvngx_dlss.dll");
            if (!File.Exists(component)) throw new FileNotFoundException("The optional official NVIDIA DLSS component is missing.", component);
            DeviceQuery = DeviceQuery with { ComponentFileVersion = FileVersionInfo.GetVersionInfo(component).FileVersion };
            library = NativeLibrary.Load(Path.GetFullPath(bridgePath), typeof(D3D12DlssTemporalPass).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            if (Export<AbiDelegate>("mmd_dlss_abi")() != 1) throw new PlatformNotSupportedException("NGX bridge ABI version does not match this module.");
            evaluate = Export<EvaluateDelegate>("mmd_dlss_evaluate"); close = Export<CloseDelegate>("mmd_dlss_close");
            string? environmentLogs = Environment.GetEnvironmentVariable("MMD_DLSS_LOG_DIRECTORY");
            string logs = D3D12RuntimePaths.ResolveDlssLogDirectory(environmentLogs,
                environmentLogs is null ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : null);
            Directory.CreateDirectory(logs);
            uint code = Export<OpenDelegate>("mmd_dlss_open")(runtime.Device.NativePointer, sdkDirectory, logs,
                (uint)outputWidth, (uint)outputHeight, quality, out nativeContext, out var query);
            DeviceQuery = DeviceQuery with { SdkApiVersion = query.SdkVersion, InitResult = query.InitResult,
                CapabilityResult = query.CapabilityResult, OptimalSettingsResult = query.OptimalResult,
                Available = query.Available != 0, NeedsUpdatedDriver = query.NeedsDriver != 0,
                MinimumDriver = $"{query.DriverMajor}.{query.DriverMinor}", FeatureInitResult = query.FeatureInitResult,
                InputWidth = checked((int)query.Width), InputHeight = checked((int)query.Height) };
            Check(code, "NGX Init / capability / optimal-settings query");
            if (query.Abi != 1 || nativeContext == 0 || InputWidth < 1 || InputHeight < 1 || InputWidth > outputWidth || InputHeight > outputHeight)
                throw new InvalidOperationException("NGX bridge ABI or queried input dimensions are invalid.");
            // NGX feature creation can record GPU initialization. Submit it on the owning queue before evaluating.
            var context = runtime.Begin();
            try
            {
                code = Export<CreateDelegate>("mmd_dlss_create")(nativeContext, context.Commands.NativePointer,
                    (uint)outputWidth, (uint)outputHeight, quality);
                DeviceQuery = DeviceQuery with { CreateResult = code };
                Check(code, "NVSDK_NGX_D3D12_CreateFeature");
                runtime.Wait(runtime.Submit());
            }
            finally { runtime.Abort(); }
            DeviceQuery = DeviceQuery with { Initialized = true };
        }
        catch
        {
            if (nativeContext != 0 && close is not null) { runtime.WaitForIdleForTeardown(); close(nativeContext); nativeContext = 0; }
            FreeLibrary(); throw;
        }
    }

    internal static D3D12DlssDeviceQuery Query(D3D12GpuRuntime runtime, string bridgePath, string sdkDirectory,
        int width, int height, D3D12DlssQuality quality = D3D12DlssQuality.Quality)
    {
        try { using var pass = new D3D12DlssTemporalPass(runtime, bridgePath, sdkDirectory, width, height, quality); return pass.DeviceQuery; }
        catch (D3D12DlssException error) { return error.DeviceQuery; }
        catch (Exception error) when (error is DllNotFoundException or FileNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        { return new(runtime.AdapterName, quality) { Failure = error.Message }; }
    }

    internal D3D12GpuTarget Record(D3D12RecordingContext context, D3D12GpuTarget color, D3D12TemporalInputs temporal,
        Vector2 jitterPixels, float frameTimeMilliseconds, bool reset)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(color); ArgumentNullException.ThrowIfNull(temporal);
        if (RequiresRecreation) throw new InvalidOperationException("Recreate native DLSS history after failed or aborted recording.");
        if (!ReferenceEquals(context.Runtime, runtime) || !runtime.IsRecording) throw new InvalidOperationException("DLSS requires its owning runtime's open frame.");
        if (lastRecordedFrame == runtime.FrameId) throw new InvalidOperationException("A DLSS context can execute once per submitted frame.");
        if (!color.Initialized || color.Depth || color.Format != Format.R16G16B16A16_Float || color.Width != InputWidth || color.Height != InputHeight
            || temporal.Width != InputWidth || temporal.Height != InputHeight || temporal.Depth.Description.Format != Format.R32_Float
            || temporal.MotionVectors.Description.Format != Format.R32G32_Float)
            throw new NotSupportedException("DLSS requires matching queried-resolution RGBA16F, R32 depth and RG32 backward pixel motion.");
        if (!float.IsFinite(jitterPixels.X) || !float.IsFinite(jitterPixels.Y) || MathF.Abs(jitterPixels.X) > .5f || MathF.Abs(jitterPixels.Y) > .5f
            || !float.IsFinite(frameTimeMilliseconds) || frameTimeMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(jitterPixels));
        if (temporal.ReactiveMask is {} mask && (mask.Description.Format != Format.R8_UNorm || mask.Description.Width != (ulong)InputWidth || mask.Description.Height != InputHeight))
            throw new NotSupportedException("DLSS bias-current-color mask must be matching R8_UNORM.");
        var commands = context.Commands; commands.UnsetRenderTargets();
        context.Transition(color, ResourceStates.NonPixelShaderResource);
        commands.ResourceBarrierTransition(temporal.Depth, D3D12TemporalInputs.ShaderReadState, ResourceStates.NonPixelShaderResource);
        commands.ResourceBarrierTransition(temporal.MotionVectors, D3D12TemporalInputs.ShaderReadState, ResourceStates.NonPixelShaderResource);
        if (temporal.ReactiveMask is {} reactive) commands.ResourceBarrierTransition(reactive, D3D12TemporalInputs.ShaderReadState, ResourceStates.NonPixelShaderResource);
        var resource = context.Own(context.Device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Texture2D(Format.R16G16B16A16_Float, (uint)outputWidth, (uint)outputHeight, mipLevels: 1, flags: ResourceFlags.AllowUnorderedAccess), ResourceStates.UnorderedAccess));
        var result = new D3D12GpuTarget("$dlss-temporal", resource, default, outputWidth, outputHeight, Format.R16G16B16A16_Float, false) { State = ResourceStates.UnorderedAccess };
        bool actualReset = reset || needsReset;
        runtime.OnAbort(() => RequiresRecreation = true);
        uint code;
        try { code = evaluate(nativeContext, commands.NativePointer, color.Resource.NativePointer, temporal.Depth.NativePointer,
            temporal.MotionVectors.NativePointer, temporal.ReactiveMask?.NativePointer ?? 0, resource.NativePointer,
            jitterPixels.X, jitterPixels.Y, frameTimeMilliseconds, actualReset ? 1u : 0); }
        catch { RequiresRecreation = true; throw; }
        finally
        {
            commands.ResourceBarrierTransition(temporal.Depth, ResourceStates.NonPixelShaderResource, D3D12TemporalInputs.ShaderReadState);
            commands.ResourceBarrierTransition(temporal.MotionVectors, ResourceStates.NonPixelShaderResource, D3D12TemporalInputs.ShaderReadState);
            if (temporal.ReactiveMask is {} restore) commands.ResourceBarrierTransition(restore, ResourceStates.NonPixelShaderResource, D3D12TemporalInputs.ShaderReadState);
        }
        if (Failed(code)) { RequiresRecreation = true; Check(code, "NVSDK_NGX_D3D12_EvaluateFeature"); }
        commands.ResourceBarrierUnorderedAccessView(resource);
        result.Initialized = true; needsReset = false; LastReset = actualReset;
        lastRecordedFrame = runtime.FrameId; RecordedDispatches++;
        return result;
    }

    public void Dispose()
    {
        if (disposed) return;
        if (runtime.IsRecording) throw new InvalidOperationException("Submit or abort the open frame before destroying DLSS history.");
        runtime.WaitForIdleForTeardown(); if (nativeContext != 0) { close(nativeContext); nativeContext = 0; }
        FreeLibrary(); disposed = true;
    }
    private void FreeLibrary() { if (library != 0) { NativeLibrary.Free(library); library = 0; } }
    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    internal static bool Failed(uint result) => (result & 0xFFF00000) == 0xBAD00000;
    private void Check(uint result, string operation) { if (Failed(result)) throw new D3D12DlssException(operation, result, DeviceQuery); }
    internal static void ValidateAbi() { if (Marshal.SizeOf<NativeQuery>() != 64) throw new PlatformNotSupportedException("Unexpected NGX bridge query packing."); }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeQuery
    {
        internal uint Abi, SdkVersion, InitResult, CapabilityResult, Available, NeedsDriver, DriverMajor, DriverMinor;
        internal uint FeatureInitResult, OptimalResult, Width, Height, MinWidth, MinHeight, MaxWidth, MaxHeight;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate uint OpenDelegate(nint device, [MarshalAs(UnmanagedType.LPWStr)] string sdkDirectory,
        [MarshalAs(UnmanagedType.LPWStr)] string logDirectory, uint width, uint height, D3D12DlssQuality quality, out nint context, out NativeQuery query);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint CreateDelegate(nint context, nint commands, uint width, uint height, D3D12DlssQuality quality);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint EvaluateDelegate(nint context, nint commands, nint color, nint depth, nint motion,
        nint responsive, nint output, float jitterX, float jitterY, float frameMilliseconds, uint reset);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CloseDelegate(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint AbiDelegate();
}
