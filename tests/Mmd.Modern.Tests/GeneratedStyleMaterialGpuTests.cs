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
public sealed class GeneratedStyleMaterialGpuTests : IDisposable
{
    private const int W = 161, H = 97;
    private static readonly SceneCamera Camera = new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2.5f, .1f, 30);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "generated-four-style-gpu-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public GeneratedStyleMaterialGpuTests() => Directory.CreateDirectory(directory);

    [Theory, Trait("Category", "Gpu")]
    [InlineData("genshin", false)] [InlineData("genshin", true)]
    [InlineData("star-rail", false)] [InlineData("star-rail", true)]
    [InlineData("wuthering-waves", false)] [InlineData("wuthering-waves", true)]
    [InlineData("endfield", false)] [InlineData("endfield", true)]
    public async Task GeneratedInputsAreConsumedAndMovedPmaRestoresEditableGpuOutput(string pipeline, bool hair)
    {
        var source = CreateSource(out string path); byte[] sourceBytes = File.ReadAllBytes(path);
        var generated = D3D12GeneratedStyleMaterialPreparation.Prepare(source,
            [new(0, new(hair ? D3D12GeneratedPmxSurfaceKind.Hair : D3D12GeneratedPmxSurfaceKind.Metal, 64))],
            pipeline, "generated-gpu-" + pipeline, "Generated GPU " + pipeline, Token);
        using var project = Project(generated.SourceRest, path, out Guid id);
        byte[] before = await Capture(project, id, generated.SourceRest, generated.Profile, pipeline);
        string selected = pipeline == "genshin" && hair ? "atlas" : "matcap";
        using var document = JsonDocument.Parse(generated.ManifestBytes);
        var selectedMap = document.RootElement.GetProperty("materials")[0].GetProperty("outputs").EnumerateArray()
            .Single(output => output.GetProperty("name").GetString() == selected);
        int ordinal = selectedMap.GetProperty("guard").GetProperty("sourceSceneTextureOrdinal").GetInt32();
        var image = generated.SourceRest.Surface.Snapshot.Textures[ordinal];
        byte[] white = Enumerable.Repeat((byte)255, image.Width * image.Height * 4).ToArray();
        var edited = generated.WithEditedMap(0, selected, new("GENERATED edited map", image.Width, image.Height, white));
        using var editedProject = Project(edited.SourceRest, path, out Guid editedId);
        byte[] changed = await Capture(editedProject, editedId, edited.SourceRest, edited.Profile, pipeline);
        int different = 0;
        for (int at = 0; at < changed.Length; at += 4)
        {
            Assert.Equal(before[at + 3], changed[at + 3]);
            if (!before.AsSpan(at, 3).SequenceEqual(changed.AsSpan(at, 3))) different++;
        }
        Assert.True(different > 30, $"{pipeline}/{hair} must consume edited generated {selected}, changed={different}.");
        AssertProtectedRegions(before, changed, Camera);
        Assert.Equal(await Capture(project, id, generated.SourceRest, generated.Profile, "mmd"),
            await Capture(editedProject, editedId, edited.SourceRest, edited.Profile, "mmd"));
        Assert.Equal(sourceBytes, File.ReadAllBytes(path));
        string first = Path.Combine(directory, "first.pma");
        PmaProjectStore.Save(first, editedProject, D3D12GeneratedStyleMaterialPma.CreateSaveOptions(editedProject,
            [new(editedId, edited)]), Token);
        string moved = Path.Combine(directory, "moved.pma"); File.Move(first, moved); File.Delete(path);
        using var loaded = D3D12MaterialBindingPma.Load(moved, Path.Combine(directory, "cache"), Load,
            new NoPhysics(), out var profiles, cancellationToken: Token);
        var obj = Assert.Single(loaded.Session.Objects);
        var restored = D3D12GeneratedStyleMaterialPreparation.Restore(obj.Source, profiles[editedId],
            loaded.ReadResource(D3D12GeneratedStyleMaterialPma.ManifestPath(editedId)));
        Assert.Equal(edited.ManifestBytes, restored.ManifestBytes);
        Assert.Equal(changed, await Capture(loaded.Session, editedId, obj.Source, profiles[editedId], pipeline));
        var second = restored.WithEditedMap(0, selected, image);
        string savedAgain = Path.Combine(directory, "edited-after-reopen.pma");
        PmaProjectStore.Save(savedAgain, loaded.Session, D3D12GeneratedStyleMaterialPma.CreateSaveOptions(loaded.Session,
            [new(editedId, second)]), Token);
        using var reopened = D3D12MaterialBindingPma.Load(savedAgain, Path.Combine(directory, "cache2"), Load,
            new NoPhysics(), out var finalProfiles, cancellationToken: Token);
        var final = Assert.Single(reopened.Session.Objects);
        Assert.Equal(before, await Capture(reopened.Session, editedId, final.Source, finalProfiles[editedId], pipeline));
    }

    private static async Task<byte[]> Capture(SceneProjectSession project, Guid id, OriginalFrameScene source,
        D3D12MaterialBindingProfile profile, string pipeline)
    {
        project.SetDefaultRenderingStyle(new(pipeline), Token);
        var mapping = D3D12MaterialBindingProfileResolver.Resolve(profile, source, id, pipeline);
        var materials = mapping.CharacterMaterials.Concat(new[] {
            new D3D12ViewCharacterMaterialBinding(id,1,new(D3D12CharacterMaterialRole.Face)),
            new D3D12ViewCharacterMaterialBinding(id,2,new(D3D12CharacterMaterialRole.Eye)) });
        await using var renderer = new D3D12ViewRenderer(new(W,H), characterMaterials: materials,
            starMcBodyDataBindings: mapping.StarMcBodyBindings, endfieldMatcapDataBindings: mapping.EndfieldMatcapBindings,
            genshinHairAtlasDataBindings: mapping.GenshinHairAtlasBindings, genshinMetallicDataBindings: mapping.GenshinMetallicBindings);
        Assert.False(renderer.IsWarp);
        var capture = project.CaptureRenderSnapshot(renderer.SceneInputRequests);
        var scene = new OriginalFrameScene(capture.Scene.Surface,capture.Scene.Outline,capture.Scene.OutlineOptions,
            new(OriginalAlphaBlendMode.Normal,true,false,BackgroundColor:Vector4.Zero),capture.Scene.Materials,capture.Scene.Lighting);
        var plan = new OfflineFramePlan(W,H,new(30),0,0);
        var frame = new OfflineFrame(plan.GetSample(0),W,H,scene,null,null,null)
        { RenderingStyles=capture.RenderingStyles, SceneInputs=capture.SceneInputs,ResolvedCamera=Camera };
        using var pixels = await renderer.CaptureAsync(frame,Token); return pixels.Pixels.ToArray();
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
            s.World, Camera, Vector3.UnitX, s.AdditionalUvs);
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
