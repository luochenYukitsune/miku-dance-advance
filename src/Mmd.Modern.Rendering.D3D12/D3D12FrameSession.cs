using Mmd.Modern.Core;
using Vortice.Direct3D12;
namespace Mmd.Modern.Rendering.D3D12;

/// <summary>A retained GPU image. Native resources stay inside the rendering assembly; dispose the lease when consumed.</summary>
public sealed class D3D12FrameOutput:IDisposable
{
    public int Width {get;}
    public int Height {get;}
    public long FrameId {get;}
    public D3D12FrameStatistics Statistics {get;}
    internal ID3D12Resource Resource {get;}
    internal D3D12FrameOutput(int width,int height,long frameId,D3D12FrameStatistics statistics,ID3D12Resource resource)
    {Width=width;Height=height;FrameId=frameId;Statistics=statistics;Resource=resource;}
    private bool disposed;
    public void Dispose(){if(disposed)return;disposed=true;Resource.Dispose();}
}

/// <summary>Persistent complete-frame recording session. Render retains GPU output; Capture alone requests CPU pixels.</summary>
public sealed class D3D12FrameSession:IDisposable
{
    internal D3D12GpuRuntime Runtime {get;}
    private readonly D3D12FrameRenderer renderer;
    private readonly object prewarmGate = new();
    private CancellationTokenSource? prewarmCts;
    private Task prewarmTask = Task.CompletedTask;
    private bool disposed;
    public string AdapterName=>Runtime.AdapterName;
    public long AdapterLuid=>Runtime.Device.AdapterLuid;
    public bool IsWarp=>Runtime.IsWarp;
    public bool DebugRequested=>Runtime.DebugRequested;
    public bool DebugLayerEnabled=>Runtime.DebugLayerEnabled;
    public bool DebugInfoQueueAvailable=>Runtime.DebugQueue is not null;
    public D3D12FrameStatistics? Statistics=>renderer.Statistics;
    public D3D12CaptureMemoryStatistics? CaptureMemoryStatistics=>renderer.CaptureMemoryStatistics;
    public D3D12VisibilityStatistics? VisibilityStatistics=>renderer.VisibilityStatistics;
    public D3D12RayGiStatistics? RayGiStatistics=>renderer.RayGiStatistics;
    public D3D12NativeLightStatistics? NativeLightStatistics=>renderer.NativeLightStatistics;
    public D3D12TemporalUpscalingStatistics? TemporalUpscalingStatistics=>renderer.TemporalUpscalingStatistics;
    public int EditorOverlayDrawCalls=>renderer.EditorOverlayDrawCalls;
    public string? RayGiBackendIdentity=>renderer.RayGiBackendIdentity;
    public D3D12RayGiCapabilities QueryRayGiCapabilities()=>renderer.QueryRayGiCapabilities();
    public D3D12XeSsDeviceQuery QueryXeSsCapabilities(int displayWidth,int displayHeight,D3D12TemporalUpscalingOptions? options=null)
        =>renderer.QueryXeSsCapabilities(displayWidth,displayHeight,options);
    public D3D12DlssDeviceQuery QueryDlssCapabilities(int displayWidth,int displayHeight,D3D12TemporalUpscalingOptions? options=null)
        =>renderer.QueryDlssCapabilities(displayWidth,displayHeight,options);
    public IReadOnlyList<PostEffectPassRecord> LastPasses=>renderer.LastPasses;
    public IReadOnlyList<PostEffectTargetRecord> LastTargets=>renderer.LastTargets;
    public IReadOnlyList<D3D12RecordingRecord> LastRecordings=>renderer.LastRecordings;
    internal bool IsPipelineLibraryLoadedFromDisk => Runtime.PipelineLibrary.LoadedFromDisk;
    internal int PipelineLibraryHits => Runtime.PipelineLibrary.LibraryHits;
    internal string? PipelineLibraryCachePath => Runtime.PipelineLibrary.CacheFilePath;
    public D3D12FrameSession() : this(null) { }
    internal D3D12FrameSession(string? pipelineCacheDirectory){Runtime=new(pipelineCacheDirectory);renderer=new(Runtime);}
    public D3D12FrameOutput Render(OriginalFrameScene frame,int width,int height,RayGiOptions? rayGiOptions=null,D3D12SpatialUpscalingOptions? upscaling=null,D3D12TemporalUpscalingOptions? temporalUpscaling=null,D3D12SurfaceStyleMap? surfaceStyles=null,D3D12DiffusionOptions? diffusion=null)
        =>renderer.RecordGpu(frame,width,height,rayGiOptions,null,upscaling,temporalUpscaling,surfaceStyles,diffusion);
    public RenderedFrame Capture(OriginalFrameScene frame,int width,int height,RayGiOptions? rayGiOptions=null,D3D12SpatialUpscalingOptions? upscaling=null,D3D12TemporalUpscalingOptions? temporalUpscaling=null,D3D12SurfaceStyleMap? surfaceStyles=null,D3D12DiffusionOptions? diffusion=null,D3D12CaptureMemoryOptions? captureMemory=null)
        =>renderer.Render(frame,width,height,rayGiOptions,upscaling,temporalUpscaling,surfaceStyles,diffusion,captureMemory);
    internal D3D12FrameOutput RenderForPresent(OriginalFrameScene frame,int width,int height,RayGiOptions? rayGiOptions,
        Action<D3D12RecordingContext,D3D12GpuTarget> appendPresent,D3D12SpatialUpscalingOptions? upscaling=null,D3D12TemporalUpscalingOptions? temporalUpscaling=null,D3D12SurfaceStyleMap? surfaceStyles=null,D3D12DiffusionOptions? diffusion=null)
        =>renderer.RecordGpu(frame,width,height,rayGiOptions,appendPresent,upscaling,temporalUpscaling,surfaceStyles,diffusion);
    public Task PrewarmAsync(D3D12ShaderWarmupSpec spec, int maxParallelism = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed), this);
        DxcShaderCompiler? compiler = null;
        if (spec.Gi)
        {
            try { compiler = Runtime.GetOrCreateDxcCompiler(); } catch { }
        }
        return D3D12ShaderPrewarmer.PrewarmAsync(spec, maxParallelism, compiler, cancellationToken);
    }
    internal void StartBackgroundPrewarm(D3D12ShaderWarmupSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        CancellationTokenSource? prior;
        CancellationTokenSource next;
        lock (prewarmGate)
        {
            if (disposed) return;
            prior = prewarmCts;
            next = new();
            prewarmCts = next;
            prewarmTask = PrewarmAsync(spec, 0, next.Token);
        }
        try { prior?.Cancel(); prior?.Dispose(); } catch { }
    }
    public void WaitForIdle()=>Runtime.WaitForIdle();
    public void Dispose()
    {
        CancellationTokenSource? cts;
        Task task;
        lock (prewarmGate)
        {
            if (disposed) return;
            disposed = true;
            cts = prewarmCts;
            prewarmCts = null;
            task = prewarmTask;
            prewarmTask = Task.CompletedTask;
        }
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            try { task.GetAwaiter().GetResult(); } catch { }
            cts.Dispose();
        }
        renderer.Dispose();Runtime.Dispose();
    }
}
