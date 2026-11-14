using System.Numerics;
using System.Text.Json;
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
public sealed class D3D12GeneratedMaterialGpuTests : IDisposable
{
    private const int W = 161, H = 97;
    private static readonly SceneCamera Camera = new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2.5f, .1f, 30);
    private static readonly Vector3[] BodySamples = [new(-1.26f, -.16f, 0), new(-1, .16f, 0), new(-.74f, -.16f, 0)];
    private readonly string directory = Path.Combine(Path.GetTempPath(), "generated-material-gpu-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public D3D12GeneratedMaterialGpuTests() => Directory.CreateDirectory(directory);

    [Fact, Trait("Category", "Gpu")]
    public async Task GeneratedMatcapProducesNonuniformNormalResponseAndCameraDependentMaterialPatternThroughView()
    {
        var source = CreateSource(out string path);
        byte[] originalFile = File.ReadAllBytes(path);
        var generated = Prepare(source);
        using var project = Project(generated.SourceRest, path, out Guid id);
        var plainProfile = Profile(generated, enabled: false);
        var profile = Profile(generated);
        float[][] patterns = new float[2][];
        SceneCamera[] cameras = [Camera, Camera with { Eye = new(1.9f, .65f, -5) }];
        for (int i = 0; i < cameras.Length; i++)
        {
            byte[] plain = await Capture(project, id, generated.SourceRest, plainProfile, "endfield", cameras[i]);
            byte[] actual = await Capture(project, id, generated.SourceRest, profile, "endfield", cameras[i]);
            AssertSameAlpha(plain, actual);
            patterns[i] = BodySamples.Select(point => RedDelta(plain, actual, point, cameras[i])).ToArray();
            Assert.True(patterns[i].Count(delta => Math.Abs(delta) >= 3) >= 2,
                "Multiple covered body pixels must exercise the generated material.");
            Assert.True(patterns[i].Max() - patterns[i].Min() >= 2,
                "Normals must change the material response across the same quad, beyond a uniform brightness offset.");
            AssertProtectedRegions(plain, actual, cameras[i]);
        }
        Assert.True(patterns[0].Zip(patterns[1], (a, b) => Math.Abs(a - b)).Max() >= 2,
            "After subtracting each camera's plain frame, the material pattern must still change with camera orientation.");
        Assert.Equal(originalFile, File.ReadAllBytes(path));
        AssertOriginalSource(source, generated.SourceRest);
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData("matcap")]
    [InlineData("p")]
    public async Task EditingGeneratedMapsChangesBodyPixelsWhileFaceEyeAndDefaultMmdRemainExact(string selected)
    {
        var source = CreateSource(out string path);
        byte[] sourceFile = File.ReadAllBytes(path);
        var generated = Prepare(source);
        var maps = Assert.Single(generated.Profile.Materials).EndfieldMatcap!;
        var slot = generated.SourceRest.Surface.Snapshot.Textures[
            selected == "matcap" ? maps.Matcap.SourceSceneTextureOrdinal : maps.P.SourceSceneTextureOrdinal];
        var replacement = Solid(slot, selected == "matcap" ? (byte)255 : (byte)0);
        var edited = selected == "matcap" ? generated.WithEditedMaps(0, matcap: replacement)
            : generated.WithEditedMaps(0, p: replacement);
        using var originalProject = Project(generated.SourceRest, path, out Guid originalId);
        using var editedProject = Project(edited.SourceRest, path, out Guid editedId);
        byte[] before = await Capture(originalProject, originalId, generated.SourceRest, Profile(generated), "endfield");
        byte[] after = await Capture(editedProject, editedId, edited.SourceRest, Profile(edited), "endfield");
        Assert.True(BodySamples.Count(point => Math.Abs(RedDelta(before, after, point, Camera)) >= 3) >= 2);
        AssertProtectedRegions(before, after, Camera);
        AssertSameAlpha(before, after);
        Assert.Equal(await Capture(originalProject, originalId, generated.SourceRest, Profile(generated), "mmd"),
            await Capture(editedProject, editedId, edited.SourceRest, Profile(edited), "mmd"));
        Assert.Equal(sourceFile, File.ReadAllBytes(path));
        AssertOriginalSource(source, edited.SourceRest);
        using var manifest = JsonDocument.Parse(edited.ManifestBytes);
        Assert.True(manifest.RootElement.GetProperty("materials")[0].GetProperty("outputsUserEdited").GetBoolean());
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task EditedGeneratedAssetsAndProvenanceSurviveMovedPmaDeletedSourceAndDefaultResaveWithExactGpuFrame()
    {
        var source = CreateSource(out string sourcePath);
        var generated = Prepare(source);
        var matcap = Assert.Single(generated.Profile.Materials).EndfieldMatcap!.Matcap;
        var texture = generated.SourceRest.Surface.Snapshot.Textures[matcap.SourceSceneTextureOrdinal];
        byte[] editedPixels = texture.Rgba.ToArray();
        for (int i = 0; i < editedPixels.Length; i += 4) editedPixels[i + 1] = (byte)(editedPixels[i + 1] / 2);
        generated = generated.WithEditedMaps(0, matcap: new("user edited generated MatCap", texture.Width, texture.Height, editedPixels));
        using var project = Project(generated.SourceRest, sourcePath, out Guid id);
        var profile = generated.Profile;
        var expected = await Capture(project, id, generated.SourceRest, profile, "endfield");
        string manifestPath = "render-resources/generated-materials/" + id.ToString("N") + ".json";
        var options = D3D12GeneratedMaterialPma.CreateSaveOptions(project, [new(id, generated)]);
        string first = Path.Combine(directory, "first.pma");
        PmaProjectStore.Save(first, project, options, Token);
        string movedDirectory = Path.Combine(directory, "different-machine");
        Directory.CreateDirectory(movedDirectory);
        string moved = Path.Combine(movedDirectory, "moved.pma");
        File.Move(first, moved);
        File.Delete(sourcePath);
        Assert.False(File.Exists(sourcePath));
        string resaved = Path.Combine(movedDirectory, "default-resave.pma");
        using (var loaded = D3D12MaterialBindingPma.Load(moved, Path.Combine(directory, "cache"), Load,
            new NoPhysics(), out var profiles, cancellationToken: Token))
        {
            var restored = Assert.Single(loaded.Session.Objects);
            AssertPortable(generated, profile, loaded, profiles[id], restored.Source, manifestPath);
            Assert.Equal(expected, await Capture(loaded.Session, id, restored.Source, profiles[id], "endfield"));
            PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
        }
        using var reopened = D3D12MaterialBindingPma.Load(resaved, Path.Combine(directory, "cache2"), Load,
            new NoPhysics(), out var again, cancellationToken: Token);
        var final = Assert.Single(reopened.Session.Objects);
        AssertPortable(generated, profile, reopened, again[id], final.Source, manifestPath);
        Assert.Equal(expected, await Capture(reopened.Session, id, final.Source, again[id], "endfield"));
        // Reopening must restore editable state, not only enough opaque pixels to display.
        var resumable = D3D12GeneratedMaterialRestore.Restore(final.Source, again[id], reopened.ReadResource(manifestPath));
        var resumedMap = resumable.Profile.Materials[0].EndfieldMatcap!.Matcap;
        var revised = resumable.WithEditedMaps(0, matcap: Solid(
            resumable.SourceRest.Surface.Snapshot.Textures[resumedMap.SourceSceneTextureOrdinal], 255));
        using var revisedProject = Project(revised.SourceRest, final.SourcePath, out Guid revisedId);
        byte[] revisedFrame = await Capture(revisedProject, revisedId, revised.SourceRest, revised.Profile, "endfield");
        Assert.True(BodySamples.Count(point => Math.Abs(RedDelta(expected, revisedFrame, point, Camera)) >= 3) >= 2);
        AssertProtectedRegions(expected, revisedFrame, Camera);
        AssertSameAlpha(expected, revisedFrame);
        string third = Path.Combine(movedDirectory, "reopened-and-edited.pma");
        PmaProjectStore.Save(third, revisedProject,
            D3D12GeneratedMaterialPma.CreateSaveOptions(revisedProject, [new(revisedId, revised)]), Token);
        using var thirdLoad = D3D12MaterialBindingPma.Load(third, Path.Combine(directory, "cache3"), Load,
            new NoPhysics(), out var thirdProfiles, cancellationToken: Token);
        var thirdObject = Assert.Single(thirdLoad.Session.Objects);
        Assert.Equal(revisedFrame, await Capture(thirdLoad.Session, revisedId, thirdObject.Source,
            thirdProfiles[revisedId], "endfield"));
        var thirdEditable = D3D12GeneratedMaterialRestore.Restore(thirdObject.Source, thirdProfiles[revisedId],
            thirdLoad.ReadResource(D3D12GeneratedMaterialPma.ManifestPath(revisedId)));
        Assert.Equal(revised.ManifestBytes, thirdEditable.ManifestBytes);
    }

    private static D3D12GeneratedEndfieldMaterialPreparationResult Prepare(OriginalFrameScene source)
        => D3D12GeneratedEndfieldMaterialPreparation.Prepare(source,
            [new(0, new(D3D12GeneratedPmxSurfaceKind.Metal, MapSize: 64, HighlightMask: .8f, MatcapWidth: .24f))],
            "generated-gpu", "Generated GPU fixture", Token);

    private static D3D12MaterialBindingProfile Profile(D3D12GeneratedEndfieldMaterialPreparationResult generated, bool enabled = true)
    {
        var body = Assert.Single(generated.Profile.Materials);
        return new(generated.Profile.StableId, generated.Profile.Name, generated.Profile.LayoutFingerprint,
            [enabled ? body : body with { EndfieldMatcap = null },
                new(1, D3D12CharacterMaterialRole.Face), new(2, D3D12CharacterMaterialRole.Eye)]);
    }

    private static async Task<byte[]> Capture(SceneProjectSession project, Guid id, OriginalFrameScene source,
        D3D12MaterialBindingProfile profile, string pipeline, SceneCamera? camera = null)
    {
        project.SetDefaultRenderingStyle(new(pipeline), Token);
        var mapping = D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline);
        await using var renderer = new D3D12ViewRenderer(new(W, H), characterMaterials: mapping.CharacterMaterials,
            endfieldMatcapDataBindings: mapping.EndfieldMatcapBindings);
        Assert.False(renderer.IsWarp);
        var snapshot = project.CaptureRenderSnapshot(renderer.SceneInputRequests);
        var scene = new OriginalFrameScene(snapshot.Scene.Surface, snapshot.Scene.Outline, snapshot.Scene.OutlineOptions,
            new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero), snapshot.Scene.Materials,
            snapshot.Scene.Lighting);
        var plan = new OfflineFramePlan(W, H, new(30), 0, 0);
        var frame = new OfflineFrame(plan.GetSample(0), W, H, scene, null, null, null)
        { RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs, ResolvedCamera = camera ?? Camera };
        using var image = await renderer.CaptureAsync(frame, Token);
        return image.Pixels.ToArray();
    }

    private static void AssertPortable(D3D12GeneratedEndfieldMaterialPreparationResult generated,
        D3D12MaterialBindingProfile expected, PmaLoadedProject loaded, D3D12MaterialBindingProfile actual, OriginalFrameScene restored,
        string manifestPath)
    {
        Assert.Equal(D3D12MaterialBindingProfileJson.Encode(expected), D3D12MaterialBindingProfileJson.Encode(actual));
        Assert.Contains(manifestPath, loaded.ResourcePaths);
        Assert.Equal(generated.ManifestBytes, loaded.ReadResource(manifestPath));
        var textures = generated.SourceRest.Surface.Snapshot.Textures;
        Assert.Equal(textures.Count, restored.Surface.Snapshot.Textures.Count);
        for (int i = 0; i < textures.Count; i++)
        {
            var copy = restored.Surface.Snapshot.Textures[i];
            Assert.Equal(textures[i].Width, copy.Width); Assert.Equal(textures[i].Height, copy.Height);
            Assert.Equal(textures[i].Rgba.ToArray(), copy.Rgba.ToArray());
        }
    }

    private static void AssertOriginalSource(OriginalFrameScene original, OriginalFrameScene generated)
    {
        Assert.Equal(original.Surface.Snapshot.Vertices.ToArray(), generated.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(original.Surface.Snapshot.Materials, generated.Surface.Snapshot.Materials);
        for (int i = 0; i < original.Surface.Snapshot.Textures.Count; i++)
            Assert.Equal(original.Surface.Snapshot.Textures[i].Rgba.ToArray(), generated.Surface.Snapshot.Textures[i].Rgba.ToArray());
    }

    private static SceneTexture Solid(SceneTexture source, byte value)
    {
        byte[] pixels = new byte[source.Width * source.Height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = pixels[i + 1] = pixels[i + 2] = value; pixels[i + 3] = 255; }
        // P alpha adds to the highlight gate; it is not image coverage.
        if (value == 0) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 0;
        return new("explicit edited map", source.Width, source.Height, pixels);
    }

    private static float RedDelta(byte[] before, byte[] after, Vector3 point, SceneCamera camera)
    {
        var (x, y) = Pixel(point, camera);
        Assert.Equal(255, before[(y * W + x) * 4 + 3]);
        Assert.Equal(255, after[(y * W + x) * 4 + 3]);
        return after[(y * W + x) * 4] - before[(y * W + x) * 4];
    }

    private static void AssertProtectedRegions(byte[] before, byte[] after, SceneCamera camera)
    {
        foreach (float center in new[] { 0f, 1f })
        {
            var (x, y) = Pixel(new(center, 0, 0), camera);
            Assert.Equal(255, before[(y * W + x) * 4 + 3]);
            for (int dy = -7; dy <= 7; dy++) for (int dx = -7; dx <= 7; dx++)
            for (int channel = 0; channel < 4; channel++)
                Assert.Equal(before[((y + dy) * W + x + dx) * 4 + channel], after[((y + dy) * W + x + dx) * 4 + channel]);
        }
    }

    private static void AssertSameAlpha(byte[] before, byte[] after)
    {
        Assert.Equal(before.Length, after.Length);
        for (int i = 3; i < before.Length; i += 4) Assert.Equal(before[i], after[i]);
    }

    private static (int X, int Y) Pixel(Vector3 point, SceneCamera camera)
    {
        var clip = Vector4.Transform(new Vector4(point, 1), camera.ViewProjection(W, H));
        return ((int)((clip.X / clip.W * .5f + .5f) * W), (int)((.5f - clip.Y / clip.W * .5f) * H));
    }

    private static SceneProjectSession Project(OriginalFrameScene source, string path, out Guid id)
    {
        var composition = new SceneCompositionEditor();
        id = composition.Add("explicit generated body with protected face and eye", path, ModelRole.Character, source).Id;
        return new(composition, new(), [], new NoPhysics(), cancellationToken: Token);
    }

    private OriginalFrameScene CreateSource(out string path)
    {
        var template = PmxReader.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
        var vertices = new List<PmxVertex>(); var faces = new List<PmxTriangle>(); var materials = new List<PmxMaterial>();
        for (int material = 0; material < 3; material++)
        {
            int start = vertices.Count; float center = material - 1;
            foreach (var corner in new[] { new Vector2(-.38f, -.5f), new(.38f, -.5f), new(.38f, .5f), new(-.38f, .5f) })
                vertices.Add(new()
                {
                    Position = new(center + corner.X, corner.Y, 0),
                    Normal = material == 0 ? Vector3.Normalize(new(corner.X * 2, .08f, -1)) : -Vector3.UnitZ,
                    Uv = new((corner.X / .38f + 1) * .5f, (corner.Y / .5f + 1) * .5f),
                    AdditionalUvs = new Vector4[template.Header.AdditionalUvCount],
                    WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1
                });
            faces.Add(new(start, start + 2, start + 1)); faces.Add(new(start, start + 3, start + 2));
            materials.Add(new()
            {
                Name = "explicit material " + material, EnglishName = "", Diffuse = new(.6f, .4f, .25f, 1),
                Specular = Vector3.Zero, SpecularPower = 10, Ambient = new(.2f), EdgeColor = Vector4.Zero, EdgeSize = 0,
                DrawMode = PmxDrawModeFlags.BothFaces, TextureIndex = -1, SphereTextureIndex = -1,
                SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Common, ToonTextureIndex = 0, Memo = "", FaceVertexCount = 6
            });
        }
        var model = new PmxModel
        {
            Header = template.Header, Name = "generated maps fixture", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = vertices.ToArray(), Faces = faces.ToArray(), Textures = [], Materials = materials.ToArray(),
            Bones = template.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
        path = Path.Combine(directory, "original.pmx");
        File.WriteAllBytes(path, PmxWriter.ToBytes(model));
        return Rest(model);
    }

    private static OriginalFrameScene Rest(PmxModel model)
    {
        var original = PmxOriginalStyleAdapter.FromRestPose(model).Scene;
        var s = original.Snapshot;
        // An unused original source image also travels byte-exactly, independently of derived maps.
        var snapshot = new SceneSnapshot(s.Vertices, s.Indices, s.Materials,
            [.. s.Textures, new SceneTexture("original unused source image", 2, 1, [13, 27, 91, 255, 88, 61, 42, 128])],
            s.World, Camera, -Vector3.UnitZ, s.AdditionalUvs);
        return new(new(snapshot, original.Materials, original.Profile), PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.One, Matrix4x4.Identity, Camera.Eye, 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
    }

    private static SceneAnimationModelAsset Load(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(path); return new(model, Rest(model)); }

    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject obj) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
