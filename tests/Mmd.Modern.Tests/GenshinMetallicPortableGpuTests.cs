using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.Smoke;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class GenshinMetallicPortableGpuTests
{
    private const int W = 97, H = 97;
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly SceneCamera Camera = new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2.6f, .1f, 30);
    private static readonly OriginalFrameOptions ModernOptions = new(OriginalAlphaBlendMode.Normal, true, false,
        BackgroundColor: Vector4.Zero);

    [Theory, Trait("Category", "Gpu")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MovedPackageRestoresExactGpuRgbaAndFullTextureTableAfterSourceImagesRemovalAndDefaultResave(bool withSpecular)
    {
        string directory = Path.Combine(Path.GetTempPath(), "metallic-gpu-pma-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string originalPath = Path.Combine(directory, "source.pmx");
            var model = Model();
            File.WriteAllBytes(originalPath, PmxWriter.ToBytes(model));
            WriteBmp(Path.Combine(directory, "selected-lightmap.bmp"), 255, 128, 255);
            WriteBmp(Path.Combine(directory, "selected-matcap.bmp"), 60, 170, 110);
            WriteBmp(Path.Combine(directory, "selected-ramp.bmp"), 60, 100, 180);
            var rest = Rest(PmxReader.Read(originalPath)); var old = rest.Surface.Snapshot;
            int start = old.Textures.Count;
            var table = old.Textures.Concat(new[]
            {
                Assert.IsType<SceneTexture>(SceneTextureLoader.Load(directory, "selected-lightmap.bmp")),
                Assert.IsType<SceneTexture>(SceneTextureLoader.Load(directory, "selected-matcap.bmp")),
                Assert.IsType<SceneTexture>(SceneTextureLoader.Load(directory, "selected-ramp.bmp")),
                new SceneTexture("unreferenced portable resource", 1, 1, [3, 8, 19, 37])
            }).ToArray();
            var snapshot = new SceneSnapshot(old.Vertices, old.Indices, old.Materials, table,
                old.World, old.Camera, old.LightDirection, old.AdditionalUvs, old.RawRadialSeeds);
            var source = new OriginalFrameScene(new(snapshot, rest.Surface.Materials, rest.Surface.Profile),
                rest.Outline, rest.OutlineOptions, ModernOptions, rest.Materials);
            var editor = new SceneCompositionEditor();
            var item = editor.Add("explicit portable metallic", originalPath, ModelRole.Character, source);
            using var project = new SceneProjectSession(editor, new SceneLightingEditor(), [], new NoPhysics(), cancellationToken: Token);
            project.SetDefaultRenderingStyle(new("genshin"), Token);
            var layout = D3D12MaterialBindingLayout.Capture(source);
            var profile = new D3D12MaterialBindingProfile("metallic-portable-gpu", "Portable metallic GPU", layout.Fingerprint,
                [new(0, D3D12CharacterMaterialRole.Body, GenshinMetallic: new(layout.CaptureTexture(start),
                    layout.CaptureTexture(start + 1), D3D12MatcapColorEncoding.SrgbRgb, new(.2f, .3f, .4f),
                    new(.9f, 1.1f, .8f), new(.8f, .7f, .6f), .65f, 1.7f, .8f,
                    D3D12GenshinMetallicLitMode.Uniform, .95f,
                    Specular: withSpecular ? new(new(.75f,.85f,.95f),Vector3.Zero,Shininess:0,
                        Ramp:layout.CaptureTexture(start+2),RampMix:1) : null))]);
            Assert.Equal(withSpecular ? 7 : 6, profile.SchemaVersion);
            var mapping = D3D12MaterialBindingProfileResolver.Resolve(profile, source, item.Id, "genshin");
            byte[] expected = await Capture(project, mapping);
            byte[] off = await Capture(project, mapping, dedicated: false);
            AssertVisibleChange(off, expected);
            if(withSpecular) AssertVisibleChange(await Capture(project,mapping,withoutSpecular:true),expected);

            string first = Path.Combine(directory, "first.pma");
            PmaProjectStore.Save(first, project, D3D12MaterialBindingPma.CreateSaveOptions(project,
                [new(item.Id, profile, source)]), Token);
            string movedDirectory = Path.Combine(directory, "moved"); Directory.CreateDirectory(movedDirectory);
            string moved = Path.Combine(movedDirectory, "portable.pma"); File.Move(first, moved);
            File.Delete(originalPath);
            File.Delete(Path.Combine(directory, "selected-lightmap.bmp"));
            File.Delete(Path.Combine(directory, "selected-matcap.bmp"));
            File.Delete(Path.Combine(directory, "selected-ramp.bmp"));
            string resaved = Path.Combine(movedDirectory, "resaved.pma");
            using (var loaded = D3D12MaterialBindingPma.Load(moved, Path.Combine(directory, "cache"), Load,
                new NoPhysics(), out var profiles, cancellationToken: Token))
            {
                var restored = Assert.Single(loaded.Session.Objects);
                Assert.Equal(item.Id, restored.Id);
                AssertRestored(profile, profiles[restored.Id], table, restored.Source);
                var restoredMapping = D3D12MaterialBindingProfileResolver.Resolve(profiles[restored.Id], restored.Source, restored.Id, "genshin");
                Assert.Equal(start, Assert.Single(restoredMapping.GenshinMetallicBindings).LightmapLocalTextureIndex);
                Assert.Equal(start + 1, Assert.Single(restoredMapping.GenshinMetallicBindings).MatcapLocalTextureIndex);
                Assert.Equal(expected, await Capture(loaded.Session, restoredMapping));
                PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
            }
            using var reopened = D3D12MaterialBindingPma.Load(resaved, Path.Combine(directory, "cache2"), Load,
                new NoPhysics(), out var savedProfiles, cancellationToken: Token);
            var again = Assert.Single(reopened.Session.Objects);
            AssertRestored(profile, savedProfiles[again.Id], table, again.Source);
            var savedMapping = D3D12MaterialBindingProfileResolver.Resolve(savedProfiles[again.Id], again.Source, again.Id, "genshin");
            Assert.Equal(expected, await Capture(reopened.Session, savedMapping));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact, Trait("Category", "Gpu")]
    public void MixedGenshinMetallicAndEndfieldMatcapSrgbDescriptorsRemainIndependentInTheSameCapture()
    {
        // The right-handed camera looking toward +Z maps world +X to the left image half.
        SceneVertex[] vertices = new[] { .55f, -.55f }.SelectMany(center => new[]
        {
            new SceneVertex(new(center - .43f, -.7f, 0), -Vector3.UnitZ, new(0, 1)),
            new SceneVertex(new(center + .43f, -.7f, 0), -Vector3.UnitZ, new(1, 1)),
            new SceneVertex(new(center + .43f, .7f, 0), -Vector3.UnitZ, new(1, 0)),
            new SceneVertex(new(center - .43f, .7f, 0), -Vector3.UnitZ, new(0, 0))
        }).ToArray();
        var scene = new SceneSnapshot(vertices, [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6],
            [new("explicit genshin", 0, 6, new(.5f, .4f, .3f, 1), Vector3.Zero, Vector3.Zero, 4),
             new("explicit endfield", 6, 6, new(.5f, .4f, .3f, 1), Vector3.Zero, Vector3.Zero, 4)],
            [new("numerical R gate", 1, 1, [255, 128, 255, 0]), new("genshin color", 1, 1, [60, 170, 110, 255]),
             new("packed RG", 1, 1, [128, 128, 0, 0]), new("numerical P", 1, 1, [255, 0, 0, 0]),
             new("endfield color", 1, 1, [180, 70, 120, 255])], camera: Camera, lightDirection: -Vector3.UnitZ);
        var frame = new OriginalFrameScene(new(scene, [new(new(.3f), DoubleSided: true), new(new(.3f), DoubleSided: true)],
            new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)), options: ModernOptions);
        var metallic = new D3D12GenshinMetallicBinding(0, 1, D3D12MatcapColorEncoding.SrgbRgb,
            new(.2f), new(.8f), Vector3.One, LitMode: D3D12GenshinMetallicLitMode.Uniform);
        var matcap = new D3D12EndfieldMatcapBinding(2, 3, 4, D3D12MatcapColorEncoding.SrgbRgb, 0,
            new(.4f), new(.1f), NormalStrength: 0, HighlightCoefficient: 1, LightControlDirection: -Vector3.UnitZ);
        D3D12SurfaceStyleMap Styles(D3D12GenshinMetallicBinding metal, D3D12EndfieldMatcapBinding cap)
            => new([D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.EndfieldPmx],
                [new(GenshinMetallic: metal), new(EndfieldMatcap: cap)]);
        using var session = new D3D12FrameSession();
        var both = session.Capture(frame, W, H, surfaceStyles: Styles(metallic, matcap));
        var genshinLinear = session.Capture(frame, W, H, surfaceStyles: Styles(
            metallic with { MatcapEncoding = D3D12MatcapColorEncoding.LinearRgb }, matcap));
        var endfieldLinear = session.Capture(frame, W, H, surfaceStyles: Styles(metallic,
            matcap with { MatcapEncoding = D3D12MatcapColorEncoding.LinearRgb }));
        int genshinChanged = 0, endfieldChanged = 0;
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
        {
            int at = (y * W + x) * 4;
            Assert.Equal(both.Rgba[at + 3], genshinLinear.Rgba[at + 3]);
            Assert.Equal(both.Rgba[at + 3], endfieldLinear.Rgba[at + 3]);
            if (x < W / 2)
            {
                Assert.Equal(both.Rgba.AsSpan(at, 4).ToArray(), endfieldLinear.Rgba.AsSpan(at, 4).ToArray());
                if (!both.Rgba.AsSpan(at, 3).SequenceEqual(genshinLinear.Rgba.AsSpan(at, 3))) genshinChanged++;
            }
            else
            {
                Assert.Equal(both.Rgba.AsSpan(at, 4).ToArray(), genshinLinear.Rgba.AsSpan(at, 4).ToArray());
                if (!both.Rgba.AsSpan(at, 3).SequenceEqual(endfieldLinear.Rgba.AsSpan(at, 3))) endfieldChanged++;
            }
        }
        Assert.True(genshinChanged > 100, $"Only {genshinChanged} Genshin pixels consumed their sRGB descriptor.");
        Assert.True(endfieldChanged > 100, $"Only {endfieldChanged} Endfield pixels consumed their sRGB descriptor.");
        Assert.Equal(both.Rgba, session.Capture(frame, W, H, surfaceStyles: Styles(metallic, matcap)).Rgba);
    }

    private static async Task<byte[]> Capture(SceneProjectSession project, D3D12ResolvedMaterialBindingProfile mapping, bool dedicated = true, bool withoutSpecular = false)
    {
        await using var renderer = new D3D12ViewRenderer(new(W, H), characterMaterials: mapping.CharacterMaterials,
            genshinMetallicDataBindings: dedicated ? withoutSpecular
                ? mapping.GenshinMetallicBindings.Select(v=>v with{Specular=null}) : mapping.GenshinMetallicBindings : []);
        Assert.False(renderer.IsWarp);
        var snapshot = project.CaptureRenderSnapshot(renderer.SceneInputRequests);
        // Composition has a legacy default. Resolve the explicitly modern fixture options before style validation.
        var modern = new OriginalFrameScene(snapshot.Scene.Surface, snapshot.Scene.Outline, snapshot.Scene.OutlineOptions,
            ModernOptions, snapshot.Scene.Materials, snapshot.Scene.Lighting);
        var plan = new OfflineFramePlan(W, H, new(30), 0, 0);
        var frame = new OfflineFrame(plan.GetSample(0), W, H, modern, null, null, null)
        { RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs, ResolvedCamera = Camera };
        using var image = await renderer.CaptureAsync(frame, Token);
        return image.Pixels.ToArray();
    }

    private static void AssertRestored(D3D12MaterialBindingProfile expected, D3D12MaterialBindingProfile actual,
        IReadOnlyList<SceneTexture> textures, OriginalFrameScene restored)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(D3D12MaterialBindingProfileJson.Encode(expected), D3D12MaterialBindingProfileJson.Encode(actual));
        var table = restored.Surface.Snapshot.Textures;
        Assert.Equal(textures.Count, table.Count);
        for (int index = 0; index < textures.Count; index++)
        {
            Assert.Equal(textures[index].Width, table[index].Width);
            Assert.Equal(textures[index].Height, table[index].Height);
            Assert.Equal(textures[index].Kind, table[index].Kind);
            Assert.Equal(textures[index].Rgba.ToArray(), table[index].Rgba.ToArray());
        }
    }

    private static void AssertVisibleChange(byte[] off, byte[] actual)
    {
        int changed = 0;
        for (int index = 0; index < off.Length; index += 4)
        {
            Assert.Equal(off[index + 3], actual[index + 3]);
            if (!off.AsSpan(index, 3).SequenceEqual(actual.AsSpan(index, 3))) changed++;
        }
        Assert.True(changed > 100, $"Only {changed} pixels consumed portable metallic data.");
    }

    // Independent 1x1 24-bit BMP; selected images are decoded from files, then deleted before reopening PMA.
    private static void WriteBmp(string path, byte r, byte g, byte b)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0x4d42); writer.Write(58); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(1); writer.Write(1); writer.Write((ushort)1); writer.Write((ushort)24);
        writer.Write(0); writer.Write(4); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        writer.Write(b); writer.Write(g); writer.Write(r); writer.Write((byte)0);
    }

    private static PmxModel Model()
    {
        var template = PmxReader.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
        Vector3[] positions = [new(-.8f, -.8f, 0), new(.8f, -.8f, 0), new(.8f, .8f, 0), new(-.8f, .8f, 0)];
        return new()
        {
            Header = template.Header, Name = "synthetic portable metallic", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = positions.Select((p, index) => new PmxVertex
            {
                Position = p, Normal = -Vector3.UnitZ, Uv = new(index is 1 or 2 ? 1 : 0, index >= 2 ? 1 : 0),
                AdditionalUvs = new Vector4[template.Header.AdditionalUvCount], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 0
            }).ToArray(), Faces = [new(0, 2, 1), new(0, 3, 2)], Textures = [], Materials = [new()
            {
                Name = "explicit receiver", EnglishName = "", Diffuse = new(.5f, .4f, .3f, 1), Specular = Vector3.Zero,
                SpecularPower = 4, Ambient = new(.1f), EdgeColor = Vector4.Zero, EdgeSize = 0,
                DrawMode = PmxDrawModeFlags.BothFaces, TextureIndex = -1, SphereTextureIndex = -1,
                SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Common, ToonTextureIndex = 0, Memo = "", FaceVertexCount = 6
            }], Bones = template.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
    }

    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, Camera.Eye, 30, 0),
        ModernOptions, PmxOriginalFrameAdapter.FromMaterials(model));

    // Deliberately neutral model loader: no source image paths, material profile or GPU-specific preparation.
    private static SceneAnimationModelAsset Load(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(path); return new(model, Rest(model)); }

    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings s, EditableSceneObject o) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings s) => throw new InvalidOperationException();
    }
}
