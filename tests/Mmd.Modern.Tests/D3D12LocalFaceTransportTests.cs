using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12LocalFaceTransportTests
{
    private static readonly D3D12StylePresetCatalog Catalog = D3D12StylePresetCatalog.BuiltIn;
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly D3D12ViewFaceHeadBasis.FixedWorld Fixed = new(-Vector3.UnitZ, Vector3.UnitX);
    private static readonly D3D12ViewFaceHeadBasis.SelectedBone Head = new(1, -Vector3.UnitZ, Vector3.UnitX);
    private static readonly SceneCamera Camera = new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 20);

    [Fact]
    public void DefinitionsDetachOnceAndRequestsDeduplicateOnlySelectedInputs()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        D3D12ViewFaceSdfBinding[] source = [new(a, 0, 2, Head), new(a, 1, 2, Head),
            new(b, 0, 1, Fixed, Encoding: D3D12FaceSdfEncoding.EndfieldRgbCmM, SecondaryLocalTextureIndex: 0)];
        int enumerations = 0;
        IEnumerable<D3D12ViewFaceSdfBinding> Once()
        { enumerations++; foreach (var value in source) yield return value; }
        var detached = D3D12ViewFaceSdfBindings.Detach(Once());
        source[0] = source[0] with { LocalTextureIndex = 99 };
        Assert.Same(detached, D3D12ViewFaceSdfBindings.Detach(detached));
        Assert.Equal(1, enumerations); Assert.Equal(2, detached[0].LocalTextureIndex);
        var requests = D3D12ViewFaceSdfBindings.CreateRequests(detached);
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
        D3D12ViewFaceSdfBinding[] local =
        [new(a.Id, 0, 1, Fixed, .1f, 1, D3D12FaceSdfEncoding.EndfieldRgbCmM, 0),
            new(b.Id, 0, 1, Fixed, .1f, 1, D3D12FaceSdfEncoding.EndfieldRgbCmM, 0)];
        var materials = Faces(a.Id, b.Id);
        var requests = D3D12ViewFaceSdfBindings.CreateRequests(local);
        void Check(SceneProjectRenderSnapshot snapshot)
        {
            var resolved = D3D12ViewRenderingStyles.Resolve(Ready(snapshot.Scene), Catalog, snapshot.RenderingStyles,
                materials, local, snapshot.SceneInputs);
            foreach (var instance in snapshot.RenderingStyles)
            {
                var sdf = resolved.SurfaceStyles.CharacterMaterials[instance.MaterialStart].FaceSdf!;
                var textures = snapshot.Scene.Surface.Snapshot.Textures;
                Assert.Same(instance.ObjectId == a.Id ? shared : secondOnly, textures[sdf.CombinedTextureIndex]);
                Assert.Same(instance.ObjectId == a.Id ? firstOnly : shared, textures[sdf.SecondaryCombinedTextureIndex]);
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
        var reverseLocal = new[] { local[0] with { ObjectId = ra.Id }, local[1] with { ObjectId = rb.Id } };
        var reverseSnapshot = reverseProject.CaptureRenderSnapshot(D3D12ViewFaceSdfBindings.CreateRequests(reverseLocal));
        var reverse = D3D12ViewRenderingStyles.Resolve(Ready(reverseSnapshot.Scene), Catalog, reverseSnapshot.RenderingStyles,
            Faces(ra.Id, rb.Id), reverseLocal, reverseSnapshot.SceneInputs);
        var aFace = reverse.SurfaceStyles.CharacterMaterials[2].FaceSdf!;
        var bFace = reverse.SurfaceStyles.CharacterMaterials[0].FaceSdf!;
        Assert.Equal(0, aFace.CombinedTextureIndex); Assert.Equal(2, aFace.SecondaryCombinedTextureIndex);
        Assert.Equal(1, bFace.CombinedTextureIndex); Assert.Equal(0, bFace.SecondaryCombinedTextureIndex);
        Assert.Same(shared, reverseSnapshot.Scene.Surface.Snapshot.Textures[aFace.CombinedTextureIndex]);
        Assert.Same(secondOnly, reverseSnapshot.Scene.Surface.Snapshot.Textures[bFace.CombinedTextureIndex]);
        Check(first); // The earlier captured mapping never mutates.
    }

    [Theory]
    [InlineData("star-rail", D3D12FaceSdfEncoding.GrayRed)]
    [InlineData("wuthering-waves", D3D12FaceSdfEncoding.GrayRed)]
    [InlineData("endfield", D3D12FaceSdfEncoding.EndfieldRgbCmM)]
    [InlineData("genshin", D3D12FaceSdfEncoding.GenshinSoftFace)]
    public void AllFamiliesPreserveExplicitFaceStrengthsAndSelectedBoneWorldOrientation(string preset, D3D12FaceSdfEncoding encoding)
    {
        using var fixture = Fixture(animated: true, transformed: true);
        fixture.Project.SetDefaultRenderingStyle(new(preset), Token);
        var local = Local(fixture.Id, encoding, Head);
        var requests = D3D12ViewFaceSdfBindings.CreateRequests([local]);
        var first = fixture.Project.CaptureRenderSnapshot(requests);
        var firstResult = Resolve(first, [local], Faces(fixture.Id, normal: .73f, shadow: .24f));
        fixture.Project.Seek(30, Token);
        var second = fixture.Project.CaptureRenderSnapshot(requests);
        var secondResult = Resolve(second, [local], Faces(fixture.Id, normal: .73f, shadow: .24f));
        var firstSdf = firstResult.SurfaceStyles.CharacterMaterials[0].FaceSdf!;
        var secondSdf = secondResult.SurfaceStyles.CharacterMaterials[0].FaceSdf!;
        var placement = fixture.Project.Objects[0].World;
        var expectedFirst = placement;
        var expectedSecond = Matrix4x4.CreateRotationY(MathF.PI) * placement;
        AssertVector(Vector3.TransformNormal(-Vector3.UnitZ, expectedFirst), firstSdf.HeadWorldForward);
        AssertVector(Vector3.TransformNormal(Vector3.UnitX, expectedFirst), firstSdf.HeadWorldRight);
        AssertVector(Vector3.TransformNormal(-Vector3.UnitZ, expectedSecond), secondSdf.HeadWorldForward);
        AssertVector(Vector3.TransformNormal(Vector3.UnitX, expectedSecond), secondSdf.HeadWorldRight);
        Assert.NotEqual(firstSdf.HeadWorldRight, secondSdf.HeadWorldRight);
        Assert.Equal(.73f, secondResult.SurfaceStyles.CharacterMaterials[0].FaceNormalShadeStrength);
        Assert.Equal(.24f, secondResult.SurfaceStyles.CharacterMaterials[0].FaceGeometricShadowStrength);
        Assert.Equal(30, fixture.Project.CurrentFrame);
        Assert.Equal(firstSdf, Resolve(first, [local], Faces(fixture.Id, normal: .73f, shadow: .24f))
            .SurfaceStyles.CharacterMaterials[0].FaceSdf);
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
        D3D12ViewFaceSdfBinding[] local = [new(hidden.Id, 99, 99, new D3D12ViewFaceHeadBasis.SelectedBone(99, -Vector3.UnitZ, Vector3.UnitX))];
        var requests = D3D12ViewFaceSdfBindings.CreateRequests(local);
        var snapshot = project.CaptureRenderSnapshot(requests);
        Assert.False(snapshot.SceneInputs[hidden.Id].Visible);
        Assert.Empty(snapshot.SceneInputs[hidden.Id].TextureIndices);
        Assert.Empty(snapshot.SceneInputs[hidden.Id].BoneWorldMatrices);
        var resolved = Resolve(snapshot, local, [new(hidden.Id, 99, new(D3D12CharacterMaterialRole.Face))]);
        Assert.All(resolved.SurfaceStyles.CharacterMaterials, material => Assert.Null(material.FaceSdf));
        project.Seek(30, Token);
        Assert.Throws<ArgumentOutOfRangeException>(() => project.CaptureRenderSnapshot(requests));
        Assert.Contains(project.Objects, item => item.Id == visible.Id);
    }

    [Fact]
    public void MissingInputsMissingRequestedTextureAndMissingRequestedBoneNeverInventMapping()
    {
        using var fixture = Fixture(animated: true);
        var local = Local(fixture.Id, D3D12FaceSdfEncoding.GrayRed, Head);
        var faces = Faces(fixture.Id);
        var noRequests = fixture.Project.CaptureRenderSnapshot();
        Assert.Throws<ArgumentException>(() => Resolve(noRequests, [local], faces));
        var noBone = fixture.Project.CaptureRenderSnapshot([new(fixture.Id, localTextureIndices: [0])]);
        Assert.Throws<ArgumentException>(() => Resolve(noBone, [local], faces));
        var noTexture = fixture.Project.CaptureRenderSnapshot([new(fixture.Id, boneIndices: [1])]);
        Assert.Throws<ArgumentException>(() => Resolve(noTexture, [local], faces));
        var correct = fixture.Project.CaptureRenderSnapshot(D3D12ViewFaceSdfBindings.CreateRequests([local]));
        Assert.NotNull(Resolve(correct, [local], faces).SurfaceStyles.CharacterMaterials[0].FaceSdf);
        Assert.Equal(0, fixture.Project.CurrentFrame);
    }

    [Fact]
    public void FixedWorldBasisWorksForStaticContentWithoutCreatingMotionController()
    {
        using var fixture = Fixture(); var local = Local(fixture.Id, D3D12FaceSdfEncoding.GrayRed, Fixed);
        var requests = D3D12ViewFaceSdfBindings.CreateRequests([local]);
        Assert.Empty(Assert.Single(requests).BoneIndices);
        var snapshot = fixture.Project.CaptureRenderSnapshot(requests);
        var sdf = Resolve(snapshot, [local], Faces(fixture.Id)).SurfaceStyles.CharacterMaterials[0].FaceSdf!;
        Assert.Equal(-Vector3.UnitZ, sdf.HeadWorldForward); Assert.Equal(Vector3.UnitX, sdf.HeadWorldRight);
        var needsBone = local with { HeadBasis = Head };
        Assert.Throws<InvalidOperationException>(() => fixture.Project.CaptureRenderSnapshot(D3D12ViewFaceSdfBindings.CreateRequests([needsBone])));
    }

    [Fact]
    public void InvalidDescriptorsAndConflictingRolesRejectBeforeNativeRendererConstruction()
    {
        var id = Guid.NewGuid(); var valid = new D3D12ViewFaceSdfBinding(id, 0, 0, Fixed);
        D3D12ViewFaceSdfBinding[][] invalid =
        [ [null!], [valid with { ObjectId = Guid.Empty }], [valid with { LocalMaterialIndex = -1 }],
            [valid with { LocalTextureIndex = -1 }], [valid with { HeadBasis = null! }],
            [valid with { HeadBasis = new D3D12ViewFaceHeadBasis.SelectedBone(-1, -Vector3.UnitZ, Vector3.UnitX) }],
            [valid with { HeadBasis = new D3D12ViewFaceHeadBasis.FixedWorld(Vector3.Zero, Vector3.UnitX) }],
            [valid with { HeadBasis = new D3D12ViewFaceHeadBasis.FixedWorld(Vector3.UnitX, Vector3.UnitX) }],
            [valid with { Transition = float.NaN }], [valid with { Encoding = (D3D12FaceSdfEncoding)999 }],
            [valid with { SecondaryLocalTextureIndex = 1 }], [valid, valid] ];
        foreach (var values in invalid)
        {
            Assert.ThrowsAny<ArgumentException>(() => new D3D12ViewRenderer(new(97, 73), characterMaterials: Faces(id), faceDataBindings: values));
            Assert.ThrowsAny<ArgumentException>(() => new D3D12RealtimeViewRenderer(0, new(97, 73), characterMaterials: Faces(id), faceDataBindings: values));
        }
        foreach (var role in new[] { D3D12CharacterMaterialRole.Body, D3D12CharacterMaterialRole.Eye })
            Assert.Throws<ArgumentException>(() => new D3D12ViewRenderer(new(97, 73),
                characterMaterials: [new(id, 0, new(role))], faceDataBindings: [valid]));
        Assert.Throws<ArgumentException>(() => new D3D12ViewRenderer(new(97, 73), faceDataBindings: [valid]));
        Assert.Throws<ArgumentException>(() => new D3D12ViewRenderer(new(97, 73), characterMaterials:
            [new(id, 0, new(D3D12CharacterMaterialRole.Face, FaceSdf: new(0, -Vector3.UnitZ, Vector3.UnitX)))], faceDataBindings: [valid]));
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("endfield")]
    public void ConfiguredGrayInputsNeverSilentlySwitchIncompatibleOrDefaultMmdStyle(string preset)
    {
        using var fixture = Fixture(); fixture.Project.SetDefaultRenderingStyle(new(preset), Token);
        var snapshot = fixture.Project.CaptureRenderSnapshot();
        var local = Local(fixture.Id, D3D12FaceSdfEncoding.GrayRed, Fixed);
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(snapshot.Scene, Catalog,
            snapshot.RenderingStyles, Faces(fixture.Id), [local]));
        Assert.Equal(preset, fixture.Project.GetRenderingStyle(fixture.Id).StableId);
    }

    [Fact]
    public async Task ProjectPreviewFakePresenterReceivesSameSampleMappingsAndHeadAxes()
    {
        var fixture = Fixture(animated: true); // Preview owns the project from here.
        var local = Local(fixture.Id, D3D12FaceSdfEncoding.GrayRed, Head);
        var renderer = new RecordingPreview(local, Faces(fixture.Id));
        await using var preview = await PreviewSession.CreateProjectAsync(new(97, 73),
            _ => ValueTask.FromResult(fixture.Project), (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
        await preview.PresentAsync(0, Token); await preview.PresentAsync(1, Token);
        Assert.Equal(new double[] { 0, 30 }, renderer.Frames.Select(frame => frame.SourceFrame));
        Assert.NotEqual(renderer.Resolved[0].HeadWorldForward, renderer.Resolved[1].HeadWorldForward);
        Assert.Equal(-Vector3.UnitZ, renderer.Resolved[0].HeadWorldForward);
        AssertVector(Vector3.UnitZ, renderer.Resolved[1].HeadWorldForward);
        Assert.Same(renderer.Frames[0].Scene.Surface.Snapshot.Textures[0], renderer.Frames[1].Scene.Surface.Snapshot.Textures[0]);
        Assert.Equal(Matrix4x4.Identity, renderer.Frames[0].SceneInputs[fixture.Id].BoneWorldMatrices[1]);
        Assert.Equal(30, fixture.Project.CurrentFrame);
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData("star-rail", D3D12FaceSdfEncoding.GrayRed)]
    [InlineData("wuthering-waves", D3D12FaceSdfEncoding.GrayRed)]
    [InlineData("endfield", D3D12FaceSdfEncoding.EndfieldRgbCmM)]
    [InlineData("genshin", D3D12FaceSdfEncoding.GenshinSoftFace)]
    public async Task ActualOfflineViewMapsLocalImagesAndAnimatedHeadToSameBytesAsExplicitWorldBinding(
        string preset, D3D12FaceSdfEncoding encoding)
    {
        using var fixture = Fixture(animated: true, encoding: encoding); fixture.Project.SetDefaultRenderingStyle(new(preset), Token);
        var local = Local(fixture.Id, encoding, Head);
        var faces = Faces(fixture.Id, normal: 1, shadow: .35f);
        var quality = new ViewQualityOptions(Antialiasing: FrameAntialiasing.Smaa1X, SmaaQuality: SmaaQuality.High, SpatialSamples: 2);
        var request = new ViewRenderRequest(129, 97, quality);
        await using var renderer = new D3D12ViewRenderer(request, characterMaterials: faces, faceDataBindings: [local]);
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
            var explicitSdf = new D3D12FaceSdfBinding(snapshot.SceneInputs[fixture.Id].TextureIndices[0],
                Vector3.TransformNormal(-Vector3.UnitZ, world), Vector3.TransformNormal(Vector3.UnitX, world),
                local.Transition, local.Offset, Encoding: encoding,
                SecondaryCombinedTextureIndex: encoding == D3D12FaceSdfEncoding.GrayRed ? -1 : snapshot.SceneInputs[fixture.Id].TextureIndices[1]);
            await using var direct = new D3D12ViewRenderer(request,
                characterMaterials: [faces[0] with { Style = faces[0].Style with { FaceSdf = explicitSdf } }]);
            using var expected = await direct.CaptureAsync(frame with { SceneInputs = SceneObjectRenderInputs.Empty }, Token);
            Assert.Equal(expected.Pixels.ToArray(), captured.Pixels.ToArray());
            Assert.Contains(captured.Pixels.ToArray().Where((_, offset) => offset % 4 == 3), alpha => alpha == 255);
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
        IReadOnlyList<D3D12ViewFaceSdfBinding> local, IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials)
        => D3D12ViewRenderingStyles.Resolve(Ready(snapshot.Scene), Catalog, snapshot.RenderingStyles, materials, local, snapshot.SceneInputs);
    private static OriginalFrameScene Ready(OriginalFrameScene scene)
        => D3D12ViewScene.Resolve(scene, null, null, null, new Dictionary<Guid, CharacterMorphChannels>(), new(), Camera);
    private static D3D12ViewCharacterMaterialBinding[] Faces(Guid id, float normal = .35f, float shadow = .35f)
        => [new(id, 0, new(D3D12CharacterMaterialRole.Face, normal, shadow))];
    private static D3D12ViewCharacterMaterialBinding[] Faces(Guid a, Guid b) => [.. Faces(a), .. Faces(b)];
    private static D3D12ViewFaceSdfBinding Local(Guid id, D3D12FaceSdfEncoding encoding, D3D12ViewFaceHeadBasis head)
        => new(id, 0, 0, head, encoding switch
            { D3D12FaceSdfEncoding.GenshinSoftFace => .001f, D3D12FaceSdfEncoding.GrayRed => 1f, _ => .1f },
            encoding == D3D12FaceSdfEncoding.GenshinSoftFace ? .55f : 1f, encoding,
            encoding == D3D12FaceSdfEncoding.GrayRed ? -1 : 1);
    private static void AssertVector(Vector3 expected, Vector3 actual)
    { Assert.InRange(Vector3.Distance(expected, actual), 0, 2e-5f); }
    private static string SourcePath => Path.Combine(Path.GetTempPath(), "mda-local-face-fixture.pmx");
    private static SceneTexture Mask(string name, byte value) => new(name, 1, 1, [value, value, value, 0]);
    private static VmdMotion Motion() => new()
    {
        BoneKeyframes = [new() { BoneName = "selected head" },
            new() { BoneName = "selected head", Frame = 30, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI) }]
    };
    private static FixtureOwner Fixture(bool animated = false, bool transformed = false,
        D3D12FaceSdfEncoding encoding = D3D12FaceSdfEncoding.GrayRed)
    {
        var model = Model(); var layout = new SceneCompositionEditor();
        var textures = new SceneTexture[]
        {
            new("original synthetic asymmetric face", 4, 1, [0,0,0,0, 64,64,64,0, 192,192,192,0, 255,255,255,0]),
            new("original synthetic secondary", 1, 1,
                encoding == D3D12FaceSdfEncoding.EndfieldRgbCmM ? new byte[] {255,0,0,0} : new byte[] {255,255,0,0})
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
    private sealed class RecordingPreview(D3D12ViewFaceSdfBinding local, D3D12ViewCharacterMaterialBinding[] materials)
        : IRealtimeViewRenderer, IViewSceneInputRequests
    {
        public ViewRendererCapabilities Capabilities => new("recording-local-face", 4096, 4096, false, true);
        public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests { get; } = D3D12ViewFaceSdfBindings.CreateRequests([local]);
        public List<PreviewFrame> Frames { get; } = [];
        public List<D3D12FaceSdfBinding> Resolved { get; } = [];
        public ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Frames.Add(frame);
            Resolved.Add(D3D12ViewRenderingStyles.Resolve(Ready(frame.Scene), Catalog, frame.RenderingStyles,
                materials, [local], frame.SceneInputs).SurfaceStyles.CharacterMaterials[0].FaceSdf!);
            return ValueTask.CompletedTask;
        }
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
