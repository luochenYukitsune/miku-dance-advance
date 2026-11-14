using System.Numerics;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

public enum D3D12XeSsQuality { UltraPerformance = 100, Performance, Balanced, Quality, UltraQuality, UltraQualityPlus, AntiAliasing }

/// <summary>Observed SDK results on the actual D3D12 device. Zero XeFX version is valid on non-Intel devices.</summary>
public sealed record D3D12XeSsDeviceQuery(string Adapter, D3D12XeSsQuality Quality)
{
    public string? RuntimeVersion { get; init; }
    public string? IntelXeFxVersion { get; init; }
    public int? CreateCode { get; init; }
    public int? OptimalDriverCode { get; init; }
    public int? PropertiesCode { get; init; }
    public int? InputResolutionCode { get; init; }
    public int? InitializeCode { get; init; }
    public uint RequiredDescriptors { get; init; }
    public ulong TemporaryBufferBytes { get; init; }
    public ulong TemporaryTextureBytes { get; init; }
    public int InputWidth { get; init; }
    public int InputHeight { get; init; }
    public bool Initialized { get; init; }
    public string? Failure { get; init; }
}

internal sealed class D3D12XeSsException(string operation, int code, D3D12XeSsDeviceQuery query)
    : InvalidOperationException($"{operation} failed with XeSS SDK return code {code}.")
{
    internal string Operation { get; } = operation;
    internal int Code { get; } = code;
    internal D3D12XeSsDeviceQuery DeviceQuery { get; } = query with { Failure = operation };
}

/// <summary>Optional native XeSS-SR runtime slice, using the application's existing command list and fence.
/// Accepts linear LDR RGBA16F and finite non-inverted device depth. SDK history belongs to this instance.
/// Subsequent passes must rebind descriptor heaps, root signatures and pipeline state.</summary>
internal sealed class D3D12XeSsTemporalPass : IDisposable
{
    private readonly D3D12GpuRuntime runtime;
    private readonly int outputWidth, outputHeight;
    private readonly bool responsivePixelMask;
    private ExecuteDelegate execute = null!;
    private ContextDelegate destroy = null!;
    private nint library, nativeContext;
    private ID3D12RootSignature? motionRoot;
    private ID3D12PipelineState? motionPipeline;
    private long lastRecordedFrame = -1;
    private bool needsReset = true, disposed;
    internal D3D12XeSsDeviceQuery DeviceQuery { get; private set; }
    internal int InputWidth => DeviceQuery.InputWidth;
    internal int InputHeight => DeviceQuery.InputHeight;
    internal long RecordedDispatches { get; private set; }
    internal bool LastReset { get; private set; }
    internal bool RequiresRecreation { get; private set; }

    internal D3D12XeSsTemporalPass(D3D12GpuRuntime runtime, string dllPath, int outputWidth, int outputHeight,
        D3D12XeSsQuality quality = D3D12XeSsQuality.Quality, bool responsivePixelMask = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(dllPath);
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) throw new PlatformNotSupportedException("XeSS requires Windows x64.");
        if (!Path.IsPathFullyQualified(dllPath) || !string.Equals(Path.GetFileName(dllPath), "libxess.dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An absolute official libxess.dll path is required.", nameof(dllPath));
        if (outputWidth is < 1 or > 4096 || outputHeight is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(outputWidth));
        if (!Enum.IsDefined(quality)) throw new ArgumentOutOfRangeException(nameof(quality));
        if (runtime.IsRecording) throw new InvalidOperationException("Create XeSS history outside an open GPU frame.");
        this.runtime = runtime; this.outputWidth = outputWidth; this.outputHeight = outputHeight;
        this.responsivePixelMask = responsivePixelMask;
        DeviceQuery = new(runtime.AdapterName, quality);
        ValidateAbi();
        D3D12XeSsDispatchThread.Invoke(() => Initialize(dllPath, quality));
    }

    // Intel's SR guide requires every SDK call on the initialization thread. The shared
    // dispatcher also prevents independent view sessions from entering its global API concurrently.
    private void Initialize(string dllPath, D3D12XeSsQuality quality)
    {
        try
        {
            library = NativeLibrary.Load(Path.GetFullPath(dllPath), typeof(D3D12XeSsTemporalPass).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            destroy = Export<ContextDelegate>("xessDestroyContext");
            execute = Export<ExecuteDelegate>("xessD3D12Execute");
            Check(Export<VersionDelegate>("xessGetVersion")(out var version), "xessGetVersion");
            DeviceQuery = DeviceQuery with { RuntimeVersion = version.ToString() };
            int code = Export<CreateDelegate>("xessD3D12CreateContext")(runtime.Device.NativePointer, out nativeContext);
            DeviceQuery = DeviceQuery with { CreateCode = code };
            Check(code, "xessD3D12CreateContext");
            if (nativeContext == 0) throw new InvalidOperationException("XeSS returned an empty native context.");
            code = Export<ContextDelegate>("xessIsOptimalDriver")(nativeContext);
            DeviceQuery = DeviceQuery with { OptimalDriverCode = code };
            // WARNING_OLD_DRIVER (2) is supported but may degrade quality/performance; report it without brand guessing.
            if (code != 0 && code != 2) Check(code, "xessIsOptimalDriver");
            Check(Export<ContextVersionDelegate>("xessGetIntelXeFXVersion")(nativeContext, out var xeFx), "xessGetIntelXeFXVersion");
            DeviceQuery = DeviceQuery with { IntelXeFxVersion = xeFx.ToString() };
            var output = new NativeSize { X = (uint)outputWidth, Y = (uint)outputHeight };
            code = Export<PropertiesDelegate>("xessGetProperties")(nativeContext, in output, out var properties);
            DeviceQuery = DeviceQuery with { PropertiesCode = code, RequiredDescriptors = properties.RequiredDescriptors,
                TemporaryBufferBytes = properties.BufferBytes, TemporaryTextureBytes = properties.TextureBytes };
            Check(code, "xessGetProperties");
            code = Export<InputResolutionDelegate>("xessGetInputResolution")(nativeContext, in output, quality, out var input);
            DeviceQuery = DeviceQuery with { InputResolutionCode = code, InputWidth = checked((int)input.X), InputHeight = checked((int)input.Y) };
            Check(code, "xessGetInputResolution");
            if (InputWidth < 1 || InputHeight < 1 || InputWidth > outputWidth || InputHeight > outputHeight)
                throw new InvalidOperationException("XeSS returned invalid input dimensions.");
            var init = new InitParameters { Output = output, Quality = quality,
                Flags = (1u << 6) | (responsivePixelMask ? 1u << 3 : 0) };
            code = Export<InitDelegate>("xessD3D12Init")(nativeContext, in init);
            DeviceQuery = DeviceQuery with { InitializeCode = code };
            Check(code, "xessD3D12Init");
            // Shared temporal inputs are current->previous pixel displacements without jitter, matching the official XeSS guide.
            Check(Export<ScaleDelegate>("xessSetVelocityScale")(nativeContext, 1, 1), "xessSetVelocityScale");
            Check(Export<ScaleDelegate>("xessSetJitterScale")(nativeContext, 1, 1), "xessSetJitterScale");
            DeviceQuery = DeviceQuery with { Initialized = true };
        }
        catch
        {
            if (nativeContext != 0 && destroy is not null) destroy(nativeContext);
            nativeContext = 0; FreeLibrary(); throw;
        }
    }

    /// <summary>Performs actual initialization to discover device/driver support; never infers support from the GPU name.</summary>
    internal static D3D12XeSsDeviceQuery Query(D3D12GpuRuntime runtime, string dllPath, int outputWidth, int outputHeight,
        D3D12XeSsQuality quality = D3D12XeSsQuality.Quality, bool responsivePixelMask = false)
    {
        try { using var pass = new D3D12XeSsTemporalPass(runtime, dllPath, outputWidth, outputHeight, quality, responsivePixelMask); return pass.DeviceQuery; }
        catch (D3D12XeSsException error) { return error.DeviceQuery; }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        { return new(runtime.AdapterName, quality) { Failure = error.Message }; }
    }

    internal D3D12GpuTarget Record(D3D12RecordingContext context, D3D12GpuTarget color, D3D12TemporalInputs temporal,
        Vector2 jitterPixels, float exposure, bool reset)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(color); ArgumentNullException.ThrowIfNull(temporal);
        if (RequiresRecreation) throw new InvalidOperationException("XeSS execution failed or recording was aborted. Recreate its native history outside a recording frame.");
        if (!ReferenceEquals(context.Runtime, runtime) || !runtime.IsRecording) throw new InvalidOperationException("XeSS requires its owning runtime's open frame.");
        if (lastRecordedFrame == runtime.FrameId) throw new InvalidOperationException("A XeSS context may execute only once per submitted frame.");
        if (!color.Initialized || color.Depth || color.Format != Format.R16G16B16A16_Float || color.Width != InputWidth || color.Height != InputHeight
            || temporal.Width != InputWidth || temporal.Height != InputHeight || temporal.Depth.Description.Format != Format.R32_Float
            || temporal.MotionVectors.Description.Format != Format.R32G32_Float)
            throw new NotSupportedException("XeSS requires initialized linear LDR RGBA16F and matching SDK input-resolution R32 depth/RG32 backward pixel motion.");
        if (!float.IsFinite(jitterPixels.X) || !float.IsFinite(jitterPixels.Y) || MathF.Abs(jitterPixels.X) > .5f || MathF.Abs(jitterPixels.Y) > .5f
            || !float.IsFinite(exposure) || exposure <= 0) throw new ArgumentOutOfRangeException(nameof(jitterPixels), "XeSS jitter must be within [-.5,.5], with finite positive exposure.");
        if (responsivePixelMask != (temporal.ReactiveMask is not null))
            throw new NotSupportedException("XeSS responsive-mask presence must match its native context creation flags.");
        if (temporal.ReactiveMask is {} mask && (mask.Description.Format != Format.R8_UNorm || mask.Description.Width != (ulong)InputWidth || mask.Description.Height != InputHeight))
            throw new NotSupportedException("XeSS responsive pixel mask must be matching R8_UNORM.");

        var commands = context.Commands;
        commands.UnsetRenderTargets();
        var motion = ConvertMotion(context, temporal);
        context.Transition(color, ResourceStates.NonPixelShaderResource);
        commands.ResourceBarrierTransition(temporal.Depth, D3D12TemporalInputs.ShaderReadState, ResourceStates.NonPixelShaderResource);
        if (temporal.ReactiveMask is {} responsive)
            commands.ResourceBarrierTransition(responsive, D3D12TemporalInputs.ShaderReadState, ResourceStates.NonPixelShaderResource);
        var resource = context.Own(context.Device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Texture2D(Format.R16G16B16A16_Float, (uint)outputWidth, (uint)outputHeight, mipLevels: 1, flags: ResourceFlags.AllowUnorderedAccess), ResourceStates.UnorderedAccess));
        var result = new D3D12GpuTarget("$xess-temporal", resource, default, outputWidth, outputHeight, Format.R16G16B16A16_Float, false)
            { State = ResourceStates.UnorderedAccess };
        bool actualReset = reset || needsReset;
        var parameters = new ExecuteParameters
        {
            Color = color.Resource.NativePointer, Velocity = motion.NativePointer, Depth = temporal.Depth.NativePointer,
            ResponsiveMask = temporal.ReactiveMask?.NativePointer ?? 0, Output = resource.NativePointer,
            JitterX = jitterPixels.X, JitterY = jitterPixels.Y, Exposure = exposure, Reset = actualReset ? 1u : 0,
            InputWidth = (uint)InputWidth, InputHeight = (uint)InputHeight
        };
        runtime.OnAbort(() => RequiresRecreation = true);
        int code;
        try { code = D3D12XeSsDispatchThread.Invoke(() => execute(nativeContext, commands.NativePointer, in parameters)); }
        catch { RequiresRecreation = true; throw; }
        finally
        {
            // XeSS requires these exact incoming states; external inputs are restored for other frame consumers.
            commands.ResourceBarrierTransition(temporal.Depth, ResourceStates.NonPixelShaderResource, D3D12TemporalInputs.ShaderReadState);
            if (temporal.ReactiveMask is {} restore)
                commands.ResourceBarrierTransition(restore, ResourceStates.NonPixelShaderResource, D3D12TemporalInputs.ShaderReadState);
        }
        if (code != 0) { RequiresRecreation = true; Check(code, "xessD3D12Execute"); }
        commands.ResourceBarrierUnorderedAccessView(resource);
        result.Initialized = true; LastReset = actualReset; needsReset = false;
        lastRecordedFrame = runtime.FrameId; RecordedDispatches++;
        return result;
    }

    // The official guide accepts R16G16_FLOAT motion; the shared scene input pass produces R32G32_FLOAT.
    // This same-queue GPU draw only converts representation, preserving signed pixel values without scaling/dilation.
    private ID3D12Resource ConvertMotion(D3D12RecordingContext context, D3D12TemporalInputs temporal)
    {
        EnsureMotionPipeline();
        var device = context.Device; var commands = context.Commands;
        var resource = context.Own(device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Texture2D(Format.R16G16_Float, (uint)InputWidth, (uint)InputHeight, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget));
        var rtv = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.RenderTargetView, 1)));
        var srv = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible)));
        device.CreateRenderTargetView(resource, null, rtv.GetCPUDescriptorHandleForHeapStart());
        device.CreateShaderResourceView(temporal.MotionVectors, null, srv.GetCPUDescriptorHandleForHeapStart());
        commands.SetDescriptorHeaps(srv); commands.SetGraphicsRootSignature(motionRoot!); commands.SetPipelineState(motionPipeline!);
        commands.SetGraphicsRootDescriptorTable(0, srv.GetGPUDescriptorHandleForHeapStart());
        commands.OMSetRenderTargets(rtv.GetCPUDescriptorHandleForHeapStart()); commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0, 0, InputWidth, InputHeight); commands.RSSetScissorRect(new RawRect(0, 0, InputWidth, InputHeight));
        commands.DrawInstanced(3, 1, 0, 0); commands.UnsetRenderTargets();
        commands.ResourceBarrierTransition(resource, ResourceStates.RenderTarget, ResourceStates.NonPixelShaderResource);
        return resource;
    }

    private void EnsureMotionPipeline()
    {
        if (motionPipeline is not null) return;
        const string source = """
            Texture2D<float2> Motion : register(t0);
            float4 VS(uint id : SV_VertexID) : SV_Position {
                float2 uv = float2((id << 1) & 2, id & 2); return float4(uv * float2(2,-2) + float2(-1,1),0,1);
            }
            float2 PS(float4 pos : SV_Position) : SV_Target { return Motion.Load(int3(pos.xy,0)); }
            """;
        var vertex = Compiler.Compile(source, "VS", "xess-motion-format.hlsl", "vs_5_1").ToArray();
        var pixel = Compiler.Compile(source, "PS", "xess-motion-format.hlsl", "ps_5_1").ToArray();
        runtime.ShaderCompilations += 2;
        var root = runtime.Device.CreateRootSignature<ID3D12RootSignature>(new RootSignatureDescription(RootSignatureFlags.None,
            [new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)), ShaderVisibility.Pixel)]), RootSignatureVersion.Version10);
        try
        {
            motionPipeline = runtime.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
            {
                RootSignature = root, VertexShader = vertex, PixelShader = pixel,
                BlendState = BlendDescription.Opaque, SampleMask = uint.MaxValue, RasterizerState = RasterizerDescription.CullNone,
                DepthStencilState = DepthStencilDescription.None, PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = [Format.R16G16_Float], SampleDescription = new(1, 0)
            });
            motionRoot = root; runtime.PipelineCreations++;
        }
        catch { root.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (disposed) return;
        if (runtime.IsRecording) throw new InvalidOperationException("Submit or abort the GPU frame before disposing XeSS history.");
        runtime.WaitForIdle();
        int code = D3D12XeSsDispatchThread.Invoke(() =>
        {
            int result = nativeContext == 0 ? 0 : destroy(nativeContext);
            nativeContext = 0; FreeLibrary(); return result;
        });
        disposed = true; motionPipeline?.Dispose(); motionRoot?.Dispose();
        Check(code, "xessDestroyContext");
    }

    private void FreeLibrary() { if (library != 0) { NativeLibrary.Free(library); library = 0; } }
    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    private void Check(int code, string operation) { if (code != 0) throw new D3D12XeSsException(operation, code, DeviceQuery); }

    // Minimal bindings from Intel MIT headers xess.h / xess_d3d12.h at de0fb9c1c510661c571164e1418ceca8101dab69.
    // Runtime binaries have Intel's separate license. No native DLL is required until this optional pass is constructed.
    internal static void ValidateAbi()
    {
        if (Marshal.SizeOf<NativeVersion>() != 8 || Marshal.SizeOf<NativeProperties>() != 24 || Marshal.SizeOf<InitParameters>() != 64
            || Marshal.SizeOf<ExecuteParameters>() != 136 || Marshal.OffsetOf<ExecuteParameters>(nameof(ExecuteParameters.DescriptorHeap)).ToInt32() != 120
            || Marshal.OffsetOf<ExecuteParameters>(nameof(ExecuteParameters.JitterX)).ToInt32() != 48)
            throw new PlatformNotSupportedException("Unexpected XeSS x64 C ABI packing.");
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct NativeVersion
    { public ushort Major, Minor, Patch, Reserved; public override readonly string ToString() => $"{Major}.{Minor}.{Patch}"; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct NativeSize { public uint X, Y; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct NativeProperties { public uint RequiredDescriptors; public ulong BufferBytes, TextureBytes; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct InitParameters
    {
        public NativeSize Output; public D3D12XeSsQuality Quality; public uint Flags, CreationNodeMask, VisibleNodeMask;
        public nint BufferHeap; public ulong BufferOffset; public nint TextureHeap; public ulong TextureOffset; public nint PipelineLibrary;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct ExecuteParameters
    {
        public nint Color, Velocity, Depth, ExposureTexture, ResponsiveMask, Output;
        public float JitterX, JitterY, Exposure; public uint Reset, InputWidth, InputHeight;
        public NativeSize ColorBase, MotionBase, DepthBase, ResponsiveBase, Reserved, OutputBase;
        public nint DescriptorHeap; public uint DescriptorOffset;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int VersionDelegate(out NativeVersion version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ContextVersionDelegate(nint context, out NativeVersion version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateDelegate(nint device, out nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ContextDelegate(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PropertiesDelegate(nint context, in NativeSize output, out NativeProperties properties);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InputResolutionDelegate(nint context, in NativeSize output, D3D12XeSsQuality quality, out NativeSize input);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitDelegate(nint context, in InitParameters parameters);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ScaleDelegate(nint context, float x, float y);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ExecuteDelegate(nint context, nint commands, in ExecuteParameters parameters);
}
