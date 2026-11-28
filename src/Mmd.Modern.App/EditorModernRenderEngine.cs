using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
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

    internal static IEditorRenderBinding CreatePreviewBinding(
        IEnumerable<D3D12ResolvedMaterialBindingProfile> bindings,
        D3D12ViewStyleCatalog? styleCatalog = null,
        D3D12StylePresetCatalog? stylePresets = null,
        IReadOnlyDictionary<string, StyleMaterialBindingData>? styleGlobalParameters = null,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var detached = bindings.ToArray();
        if (detached.Any(value => value is null))
            throw new ArgumentException("Material mappings cannot contain null entries.", nameof(bindings));
        return new ModernBinding(detached, false, styleCatalog, stylePresets, styleGlobalParameters, boneIndicesByObject);
    }

    public IEditorRenderBinding Bind(EditorProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Session is not { } source)
            return EmptyBinding;

        var styleCatalog = workspace.StyleCatalog;
        var stylePresets = workspace.PresetCatalog;
        var resolvedBindings = ResolveBindings(source, workspace.MaterialBindings, styleCatalog, stylePresets);
        var overrideCharacterMaterials = BuildEffectiveCharacterMaterials(
            source, resolvedBindings, workspace.MaterialBindings, workspace.GuessMaterialRoles, styleCatalog, stylePresets);
        return new ModernBinding(
            resolvedBindings,
            HasPaletteReceiver(source),
            styleCatalog,
            stylePresets,
            workspace.StyleGlobalParameters,
            BuildBoneIndicesByObject(source),
            overrideCharacterMaterials);
    }

    internal static IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? BuildBoneIndicesByObject(SceneProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var motions = session.CaptureAuthoredContent().Motions;
        if (motions.Count == 0)
            return null;

        var result = new Dictionary<Guid, IReadOnlyDictionary<string, int>>(motions.Count);
        foreach (var actor in motions)
        {
            var bones = actor.Model.Bones;
            if (bones.Count == 0)
                continue;
            var map = new Dictionary<string, int>(bones.Count, StringComparer.Ordinal);
            for (int i = 0; i < bones.Count; i++)
            {
                if (!string.IsNullOrEmpty(bones[i].Name))
                    map.TryAdd(bones[i].Name, i);
            }
            if (map.Count > 0)
                result[actor.ObjectId] = map;
        }
        return result.Count == 0 ? null : result;
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
        var nextGi = gi;
        if (hasPaletteReceiver && nextGi.Enabled && nextGi.Strength > 0 && nextGi.Composition == RayGiComposition.Additive)
            nextGi = nextGi with { Composition = RayGiComposition.ToonPalette };
        if (nextGi.PreviewAcceleration)
            nextGi = nextGi with { PreviewAcceleration = false };
        return nextGi == gi ? render : render with { Quality = quality with { Gi = nextGi } };
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
        bool HasPaletteReceiver);

    internal sealed record ProjectPrewarmSnapshot(
        bool NativeLights,
        bool HasPaletteReceiver,
        IReadOnlyList<ProjectPrewarmObjectSnapshot> Objects,
        D3D12ViewStyleCatalog StyleCatalog,
        IReadOnlyList<D3D12ViewCharacterMaterial> CharacterMaterials,
        IReadOnlyList<D3D12ViewPluginMaterialBinding> PluginBindings);

    internal static ProjectPrewarmSnapshot CapturePrewarmSnapshot(
        SceneProjectSession session,
        IReadOnlyDictionary<Guid, D3D12MaterialBindingProfile> mappings,
        D3D12ViewStyleCatalog? styleCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(mappings);
        var activeCatalog = styleCatalog ?? D3D12ViewStyleCatalog.Empty;
        var presetCatalog = activeCatalog.ToD3D12PresetCatalog();
        bool nativeLights = session.Lighting.Settings.EnvironmentStrength > 0
            || session.Lighting.EffectiveLights.Any(l => l.Enabled && l.Intensity > 0 && l.Color != Vector3.Zero);
        bool hasPaletteReceiver = false;
        var items = new List<ProjectPrewarmObjectSnapshot>(session.Objects.Count);
        var usedStyleIds = new HashSet<string>(StringComparer.Ordinal);
        var characterMaterials = new List<D3D12ViewCharacterMaterial>();
        var pluginBindings = new List<D3D12ViewPluginMaterialBinding>();

        AddReferencedStyle(session.RenderingStyles.DefaultStyle.StableId);
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
            items.Add(new ProjectPrewarmObjectSnapshot(
                subTexture,
                textureMorph,
                characterToon,
                directPalette,
                outline,
                charOutline,
                frame.Surface.Snapshot.HasDynamicVertices,
                paletteReceiver));

            var objectStyleRef = session.GetRenderingStyle(item.Id);
            AddReferencedStyle(objectStyleRef.StableId);
            if (mappings.TryGetValue(item.Id, out var profile))
            {
                foreach (var row in profile.Materials)
                {
                    foreach (var binding in row.PluginBindings)
                        AddReferencedStyle(binding.StyleId);
                }

                var resolvedPreset = presetCatalog.Find(objectStyleRef.StableId);
                if (resolvedPreset.IsAvailable && resolvedPreset.Definition is not null)
                {
                    try
                    {
                        var resolved = D3D12MaterialBindingProfileResolver.Resolve(
                            profile, item.Source, item.Id, resolvedPreset.Definition.PipelineId, activeCatalog);
                        characterMaterials.AddRange(resolved.CharacterMaterials);
                        pluginBindings.AddRange(resolved.PluginBindings);
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidDataException or NotSupportedException)
                    {
                        // Prewarm best-effort skips invalid or partially unresolvable bindings.
                    }
                }
            }
        }

        var usedStyles = usedStyleIds
            .Select(activeCatalog.FindStyle)
            .Where(static style => style is not null)
            .Cast<IRenderingStyleDefinition>()
            .ToArray();
        var filteredCatalog = usedStyles.Length == 0
            ? D3D12ViewStyleCatalog.Empty
            : new D3D12ViewStyleCatalog(usedStyles);

        return new ProjectPrewarmSnapshot(
            nativeLights,
            hasPaletteReceiver,
            items,
            filteredCatalog,
            characterMaterials,
            pluginBindings);

        void AddReferencedStyle(string styleOrPresetId)
        {
            if (string.IsNullOrWhiteSpace(styleOrPresetId))
                return;
            usedStyleIds.Add(styleOrPresetId);
            var resolved = presetCatalog.Find(styleOrPresetId);
            if (resolved.IsAvailable && resolved.Definition is not null && !string.IsNullOrWhiteSpace(resolved.Definition.PipelineId))
                usedStyleIds.Add(resolved.Definition.PipelineId);
        }
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
                foreach (var obj in snapshot.Objects)
                {
                    subTexture |= obj.HasSubTexture;
                    textureMorph |= obj.HasTextureMorph;
                    characterToon |= obj.HasCharacterToon;
                    directPalette |= obj.HasCharacterDirectPalette;
                    outline |= obj.HasOutline;
                    charOutline |= obj.HasCharacterOutline;
                    dynamicVerts |= obj.HasDynamicVertices;
                }

                var spec = D3D12ViewShaderPrewarm.BuildFromViewRequest(
                    quality,
                    styleCatalog: snapshot.StyleCatalog,
                    characterMaterials: snapshot.CharacterMaterials,
                    pluginBindings: snapshot.PluginBindings,
                    stylePresets: snapshot.StyleCatalog.ToD3D12PresetCatalog(),
                    hasNativeLights: snapshot.NativeLights,
                    hasTextureMorph: textureMorph,
                    hasSubTexture: subTexture,
                    hasCharacterToon: characterToon,
                    hasCharacterDirectPalette: directPalette,
                    hasOutline: outline,
                    hasCharacterOutline: charOutline,
                    hasDynamicVertices: dynamicVerts,
                    enableStylizedPolish: true);
                await D3D12ShaderPrewarmer.PrewarmAsync(spec, 0, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }, CancellationToken.None);
    }

    internal static IReadOnlyList<D3D12ResolvedMaterialBindingProfile> ResolveBindings(
        SceneProjectSession session,
        IReadOnlyDictionary<Guid, D3D12MaterialBindingProfile> mappings,
        D3D12ViewStyleCatalog? styleCatalog = null,
        D3D12StylePresetCatalog? stylePresets = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(mappings);
        var effectiveStyleCatalog = styleCatalog ?? D3D12ViewStyleCatalog.Empty;
        var effectivePresets = D3D12ViewRenderingStyles.MergePresetCatalog(
            stylePresets ?? effectiveStyleCatalog.ToD3D12PresetCatalog(),
            effectiveStyleCatalog);
        _ = RequirePipeline(session.RenderingStyles.DefaultStyle);
        var objects = session.Objects.ToDictionary(value => value.Id);
        if (mappings.Keys.Any(id => !objects.ContainsKey(id)))
            throw new ArgumentException(EditorLocalization.Text("材质映射引用了不存在的模型。"), nameof(mappings));
        List<D3D12ResolvedMaterialBindingProfile> result = [];
        foreach (var item in session.Objects)
        {
            string pipeline = RequirePipeline(session.GetRenderingStyle(item.Id));
            if (mappings.TryGetValue(item.Id, out var profile))
                result.Add(D3D12MaterialBindingProfileResolver.Resolve(profile, item.Source, item.Id, pipeline, effectiveStyleCatalog));
        }
        return result.AsReadOnly();

        string RequirePipeline(RenderingPresetReference reference)
        {
            var resolved = effectivePresets.Find(reference.StableId);
            if (!resolved.IsAvailable || resolved.Definition is null)
                throw new NotSupportedException(EditorLocalization.Format("画风预设“{0}”不可用。{1}", reference.StableId, resolved.Diagnostic));
            return resolved.Definition.PipelineId;
        }
    }

    private static D3D12ViewCharacterMaterial[]? BuildEffectiveCharacterMaterials(
        SceneProjectSession session,
        IReadOnlyList<D3D12ResolvedMaterialBindingProfile> resolvedBindings,
        IReadOnlyDictionary<Guid, D3D12MaterialBindingProfile> mappings,
        bool guessMaterialRoles,
        D3D12ViewStyleCatalog styleCatalog,
        D3D12StylePresetCatalog stylePresets)
    {
        if (guessMaterialRoles && mappings.Values.All(static profile => profile.GuessMaterialRoles))
            return null;

        var effectivePresets = D3D12ViewRenderingStyles.MergePresetCatalog(stylePresets, styleCatalog);
        var map = new Dictionary<(Guid ObjectId, int LocalMaterialIndex), D3D12ViewCharacterMaterial>();
        foreach (var profile in resolvedBindings)
        {
            foreach (var material in profile.CharacterMaterials)
                map[(material.ObjectId, material.LocalMaterialIndex)] = material;
        }

        var activePluginBindings = new Dictionary<(Guid ObjectId, int LocalMaterialIndex), List<StyleMaterialBindingData>>();
        foreach (var profile in resolvedBindings)
        {
            foreach (var binding in profile.PluginBindings)
            {
                var key = (binding.ObjectId, binding.LocalMaterialIndex);
                if (!activePluginBindings.TryGetValue(key, out var list))
                {
                    list = [];
                    activePluginBindings.Add(key, list);
                }
                list.Add(binding.Binding);
            }
        }

        foreach (var item in session.Objects)
        {
            if (item.Role != ModelRole.Character)
                continue;
            bool modelGuess = guessMaterialRoles
                && (!mappings.TryGetValue(item.Id, out var explicitProfile) || explicitProfile.GuessMaterialRoles);
            if (modelGuess)
                continue;

            var styleRef = session.GetRenderingStyle(item.Id);
            string pipelineId = effectivePresets.Find(styleRef.StableId).Definition?.PipelineId ?? D3D12SurfaceStyleMap.DefaultStyleId;
            var style = styleCatalog.FindStyle(pipelineId);
            var surfaceMaterials = item.Source.Surface.Snapshot.Materials;

            for (int localIdx = 0; localIdx < surfaceMaterials.Count; localIdx++)
            {
                var key = (item.Id, localIdx);
                if (map.TryGetValue(key, out var existing) && existing.ExplicitRole)
                    continue;

                D3D12CharacterMaterialRole nonGuessedRole;
                if (style is not null)
                {
                    IReadOnlyList<StyleMaterialBindingData>? matBindings = activePluginBindings.TryGetValue(key, out var list)
                        ? list.Where(b => string.Equals(b.StyleId, pipelineId, StringComparison.Ordinal)).ToArray()
                        : null;
                    var classification = style.ClassifyMaterial(new StyleMaterialInspectionContext(
                        item.Id,
                        localIdx,
                        surfaceMaterials[localIdx].Name,
                        ExplicitRole: null,
                        GuessMaterialRoles: false,
                        matBindings));
                    nonGuessedRole = D3D12StylePluginAdapter.ToD3D12Role(classification.ResolvedRole);
                }
                else
                {
                    nonGuessedRole = D3D12CharacterMaterialRole.Body;
                }

                if (existing is not null)
                {
                    var treatment = nonGuessedRole is D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair
                        ? (nonGuessedRole == D3D12CharacterMaterialRole.Body && existing.PmxTreatment is { HairHighlightStrength: > 0f } t
                            ? t with { HairHighlightStrength = 0f }
                            : existing.PmxTreatment)
                        : null;
                    map[key] = existing with { Role = nonGuessedRole, ExplicitRole = true, PmxTreatment = treatment };
                }
                else
                {
                    map[key] = new D3D12ViewCharacterMaterial(item.Id, localIdx, nonGuessedRole, ExplicitRole: true);
                }
            }
        }

        return [.. map.Values];
    }

    private sealed class ModernBinding : IEditorRenderBinding
    {
        private readonly D3D12ViewStyleCatalog styleCatalog;
        private readonly D3D12StylePresetCatalog stylePresets;
        private readonly D3D12ViewCharacterMaterial[] characterMaterials;
        private readonly D3D12ViewPluginMaterialBinding[] pluginBindings;
        private readonly D3D12ViewOutlineWidthBinding[] outlineWidths;
        private readonly IReadOnlyDictionary<string, StyleMaterialBindingData>? styleGlobalParameters;
        private readonly IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject;
        private readonly bool hasPaletteReceiver;
        private readonly EditorOptionalRuntimePaths paths;
        public IEditorRenderEngine Engine => Instance;

        internal ModernBinding(
            IReadOnlyList<D3D12ResolvedMaterialBindingProfile> bindings,
            bool hasPaletteReceiver,
            D3D12ViewStyleCatalog? styleCatalog = null,
            D3D12StylePresetCatalog? stylePresets = null,
            IReadOnlyDictionary<string, StyleMaterialBindingData>? styleGlobalParameters = null,
            IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject = null,
            D3D12ViewCharacterMaterial[]? overrideCharacterMaterials = null)
        {
            this.styleCatalog = styleCatalog ?? D3D12ViewStyleCatalog.Empty;
            this.stylePresets = D3D12ViewRenderingStyles.MergePresetCatalog(
                stylePresets ?? this.styleCatalog.ToD3D12PresetCatalog(),
                this.styleCatalog);
            characterMaterials = overrideCharacterMaterials ?? bindings.SelectMany(value => value.CharacterMaterials).ToArray();
            pluginBindings = bindings.SelectMany(value => value.PluginBindings).ToArray();
            outlineWidths = bindings.SelectMany(value => value.OutlineWidths).ToArray();
            this.styleGlobalParameters = styleGlobalParameters;
            this.boneIndicesByObject = boneIndicesByObject;
            this.hasPaletteReceiver = hasPaletteReceiver;
            paths = EditorOptionalRuntimePaths.Resolve();
        }

        public ExportRequest PrepareExport(ExportRequest request)
            => BuildExportRequest(request, hasPaletteReceiver);

        public IEditorViewportRenderer CreatePreview(nint window, ViewRenderRequest request)
            => new EditorViewportRenderer(new D3D12RealtimeViewRenderer(window, request,
                fsrLoaderPath: paths.FsrLoaderPath, xessLibraryPath: paths.XessLibraryPath,
                dlssBridgePath: paths.DlssBridgePath, dlssSdkDirectory: paths.DlssSdkDirectory,
                stylePresets: stylePresets, characterMaterials: characterMaterials,
                pluginBindings: pluginBindings, outlineWidths: outlineWidths,
                styleCatalog: styleCatalog, styleGlobalParameters: styleGlobalParameters,
                boneIndicesByObject: boneIndicesByObject,
                enableStylizedPolish: true));

        public IViewRenderer CreateCapture(ViewRenderRequest request, Vector4 background)
            => new OutputRenderer(new D3D12ViewRenderer(request,
                fsrLoaderPath: paths.FsrLoaderPath, xessLibraryPath: paths.XessLibraryPath,
                dlssBridgePath: paths.DlssBridgePath, dlssSdkDirectory: paths.DlssSdkDirectory,
                stylePresets: stylePresets, characterMaterials: characterMaterials,
                pluginBindings: pluginBindings, outlineWidths: outlineWidths,
                styleCatalog: styleCatalog, styleGlobalParameters: styleGlobalParameters,
                boneIndicesByObject: boneIndicesByObject,
                enableStylizedPolish: true), background);
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
