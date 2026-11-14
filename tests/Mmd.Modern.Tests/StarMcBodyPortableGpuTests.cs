using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class StarMcBodyPortableGpuTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly SceneCamera Camera = new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2.6f, .1f, 30);

    [Theory, Trait("Category", "Gpu")]
    [InlineData("star-rail", "genshin")]
    [InlineData("wuthering-waves", "genshin")]
    [InlineData("endfield", "genshin")]
    [InlineData("genshin", "endfield")]
    [InlineData("star-rail", "endfield")]
    [InlineData("wuthering-waves", "endfield")]
    [InlineData("genshin", "star-mc")]
    [InlineData("endfield", "star-mc")]
    public async Task PublicConstructorsKeepDormantTypedHairEquivalentToUnclassifiedBaseline(string pipeline, string dormant)
    {
        var scene = StarMcBodyViewProfileTests.Modern(StarMcBodyViewProfileTests.Source()); var actor = Guid.NewGuid();
        var objects = new[] { new SceneRenderingStyleBinding(actor, new(pipeline), ModelRole.Character, 0, 1, true) };
        D3D12ViewGenshinHairBinding[] genshin = dormant == "genshin" ? [new(actor, 0, 0)] : [];
        D3D12ViewStarMcHairBinding[] starMc = dormant == "star-mc" ? [new(actor, 0, 0, 1)] : [];
        D3D12ViewEndfieldHairBinding[] endfield = dormant == "endfield" ? [new(actor, 0, 999, new(.3f))] : [];
        var request = new ViewRenderRequest(96, 72);
        var plan = new OfflineFramePlan(96, 72, new(30), 0, 0);
        var frame = new OfflineFrame(plan.GetSample(0), 96, 72, scene, null, null, null)
        { RenderingStyles = objects, ResolvedCamera = Camera };
        byte[] expected;
        await using (var baseline = new D3D12ViewRenderer(request))
        {
            using var pixels = await baseline.CaptureAsync(frame, Token); Assert.False(baseline.IsWarp);
            expected = pixels.Pixels.ToArray();
        }
        await using (var configured = new D3D12ViewRenderer(request, hairDataBindings: genshin,
            starMcHairDataBindings: starMc, endfieldHairDataBindings: endfield))
        {
            configured.ValidateRenderingStyles(scene, objects);
            configured.ValidateSceneInputs(new PreviewFrame(96, 72, 0, scene, null, null, null)
            { RenderingStyles = objects, ResolvedCamera = Camera });
            using var pixels = await configured.CaptureAsync(frame, Token);
            Assert.Equal(expected, pixels.Pixels.ToArray());
        }
        nint window = CreateWindowExW(0, "STATIC", "Dormant material View present", 0x80000000, 0, 0, 120, 90, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            var previewFrame = new PreviewFrame(96, 72, 0, scene, null, null, null)
            { RenderingStyles = objects, ResolvedCamera = Camera };
            int expectedDraws;
            await using (var baseline = new D3D12RealtimeViewRenderer(window, request))
            {
                await baseline.PresentAsync(previewFrame, Token); Assert.False(baseline.IsWarp);
                expectedDraws = baseline.Statistics.Frame!.SceneDrawCalls;
            }
            await using var preview = new D3D12RealtimeViewRenderer(window, request, hairDataBindings: genshin,
                starMcHairDataBindings: starMc, endfieldHairDataBindings: endfield);
            preview.ValidateRenderingStyles(scene, objects);
            preview.ValidateSceneInputs(previewFrame);
            await preview.PresentAsync(previewFrame, Token);
            await preview.PresentAsync(previewFrame, Token);
            Assert.Equal(expectedDraws, preview.Statistics.Frame!.SceneDrawCalls);
            Assert.Equal(2, preview.Statistics.PresentCount);
            Assert.Equal(0, preview.Statistics.ExplicitCaptureCount);
            Assert.Equal(0, preview.Statistics.Frame.TotalReadbacks);
        }
        finally { DestroyWindow(window); }
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    public async Task PublicCaptureAndPresentRejectMissingInputsAndCancellationWithoutAdvancingHistory(string pipeline)
    {
        var source = StarMcBodyViewProfileTests.Source(); var editor = new SceneCompositionEditor();
        var item = editor.Add("Body history", Path.Combine(Path.GetTempPath(), "body-history.pmx"), ModelRole.Character, source);
        using var project = new SceneProjectSession(editor, new(), [], new StarMcBodyViewProfileTests.NoPhysics(), cancellationToken: Token);
        project.SetDefaultRenderingStyle(new(pipeline), Token);
        var mapping = D3D12MaterialBindingProfileResolver.Resolve(StarMcBodyViewProfileTests.Profile(source, pipeline: pipeline), source, item.Id, pipeline);
        var request = new ViewRenderRequest(96, 72, new(Gi: ViewGiTemporalAdapterTests.Options));
        await using var renderer = new D3D12ViewRenderer(request, characterMaterials: mapping.CharacterMaterials,
            starMcBodyDataBindings: mapping.StarMcBodyBindings);
        Assert.False(renderer.IsWarp);
        var capture = project.CaptureRenderSnapshot(renderer.SceneInputRequests);
        var scene = StarMcBodyViewProfileTests.Modern(capture.Scene);
        var plan = new OfflineFramePlan(96, 72, new(30), 0, 10);
        OfflineFrame Frame(int index) => new(plan.GetSample(index), 96, 72, scene, null, null, null)
        { RenderingStyles = capture.RenderingStyles, SceneInputs = capture.SceneInputs, ResolvedCamera = Camera };
        using (await renderer.CaptureAsync(Frame(0), Token)) { }
        Assert.Equal(1UL, renderer.RayGiStatistics!.TemporalHistoryFrames);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        { using var ignored = await renderer.CaptureAsync(Frame(1) with { SceneInputs = SceneObjectRenderInputs.Empty }, Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { using var ignored = await renderer.CaptureAsync(Frame(1), new CancellationToken(true)); });
        using (await renderer.CaptureAsync(Frame(1), Token)) { }
        Assert.Equal(2UL, renderer.RayGiStatistics!.TemporalHistoryFrames);
        Assert.False(renderer.RayGiStatistics.TemporalResetHistory);

        nint window = CreateWindowExW(0, "STATIC", "Body View present", 0x80000000, 0, 0, 120, 90, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            await using var preview = new D3D12RealtimeViewRenderer(window, request, characterMaterials: mapping.CharacterMaterials,
                starMcBodyDataBindings: mapping.StarMcBodyBindings);
            var frame = new PreviewFrame(96, 72, 0, scene, null, null, null)
            { RenderingStyles = capture.RenderingStyles, SceneInputs = capture.SceneInputs, ResolvedCamera = Camera };
            await preview.PresentAsync(frame, Token);
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await preview.PresentAsync(frame with { SceneInputs = SceneObjectRenderInputs.Empty }, Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await preview.PresentAsync(frame, new CancellationToken(true)));
            await preview.PresentAsync(frame, Token);
            Assert.Equal(2, preview.Statistics.PresentCount);
            Assert.Equal(2UL, preview.Statistics.RayGi!.TemporalHistoryFrames);
            Assert.Equal(0, preview.Statistics.Frame!.TotalReadbacks);
            Assert.Equal(0, preview.Statistics.ExplicitCaptureCount);
        }
        finally { DestroyWindow(window); }
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public async Task CurrentViewAndMovedPmaRestoreExactRgbaAfterOriginalRemovalAndDefaultResave(string pipeline)
    {
        string directory = Path.Combine(Path.GetTempPath(), "body-view-pma-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string original = Path.Combine(directory, "source.pmx"); var model = Model();
            File.WriteAllBytes(original, PmxWriter.ToBytes(model));
            var rest = Rest(model); var snapshot = rest.Surface.Snapshot; int offset = snapshot.Textures.Count;
            var textures = snapshot.Textures.Concat(StarMcBodyViewProfileTests.Source().Surface.Snapshot.Textures)
                .Append(new SceneTexture("unused portable image", 1, 1, [3, 8, 19, 37])).ToArray();
            var prepared = new SceneSnapshot(snapshot.Vertices, snapshot.Indices, snapshot.Materials, textures,
                snapshot.World, snapshot.Camera, snapshot.LightDirection, snapshot.AdditionalUvs, snapshot.RawRadialSeeds);
            var source = new OriginalFrameScene(new(prepared, rest.Surface.Materials, rest.Surface.Profile), rest.Outline,
                rest.OutlineOptions, rest.Options, rest.Materials, rest.Lighting);
            var editor = new SceneCompositionEditor(); var item = editor.Add("portable Body", original, ModelRole.Character, source);
            using var project = new SceneProjectSession(editor, new SceneLightingEditor(), [], new StarMcBodyViewProfileTests.NoPhysics(), cancellationToken: Token);
            project.SetDefaultRenderingStyle(new(pipeline), Token);
            var profile = pipeline == "endfield" ? Endfield(source, offset) : StarMcBodyViewProfileTests.Profile(source, offset, pipeline);
            var mapping = D3D12MaterialBindingProfileResolver.Resolve(profile, source, item.Id, pipeline);
            byte[] expected = await Capture(project, mapping);
            byte[] baseline = await Capture(project, mapping, dedicated: false);
            int changed = 0;
            for (int at = 0; at < expected.Length; at += 4)
            {
                Assert.Equal(expected[at + 3], baseline[at + 3]);
                if (!expected.AsSpan(at, 3).SequenceEqual(baseline.AsSpan(at, 3))) changed++;
            }
            Assert.True(changed > 100, $"Only {changed} pixels consumed {pipeline} portable Body data.");
            string first = Path.Combine(directory, "first.pma");
            PmaProjectStore.Save(first, project, D3D12MaterialBindingPma.CreateSaveOptions(project, [new(item.Id, profile, source)]), Token);
            string movedDirectory = Path.Combine(directory, "moved"); Directory.CreateDirectory(movedDirectory);
            string moved = Path.Combine(movedDirectory, "moved.pma"); File.Move(first, moved); File.Delete(original);
            string resaved = Path.Combine(movedDirectory, "resaved.pma");
            using (var loaded = D3D12MaterialBindingPma.Load(moved, Path.Combine(directory, "cache"), Load,
                new StarMcBodyViewProfileTests.NoPhysics(), out var profiles, cancellationToken: Token))
            {
                var restored = Assert.Single(loaded.Session.Objects);
                Assert.Equal(textures.Length, restored.Source.Surface.Snapshot.Textures.Count);
                for (int i = 0; i < textures.Length; i++)
                    Assert.Equal(textures[i].Rgba.ToArray(), restored.Source.Surface.Snapshot.Textures[i].Rgba.ToArray());
                Assert.Equal(D3D12MaterialBindingProfileJson.Encode(profile), D3D12MaterialBindingProfileJson.Encode(profiles[item.Id]));
                Assert.Equal(expected, await Capture(loaded.Session, D3D12MaterialBindingProfileResolver.Resolve(profiles[item.Id], restored.Source, item.Id, pipeline)));
                PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
            }
            using var reopened = D3D12MaterialBindingPma.Load(resaved, Path.Combine(directory, "cache2"), Load,
                new StarMcBodyViewProfileTests.NoPhysics(), out var savedProfiles, cancellationToken: Token);
            var again = Assert.Single(reopened.Session.Objects);
            Assert.Equal(expected, await Capture(reopened.Session, D3D12MaterialBindingProfileResolver.Resolve(savedProfiles[item.Id], again.Source, item.Id, pipeline)));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static D3D12MaterialBindingProfile Endfield(OriginalFrameScene source, int offset)
    {
        var layout = D3D12MaterialBindingLayout.Capture(source);
        return new("endfield-live-portable", "Endfield live portable", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Body, EndfieldMatcap: new(layout.CaptureTexture(offset),
                layout.CaptureTexture(offset + 2), layout.CaptureTexture(offset + 3), D3D12MatcapColorEncoding.SrgbRgb,
                0, new(.2f), new(.4f, .2f, .3f), NormalStrength: 1.1f, HighlightCoefficient: .2f, CoupleEndfieldPalette: true,
                Palette: new(new(.3f), LightInfluence: .6f, Density: .3f, ReceivedLightInputMode: D3D12EndfieldPaletteInputMode.RendererDiffuse),
                HighlightRange: new(.7f, 1.2f, new(-Vector3.UnitZ, .4f, .8f))))]);
    }

    private static async Task<byte[]> Capture(SceneProjectSession project, D3D12ResolvedMaterialBindingProfile mapping, bool dedicated = true)
    {
        await using var renderer = new D3D12ViewRenderer(new(97, 97), characterMaterials: mapping.CharacterMaterials,
            starMcBodyDataBindings: dedicated ? mapping.StarMcBodyBindings : [],
            endfieldMatcapDataBindings: dedicated ? mapping.EndfieldMatcapBindings : []);
        Assert.False(renderer.IsWarp);
        var capture = project.CaptureRenderSnapshot(renderer.SceneInputRequests);
        var plan = new OfflineFramePlan(97, 97, new(30), 0, 0);
        var frame = new OfflineFrame(plan.GetSample(0), 97, 97, StarMcBodyViewProfileTests.Modern(capture.Scene), null, null, null)
        { RenderingStyles = capture.RenderingStyles, SceneInputs = capture.SceneInputs, ResolvedCamera = Camera };
        using var result = await renderer.CaptureAsync(frame, Token);
        return result.Pixels.ToArray();
    }

    private static PmxModel Model()
    {
        var template = PmxReader.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
        Vector3[] positions = [new(-.8f, -.8f, 0), new(.8f, -.8f, 0), new(.8f, .8f, 0), new(-.8f, .8f, 0)];
        return new()
        {
            Header = template.Header, Name = "portable Body fixture", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = positions.Select((p, i) => new PmxVertex
            {
                Position = p, Normal = -Vector3.UnitZ, Uv = new(i is 1 or 2 ? 1 : 0, i >= 2 ? 1 : 0),
                AdditionalUvs = new Vector4[template.Header.AdditionalUvCount], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 0
            }).ToArray(), Faces = [new(0, 2, 1), new(0, 3, 2)], Textures = [], Materials = [new()
            {
                Name = "explicit Body", EnglishName = "", Diffuse = new(.5f, .4f, .3f, 1), Specular = Vector3.Zero,
                SpecularPower = 4, Ambient = new(.1f), EdgeColor = Vector4.Zero, EdgeSize = 0, DrawMode = PmxDrawModeFlags.BothFaces,
                TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
                ToonMode = PmxToonMode.Common, ToonTextureIndex = 0, Memo = "", FaceVertexCount = 6
            }], Bones = template.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
    }

    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, Camera.Eye, 30, 0),
        new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero), PmxOriginalFrameAdapter.FromMaterials(model));
    private static SceneAnimationModelAsset Load(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(path); return new(model, Rest(model)); }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extended, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
