using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Independently owned complete-frame capture session; only explicit export requests perform readback.</summary>
public sealed class D3D12ViewRenderer : IViewRenderer, IViewRenderingStylePreflight, IViewSceneInputRequests, IViewSceneInputPreflight, IViewRendererDeviceInfoProvider
{
    private readonly D3D12FrameSession session;
    private readonly ViewRenderRequest request;
    private readonly string? fsrLoaderPath;
    private readonly string? xessLibraryPath;
    private readonly string? dlssBridgePath;
    private readonly string? dlssSdkDirectory;
    private readonly D3D12ViewStyleCatalog styleCatalog;
    private readonly D3D12StylePresetCatalog stylePresets;
    private readonly IReadOnlyList<D3D12ViewCharacterMaterial> characterMaterials;
    private readonly IReadOnlyList<D3D12ViewPluginMaterialBinding> pluginBindings;
    private readonly IReadOnlyDictionary<string, StyleMaterialBindingData>? styleGlobalParameters;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject;
    private readonly IReadOnlyList<D3D12ViewOutlineWidthBinding> outlineWidths;
    private readonly bool enableStylizedPolish;
    public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests { get; }
    private readonly D3D12ViewTemporalClock temporalClock = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    // Pixel storage returned by disposed leases. It grows only to the number of leases consumers hold at once.
    private readonly Stack<byte[]> freePixels = new();
    private volatile bool disposed;
    public ViewRendererCapabilities Capabilities { get; }
    public string AdapterName => session.AdapterName;
    public bool IsWarp => session.IsWarp;
    public ViewRendererDeviceInfo DeviceInfo { get; }
    public D3D12FrameStatistics? Statistics => session.Statistics;
    public D3D12RayGiStatistics? RayGiStatistics => session.RayGiStatistics;
    public D3D12ClusteredLightStatistics? ClusteredLightStatistics => session.ClusteredLightStatistics;
    public D3D12PathTracingStatistics? PathTracingStatistics => session.PathTracingStatistics;
    public D3D12GpuSkinningStatistics? GpuSkinningStatistics => session.GpuSkinningStatistics;

    public D3D12ViewRenderer(
        ViewRenderRequest request,
        string? fsrLoaderPath = null,
        string? xessLibraryPath = null,
        string? dlssBridgePath = null,
        string? dlssSdkDirectory = null,
        D3D12StylePresetCatalog? stylePresets = null,
        IEnumerable<D3D12ViewCharacterMaterial>? characterMaterials = null,
        IEnumerable<D3D12ViewPluginMaterialBinding>? pluginBindings = null,
        IEnumerable<D3D12ViewOutlineWidthBinding>? outlineWidths = null,
        D3D12ViewStyleCatalog? styleCatalog = null,
        IReadOnlyDictionary<string, StyleMaterialBindingData>? styleGlobalParameters = null,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject = null,
        bool enableStylizedPolish = false)
    {
        ArgumentNullException.ThrowIfNull(request); request.Validate();
        // These are the existing recorder's actual limits, even though D3D12 hardware permits larger textures.
        new ViewRendererCapabilities("d3d12", 4096, 4096, true, false).Validate(request, capture: true);
        if (fsrLoaderPath is not null && !Path.IsPathFullyQualified(fsrLoaderPath))
            throw new ArgumentException("The optional FSR loader path must be absolute.", nameof(fsrLoaderPath));
        if (xessLibraryPath is not null && !Path.IsPathFullyQualified(xessLibraryPath))
            throw new ArgumentException("The optional XeSS library path must be absolute.", nameof(xessLibraryPath));
        if (dlssBridgePath is not null && !Path.IsPathFullyQualified(dlssBridgePath))
            throw new ArgumentException("The optional DLSS bridge path must be absolute.", nameof(dlssBridgePath));
        if (dlssSdkDirectory is not null && !Path.IsPathFullyQualified(dlssSdkDirectory))
            throw new ArgumentException("The optional DLSS component directory must be absolute.", nameof(dlssSdkDirectory));

        this.request = request;
        this.fsrLoaderPath = fsrLoaderPath;
        this.xessLibraryPath = xessLibraryPath;
        this.dlssBridgePath = dlssBridgePath;
        this.dlssSdkDirectory = dlssSdkDirectory;
        this.styleCatalog = styleCatalog ?? D3D12ViewStyleCatalog.Empty;
        this.stylePresets = D3D12ViewRenderingStyles.MergePresetCatalog(stylePresets ?? D3D12StylePresetCatalog.BuiltIn, this.styleCatalog);
        this.outlineWidths = D3D12ViewOutlineWidths.Detach(outlineWidths);
        this.characterMaterials = D3D12ViewCharacterMaterials.Detach(characterMaterials);
        this.pluginBindings = D3D12ViewPluginMaterialBindings.Detach(pluginBindings);
        this.styleGlobalParameters = D3D12ViewPluginMaterialBindings.DetachGlobalParameters(styleGlobalParameters);
        this.boneIndicesByObject = D3D12RealtimeViewRenderer.DetachBoneIndices(boneIndicesByObject);
        this.enableStylizedPolish = enableStylizedPolish;

        D3D12ViewPluginMaterialBindings.Validate(this.characterMaterials, this.pluginBindings, this.styleCatalog);
        SceneInputRequests = D3D12ViewPluginMaterialBindings.CreateRequests(
            this.pluginBindings, this.styleCatalog, this.characterMaterials, this.boneIndicesByObject);

        session = new();
        Capabilities = new(session.IsWarp ? "d3d12-warp" : "d3d12-hardware", 4096, 4096, true, false);
        DeviceInfo = new(Capabilities.BackendId, session.AdapterName, session.AdapterLuid, session.IsWarp);
    }

    public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
        => CaptureAsync(frame, new ViewTemporalFrameState(), cancellationToken);

    public void ValidateRenderingStyles(OriginalFrameScene scene, IReadOnlyList<SceneRenderingStyleBinding> bindings)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        D3D12ViewRenderingStyles.Preflight(
            scene, stylePresets, bindings, characterMaterials, pluginBindings, outlineWidths, styleCatalog).EnsureAvailable();
    }

    /// <summary>Explicit host seek/camera-cut markers; ordinary offline sequences also detect skipped/repeated samples.</summary>
    public async ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, ViewTemporalFrameState temporalState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(temporalState); temporalState.Validate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); ArgumentNullException.ThrowIfNull(frame);
            cancellationToken.ThrowIfCancellationRequested();
            if (frame.Width != request.Width || frame.Height != request.Height) throw new InvalidOperationException("The offline frame dimensions differ from this renderer's configured output.");
            var scene = D3D12ViewScene.Resolve(frame.Scene, frame.Camera, frame.Light, frame.SelfShadow, frame.MorphChannels, request.EffectiveQuality, frame.ResolvedCamera, frame.CameraParentTransform);
            var style = D3D12ViewRenderingStyles.Resolve(
                scene,
                stylePresets,
                frame.RenderingStyles,
                characterMaterials,
                pluginBindings,
                frame.SceneInputs,
                outlineWidths,
                styleCatalog,
                styleGlobalParameters,
                boneIndicesByObject: boneIndicesByObject);
            scene = D3D12ViewScene.ApplyPostStyleOutlineOverrides(style.Scene, request.EffectiveQuality, frame.RenderingStyles, outlineWidths);
            scene = NormalizeCaptureAlpha(scene);
            scene = D3D12ViewQuality.ApplyFocusedCharacterShadow(enableStylizedPolish, scene, style.SurfaceStyles);
            D3D12ViewQuality.ValidateDiffusion(scene, request.EffectiveQuality);
            var polish = D3D12ViewQuality.ResolveStylizedPolish(enableStylizedPolish, scene, style.SurfaceStyles, request.EffectiveQuality);
            // Resolve discontinuities once for both independent histories; capture itself does not reset GI.
            var state = temporalClock.Resolve(frame.Sample, temporalState);
            if (D3D12ViewQuality.PathTracing(request.EffectiveQuality, state, interactive: false, scenePathTracing: scene.Options?.PathTracing) is { } pathTracing && scene.Options is not null)
                scene = new(scene.Surface, scene.Outline, scene.OutlineOptions, scene.Options with { PathTracing = pathTracing },
                    scene.Materials, scene.Lighting, scene.EditorOverlay);
            var temporal = D3D12ViewQuality.Temporal(request.EffectiveQuality, state, fsrLoaderPath, xessLibraryPath, dlssBridgePath, dlssSdkDirectory);
            int requiredBytes = checked(request.Width * request.Height * 4);
            byte[]? pixels = null;
            lock (freePixels)
            {
                while (freePixels.Count > 0)
                {
                    var candidate = freePixels.Pop();
                    if (candidate.Length == requiredBytes) { pixels = candidate; break; }
                }
            }
            pixels ??= new byte[requiredBytes];
            void Release()
            {
                lock (freePixels)
                {
                    if (disposed || pixels.Length != requiredBytes) return;
                    freePixels.Push(pixels);
                }
            }
            try
            {
                session.CaptureInto(pixels, scene, request.Width, request.Height, rayGiOptions: D3D12ViewQuality.Gi(request.EffectiveQuality, state),
                    upscaling: D3D12ViewQuality.Upscaling(request.EffectiveQuality), temporalUpscaling: temporal,
                    surfaceStyles: style.SurfaceStyles, diffusion: D3D12ViewQuality.Diffusion(request.EffectiveQuality),
                    captureMemory: D3D12ViewCaptureMemory.Map(request.CaptureMemory),
                    sharpening: D3D12ViewQuality.Sharpening(request.EffectiveQuality),
                    depthRim: polish.DepthRim,
                    bloom: polish.Bloom,
                    toneCurve: polish.ToneCurve);
                temporalClock.Commit(frame.Sample);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch { Release(); throw; }
            // The storage is reused only after the consumer disposes this lease.
            return new(request.Width, request.Height, checked(request.Width * 4), pixels, Release);
        }
        finally { gate.Release(); }
    }

    public void ValidateSceneInputs(PreviewFrame frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ArgumentNullException.ThrowIfNull(frame);
        if (pluginBindings.Count == 0 && !(request.EffectiveQuality.EffectiveDiffusion is { Enabled: true, Strength: > 0 }))
            return;
        var scene = D3D12ViewScene.Resolve(frame.Scene, frame.Camera, frame.Light, frame.SelfShadow,
            frame.MorphChannels, request.EffectiveQuality, frame.ResolvedCamera, frame.CameraParentTransform);
        scene = NormalizeCaptureAlpha(scene);
        D3D12ViewQuality.ValidateDiffusion(scene, request.EffectiveQuality);
        _ = D3D12ViewRenderingStyles.Resolve(
            scene,
            stylePresets,
            frame.RenderingStyles,
            characterMaterials,
            pluginBindings,
            frame.SceneInputs,
            outlineWidths,
            styleCatalog,
            styleGlobalParameters,
            boneIndicesByObject: boneIndicesByObject);
    }

    private static OriginalFrameScene NormalizeCaptureAlpha(OriginalFrameScene scene)
        => scene.Options is { BackgroundColor: { W: not 1f } } options
            ? new(scene.Surface, scene.Outline, scene.OutlineOptions,
                options with { AlphaOutput = FrameAlphaOutput.Straight }, scene.Materials, scene.Lighting, scene.EditorOverlay)
            : scene;

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (freePixels)
            {
                if (disposed) return;
                disposed = true;
                freePixels.Clear();
            }
            session.Dispose();
        }
        finally { gate.Release(); }
    }
}
