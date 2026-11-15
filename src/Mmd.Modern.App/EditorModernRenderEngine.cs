using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;

namespace Mmd.Modern.App;

/// <summary>Owns all modern material, runtime and renderer construction policy. Bind on the
/// workspace owner; the resulting immutable inputs can then be consumed by a render worker.</summary>
internal sealed class EditorModernRenderEngine : IEditorRenderEngine
{
    internal static EditorModernRenderEngine Instance { get; } = new();
    private readonly Lazy<IEditorRenderBinding> empty = new(() => new ModernBinding([], false));
    public string Id => "modern.d3d12";
    public IEditorProjectPersistence Persistence { get; }
    public IEditorRenderBinding EmptyBinding => empty.Value;

    private EditorModernRenderEngine() => Persistence = new EditorModernProjectPersistence(this);

    internal static IEditorRenderBinding CreatePreviewBinding(IEnumerable<D3D12ResolvedMaterialBindingProfile> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var detached = bindings.ToArray();
        if (detached.Any(value => value is null))
            throw new ArgumentException("Material mappings cannot contain null entries.", nameof(bindings));
        return new ModernBinding(detached, false);
    }

    public IEditorRenderBinding Bind(EditorProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace.Session is not { } source ? EmptyBinding
            : new ModernBinding(ResolveBindings(source, workspace.MaterialBindings),
                HasPaletteReceiver(source));
    }

    // Resolve against every potential object, not just the current visible frame: animation can reveal
    // a character later in the output. The role policy also preserves explicit non-character overrides.
    internal static bool HasPaletteReceiver(SceneProjectSession source)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (var item in source.Objects)
        {
            var frame = SceneModelShadingPolicy.Apply(item.Source, source.GetModelPurpose(item.Id));
            if (frame.Materials.Where((flags, index) => frame.Surface.Snapshot.Materials[index].IndexCount > 0)
                .Any(flags => flags.GiToonResponseOverride == RayGiToonResponse.PreservePaletteBands)) return true;
        }
        return false;
    }

    // The export owns this copy; neither the editable draft nor the independent preview is changed.
    internal static ViewRenderRequest BuildRenderRequest(ExportRequest request, bool hasPaletteReceiver)
    {
        ArgumentNullException.ThrowIfNull(request);
        var render = request.Render;
        var quality = render.EffectiveQuality;
        var gi = quality.EffectiveGi;
        return hasPaletteReceiver && gi.Enabled && gi.Strength > 0 && gi.Composition == RayGiComposition.Additive
            ? render with { Quality = quality with { Gi = gi with { Composition = RayGiComposition.ToonPalette } } }
            : render;
    }

    internal static ExportRequest BuildExportRequest(ExportRequest request, bool hasPaletteReceiver)
    {
        var render = BuildRenderRequest(request, hasPaletteReceiver);
        return render == request.Render ? request : request with { Quality = render.Quality };
    }

    internal static IReadOnlyList<D3D12ResolvedMaterialBindingProfile> ResolveBindings(SceneProjectSession session,
        IReadOnlyDictionary<Guid, D3D12MaterialBindingProfile> mappings)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(mappings);
        var catalog = D3D12StylePresetCatalog.BuiltIn;
        _ = RequirePipeline(session.RenderingStyles.DefaultStyle);
        var objects = session.Objects.ToDictionary(value => value.Id);
        if (mappings.Keys.Any(id => !objects.ContainsKey(id)))
            throw new ArgumentException(EditorLocalization.Text("材质映射引用了不存在的模型。"), nameof(mappings));
        List<D3D12ResolvedMaterialBindingProfile> result = [];
        foreach (var item in session.Objects)
        {
            string pipeline = RequirePipeline(session.GetRenderingStyle(item.Id));
            if (mappings.TryGetValue(item.Id, out var profile))
                result.Add(D3D12MaterialBindingProfileResolver.Resolve(profile, item.Source, item.Id, pipeline));
        }
        return result.AsReadOnly();

        string RequirePipeline(RenderingPresetReference reference)
        {
            var resolved = catalog.Find(reference.StableId);
            if (!resolved.IsAvailable || resolved.Definition is null)
                throw new NotSupportedException(EditorLocalization.Format("画风预设“{0}”不可用。{1}", reference.StableId, resolved.Diagnostic));
            return resolved.Definition.PipelineId;
        }
    }

    private sealed class ModernBinding : IEditorRenderBinding
    {
        private readonly D3D12ViewCharacterMaterialBinding[] characterMaterials;
        private readonly D3D12ViewFaceSdfBinding[] faces;
        private readonly D3D12ViewGenshinHairBinding[] genshinHair;
        private readonly D3D12ViewStarMcHairBinding[] starMcHair;
        private readonly D3D12ViewEndfieldHairBinding[] endfieldHair;
        private readonly D3D12ViewGenshinHairAtlasBinding[] genshinHairAtlas;
        private readonly D3D12ViewEndfieldMatcapBinding[] endfieldMatcap;
        private readonly D3D12ViewGenshinMetallicBinding[] genshinMetallic;
        private readonly D3D12ViewStarMcBodyBinding[] starMcBody;
        private readonly D3D12ViewOutlineWidthBinding[] outlineWidths;
        private readonly bool hasPaletteReceiver;
        private readonly EditorOptionalRuntimePaths paths;
        public IEditorRenderEngine Engine => Instance;

        internal ModernBinding(IReadOnlyList<D3D12ResolvedMaterialBindingProfile> bindings, bool hasPaletteReceiver)
        {
            characterMaterials = bindings.SelectMany(value => value.CharacterMaterials).ToArray();
            faces = bindings.SelectMany(value => value.FaceBindings).ToArray();
            genshinHair = bindings.SelectMany(value => value.GenshinHairBindings).ToArray();
            starMcHair = bindings.SelectMany(value => value.StarMcHairBindings).ToArray();
            endfieldHair = bindings.SelectMany(value => value.EndfieldHairBindings).ToArray();
            genshinHairAtlas = bindings.SelectMany(value => value.GenshinHairAtlasBindings).ToArray();
            endfieldMatcap = bindings.SelectMany(value => value.EndfieldMatcapBindings).ToArray();
            genshinMetallic = bindings.SelectMany(value => value.GenshinMetallicBindings).ToArray();
            starMcBody = bindings.SelectMany(value => value.StarMcBodyBindings).ToArray();
            outlineWidths = bindings.SelectMany(value => value.OutlineWidths).ToArray();
            this.hasPaletteReceiver = hasPaletteReceiver;
            paths = EditorOptionalRuntimePaths.Resolve();
        }

        public ExportRequest PrepareExport(ExportRequest request)
            => BuildExportRequest(request, hasPaletteReceiver);

        public IEditorViewportRenderer CreatePreview(nint window, ViewRenderRequest request)
            => new EditorViewportRenderer(new D3D12RealtimeViewRenderer(window, request,
                fsrLoaderPath: paths.FsrLoaderPath, xessLibraryPath: paths.XessLibraryPath,
                dlssBridgePath: paths.DlssBridgePath, dlssSdkDirectory: paths.DlssSdkDirectory,
                characterMaterials: characterMaterials, faceDataBindings: faces, hairDataBindings: genshinHair,
                starMcHairDataBindings: starMcHair, endfieldHairDataBindings: endfieldHair,
                genshinHairAtlasDataBindings: genshinHairAtlas, endfieldMatcapDataBindings: endfieldMatcap,
                genshinMetallicDataBindings: genshinMetallic, starMcBodyDataBindings: starMcBody, outlineWidths: outlineWidths));

        public IViewRenderer CreateCapture(ViewRenderRequest request, Vector4 background)
            => new OutputRenderer(new D3D12ViewRenderer(request,
                fsrLoaderPath: paths.FsrLoaderPath, xessLibraryPath: paths.XessLibraryPath,
                dlssBridgePath: paths.DlssBridgePath, dlssSdkDirectory: paths.DlssSdkDirectory,
                characterMaterials: characterMaterials, faceDataBindings: faces, hairDataBindings: genshinHair,
                starMcHairDataBindings: starMcHair, endfieldHairDataBindings: endfieldHair,
                genshinHairAtlasDataBindings: genshinHairAtlas, endfieldMatcapDataBindings: endfieldMatcap,
                genshinMetallicDataBindings: genshinMetallic, starMcBodyDataBindings: starMcBody, outlineWidths: outlineWidths), background);
    }

    private sealed class OutputRenderer(D3D12ViewRenderer renderer, Vector4 background) : IViewRenderer,
        IViewRenderingStylePreflight, IViewSceneInputRequests, IViewSceneInputPreflight, IViewRendererDeviceInfoProvider
    {
        public ViewRendererCapabilities Capabilities => renderer.Capabilities;
        public ViewRendererDeviceInfo DeviceInfo => renderer.DeviceInfo;
        public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests => renderer.SceneInputRequests;
        public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
            => renderer.CaptureAsync(frame with { Scene = ApplyBackground(frame.Scene) }, cancellationToken);
        public void ValidateRenderingStyles(OriginalFrameScene scene, IReadOnlyList<SceneRenderingStyleBinding> bindings)
            => renderer.ValidateRenderingStyles(ApplyBackground(scene), bindings);
        public void ValidateSceneInputs(PreviewFrame frame)
            => renderer.ValidateSceneInputs(frame with { Scene = ApplyBackground(frame.Scene) });
        public ValueTask DisposeAsync() => renderer.DisposeAsync();
        private OriginalFrameScene ApplyBackground(OriginalFrameScene scene)
        {
            // Straight alpha only matters when the background is transparent. Forcing it for opaque exports made the
            // renderer reject every spatial/temporal upscaler (FSR, XeSS, DLSS) on the first frame, although the
            // same settings render in the preview. Keep the scene's own alpha mode for an opaque background.
            var options = scene.Options ?? new(OriginalAlphaBlendMode.Normal, true, false);
            options = options with
            {
                BackgroundColor = background,
                AlphaOutput = background.W < 1 ? FrameAlphaOutput.Straight : options.AlphaOutput,
            };
            return new(scene.Surface, scene.Outline, scene.OutlineOptions, options, scene.Materials, scene.Lighting, scene.EditorOverlay);
        }
    }
}
