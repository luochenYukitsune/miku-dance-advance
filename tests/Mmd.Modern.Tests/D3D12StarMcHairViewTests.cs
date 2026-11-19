using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12StarMcHairViewTests
{
    private static readonly D3D12StylePresetCatalog Catalog = TestStylePlugins.AllPresetCatalog;
    private static readonly D3D12ViewStyleCatalog ViewCatalog = TestStylePlugins.AllViewCatalog;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void DetachedInputsUnionTwoImagesWithExistingFaceBonesAndGenshinRequests()
    {
        var id = Guid.NewGuid(); var other = Guid.NewGuid();
        D3D12ViewPluginMaterialBinding[] source = [
            new(id, 0, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 2, 3)),
            new(other, 0, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1)),
            new(id, 1, T8cTestHelpers.FaceSdfBinding(T8cTestHelpers.GenshinStyleId, 1, 2,
                trackingMode: StyleBoneTrackingMode.SelectedBone, boneName: "head")),
            new(id, 2, TestStylePlugins.GenshinHair(4)),
        ];
        var detached = D3D12ViewPluginMaterialBindings.Detach(source);
        source[0] = new(id, 0, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 99, 3));
        Assert.Equal(2, detached[0].Binding.TextureSlots!["highlight"]);
        Assert.Same(detached, D3D12ViewPluginMaterialBindings.Detach(detached));
        D3D12ViewCharacterMaterial[] charMats = [
            new(id, 0, D3D12CharacterMaterialRole.Hair),
            new(other, 0, D3D12CharacterMaterialRole.Hair),
            new(id, 1, D3D12CharacterMaterialRole.Face),
            new(id, 2, D3D12CharacterMaterialRole.Hair),
        ];
        var boneIndices = new Dictionary<Guid, IReadOnlyDictionary<string, int>>
        {
            [id] = new Dictionary<string, int>(StringComparer.Ordinal) { ["head"] = 4 },
        };
        var requests = D3D12ViewPluginMaterialBindings.CreateRequests(detached, ViewCatalog, charMats, boneIndices);
        var item = Assert.Single(requests, request => request.ObjectId == id);
        Assert.Equal(new[] { 4 }, item.BoneIndices);
        Assert.Equal(new[] { 1, 2, 3, 4 }, item.LocalTextureIndices.Order());
        Assert.Equal(new[] { 0, 1 }, Assert.Single(requests, request => request.ObjectId == other).LocalTextureIndices);
    }

    [Theory, InlineData("star-rail"), InlineData("wuthering-waves")]
    public void SameSampleMapsRebaseBothLocalZeroAndOneAcrossIndependentObjects(string preset)
    {
        var layout = new SceneCompositionEditor();
        var a = layout.Add("a", SourcePath, ModelRole.Character, Source(64));
        var b = layout.Add("b", SourcePath, ModelRole.Character, Source(192));
        using var project = Project(layout); project.SetDefaultRenderingStyle(new(preset), Token);
        D3D12ViewPluginMaterialBinding[] hair = [
            new(a.Id, 0, T8cTestHelpers.StarMcHairBinding(preset, 0, 1)),
            new(b.Id, 0, T8cTestHelpers.StarMcHairBinding(preset, 0, 1, highlightBrightness: 3))
        ];
        D3D12ViewCharacterMaterial[] charMats = [
            new(a.Id, 0, D3D12CharacterMaterialRole.Hair),
            new(b.Id, 0, D3D12CharacterMaterialRole.Hair),
        ];
        var snapshot = project.CaptureRenderSnapshot(D3D12ViewPluginMaterialBindings.CreateRequests(hair, ViewCatalog, charMats));
        var result = Resolve(snapshot, hair, charMats);
        Assert.Equal(4, snapshot.Scene.Surface.Snapshot.Textures.Count);
        foreach (var instance in snapshot.RenderingStyles)
        {
            var selected = result.SurfaceStyles.CharacterMaterials[instance.MaterialStart];
            Assert.Equal(D3D12CharacterMaterialRole.Hair, selected.Role);
            var draw = Assert.IsType<D3D12PluginDrawData>(selected.PluginDraw);
            Assert.Equal("hair", draw.ShaderModuleId);
            var indices = snapshot.SceneInputs[instance.ObjectId].TextureIndices;
            Assert.Equal(indices[0], draw.TextureBindings![0].SceneTextureIndex);
            Assert.Equal(indices[1], draw.TextureBindings[1].SceneTextureIndex);
            Assert.Equal(instance.ObjectId == a.Id ? 64 : 192,
                snapshot.Scene.Surface.Snapshot.Textures[indices[0]].Rgba[2]);
            var floats = MemoryMarshal.Cast<byte, float>(draw.ConstantBufferData.Span);
            Assert.Equal(instance.ObjectId == a.Id ? 2f : 3f, floats[1]);
        }
        Assert.Throws<ArgumentException>(() => Resolve(project.CaptureRenderSnapshot(), hair, charMats));
        var partial = project.CaptureRenderSnapshot([new(a.Id, localTextureIndices: [0]), new(b.Id, localTextureIndices: [0, 1])]);
        Assert.Throws<ArgumentException>(() => Resolve(partial, hair, charMats));
        Assert.Equal(0, project.CurrentFrame);
    }

    [Theory, InlineData("mmd"), InlineData("genshin"), InlineData("endfield")]
    public void LocalConfigurationRemainsDormantAndDoesNotChangeOtherFamilies(string preset)
    {
        var layout = new SceneCompositionEditor();
        var item = layout.Add("selected", SourcePath, ModelRole.Character, Source(128));
        using var project = Project(layout); project.SetDefaultRenderingStyle(new(preset), Token);
        var snapshot = project.CaptureRenderSnapshot();
        D3D12ViewPluginMaterialBinding[] hair = [
            new(item.Id, 0, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1))
        ];
        D3D12ViewCharacterMaterial[] charMats = [new(item.Id, 0, D3D12CharacterMaterialRole.Hair)];
        Assert.True(D3D12ViewRenderingStyles.Preflight(snapshot.Scene, Catalog, snapshot.RenderingStyles,
            characterMaterials: charMats, pluginBindings: hair, styleCatalog: ViewCatalog).IsAvailable);
        var ordinary = Resolve(snapshot, [], charMats); var dormant = Resolve(snapshot, hair, charMats);
        Assert.Null(dormant.SurfaceStyles.GetPluginDraw(0));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(ordinary.SurfaceStyles, dormant.SurfaceStyles));
        Assert.Equal(preset, project.GetRenderingStyle(item.Id).StableId);
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(snapshot.Scene, Catalog,
            snapshot.RenderingStyles,
            characterMaterials: [new(item.Id, 0, D3D12CharacterMaterialRole.Body)],
            pluginBindings: [new(item.Id, 0, T8cTestHelpers.StarMcHairBinding(preset, 0, 1))],
            styleCatalog: ViewCatalog));
    }

    [Fact]
    public void InvalidCrossFamilyAndLocalComposedConflictsRejectBeforeNativeConstruction()
    {
        var id = Guid.NewGuid();
        var valid = new D3D12ViewPluginMaterialBinding(id, 0, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1));
        D3D12ViewCharacterMaterial[] hairRole = [new(id, 0, D3D12CharacterMaterialRole.Hair)];
        D3D12ViewPluginMaterialBinding[][] invalid = [
            [null!],
            [valid, valid],
            [new(Guid.Empty, 0, valid.Binding)],
            [new(id, -1, valid.Binding)],
            [new(id, 0, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, -1))],
            [new(id, 0, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, -1, 1))],
            [new(id, 0, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1, shadowSoftness: 0))],
            [new(id, 0, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1, "Invalid"))],
            [new(id, 0, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1, intensity: float.NaN))]
        ];
        foreach (var value in invalid)
        {
            Assert.ThrowsAny<ArgumentException>(() => new D3D12ViewRenderer(new(97, 73),
                stylePresets: Catalog, characterMaterials: hairRole, pluginBindings: value, styleCatalog: ViewCatalog));
            Assert.ThrowsAny<ArgumentException>(() => new D3D12RealtimeViewRenderer(0, new(97, 73),
                stylePresets: Catalog, characterMaterials: hairRole, pluginBindings: value, styleCatalog: ViewCatalog));
        }
        foreach (var role in new[] { D3D12CharacterMaterialRole.Body, D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye })
        {
            D3D12ViewCharacterMaterial[] materials = [new(id, 0, role)];
            Assert.Throws<ArgumentException>(() => new D3D12ViewRenderer(new(97, 73),
                stylePresets: Catalog, characterMaterials: materials, pluginBindings: [valid], styleCatalog: ViewCatalog));
            Assert.Throws<ArgumentException>(() => new D3D12RealtimeViewRenderer(0, new(97, 73),
                stylePresets: Catalog, characterMaterials: materials, pluginBindings: [valid], styleCatalog: ViewCatalog));
        }
        D3D12ViewPluginMaterialBinding[] sameStyleConflict = [
            valid,
            new(id, 0, T8cTestHelpers.StarMcBodyBinding(T8cTestHelpers.StarRailStyleId, 0, 1, 2, 3))
        ];
        Assert.Throws<ArgumentException>(() => new D3D12ViewRenderer(new(97, 73),
            stylePresets: Catalog, characterMaterials: hairRole, pluginBindings: sameStyleConflict, styleCatalog: ViewCatalog));
        Assert.Throws<ArgumentException>(() => new D3D12RealtimeViewRenderer(0, new(97, 73),
            stylePresets: Catalog, characterMaterials: hairRole, pluginBindings: sameStyleConflict, styleCatalog: ViewCatalog));
    }

    [Fact]
    public void PreflightRejectsWrongObjectRoleAndStarAlphaButHiddenIndicesWaitForReveal()
    {
        var scene = Source(128); var id = Guid.NewGuid();
        var item = new SceneRenderingStyleBinding(id, new("star-rail"), ModelRole.Character, 0, 1, true);
        D3D12ViewCharacterMaterial[] hairRole = [new(id, 0, D3D12CharacterMaterialRole.Hair)];
        D3D12ViewPluginMaterialBinding[] starBlue = [new(id, 0, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1))];
        foreach (var bad in new[] { item with { ObjectId = Guid.NewGuid() }, item with { Role = ModelRole.Environment },
            item with { Role = ModelRole.Prop }, item with { Role = ModelRole.Unspecified } })
            Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene, Catalog, [bad],
                characterMaterials: hairRole, pluginBindings: starBlue, styleCatalog: ViewCatalog));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene, Catalog, [item],
            characterMaterials: hairRole,
            pluginBindings: [new(id, 0, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1, "Alpha"))],
            styleCatalog: ViewCatalog));
        Assert.True(D3D12ViewRenderingStyles.Preflight(scene, Catalog,
            [item with { Preset = new("wuthering-waves") }],
            characterMaterials: hairRole,
            pluginBindings: [new(id, 0, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.WutheringStyleId, 0, 1, "Alpha"))],
            styleCatalog: ViewCatalog).IsAvailable);
        var hidden = new SceneRenderingStyleBinding(Guid.NewGuid(), new("star-rail"), ModelRole.Character, 1, 0, false);
        D3D12ViewPluginMaterialBinding[] selected = [
            new(hidden.ObjectId, 99, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 99, 100))
        ];
        Assert.True(D3D12ViewRenderingStyles.Preflight(scene, Catalog, [item, hidden], pluginBindings: selected, styleCatalog: ViewCatalog).IsAvailable);
        Assert.Null(D3D12ViewRenderingStyles.Resolve(scene, Catalog, [item, hidden], pluginBindings: selected, styleCatalog: ViewCatalog)
            .SurfaceStyles.GetPluginDraw(0));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene, Catalog,
            [item with { MaterialCount = 0 }, hidden with { MaterialStart = 0, MaterialCount = 1, Visible = true }],
            pluginBindings: selected, styleCatalog: ViewCatalog));
    }

    [Theory, InlineData("star-rail"), InlineData("wuthering-waves")]
    [Trait("Category", "Gpu")]
    public async Task CaptureMatchesComposedTwoImageBindingAndPresentUsesTheSameInputs(string preset)
    {
        var layout = new SceneCompositionEditor();
        var item = layout.Add("selected", SourcePath, ModelRole.Character, Source(128));
        using var project = Project(layout); project.SetDefaultRenderingStyle(new(preset), Token);
        var bindingData = T8cTestHelpers.StarMcHairBinding(preset, 0, 1, intensity: .2f);
        D3D12ViewPluginMaterialBinding[] host = [new(item.Id, 0, bindingData)];
        D3D12ViewCharacterMaterial[] charMats = [new(item.Id, 0, D3D12CharacterMaterialRole.Hair)];
        var request = new ViewRenderRequest(97, 73);
        await using var capture = new D3D12ViewRenderer(request,
            stylePresets: Catalog, characterMaterials: charMats, pluginBindings: host, styleCatalog: ViewCatalog);
        var snapshot = project.CaptureRenderSnapshot(capture.SceneInputRequests);
        host[0] = new(item.Id, 0, T8cTestHelpers.StarMcHairBinding(preset, 0, 1, intensity: 0f));
        var plan = new OfflineFramePlan(97, 73, new(30), 0, 2);
        var camera = item.Source.Surface.Snapshot.Camera;
        var frame = new OfflineFrame(plan.GetSample(0), 97, 73, snapshot.Scene, null, null, null)
        { RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs, ResolvedCamera = camera };
        using var image = await capture.CaptureAsync(frame, Token);
        var indices = snapshot.SceneInputs[item.Id].TextureIndices;
        var resolvedSource = D3D12ViewScene.Resolve(snapshot.Scene, null, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), request.EffectiveQuality, camera);
        var expectedMap = T8cTestHelpers.CreateDirectMap([preset],
            [T8cTestHelpers.MaterialWithBinding(preset, T8cTestHelpers.StarMcHairBinding(preset, indices[0], indices[1], intensity: .2f), D3D12CharacterMaterialRole.Hair, frame: resolvedSource)]);
        using var lowLevel = new D3D12FrameSession();
        var expected = lowLevel.Capture(resolvedSource, 97, 73, surfaceStyles: expectedMap);
        Assert.Equal(expected.Rgba, image.Pixels.ToArray());
        await using var ordinary = new D3D12ViewRenderer(request, stylePresets: Catalog, styleCatalog: ViewCatalog);
        using var baseline = await ordinary.CaptureAsync(frame with { SceneInputs = SceneObjectRenderInputs.Empty }, Token);
        int changed = 0;
        for (int offset = 0; offset < image.Pixels.Length; offset += 4)
        {
            Assert.Equal(baseline.Pixels.Span[offset + 3], image.Pixels.Span[offset + 3]);
            if (!baseline.Pixels.Span.Slice(offset, 3).SequenceEqual(image.Pixels.Span.Slice(offset, 3))) changed++;
        }
        Assert.True(changed > 100, $"Only {changed} pixels consumed the dedicated two-image response.");
        var before = capture.Statistics;
        await Assert.ThrowsAsync<ArgumentException>(() => capture.CaptureAsync(frame with { SceneInputs = SceneObjectRenderInputs.Empty }, Token).AsTask());
        Assert.Same(before, capture.Statistics);
        nint window = CreateWindowExW(0, "STATIC", "MDA Star MC local hair", 0x80000000, 0, 0, 128, 96, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            await using var presenter = new D3D12RealtimeViewRenderer(window, request,
                stylePresets: Catalog,
                characterMaterials: charMats,
                pluginBindings: [new(item.Id, 0, T8cTestHelpers.StarMcHairBinding(preset, 0, 1, intensity: .2f))],
                styleCatalog: ViewCatalog);
            Assert.Equal(capture.SceneInputRequests[0].LocalTextureIndices, presenter.SceneInputRequests[0].LocalTextureIndices);
            var preview = new PreviewFrame(97, 73, 0, snapshot.Scene, null, null, null)
            { RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs, ResolvedCamera = camera };
            presenter.ValidateRenderingStyles(snapshot.Scene, snapshot.RenderingStyles);
            await presenter.PresentAsync(preview, Token);
            var stats = presenter.Statistics;
            await Assert.ThrowsAsync<ArgumentException>(() => presenter.PresentAsync(preview with { SceneInputs = SceneObjectRenderInputs.Empty }, Token).AsTask());
            Assert.Equal(stats.PresentCount, presenter.Statistics.PresentCount);
            await presenter.PresentAsync(preview with { RenderingStyles = snapshot.RenderingStyles.Select(value => value with { Preset = new("mmd") }).ToArray(),
                SceneInputs = SceneObjectRenderInputs.Empty }, Token);
            Assert.Equal(2, presenter.Statistics.PresentCount);
            Assert.Equal(0, presenter.Statistics.ExplicitCaptureCount);
            Assert.Equal(0, presenter.Statistics.Frame!.TotalReadbacks);
        }
        finally { DestroyWindow(window); }
    }

    private static D3D12ViewRenderingStylesResult Resolve(SceneProjectRenderSnapshot snapshot,
        IReadOnlyList<D3D12ViewPluginMaterialBinding> bindings,
        IReadOnlyList<D3D12ViewCharacterMaterial>? characterMaterials = null)
        => D3D12ViewRenderingStyles.Resolve(D3D12ViewScene.Resolve(snapshot.Scene, null, null, null,
                new Dictionary<Guid, CharacterMorphChannels>(), new()), Catalog, snapshot.RenderingStyles,
            characterMaterials: characterMaterials, pluginBindings: bindings, sceneInputs: snapshot.SceneInputs,
            styleCatalog: ViewCatalog);
    private static string SourcePath => Path.Combine(Path.GetTempPath(), "mda-star-mc-synthetic.pmx");
    private static OriginalFrameScene Source(byte blue)
    {
        SceneVertex[] vertices = [new(new(-1,-1,0), -Vector3.UnitZ, Vector2.Zero), new(new(1,-1,0), -Vector3.UnitZ, Vector2.UnitX),
            new(new(1,1,0), -Vector3.UnitZ, Vector2.One), new(new(-1,1,0), -Vector3.UnitZ, Vector2.UnitY)];
        var snapshot = new SceneSnapshot(vertices, [0,2,1,0,3,2],
            [new("explicit Hair", 0, 6, new(.3f,.2f,.1f,1), new(.8f,.4f,.2f), Vector3.Zero, 1)],
            [new("highlight", 1, 1, [0, 11, blue, 79]), new("shadow", 1, 1, [255, 9, 37, 0])],
            camera: new(new(0,0,-5), Vector3.Zero, Vector3.UnitY, 3, .1f, 20), lightDirection: -Vector3.UnitZ);
        var outline = new OriginalOutlineData(vertices.Select(vertex => new OriginalOutlineVertex(vertex.Position,
            vertex.Normal, OriginalOutlineVertexMode.Expand, 0, 0)), [new OriginalOutlineMaterial(0, Vector4.Zero, false)]);
        return new(new(snapshot, [new(new(.2f))], new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            outline, new(Vector4.Zero, Matrix4x4.Identity, snapshot.Camera.Eye, 30, 1),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero));
    }
    private static SceneProjectSession Project(SceneCompositionEditor layout) => new(layout, new(), [], new NoPhysics(), cancellationToken: Token);
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extended, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
