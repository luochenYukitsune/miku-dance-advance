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

    internal sealed record ProjectPrewarmObjectSnapshot(
        bool HasSubTexture,
        bool HasTextureMorph,
        bool HasCharacterToon,
        bool HasCharacterDirectPalette,
        bool HasOutline,
        bool HasCharacterOutline,
        bool HasDynamicVertices,
        bool HasPaletteReceiver,
        int MaterialCount,
        string PresetStableId,
        D3D12MaterialBindingProfile? BindingProfile);

    internal sealed record ProjectPrewarmSnapshot(
        bool NativeLights,
        bool HasPaletteReceiver,
        IReadOnlyList<ProjectPrewarmObjectSnapshot> Objects);

    internal static ProjectPrewarmSnapshot CapturePrewarmSnapshot(
        SceneProjectSession session,
        IReadOnlyDictionary<Guid, D3D12MaterialBindingProfile> mappings)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(mappings);
        bool nativeLights = session.Lighting.Settings.EnvironmentStrength > 0
            || session.Lighting.EffectiveLights.Any(l => l.Enabled && l.Intensity > 0 && l.Color != Vector3.Zero);
        bool hasPaletteReceiver = false;
        var items = new List<ProjectPrewarmObjectSnapshot>(session.Objects.Count);
        foreach (var item in session.Objects)
        {
            var frame = SceneModelShadingPolicy.Apply(item.Source, session.GetModelPurpose(item.Id));
            bool subTexture = frame.Surface.Materials.Any(b => b.SphereMode == OriginalSphereMode.SubTexture && b.SphereTexture >= 0);
            bool textureMorph = subTexture || frame.Surface.Materials.Any(b => !b.TextureFactors.IsIdentity);
            bool characterToon = frame.Materials.Any(f => f.CharacterToonResponse is not null);
            bool directPalette = nativeLights && frame.Materials.Any(f => f.PreserveDirectLightPalette);
            bool outline = frame.Outline is not null && frame.OutlineOptions is { Eligible: true, ModelWidth: > 0 };
            bool charOutline = outline && frame.Materials.Any(f => f.CharacterOutlineStyle is not null);
            bool paletteReceiver = frame.Materials
                .Where((_, index) => frame.Surface.Snapshot.Materials[index].IndexCount > 0)
                .Any(flags => flags.GiToonResponseOverride == RayGiToonResponse.PreservePaletteBands);
            hasPaletteReceiver |= paletteReceiver;
            mappings.TryGetValue(item.Id, out var profile);
            items.Add(new ProjectPrewarmObjectSnapshot(
                subTexture,
                textureMorph,
                characterToon,
                directPalette,
                outline,
                charOutline,
                frame.Surface.Snapshot.HasDynamicVertices,
                paletteReceiver,
                frame.Surface.Snapshot.Materials.Count,
                session.GetRenderingStyle(item.Id).StableId,
                profile));
        }
        return new ProjectPrewarmSnapshot(nativeLights, hasPaletteReceiver, items);
    }

    internal static Task PrewarmProjectAsync(ProjectPrewarmSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (cancellationToken.IsCancellationRequested || snapshot.Objects.Count == 0)
            return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try
            {
                var quality = EditorPreferences.LoadPreviewQuality();
                var gi = quality.EffectiveGi;
                if (snapshot.HasPaletteReceiver && gi.Enabled && gi.Strength > 0 && gi.Composition == RayGiComposition.Additive)
                    quality = quality with { Gi = gi with { Composition = RayGiComposition.ToonPalette } };

                bool subTexture = false, textureMorph = false, characterToon = false, directPalette = false;
                bool outline = false, charOutline = false, dynamicVerts = false;
                var variants = new HashSet<D3D12ShaderStyleWarmupVariant>();
                var catalog = D3D12StylePresetCatalog.BuiltIn;

                foreach (var obj in snapshot.Objects)
                {
                    subTexture |= obj.HasSubTexture;
                    textureMorph |= obj.HasTextureMorph;
                    characterToon |= obj.HasCharacterToon;
                    directPalette |= obj.HasCharacterDirectPalette;
                    outline |= obj.HasOutline;
                    charOutline |= obj.HasCharacterOutline;
                    dynamicVerts |= obj.HasDynamicVertices;

                    var resolved = catalog.Find(obj.PresetStableId);
                    string? pipelineId = resolved.IsAvailable ? resolved.Definition?.PipelineId : null;
                    D3D12SurfaceStyle? family = pipelineId switch
                    {
                        "genshin" => D3D12SurfaceStyle.GenshinPmx,
                        "star-rail" => D3D12SurfaceStyle.StarRailPmx,
                        "wuthering-waves" => D3D12SurfaceStyle.WutheringPmx,
                        "endfield" => D3D12SurfaceStyle.EndfieldPmx,
                        _ => null,
                    };
                    if (family is { } fam)
                    {
                        int specializedCount = 0;
                        if (obj.BindingProfile is { } profile)
                        {
                            foreach (var row in profile.Materials)
                            {
                                bool hasAny = row.Face is not null || row.GenshinHair is not null || row.StarMcHair is not null
                                    || row.EndfieldHair is not null || row.EndfieldRim is not null || row.GenshinHairAtlas is not null
                                    || row.EndfieldMatcap is not null || row.GenshinMetallic is not null || row.StarMcBody is not null;
                                if (hasAny) specializedCount++;
                                variants.Add(new D3D12ShaderStyleWarmupVariant(
                                    Family: fam,
                                    FaceSdf: row.Face is not null,
                                    GenshinHair: row.GenshinHair is not null,
                                    StarMcHair: row.StarMcHair is not null,
                                    EndfieldHair: row.EndfieldHair is not null,
                                    EndfieldRim: row.EndfieldRim is not null,
                                    GenshinAtlas: row.GenshinHairAtlas is not null,
                                    EndfieldMatcap: row.EndfieldMatcap is not null,
                                    GenshinMetallic: row.GenshinMetallic is not null,
                                    GenshinMetallicSpecular: row.GenshinMetallic?.Specular is not null,
                                    EndfieldPalette: row.EndfieldMatcap?.Palette is not null,
                                    EndfieldBaseImage: row.EndfieldMatcap?.Palette?.BaseImage is not null,
                                    StarMcBody: row.StarMcBody is not null));
                            }
                        }
                        if (specializedCount < obj.MaterialCount)
                            variants.Add(new D3D12ShaderStyleWarmupVariant(fam));
                    }
                }

                var giOpts = quality.EffectiveGi;
                bool giEnabled = giOpts.Enabled && giOpts.Strength > 0;
                bool temporal = quality.EffectiveTemporalUpscaling.Mode != ViewTemporalUpscalingMode.Off;
                var spec = new D3D12ShaderWarmupSpec
                {
                    Gi = giEnabled,
                    GiMode = giOpts.Mode,
                    GiComposition = giOpts.Composition,
                    IncludeGiDenoiser = giEnabled && giOpts.Denoiser != RayGiDenoiser.Off,
                    IncludeGiTemporal = giEnabled && giOpts.Denoiser == RayGiDenoiser.TemporalSpatial,
                    IncludePositionGather = giEnabled && giOpts.Mode == LightingMode.HardwareRayTracing && dynamicVerts,
                    NativeLights = snapshot.NativeLights,
                    TextureMorph = textureMorph,
                    SubTexture = subTexture,
                    CharacterToon = characterToon,
                    CharacterDirectPalette = directPalette,
                    Outline = outline,
                    CharacterOutline = charOutline,
                    StylizedVariants = variants.ToArray(),
                    SmaaQuality = quality.Antialiasing == FrameAntialiasing.Smaa1X ? (quality.SmaaQuality ?? SmaaQuality.High) : null,
                    IncludeFsr1 = quality.EffectiveUpscaling.Mode != ViewUpscalingMode.Off,
                    IncludeTemporalInputs = temporal,
                    IncludeTemporalCharacter = temporal && (charOutline || subTexture || textureMorph || variants.Count > 0),
                    IncludeTemporalReactive = temporal && quality.EffectiveTemporalUpscaling.Mode == ViewTemporalUpscalingMode.XeSs,
                    IncludeTemporalColorTransfer = temporal,
                    IncludeDiffusion = quality.EffectiveDiffusion.Enabled && quality.EffectiveDiffusion.Strength > 0,
                    IncludeSpatialAccumulation = (quality.SpatialSamples ?? 1) > 1,
                };
                await D3D12ShaderPrewarmer.PrewarmAsync(spec, 0, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }, CancellationToken.None);
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
            var options = (scene.Options ?? new(OriginalAlphaBlendMode.Normal, true, false)) with
            { BackgroundColor = background, AlphaOutput = FrameAlphaOutput.Straight };
            return new(scene.Surface, scene.Outline, scene.OutlineOptions, options, scene.Materials, scene.Lighting, scene.EditorOverlay);
        }
    }
}
