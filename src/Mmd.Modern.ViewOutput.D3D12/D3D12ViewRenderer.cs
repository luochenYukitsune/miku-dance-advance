using Mmd.Modern.Application;
using Mmd.Modern.Core;
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
    private readonly D3D12ViewTemporalClock temporalClock = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    public ViewRendererCapabilities Capabilities { get; }
    public string AdapterName => session.AdapterName;
    public bool IsWarp => session.IsWarp;
    public ViewRendererDeviceInfo DeviceInfo { get; }
    public D3D12FrameStatistics? Statistics => session.Statistics;
    public D3D12RayGiStatistics? RayGiStatistics => session.RayGiStatistics;

    public D3D12ViewRenderer(ViewRenderRequest request, string? fsrLoaderPath = null, string? xessLibraryPath = null,
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
        this.stylePresets = stylePresets ?? D3D12StylePresetCatalog.BuiltIn; session = new();
        Capabilities = new(session.IsWarp ? "d3d12-warp" : "d3d12-hardware", 4096, 4096, true, false);
        DeviceInfo = new(Capabilities.BackendId, session.AdapterName, session.AdapterLuid, session.IsWarp);
    }

    public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
        => CaptureAsync(frame, new ViewTemporalFrameState(), cancellationToken);

    public void ValidateRenderingStyles(OriginalFrameScene scene, IReadOnlyList<SceneRenderingStyleBinding> bindings)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        D3D12ViewRenderingStyles.Preflight(scene, stylePresets, bindings, explicitMaterials, faceDataBindings, hairDataBindings, starMcHairDataBindings, endfieldHairDataBindings, genshinHairAtlasDataBindings, endfieldMatcapDataBindings, genshinMetallicDataBindings, starMcBodyDataBindings, outlineWidths).EnsureAvailable();
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
            var style = D3D12ViewRenderingStyles.Resolve(scene, stylePresets, frame.RenderingStyles, explicitMaterials,
                faceDataBindings, frame.SceneInputs, hairDataBindings, starMcHairDataBindings, endfieldHairDataBindings, genshinHairAtlasDataBindings, endfieldMatcapDataBindings, genshinMetallicDataBindings, starMcBodyDataBindings, outlineWidths);
            scene = style.Scene;
            scene = NormalizeCaptureAlpha(scene);
            D3D12ViewQuality.ValidateDiffusion(scene, request.EffectiveQuality);
            // Resolve discontinuities once for both independent histories; capture itself does not reset GI.
            var state = temporalClock.Resolve(frame.Sample, temporalState);
            var temporal = D3D12ViewQuality.Temporal(request.EffectiveQuality, state, fsrLoaderPath, xessLibraryPath, dlssBridgePath, dlssSdkDirectory);
            var result = session.Capture(scene, request.Width, request.Height, rayGiOptions: D3D12ViewQuality.Gi(request.EffectiveQuality, state),
                upscaling: D3D12ViewQuality.Upscaling(request.EffectiveQuality), temporalUpscaling: temporal,
                surfaceStyles: style.SurfaceStyles, diffusion: D3D12ViewQuality.Diffusion(request.EffectiveQuality),
                captureMemory: D3D12ViewCaptureMemory.Map(request.CaptureMemory));
            temporalClock.Commit(frame.Sample);
            cancellationToken.ThrowIfCancellationRequested();
            return new(result.Width, result.Height, checked(result.Width * 4), result.Rgba);
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
        scene = NormalizeCaptureAlpha(scene);
        D3D12ViewQuality.ValidateDiffusion(scene, request.EffectiveQuality);
        _ = D3D12ViewRenderingStyles.Resolve(scene, stylePresets, frame.RenderingStyles, explicitMaterials,
            faceDataBindings, frame.SceneInputs, hairDataBindings, starMcHairDataBindings, endfieldHairDataBindings, genshinHairAtlasDataBindings, endfieldMatcapDataBindings, genshinMetallicDataBindings, starMcBodyDataBindings, outlineWidths);
    }

    private static OriginalFrameScene NormalizeCaptureAlpha(OriginalFrameScene scene)
        => scene.Options is { BackgroundColor: { W: not 1f } } options
            ? new(scene.Surface, scene.Outline, scene.OutlineOptions,
                options with { AlphaOutput = FrameAlphaOutput.Straight }, scene.Materials, scene.Lighting, scene.EditorOverlay)
            : scene;

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        // Mark disposed only after the session released: a failed release must stay retryable by the cleanup ledger.
        try { if (disposed) return; session.Dispose(); disposed = true; } finally { gate.Release(); }
    }
}
