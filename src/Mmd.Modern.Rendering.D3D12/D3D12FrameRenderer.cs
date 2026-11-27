using System.Collections.ObjectModel;
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
    private static readonly SceneTexture WhiteTexture = new("white", 1, 1, [255, 255, 255, 255]);
    private static readonly SceneTexture WhiteSphereTexture = new("white-sphere", 1, 1, [255, 255, 255, 255]);
    private readonly D3D12GpuRuntime runtime;
    private readonly bool ownsRuntime;
    private readonly D3D12GpuCache cache;
    private readonly D3D12FrameDescriptorHeaps frameDescriptors;
    private readonly D3D12OriginalStyleRenderer originalRecorder;
    private D3D12RayGiRenderer? rayGiRenderer;
    private D3D12SmaaPass? smaaPass;
    private D3D12FxaaPass? fxaaPass;
    private D3D12NativeTaaPass? nativeTaaPass;
    private D3D12Fsr1Pass? fsr1Pass;
    private D3D12RcasPass? rcasPass;
    private D3D12ScreenSpaceAoPass? ssaoPass;
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
    private readonly D3D12TemporalHistory nativeTaaHistory = new();
    private readonly MipmapTextureCache mipmapCache = new();
    private readonly PostEffectTargetRecorder targetRecorder = new();
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
    internal D3D12OriginalStyleRenderer OriginalRecorder => originalRecorder;
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
        RayGiOptions? rayGiOptions=null,D3D12SpatialUpscalingOptions? upscaling=null,D3D12TemporalUpscalingOptions? temporalUpscaling=null,D3D12SurfaceStyleMap? surfaceStyles=null,D3D12DiffusionOptions? diffusion=null,D3D12CaptureMemoryOptions? captureMemory=null,D3D12SharpeningOptions? sharpening=null)
        =>RenderCore(frame,width,height,true,null,rayGiOptions,upscaling,temporalUpscaling,surfaceStyles,diffusion,captureMemory,null,sharpening);
    internal RenderedFrame RenderInto(Memory<byte> destination,OriginalFrameScene frame,int width,int height,
        RayGiOptions? rayGiOptions=null,D3D12SpatialUpscalingOptions? upscaling=null,D3D12TemporalUpscalingOptions? temporalUpscaling=null,D3D12SurfaceStyleMap? surfaceStyles=null,D3D12DiffusionOptions? diffusion=null,D3D12CaptureMemoryOptions? captureMemory=null,D3D12SharpeningOptions? sharpening=null)
    {
        if(width is < 1 or > 4096 || height is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(width));
        if(destination.Length<checked(width*height*4)) throw new ArgumentException("Destination must hold width*height*4 RGBA bytes.",nameof(destination));
        return RenderCore(frame,width,height,true,null,rayGiOptions,upscaling,temporalUpscaling,surfaceStyles,diffusion,captureMemory,destination,sharpening);
    }

    internal D3D12FrameOutput RecordGpu(OriginalFrameScene frame,int width,int height,RayGiOptions? rayGiOptions,
        Action<D3D12RecordingContext,D3D12GpuTarget>? appendPresent,D3D12SpatialUpscalingOptions? upscaling=null,D3D12TemporalUpscalingOptions? temporalUpscaling=null,D3D12SurfaceStyleMap? surfaceStyles=null,D3D12DiffusionOptions? diffusion=null,D3D12SharpeningOptions? sharpening=null)
    {
        LastGpuOutput=null;
        RenderCore(frame,width,height,false,appendPresent,rayGiOptions,upscaling,temporalUpscaling,surfaceStyles,diffusion,null,null,sharpening);
        return LastGpuOutput!;
    }

    private RenderedFrame RenderCore(OriginalFrameScene frame,int width,int height,bool capture,
        Action<D3D12RecordingContext,D3D12GpuTarget>? appendPresent,RayGiOptions? rayGiOptions=null,D3D12SpatialUpscalingOptions? upscaling=null,D3D12TemporalUpscalingOptions? temporalUpscaling=null,D3D12SurfaceStyleMap? surfaceStyles=null,D3D12DiffusionOptions? diffusion=null,D3D12CaptureMemoryOptions? captureMemory=null,Memory<byte>? destination=null,D3D12SharpeningOptions? sharpening=null)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ArgumentNullException.ThrowIfNull(frame);
        if (rendering) throw new InvalidOperationException("Reentrant/concurrent rendering is unsupported.");
        if (frame.Options is not null) OriginalFrameMath.Validate(frame.Options);
        sharpening?.Validate();
        captureMemory??=D3D12CaptureMemoryOptions.Default;
        captureMemory.Validate();
        CaptureMemoryStatistics=null;
        diffusion?.ValidateFrame(frame); // Before captures, SDK recreation or any recording.
        bool diffusionActive=diffusion?.IsActive==true;
        float exposureEv=frame.Lighting?.Settings.ExposureEv??0;
        rayGiOptions??=frame.Options?.RayGi;
        if(capture&&rayGiOptions is {PreviewAcceleration:true})
            rayGiOptions=rayGiOptions with {PreviewAcceleration=false};
        rayGiOptions=D3D12SceneLighting.ResolveGi(frame,rayGiOptions);
        surfaceStyles?.Validate(frame,rayGiOptions);
        surfaceStyles=surfaceStyles?.CaptureResolvedInputs(frame);
        var scene=frame.Surface.Snapshot;
        ulong radialReservation=D3D12SsaaGiMemoryBudget.MeasureRadialSeeds(runtime,scene,
            surfaceStyles?.HasGeometryRequirement(D3D12StyleGeometryRequirements.RawRadialSeeds)==true);
        ulong pluginStyleReservation=MeasurePluginStyleMemory(runtime,scene,surfaceStyles);
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
        ulong additionalReservation=checked(radialReservation+pluginStyleReservation+diffusionReservation
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
        ValidateOutputModes(frame, capture, upscaling.Mode, temporalUpscaling.Mode, rayGiOptions);
        if (temporal) dimensions = temporalDimensions;
        bool upscale=upscaling.Mode==D3D12SpatialUpscalingMode.Fsr1;
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
        var antialiasing=frame.Options?.Antialiasing??FrameAntialiasing.Off;
        bool nativeTaa=antialiasing==FrameAntialiasing.Taa;
        bool taaReset=false;int taaPhase=0;const int taaPhaseCount=8;Vector2 taaJitter=default;
        if(nativeTaa)
        {
            taaReset=nativeTaaHistory.ShouldReset(frame,temporalUpscaling,historyRayGiOptions);
            taaPhase=taaReset?0:nativeTaaHistory.Phase;
            taaJitter=D3D12TemporalHistory.Jitter(taaPhase);
            var effectiveCamera=(frame.Options?.CameraOverride??scene.Camera) with {ProjectionJitterPixels=taaJitter};
            if(frame.Options is {} options)
                frame=new(frame.Surface,frame.Outline,frame.OutlineOptions,options with {CameraOverride=effectiveCamera},frame.Materials,frame.Lighting,frame.EditorOverlay);
            else
            {
                scene=scene.WithCamera(effectiveCamera);
                frame=new(new OriginalStyleScene(scene,frame.Surface.Materials,frame.Surface.Profile),frame.Outline,frame.OutlineOptions,
                    null,frame.Materials,frame.Lighting,frame.EditorOverlay);
            }
        }
        else if(nativeTaaPass is not null)
        {
            nativeTaaPass.InvalidateHistory();
            nativeTaaHistory.Invalidate("ModeChanged");
        }
        bool ssaoActive=frame.Options?.ScreenSpaceAo is { IsActive: true };
        var ssaoOptions=frame.Options?.ScreenSpaceAo;
        var colorFormat=straightAlpha?Format.R16G16B16A16_Float:Format.R8G8B8A8_UNorm;
        var depthFormat=frame.Options?.GroundShadow?.UseStencil==true?Format.D24_UNorm_S8_UInt:Format.D32_Float;
        int msaaSamples=ResolveSupportedMsaaSamples(device,colorFormat,depthFormat,OriginalFrameMath.MsaaSampleCount(antialiasing));
        var effectiveShadowFilter=frame.Options?.SelfShadow is { } shadowSpec
            ? frame.Options.ResolveShadowFilter(shadowSpec)
            : (frame.Options?.ShadowFilter ?? ShadowFilterMode.Linear);
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
        var context=runtime.Begin();
        originalRecorder.ResetSceneRecordCpuStatistics();
        double beginFrameMilliseconds=recordTimer.Elapsed.TotalMilliseconds-runtime.LastSlotWaitMilliseconds;
        rendering=true;
        double geometryMilliseconds=0,textureMilliseconds=0,sceneMilliseconds=0,submitMilliseconds=0;
        double frameTargetMilliseconds=0,antialiasingMilliseconds=0,editorVisibilityMilliseconds=0,editorOverlayMilliseconds=0,appendPresentMilliseconds=0;
        long sceneAllocatedBytes=0,editorVisibilityAllocatedBytes=0;
        int antialiasingDrawCalls=0,spatialResolveDrawCalls=0,upscalingDrawCalls=0,ssaoPassDrawCalls=0,sharpeningPassDrawCalls=0;
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
            int pluginSlotBase=checked(textureSlots+scene.Textures.Count+4+nativeDescriptors);
            int[]? pluginTableSlots=D3D12OriginalStyleRenderer.ComputePluginTextureTableSlots(surfaceStyles,pluginSlotBase,out int pluginDescriptors);
            int giNextSlot=checked(pluginSlotBase+pluginDescriptors);
            // Each recorded GI receiver keeps its own SRV pair for the whole submission. Later
            // SSAA samples must not overwrite descriptors referenced by earlier queued work.
            int giRecordings=spatialSamples;
            int giDescriptors=rayGiOptions?.Enabled==true?checked(2*giRecordings):0;
            int descriptorCount=checked(giNextSlot+giDescriptors);
            var heaps=frameDescriptors.Get(descriptorCount);
            heaps.UseLayout((scene.Textures.Count,nativeDescriptors,pluginDescriptors));
            var rtvHeap=heaps.Rtv;var dsvHeap=heaps.Dsv;var srvHeap=heaps.Srv;
            runtime.DescriptorHighWater=Math.Max(runtime.DescriptorHighWater,descriptorCount);
            int srvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            int rtvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
            int dsvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
            targetRecorder.Begin(); int colorIndex = 0, depthIndex = 0;
            int descriptorWrites=0,descriptorSkips=0;
            void Counted(bool written){if(written)descriptorWrites++;else descriptorSkips++;}
            D3D12GpuTarget Allocate(string name, int w, int h, Format format, bool depth)
            {
                // Both targets are cleared before use; the pool reuses them after their last fence.
                var resource = runtime.Transients.Resource(context, HeapType.Default, ResourceDescription.Texture2D(format, (uint)w, (uint)h,
                    mipLevels: 1, flags: depth ? ResourceFlags.AllowDepthStencil : ResourceFlags.AllowRenderTarget),
                    depth ? ResourceStates.DepthWrite : ResourceStates.RenderTarget);
                var handle = depth ? dsvHeap.GetCPUDescriptorHandleForHeapStart().Offset(depthIndex++, dsvStride)
                    : rtvHeap.GetCPUDescriptorHandleForHeapStart().Offset(colorIndex++, rtvStride);
                Counted(heaps.WriteTarget(device, resource, depth));
                var target = runtime.Transients.Track(new D3D12GpuTarget(name, resource, handle, w, h, format, depth));
                targetRecorder.Add(name, w, h, format);
                return target;
            }
            D3D12GpuTarget AllocateDepthTarget(string name, int w, int h, Format dsvFormat, bool typelessForSrv, int sampleCount)
            {
                Format resourceFormat = typelessForSrv ? D3D12ScreenSpaceAoPass.DepthResourceFormat(dsvFormat) : dsvFormat;
                var resource = runtime.Transients.Resource(context, HeapType.Default, ResourceDescription.Texture2D(resourceFormat, (uint)w, (uint)h,
                    mipLevels: 1, sampleCount: sampleCount, sampleQuality: 0, flags: ResourceFlags.AllowDepthStencil),
                    ResourceStates.DepthWrite);
                CpuDescriptorHandle handle;
                if (!typelessForSrv && sampleCount == 1)
                {
                    handle = dsvHeap.GetCPUDescriptorHandleForHeapStart().Offset(depthIndex++, dsvStride);
                    Counted(heaps.WriteTarget(device, resource, true));
                }
                else
                {
                    var localDsv = runtime.Transients.Heap(new(DescriptorHeapType.DepthStencilView, 1));
                    handle = localDsv.GetCPUDescriptorHandleForHeapStart();
                    device.CreateDepthStencilView(resource, new DepthStencilViewDescription
                    {
                        Format = dsvFormat,
                        ViewDimension = sampleCount > 1 ? DepthStencilViewDimension.Texture2DMultisampled : DepthStencilViewDimension.Texture2D,
                    }, handle);
                }
                var target = runtime.Transients.Track(new D3D12GpuTarget(name, resource, handle, w, h, dsvFormat, true, sampleCount));
                targetRecorder.Add(name, w, h, dsvFormat);
                return target;
            }
            var output = Allocate("$output", width, height, colorFormat, false);
            var defaultDepth = AllocateDepthTarget("$depth", width, height, depthFormat, ssaoActive && msaaSamples == 1, 1);
            D3D12GpuTarget? msaaColor = null;
            D3D12GpuTarget? msaaDepth = null;
            if (msaaSamples > 1)
            {
                var msaaColorRes = runtime.Transients.Resource(context, HeapType.Default, ResourceDescription.Texture2D(colorFormat, (uint)width, (uint)height,
                    mipLevels: 1, sampleCount: msaaSamples, sampleQuality: 0, flags: ResourceFlags.AllowRenderTarget),
                    ResourceStates.RenderTarget);
                var msaaRtv = runtime.Transients.Heap(new(DescriptorHeapType.RenderTargetView, 1));
                var msaaRtvHandle = msaaRtv.GetCPUDescriptorHandleForHeapStart();
                device.CreateRenderTargetView(msaaColorRes, null, msaaRtvHandle);
                msaaColor = runtime.Transients.Track(new D3D12GpuTarget("$msaa-color", msaaColorRes, msaaRtvHandle, width, height, colorFormat, false, msaaSamples));
                targetRecorder.Add("$msaa-color", width, height, colorFormat);
                msaaDepth = AllocateDepthTarget("$msaa-depth", width, height, depthFormat, ssaoActive, msaaSamples);
            }
            commands.SetDescriptorHeaps(srvHeap);
            frameTargetMilliseconds=System.Diagnostics.Stopwatch.GetElapsedTime(frameTargetStart).TotalMilliseconds;
            var stageTimer=System.Diagnostics.Stopwatch.StartNew();
            var geometry=emptyScene?null:GetGeometry(scene);var vb=geometry?.Vertex;var ib=geometry?.Index;
            geometryMilliseconds=stageTimer.Elapsed.TotalMilliseconds;stageTimer.Restart();
            bool[] mipmapTextures = GetMipmapTextures(frame, surfaceStyles);
            void UploadTexture(SceneTexture texture, int slot, bool generateMipmaps)
                => Counted(heaps.WriteSrv(device, slot, cache.Texture(texture, generateMipmaps), srvStride, Format.R8G8B8A8_UNorm));
            var whiteGpu = cache.Texture(WhiteTexture, false);
            Counted(heaps.WriteSrv(device, textureSlots, whiteGpu, srvStride, Format.R8G8B8A8_UNorm));
            UploadTexture(WhiteSphereTexture, textureSlots + 1, false);
            for (int i = 0; i < scene.Textures.Count; i++) UploadTexture(scene.Textures[i], textureSlots + i + 2, mipmapTextures[i]);
            if(pluginDescriptors>0)
            {
                for(int s=0;s<D3D12PluginResourceLimits.MaxTextureSlots;s++)
                    Counted(heaps.WriteSrv(device,pluginSlotBase+s,whiteGpu,srvStride,Format.R8G8B8A8_UNorm));
                for(int i=0;i<surfaceStyles!.Count;i++)
                {
                    int tableSlot=pluginTableSlots![i];
                    if(tableSlot==pluginSlotBase)continue;
                    var bindings=surfaceStyles.GetPluginDraw(i)?.TextureBindings;
                    for(int s=0;s<D3D12PluginResourceLimits.MaxTextureSlots;s++)
                    {
                        D3D12PluginTextureBinding? found=null;
                        if(bindings is not null)
                            for(int j=0;j<bindings.Count;j++)
                                if(bindings[j].SlotIndex==s){found=bindings[j];break;}
                        if(found is null||(found.SceneTextureIndex<0&&found.InlineTexture is null))
                        {
                            Counted(heaps.WriteSrv(device,tableSlot+s,whiteGpu,srvStride,Format.R8G8B8A8_UNorm));
                            continue;
                        }
                        if(found.InlineTexture is {} inlineTex)
                        {
                            var effectiveMip=found.MipRule!=D3D12PluginTextureMipRule.FromSource?found.MipRule:inlineTex.MipRule;
                            var gpuTex=cache.PluginInlineTexture(inlineTex,found.Encoding,effectiveMip);
                            int mipOverride=effectiveMip==D3D12PluginTextureMipRule.PointNoMip?1:-1;
                            Counted(heaps.WriteSrv(device,tableSlot+s,gpuTex,srvStride,gpuTex.Description.Format,mipOverride));
                        }
                        else
                        {
                            var sceneTex=scene.Textures[found.SceneTextureIndex];
                            bool genMips=found.MipRule==D3D12PluginTextureMipRule.GenerateMips&&sceneTex.Kind==SceneTextureKind.Image;
                            if(found.MipRule==D3D12PluginTextureMipRule.FromSource)genMips=mipmapTextures[found.SceneTextureIndex];
                            var gpuTex=cache.TextureColor(sceneTex,found.Encoding,genMips);
                            int mipOverride=found.MipRule==D3D12PluginTextureMipRule.PointNoMip?1:-1;
                            Format viewFormat=found.Encoding==D3D12PluginTextureEncoding.SrgbColor
                                ?Format.R8G8B8A8_UNorm_SRgb:Format.R8G8B8A8_UNorm;
                            Counted(heaps.WriteSrv(device,tableSlot+s,gpuTex,srvStride,viewFormat,mipOverride));
                        }
                    }
                }
            }
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
            D3D12ScreenSpaceAoPass.Result? ssaoReusable = null;
            D3D12GpuTarget RunSsao(D3D12GpuTarget sceneColor, D3D12GpuTarget sceneDepth1x, OriginalFrameScene activeFrame, bool reuseAcrossSamples = false)
            {
                var activeCamera = activeFrame.Options?.CameraOverride ?? activeFrame.Surface.Snapshot.Camera;
                var ssaoResult = (ssaoPass ??= new(runtime, cache)).Record(context, sceneColor, sceneDepth1x, activeCamera, ssaoOptions!, reuseAcrossSamples ? ssaoReusable : null);
                if (reuseAcrossSamples && ssaoReusable is null) ssaoReusable = ssaoResult;
                Recorded("ssao:raw", ssaoResult.RawAo, sceneDepth1x);
                Recorded("ssao:composite", ssaoResult.Output, sceneDepth1x);
                targetRecorder.Add(ssaoResult.RawAo);
                targetRecorder.Add(ssaoResult.Output);
                ssaoPassDrawCalls += 2;
                LastPassDrawCalls += 2;
                return ssaoResult.Output;
            }
            if(msaaSamples>1)
            {
                current=msaaColor!;
                currentDepth=msaaDepth!;
                commands.ClearRenderTargetView(msaaColor!.Handle,new Color4(background.X,background.Y,background.Z,background.W));
                commands.ClearDepthStencilView(msaaDepth!.Handle,ClearFlags.Depth|(msaaDepth.Format==Format.D24_UNorm_S8_UInt?ClearFlags.Stencil:0),1,0);
                msaaColor.Initialized=true;msaaDepth.Initialized=true;
                msaaDepth.StencilInitialized=msaaDepth.Format==Format.D24_UNorm_S8_UInt;
                Scene();
                commands.UnsetRenderTargets();
                Transition(msaaColor,ResourceStates.ResolveSource);
                Transition(output,ResourceStates.ResolveDest);
                commands.ResolveSubresource(output.Resource,0,msaaColor.Resource,0,output.Format);
                output.Initialized=true;
                Recorded("msaa:resolve",output,defaultDepth);
                if(ssaoActive)
                {
                    var depth1x=(ssaoPass??=new(runtime,cache)).ResolveMsaaDepth(context,msaaDepth,msaaSamples);
                    Recorded("msaa:depth-resolve",output,depth1x);
                    targetRecorder.Add(depth1x);
                    ssaoPassDrawCalls++;
                    LastPassDrawCalls++;
                    output=RunSsao(output,depth1x,frame);
                }
            }
            else
            {
                commands.ClearRenderTargetView(output.Handle,new Color4(background.X,background.Y,background.Z,background.W));
                commands.ClearDepthStencilView(defaultDepth.Handle,ClearFlags.Depth|(defaultDepth.Format==Format.D24_UNorm_S8_UInt?ClearFlags.Stencil:0),1,0);
                output.Initialized=true; defaultDepth.Initialized=true;
                defaultDepth.StencilInitialized=defaultDepth.Format==Format.D24_UNorm_S8_UInt;
                if(spatialSamples==1)
                {
                    Scene();
                    if(ssaoActive)output=RunSsao(output,defaultDepth,frame);
                }
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
                            Transition(defaultDepth,ResourceStates.DepthWrite);
                            commands.ClearRenderTargetView(output.Handle,new Color4(background.X,background.Y,background.Z,background.W));
                            commands.ClearDepthStencilView(defaultDepth.Handle,ClearFlags.Depth|(defaultDepth.Format==Format.D24_UNorm_S8_UInt?ClearFlags.Stencil:0),1,0);
                        }
                        frame=new(sourceFrame.Surface,sourceFrame.Outline,sourceFrame.OutlineOptions,
                            sourceOptions with {CameraOverride=camera with {ProjectionJitterPixels=camera.ProjectionJitterPixels+SpatialSampling.JitterPixels(spatialSamples,sample)}},
                            sourceFrame.Materials,sourceFrame.Lighting,sourceFrame.EditorOverlay);
                        commands.SetDescriptorHeaps(srvHeap);
                        Scene(sample);
                        var sampleColor=ssaoActive?RunSsao(output,defaultDepth,frame,reuseAcrossSamples:true):output;
                        accumulation.Accumulate(context,sampleColor,sample);
                        Recorded("ssaa:accumulate",sampleColor,defaultDepth);
                    }
                    frame=sourceFrame;
                    output=accumulation.Resolve(context);
                    spatialResolveDrawCalls=1;
                    Recorded("ssaa:resolve",output,defaultDepth);
                    targetRecorder.Add(output.Name,width,height,output.Format);
                }
            }
            if(antialiasing==FrameAntialiasing.Smaa1X)
            {
                long antialiasingStart=System.Diagnostics.Stopwatch.GetTimestamp();
                var aa=(smaaPass??=new(runtime,cache)).Record(context,output,smaaQuality,alphaCoverage:straightAlpha);
                Recorded("smaa:edges",aa.Edges,defaultDepth);
                Recorded("smaa:weights",aa.Weights,defaultDepth);
                Recorded("smaa:neighborhood",aa.Output,defaultDepth);
                targetRecorder.Add(aa.Edges);
                targetRecorder.Add(aa.Weights);
                targetRecorder.Add(aa.Output);
                output=aa.Output;antialiasingDrawCalls=3;
                antialiasingMilliseconds=System.Diagnostics.Stopwatch.GetElapsedTime(antialiasingStart).TotalMilliseconds;
            }
            else if(antialiasing==FrameAntialiasing.Fxaa)
            {
                long antialiasingStart=System.Diagnostics.Stopwatch.GetTimestamp();
                var fxaaOut=(fxaaPass??=new(runtime,cache)).Record(context,output,alphaCoverage:straightAlpha);
                Recorded("fxaa:apply",fxaaOut,defaultDepth);
                targetRecorder.Add(fxaaOut);
                output=fxaaOut;antialiasingDrawCalls=1;
                antialiasingMilliseconds=System.Diagnostics.Stopwatch.GetElapsedTime(antialiasingStart).TotalMilliseconds;
            }
            else if(nativeTaa)
            {
                long antialiasingStart=System.Diagnostics.Stopwatch.GetTimestamp();
                var currentTaaFrame=frame;
                var prevTaaFrame=taaReset?currentTaaFrame:nativeTaaHistory.Previous!;
                var skinningForTaa=(currentTaaFrame.Surface.Snapshot.HasDeferredDeformation||prevTaaFrame.Surface.Snapshot.HasDeferredDeformation)
                    ?cache.GpuSkinning:null;
                var coverage=(temporalInputs??=new(runtime)).Record(context,currentTaaFrame,prevTaaFrame,width,height,preparedFrame,skinningForTaa,vb);
                var taaResult=(nativeTaaPass??=new(runtime,cache)).Record(context,output,coverage,taaJitter,taaReset);
                Recorded("taa:resolve",taaResult.Output,defaultDepth);
                targetRecorder.Add(taaResult.Output);
                output=taaResult.Output;antialiasingDrawCalls=1;
                antialiasingMilliseconds=System.Diagnostics.Stopwatch.GetElapsedTime(antialiasingStart).TotalMilliseconds;
            }
            if(upscale)
            {
                var reconstructed=(fsr1Pass??=new(runtime,cache)).Record(context,output,displayWidth,displayHeight,upscaling);
                Recorded("fsr1:easu",reconstructed.Easu,defaultDepth);
                targetRecorder.Add(reconstructed.Easu);
                if(reconstructed.Rcas is {} rcas)
                {
                    Recorded("fsr1:rcas",rcas,defaultDepth);
                    targetRecorder.Add(rcas);
                }
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
                targetRecorder.Add(linear);
                targetRecorder.Add(reconstructed);
                targetRecorder.Add(output);
                upscalingDrawCalls=2;
            }
            if(diffusionActive)
            {
                var filtered=(diffusionPass??=new(runtime,cache)).Record(context,output,diffusion!);
                Recorded("diffusion:x",filtered.Horizontal,defaultDepth);
                Recorded("diffusion:y",filtered.Output,defaultDepth);
                targetRecorder.Add(filtered.Horizontal);
                targetRecorder.Add(filtered.Output);
                output=filtered.Output;LastPassDrawCalls+=2;
            }
            if(straightAlpha)
            {
                output=(alphaResolvePass??=new(runtime,cache)).Record(context,output);
                LastPassDrawCalls++;Recorded("alpha:straight",output,defaultDepth);
                targetRecorder.Add(output.Name,width,height,output.Format);
            }
            if(sharpening is { IsActive: true })
            {
                output=(rcasPass??=new(runtime,cache)).Record(context,output,sharpening);
                sharpeningPassDrawCalls=1;LastPassDrawCalls++;
                Recorded("sharpening:rcas",output,defaultDepth);
                targetRecorder.Add(output);
            }
            if(exposureEv!=0)
            {
                output=(exposurePass??=new(runtime,cache)).Record(context,output,exposureEv);
                LastPassDrawCalls++;Recorded("lighting:exposure",output,defaultDepth);
                targetRecorder.Add(output);
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
                    targetRecorder.Add(guideDepth);
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
                readback=runtime.Transients.Resource(context,HeapType.Readback,ResourceDescription.Buffer(bytes),ResourceStates.CopyDest);
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
            if(nativeTaa)
            {
                nativeTaaHistory.Commit(frame,temporalUpscaling,taaPhase,taaPhaseCount,historyRayGiOptions);
                nativeTaaPass?.CommitHistory(completion);
            }
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
                runtime.Wait(completion);var pixels=destination??(rgba=new byte[checked(displayWidth*displayHeight*4)]);var data=readback!.Map<byte>(0,checked((int)bytes));
                try{for(int y=0;y<displayHeight;y++)data.Slice(y*footprints[0].Footprint.RowPitch,displayWidth*4).CopyTo(pixels.Span.Slice(y*displayWidth*4,displayWidth*4));}
                finally{readback.Unmap(0);}
                rayGiRenderer?.CompleteDiagnosticCapture();
            }
            Statistics=runtime.Statistics(LastSceneDrawCalls,LastPassDrawCalls,LastReadbackCount,LastSubmissionCount) with {CachedResources=cache.LiveResourceCount,RecordCpuMilliseconds=recordMilliseconds,GeometryCacheMilliseconds=geometryMilliseconds,
                TextureBindingMilliseconds=textureMilliseconds,TextureDescriptorWrites=descriptorWrites,TextureDescriptorSkips=descriptorSkips,SceneRecordMilliseconds=sceneMilliseconds,SubmitCpuMilliseconds=submitMilliseconds,
                SceneRecordCpu=originalRecorder.SceneRecordCpuStatistics,
                BeginFrameCpuMilliseconds=beginFrameMilliseconds,FrameTargetMilliseconds=frameTargetMilliseconds,
                AntialiasingCpuMilliseconds=antialiasingMilliseconds,EditorVisibilityCpuMilliseconds=editorVisibilityMilliseconds,
                EditorOverlayCpuMilliseconds=editorOverlayMilliseconds,AppendPresentCpuMilliseconds=appendPresentMilliseconds,
                RecordAllocatedBytes=recordAllocatedBytes,SceneRecordAllocatedBytes=sceneAllocatedBytes,EditorVisibilityAllocatedBytes=editorVisibilityAllocatedBytes,
                Antialiasing=antialiasing,AntialiasingPassDrawCalls=antialiasingDrawCalls,MsaaSampleCount=msaaSamples,
                AnisotropicFiltering=frame.Options?.AnisotropicFiltering??1,ShadowFilter=effectiveShadowFilter,
                ScreenSpaceAoPassDrawCalls=ssaoPassDrawCalls,SharpeningPassDrawCalls=sharpeningPassDrawCalls,SmaaQuality=smaaQuality,
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
            LastTargets=targetRecorder.Commit();
            LastRecordings=recordingRecords;
            // A pooled final image leaves the pool until the lease is disposed, so later frames cannot overwrite it.
            if(!capture)LastGpuOutput=new(displayWidth,displayHeight,runtime.FrameId,Statistics,output.Resource.QueryInterface<ID3D12Resource>(),
                runtime.Transients.Detach(output.Resource));
            return new(displayWidth,displayHeight,rgba,"Direct3D12ModernFrame",$"{AdapterName} ({FeatureLevel})",LastSceneDrawCalls+LastPassDrawCalls+antialiasingDrawCalls+spatialResolveDrawCalls+upscalingDrawCalls);
        }
        finally
        {
            targetRecorder.ResetStaged();
            if(runtime.IsRecording){rayGiRenderer?.AbortFrame();rayGiRenderer?.BeginFrame(retainTriangleScratch);if(temporal)temporalHistory.Invalidate("AbortedFrame");if(nativeTaa){nativeTaaHistory.Invalidate("AbortedFrame");nativeTaaPass?.AbortPending();}}
            runtime.Abort();
            if(debugQueue is not null){var messages=new List<string>();for(ulong i=0;i<debugQueue.NumStoredMessages;i++)messages.Add(debugQueue.GetMessage(i).ToString());LastDebugMessages=messages;}
            rendering=false;
        }
    }

    private static int ResolveSupportedMsaaSamples(ID3D12Device device, Format colorFormat, Format depthFormat, int requested)
    {
        if (requested <= 1) return 1;
        for (int count = requested; count >= 2; count /= 2)
        {
            var colorLevels = new FeatureDataMultisampleQualityLevels { Format = colorFormat, SampleCount = count };
            var depthLevels = new FeatureDataMultisampleQualityLevels { Format = depthFormat, SampleCount = count };
            if (device.CheckFeatureSupport(Vortice.Direct3D12.Feature.MultisampleQualityLevels, ref colorLevels)
                && colorLevels.NumQualityLevels > 0
                && device.CheckFeatureSupport(Vortice.Direct3D12.Feature.MultisampleQualityLevels, ref depthLevels)
                && depthLevels.NumQualityLevels > 0)
                return count;
        }
        return 1;
    }

    private static void ValidateOutputModes(OriginalFrameScene frame, bool capture,
        D3D12SpatialUpscalingMode spatialMode, D3D12TemporalUpscalingMode temporalMode, RayGiOptions? rayGiOptions = null)
    {
        var antialiasing = frame.Options?.Antialiasing ?? FrameAntialiasing.Off;
        if (frame.Options?.ScreenSpaceAo is { Enabled: true } && !(frame.Options?.WriteDepth ?? true))
            throw new NotSupportedException("Screen-space ambient occlusion requires surface depth writes (WriteDepth = true).");
        if (frame.Options?.ScreenSpaceAo is { Enabled: true } && rayGiOptions is { Enabled: true, OcclusionStrength: > 0f })
            throw new NotSupportedException("Screen-space ambient occlusion cannot be enabled together with Ray GI occlusion (OcclusionStrength > 0).");
        if (frame.Options?.AlphaOutput == FrameAlphaOutput.Straight)
        {
            D3D12AlphaResolvePass.ValidateCoverage(frame);
            if (temporalMode != D3D12TemporalUpscalingMode.Off || spatialMode != D3D12SpatialUpscalingMode.Off)
                throw new NotSupportedException("Straight-alpha output with spatial/temporal upscaling requires an explicit coverage contract and is not supported.");
            if (antialiasing == FrameAntialiasing.Taa)
                throw new NotSupportedException("Native TAA currently requires an opaque composited background; straight-alpha output is unsupported.");
            if (!capture && frame.EditorOverlay is { Lines.Count: > 0 })
                throw new NotSupportedException("Straight-alpha GPU output requires editor overlays to be disabled; their compositor uses the opaque viewport contract.");
        }
        if (temporalMode != D3D12TemporalUpscalingMode.Off)
        {
            if (spatialMode != D3D12SpatialUpscalingMode.Off || (frame.Options?.SpatialSamples ?? 1) != 1 || antialiasing != FrameAntialiasing.Off)
                throw new NotSupportedException("Temporal reconstruction requires spatial upscaling, frame antialiasing and spatial supersampling to be explicitly disabled.");
            if (frame.Options?.BackgroundColor is { W: not 1f })
                throw new NotSupportedException("Temporal reconstruction currently requires an opaque composited background.");
            var camera = frame.Options?.CameraOverride ?? frame.Surface.Snapshot.Camera;
            // The locked FSR provider reconstructs perspective view Z from near/far/FOV.
            // XeSS consumes device depth for motion dilation and receives no projection parameters.
            if (temporalMode == D3D12TemporalUpscalingMode.Fsr && camera.VerticalFovDegrees is null)
                throw new NotSupportedException("Temporal FSR currently requires a perspective camera; its installed provider has no orthographic projection contract.");
            if (temporalMode == D3D12TemporalUpscalingMode.Dlss && camera.VerticalFovDegrees is null)
                throw new NotSupportedException("Temporal DLSS currently requires a perspective camera; orthographic reconstruction has not been validated.");
            if (camera.ProjectionJitterPixels != Vector2.Zero)
                throw new NotSupportedException("Temporal reconstruction owns the camera jitter sequence; supply an unjittered effective camera.");
        }
        if (antialiasing == FrameAntialiasing.Taa)
        {
            if (spatialMode != D3D12SpatialUpscalingMode.Off || (frame.Options?.SpatialSamples ?? 1) != 1)
                throw new NotSupportedException("TAA cannot be combined with spatial supersampling, FSR1, or temporal upscaling.");
            if (frame.Options?.BackgroundColor is { W: not 1f })
                throw new NotSupportedException("Native TAA currently requires an opaque composited background.");
            var camera = frame.Options?.CameraOverride ?? frame.Surface.Snapshot.Camera;
            if (camera.ProjectionJitterPixels != Vector2.Zero)
                throw new NotSupportedException("Native TAA owns the camera jitter sequence; supply an unjittered effective camera.");
        }
        if (OriginalFrameMath.IsMsaa(antialiasing) && (spatialMode != D3D12SpatialUpscalingMode.Off || (frame.Options?.SpatialSamples ?? 1) != 1))
            throw new NotSupportedException("MSAA cannot be combined with spatial supersampling, FSR1, or temporal upscaling.");
        if (spatialMode == D3D12SpatialUpscalingMode.Fsr1 && frame.Options?.BackgroundColor is { W: not 1f })
            throw new NotSupportedException("FSR1 currently requires an opaque background; transparent coverage reconstruction is not implemented.");
    }

    internal bool[] GetMipmapTextures(OriginalFrameScene frame, D3D12SurfaceStyleMap? surfaceStyles = null)
        => mipmapCache.Get(frame, surfaceStyles);

    internal static bool[] ComputeMipmapTextures(OriginalFrameScene frame, D3D12SurfaceStyleMap? surfaceStyles = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var scene = frame.Surface.Snapshot;
        int textureCount = scene.Textures.Count;
        if (textureCount == 0) return [];
        var allow = new bool[textureCount];
        var disallow = new bool[textureCount];
        for (int i = 0; i < scene.Materials.Count; i++)
        {
            var material = scene.Materials[i];
            var binding = frame.Surface.Materials[i];
            bool useMipmaps = frame.Materials[i].UseMipmaps;
            if (material.DiffuseTexture >= 0)
            {
                int t = material.DiffuseTexture;
                if (useMipmaps && scene.Textures[t].Kind == SceneTextureKind.Image) allow[t] = true;
                else disallow[t] = true;
            }
            if (material.ToonTexture >= 0) disallow[material.ToonTexture] = true;
            if (binding.SphereTexture >= 0) disallow[binding.SphereTexture] = true;
        }
        if (surfaceStyles is not null)
        {
            void Disallow(int index)
            {
                if (index >= 0 && index < textureCount) disallow[index] = true;
            }
            int styleCount = Math.Min(surfaceStyles.Count, scene.Materials.Count);
            for (int i = 0; i < styleCount; i++)
            {
                if (!surfaceStyles.IsDefaultStyle(i))
                    Disallow(scene.Materials[i].DiffuseTexture);
                if (surfaceStyles.GetPluginDraw(i)?.TextureBindings is { } bindings)
                {
                    foreach (var binding in bindings)
                    {
                        int idx = binding.SceneTextureIndex;
                        if (idx < 0 || idx >= textureCount) continue;
                        if (binding.MipRule == D3D12PluginTextureMipRule.GenerateMips)
                        {
                            if (scene.Textures[idx].Kind == SceneTextureKind.Image)
                                allow[idx] = true;
                        }
                        else
                        {
                            Disallow(idx);
                        }
                    }
                }
            }
        }
        for (int t = 0; t < textureCount; t++)
            allow[t] &= !disallow[t];
        return allow;
    }

    private sealed record GeometryIdentity(ID3D12Resource Vertex,ID3D12Resource Index);
    private static ulong MeasurePluginStyleMemory(D3D12GpuRuntime runtime,SceneSnapshot scene,D3D12SurfaceStyleMap? surfaceStyles)
    {
        if(surfaceStyles is null || surfaceStyles.IsDefault)return 0;
        ulong Allocation(ResourceDescription description)
        {
            ulong bytes=runtime.Device.GetResourceAllocationInfo(0,[description]).SizeInBytes;
            if(bytes is 0 or ulong.MaxValue)throw new NotSupportedException("Device cannot estimate plugin style resources.");
            return bytes;
        }
        ulong reservation=0;
        var srgbSceneTextures=new HashSet<SceneTexture>(ReferenceEqualityComparer.Instance);
        var inlineAssets=new HashSet<string>(StringComparer.Ordinal);
        var structuredKeys=new HashSet<string>(StringComparer.Ordinal);
        int count=Math.Min(surfaceStyles.Count,scene.Materials.Count);
        for(int i=0;i<count;i++)
        {
            if(surfaceStyles.GetPluginDraw(i) is not {} draw)continue;
            if(draw.BufferBindings is not null)
            {
                foreach(var buf in draw.BufferBindings)
                {
                    if(buf.Kind==D3D12PluginBufferKind.MikkTangentCorners)
                    {
                        ulong cornerBytes=buf.Data.IsEmpty
                            ?checked((ulong)scene.Materials[i].IndexCount*64)
                            :(ulong)buf.Data.Length;
                        if(cornerBytes>0)
                        {
                            ulong cornerAllocation=Allocation(ResourceDescription.Buffer(cornerBytes));
                            reservation=checked(reservation+cornerAllocation*(D3D12SsaaGiMemoryBudget.MaximumInFlightFrames*2UL+1));
                        }
                    }
                    else if(buf.Kind==D3D12PluginBufferKind.CustomStructured)
                    {
                        string key=$"{buf.DataKey}:{buf.ElementStrideBytes}:{buf.ElementCount}";
                        if(!structuredKeys.Add(key))continue;
                        ulong byteCount=(ulong)(buf.Data.IsEmpty?checked(buf.ElementStrideBytes*buf.ElementCount):buf.Data.Length);
                        if(byteCount>0)
                        {
                            ulong bufAllocation=Allocation(ResourceDescription.Buffer(byteCount));
                            reservation=checked(reservation+bufAllocation*(D3D12SsaaGiMemoryBudget.MaximumInFlightFrames+1UL));
                        }
                    }
                }
            }
            if(draw.TextureBindings is not null)
            {
                foreach(var tex in draw.TextureBindings)
                {
                    if(tex.InlineTexture is {} inlineTex)
                    {
                        string assetKey=$"{inlineTex.AssetKey}:{inlineTex.Format}:{inlineTex.Width}:{inlineTex.Height}:{tex.Encoding}";
                        if(!inlineAssets.Add(assetKey))continue;
                        Format fmt=inlineTex.Format switch
                        {
                            D3D12PluginTexturePixelFormat.Rgba8Unorm=>tex.Encoding==D3D12PluginTextureEncoding.SrgbColor?Format.R8G8B8A8_UNorm_SRgb:Format.R8G8B8A8_UNorm,
                            D3D12PluginTexturePixelFormat.Rgba8UnormSrgb=>Format.R8G8B8A8_UNorm_SRgb,
                            D3D12PluginTexturePixelFormat.Rgba16Float=>Format.R16G16B16A16_Float,
                            D3D12PluginTexturePixelFormat.Rgba32Float=>Format.R32G32B32A32_Float,
                            D3D12PluginTexturePixelFormat.R8Unorm=>Format.R8_UNorm,
                            _=>Format.R8G8B8A8_UNorm,
                        };
                        var desc=ResourceDescription.Texture2D(fmt,(uint)inlineTex.Width,(uint)inlineTex.Height,mipLevels:1);
                        runtime.Device.GetCopyableFootprints(desc,0,1,0,new PlacedSubresourceFootPrint[1],new int[1],new ulong[1],out ulong stagingBytes);
                        reservation=checked(reservation+Allocation(desc)
                            +Allocation(ResourceDescription.Buffer(stagingBytes))*D3D12SsaaGiMemoryBudget.MaximumInFlightFrames);
                    }
                    else if(tex.SceneTextureIndex>=0&&tex.SceneTextureIndex<scene.Textures.Count&&tex.Encoding==D3D12PluginTextureEncoding.SrgbColor)
                    {
                        var sceneTex=scene.Textures[tex.SceneTextureIndex];
                        if(!srgbSceneTextures.Add(sceneTex))continue;
                        var desc=ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm_SRgb,(uint)sceneTex.Width,(uint)sceneTex.Height,mipLevels:1);
                        runtime.Device.GetCopyableFootprints(desc,0,1,0,new PlacedSubresourceFootPrint[1],new int[1],new ulong[1],out ulong stagingBytes);
                        reservation=checked(reservation+Allocation(desc)
                            +Allocation(ResourceDescription.Buffer(stagingBytes))*D3D12SsaaGiMemoryBudget.MaximumInFlightFrames);
                    }
                }
            }
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
    public void Dispose()
    {
        if (disposed) return;
        runtime.WaitForIdleForDisposal();
        disposed = true;
        mipmapCache.Clear();
        targetRecorder.Clear();
        // Native upscalers may report a teardown error after device loss. Release
        // independent pools and the owning device before reporting those failures.
        IDisposable?[] resources = [temporalPass, xeSsTemporalPass, dlssTemporalPass, nativeTaaPass, temporalInputs,
            rayGiRenderer, frameDescriptors, cache, ownsOriginalRecorder ? originalRecorder : null,
            ownsRuntime ? runtime : null];
        List<Exception>? failures = null;
        foreach (var resource in resources)
        {
            try { resource?.Dispose(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is not null) throw new AggregateException("D3D12 frame renderer disposal failed.", failures);
    }

    internal sealed class MipmapTextureCache
    {
        private ReadOnlyCollection<SceneTexture>? cachedTextures;
        private ReadOnlyCollection<SceneMaterial>? cachedMaterials;
        private ReadOnlyCollection<OriginalStyleMaterialBinding>? cachedBindings;
        private ReadOnlyCollection<OriginalFrameMaterialFlags>? cachedFlags;
        private D3D12SurfaceStyleMap? cachedSurfaceStyles;
        private bool[] cachedResult = [];

        internal bool[] Get(OriginalFrameScene frame, D3D12SurfaceStyleMap? surfaceStyles = null)
        {
            ArgumentNullException.ThrowIfNull(frame);
            var scene = frame.Surface.Snapshot;
            if (scene.Textures.Count == 0)
            {
                Clear();
                return [];
            }

            if (cachedTextures is not null
                && SameTextures(cachedTextures, scene.Textures)
                && SameMaterialTextureSlots(cachedMaterials!, scene.Materials)
                && SameSphereTextureSlots(cachedBindings!, frame.Surface.Materials)
                && SameUseMipmapFlags(cachedFlags!, frame.Materials)
                && SameMipmapStyleInputs(cachedSurfaceStyles, surfaceStyles, scene.Materials.Count))
            {
                return cachedResult;
            }

            bool[] computed = ComputeMipmapTextures(frame, surfaceStyles);
            cachedTextures = scene.Textures;
            cachedMaterials = scene.Materials;
            cachedBindings = frame.Surface.Materials;
            cachedFlags = frame.Materials;
            cachedSurfaceStyles = surfaceStyles;
            cachedResult = computed;
            return computed;
        }

        internal void Clear()
        {
            cachedTextures = null;
            cachedMaterials = null;
            cachedBindings = null;
            cachedFlags = null;
            cachedSurfaceStyles = null;
            cachedResult = [];
        }

        private static bool SameTextures(ReadOnlyCollection<SceneTexture> cached, ReadOnlyCollection<SceneTexture> current)
        {
            if (ReferenceEquals(cached, current)) return true;
            if (cached.Count != current.Count) return false;
            for (int i = 0; i < cached.Count; i++)
                if (!ReferenceEquals(cached[i], current[i]))
                    return false;
            return true;
        }

        private static bool SameMaterialTextureSlots(ReadOnlyCollection<SceneMaterial> cached, ReadOnlyCollection<SceneMaterial> current)
        {
            if (ReferenceEquals(cached, current)) return true;
            if (cached.Count != current.Count) return false;
            for (int i = 0; i < cached.Count; i++)
            {
                var a = cached[i];
                var b = current[i];
                if (!ReferenceEquals(a, b) && (a.DiffuseTexture != b.DiffuseTexture || a.ToonTexture != b.ToonTexture))
                    return false;
            }
            return true;
        }

        private static bool SameSphereTextureSlots(ReadOnlyCollection<OriginalStyleMaterialBinding> cached, ReadOnlyCollection<OriginalStyleMaterialBinding> current)
        {
            if (ReferenceEquals(cached, current)) return true;
            if (cached.Count != current.Count) return false;
            for (int i = 0; i < cached.Count; i++)
            {
                var a = cached[i];
                var b = current[i];
                if (!ReferenceEquals(a, b) && a.SphereTexture != b.SphereTexture)
                    return false;
            }
            return true;
        }

        private static bool SameUseMipmapFlags(ReadOnlyCollection<OriginalFrameMaterialFlags> cached, ReadOnlyCollection<OriginalFrameMaterialFlags> current)
        {
            if (ReferenceEquals(cached, current)) return true;
            if (cached.Count != current.Count) return false;
            for (int i = 0; i < cached.Count; i++)
            {
                var a = cached[i];
                var b = current[i];
                if (!ReferenceEquals(a, b) && a.UseMipmaps != b.UseMipmaps)
                    return false;
            }
            return true;
        }

        private static bool SameMipmapStyleInputs(D3D12SurfaceStyleMap? a, D3D12SurfaceStyleMap? b, int materialCount)
        {
            if (ReferenceEquals(a, b)) return true;
            bool aDefault = a is null || a.IsDefault;
            bool bDefault = b is null || b.IsDefault;
            if (aDefault || bDefault) return aDefault && bDefault;
            int styleCountA = Math.Min(a!.Count, materialCount);
            int styleCountB = Math.Min(b!.Count, materialCount);
            if (styleCountA != styleCountB) return false;
            for (int i = 0; i < styleCountA; i++)
            {
                if (a.IsDefaultStyle(i) != b.IsDefaultStyle(i)) return false;
                var texA = a.GetPluginDraw(i)?.TextureBindings;
                var texB = b.GetPluginDraw(i)?.TextureBindings;
                if (ReferenceEquals(texA, texB)) continue;
                int countA = texA?.Count ?? 0;
                int countB = texB?.Count ?? 0;
                if (countA != countB) return false;
                for (int j = 0; j < countA; j++)
                {
                    var ba = texA![j];
                    var bb = texB![j];
                    if (ba.SceneTextureIndex != bb.SceneTextureIndex || ba.MipRule != bb.MipRule)
                        return false;
                }
            }
            return true;
        }
    }

    internal sealed class PostEffectTargetRecorder
    {
        private readonly record struct TargetEntry(string Name, int Width, int Height, Format Format);

        private readonly List<TargetEntry> staged = new(16);
        private TargetEntry[] lastEntries = [];
        private IReadOnlyList<PostEffectTargetRecord> lastRecords = [];

        internal void Begin() => staged.Clear();

        internal void Add(D3D12GpuTarget target)
            => staged.Add(new TargetEntry(target.Name, target.Width, target.Height, target.Format));

        internal void Add(string name, int width, int height, Format format)
            => staged.Add(new TargetEntry(name, width, height, format));

        internal void ResetStaged() => staged.Clear();

        internal void Clear()
        {
            staged.Clear();
            lastEntries = [];
            lastRecords = [];
        }

        internal IReadOnlyList<PostEffectTargetRecord> Commit()
        {
            int count = staged.Count;
            if (count == 0)
            {
                if (lastEntries.Length != 0)
                {
                    lastEntries = [];
                    lastRecords = [];
                }
                return lastRecords;
            }

            if (count == lastEntries.Length)
            {
                bool same = true;
                for (int i = 0; i < count; i++)
                {
                    var a = staged[i];
                    var b = lastEntries[i];
                    if (a.Width != b.Width || a.Height != b.Height || a.Format != b.Format
                        || !string.Equals(a.Name, b.Name, StringComparison.Ordinal))
                    {
                        same = false;
                        break;
                    }
                }
                if (same)
                {
                    staged.Clear();
                    return lastRecords;
                }
            }

            var entries = new TargetEntry[count];
            var records = new PostEffectTargetRecord[count];
            for (int i = 0; i < count; i++)
            {
                var entry = staged[i];
                entries[i] = entry;
                records[i] = new PostEffectTargetRecord(entry.Name, entry.Width, entry.Height, FormatName(entry.Format));
            }

            lastEntries = entries;
            var published = Array.AsReadOnly(records);
            lastRecords = published;
            staged.Clear();
            return published;
        }

        internal static string FormatName(Format format) => format switch
        {
            Format.R8G8B8A8_UNorm => nameof(Format.R8G8B8A8_UNorm),
            Format.R8G8B8A8_UNorm_SRgb => nameof(Format.R8G8B8A8_UNorm_SRgb),
            Format.R16G16B16A16_Float => nameof(Format.R16G16B16A16_Float),
            Format.R16G16B16A16_UNorm => nameof(Format.R16G16B16A16_UNorm),
            Format.R32G32B32A32_Float => nameof(Format.R32G32B32A32_Float),
            Format.R11G11B10_Float => nameof(Format.R11G11B10_Float),
            Format.R10G10B10A2_UNorm => nameof(Format.R10G10B10A2_UNorm),
            Format.R16G16_Float => nameof(Format.R16G16_Float),
            Format.R8G8_UNorm => nameof(Format.R8G8_UNorm),
            Format.R8_UNorm => nameof(Format.R8_UNorm),
            Format.D32_Float => nameof(Format.D32_Float),
            Format.D24_UNorm_S8_UInt => nameof(Format.D24_UNorm_S8_UInt),
            Format.D16_UNorm => nameof(Format.D16_UNorm),
            _ => format.ToString(),
        };
    }
}
