using Mmd.Modern.Application;
using Mmd.Modern.Core;
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
    private readonly D3D12StylePresetCatalog stylePresets;
    private readonly IReadOnlyList<D3D12ViewCharacterMaterialBinding> characterMaterials;
    private readonly IReadOnlyList<D3D12ViewCharacterMaterialBinding> explicitMaterials;
    private readonly IReadOnlyList<D3D12ViewFaceSdfBinding> faceDataBindings;
    private readonly IReadOnlyList<D3D12ViewGenshinHairBinding> hairDataBindings;
    private readonly IReadOnlyList<D3D12ViewStarMcHairBinding> starMcHairDataBindings;
    private readonly IReadOnlyList<D3D12ViewEndfieldHairBinding> endfieldHairDataBindings;
    private readonly IReadOnlyList<D3D12ViewGenshinHairAtlasBinding> genshinHairAtlasDataBindings;
    private readonly IReadOnlyList<D3D12ViewEndfieldMatcapBinding> endfieldMatcapDataBindings;
    private readonly IReadOnlyList<D3D12ViewGenshinMetallicBinding> genshinMetallicDataBindings;
    private readonly IReadOnlyList<D3D12ViewStarMcBodyBinding> starMcBodyDataBindings;
    private readonly IReadOnlyList<D3D12ViewOutlineWidthBinding> outlineWidths;
    public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests { get; }
    private bool disposed;
    public ViewRendererCapabilities Capabilities { get; }
    public string AdapterName => presenter.AdapterName;
    public bool IsWarp => presenter.IsWarp;
    public D3D12PresentationStatistics Statistics => presenter.Statistics with
    {
        RequestedSpatialSamples = previewSampling.LastRequestedSpatialSamples,
        EffectiveSpatialSamples = previewSampling.LastEffectiveSpatialSamples,
        PreviewSamplingAdjustmentReason = previewSampling.LastAdjustmentReason
    };

    public D3D12RealtimeViewRenderer(nint windowHandle, ViewRenderRequest request, string? fsrLoaderPath = null, string? xessLibraryPath = null,
        string? dlssBridgePath = null, string? dlssSdkDirectory = null, D3D12StylePresetCatalog? stylePresets = null,
        IEnumerable<D3D12ViewCharacterMaterialBinding>? characterMaterials = null,
        IEnumerable<D3D12ViewFaceSdfBinding>? faceDataBindings = null,
        IEnumerable<D3D12ViewGenshinHairBinding>? hairDataBindings = null,
        IEnumerable<D3D12ViewStarMcHairBinding>? starMcHairDataBindings = null,
        IEnumerable<D3D12ViewEndfieldHairBinding>? endfieldHairDataBindings = null,
        IEnumerable<D3D12ViewGenshinHairAtlasBinding>? genshinHairAtlasDataBindings = null,
        IEnumerable<D3D12ViewEndfieldMatcapBinding>? endfieldMatcapDataBindings = null,
        IEnumerable<D3D12ViewGenshinMetallicBinding>? genshinMetallicDataBindings = null,
        IEnumerable<D3D12ViewStarMcBodyBinding>? starMcBodyDataBindings = null,
        IEnumerable<D3D12ViewOutlineWidthBinding>? outlineWidths = null)
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
        this.request = request; this.fsrLoaderPath = fsrLoaderPath; this.xessLibraryPath = xessLibraryPath;
        this.dlssBridgePath = dlssBridgePath; this.dlssSdkDirectory = dlssSdkDirectory;
        this.outlineWidths = D3D12ViewOutlineWidths.Detach(outlineWidths);
        this.hairDataBindings = D3D12ViewGenshinHairBindings.Detach(hairDataBindings);
        this.starMcHairDataBindings = D3D12ViewStarMcHairBindings.Detach(starMcHairDataBindings);
        this.endfieldHairDataBindings = D3D12ViewEndfieldHairBindings.Detach(endfieldHairDataBindings);
        this.genshinHairAtlasDataBindings = D3D12ViewGenshinHairAtlasBindings.Detach(genshinHairAtlasDataBindings);
        explicitMaterials = D3D12ViewCharacterMaterials.Detach(characterMaterials);
        this.characterMaterials = D3D12ViewGenshinHairAtlasBindings.SelectCharacterMaterials(
            D3D12ViewEndfieldHairBindings.SelectCharacterMaterials(
            D3D12ViewStarMcHairBindings.SelectCharacterMaterials(
            D3D12ViewGenshinHairBindings.SelectCharacterMaterials(
                explicitMaterials, this.hairDataBindings),
            this.starMcHairDataBindings, this.hairDataBindings), this.endfieldHairDataBindings, this.hairDataBindings, this.starMcHairDataBindings), this.genshinHairAtlasDataBindings, this.starMcHairDataBindings, this.endfieldHairDataBindings);
        this.endfieldMatcapDataBindings = D3D12ViewEndfieldMatcapBindings.Detach(endfieldMatcapDataBindings);
        this.characterMaterials = D3D12ViewEndfieldMatcapBindings.SelectCharacterMaterials(this.characterMaterials,
            this.endfieldMatcapDataBindings, this.hairDataBindings, this.starMcHairDataBindings, this.genshinHairAtlasDataBindings, this.endfieldHairDataBindings);
        this.genshinMetallicDataBindings = D3D12ViewGenshinMetallicBindings.Detach(genshinMetallicDataBindings);
        this.characterMaterials = D3D12ViewGenshinMetallicBindings.SelectCharacterMaterials(this.characterMaterials,
            this.genshinMetallicDataBindings, this.hairDataBindings, this.genshinHairAtlasDataBindings,
            this.starMcHairDataBindings, this.endfieldHairDataBindings, this.endfieldMatcapDataBindings);
        this.starMcBodyDataBindings = D3D12ViewStarMcBodyBindings.Detach(starMcBodyDataBindings);
        this.characterMaterials = D3D12ViewStarMcBodyBindings.SelectCharacterMaterials(this.characterMaterials,
            this.starMcBodyDataBindings, this.hairDataBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex))
                .Concat(this.starMcHairDataBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex)))
                .Concat(this.endfieldHairDataBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex)))
                .Concat(this.genshinHairAtlasDataBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex)))
                .Concat(this.endfieldMatcapDataBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex)))
                .Concat(this.genshinMetallicDataBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex))));
        this.faceDataBindings = D3D12ViewFaceSdfBindings.Detach(faceDataBindings);
        D3D12ViewFaceSdfBindings.ValidateCharacterMaterials(this.characterMaterials, this.faceDataBindings);
        SceneInputRequests = D3D12ViewGenshinHairAtlasBindings.CreateRequests(this.genshinHairAtlasDataBindings,
            D3D12ViewEndfieldHairBindings.CreateRequests(this.endfieldHairDataBindings, this.faceDataBindings, this.hairDataBindings, this.starMcHairDataBindings, this.characterMaterials));
        SceneInputRequests = D3D12ViewEndfieldMatcapBindings.CreateRequests(this.endfieldMatcapDataBindings, SceneInputRequests, this.characterMaterials);
        SceneInputRequests = D3D12ViewGenshinMetallicBindings.CreateRequests(this.genshinMetallicDataBindings, SceneInputRequests);
        SceneInputRequests = D3D12ViewStarMcBodyBindings.CreateRequests(this.starMcBodyDataBindings, SceneInputRequests, this.characterMaterials);
        this.stylePresets = stylePresets ?? D3D12StylePresetCatalog.BuiltIn;
        presenter = new(windowHandle, request.Width, request.Height);
        Capabilities = new(presenter.IsWarp ? "d3d12-warp" : "d3d12-hardware", 4096, 4096, false, true);
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
            var style = D3D12ViewRenderingStyles.Resolve(scene, stylePresets, frame.RenderingStyles, explicitMaterials,
                faceDataBindings, frame.SceneInputs, hairDataBindings, starMcHairDataBindings, endfieldHairDataBindings, genshinHairAtlasDataBindings, endfieldMatcapDataBindings, genshinMetallicDataBindings, starMcBodyDataBindings, outlineWidths);
            scene = style.Scene;
            D3D12ViewQuality.ValidateDiffusion(scene, request.EffectiveQuality);
            previewSampling.Prepare(frame.Width, frame.Height, request.EffectiveQuality, scene, style.SurfaceStyles);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var prepared = previewSampling.ApplyTo(scene);
                    var gi = D3D12ViewQuality.Gi(request.EffectiveQuality, frame.TemporalState);
                    var upscaling = D3D12ViewQuality.Upscaling(request.EffectiveQuality);
                    var diffusion = D3D12ViewQuality.Diffusion(request.EffectiveQuality);
                    if (recordAhead)
                    {
                        if (request.EffectiveQuality.EffectiveTemporalUpscaling.Mode != ViewTemporalUpscalingMode.Off)
                            throw new InvalidOperationException("Recording ahead requires vendor temporal upscaling to be off.");
                        presenter.PresentRecordedAhead(prepared, frame.Width, frame.Height, gi, upscaling, style.SurfaceStyles, diffusion, cancellationToken);
                    }
                    else presenter.Present(prepared, frame.Width, frame.Height, gi, upscaling,
                        D3D12ViewQuality.Temporal(request.EffectiveQuality, frame.TemporalState, fsrLoaderPath, xessLibraryPath, dlssBridgePath, dlssSdkDirectory),
                        style.SurfaceStyles, diffusion);
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
        D3D12ViewRenderingStyles.Preflight(scene, stylePresets, bindings, explicitMaterials, faceDataBindings, hairDataBindings, starMcHairDataBindings, endfieldHairDataBindings, genshinHairAtlasDataBindings, endfieldMatcapDataBindings, genshinMetallicDataBindings, starMcBodyDataBindings, outlineWidths).EnsureAvailable();
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
            if (request.EffectiveQuality != quality) previewSampling.Reset();
            request = next;
        }
        finally { gate.Release(); }
    }
    public void ValidateSceneInputs(PreviewFrame frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ArgumentNullException.ThrowIfNull(frame);
        // Legacy renderers retain their old preflight path. Only this new data-dependent request
        // needs the exact current object source availability before the owner advances.
        if (starMcBodyDataBindings.Count == 0 && genshinMetallicDataBindings.Count == 0 && endfieldMatcapDataBindings.Count == 0 && genshinHairAtlasDataBindings.Count == 0 && endfieldHairDataBindings.Count == 0
            && !characterMaterials.Any(value => value.Style.EndfieldHair is not null || value.Style.EndfieldMatcap is not null || value.Style.GenshinMetallic is not null || value.Style.StarMcBody is not null)
            && !(request.EffectiveQuality.EffectiveDiffusion is { Enabled: true, Strength: > 0 })) return;
        var scene = D3D12ViewScene.Resolve(frame.Scene, frame.Camera, frame.Light, frame.SelfShadow,
            frame.MorphChannels, request.EffectiveQuality, frame.ResolvedCamera, frame.CameraParentTransform);
        D3D12ViewQuality.ValidateDiffusion(scene, request.EffectiveQuality);
        _ = D3D12ViewRenderingStyles.Resolve(scene, stylePresets, frame.RenderingStyles, explicitMaterials,
            faceDataBindings, frame.SceneInputs, hairDataBindings, starMcHairDataBindings, endfieldHairDataBindings, genshinHairAtlasDataBindings, endfieldMatcapDataBindings, genshinMetallicDataBindings, starMcBodyDataBindings, outlineWidths);
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        // Mark disposed only after the presenter released: a failed release must stay retryable by the cleanup ledger.
        try { if (disposed) return; presenter.Dispose(); disposed = true; } finally { gate.Release(); }
    }
}
