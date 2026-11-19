using System.Numerics;
using System.Runtime.InteropServices;
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
public sealed class EndfieldHairPmaSeedGpuTests : IDisposable
{
    private const int Width = 129, Height = 81;
    private static readonly SceneCamera Camera = new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 20);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "endfield-hair-pma-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public EndfieldHairPmaSeedGpuTests() => Directory.CreateDirectory(directory);

    [Theory, Trait("Category", "Gpu")]
    [InlineData("endfield")]
    [InlineData("mmd")]
    public async Task MovedSeedsAndGreenImagesKeepInstanceIsolationAndExactPixelsThroughDefaultResave(string pipeline)
    {
        using var project = Project(out var entries);
        project.SetDefaultRenderingStyle(new(pipeline), Token);
        var before = entries.Select(entry => D3D12MaterialBindingProfileResolver.Resolve(
            entry.Profile, entry.SourceRest, entry.ObjectId, pipeline, TestStylePlugins.AllViewCatalog)).ToArray();
        byte[] expected = await CaptureAsync(project, before);
        byte[] withoutHair = await CaptureAsync(project, pipeline == "endfield" ? before : [], dedicated: false);
        int changed = 0;
        for (int offset = 0; offset < expected.Length; offset += 4)
        {
            Assert.Equal(withoutHair[offset + 3], expected[offset + 3]);
            if (!withoutHair.AsSpan(offset, 3).SequenceEqual(expected.AsSpan(offset, 3))) changed++;
        }
        if (pipeline == "endfield")
            Assert.True(changed > 100, $"Only {changed} pixels consumed the authored radial Hair inputs.");
        else
        {
            Assert.Equal(withoutHair, expected);
            Assert.All(before, mapping => Assert.Empty(mapping.PluginBindings));
        }
        Assert.Contains(expected.Where((_, index) => index % 4 == 3), alpha => alpha != 0);

        string first = Path.Combine(directory, "first.pma");
        PmaProjectStore.Save(first, project, D3D12MaterialBindingPma.CreateSaveOptions(project, entries), Token);
        string movedDirectory = Path.Combine(directory, "moved"); Directory.CreateDirectory(movedDirectory);
        string moved = Path.Combine(movedDirectory, "project.pma"); File.Move(first, moved);
        File.Delete(project.Objects[0].SourcePath);
        int plainLoads = 0;
        SceneAnimationModelAsset PlainLoader(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var model = PmxReader.Read(path); var source = Rest(model);
            Assert.Null(source.Surface.Snapshot.RawRadialSeeds);
            plainLoads++;
            return new(model, source);
        }

        string resaved = Path.Combine(directory, "resaved.pma");
        using (var loaded = D3D12MaterialBindingPma.Load(moved, Path.Combine(directory, "cache"), PlainLoader,
            new NoPhysics(), out var profiles, cancellationToken: Token))
        {
            var after = AssertRestored(loaded.Session, profiles, entries, pipeline);
            Assert.Equal(expected, await CaptureAsync(loaded.Session, after));
            await PresentAsync(loaded.Session, after);
            PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
        }
        using var again = D3D12MaterialBindingPma.Load(resaved, Path.Combine(directory, "cache2"), PlainLoader,
            new NoPhysics(), out var savedProfiles, cancellationToken: Token);
        var saved = AssertRestored(again.Session, savedProfiles, entries, pipeline);
        Assert.Equal(expected, await CaptureAsync(again.Session, saved));
        Assert.Equal(4, plainLoads);
    }

    private static D3D12ResolvedMaterialBindingProfile[] AssertRestored(SceneProjectSession project,
        IReadOnlyDictionary<Guid, D3D12MaterialBindingProfile> profiles, D3D12MaterialBindingPmaEntry[] entries, string pipeline)
    {
        Assert.Equal(2, profiles.Count);
        var mappings = new List<D3D12ResolvedMaterialBindingProfile>();
        foreach (var entry in entries)
        {
            var source = project.Objects.Single(value => value.Id == entry.ObjectId).Source;
            var expected = entry.SourceRest.Surface.Snapshot; var actual = source.Surface.Snapshot;
            Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, profiles[entry.ObjectId].SchemaVersion);
            Assert.Equal(D3D12MaterialBindingProfileJson.Encode(entry.Profile), D3D12MaterialBindingProfileJson.Encode(profiles[entry.ObjectId]));
            Assert.Equal(D3D12MaterialBindingLayout.Capture(entry.SourceRest).Fingerprint,
                D3D12MaterialBindingLayout.Capture(source).Fingerprint);
            Assert.NotNull(actual.RawRadialSeeds);
            Assert.Equal(SeedBits(expected.RawRadialSeeds!), SeedBits(actual.RawRadialSeeds));
            Assert.Equal(expected.Textures.Count, actual.Textures.Count);
            for (int index = 0; index < expected.Textures.Count; index++)
                Assert.Equal(expected.Textures[index].Rgba.ToArray(), actual.Textures[index].Rgba.ToArray());
            var mapping = D3D12MaterialBindingProfileResolver.Resolve(profiles[entry.ObjectId], source, entry.ObjectId, pipeline, TestStylePlugins.AllViewCatalog);
            Assert.Equal(pipeline == "endfield" ? 1 : 0, mapping.PluginBindings.Count);
            mappings.Add(mapping);
        }
        Assert.NotEqual(SeedBits(project.Objects[0].Source.Surface.Snapshot.RawRadialSeeds!),
            SeedBits(project.Objects[1].Source.Surface.Snapshot.RawRadialSeeds!));
        Assert.NotEqual(project.Objects[0].Source.Surface.Snapshot.Textures[^1].Rgba.ToArray(),
            project.Objects[1].Source.Surface.Snapshot.Textures[^1].Rgba.ToArray());
        return mappings.ToArray();
    }

    private static int[] SeedBits(SceneRawRadialSeeds seeds) => seeds.Vertices.ToArray()
        .SelectMany(value => new[] { value.X, value.Y, value.Z }).Select(BitConverter.SingleToInt32Bits).ToArray();

    private static async Task<byte[]> CaptureAsync(SceneProjectSession project,
        D3D12ResolvedMaterialBindingProfile[] mappings, bool dedicated = true)
    {
        await using var renderer = new D3D12ViewRenderer(new(Width, Height),
            stylePresets: TestStylePlugins.AllPresetCatalog,
            characterMaterials: mappings.SelectMany(value => value.CharacterMaterials),
            pluginBindings: dedicated ? mappings.SelectMany(value => value.PluginBindings) : [],
            styleCatalog: TestStylePlugins.AllViewCatalog);
        Assert.False(renderer.IsWarp);
        var snapshot = project.CaptureRenderSnapshot(renderer.SceneInputRequests);
        if (dedicated) AssertComposedInputs(project, mappings, snapshot);
        var plan = new OfflineFramePlan(Width, Height, new(30), 0, 0);
        var frame = new OfflineFrame(plan.GetSample(0), Width, Height, snapshot.Scene, null, null, null)
        { RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs, ResolvedCamera = Camera };
        using var image = await renderer.CaptureAsync(frame, Token);
        return image.Pixels.ToArray();
    }

    private static void AssertComposedInputs(SceneProjectSession project, D3D12ResolvedMaterialBindingProfile[] mappings,
        SceneProjectRenderSnapshot snapshot)
    {
        var bindings = mappings.SelectMany(value => value.PluginBindings).ToArray();
        if (bindings.Length == 0) return;
        var styles = D3D12ViewRenderingStyles.Resolve(D3D12ViewScene.Resolve(snapshot.Scene, null, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), new(), Camera), TestStylePlugins.AllPresetCatalog,
            snapshot.RenderingStyles, mappings.SelectMany(value => value.CharacterMaterials).ToArray(),
            bindings, sceneInputs: snapshot.SceneInputs, styleCatalog: TestStylePlugins.AllViewCatalog);
        var combinedSeeds = snapshot.Scene.Surface.Snapshot.RawRadialSeeds!;
        int vertexStart = 0;
        var usedTextures = new HashSet<int>();
        foreach (var instance in snapshot.RenderingStyles)
        {
            var source = project.Objects.Single(value => value.Id == instance.ObjectId).Source.Surface.Snapshot;
            Assert.True(snapshot.SceneInputs[instance.ObjectId].HasRawRadialSeeds);
            Assert.Equal(source.RawRadialSeeds!.Vertices.ToArray(), combinedSeeds.Vertices.Slice(vertexStart, source.Vertices.Length).ToArray());
            vertexStart += source.Vertices.Length;
            var binding = bindings.Single(value => value.ObjectId == instance.ObjectId);
            int localTexture = binding.Binding.TextureSlots!["texture"];
            int texture = snapshot.SceneInputs[instance.ObjectId].TextureIndices[localTexture];
            Assert.True(usedTextures.Add(texture), "Each instance must retain its distinct Green image.");
            Assert.Equal(source.Textures[localTexture].Rgba.ToArray(), snapshot.Scene.Surface.Snapshot.Textures[texture].Rgba.ToArray());
            var draw = styles.SurfaceStyles.CharacterMaterials[instance.MaterialStart].PluginDraw;
            Assert.NotNull(draw);
            Assert.Equal("hair", draw.ShaderModuleId);
            Assert.Equal(texture, Assert.Single(draw.TextureBindings!).SceneTextureIndex);
            var cb = MemoryMarshal.Cast<byte, Vector4>(draw.ConstantBufferData.Span)[0];
            var expectedColor = binding.Binding.VectorValues!["paletteColor"];
            Assert.Equal(new Vector4(expectedColor[0], expectedColor[1], expectedColor[2], (float)binding.Binding.NumberValues!["brightness"]), cb);
        }
    }

    private static async Task PresentAsync(SceneProjectSession project, D3D12ResolvedMaterialBindingProfile[] mappings)
    {
        nint window = CreateWindowExW(0, "STATIC", "MDA portable Endfield Hair", 0x80000000, 0, 0, 160, 100, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            await using var renderer = new D3D12RealtimeViewRenderer(window, new(Width, Height),
                stylePresets: TestStylePlugins.AllPresetCatalog,
                characterMaterials: mappings.SelectMany(value => value.CharacterMaterials),
                pluginBindings: mappings.SelectMany(value => value.PluginBindings),
                styleCatalog: TestStylePlugins.AllViewCatalog);
            var snapshot = project.CaptureRenderSnapshot(renderer.SceneInputRequests);
            var frame = new PreviewFrame(Width, Height, 0, snapshot.Scene, null, null, null)
            { RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs, ResolvedCamera = Camera };
            renderer.ValidateRenderingStyles(snapshot.Scene, snapshot.RenderingStyles);
            renderer.ValidateSceneInputs(frame);
            await renderer.PresentAsync(frame, Token);
            Assert.Equal(1, renderer.Statistics.PresentCount);
            Assert.Equal(0, renderer.Statistics.ExplicitCaptureCount);
            Assert.Equal(0, renderer.Statistics.Frame!.TotalReadbacks);
        }
        finally { DestroyWindow(window); }
    }

    private SceneProjectSession Project(out D3D12MaterialBindingPmaEntry[] entries)
    {
        var template = PmxReader.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
        Vector3[] positions = [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)];
        var model = new PmxModel
        {
            Header = template.Header, Name = "portable radial Hair", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = positions.Select((position, index) => new PmxVertex
            {
                Position = position, Normal = -Vector3.UnitZ, Uv = new(index is 1 or 2 ? 1 : 0, index >= 2 ? 1 : 0),
                AdditionalUvs = new Vector4[template.Header.AdditionalUvCount], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 0
            }).ToArray(),
            Faces = [new(0, 2, 1), new(0, 3, 2)], Textures = [], Materials = [new()
            {
                Name = "explicit Hair", EnglishName = "", Diffuse = new(.3f, .2f, .1f, 1), Specular = Vector3.Zero,
                SpecularPower = 1, Ambient = new(.1f), EdgeColor = Vector4.Zero, EdgeSize = 0, DrawMode = PmxDrawModeFlags.BothFaces,
                TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
                ToonMode = PmxToonMode.Common, ToonTextureIndex = 0, Memo = "", FaceVertexCount = 6
            }],
            Bones = template.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
        string path = Path.Combine(directory, "model.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
        var composition = new SceneCompositionEditor(); var list = new List<D3D12MaterialBindingPmaEntry>();
        for (int index = 0; index < 2; index++)
        {
            var rest = Rest(model); var source = rest.Surface.Snapshot;
            Vector3[] seeds = index == 0 ? [new(2, 0, 0), Vector3.Zero, new(1, .5f, .25f), new(-.5f, 2, -.25f)]
                : [new(-2, .25f, .5f), new(-1, -2, 0), new(.5f, -.5f, -.25f), new(.125f, 3, 1)];
            var green = new SceneTexture("explicit instance Green", 1, 1, [7, (byte)(64 + index * 128), 37, 0]);
            var snapshot = new SceneSnapshot(source.Vertices, source.Indices, source.Materials, [.. source.Textures, green],
                source.World, source.Camera, source.LightDirection, additionalUvs: source.AdditionalUvs, rawRadialSeeds: new(seeds));
            var frame = new OriginalFrameScene(new(snapshot, rest.Surface.Materials, rest.Surface.Profile),
                rest.Outline, rest.OutlineOptions, rest.Options, rest.Materials, rest.Lighting);
            var item = composition.Add("explicit instance " + index, path, ModelRole.Character, frame);
            composition.SetTransform(item.Id, new((index - .5f) * 2.3f, 0, 0), Quaternion.Identity, 1);
            var layout = D3D12MaterialBindingLayout.Capture(frame);
            int texOrdinal = snapshot.Textures.Count - 1;
            var paletteColor = index == 0 ? new Vector3(.22f, .12f, .08f) : new Vector3(.08f, .2f, .3f);
            list.Add(new(item.Id, new("radial-" + index, "radial mapping " + index, layout.Fingerprint,
                [new(0, D3D12CharacterMaterialRole.Hair,
                    pluginBindings: [T8dTestHelpers.EndfieldHair(texOrdinal, paletteColor, .3f + index * .3f)],
                    textureGuards: [layout.CaptureTexture(texOrdinal)])]), frame));
        }
        entries = list.ToArray();
        return new(composition, new SceneLightingEditor(), [], new NoPhysics(), cancellationToken: Token);
    }

    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, Camera.Eye, 30, 1),
        options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));

    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extended, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
