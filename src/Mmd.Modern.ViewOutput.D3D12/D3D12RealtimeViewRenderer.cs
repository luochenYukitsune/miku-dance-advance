using System.Collections.ObjectModel;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Backend-only HWND binding. Present uses the existing GPU swapchain path with no routine CPU capture.</summary>
public sealed class D3D12RealtimeViewRenderer : IRealtimeViewRenderer, IViewRenderingStylePreflight, IViewSceneInputRequests, IViewSceneInputPreflight
{
    private readonly D3D12SwapchainPresenter presenter;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly D3D12PreviewAdaptiveSampling previewSampling = new();
    private ViewRenderRequest request;
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
    private readonly D3D12ViewRenderingStylesCache styleCache;
    public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests { get; }
    private readonly object prewarmGate = new();
    private CancellationTokenSource? prewarmCts;
    private Task prewarmTask = Task.CompletedTask;
    private bool disposed;
    public ViewRendererCapabilities Capabilities { get; }
    public string AdapterName => presenter.AdapterName;
    public bool IsWarp => presenter.IsWarp;
    internal bool LastNativeTaaReset => presenter.LastNativeTaaReset;
    internal int NativeTaaPhase => presenter.NativeTaaPhase;
    public D3D12PresentationStatistics Statistics => presenter.Statistics with
    {
        RequestedSpatialSamples = previewSampling.LastRequestedSpatialSamples,
        EffectiveSpatialSamples = previewSampling.LastEffectiveSpatialSamples,
        PreviewSamplingAdjustmentReason = previewSampling.LastAdjustmentReason
    };

    public D3D12RealtimeViewRenderer(
        nint windowHandle,
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
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        new ViewRendererCapabilities("d3d12", 4096, 4096, false, true).Validate(request, capture: false);
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
        this.boneIndicesByObject = DetachBoneIndices(boneIndicesByObject);

        D3D12ViewPluginMaterialBindings.Validate(this.characterMaterials, this.pluginBindings, this.styleCatalog);
        styleCache = new D3D12ViewRenderingStylesCache(
            this.stylePresets,
            this.characterMaterials,
            this.pluginBindings,
            this.outlineWidths,
            this.styleCatalog,
            this.styleGlobalParameters,
            guessMaterialRoles: true,
            this.boneIndicesByObject);
        SceneInputRequests = D3D12ViewPluginMaterialBindings.CreateRequests(
            this.pluginBindings, this.styleCatalog, this.characterMaterials, this.boneIndicesByObject);

        presenter = new(windowHandle, request.Width, request.Height);
        Capabilities = new(presenter.IsWarp ? "d3d12-warp" : "d3d12-hardware", 4096, 4096, false, true);
        StartShaderPrewarm(request.EffectiveQuality);
    }

    /// <summary>Wait for DXGI to admit a new frame before the caller captures current scene state.</summary>
    public ValueTask WaitForNextFrameAsync(CancellationToken cancellationToken = default)
        => presenter.WaitForNextFrameAsync(cancellationToken);

    public ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default)
        => PresentCoreAsync(frame, false, cancellationToken);

    internal ValueTask PresentRecordedAheadAsync(PreviewFrame frame, CancellationToken cancellationToken)
        => PresentCoreAsync(frame, true, cancellationToken);

    private async ValueTask PresentCoreAsync(PreviewFrame frame, bool recordAhead, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); ArgumentNullException.ThrowIfNull(frame); cancellationToken.ThrowIfCancellationRequested();
            if (frame.Width != request.Width || frame.Height != request.Height) throw new InvalidOperationException("Resize the realtime renderer before presenting new dimensions.");
            var scene = D3D12ViewScene.Resolve(frame.Scene, frame.Camera, frame.Light, frame.SelfShadow, frame.MorphChannels, request.EffectiveQuality, frame.ResolvedCamera, frame.CameraParentTransform);
            var style = styleCache.Resolve(scene, frame.RenderingStyles, frame.SceneInputs);
            scene = D3D12ViewScene.ApplyPostStyleOutlineOverrides(style.Scene, request.EffectiveQuality, frame.RenderingStyles, outlineWidths);
            D3D12ViewQuality.ValidateDiffusion(scene, request.EffectiveQuality);
            previewSampling.Prepare(frame.Width, frame.Height, request.EffectiveQuality, scene, style.SurfaceStyles);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var prepared = previewSampling.ApplyTo(scene);
                    if (D3D12ViewQuality.PathTracing(request.EffectiveQuality, frame.TemporalState, interactive: false, scenePathTracing: prepared.Options?.PathTracing) is { } pathTracing && prepared.Options is not null)
                        prepared = new(prepared.Surface, prepared.Outline, prepared.OutlineOptions, prepared.Options with { PathTracing = pathTracing },
                            prepared.Materials, prepared.Lighting, prepared.EditorOverlay);
                    var gi = D3D12ViewQuality.Gi(request.EffectiveQuality, frame.TemporalState, allowPreviewAcceleration: true);
                    var upscaling = D3D12ViewQuality.Upscaling(request.EffectiveQuality);
                    var temporal = D3D12ViewQuality.Temporal(request.EffectiveQuality, frame.TemporalState, fsrLoaderPath, xessLibraryPath, dlssBridgePath, dlssSdkDirectory);
                    var diffusion = D3D12ViewQuality.Diffusion(request.EffectiveQuality);
                    var sharpening = D3D12ViewQuality.Sharpening(request.EffectiveQuality);
                    if (recordAhead)
                    {
                        if (request.EffectiveQuality.EffectiveTemporalUpscaling.Mode != ViewTemporalUpscalingMode.Off)
                            throw new InvalidOperationException("Recording ahead requires vendor temporal upscaling to be off.");
                        presenter.PresentRecordedAhead(prepared, frame.Width, frame.Height, gi, upscaling, style.SurfaceStyles, diffusion, cancellationToken, sharpening, temporal);
                    }
                    else presenter.Present(prepared, frame.Width, frame.Height, gi, upscaling, temporal,
                        style.SurfaceStyles, diffusion, sharpening);
                    previewSampling.CommitPresentation();
                    break;
                }
                // This exception is owned by pre-submit GI admission. Device/recording failures
                // and unrelated unsupported features must escape without replaying GPU work.
                catch (D3D12GiMemoryBudgetExceededException error) when (error.IsPreSubmit && previewSampling.Reduce(error.Message)) { }
            }
        }
        finally { gate.Release(); }
    }

    public void ValidateRenderingStyles(OriginalFrameScene scene, IReadOnlyList<SceneRenderingStyleBinding> bindings)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        D3D12ViewRenderingStyles.Preflight(
            scene, stylePresets, bindings, characterMaterials, pluginBindings, outlineWidths, styleCatalog).EnsureAvailable();
    }

    /// <summary>Drains in-flight GPU work now. The existing presenter applies its native backbuffer resize on the next Present.</summary>
    public async ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); cancellationToken.ThrowIfCancellationRequested();
            var next = request with { Width = width, Height = height }; Capabilities.Validate(next, capture: false);
            presenter.WaitForIdle();
            if (request.Width != width || request.Height != height) previewSampling.Reset();
            request = next;
        }
        finally { gate.Release(); }
    }

    /// <summary>Changes per-view processing on the existing presenter. In particular, final filters
    /// do not recreate the renderer or reset GI / temporal reconstruction histories.</summary>
    public async ValueTask UpdateQualityAsync(ViewQualityOptions quality, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quality); quality.Validate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); cancellationToken.ThrowIfCancellationRequested();
            var next = request with { Quality = quality };
            Capabilities.Validate(next, capture: false);
            if (request.EffectiveQuality != quality)
            {
                previewSampling.Reset();
                StartShaderPrewarm(quality);
            }
            request = next;
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
        D3D12ViewQuality.ValidateDiffusion(scene, request.EffectiveQuality);
        _ = styleCache.Resolve(scene, frame.RenderingStyles, frame.SceneInputs);
    }

    private void StartShaderPrewarm(ViewQualityOptions quality)
    {
        var spec = D3D12ViewShaderPrewarm.BuildFromViewRequest(
            quality, styleCatalog, characterMaterials, pluginBindings, stylePresets);
        CancellationTokenSource? prior;
        CancellationTokenSource next;
        lock (prewarmGate)
        {
            if (disposed) return;
            prior = prewarmCts;
            next = new();
            prewarmCts = next;
            prewarmTask = D3D12ShaderPrewarmer.PrewarmAsync(spec, 0, next.Token);
        }
        try { prior?.Cancel(); prior?.Dispose(); } catch { }
    }

    internal static IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? DetachBoneIndices(
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject)
    {
        if (boneIndicesByObject is null || boneIndicesByObject.Count == 0)
            return null;

        var copy = new Dictionary<Guid, IReadOnlyDictionary<string, int>>(boneIndicesByObject.Count);
        foreach (var (objectId, bones) in boneIndicesByObject)
        {
            if (objectId == Guid.Empty)
                throw new ArgumentException("Bone index map requires nonempty object identities.", nameof(boneIndicesByObject));
            if (bones is null)
                throw new ArgumentException("Bone index map entries cannot be null.", nameof(boneIndicesByObject));
            var boneCopy = new Dictionary<string, int>(bones.Count, StringComparer.Ordinal);
            foreach (var (name, idx) in bones)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("Bone names cannot be null or whitespace.", nameof(boneIndicesByObject));
                if (idx < 0)
                    throw new ArgumentOutOfRangeException(nameof(boneIndicesByObject), "Bone indices must be nonnegative.");
                boneCopy.Add(name, idx);
            }
            copy.Add(objectId, new ReadOnlyDictionary<string, int>(boneCopy));
        }
        return new ReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>(copy);
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
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
                try { await task.ConfigureAwait(false); } catch { }
                cts.Dispose();
            }
            presenter.Dispose();
        }
        finally { gate.Release(); }
    }
}
