using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

public sealed record PostEffectTargetRecord(string Name, int Width, int Height, string Format)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public (int Width, int Height) Size => (Width, Height);
}
public sealed record PostEffectPassRecord(string Name, string VertexEntry, string PixelEntry,
    string VertexSha256, string PixelSha256, string Target, int Width, int Height, IReadOnlyList<string> SampledTextures);
public sealed record D3D12RecordingRecord(long FrameId,string Channel,string Target,string ColorFormat,string DepthFormat,string DeviceIdentity,string CommandListIdentity);

/// <summary>Typed modern frame recording with built-in GI, alpha, AA and upscaling passes.
/// MME parsing and graph execution are deliberately absent. CPU pixels are requested only by Capture.</summary>
public sealed class D3D12FrameRenderer : IDisposable
{
    private readonly D3D12GpuRuntime runtime;
    private readonly bool ownsRuntime;
    private readonly D3D12GpuCache cache;
    private readonly D3D12FrameDescriptorHeaps frameDescriptors;
    private readonly D3D12OriginalStyleRenderer originalRecorder;
    private D3D12RayGiRenderer? rayGiRenderer;
    private D3D12SmaaPass? smaaPass;
    private D3D12Fsr1Pass? fsr1Pass;
    private D3D12DiffusionPass? diffusionPass;
    private D3D12ExposurePass? exposurePass;
    private D3D12SpatialAccumulationPass? spatialPass;
    private D3D12AlphaResolvePass? alphaResolvePass;
    private D3D12LightGizmoPass? lightGizmoPass;
    private D3D12FsrTemporalPass? temporalPass;
    private D3D12XeSsTemporalPass? xeSsTemporalPass;
    private D3D12DlssTemporalPass? dlssTemporalPass;
    private D3D12TemporalInputPass? temporalInputs;
    private D3D12TemporalColorTransfer? temporalColor;
    private readonly D3D12TemporalHistory temporalHistory = new();
    private D3D12SurfaceStyleMap? lastSubmittedSurfaceStyles;
    private (int DisplayWidth, int DisplayHeight, string Loader, D3D12TemporalUpscalingMode Mode, D3D12FsrQualityPreset Quality,
        D3D12XeSsQuality XeSsQuality, D3D12DlssQuality DlssQuality, string? DlssSdkDirectory)? temporalConfiguration;
    public D3D12TemporalUpscalingStatistics? TemporalUpscalingStatistics { get; private set; }
    public D3D12CaptureMemoryStatistics? CaptureMemoryStatistics { get; private set; }
    internal D3D12VisibilityStatistics? VisibilityStatistics=>originalRecorder.LastVisibilityStatistics;
    private readonly bool ownsOriginalRecorder;
    private ID3D12Device device=>runtime.Device;
    private ID3D12GraphicsCommandList commands=>runtime.Commands;
    private ID3D12InfoQueue? debugQueue=>runtime.DebugQueue;
    private bool disposed,rendering;
    internal D3D12FrameOutput? LastGpuOutput {get;private set;}
    internal D3D12FrameStatistics? Statistics {get;private set;}
    public string AdapterName { get; }
    public bool IsWarp { get; }
    public FeatureLevel FeatureLevel { get; }
    public bool HasDebugQueue => debugQueue is not null;
    public int LastSceneDrawCalls { get; private set; }
    public int LastPassDrawCalls { get; private set; }
    public int LastReadbackCount { get; private set; }
    public int LastSubmissionCount { get; private set; }
    public int EditorOverlayDrawCalls { get; private set; }
    public IReadOnlyList<PostEffectTargetRecord> LastTargets { get; private set; } = [];
    public IReadOnlyList<PostEffectPassRecord> LastPasses { get; private set; } = [];
    public IReadOnlyList<string> LastDebugMessages { get; private set; } = [];
    public IReadOnlyList<D3D12RecordingRecord> LastRecordings {get;private set;}=[];
    public D3D12RayGiStatistics? RayGiStatistics => rayGiRenderer?.LastStatistics;
    public D3D12NativeLightStatistics? NativeLightStatistics => originalRecorder.LastNativeLightStatistics;
    public D3D12GpuSkinningStatistics? GpuSkinningStatistics => cache.ExistingGpuSkinning?.Statistics;
    public string? RayGiBackendIdentity => rayGiRenderer?.LastBackendIdentity;
    public D3D12RayGiCapabilities QueryRayGiCapabilities()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        return (rayGiRenderer ??= new(runtime, cache)).QueryCapabilities();
    }
    /// <summary>Initializes the selected XeSS quality at the requested output size on this device,
    /// then releases the probe. SDK input dimensions and failure codes are returned unchanged.</summary>
    public D3D12XeSsDeviceQuery QueryXeSsCapabilities(int displayWidth, int displayHeight, D3D12TemporalUpscalingOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        options ??= new() { Mode = D3D12TemporalUpscalingMode.XeSs };
        options.Validate(displayWidth, displayHeight);
        if (options.Mode != D3D12TemporalUpscalingMode.XeSs) throw new ArgumentException("Select XeSS for its device query.", nameof(options));
        string loader = options.LoaderPath ?? Environment.GetEnvironmentVariable("MMD_XESS_RUNTIME_PATH")
            ?? throw new InvalidOperationException("Set LoaderPath to the installed official libxess.dll before querying XeSS.");
        return D3D12XeSsTemporalPass.Query(runtime, loader, displayWidth, displayHeight, options.XeSsQuality, responsivePixelMask: true);
    }
    /// <summary>Queries and initializes the explicitly selected NGX quality on this device, then releases the probe.
    /// This may submit feature initialization work; it does not submit an application frame or claim evaluation.</summary>
    public D3D12DlssDeviceQuery QueryDlssCapabilities(int displayWidth, int displayHeight, D3D12TemporalUpscalingOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        options ??= new() { Mode = D3D12TemporalUpscalingMode.Dlss };
        options.Validate(displayWidth, displayHeight);
        if (options.Mode != D3D12TemporalUpscalingMode.Dlss) throw new ArgumentException("Select DLSS for its device query.", nameof(options));
        var paths = options.ResolveDlssPaths();
        return D3D12DlssTemporalPass.Query(runtime, paths.Bridge, paths.SdkDirectory, displayWidth, displayHeight, options.DlssQuality);
    }

    public D3D12FrameRenderer():this(new D3D12GpuRuntime(),true) {}
    internal D3D12FrameRenderer(D3D12GpuRuntime runtime,bool ownsRuntime=false,D3D12OriginalStyleRenderer? originalRecorder=null)
    {
        this.runtime=runtime;this.ownsRuntime=ownsRuntime;cache=new(runtime);frameDescriptors=new(runtime);
        this.originalRecorder=originalRecorder??new D3D12OriginalStyleRenderer(runtime);ownsOriginalRecorder=originalRecorder is null;
        AdapterName=runtime.AdapterName;IsWarp=runtime.IsWarp;FeatureLevel=runtime.FeatureLevel;
    }

    public RenderedFrame Render(OriginalFrameScene frame,int width,int height,
        RayGiOptions? rayGiOptions=null,D3D12SpatialUpscalingOptions? upscaling=null,D3D12TemporalUpscalingOptions? temporalUpscaling=null,D3D12SurfaceStyleMap? surfaceStyles=null,D3D12DiffusionOptions? diffusion=null,D3D12CaptureMemoryOptions? captureMemory=null)
        =>RenderCore(frame,width,height,true,null,rayGiOptions,upscaling,temporalUpscaling,surfaceStyles,diffusion,captureMemory);

    internal D3D12FrameOutput RecordGpu(OriginalFrameScene frame,int width,int height,RayGiOptions? rayGiOptions,
        Action<D3D12RecordingContext,D3D12GpuTarget>? appendPresent,D3D12SpatialUpscalingOptions? upscaling=null,D3D12TemporalUpscalingOptions? temporalUpscaling=null,D3D12SurfaceStyleMap? surfaceStyles=null,D3D12DiffusionOptions? diffusion=null)
    {
        RenderCore(frame,width,height,false,appendPresent,rayGiOptions,upscaling,temporalUpscaling,surfaceStyles,diffusion);
        return LastGpuOutput!;
    }

    private RenderedFrame RenderCore(OriginalFrameScene frame,int width,int height,bool capture,
        Action<D3D12RecordingContext,D3D12GpuTarget>? appendPresent,RayGiOptions? rayGiOptions=null,D3D12SpatialUpscalingOptions? upscaling=null,D3D12TemporalUpscalingOptions? temporalUpscaling=null,D3D12SurfaceStyleMap? surfaceStyles=null,D3D12DiffusionOptions? diffusion=null,D3D12CaptureMemoryOptions? captureMemory=null)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ArgumentNullException.ThrowIfNull(frame);
        if (rendering) throw new InvalidOperationException("Reentrant/concurrent rendering is unsupported.");
        captureMemory??=D3D12CaptureMemoryOptions.Default;
        captureMemory.Validate();
        CaptureMemoryStatistics=null;
        diffusion?.ValidateFrame(frame); // Before captures, SDK recreation or any recording.
        bool diffusionActive=diffusion?.IsActive==true;
        float exposureEv=frame.Lighting?.Settings.ExposureEv??0;
        rayGiOptions??=frame.Options?.RayGi;
        rayGiOptions=D3D12SceneLighting.ResolveGi(frame,rayGiOptions);
        surfaceStyles?.Validate(frame,rayGiOptions);
        surfaceStyles=surfaceStyles?.CaptureFaceTextures(frame);
        var scene=frame.Surface.Snapshot;
        ulong radialReservation=D3D12SsaaGiMemoryBudget.MeasureEndfieldSeeds(runtime,scene,surfaceStyles?.HasConsumedEndfieldHair==true);
        ulong matcapReservation=MeasureEndfieldMatcapMemory(runtime,scene,surfaceStyles);
        ulong metallicReservation=MeasureGenshinMetallicMemory(runtime,scene,surfaceStyles);
        ulong baseImageReservation=MeasureEndfieldBaseImageMemory(runtime,scene,surfaceStyles);
        // These request-only resets must not become the next AA configuration comparison.
        // Commit the requested GI settings and actual families only after successful Submit.
        var historyRayGiOptions=rayGiOptions;
        bool surfaceStyleChanged=!D3D12SurfaceStyleMap.Equivalent(lastSubmittedSurfaceStyles,surfaceStyles);
        if(surfaceStyleChanged&&rayGiOptions is not null)
            rayGiOptions=rayGiOptions with {ResetTemporalHistory=true};
        D3D12OriginalStyleRenderer.ValidateSubTexture(frame,rayGiOptions,temporalUpscaling);
        if(rayGiOptions is not null)D3D12RayGiRenderer.Validate(frame,rayGiOptions);
        if((rayGiOptions?.Enabled==true||D3D12OriginalStyleRenderer.HasNativeLights(frame)||frame.UsesSceneLighting)&&frame.Options is null)
            frame=new OriginalFrameScene(frame.Surface,frame.Outline,frame.OutlineOptions,new(OriginalAlphaBlendMode.Normal,true,false),frame.Materials,frame.Lighting,frame.EditorOverlay);
        bool emptyScene=scene.Indices.Length==0;
        bool retainTriangleScratch=!emptyScene&&D3D12RayGiRenderer.UsesTriangleScratch(scene,rayGiOptions,capture);
        rayGiRenderer?.BeginFrame(retainTriangleScratch);
        if (width is < 1 or > 4096 || height is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(width));
        ulong diffusionReservation=D3D12DiffusionMemoryBudget.Measure(runtime,width,height,diffusionActive,
            alphaCoverage:frame.Options?.AlphaOutput==FrameAlphaOutput.Straight);
        ulong additionalReservation=checked(radialReservation+matcapReservation+metallicReservation+baseImageReservation+diffusionReservation
            +D3D12ExposurePass.Measure(runtime,width,height,exposureEv!=0));
        bool additionalValidatedEarly=additionalReservation>0&&(!capture||captureMemory.Mode==D3D12CaptureMemoryMode.Default);
        if(additionalValidatedEarly)D3D12SsaaGiMemoryBudget.RequireFits(new(0,0,additionalReservation),D3D12SsaaGiMemoryBudget.Query(runtime),width,height,1);
        upscaling??=D3D12SpatialUpscalingOptions.Off;
        var dimensions=upscaling.GetDimensions(width,height);
        temporalUpscaling??=D3D12TemporalUpscalingOptions.Off;
        if(surfaceStyleChanged)temporalUpscaling=temporalUpscaling with {ResetHistory=true};
        temporalUpscaling.Validate(width,height);
        var temporalDimensions=temporalUpscaling.Mode is D3D12TemporalUpscalingMode.XeSs or D3D12TemporalUpscalingMode.Dlss
            ? new D3D12SpatialUpscalingDimensions(width,height,width,height) : temporalUpscaling.GetDimensions(width,height);
        bool temporal=temporalUpscaling.Mode!=D3D12TemporalUpscalingMode.Off;
        bool straightAlpha=frame.Options?.AlphaOutput==FrameAlphaOutput.Straight;
        if(straightAlpha)D3D12AlphaResolvePass.ValidateCoverage(frame);
        if(straightAlpha&&(temporal||upscaling.Mode!=D3D12SpatialUpscalingMode.Off))
            throw new NotSupportedException("Straight-alpha output with spatial/temporal upscaling requires an explicit coverage contract and is not supported.");
        if(straightAlpha&&!capture&&frame.EditorOverlay is {Lines.Count:>0})
            throw new NotSupportedException("Straight-alpha GPU output requires editor overlays to be disabled; their compositor uses the opaque viewport contract.");
        if(temporal)
        {
            if(upscaling.Mode!=D3D12SpatialUpscalingMode.Off || (frame.Options?.SpatialSamples??1)!=1 || frame.Options?.Antialiasing==FrameAntialiasing.Smaa1X)
                throw new NotSupportedException("Temporal reconstruction requires spatial upscaling, SMAA and spatial supersampling to be explicitly disabled.");
            if(frame.Options?.BackgroundColor is {W:not 1f})
                throw new NotSupportedException("Temporal reconstruction currently requires an opaque composited background.");
            var camera=frame.Options?.CameraOverride??scene.Camera;
            // The locked FSR provider reconstructs perspective view Z from near/far/FOV.
            // XeSS consumes device depth for motion dilation and receives no projection parameters.
            if(temporalUpscaling.Mode==D3D12TemporalUpscalingMode.Fsr&&camera.VerticalFovDegrees is null)
                throw new NotSupportedException("Temporal FSR currently requires a perspective camera; its installed provider has no orthographic projection contract.");
            if(temporalUpscaling.Mode==D3D12TemporalUpscalingMode.Dlss&&camera.VerticalFovDegrees is null)
                throw new NotSupportedException("Temporal DLSS currently requires a perspective camera; orthographic reconstruction has not been validated.");
            if(camera.ProjectionJitterPixels!=Vector2.Zero)
                throw new NotSupportedException("Temporal reconstruction owns the camera jitter sequence; supply an unjittered effective camera.");
            dimensions=temporalDimensions;
        }
        bool upscale=upscaling.Mode==D3D12SpatialUpscalingMode.Fsr1;
        if(upscale&&frame.Options?.BackgroundColor is { W: not 1f })
            throw new NotSupportedException("FSR1 currently requires an opaque background; transparent coverage reconstruction is not implemented.");
        int displayWidth=width,displayHeight=height;
        width=dimensions.RenderWidth;height=dimensions.RenderHeight;
        int spatialSamples=frame.Options?.SpatialSamples??1;
        var smaaQuality=frame.Options?.SmaaQuality??SmaaQuality.High;
        D3D12TemporalGiMemoryBudget.Admission? temporalGiAdmission=null;
        bool captureLowMemory=false;
        double captureRetirementMilliseconds=0;
        void SelectCaptureMemory()
        {
            if(!capture)return;
            bool temporalGi=D3D12RayGiRenderer.IsTemporalEnabled(rayGiOptions);
            ulong? MeasureGi(bool lowMemory)
            {
                if(rayGiOptions?.Enabled!=true)return null;
                // The default single-sample API did not require this SSAA preflight. Auto and
                // LowMemory explicitly request it so their selected policy can be explained.
                if(!temporalGi&&spatialSamples==1&&captureMemory.Mode==D3D12CaptureMemoryMode.Default)return null;
                if(temporalGi)
                {
                    var estimate=D3D12TemporalGiMemoryBudget.WithAdditionalReservation(
                        D3D12TemporalGiMemoryBudget.Measure(runtime,width,height,displayWidth,displayHeight,rayGiOptions!,capture,
                            straightAlpha,frame,temporal,spatialSamples,reuseReceiverWorkspace:lowMemory),additionalReservation);
                    return lowMemory?D3D12TemporalGiMemoryBudget.ForBoundedFlight(estimate).InFlightBytes:estimate.InFlightBytes;
                }
                return D3D12SsaaGiMemoryBudget.WithAdditionalReservation(
                    D3D12SsaaGiMemoryBudget.Measure(runtime,width,height,displayWidth,displayHeight,spatialSamples,rayGiOptions!,capture,
                        frame.Options?.Antialiasing==FrameAntialiasing.Smaa1X,upscale,straightAlpha,frame,
                        reuseReceiverWorkspace:lowMemory,inFlightFrames:lowMemory?1:D3D12SsaaGiMemoryBudget.MaximumInFlightFrames,
                        temporalUpscaling:temporal),
                    additionalReservation).InFlightBytes;
            }
            ulong? ordinary=MeasureGi(false);
            var budget=D3D12SsaaGiMemoryBudget.Query(runtime);
            var selected=captureMemory.Select(ordinary,budget.AvailableBytes);
            captureLowMemory=selected==D3D12CaptureMemoryMode.LowMemory;
            if(captureLowMemory&&!temporalGi)
            {
                var retirement=System.Diagnostics.Stopwatch.StartNew();
                // Capture has already waited for completion, but completed slots retain their
                // resources until retirement. Do this before EVERY reduced-window recording.
                runtime.WaitForIdle();
                captureRetirementMilliseconds=retirement.Elapsed.TotalMilliseconds;
                budget=D3D12SsaaGiMemoryBudget.Query(runtime);
            }
            CaptureMemoryStatistics=new(captureMemory.Mode,selected,ordinary,captureLowMemory?MeasureGi(true):ordinary,
                budget.AvailableBytes,captureLowMemory?1:D3D12SsaaGiMemoryBudget.MaximumInFlightFrames,captureRetirementMilliseconds);
        }
        void RecordTemporalCaptureMemory()
        {
            if(!capture||CaptureMemoryStatistics is null||temporalGiAdmission is not {} admission)return;
            captureRetirementMilliseconds+=admission.RetirementMilliseconds;
            CaptureMemoryStatistics=CaptureMemoryStatistics with
            {
                SelectedEstimatedGiReservationBytes=admission.Reservation.InFlightBytes,
                AvailableReservationBytes=admission.Budget.AvailableBytes,
                InFlightFrames=admission.Reservation.InFlightFrames,
                RetirementMilliseconds=captureRetirementMilliseconds
            };
        }
        if(!temporal)SelectCaptureMemory();
        if((spatialSamples>1||captureLowMemory) && rayGiOptions?.Enabled==true&&!D3D12RayGiRenderer.IsTemporalEnabled(rayGiOptions))
            D3D12SsaaGiMemoryBudget.Validate(runtime,width,height,displayWidth,displayHeight,spatialSamples,rayGiOptions,capture,
                frame.Options?.Antialiasing==FrameAntialiasing.Smaa1X,upscale,straightAlpha,frame,additionalInFlightBytes:additionalReservation,
                reuseReceiverWorkspace:captureLowMemory,inFlightFrames:captureLowMemory?1:D3D12SsaaGiMemoryBudget.MaximumInFlightFrames);
        // In the SSAA/GI-only path the render dimensions are already authoritative. Reject
        // the complete reservation before changing any retained temporal-AA SDK owner.
        if(!temporal&&D3D12RayGiRenderer.IsTemporalEnabled(rayGiOptions))
            temporalGiAdmission=D3D12TemporalGiMemoryBudget.Validate(runtime,width,height,displayWidth,displayHeight,rayGiOptions!,capture,
                straightAlpha,frame,spatialSamples:spatialSamples,additionalInFlightBytes:additionalReservation,
                allowBoundedFlight:capture,forceBoundedFlight:captureLowMemory,reuseReceiverWorkspace:captureLowMemory);
        if(!temporal)RecordTemporalCaptureMemory();
        bool temporalReset=false;string temporalResetReason="None";int temporalPhase=0,temporalPhaseCount=1;Vector2 temporalJitter=default;
        if(temporal)
        {
            bool xess=temporalUpscaling.Mode==D3D12TemporalUpscalingMode.XeSs;
            bool dlss=temporalUpscaling.Mode==D3D12TemporalUpscalingMode.Dlss;
            var dlssPaths=dlss?temporalUpscaling.ResolveDlssPaths():default;
            string loader=dlss?dlssPaths.Bridge:temporalUpscaling.LoaderPath??Environment.GetEnvironmentVariable(xess?"MMD_XESS_RUNTIME_PATH":"MMD_FSR_LOADER_PATH")
                ??throw new InvalidOperationException(xess
                    ? "Set LoaderPath to the installed official libxess.dll before enabling XeSS."
                    : "Set LoaderPath to the installed amd_fidelityfx_loader_dx12.dll before enabling temporal FSR.");
            var configuration=(displayWidth,displayHeight,Path.GetFullPath(loader),temporalUpscaling.Mode,temporalUpscaling.QualityPreset,
                temporalUpscaling.XeSsQuality,temporalUpscaling.DlssQuality,dlss?Path.GetFullPath(dlssPaths.SdkDirectory):null);
            bool needsRecreation=temporalPass?.RequiresRecreation==true||xeSsTemporalPass?.RequiresRecreation==true||dlssTemporalPass?.RequiresRecreation==true;
            if((dlss?dlssTemporalPass is null:xess?xeSsTemporalPass is null:temporalPass is null)||needsRecreation||temporalConfiguration!=configuration)
            {
                string reason=needsRecreation?"AbortedFrame":temporalConfiguration is null?temporalHistory.InvalidationReason
                    :temporalConfiguration.Value.Mode!=temporalUpscaling.Mode?"ModeChanged":"ConfigurationChanged";
                DisposeTemporalPasses();
                temporalConfiguration=null;temporalHistory.Invalidate(reason);
                if(dlss)dlssTemporalPass=new(runtime,loader,dlssPaths.SdkDirectory,displayWidth,displayHeight,temporalUpscaling.DlssQuality);
                else if(xess)xeSsTemporalPass=new(runtime,loader,displayWidth,displayHeight,temporalUpscaling.XeSsQuality,responsivePixelMask:true);
                else temporalPass=new(runtime,loader,width,height,displayWidth,displayHeight);
                temporalConfiguration=configuration;
            }
            if(xess||dlss)
            {
                width=dlss?dlssTemporalPass!.InputWidth:xeSsTemporalPass!.InputWidth;
                height=dlss?dlssTemporalPass!.InputHeight:xeSsTemporalPass!.InputHeight;
                dimensions=new(width,height,displayWidth,displayHeight);
            }
            temporalReset=temporalHistory.ShouldReset(frame,temporalUpscaling,historyRayGiOptions);
            temporalResetReason=temporalHistory.ResetReason(frame,temporalUpscaling,historyRayGiOptions);
            temporalPhase=temporalReset?0:temporalHistory.Phase;
            temporalPhaseCount=Math.Max(1,(int)(8f*displayWidth*displayWidth/((float)width*width)));
            temporalJitter=D3D12TemporalHistory.Jitter(temporalPhase);
            var effectiveCamera=(frame.Options?.CameraOverride??scene.Camera) with {ProjectionJitterPixels=temporalJitter};
            if(frame.Options is {} options)
                frame=new(frame.Surface,frame.Outline,frame.OutlineOptions,options with {CameraOverride=effectiveCamera},frame.Materials,frame.Lighting,frame.EditorOverlay);
            else
            {
                // A null frame Options selects the established original-style surface technique.
                // Change only its immutable camera; manufacturing frame options would select a different shader/blend path.
                scene=scene.WithCamera(effectiveCamera);
                frame=new(new OriginalStyleScene(scene,frame.Surface.Materials,frame.Surface.Profile),frame.Outline,frame.OutlineOptions,
                    null,frame.Materials,frame.Lighting,frame.EditorOverlay);
            }
        }
        else if(temporalPass is not null||xeSsTemporalPass is not null||dlssTemporalPass is not null)
        {DisposeTemporalPasses();temporalConfiguration=null;temporalHistory.Invalidate("ModeChanged");}
        if(temporal)SelectCaptureMemory();
        if(temporal&&captureLowMemory&&rayGiOptions?.Enabled==true&&!D3D12RayGiRenderer.IsTemporalEnabled(rayGiOptions))
            D3D12SsaaGiMemoryBudget.Validate(runtime,width,height,displayWidth,displayHeight,spatialSamples,rayGiOptions,capture,
                false,false,straightAlpha,frame,additionalInFlightBytes:additionalReservation,
                reuseReceiverWorkspace:true,inFlightFrames:1,temporalUpscaling:true);
        if(temporal&&D3D12RayGiRenderer.IsTemporalEnabled(rayGiOptions))
            temporalGiAdmission=D3D12TemporalGiMemoryBudget.Validate(runtime,width,height,displayWidth,displayHeight,rayGiOptions!,capture,
                straightAlpha,frame,temporalUpscaling:temporal,spatialSamples:spatialSamples,additionalInFlightBytes:additionalReservation,
                allowBoundedFlight:capture,forceBoundedFlight:captureLowMemory,reuseReceiverWorkspace:captureLowMemory);
        if(temporal)RecordTemporalCaptureMemory();
        if(additionalReservation>0&&!additionalValidatedEarly)
            D3D12SsaaGiMemoryBudget.RequireFits(new(0,0,additionalReservation),D3D12SsaaGiMemoryBudget.Query(runtime),width,height,1);
        TemporalUpscalingStatistics=null;
        LastSceneDrawCalls = LastPassDrawCalls = LastReadbackCount = LastSubmissionCount = 0;
        EditorOverlayDrawCalls=0;
        LastPasses=[];
        long recordAllocatedStart=GC.GetAllocatedBytesForCurrentThread();
        var recordTimer=System.Diagnostics.Stopwatch.StartNew();
        var context=runtime.Begin(); T Own<T>(T resource) where T:IDisposable=>context.Own(resource);
        originalRecorder.ResetSceneRecordCpuStatistics();
        double beginFrameMilliseconds=recordTimer.Elapsed.TotalMilliseconds-runtime.LastSlotWaitMilliseconds;
        rendering=true;
        double geometryMilliseconds=0,textureMilliseconds=0,sceneMilliseconds=0,submitMilliseconds=0;
        double frameTargetMilliseconds=0,antialiasingMilliseconds=0,editorVisibilityMilliseconds=0,editorOverlayMilliseconds=0,appendPresentMilliseconds=0;
        long sceneAllocatedBytes=0,editorVisibilityAllocatedBytes=0;
        var antialiasing=frame.Options?.Antialiasing??FrameAntialiasing.Off;
        int antialiasingDrawCalls=0,spatialResolveDrawCalls=0,upscalingDrawCalls=0;
        var recordingRecords=new List<D3D12RecordingRecord>();
        void Recorded(string channel,D3D12GpuTarget color,D3D12GpuTarget depth)=>recordingRecords.Add(new(runtime.FrameId,channel,color.Name,color.Format.ToString(),depth.Format.ToString(),runtime.DeviceIdentity,runtime.CommandListIdentity));
        try
        {
            cache.BeginFrame(!emptyScene&&rayGiOptions is {Enabled:true,Strength:>0});
            long frameTargetStart=System.Diagnostics.Stopwatch.GetTimestamp();
            D3D12ShadowTargets.ReleaseInactive(cache,frame);
            debugQueue?.ClearStoredMessages();
            const int textureSlots = 0;
            int nativeDescriptors=D3D12OriginalStyleRenderer.HasNativeLights(frame)?2:0;
            int matcapDescriptors=surfaceStyles?.HasConsumedSurfaceMatcap==true?surfaceStyles.Count:0;
            int matcapSlotBase=checked(textureSlots+scene.Textures.Count+4+nativeDescriptors);
            int metallicDescriptors=surfaceStyles?.HasConsumedGenshinMetallic==true?surfaceStyles.Count:0;
            int metallicSlotBase=checked(matcapSlotBase+matcapDescriptors);
            int baseImageDescriptors=surfaceStyles?.HasConsumedEndfieldBaseImage==true?surfaceStyles.Count:0;
            int baseImageSlotBase=checked(metallicSlotBase+metallicDescriptors);
            int giNextSlot=checked(baseImageSlotBase+baseImageDescriptors);
            // Each recorded GI receiver keeps its own SRV pair for the whole submission. Later
            // SSAA samples must not overwrite descriptors referenced by earlier queued work.
            int giRecordings=spatialSamples;
            int giDescriptors=rayGiOptions?.Enabled==true?checked(2*giRecordings):0;
            int descriptorCount=checked(giNextSlot+giDescriptors);
            var heaps=frameDescriptors.Get(descriptorCount);
            var rtvHeap=heaps.Rtv;var dsvHeap=heaps.Dsv;var srvHeap=heaps.Srv;
            runtime.DescriptorHighWater=Math.Max(runtime.DescriptorHighWater,descriptorCount);
            int srvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            int rtvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
            int dsvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
            var targets = new Dictionary<string, D3D12GpuTarget>(StringComparer.OrdinalIgnoreCase); int colorIndex = 0, depthIndex = 0;
            D3D12GpuTarget Allocate(string name, int w, int h, Format format, bool depth)
            {
                var resource = Own(device.CreateCommittedResource(HeapType.Default, ResourceDescription.Texture2D(format, (uint)w, (uint)h,
                    mipLevels: 1, flags: depth ? ResourceFlags.AllowDepthStencil : ResourceFlags.AllowRenderTarget),
                    depth ? ResourceStates.DepthWrite : ResourceStates.RenderTarget));
                var handle = depth ? dsvHeap.GetCPUDescriptorHandleForHeapStart().Offset(depthIndex++, dsvStride)
                    : rtvHeap.GetCPUDescriptorHandleForHeapStart().Offset(colorIndex++, rtvStride);
                if (depth) device.CreateDepthStencilView(resource, null, handle); else device.CreateRenderTargetView(resource, null, handle);
                var target = new D3D12GpuTarget(name, resource, handle, w, h, format, depth); targets.Add(name, target); return target;
            }
            var output = Allocate("$output", width, height, straightAlpha?Format.R16G16B16A16_Float:Format.R8G8B8A8_UNorm, false);
            var defaultDepth = Allocate("$depth", width, height, frame.Options?.GroundShadow?.UseStencil==true?Format.D24_UNorm_S8_UInt:Format.D32_Float, true);
            LastTargets = targets.Values.Select(t => new PostEffectTargetRecord(t.Name, t.Width, t.Height, t.Format.ToString())).ToArray();
            commands.SetDescriptorHeaps(srvHeap);
            frameTargetMilliseconds=System.Diagnostics.Stopwatch.GetElapsedTime(frameTargetStart).TotalMilliseconds;
            var stageTimer=System.Diagnostics.Stopwatch.StartNew();
            var geometry=emptyScene?null:GetGeometry(scene);var vb=geometry?.Vertex;var ib=geometry?.Index;
            geometryMilliseconds=stageTimer.Elapsed.TotalMilliseconds;stageTimer.Restart();
            void UploadTexture(SceneTexture texture,int slot)
                =>device.CreateShaderResourceView(cache.Texture(texture),null,srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(slot,srvStride));
            UploadTexture(new("white", 1, 1, [255,255,255,255]), textureSlots);
            UploadTexture(new SceneTexture("white-sphere",1,1,[255,255,255,255]), textureSlots + 1);
            for (int i = 0; i < scene.Textures.Count; i++) UploadTexture(scene.Textures[i], textureSlots + i + 2);
            for(int i=0;i<matcapDescriptors;i++)
                if(surfaceStyles!.EffectiveSurfaceMatcap(i) is {} matcap)
                    device.CreateShaderResourceView(cache.TextureColor(scene.Textures[matcap.MatcapCombinedTextureIndex],matcap.MatcapEncoding),null,
                        srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(matcapSlotBase+i,srvStride));
            for(int i=0;i<metallicDescriptors;i++)
                if(surfaceStyles!.EffectiveGenshinMetallic(i) is {} metal)
                    device.CreateShaderResourceView(cache.TextureColor(scene.Textures[metal.MatcapCombinedTextureIndex],metal.MatcapEncoding),null,
                        srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(metallicSlotBase+i,srvStride));
            for(int i=0;i<baseImageDescriptors;i++)
                if(surfaceStyles!.EffectiveEndfieldMatcap(i)?.Palette?.BaseImage is {ConsumesBaseTexture:true} baseImage)
                    device.CreateShaderResourceView(cache.TextureColor(scene.Textures[baseImage.BaseCombinedTextureIndex!.Value],baseImage.BaseEncoding),null,
                        srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(baseImageSlotBase+i,srvStride));
            textureMilliseconds=stageTimer.Elapsed.TotalMilliseconds;
            D3D12OriginalStyleRenderer.PreparedFrame? preparedFrame=null;
            D3D12RayGiWorkspace? sharedGiWorkspace=null;
            D3D12GpuTarget current = output, currentDepth = defaultDepth;
            void Transition(D3D12GpuTarget t, ResourceStates state)
            { if (t.State != state) { commands.ResourceBarrierTransition(t.Resource, t.State, state); t.State = state; } }
            void BindTarget(bool depth)
            {
                commands.UnsetRenderTargets(); Transition(current, ResourceStates.RenderTarget);
                if (depth) { Transition(currentDepth, ResourceStates.DepthWrite); commands.OMSetRenderTargets(current.Handle, currentDepth.Handle); }
                else commands.OMSetRenderTargets(current.Handle);
                commands.RSSetViewport(0, 0, current.Width, current.Height, 0, 1);
                commands.RSSetScissorRect(new RawRect(0, 0, current.Width, current.Height));
            }
            void Scene(int sampleIndex=0)
            {
                long allocatedStart=GC.GetAllocatedBytesForCurrentThread();
                var timer=System.Diagnostics.Stopwatch.StartNew();
                originalRecorder.BeginSceneRecordCpu();
                try
                {
                    SceneCore(sampleIndex);
                }
                finally
                {
                    originalRecorder.EndSceneRecordCpu();
                    sceneMilliseconds+=timer.Elapsed.TotalMilliseconds;
                    sceneAllocatedBytes+=GC.GetAllocatedBytesForCurrentThread()-allocatedStart;
                }
            }
            void SceneCore(int sampleIndex)
            {
                // All actors may be hidden. Keep the real cleared GPU target and built-in passes,
                // without allocating empty geometry or tracing hidden surfaces.
                if(emptyScene){originalRecorder.ResetNativeLightStatistics();return;}
                if(!current.Initialized || !currentDepth.Initialized) throw new InvalidOperationException("Scene targets must be explicitly cleared before their first draw.");
                if (current.Format != (straightAlpha?Format.R16G16B16A16_Float:Format.R8G8B8A8_UNorm)) throw new InvalidOperationException("Scene injection requires the selected scene color target format.");
                if (currentDepth.Width < current.Width || currentDepth.Height < current.Height) throw new InvalidOperationException("Scene depth target is smaller than scene color target.");
                // Ground stencil is initialized independently from depth before its first use.
                if(frame.Options?.GroundShadow is {UseStencil:true} && !currentDepth.StencilInitialized)
                {
                    if(currentDepth.Format!=Format.D24_UNorm_S8_UInt)throw new InvalidOperationException("Stencil ground requires D24S8 target.");
                    Transition(currentDepth,ResourceStates.DepthWrite);
                    commands.ClearDepthStencilView(currentDepth.Handle,ClearFlags.Stencil,1,0);currentDepth.StencilInitialized=true;
                }
                BindTarget(true);
                Recorded("original-frame",current,currentDepth);
                D3D12RayGiRenderer.Prepared? gi=null;
                if(rayGiOptions?.Enabled==true)
                {
                    if(giNextSlot>descriptorCount-2)throw new InvalidOperationException("The GI recording exceeds its frame descriptor reservation.");
                    if(D3D12OriginalStyleRenderer.HasSubTexture(frame))
                        LastSceneDrawCalls+=originalRecorder.PrepareGiFrame(context,frame,currentDepth.Format,vb!,ib!,cache,ref preparedFrame);
                    rayGiRenderer??=new(runtime,cache);
                    if(captureLowMemory)sharedGiWorkspace??=new(context,current.Width,current.Height);
                    gi=rayGiRenderer.Record(context,frame,current,currentDepth,rayGiOptions,originalRecorder,vb!,ib!,srvHeap,srvStride,textureSlots,giNextSlot,capture,preparedFrame,sampleIndex,spatialSamples,sharedGiWorkspace);
                    giNextSlot+=2;
                }
                LastSceneDrawCalls+=originalRecorder.RecordBorrowed(context,current,currentDepth,frame,vb!,ib!,srvHeap,srvStride,textureSlots,cache,ref preparedFrame,gi,surfaceStyles);
            }
            var background=frame.Options?.BackgroundColor??new Vector4(20f/255,24f/255,32f/255,1);
            if(straightAlpha)background=new(background.X*background.W,background.Y*background.W,background.Z*background.W,background.W);
            commands.ClearRenderTargetView(output.Handle,new Color4(background.X,background.Y,background.Z,background.W));
            commands.ClearDepthStencilView(defaultDepth.Handle,ClearFlags.Depth|(defaultDepth.Format==Format.D24_UNorm_S8_UInt?ClearFlags.Stencil:0),1,0);
            output.Initialized=true; defaultDepth.Initialized=true;
            defaultDepth.StencilInitialized=defaultDepth.Format==Format.D24_UNorm_S8_UInt;
            if(spatialSamples==1)Scene();
            else
            {
                var sourceFrame=frame;
                var sourceOptions=sourceFrame.Options!;
                var camera=sourceOptions.CameraOverride??scene.Camera;
                var accumulation=(spatialPass??=new(runtime,cache)).Begin(context,width,height,spatialSamples,output.Format);
                for(int sample=0;sample<spatialSamples;sample++)
                {
                    if(sample>0)
                    {
                        Transition(output,ResourceStates.RenderTarget);
                        commands.ClearRenderTargetView(output.Handle,new Color4(background.X,background.Y,background.Z,background.W));
                        commands.ClearDepthStencilView(defaultDepth.Handle,ClearFlags.Depth|(defaultDepth.Format==Format.D24_UNorm_S8_UInt?ClearFlags.Stencil:0),1,0);
                    }
                    frame=new(sourceFrame.Surface,sourceFrame.Outline,sourceFrame.OutlineOptions,
                        sourceOptions with {CameraOverride=camera with {ProjectionJitterPixels=camera.ProjectionJitterPixels+SpatialSampling.JitterPixels(spatialSamples,sample)}},
                        sourceFrame.Materials,sourceFrame.Lighting,sourceFrame.EditorOverlay);
                    commands.SetDescriptorHeaps(srvHeap);
                    Scene(sample);
                    accumulation.Accumulate(context,output,sample);
                    Recorded("ssaa:accumulate",output,defaultDepth);
                }
                frame=sourceFrame;
                output=accumulation.Resolve(context);
                spatialResolveDrawCalls=1;
                Recorded("ssaa:resolve",output,defaultDepth);
                LastTargets=LastTargets.Append(new(output.Name,width,height,output.Format.ToString())).ToArray();
            }
            if(antialiasing==FrameAntialiasing.Smaa1X)
            {
                long antialiasingStart=System.Diagnostics.Stopwatch.GetTimestamp();
                var aa=(smaaPass??=new(runtime,cache)).Record(context,output,smaaQuality,alphaCoverage:straightAlpha);
                Recorded("smaa:edges",aa.Edges,defaultDepth);
                Recorded("smaa:weights",aa.Weights,defaultDepth);
                Recorded("smaa:neighborhood",aa.Output,defaultDepth);
                LastTargets=LastTargets.Concat(new[]{aa.Edges,aa.Weights,aa.Output}.Select(t=>new PostEffectTargetRecord(t.Name,t.Width,t.Height,t.Format.ToString()))).ToArray();
                output=aa.Output;antialiasingDrawCalls=3;
                antialiasingMilliseconds=System.Diagnostics.Stopwatch.GetElapsedTime(antialiasingStart).TotalMilliseconds;
            }
            if(upscale)
            {
                var reconstructed=(fsr1Pass??=new(runtime,cache)).Record(context,output,displayWidth,displayHeight,upscaling);
                Recorded("fsr1:easu",reconstructed.Easu,defaultDepth);
                if(reconstructed.Rcas is {} rcas)Recorded("fsr1:rcas",rcas,defaultDepth);
                LastTargets=LastTargets.Concat(new[]{reconstructed.Easu,reconstructed.Rcas}.OfType<D3D12GpuTarget>()
                    .Select(t=>new PostEffectTargetRecord(t.Name,t.Width,t.Height,t.Format.ToString()))).ToArray();
                output=reconstructed.Output;upscalingDrawCalls=upscaling.EnableRcas?2:1;
            }
            if(temporal)
            {
                var currentFrame=frame;
                var prevTemporalFrame=temporalReset?currentFrame:temporalHistory.Previous!;
                var skinningForTemporal=(currentFrame.Surface.Snapshot.HasDeferredDeformation||prevTemporalFrame.Surface.Snapshot.HasDeferredDeformation)
                    ?cache.GpuSkinning:null;
                var coverage=(temporalInputs??=new(runtime)).Record(context,currentFrame,prevTemporalFrame,width,height,preparedFrame,skinningForTemporal,vb);
                var transfer=temporalColor??=new(runtime,cache);
                var linear=transfer.Decode(context,output);
                var camera=currentFrame.Options?.CameraOverride??currentFrame.Surface.Snapshot.Camera;
                bool xess=temporalUpscaling.Mode==D3D12TemporalUpscalingMode.XeSs;
                bool dlss=temporalUpscaling.Mode==D3D12TemporalUpscalingMode.Dlss;
                var reconstructed=dlss?dlssTemporalPass!.Record(context,linear,coverage,temporalJitter,temporalUpscaling.FrameTimeMilliseconds,temporalReset)
                    :xess?xeSsTemporalPass!.Record(context,linear,coverage,temporalJitter,1,temporalReset)
                    :temporalPass!.Record(context,linear,coverage,temporalJitter,temporalUpscaling.FrameTimeMilliseconds,
                        temporalReset,camera.Near,camera.Far,camera.VerticalFovDegrees!.Value*MathF.PI/180);
                output=transfer.Encode(context,reconstructed);
                string vendor=dlss?"dlss":xess?"xess":"fsr";
                Recorded(vendor+":decode",linear,defaultDepth);
                Recorded(vendor+":temporal",reconstructed,defaultDepth);
                Recorded(vendor+":encode",output,defaultDepth);
                LastTargets=LastTargets.Concat(new[]{linear,reconstructed,output}.Select(t=>new PostEffectTargetRecord(t.Name,t.Width,t.Height,t.Format.ToString()))).ToArray();
                upscalingDrawCalls=2;
            }
            if(diffusionActive)
            {
                var filtered=(diffusionPass??=new(runtime,cache)).Record(context,output,diffusion!);
                Recorded("diffusion:x",filtered.Horizontal,defaultDepth);
                Recorded("diffusion:y",filtered.Output,defaultDepth);
                LastTargets=LastTargets.Concat(new[]{filtered.Horizontal,filtered.Output}
                    .Select(t=>new PostEffectTargetRecord(t.Name,t.Width,t.Height,t.Format.ToString()))).ToArray();
                output=filtered.Output;LastPassDrawCalls+=2;
            }
            if(straightAlpha)
            {
                output=(alphaResolvePass??=new(runtime,cache)).Record(context,output);
                LastPassDrawCalls++;Recorded("alpha:straight",output,defaultDepth);
                LastTargets=LastTargets.Append(new(output.Name,width,height,output.Format.ToString())).ToArray();
            }
            if(exposureEv!=0)
            {
                output=(exposurePass??=new(runtime,cache)).Record(context,output,exposureEv);
                LastPassDrawCalls++;Recorded("lighting:exposure",output,defaultDepth);
                LastTargets=LastTargets.Append(new(output.Name,output.Width,output.Height,output.Format.ToString())).ToArray();
            }
            if(!capture&&frame.EditorOverlay is {Lines.Count:>0} overlay)
            {
                D3D12GpuTarget? guideDepth=null;
                if(overlay.Lines.Any(line=>line.DeviceDepth is not null))
                {
                    long visibilityAllocatedStart=GC.GetAllocatedBytesForCurrentThread();
                    long visibilityStart=System.Diagnostics.Stopwatch.GetTimestamp();
                    // Scene depth can be render-sized or jittered by the final SSAA/temporal sample.
                    // Reuse exact character alpha/culling coverage at output resolution, without jitter.
                    var editorGeometry=emptyScene?null:originalRecorder.BorrowEditorGeometry(context,frame,vb!,ib!,cache);
                    guideDepth=(temporalInputs??=new(runtime)).RecordEditorVisibility(context,frame,output.Width,output.Height,preparedFrame,editorGeometry);
                    Recorded("editor:world-guide-depth",output,guideDepth);
                    LastTargets=LastTargets.Append(new(guideDepth.Name,guideDepth.Width,guideDepth.Height,guideDepth.Format.ToString())).ToArray();
                    editorVisibilityMilliseconds=System.Diagnostics.Stopwatch.GetElapsedTime(visibilityStart).TotalMilliseconds;
                    editorVisibilityAllocatedBytes=GC.GetAllocatedBytesForCurrentThread()-visibilityAllocatedStart;
                }
                long overlayStart=System.Diagnostics.Stopwatch.GetTimestamp();
                EditorOverlayDrawCalls=(lightGizmoPass??=new(runtime,cache)).Record(context,output,overlay,guideDepth);
                if(guideDepth is not null)Recorded("editor:world-guides",output,guideDepth);
                if(overlay.Lines.Any(line=>line.DeviceDepth is null))Recorded("editor:selection-overlay",output,defaultDepth);
                editorOverlayMilliseconds=System.Diagnostics.Stopwatch.GetElapsedTime(overlayStart).TotalMilliseconds;
            }
            commands.UnsetRenderTargets(); Transition(output,ResourceStates.CopySource);
            byte[] rgba=[];
            ID3D12Resource? readback=null;ulong bytes=0;var footprints=new PlacedSubresourceFootPrint[1];
            if(capture)
            {
                var rowCounts=new int[1];var rowSizes=new ulong[1];
                device.GetCopyableFootprints(output.Resource.Description,0,1,0,footprints,rowCounts,rowSizes,out bytes);
                readback=Own(device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer(bytes),ResourceStates.CopyDest));
                commands.CopyTextureRegion(new TextureCopyLocation(readback,footprints[0]),0,0,0,new TextureCopyLocation(output.Resource,0));
                LastReadbackCount++;runtime.Readbacks++;
                int diagnosticReadbacks=rayGiRenderer?.DiagnosticReadbackCount??0;LastReadbackCount+=diagnosticReadbacks;runtime.Readbacks+=diagnosticReadbacks;
            }
            long appendPresentStart=System.Diagnostics.Stopwatch.GetTimestamp();
            appendPresent?.Invoke(context,output);
            appendPresentMilliseconds=System.Diagnostics.Stopwatch.GetElapsedTime(appendPresentStart).TotalMilliseconds;
            if(D3D12RayGiRenderer.IsTemporalEnabled(rayGiOptions))rayGiRenderer?.ValidatePendingFrame();
            double recordMilliseconds=recordTimer.Elapsed.TotalMilliseconds-runtime.LastSlotWaitMilliseconds;
            long recordAllocatedBytes=GC.GetAllocatedBytesForCurrentThread()-recordAllocatedStart;
            var submitTimer=System.Diagnostics.Stopwatch.StartNew();var completion=runtime.Submit();LastSubmissionCount++;submitMilliseconds=submitTimer.Elapsed.TotalMilliseconds;
            lastSubmittedSurfaceStyles=surfaceStyles;
            if(D3D12RayGiRenderer.IsTemporalEnabled(rayGiOptions))rayGiRenderer?.CommitFrame();
            else rayGiRenderer?.InvalidateTemporalHistory("ModeChanged");
            if(temporal)
            {
                temporalHistory.Commit(frame,temporalUpscaling,temporalPhase,temporalPhaseCount,historyRayGiOptions);
                bool xess=temporalUpscaling.Mode==D3D12TemporalUpscalingMode.XeSs;
                bool dlss=temporalUpscaling.Mode==D3D12TemporalUpscalingMode.Dlss;
                TemporalUpscalingStatistics=new(dlss?"DLSS "+dlssTemporalPass!.DeviceQuery.ComponentFileVersion
                        :xess?"XeSS "+xeSsTemporalPass!.DeviceQuery.RuntimeVersion:temporalPass!.ProviderName,
                    xess||dlss?0:temporalPass!.ProviderId,dlss?dlssTemporalPass!.LastReset:xess?xeSsTemporalPass!.LastReset:temporalPass!.LastReset,
                    temporalHistory.SubmittedFrames,temporalPhase,temporalJitter,width,height,displayWidth,displayHeight)
                    {ResetReason=temporalResetReason,Mode=temporalUpscaling.Mode,XeSsDeviceQuery=xess?xeSsTemporalPass!.DeviceQuery:null,
                        DlssDeviceQuery=dlss?dlssTemporalPass!.DeviceQuery:null};
            }
            if(capture)
            {
                runtime.Wait(completion);rgba=new byte[checked(displayWidth*displayHeight*4)];var data=readback!.Map<byte>(0,checked((int)bytes));
                try{for(int y=0;y<displayHeight;y++)data.Slice(y*footprints[0].Footprint.RowPitch,displayWidth*4).CopyTo(rgba.AsSpan(y*displayWidth*4,displayWidth*4));}
                finally{readback.Unmap(0);}
                rayGiRenderer?.CompleteDiagnosticCapture();
            }
            Statistics=runtime.Statistics(LastSceneDrawCalls,LastPassDrawCalls,LastReadbackCount,LastSubmissionCount) with {CachedResources=cache.LiveResourceCount,RecordCpuMilliseconds=recordMilliseconds,GeometryCacheMilliseconds=geometryMilliseconds,
                TextureBindingMilliseconds=textureMilliseconds,SceneRecordMilliseconds=sceneMilliseconds,SubmitCpuMilliseconds=submitMilliseconds,
                SceneRecordCpu=originalRecorder.SceneRecordCpuStatistics,
                BeginFrameCpuMilliseconds=beginFrameMilliseconds,FrameTargetMilliseconds=frameTargetMilliseconds,
                AntialiasingCpuMilliseconds=antialiasingMilliseconds,EditorVisibilityCpuMilliseconds=editorVisibilityMilliseconds,
                EditorOverlayCpuMilliseconds=editorOverlayMilliseconds,AppendPresentCpuMilliseconds=appendPresentMilliseconds,
                RecordAllocatedBytes=recordAllocatedBytes,SceneRecordAllocatedBytes=sceneAllocatedBytes,EditorVisibilityAllocatedBytes=editorVisibilityAllocatedBytes,
                Antialiasing=antialiasing,AntialiasingPassDrawCalls=antialiasingDrawCalls,SmaaQuality=smaaQuality,
                SpatialSamples=spatialSamples,SpatialAccumulationDispatches=spatialSamples>1?spatialSamples:0,SpatialResolveDrawCalls=spatialResolveDrawCalls,
                SpatialUpscaling=upscaling.Mode,UpscalingInputWidth=width,UpscalingInputHeight=height,
                UpscalingOutputWidth=displayWidth,UpscalingOutputHeight=displayHeight,UpscalingPassDrawCalls=upscalingDrawCalls,
                TemporalGiInFlightFrames=temporalGiAdmission?.Reservation.InFlightFrames??0,
                TemporalGiHistoryGenerations=temporalGiAdmission?.Reservation.HistoryGenerations??0,
                TemporalGiReservationBytes=temporalGiAdmission?.Reservation.InFlightBytes??0,
                TemporalGiAvailableBytes=temporalGiAdmission?.Budget.AvailableBytes??0,
                TemporalGiRetirementMilliseconds=temporalGiAdmission?.RetirementMilliseconds??0,
                CaptureLowMemory=captureLowMemory,CaptureInFlightFrames=CaptureMemoryStatistics?.InFlightFrames??0,
                CaptureRetirementMilliseconds=captureRetirementMilliseconds,
                TemporalUpscaling=TemporalUpscalingStatistics};
            LastRecordings=recordingRecords;
            if(!capture)LastGpuOutput=new(displayWidth,displayHeight,runtime.FrameId,Statistics,output.Resource.QueryInterface<ID3D12Resource>());
            return new(displayWidth,displayHeight,rgba,"Direct3D12ModernFrame",$"{AdapterName} ({FeatureLevel})",LastSceneDrawCalls+LastPassDrawCalls+antialiasingDrawCalls+spatialResolveDrawCalls+upscalingDrawCalls);
        }
        finally
        {
            if(runtime.IsRecording){rayGiRenderer?.AbortFrame();rayGiRenderer?.BeginFrame(retainTriangleScratch);if(temporal)temporalHistory.Invalidate("AbortedFrame");}
            runtime.Abort();
            if(debugQueue is not null){var messages=new List<string>();for(ulong i=0;i<debugQueue.NumStoredMessages;i++)messages.Add(debugQueue.GetMessage(i).ToString());LastDebugMessages=messages;}
            rendering=false;
        }
    }

    private sealed record GeometryIdentity(ID3D12Resource Vertex,ID3D12Resource Index);
    private static ulong MeasureEndfieldMatcapMemory(D3D12GpuRuntime runtime,SceneSnapshot scene,D3D12SurfaceStyleMap? surfaceStyles)
    {
        if(surfaceStyles?.HasConsumedSurfaceMatcap!=true)return 0;
        ulong Allocation(ResourceDescription description)
        {
            ulong bytes=runtime.Device.GetResourceAllocationInfo(0,[description]).SizeInBytes;
            if(bytes is 0 or ulong.MaxValue)throw new NotSupportedException("Device cannot estimate MatCap corner/color resources.");
            return bytes;
        }
        ulong reservation=0;
        var colorImages=new HashSet<SceneTexture>(ReferenceEqualityComparer.Instance);
        for(int i=0;i<surfaceStyles.Count;i++)
        {
            if(surfaceStyles.EffectiveSurfaceMatcap(i) is not {} matcap)continue;
            var source=matcap.Geometry?.Source??scene;
            int localMaterial=matcap.Geometry?.LocalMaterialIndex??i;
            ulong cornerBytes=checked((ulong)source.Materials[localMaterial].IndexCount*64);
            if(cornerBytes>0)
            {
                ulong cornerAllocation=Allocation(ResourceDescription.Buffer(cornerBytes));
                // One current replaceable owner, three fence-leased old owners and three
                // upload allocations. SSAA samples reuse this same immutable pose payload.
                reservation=checked(reservation+cornerAllocation*(D3D12SsaaGiMemoryBudget.MaximumInFlightFrames*2UL+1));
            }
            if(matcap.MatcapEncoding!=D3D12MatcapColorEncoding.SrgbRgb)continue;
            var texture=scene.Textures[matcap.MatcapCombinedTextureIndex];
            if(!colorImages.Add(texture))continue;
            var description=ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm_SRgb,(uint)texture.Width,(uint)texture.Height,mipLevels:1);
            runtime.Device.GetCopyableFootprints(description,0,1,0,new PlacedSubresourceFootPrint[1],new int[1],new ulong[1],out ulong stagingBytes);
            reservation=checked(reservation+Allocation(description)
                +Allocation(ResourceDescription.Buffer(stagingBytes))*D3D12SsaaGiMemoryBudget.MaximumInFlightFrames);
        }
        return reservation;
    }
    private static ulong MeasureGenshinMetallicMemory(D3D12GpuRuntime runtime,SceneSnapshot scene,D3D12SurfaceStyleMap? styles)
    {
        if(styles?.HasConsumedGenshinMetallic!=true)return 0;
        ulong Allocation(ResourceDescription desc)
        {
            ulong bytes=runtime.Device.GetResourceAllocationInfo(0,[desc]).SizeInBytes;
            if(bytes is 0 or ulong.MaxValue)throw new NotSupportedException("Device cannot estimate metallic color resources.");
            return bytes;
        }
        ulong reservation=0;var images=new HashSet<SceneTexture>(ReferenceEqualityComparer.Instance);
        for(int i=0;i<styles.Count;i++)
        {
            if(styles.EffectiveGenshinMetallic(i) is not {MatcapEncoding:D3D12MatcapColorEncoding.SrgbRgb} b)continue;
            var texture=scene.Textures[b.MatcapCombinedTextureIndex];if(!images.Add(texture))continue;
            var desc=ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm_SRgb,(uint)texture.Width,(uint)texture.Height,mipLevels:1);
            runtime.Device.GetCopyableFootprints(desc,0,1,0,new PlacedSubresourceFootPrint[1],new int[1],new ulong[1],out ulong upload);
            reservation=checked(reservation+Allocation(desc)+Allocation(ResourceDescription.Buffer(upload))*D3D12SsaaGiMemoryBudget.MaximumInFlightFrames);
        }
        return reservation;
    }
    private static ulong MeasureEndfieldBaseImageMemory(D3D12GpuRuntime runtime,SceneSnapshot scene,D3D12SurfaceStyleMap? styles)
    {
        if(styles?.HasConsumedEndfieldBaseImage!=true)return 0;
        ulong Allocation(ResourceDescription desc)
        {
            ulong bytes=runtime.Device.GetResourceAllocationInfo(0,[desc]).SizeInBytes;
            if(bytes is 0 or ulong.MaxValue)throw new NotSupportedException("Device cannot estimate Endfield base-image resources.");
            return bytes;
        }
        ulong reservation=checked(Allocation(ResourceDescription.Buffer((ulong)D3D12EndfieldShadowLut.Bytes.Length))
            *(D3D12SsaaGiMemoryBudget.MaximumInFlightFrames+1UL));
        var images=new HashSet<SceneTexture>(ReferenceEqualityComparer.Instance);
        for(int i=0;i<styles.Count;i++)
        {
            if(styles.EffectiveEndfieldMatcap(i)?.Palette?.BaseImage is not {ConsumesBaseTexture:true,BaseEncoding:D3D12MatcapColorEncoding.SrgbRgb} b)continue;
            var texture=scene.Textures[b.BaseCombinedTextureIndex!.Value];if(!images.Add(texture))continue;
            var desc=ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm_SRgb,(uint)texture.Width,(uint)texture.Height,mipLevels:1);
            runtime.Device.GetCopyableFootprints(desc,0,1,0,new PlacedSubresourceFootPrint[1],new int[1],new ulong[1],out ulong upload);
            reservation=checked(reservation+Allocation(desc)+Allocation(ResourceDescription.Buffer(upload))*D3D12SsaaGiMemoryBudget.MaximumInFlightFrames);
        }
        return reservation;
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SceneSnapshot,GeometryIdentity> geometryIdentities=new();
    private GeometryIdentity GetGeometry(SceneSnapshot scene)
    {
        if(scene.HasDeferredDeformation)
        {
            var vb=cache.SkinnedVertexBuffer("frame-deformed-vertices",scene.StaticVertexView,out bool wrote);
            cache.GpuSkinning.Skin(vb,scene,wrote);
            return new(vb,cache.IndexBuffer(scene));
        }
        // Proven composition revisions use slot-owned partial updates. Arbitrary dynamic
        // poses preserve immutable reuse; neither path enters the permanent content cache/CWT.
        if(scene.HasDynamicVertices||scene.VertexRevision.SupportsIncrementalUpdates)
            return new(cache.SceneVertexBuffer("frame-deformed-vertices",scene),
                cache.IndexBuffer(scene));
        if(geometryIdentities.TryGetValue(scene,out var identity))return identity;
        var vertex=cache.Buffer(MemoryMarshal.AsBytes(scene.Vertices),ResourceStates.VertexAndConstantBuffer);
        var index=cache.Buffer(MemoryMarshal.AsBytes(scene.Indices),ResourceStates.IndexBuffer);
        identity=new(vertex,index);geometryIdentities.Add(scene,identity);runtime.OnAbort(()=>geometryIdentities.Remove(scene));return identity;
    }
    private void DisposeTemporalPasses()
    {
        temporalPass?.Dispose();temporalPass=null;xeSsTemporalPass?.Dispose();xeSsTemporalPass=null;
        dlssTemporalPass?.Dispose();dlssTemporalPass=null;
    }
    public void Dispose(){if(disposed)return;runtime.WaitForIdle();disposed=true;DisposeTemporalPasses();temporalInputs?.Dispose();rayGiRenderer?.Dispose();frameDescriptors.Dispose();cache.Dispose();if(ownsOriginalRecorder)originalRecorder.Dispose();if(ownsRuntime)runtime.Dispose();}
}
