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
public sealed class D3D12GenshinHairViewTests
{
    private static readonly D3D12StylePresetCatalog Catalog = D3D12StylePresetCatalog.BuiltIn;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void LocalDefinitionsDetachAndUnionFaceTexturesAndSelectedBonesPerObject()
    {
        var id = Guid.NewGuid(); var other = Guid.NewGuid();
        D3D12ViewGenshinHairBinding[] source = [new(id, 0, 2), new(id, 1, 2), new(other, 0, 0)];
        int enumerations = 0;
        IEnumerable<D3D12ViewGenshinHairBinding> Once()
        { enumerations++; foreach (var value in source) yield return value; }
        var detached = D3D12ViewGenshinHairBindings.Detach(Once());
        source[0] = source[0] with { LocalTextureIndex = 99 };
        Assert.Same(detached, D3D12ViewGenshinHairBindings.Detach(detached));
        Assert.Equal(1, enumerations); Assert.Equal(2, detached[0].LocalTextureIndex);
        D3D12ViewFaceSdfBinding[] faces = [D3D12ViewFaceSdfBinding.GenshinSoft(id, 3, 1, 2,
            new D3D12ViewFaceHeadBasis.SelectedBone(4, -Vector3.UnitZ, Vector3.UnitX))];
        var requests = D3D12ViewGenshinHairBindings.CreateRequests(detached, faces);
        var selected = Assert.Single(requests, request => request.ObjectId == id);
        Assert.Equal(new[] { 4 }, selected.BoneIndices);
        Assert.Equal(new[] { 1, 2 }, selected.LocalTextureIndices);
        Assert.Equal(new[] { 0 }, Assert.Single(requests, request => request.ObjectId == other).LocalTextureIndices);
    }

    [Fact]
    public void ExplicitHostSelectionCreatesHairAndRebasesTwoLocalZeroTexturesBySameSampleMap()
    {
        var first = Texture("same label", 64); var second = Texture("same label", 192);
        var layout = new SceneCompositionEditor();
        var a = layout.Add("first", SourcePath, ModelRole.Character, Source(first));
        var b = layout.Add("second", SourcePath, ModelRole.Character, Source(second));
        using var project = Project(layout); project.SetDefaultRenderingStyle(new("genshin"), Token);
        D3D12ViewGenshinHairBinding[] hair = [new(a.Id, 0, 0, .75f, 5, .4f, 2), new(b.Id, 0, 0)];
        var snapshot = project.CaptureRenderSnapshot(D3D12ViewGenshinHairBindings.CreateRequests(hair));
        var result = Resolve(snapshot, hair);
        Assert.Equal(2, snapshot.Scene.Surface.Snapshot.Textures.Count);
        foreach (var instance in snapshot.RenderingStyles)
        {
            var material = result.SurfaceStyles.CharacterMaterials[instance.MaterialStart];
            Assert.Equal(D3D12CharacterMaterialRole.Hair, material.Role);
            var binding = Assert.IsType<D3D12GenshinNonMetallicSpecularBinding>(material.GenshinNonMetallicSpecular);
            Assert.Equal(snapshot.SceneInputs[instance.ObjectId].TextureIndices[0], binding.CombinedTextureIndex);
            Assert.Same(instance.ObjectId == a.Id ? first : second,
                snapshot.Scene.Surface.Snapshot.Textures[binding.CombinedTextureIndex]);
        }
        var firstBinding = result.SurfaceStyles.CharacterMaterials[0].GenshinNonMetallicSpecular!;
        Assert.Equal((.75f, 5f, .4f, 2f),
            (firstBinding.GlobalSpecularScale, firstBinding.Shininess, firstBinding.Multiplier, firstBinding.Intensity));
        Assert.Throws<ArgumentException>(() => Resolve(project.CaptureRenderSnapshot(), hair));
        var partial = project.CaptureRenderSnapshot([new(a.Id, localTextureIndices: [0])]);
        Assert.Throws<ArgumentException>(() => Resolve(partial, hair));
        Assert.Equal(0, project.CurrentFrame); // Resolving inputs never evaluates another animation sample.
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void LocalHairIsDormantOutsideGenshinAndNeverSwitchesPreset(string preset)
    {
        var layout = new SceneCompositionEditor();
        var item = layout.Add("selected", SourcePath, ModelRole.Character, Source(Texture("mask", 128)));
        using var project = Project(layout); project.SetDefaultRenderingStyle(new(preset), Token);
        var snapshot = project.CaptureRenderSnapshot();
        var plain = Resolve(snapshot, []);
        var local = new D3D12ViewGenshinHairBinding(item.Id, 0, 0);
        Assert.True(D3D12ViewRenderingStyles.Preflight(snapshot.Scene, Catalog, snapshot.RenderingStyles,
            hairDataBindings: [local]).IsAvailable);
        var retained = Resolve(snapshot, [local]);
        Assert.Null(retained.SurfaceStyles.CharacterMaterials[0].GenshinNonMetallicSpecular);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(plain.SurfaceStyles, retained.SurfaceStyles));
        Assert.Equal(plain.Scene.Materials, retained.Scene.Materials);
        Assert.Equal(preset, project.GetRenderingStyle(item.Id).StableId);
        // A composed-frame payload is an active low-level request, unlike stored local configuration.
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(snapshot.Scene, Catalog,
            snapshot.RenderingStyles, [new(item.Id, 0, new(D3D12CharacterMaterialRole.Hair,
                GenshinNonMetallicSpecular: new(0)))]));
    }

    [Fact]
    public void HiddenHairRetainsSelectionAndRevalidatesMaterialBoundsOnReveal()
    {
        var visible = new SceneRenderingStyleBinding(Guid.NewGuid(), new("genshin"), ModelRole.Character, 0, 1, true);
        var hidden = new SceneRenderingStyleBinding(Guid.NewGuid(), new("genshin"), ModelRole.Character, 1, 0, false);
        var scene = Source(Texture("mask", 128));
        D3D12ViewGenshinHairBinding[] hair = [new(hidden.ObjectId, 99, 99)];
        Assert.True(D3D12ViewRenderingStyles.Preflight(scene, Catalog, [visible, hidden], hairDataBindings: hair).IsAvailable);
        var retained = D3D12ViewRenderingStyles.Resolve(scene, Catalog, [visible, hidden], hairDataBindings: hair);
        Assert.Null(Assert.Single(retained.SurfaceStyles.CharacterMaterials).GenshinNonMetallicSpecular);
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene, Catalog,
            [visible with { MaterialCount = 0 }, hidden with { MaterialStart = 0, MaterialCount = 1, Visible = true }],
            hairDataBindings: hair));
    }

    [Fact]
    public void UnknownObjectsAndNonCharactersAreRejectedByPreflightAndResolve()
    {
        var scene = Source(Texture("mask", 128)); var id = Guid.NewGuid();
        var instance = new SceneRenderingStyleBinding(id, new("genshin"), ModelRole.Character, 0, 1, true);
        foreach (var candidate in new[] { instance with { ObjectId = Guid.NewGuid() },
            instance with { Role = ModelRole.Environment }, instance with { Role = ModelRole.Prop },
            instance with { Role = ModelRole.Unspecified } })
        {
            Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene, Catalog,
                [candidate], hairDataBindings: [new(id, 0, 0)]));
            Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(scene, Catalog,
                [candidate], hairDataBindings: [new(id, 0, 0)]));
        }
    }

    [Fact]
    public void InvalidAndConflictingLocalSelectionsRejectBeforeEitherNativeRendererIsCreated()
    {
        var id = Guid.NewGuid(); var valid = new D3D12ViewGenshinHairBinding(id, 0, 0);
        D3D12ViewGenshinHairBinding[][] invalid = [[null!], [valid, valid],
            [valid with { ObjectId = Guid.Empty }], [valid with { LocalMaterialIndex = -1 }],
            [valid with { LocalTextureIndex = -1 }], [valid with { GlobalSpecularScale = float.NaN }],
            [valid with { Shininess = 65 }], [valid with { Multiplier = -1 }], [valid with { Intensity = float.PositiveInfinity }]];
        foreach (var bindings in invalid)
        {
            Assert.ThrowsAny<ArgumentException>(() => new D3D12ViewRenderer(new(97, 73), hairDataBindings: bindings));
            Assert.ThrowsAny<ArgumentException>(() => new D3D12RealtimeViewRenderer(0, new(97, 73), hairDataBindings: bindings));
        }
        foreach (var style in new[] { new D3D12CharacterMaterialStyle(), new(D3D12CharacterMaterialRole.Face),
            new(D3D12CharacterMaterialRole.Eye), new(D3D12CharacterMaterialRole.Hair, GenshinNonMetallicSpecular: new(0)) })
        {
            D3D12ViewCharacterMaterialBinding[] materials = [new(id, 0, style)];
            Assert.Throws<ArgumentException>(() => new D3D12ViewRenderer(new(97, 73), characterMaterials: materials, hairDataBindings: [valid]));
            Assert.Throws<ArgumentException>(() => new D3D12RealtimeViewRenderer(0, new(97, 73), characterMaterials: materials, hairDataBindings: [valid]));
        }
    }

    [Fact]
    public void MixedFaceAndHairResolveWithOneObjectRequestAndIndependentMaterialInputs()
    {
        var layout = new SceneCompositionEditor();
        var item = layout.Add("mixed", SourcePath, ModelRole.Character, Source(Texture("shared", 255), 2));
        using var project = Project(layout); project.SetDefaultRenderingStyle(new("genshin"), Token);
        D3D12ViewGenshinHairBinding[] hair = [new(item.Id, 0, 0)];
        D3D12ViewFaceSdfBinding[] faces = [D3D12ViewFaceSdfBinding.GenshinSoft(item.Id, 1, 0, 0,
            new D3D12ViewFaceHeadBasis.FixedWorld(-Vector3.UnitZ, Vector3.UnitX))];
        var requests = D3D12ViewGenshinHairBindings.CreateRequests(hair, faces);
        Assert.Equal(new[] { 0 }, Assert.Single(requests).LocalTextureIndices);
        var snapshot = project.CaptureRenderSnapshot(requests);
        var result = D3D12ViewRenderingStyles.Resolve(Ready(snapshot.Scene), Catalog, snapshot.RenderingStyles,
            [new(item.Id, 1, new(D3D12CharacterMaterialRole.Face, .72f, .24f))], faces, snapshot.SceneInputs, hair);
        Assert.NotNull(result.SurfaceStyles.CharacterMaterials[0].GenshinNonMetallicSpecular);
        Assert.Null(result.SurfaceStyles.CharacterMaterials[0].FaceSdf);
        Assert.NotNull(result.SurfaceStyles.CharacterMaterials[1].FaceSdf);
        Assert.Null(result.SurfaceStyles.CharacterMaterials[1].GenshinNonMetallicSpecular);
        Assert.Equal(.72f, result.SurfaceStyles.CharacterMaterials[1].FaceNormalShadeStrength);
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task ActualCaptureMatchesComposedBindingAndPresentUsesSameMappingsWithoutReadback()
    {
        var layout = new SceneCompositionEditor();
        var item = layout.Add("selected", SourcePath, ModelRole.Character, Source(Texture("mask", 128)));
        using var project = Project(layout); project.SetDefaultRenderingStyle(new("genshin"), Token);
        D3D12ViewGenshinHairBinding[] host = [new(item.Id, 0, 0, 1, 1, .4f, 1)];
        var request = new ViewRenderRequest(97, 73);
        await using var capture = new D3D12ViewRenderer(request, hairDataBindings: host);
        var snapshot = project.CaptureRenderSnapshot(capture.SceneInputRequests);
        host[0] = host[0] with { Intensity = 0 }; // Construction already detached the selected values.
        var plan = new OfflineFramePlan(97, 73, new(30), 0, 2);
        // Project composition uses its default camera; retain the fixture framing in both view paths.
        var camera = item.Source.Surface.Snapshot.Camera;
        var frame = new OfflineFrame(plan.GetSample(0), 97, 73, snapshot.Scene, null, null, null)
        { RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs, ResolvedCamera = camera };
        using var image = await capture.CaptureAsync(frame, Token);
        await using var composed = new D3D12ViewRenderer(request, characterMaterials:
            [new(item.Id, 0, new(D3D12CharacterMaterialRole.Hair,
                GenshinNonMetallicSpecular: new(snapshot.SceneInputs[item.Id].TextureIndices[0], 1, 1, .4f, 1)))]);
        using var expected = await composed.CaptureAsync(frame with { SceneInputs = SceneObjectRenderInputs.Empty }, Token);
        Assert.Equal(expected.Pixels.ToArray(), image.Pixels.ToArray());
        await using var ordinary = new D3D12ViewRenderer(request);
        using var baseline = await ordinary.CaptureAsync(frame with { SceneInputs = SceneObjectRenderInputs.Empty }, Token);
        int changed = 0;
        for (int offset = 0; offset < image.Pixels.Length; offset += 4)
        {
            Assert.Equal(baseline.Pixels.Span[offset + 3], image.Pixels.Span[offset + 3]);
            if (image.Pixels.Span[offset] != baseline.Pixels.Span[offset]
                || image.Pixels.Span[offset + 1] != baseline.Pixels.Span[offset + 1]
                || image.Pixels.Span[offset + 2] != baseline.Pixels.Span[offset + 2]) changed++;
        }
        Assert.True(changed > 100, $"Only {changed} pixels consumed dedicated hair lighting.");
        var beforeCapture = capture.Statistics!;
        await Assert.ThrowsAsync<ArgumentException>(() => capture.CaptureAsync(frame with { SceneInputs = SceneObjectRenderInputs.Empty }, Token).AsTask());
        Assert.Same(beforeCapture, capture.Statistics);

        nint window = CreateWindowExW(0, "STATIC", "MDA local hair", 0x80000000, 0, 0, 128, 96, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            await using var present = new D3D12RealtimeViewRenderer(window, request, hairDataBindings:
                [new(item.Id, 0, 0, 1, 1, .4f, 1)]);
            Assert.Equal(capture.SceneInputRequests[0].LocalTextureIndices, present.SceneInputRequests[0].LocalTextureIndices);
            var preview = new PreviewFrame(97, 73, 0, snapshot.Scene, null, null, null)
            { RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs, ResolvedCamera = camera };
            present.ValidateRenderingStyles(snapshot.Scene, snapshot.RenderingStyles);
            await present.PresentAsync(preview, Token);
            var before = present.Statistics;
            await Assert.ThrowsAsync<ArgumentException>(() => present.PresentAsync(preview with { SceneInputs = SceneObjectRenderInputs.Empty }, Token).AsTask());
            Assert.Equal(before.PresentCount, present.Statistics.PresentCount);
            Assert.Equal(before.Frame!.FrameId, present.Statistics.Frame!.FrameId);
            await present.PresentAsync(preview with { RenderingStyles = snapshot.RenderingStyles.Select(value => value with { Preset = new("mmd") }).ToArray(),
                SceneInputs = SceneObjectRenderInputs.Empty }, Token);
            Assert.Equal(2, present.Statistics.PresentCount);
            Assert.Equal(0, present.Statistics.ExplicitCaptureCount);
            Assert.Equal(0, present.Statistics.Frame!.TotalReadbacks);
        }
        finally { DestroyWindow(window); }
    }

    private static D3D12ViewRenderingStylesResult Resolve(SceneProjectRenderSnapshot snapshot,
        IReadOnlyList<D3D12ViewGenshinHairBinding> hair)
        => D3D12ViewRenderingStyles.Resolve(Ready(snapshot.Scene), Catalog, snapshot.RenderingStyles,
            sceneInputs: snapshot.SceneInputs, hairDataBindings: hair);
    private static OriginalFrameScene Ready(OriginalFrameScene scene)
        => D3D12ViewScene.Resolve(scene, null, null, null, new Dictionary<Guid, CharacterMorphChannels>(), new());
    private static string SourcePath => Path.Combine(Path.GetTempPath(), "mda-synthetic-hair.pmx");
    private static SceneTexture Texture(string label, byte red) => new(label, 1, 1, [red, 0, 255, 0]);
    private static OriginalFrameScene Source(SceneTexture texture, int materialCount = 1)
    {
        SceneVertex[] vertices = [new(new(-1,-1,0), -Vector3.UnitZ, Vector2.Zero), new(new(1,-1,0), -Vector3.UnitZ, Vector2.UnitX),
            new(new(1,1,0), -Vector3.UnitZ, Vector2.One), new(new(-1,1,0), -Vector3.UnitZ, Vector2.UnitY)];
        var snapshot = new SceneSnapshot(vertices, Enumerable.Range(0, materialCount).SelectMany(_ => new uint[] { 0,2,1,0,3,2 }).ToArray(),
            Enumerable.Range(0, materialCount).Select(index => new SceneMaterial("explicit draw", index * 6, 6,
                new(.3f,.2f,.1f,1), new(.8f,.4f,.2f), Vector3.Zero, 1)).ToArray(), [texture],
            camera: new(new(0,0,-5), Vector3.Zero, Vector3.UnitY, 3, .1f, 20), lightDirection: -Vector3.UnitZ);
        var outline = new OriginalOutlineData(vertices.Select(vertex => new OriginalOutlineVertex(vertex.Position,
            vertex.Normal, OriginalOutlineVertexMode.Expand, 0, 0)),
            Enumerable.Range(0, materialCount).Select(_ => new OriginalOutlineMaterial(0, Vector4.Zero, false)));
        return new(new(snapshot, Enumerable.Range(0, materialCount).Select(_ => new OriginalStyleMaterialBinding(new(.2f))).ToArray(),
            new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)), outline,
            new(Vector4.Zero, Matrix4x4.Identity, snapshot.Camera.Eye, 30, 1),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero));
    }
    private static SceneProjectSession Project(SceneCompositionEditor layout)
        => new(layout, new(), [], new NoPhysics(), cancellationToken: Token);
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
            => throw new InvalidOperationException("Synthetic scene contains no physical bodies.");
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings)
            => throw new InvalidOperationException("Synthetic scene contains no shared physics.");
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extended, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
