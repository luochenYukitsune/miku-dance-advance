using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12StarMcHairViewTests
{
    private static readonly D3D12StylePresetCatalog Catalog = D3D12StylePresetCatalog.BuiltIn;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void DetachedInputsUnionTwoImagesWithExistingFaceBonesAndGenshinRequests()
    {
        var id = Guid.NewGuid(); var other = Guid.NewGuid();
        D3D12ViewStarMcHairBinding[] source = [new(id, 0, 2, 3), new(other, 0, 0, 1)];
        var detached = D3D12ViewStarMcHairBindings.Detach(source);
        source[0] = source[0] with { HighlightLocalTextureIndex = 99 };
        Assert.Equal(2, detached[0].HighlightLocalTextureIndex);
        Assert.Same(detached, D3D12ViewStarMcHairBindings.Detach(detached));
        D3D12ViewFaceSdfBinding[] faces = [D3D12ViewFaceSdfBinding.GenshinSoft(id, 2, 1, 2,
            new D3D12ViewFaceHeadBasis.SelectedBone(4, -Vector3.UnitZ, Vector3.UnitX))];
        D3D12ViewGenshinHairBinding[] genshin = [new(id, 1, 4)];
        var requests = D3D12ViewStarMcHairBindings.CreateRequests(detached, faces, genshin);
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
        D3D12ViewStarMcHairBinding[] hair = [new(a.Id, 0, 0, 1), new(b.Id, 0, 0, 1, HighlightBrightness: 3)];
        var snapshot = project.CaptureRenderSnapshot(D3D12ViewStarMcHairBindings.CreateRequests(hair));
        var result = Resolve(snapshot, hair);
        Assert.Equal(4, snapshot.Scene.Surface.Snapshot.Textures.Count);
        foreach (var instance in snapshot.RenderingStyles)
        {
            var selected = result.SurfaceStyles.CharacterMaterials[instance.MaterialStart];
            Assert.Equal(D3D12CharacterMaterialRole.Hair, selected.Role);
            var binding = Assert.IsType<D3D12StarMcHairBinding>(selected.StarMcHair);
            var indices = snapshot.SceneInputs[instance.ObjectId].TextureIndices;
            Assert.Equal(indices[0], binding.HighlightCombinedTextureIndex);
            Assert.Equal(indices[1], binding.ShadowCombinedTextureIndex);
            Assert.Equal(instance.ObjectId == a.Id ? 64 : 192,
                snapshot.Scene.Surface.Snapshot.Textures[indices[0]].Rgba[2]);
            Assert.Equal(instance.ObjectId == a.Id ? 2f : 3f, binding.HighlightBrightness);
        }
        Assert.Throws<ArgumentException>(() => Resolve(project.CaptureRenderSnapshot(), hair));
        var partial = project.CaptureRenderSnapshot([new(a.Id, localTextureIndices: [0]), new(b.Id, localTextureIndices: [0, 1])]);
        Assert.Throws<ArgumentException>(() => Resolve(partial, hair));
        Assert.Equal(0, project.CurrentFrame);
    }

    [Theory, InlineData("mmd"), InlineData("genshin"), InlineData("endfield")]
    public void LocalConfigurationRemainsDormantAndDoesNotChangeOtherFamilies(string preset)
    {
        var layout = new SceneCompositionEditor();
        var item = layout.Add("selected", SourcePath, ModelRole.Character, Source(128));
        using var project = Project(layout); project.SetDefaultRenderingStyle(new(preset), Token);
        var snapshot = project.CaptureRenderSnapshot();
        D3D12ViewStarMcHairBinding[] hair = [new(item.Id, 0, 0, 1)];
        Assert.True(D3D12ViewRenderingStyles.Preflight(snapshot.Scene, Catalog, snapshot.RenderingStyles,
            starMcHairDataBindings: hair).IsAvailable);
        var ordinary = Resolve(snapshot, []); var dormant = Resolve(snapshot, hair);
        Assert.Null(dormant.SurfaceStyles.CharacterMaterials[0].StarMcHair);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(ordinary.SurfaceStyles, dormant.SurfaceStyles));
        Assert.Equal(preset, project.GetRenderingStyle(item.Id).StableId);
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(snapshot.Scene, Catalog,
            snapshot.RenderingStyles, [new(item.Id, 0, new(D3D12CharacterMaterialRole.Hair, StarMcHair: new(0, 1)))]));
    }

    [Fact]
    public void InvalidCrossFamilyAndLocalComposedConflictsRejectBeforeNativeConstruction()
    {
        var id = Guid.NewGuid(); var valid = new D3D12ViewStarMcHairBinding(id, 0, 0, 1);
        D3D12ViewStarMcHairBinding[][] invalid = [[null!], [valid, valid], [valid with { ObjectId = Guid.Empty }],
            [valid with { LocalMaterialIndex = -1 }], [valid with { ShadowLocalTextureIndex = -1 }],
            [valid with { HighlightLocalTextureIndex = -1 }], [valid with { ShadowSoftness = 0 }],
            [valid with { HighlightChannel = (D3D12HairHighlightChannel)17 }], [valid with { Intensity = float.NaN }]];
        foreach (var value in invalid)
        {
            Assert.ThrowsAny<ArgumentException>(() => new D3D12ViewRenderer(new(97, 73), starMcHairDataBindings: value));
            Assert.ThrowsAny<ArgumentException>(() => new D3D12RealtimeViewRenderer(0, new(97, 73), starMcHairDataBindings: value));
        }
        foreach (var style in new[] { new D3D12CharacterMaterialStyle(), new(D3D12CharacterMaterialRole.Face),
            new(D3D12CharacterMaterialRole.Eye), new(D3D12CharacterMaterialRole.Hair, StarMcHair: new(0, 1)),
            new(D3D12CharacterMaterialRole.Hair, GenshinNonMetallicSpecular: new(0)) })
        {
            D3D12ViewCharacterMaterialBinding[] materials = [new(id, 0, style)];
            Assert.Throws<ArgumentException>(() => new D3D12ViewRenderer(new(97, 73),
                characterMaterials: materials, starMcHairDataBindings: [valid]));
            Assert.Throws<ArgumentException>(() => new D3D12RealtimeViewRenderer(0, new(97, 73),
                characterMaterials: materials, starMcHairDataBindings: [valid]));
        }
        Assert.Throws<ArgumentException>(() => new D3D12ViewRenderer(new(97, 73),
            hairDataBindings: [new(id, 0, 0)], starMcHairDataBindings: [valid]));
        Assert.Throws<ArgumentException>(() => new D3D12RealtimeViewRenderer(0, new(97, 73),
            hairDataBindings: [new(id, 0, 0)], starMcHairDataBindings: [valid]));
    }

    [Fact]
    public void PreflightRejectsWrongObjectRoleAndStarAlphaButHiddenIndicesWaitForReveal()
    {
        var scene = Source(128); var id = Guid.NewGuid();
        var item = new SceneRenderingStyleBinding(id, new("star-rail"), ModelRole.Character, 0, 1, true);
        foreach (var bad in new[] { item with { ObjectId = Guid.NewGuid() }, item with { Role = ModelRole.Environment },
            item with { Role = ModelRole.Prop }, item with { Role = ModelRole.Unspecified } })
            Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene, Catalog, [bad],
                starMcHairDataBindings: [new(id, 0, 0, 1)]));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene, Catalog, [item],
            starMcHairDataBindings: [new(id, 0, 0, 1, D3D12HairHighlightChannel.Alpha)]));
        Assert.True(D3D12ViewRenderingStyles.Preflight(scene, Catalog,
            [item with { Preset = new("wuthering-waves") }],
            starMcHairDataBindings: [new(id, 0, 0, 1, D3D12HairHighlightChannel.Alpha)]).IsAvailable);
        var hidden = new SceneRenderingStyleBinding(Guid.NewGuid(), new("star-rail"), ModelRole.Character, 1, 0, false);
        D3D12ViewStarMcHairBinding[] selected = [new(hidden.ObjectId, 99, 99, 100)];
        Assert.True(D3D12ViewRenderingStyles.Preflight(scene, Catalog, [item, hidden], starMcHairDataBindings: selected).IsAvailable);
        Assert.Null(D3D12ViewRenderingStyles.Resolve(scene, Catalog, [item, hidden], starMcHairDataBindings: selected)
            .SurfaceStyles.CharacterMaterials[0].StarMcHair);
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene, Catalog,
            [item with { MaterialCount = 0 }, hidden with { MaterialStart = 0, MaterialCount = 1, Visible = true }],
            starMcHairDataBindings: selected));
    }

    [Theory, InlineData("star-rail"), InlineData("wuthering-waves")]
    [Trait("Category", "Gpu")]
    public async Task CaptureMatchesComposedTwoImageBindingAndPresentUsesTheSameInputs(string preset)
    {
        var layout = new SceneCompositionEditor();
        var item = layout.Add("selected", SourcePath, ModelRole.Character, Source(128));
        using var project = Project(layout); project.SetDefaultRenderingStyle(new(preset), Token);
        D3D12ViewStarMcHairBinding[] host = [new(item.Id, 0, 0, 1, Intensity: .2f)];
        var request = new ViewRenderRequest(97, 73);
        await using var capture = new D3D12ViewRenderer(request, starMcHairDataBindings: host);
        var snapshot = project.CaptureRenderSnapshot(capture.SceneInputRequests);
        host[0] = host[0] with { Intensity = 0 };
        var plan = new OfflineFramePlan(97, 73, new(30), 0, 2);
        var camera = item.Source.Surface.Snapshot.Camera;
        var frame = new OfflineFrame(plan.GetSample(0), 97, 73, snapshot.Scene, null, null, null)
        { RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs, ResolvedCamera = camera };
        using var image = await capture.CaptureAsync(frame, Token);
        var indices = snapshot.SceneInputs[item.Id].TextureIndices;
        await using var composed = new D3D12ViewRenderer(request, characterMaterials:
            [new(item.Id, 0, new(D3D12CharacterMaterialRole.Hair, StarMcHair: new(indices[0], indices[1], Intensity: .2f)))]);
        using var expected = await composed.CaptureAsync(frame with { SceneInputs = SceneObjectRenderInputs.Empty }, Token);
        Assert.Equal(expected.Pixels.ToArray(), image.Pixels.ToArray());
        await using var ordinary = new D3D12ViewRenderer(request);
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
                starMcHairDataBindings: [new(item.Id, 0, 0, 1, Intensity: .2f)]);
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
        IReadOnlyList<D3D12ViewStarMcHairBinding> bindings)
        => D3D12ViewRenderingStyles.Resolve(D3D12ViewScene.Resolve(snapshot.Scene, null, null, null,
                new Dictionary<Guid, CharacterMorphChannels>(), new()), Catalog, snapshot.RenderingStyles,
            sceneInputs: snapshot.SceneInputs, starMcHairDataBindings: bindings);
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
