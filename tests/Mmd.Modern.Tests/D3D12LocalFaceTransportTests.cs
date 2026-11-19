using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12LocalFaceTransportTests
{
    private static D3D12StylePresetCatalog Catalog => TestStylePlugins.AllPresetCatalog;
    private static D3D12ViewStyleCatalog ViewCatalog => TestStylePlugins.AllViewCatalog;
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly SceneCamera Camera = new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 20);

    [Fact]
    public void DefinitionsDetachOnceAndRequestsDeduplicateOnlySelectedInputs()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var bones = BoneMap(a);
        D3D12ViewPluginMaterialBinding[] source =
        [
            TestStylePlugins.Bind(a, 0, SelectedFace(T8cTestHelpers.StarRailStyleId, 2)),
            TestStylePlugins.Bind(a, 1, SelectedFace(T8cTestHelpers.StarRailStyleId, 2)),
            TestStylePlugins.Bind(b, 0, T8cTestHelpers.EndfieldFaceSdfBinding(1, 0, -Vector3.UnitZ, Vector3.UnitX)),
        ];
        int enumerations = 0;
        IEnumerable<D3D12ViewPluginMaterialBinding> Once()
        { enumerations++; foreach (var value in source) yield return value; }
        var detached = D3D12ViewPluginMaterialBindings.Detach(Once());
        source[0] = TestStylePlugins.Bind(a, 0, SelectedFace(T8cTestHelpers.StarRailStyleId, 99));
        Assert.Same(detached, D3D12ViewPluginMaterialBindings.Detach(detached));
        Assert.Equal(1, enumerations); Assert.Equal(2, detached[0].Binding.TextureSlots!["primary"]);
        var requests = D3D12ViewPluginMaterialBindings.CreateRequests(detached, ViewCatalog, null, bones);
        Assert.Equal(new[] { a, b }, requests.Select(value => value.ObjectId));
        Assert.Equal(new[] { 1 }, requests[0].BoneIndices);
        Assert.Equal(new[] { 2 }, requests[0].LocalTextureIndices);
        Assert.Empty(requests[1].BoneIndices);
        Assert.Equal(new[] { 1, 0 }, requests[1].LocalTextureIndices);
    }

    [Fact]
    public void TwoModelsUseAuthoritativeRemappingThroughReorderAndReferenceDeduplication()
    {
        var shared = Mask("shared", 32);
        var firstOnly = Mask("equal display name", 96);
        var secondOnly = Mask("equal display name", 96); // equal bytes and labels, different identity
        var layout = new SceneCompositionEditor(); var model = Model();
        var a = layout.Add("first", SourcePath, ModelRole.Character, Rest(model, [firstOnly, shared]));
        var b = layout.Add("second", SourcePath, ModelRole.Character, Rest(model, [shared, secondOnly]));
        using var project = Project(layout);
        project.SetDefaultRenderingStyle(new("endfield"), Token);
        D3D12ViewPluginMaterialBinding[] local =
        [
            TestStylePlugins.Bind(a.Id, 0, T8cTestHelpers.EndfieldFaceSdfBinding(1, 0)),
            TestStylePlugins.Bind(b.Id, 0, T8cTestHelpers.EndfieldFaceSdfBinding(1, 0)),
        ];
        var materials = Faces(a.Id, b.Id);
        var requests = D3D12ViewPluginMaterialBindings.CreateRequests(local, ViewCatalog, materials);
        void Check(SceneProjectRenderSnapshot snapshot)
        {
            var resolved = D3D12ViewRenderingStyles.Resolve(Ready(snapshot.Scene), Catalog, snapshot.RenderingStyles,
                materials, local, snapshot.SceneInputs, styleCatalog: ViewCatalog);
            foreach (var instance in snapshot.RenderingStyles)
            {
                var sdf = resolved.SurfaceStyles.CharacterMaterials[instance.MaterialStart].PluginDraw!;
                var textures = snapshot.Scene.Surface.Snapshot.Textures;
                Assert.Same(instance.ObjectId == a.Id ? shared : secondOnly, textures[sdf.TextureBindings![0].SceneTextureIndex]);
                Assert.Same(instance.ObjectId == a.Id ? firstOnly : shared, textures[sdf.TextureBindings[1].SceneTextureIndex]);
            }
            Assert.Equal(3, snapshot.Scene.Surface.Snapshot.Textures.Count);
        }
        var first = project.CaptureRenderSnapshot(requests); Check(first);
        Assert.Equal(1, first.SceneInputs[a.Id].TextureIndices[1]);
        Assert.Equal(2, first.SceneInputs[b.Id].TextureIndices[1]);
        // Recreate the source order explicitly; remap the new instance identities rather than retaining offsets.
        var reversed = new SceneCompositionEditor();
        var rb = reversed.Add("second", SourcePath, ModelRole.Character, b.Source);
        var ra = reversed.Add("first", SourcePath, ModelRole.Character, a.Source);
        using var reverseProject = Project(reversed);
        reverseProject.SetDefaultRenderingStyle(new("endfield"), Token);
        var reverseLocal = new[]
        {
            local[0] with { ObjectId = ra.Id },
            local[1] with { ObjectId = rb.Id },
        };
        var reverseSnapshot = reverseProject.CaptureRenderSnapshot(D3D12ViewPluginMaterialBindings.CreateRequests(reverseLocal, ViewCatalog, Faces(ra.Id, rb.Id)));
        var reverse = D3D12ViewRenderingStyles.Resolve(Ready(reverseSnapshot.Scene), Catalog, reverseSnapshot.RenderingStyles,
            Faces(ra.Id, rb.Id), reverseLocal, reverseSnapshot.SceneInputs, styleCatalog: ViewCatalog);
        var aFace = reverse.SurfaceStyles.CharacterMaterials[2].PluginDraw!;
        var bFace = reverse.SurfaceStyles.CharacterMaterials[0].PluginDraw!;
        Assert.Equal(0, aFace.TextureBindings![0].SceneTextureIndex); Assert.Equal(2, aFace.TextureBindings[1].SceneTextureIndex);
        Assert.Equal(1, bFace.TextureBindings![0].SceneTextureIndex); Assert.Equal(0, bFace.TextureBindings[1].SceneTextureIndex);
        Assert.Same(shared, reverseSnapshot.Scene.Surface.Snapshot.Textures[aFace.TextureBindings[0].SceneTextureIndex]);
        Assert.Same(secondOnly, reverseSnapshot.Scene.Surface.Snapshot.Textures[bFace.TextureBindings[0].SceneTextureIndex]);
        Check(first); // The earlier captured mapping never mutates.
    }

    [Theory]
    [InlineData("star-rail", "GrayRed")]
    [InlineData("wuthering-waves", "GrayRed")]
    [InlineData("endfield", "EndfieldRgbCmM")]
    [InlineData("genshin", "GenshinSoftFace")]
    public void AllFamiliesPreserveExplicitFaceStrengthsAndSelectedBoneWorldOrientation(string preset, string encoding)
    {
        using var fixture = Fixture(animated: true, transformed: true);
        fixture.Project.SetDefaultRenderingStyle(new(preset), Token);
        var bones = BoneMap(fixture.Id);
        var local = Local(fixture.Id, preset, encoding, selectedBone: true);
        var requests = D3D12ViewPluginMaterialBindings.CreateRequests([local], ViewCatalog, Faces(fixture.Id), bones);
        var first = fixture.Project.CaptureRenderSnapshot(requests);
        var firstResult = Resolve(first, [local], Faces(fixture.Id, normal: .73f, shadow: .24f), bones);
        fixture.Project.Seek(30, Token);
        var second = fixture.Project.CaptureRenderSnapshot(requests);
        var secondResult = Resolve(second, [local], Faces(fixture.Id, normal: .73f, shadow: .24f), bones);
        var firstDraw = firstResult.SurfaceStyles.CharacterMaterials[0].PluginDraw!;
        var secondDraw = secondResult.SurfaceStyles.CharacterMaterials[0].PluginDraw!;
        var firstBasis = T8cTestHelpers.ReadFaceSdfConstants(firstDraw);
        var secondBasis = T8cTestHelpers.ReadFaceSdfConstants(secondDraw);
        var placement = fixture.Project.Objects[0].World;
        var expectedFirst = placement;
        var expectedSecond = Matrix4x4.CreateRotationY(MathF.PI) * placement;
        AssertVector(Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, expectedFirst)), firstBasis.Forward);
        AssertVector(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, expectedFirst)), firstBasis.Right);
        AssertVector(Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, expectedSecond)), secondBasis.Forward);
        AssertVector(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, expectedSecond)), secondBasis.Right);
        Assert.NotEqual(firstBasis.Right, secondBasis.Right);
        Assert.Equal(.73f, secondResult.SurfaceStyles.CharacterMaterials[0].FaceNormalShadeStrength);
        Assert.Equal(.24f, secondResult.SurfaceStyles.CharacterMaterials[0].FaceGeometricShadowStrength);
        Assert.Equal(30, fixture.Project.CurrentFrame);
        var reResolved = Resolve(first, [local], Faces(fixture.Id, normal: .73f, shadow: .24f), bones).SurfaceStyles.CharacterMaterials[0].PluginDraw!;
        Assert.Equal(firstDraw.ConstantBufferData.ToArray(), reResolved.ConstantBufferData.ToArray());
        Assert.Equal(D3D12CharacterMaterialRole.Body, secondResult.SurfaceStyles.CharacterMaterials[1].Role);
    }

    [Fact]
    public void HiddenObjectKeepsConfigurationWithoutConsumingAbsentTexturesOrBoneAndRevalidatesOnReveal()
    {
        var model = Model(); var layout = new SceneCompositionEditor();
        var visible = layout.Add("visible", SourcePath, ModelRole.Character, Rest(model, [Mask("visible", 32)]));
        var hidden = layout.Add("hidden", SourcePath, ModelRole.Character, Rest(model, [Mask("hidden", 128)]));
        var motion = new VmdMotion { BoneKeyframes = Motion().BoneKeyframes,
            IkKeyframes = [new() { Visible = false }, new() { Frame = 30, Visible = true }] };
        using var project = Project(layout, [new(hidden.Id, model, motion, PhysicsEnabled: false)]);
        project.SetDefaultRenderingStyle(new("star-rail"), Token);
        // Intentionally invalid upper bounds cannot be consumed while the object is hidden.
        D3D12ViewPluginMaterialBinding[] local =
        [
            TestStylePlugins.Bind(hidden.Id, 99, T8cTestHelpers.FaceSdfBinding(T8cTestHelpers.StarRailStyleId, 99,
                trackingMode: StyleBoneTrackingMode.SelectedBone, boneName: "99")),
        ];
        var materials = new D3D12ViewCharacterMaterial[] { new(hidden.Id, 99, D3D12CharacterMaterialRole.Face) };
        var requests = D3D12ViewPluginMaterialBindings.CreateRequests(local, ViewCatalog, materials);
        var snapshot = project.CaptureRenderSnapshot(requests);
        Assert.False(snapshot.SceneInputs[hidden.Id].Visible);
        Assert.Empty(snapshot.SceneInputs[hidden.Id].TextureIndices);
        Assert.Empty(snapshot.SceneInputs[hidden.Id].BoneWorldMatrices);
        var resolved = Resolve(snapshot, local, materials);
        Assert.All(resolved.SurfaceStyles.CharacterMaterials, material => Assert.Null(material.PluginDraw));
        project.Seek(30, Token);
        Assert.Throws<ArgumentOutOfRangeException>(() => project.CaptureRenderSnapshot(requests));
        Assert.Contains(project.Objects, item => item.Id == visible.Id);
    }

    [Fact]
    public void MissingInputsMissingRequestedTextureAndMissingRequestedBoneNeverInventMapping()
    {
        using var fixture = Fixture(animated: true);
        var bones = BoneMap(fixture.Id);
        var local = Local(fixture.Id, T8cTestHelpers.StarRailStyleId, "GrayRed", selectedBone: true);
        var faces = Faces(fixture.Id);
        var noRequests = fixture.Project.CaptureRenderSnapshot();
        Assert.Throws<ArgumentException>(() => Resolve(noRequests, [local], faces, bones));
        var noBone = fixture.Project.CaptureRenderSnapshot([new(fixture.Id, localTextureIndices: [0])]);
        Assert.Throws<ArgumentException>(() => Resolve(noBone, [local], faces, bones));
        var noTexture = fixture.Project.CaptureRenderSnapshot([new(fixture.Id, boneIndices: [1])]);
        Assert.Throws<ArgumentException>(() => Resolve(noTexture, [local], faces, bones));
        var correct = fixture.Project.CaptureRenderSnapshot(D3D12ViewPluginMaterialBindings.CreateRequests([local], ViewCatalog, faces, bones));
        Assert.Equal("face-sdf", Resolve(correct, [local], faces, bones).SurfaceStyles.CharacterMaterials[0].PluginDraw!.ShaderModuleId);
        Assert.Equal(0, fixture.Project.CurrentFrame);
    }

    [Fact]
    public void FixedWorldBasisWorksForStaticContentWithoutCreatingMotionController()
    {
        using var fixture = Fixture();
        var bones = BoneMap(fixture.Id);
        var local = Local(fixture.Id, T8cTestHelpers.StarRailStyleId, "GrayRed", selectedBone: false);
        var requests = D3D12ViewPluginMaterialBindings.CreateRequests([local], ViewCatalog, Faces(fixture.Id), bones);
        Assert.Empty(Assert.Single(requests).BoneIndices);
        var snapshot = fixture.Project.CaptureRenderSnapshot(requests);
        var basis = T8cTestHelpers.ReadFaceSdfConstants(Resolve(snapshot, [local], Faces(fixture.Id), bones).SurfaceStyles.CharacterMaterials[0].PluginDraw!);
        Assert.Equal(-Vector3.UnitZ, basis.Forward); Assert.Equal(Vector3.UnitX, basis.Right);
        var needsBone = Local(fixture.Id, T8cTestHelpers.StarRailStyleId, "GrayRed", selectedBone: true);
        Assert.Throws<InvalidOperationException>(() => fixture.Project.CaptureRenderSnapshot(
            D3D12ViewPluginMaterialBindings.CreateRequests([needsBone], ViewCatalog, Faces(fixture.Id), bones)));
    }

    [Fact]
    public void InvalidDescriptorsAndConflictingRolesRejectBeforeNativeRendererConstruction()
    {
        var id = Guid.NewGuid();
        var validData = T8cTestHelpers.FaceSdfBinding(T8cTestHelpers.StarRailStyleId, 0);
        var valid = TestStylePlugins.Bind(id, 0, validData);
        D3D12ViewPluginMaterialBinding[][] invalid =
        [
            [null!],
            [valid with { ObjectId = Guid.Empty }],
            [valid with { LocalMaterialIndex = -1 }],
            [TestStylePlugins.Bind(id, 0, T8cTestHelpers.FaceSdfBinding(T8cTestHelpers.StarRailStyleId, -1))],
            [TestStylePlugins.Bind(id, 0, validData with { BoneBinding = new(StyleBoneTrackingMode.SelectedBone, BoneName: "") })],
            [TestStylePlugins.Bind(id, 0, T8cTestHelpers.FaceSdfBinding(T8cTestHelpers.StarRailStyleId, 0, forward: Vector3.Zero))],
            [TestStylePlugins.Bind(id, 0, T8cTestHelpers.FaceSdfBinding(T8cTestHelpers.StarRailStyleId, 0, forward: Vector3.UnitX, right: Vector3.UnitX))],
            [TestStylePlugins.Bind(id, 0, T8cTestHelpers.FaceSdfBinding(T8cTestHelpers.StarRailStyleId, 0, transition: float.NaN))],
            [TestStylePlugins.Bind(id, 0, T8cTestHelpers.FaceSdfBinding(T8cTestHelpers.StarRailStyleId, 0, encoding: "Unknown"))],
            [TestStylePlugins.Bind(id, 0, T8cTestHelpers.FaceSdfBinding(T8cTestHelpers.StarRailStyleId, 0, secondaryTextureSlot: 1))],
            [valid, valid],
        ];
        foreach (var values in invalid)
        {
            Assert.ThrowsAny<ArgumentException>(() => new D3D12ViewRenderer(new(97, 73), characterMaterials: Faces(id),
                pluginBindings: values, styleCatalog: ViewCatalog));
            Assert.ThrowsAny<ArgumentException>(() => new D3D12RealtimeViewRenderer(0, new(97, 73), characterMaterials: Faces(id),
                pluginBindings: values, styleCatalog: ViewCatalog));
        }
        foreach (var role in new[] { D3D12CharacterMaterialRole.Body, D3D12CharacterMaterialRole.Eye })
            Assert.Throws<ArgumentException>(() => new D3D12ViewRenderer(new(97, 73),
                characterMaterials: [new(id, 0, role)], pluginBindings: [valid], styleCatalog: ViewCatalog));
        var wutheringFace = TestStylePlugins.Bind(id, 0, T8cTestHelpers.FaceSdfBinding(T8cTestHelpers.WutheringStyleId, 0));
        Assert.Throws<ArgumentException>(() => new D3D12ViewRenderer(new(97, 73), pluginBindings: [wutheringFace], styleCatalog: ViewCatalog));
        Assert.Throws<ArgumentException>(() => new D3D12ViewRenderer(new(97, 73),
            characterMaterials: Faces(id),
            pluginBindings: [valid, TestStylePlugins.Bind(id, 0, T8cTestHelpers.StarMcHairBinding(T8cTestHelpers.StarRailStyleId, 0, 1))],
            styleCatalog: ViewCatalog));
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("endfield")]
    public void ConfiguredGrayInputsNeverSilentlySwitchIncompatibleOrDefaultMmdStyle(string preset)
    {
        using var fixture = Fixture(); fixture.Project.SetDefaultRenderingStyle(new(preset), Token);
        var snapshot = fixture.Project.CaptureRenderSnapshot();
        if (preset == "mmd")
        {
            var dormant = Local(fixture.Id, T8cTestHelpers.StarRailStyleId, "GrayRed", selectedBone: false);
            var resolved = D3D12ViewRenderingStyles.Resolve(Ready(snapshot.Scene), Catalog,
                snapshot.RenderingStyles, Faces(fixture.Id), [dormant], snapshot.SceneInputs, styleCatalog: ViewCatalog);
            Assert.True(resolved.SurfaceStyles.IsDefault);
            Assert.Null(resolved.SurfaceStyles.CharacterMaterials[0].PluginDraw);
        }
        else
        {
            var incompatible = TestStylePlugins.Bind(fixture.Id, 0, T8cTestHelpers.FaceSdfBinding(preset, 0, encoding: "GrayRed"));
            Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(snapshot.Scene, Catalog,
                snapshot.RenderingStyles, Faces(fixture.Id), [incompatible], styleCatalog: ViewCatalog));
        }
        Assert.Equal(preset, fixture.Project.GetRenderingStyle(fixture.Id).StableId);
    }

    [Fact]
    public async Task ProjectPreviewFakePresenterReceivesSameSampleMappingsAndHeadAxes()
    {
        var fixture = Fixture(animated: true); // Preview owns the project from here.
        var bones = BoneMap(fixture.Id);
        var local = Local(fixture.Id, T8cTestHelpers.StarRailStyleId, "GrayRed", selectedBone: true);
        var renderer = new RecordingPreview(local, Faces(fixture.Id), bones);
        await using var preview = await PreviewSession.CreateProjectAsync(new(97, 73),
            _ => ValueTask.FromResult(fixture.Project), (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
        await preview.PresentAsync(0, Token); await preview.PresentAsync(1, Token);
        Assert.Equal(new double[] { 0, 30 }, renderer.Frames.Select(frame => frame.SourceFrame));
        Assert.NotEqual(renderer.Resolved[0].Forward, renderer.Resolved[1].Forward);
        Assert.Equal(-Vector3.UnitZ, renderer.Resolved[0].Forward);
        AssertVector(Vector3.UnitZ, renderer.Resolved[1].Forward);
        Assert.Same(renderer.Frames[0].Scene.Surface.Snapshot.Textures[0], renderer.Frames[1].Scene.Surface.Snapshot.Textures[0]);
        Assert.Equal(Matrix4x4.Identity, renderer.Frames[0].SceneInputs[fixture.Id].BoneWorldMatrices[1]);
        Assert.Equal(30, fixture.Project.CurrentFrame);
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData("star-rail", "GrayRed")]
    [InlineData("wuthering-waves", "GrayRed")]
    [InlineData("endfield", "EndfieldRgbCmM")]
    [InlineData("genshin", "GenshinSoftFace")]
    public async Task ActualOfflineViewMapsLocalImagesAndAnimatedHeadToSameBytesAsExplicitWorldBinding(
        string preset, string encoding)
    {
        using var fixture = Fixture(animated: true, encoding: encoding); fixture.Project.SetDefaultRenderingStyle(new(preset), Token);
        var bones = BoneMap(fixture.Id);
        var local = Local(fixture.Id, preset, encoding, selectedBone: true);
        var faces = Faces(fixture.Id, normal: 1, shadow: .35f);
        var quality = new ViewQualityOptions(Antialiasing: FrameAntialiasing.Smaa1X, SmaaQuality: SmaaQuality.High, SpatialSamples: 2);
        var request = new ViewRenderRequest(129, 97, quality);
        await using var renderer = new D3D12ViewRenderer(request, characterMaterials: faces,
            pluginBindings: [local], styleCatalog: ViewCatalog, boneIndicesByObject: bones);
        Assert.False(renderer.IsWarp); Assert.Single(renderer.SceneInputRequests);
        var plan = new OfflineFramePlan(request.Width, request.Height, new(30), 0, 31);
        var images = new List<byte[]>();
        foreach (int index in new[] { 0, 30 })
        {
            fixture.Project.Seek(index, Token);
            var snapshot = fixture.Project.CaptureRenderSnapshot(renderer.SceneInputRequests);
            var frame = new OfflineFrame(plan.GetSample(index), request.Width, request.Height, snapshot.Scene,
                snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
            { RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs, ResolvedCamera = Camera };
            using var captured = await renderer.CaptureAsync(frame, Token);
            var world = snapshot.SceneInputs[fixture.Id].BoneWorldMatrices[1];
            float transition = (float)local.Binding.NumberValues!["transition"];
            float offset = (float)local.Binding.NumberValues["offset"];
            var texIndices = snapshot.SceneInputs[fixture.Id].TextureIndices;
            var explicitSdf = T8cTestHelpers.FaceSdfBinding(
                preset,
                texIndices[0],
                secondaryTextureSlot: encoding == "GrayRed" ? -1 : texIndices[1],
                forward: Vector3.TransformNormal(-Vector3.UnitZ, world),
                right: Vector3.TransformNormal(Vector3.UnitX, world),
                transition: transition,
                offset: offset,
                encoding: encoding);
            await using var direct = new D3D12ViewRenderer(request, characterMaterials: faces,
                pluginBindings: [TestStylePlugins.Bind(fixture.Id, 0, explicitSdf)], styleCatalog: ViewCatalog);
            var directInputs = new Dictionary<Guid, SceneObjectRenderInputs>
            {
                [fixture.Id] = TestStylePlugins.CreateSceneInputs(fixture.Id, snapshot.Scene.Surface.Snapshot.Textures.Count),
            };
            using var expected = await direct.CaptureAsync(frame with { SceneInputs = directInputs }, Token);
            Assert.Equal(expected.Pixels.ToArray(), captured.Pixels.ToArray());
            Assert.Contains(captured.Pixels.ToArray().Where((_, pixelOffset) => pixelOffset % 4 == 3), alpha => alpha == 255);
            images.Add(captured.Pixels.ToArray());
        }
        Assert.NotEqual(images[0], images[1]);
        var before = renderer.Statistics!;
        var missing = fixture.Project.CaptureRenderSnapshot();
        var invalid = new OfflineFrame(plan.GetSample(30), request.Width, request.Height, missing.Scene, null, null, null)
        { RenderingStyles = missing.RenderingStyles, ResolvedCamera = Camera };
        await Assert.ThrowsAsync<ArgumentException>(() => renderer.CaptureAsync(invalid, Token).AsTask());
        Assert.Same(before, renderer.Statistics);
    }

    private static D3D12ViewRenderingStylesResult Resolve(SceneProjectRenderSnapshot snapshot,
        IReadOnlyList<D3D12ViewPluginMaterialBinding> local, IReadOnlyList<D3D12ViewCharacterMaterial> materials,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? bones = null)
        => D3D12ViewRenderingStyles.Resolve(Ready(snapshot.Scene), Catalog, snapshot.RenderingStyles, materials, local,
            snapshot.SceneInputs, styleCatalog: ViewCatalog, boneIndicesByObject: bones);
    private static OriginalFrameScene Ready(OriginalFrameScene scene)
        => D3D12ViewScene.Resolve(scene, null, null, null, new Dictionary<Guid, CharacterMorphChannels>(), new(), Camera);
    private static D3D12ViewCharacterMaterial[] Faces(Guid id, float normal = .35f, float shadow = .35f)
        => [new(id, 0, D3D12CharacterMaterialRole.Face, FaceNormalShadeStrength: normal, FaceGeometricShadowStrength: shadow)];
    private static D3D12ViewCharacterMaterial[] Faces(Guid a, Guid b) => [.. Faces(a), .. Faces(b)];
    private static IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>> BoneMap(Guid id)
        => new Dictionary<Guid, IReadOnlyDictionary<string, int>>
        {
            [id] = new Dictionary<string, int>(StringComparer.Ordinal) { ["selected head"] = 1 },
        };
    private static StyleMaterialBindingData SelectedFace(string styleId, int primary)
        => T8cTestHelpers.FaceSdfBinding(styleId, primary, trackingMode: StyleBoneTrackingMode.SelectedBone, boneName: "selected head");
    private static D3D12ViewPluginMaterialBinding Local(Guid id, string styleId, string encoding, bool selectedBone)
        => TestStylePlugins.Bind(id, 0, T8cTestHelpers.FaceSdfBinding(
            styleId,
            0,
            secondaryTextureSlot: encoding == "GrayRed" ? -1 : 1,
            transition: encoding switch { "GenshinSoftFace" => .001f, "GrayRed" => 1f, _ => .1f },
            offset: encoding == "GenshinSoftFace" ? .55f : 1f,
            encoding: encoding,
            trackingMode: selectedBone ? StyleBoneTrackingMode.SelectedBone : StyleBoneTrackingMode.FixedWorld,
            boneName: selectedBone ? "selected head" : null));
    private static void AssertVector(Vector3 expected, Vector3 actual)
    { Assert.InRange(Vector3.Distance(expected, actual), 0, 2e-5f); }
    private static string SourcePath => Path.Combine(Path.GetTempPath(), "mda-local-face-fixture.pmx");
    private static SceneTexture Mask(string name, byte value) => new(name, 1, 1, [value, value, value, 0]);
    private static VmdMotion Motion() => new()
    {
        BoneKeyframes = [new() { BoneName = "selected head" },
            new() { BoneName = "selected head", Frame = 30, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI) }]
    };
    private static FixtureOwner Fixture(bool animated = false, bool transformed = false, string encoding = "GrayRed")
    {
        var model = Model(); var layout = new SceneCompositionEditor();
        var textures = new SceneTexture[]
        {
            new("original synthetic asymmetric face", 4, 1, [0,0,0,0, 64,64,64,0, 192,192,192,0, 255,255,255,0]),
            new("original synthetic secondary", 1, 1,
                encoding == "EndfieldRgbCmM" ? new byte[] {255,0,0,0} : new byte[] {255,255,0,0})
        };
        var item = layout.Add("explicit character", SourcePath, ModelRole.Character, Rest(model, textures));
        if (transformed) layout.SetTransform(item.Id, new(7, 8, 9), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .3f), 2);
        var project = Project(layout, animated ? [new(item.Id, model, Motion(), PhysicsEnabled: false)] : []);
        project.SetDefaultRenderingStyle(new("star-rail"), Token);
        return new(project, item.Id);
    }
    private static SceneProjectSession Project(SceneCompositionEditor layout, IEnumerable<SceneActorMotion>? motions = null)
        => new(layout, new(), motions ?? [], new NoPhysics(), cancellationToken: Token);
    private sealed record FixtureOwner(SceneProjectSession Project, Guid Id) : IDisposable
    { public void Dispose() => Project.Dispose(); }
    private static OriginalFrameScene Rest(PmxModel model, IReadOnlyList<SceneTexture> textures)
    {
        var adapted = PmxOriginalStyleAdapter.FromRestPose(model).Scene;
        var original = adapted.Snapshot;
        var snapshot = new SceneSnapshot(original.Vertices, original.Indices, original.Materials, textures,
            camera: Camera, lightDirection: Vector3.UnitX);
        return new(new(snapshot, adapted.Materials.Select(material => material with { ToonShadowColor = new(.2f) }),
                new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, Camera.Eye, 30, 1),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero),
            materials: PmxOriginalFrameAdapter.FromMaterials(model));
    }
    private static PmxModel Model()
    {
        Vector3[] Quad(float y) => [new(-3, y - .35f, 0), new(3, y - .35f, 0), new(3, y + .35f, 0), new(-3, y + .35f, 0)];
        var vertices = new[] { Quad(.55f), Quad(-.55f) }.SelectMany(quad => quad).Select(position => new PmxVertex
        { Position = position, Normal = Vector3.UnitX, Uv = new(.375f, .5f), AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
            BoneIndices = [0,-1,-1,-1], BoneWeights = [1,0,0,0], EdgeScale = 1 }).ToArray();
        PmxMaterial Material() => new() { Name = "explicit draw", EnglishName = "", Diffuse = new(.6f,.4f,.2f,1),
            Specular = Vector3.Zero, SpecularPower = 1, Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None,
            EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
            ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 6 };
        return new()
        {
            Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = "fixture", EnglishName = "", Comment = "", EnglishComment = "", Textures = [],
            Faces = [new(0,2,1),new(0,3,2),new(4,6,5),new(4,7,6)], Materials = [Material(),Material()], Vertices = vertices,
            Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None },
                new() { Name = "selected head", EnglishName = "", Position = Vector3.Zero, ParentIndex = 0, DeformDepth = 0, Flags = PmxBoneFlags.None }],
            Morphs = [], DisplayFrames = [], Joints = [], RigidBodies = [], SoftBodies = []
        };
    }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
            => throw new InvalidOperationException("The fixture has no physical bodies.");
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings)
            => throw new InvalidOperationException("The fixture has no shared physics.");
    }
    private sealed class RecordingPreview(
        D3D12ViewPluginMaterialBinding local,
        D3D12ViewCharacterMaterial[] materials,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>> bones)
        : IRealtimeViewRenderer, IViewSceneInputRequests
    {
        public ViewRendererCapabilities Capabilities => new("recording-local-face", 4096, 4096, false, true);
        public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests { get; } =
            D3D12ViewPluginMaterialBindings.CreateRequests([local], ViewCatalog, materials, bones);
        public List<PreviewFrame> Frames { get; } = [];
        public List<(Vector3 Forward, Vector3 Right, float Transition, float Offset)> Resolved { get; } = [];
        public ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Frames.Add(frame);
            var draw = D3D12ViewRenderingStyles.Resolve(Ready(frame.Scene), Catalog, frame.RenderingStyles,
                materials, [local], frame.SceneInputs, styleCatalog: ViewCatalog, boneIndicesByObject: bones)
                .SurfaceStyles.CharacterMaterials[0].PluginDraw!;
            Resolved.Add(T8cTestHelpers.ReadFaceSdfConstants(draw));
            return ValueTask.CompletedTask;
        }
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
