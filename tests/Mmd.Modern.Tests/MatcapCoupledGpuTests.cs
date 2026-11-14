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
public sealed class MatcapCoupledGpuTests
{
    private const int W = 97, H = 97;
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly SceneCamera Camera = new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2.6f, .1f, 30);
    private static readonly Vector3 Base = new(.085f, .07f, .06f);
    private static readonly D3D12EndfieldMatcapBinding Matcap = new(0, 1, 2,
        D3D12MatcapColorEncoding.LinearRgb, 0, Base, new(.2f, .45f, .12f),
        NormalStrength: 1.7f, HighlightCoefficient: .1f, LightControlDirection: Vector3.UnitZ,
        BaselineStrength: 1, CoupleEndfieldPalette: true);
    // Normal incidence yields Fresnel=1/9; EdgeSize=4 saturates the independent
    // smoothstep to one, so the actual rim scalar is exactly EdgeStrength.
    private static readonly D3D12EndfieldRimBinding Rim = new(Base, new(.8f, .7f, .6f),
        EdgeSize: 4, EdgeStrength: .18f, RangeStrength: 1);
    private static readonly D3D12EndfieldHairBinding Hair = new(3, Base, .3f);

    [Theory, InlineData(true, false), InlineData(false, true), InlineData(true, true)]
    [Trait("Category", "Gpu")]
    public void NonwhitePackedNormalMatcapIncludesActualRimAndRadialCrossTerms(bool rim, bool hair)
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(straight: true);
        var baseline = session.Capture(frame, W, H, surfaceStyles: Styles(null, rim, hair));
        var actual = session.Capture(frame, W, H, surfaceStyles: Styles(Matcap, rim, hair));
        var changed = ChangedColor(frame.Surface.Snapshot, Matcap);
        float r = rim ? Rim.EdgeStrength : 0;
        // Planar N=+Z, seed=+Y and orthographic incoming=-Z give B=+X,
        // hence q=1 and min(q,.75)=.75, independently of the shader helper.
        float p = hair ? .75f * (173f / 255) * (Hair.Brightness + 1.5001001358032227f) : 0;
        var expected = MatcapOracle.Delta(Base, changed, 1, r, p, rim ? Rim.RimColor : null);
        var uncoupled = MatcapOracle.Delta(Base, changed, 1);
        Assert.True(expected.Length() * 255 > 6, "The coupled fixture must exercise visible MatCap output.");
        Assert.True(Vector3.Distance(expected, uncoupled) * 255 > 2,
            "The fixture must reject a plain additive MatCap delta that omits the coupled cross terms.");
        foreach (int y in new[] { 40, 48, 56 }) foreach (int x in new[] { 40, 48, 56 })
            AssertPixel(baseline, actual, x, y, expected);
        AssertAlpha(baseline, actual);
        Assert.Equal(baseline.Rgba, session.Capture(frame, W, H, surfaceStyles: Styles(null, rim, hair)).Rgba);
    }

    [Fact, Trait("Category", "Gpu")]
    public void CoupledStraightOutputSurvivesSsaaSmaaAndWarmRenderHasNoReadbackOrPipelineGrowth()
    {
        using var session = new D3D12FrameSession();
        var source = Frame(alpha: .43f, straight: true);
        var frame = new OriginalFrameScene(source.Surface, options: source.Options! with
        { SpatialSamples = 2, Antialiasing = FrameAntialiasing.Smaa1X, SmaaQuality = SmaaQuality.High });
        var baseline = session.Capture(frame, W, H, surfaceStyles: Styles(null, true, true));
        var actual = session.Capture(frame, W, H, surfaceStyles: Styles(Matcap, true, true));
        AssertAlpha(baseline, actual);
        Assert.InRange(actual.Rgba[(48 * W + 48) * 4 + 3], 108, 111);
        float p = .75f * (173f / 255) * (Hair.Brightness + 1.5001001358032227f);
        AssertPixel(baseline, actual, 48, 48,
            MatcapOracle.Delta(Base, ChangedColor(frame.Surface.Snapshot, Matcap), 1, Rim.EdgeStrength, p, Rim.RimColor), 2.1);
        using (var warmup = session.Render(frame, W, H, surfaceStyles: Styles(Matcap, true, true))) { }
        var warm = session.Statistics!; long reads = session.Runtime.Readbacks;
        for (int index = 0; index < 5; index++)
        {
            var edited = Matcap with { RotationRadians = index * .3f, HighlightCoefficient = .05f * index };
            using var image = session.Render(frame, W, H, surfaceStyles: Styles(edited, true, true));
            Assert.Equal(warm.ShaderCompilations, image.Statistics.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations, image.Statistics.PipelineCreations);
            Assert.InRange(image.Statistics.CachedResources, 0, warm.CachedResources + 1);
        }
        Assert.Equal(reads, session.Runtime.Readbacks);
        Assert.Equal(baseline.Rgba, session.Capture(frame, W, H, surfaceStyles: Styles(null, true, true)).Rgba);
    }

    [Fact, Trait("Category", "Gpu")]
    public void HighResolutionFinalDiffusionKeepsCoupledStraightSilhouetteAndOffRestoresExactly()
    {
        using var session=new D3D12FrameSession();
        var source=Frame(alpha:.43f,straight:true);
        var frame=new OriginalFrameScene(source.Surface,options:source.Options! with
            { SpatialSamples=2,Antialiasing=FrameAntialiasing.Smaa1X });
        var styles=Styles(Matcap,true,true);
        var original=session.Capture(frame,1440,900,surfaceStyles:styles);
        var filtered=session.Capture(frame,1440,900,surfaceStyles:styles,diffusion:new(true,.7f,.002f));
        AssertAlpha(original,filtered);
        int changed=0;
        for(int index=0;index<filtered.Rgba.Length;index+=4)
        {
            if(filtered.Rgba[index+3]==0)
                Assert.Equal(new byte[]{0,0,0},filtered.Rgba.AsSpan(index,3).ToArray());
            else if(!original.Rgba.AsSpan(index,3).SequenceEqual(filtered.Rgba.AsSpan(index,3)))changed++;
        }
        Assert.True(changed>100,"The final filter must visibly consume the coupled scene, not bypass it.");
        Assert.Equal(original.Rgba,session.Capture(frame,1440,900,surfaceStyles:styles,diffusion:D3D12DiffusionOptions.Off).Rgba);
    }

    [Fact, Trait("Category", "Gpu")]
    public void SubmittedMatcapEditsResetBothHistoriesWhileAbortedEditsPreserveTheCommittedPalette()
    {
        using var session = new D3D12FrameSession();
        var source = Frame();
        var perspective=source.Surface.Snapshot.WithCamera(Camera with { VerticalFovDegrees=30 });
        var frame = new OriginalFrameScene(new(perspective,source.Surface.Materials,source.Surface.Profile),
            options: source.Options! with { BackgroundColor = Vector4.One });
        string loader = Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
            ?? throw new InvalidOperationException("Real FSR loader required for NativeAA history acceptance.");
        var temporal = new D3D12TemporalUpscalingOptions { Mode = D3D12TemporalUpscalingMode.Fsr,
            QualityPreset = D3D12FsrQualityPreset.NativeAa, LoaderPath = loader };
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4, Seed: 31,
            Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial);
        var original = Styles(Matcap, true, true);
        var edited = Styles(Matcap with { HighlightColor = new(.6f, .1f, .3f), RotationRadians = .8f }, true, true);
        long reads = session.Runtime.Readbacks;
        Render(original, 1, "FirstFrame", "FirstFrame");
        Render(Styles(Matcap with { }, true, true), 2, "None", "None");
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(frame, 96, 72, gi,
            (_, _) => throw new OperationCanceledException("Abort unsubmitted coupled MatCap palette edit."),
            temporalUpscaling: temporal, surfaceStyles: edited));
        Assert.False(session.Runtime.IsRecording);
        Render(original, 3, "None", "AbortedFrame");
        Render(edited, 1, "ExplicitReset", "ExplicitRequest");
        Render(edited, 2, "None", "None");
        Render(Styles(null, true, true), 1, "ExplicitReset", "ExplicitRequest");
        Assert.Equal(reads, session.Runtime.Readbacks);

        void Render(D3D12SurfaceStyleMap styles, ulong count, string giReason, string aaReason)
        {
            using var image = session.Render(frame, 96, 72, gi, temporalUpscaling: temporal, surfaceStyles: styles);
            Assert.Equal(count, session.RayGiStatistics!.TemporalHistoryFrames);
            Assert.Equal(giReason, session.RayGiStatistics.TemporalResetReason);
            Assert.Equal(aaReason, image.Statistics.TemporalUpscaling!.ResetReason);
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task SameCaptureGeometryAndPortableImagesRebuildTheSameVisibleGpuResultAfterMoveAndDefaultResave()
    {
        string directory = Path.Combine(Path.GetTempPath(), "matcap-gpu-pma-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "source.pmx");
            var model = Model(); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
            var rest = Rest(model); var original = rest.Surface.Snapshot; int start = original.Textures.Count;
            var snapshot = new SceneSnapshot(original.Vertices, original.Indices, original.Materials,
                [.. original.Textures, .. Textures().Take(3)], original.World, original.Camera, original.LightDirection,
                additionalUvs: original.AdditionalUvs);
            var source = new OriginalFrameScene(new(snapshot, rest.Surface.Materials, rest.Surface.Profile),
                rest.Outline, rest.OutlineOptions, rest.Options, rest.Materials);
            var editor = new SceneCompositionEditor(); var item = editor.Add("explicit portable MatCap", path, ModelRole.Character, source);
            using var project = new SceneProjectSession(editor, new SceneLightingEditor(), [], new NoPhysics(), cancellationToken: Token);
            project.SetDefaultRenderingStyle(new("endfield"), Token);
            var layout = D3D12MaterialBindingLayout.Capture(source);
            var profile = new D3D12MaterialBindingProfile("matcap-gpu", "Portable packed normal MatCap", layout.Fingerprint,
                [new(0, D3D12CharacterMaterialRole.Body, EndfieldMatcap: new(layout.CaptureTexture(start),
                    layout.CaptureTexture(start + 1), layout.CaptureTexture(start + 2), D3D12MatcapColorEncoding.LinearRgb,
                    0, Base, Matcap.HighlightColor, NormalStrength: 1.7f, HighlightCoefficient: .1f,
                    LightControlDirection: -Vector3.UnitZ))]);
            var before = D3D12MaterialBindingProfileResolver.Resolve(profile, source, item.Id, "endfield");
            byte[] expected = await Capture(project, before);
            byte[] off = await Capture(project, before, dedicated: false);
            int changed = 0;
            for (int index = 0; index < off.Length; index += 4)
            {
                Assert.Equal(off[index + 3], expected[index + 3]);
                if (!off.AsSpan(index, 3).SequenceEqual(expected.AsSpan(index, 3))) changed++;
            }
            Assert.True(changed > 100, $"Only {changed} pixels consumed portable MatCap data.");
            // Persisting a dedicated mapping must never activate it in the default MMD pipeline.
            project.SetDefaultRenderingStyle(new("mmd"), Token);
            Assert.Equal(await Capture(project, before, dedicated: false), await Capture(project, before));
            project.SetDefaultRenderingStyle(new("endfield"), Token);
            string first = Path.Combine(directory, "first.pma");
            PmaProjectStore.Save(first, project, D3D12MaterialBindingPma.CreateSaveOptions(project,
                [new(item.Id, profile, source)]), Token);
            string moved = Path.Combine(directory, "moved.pma"); File.Move(first, moved); File.Delete(path);
            string saved = Path.Combine(directory, "saved.pma");
            using (var loaded = D3D12MaterialBindingPma.Load(moved, Path.Combine(directory, "cache"), Load,
                new NoPhysics(), out var profiles, cancellationToken: Token))
            {
                var value = loaded.Session.Objects.Single();
                var mapping = D3D12MaterialBindingProfileResolver.Resolve(profiles[value.Id], value.Source, value.Id, "endfield");
                Assert.Equal(expected, await Capture(loaded.Session, mapping));
                PmaProjectStore.Save(saved, loaded.Session, cancellationToken: Token);
            }
            using var again = D3D12MaterialBindingPma.Load(saved, Path.Combine(directory, "cache2"), Load,
                new NoPhysics(), out var savedProfiles, cancellationToken: Token);
            var restored = again.Session.Objects.Single();
            var resaved = D3D12MaterialBindingProfileResolver.Resolve(savedProfiles[restored.Id], restored.Source, restored.Id, "endfield");
            Assert.Equal(expected, await Capture(again.Session, resaved));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task<byte[]> Capture(SceneProjectSession project, D3D12ResolvedMaterialBindingProfile mapping, bool dedicated = true)
    {
        await using var renderer = new D3D12ViewRenderer(new(W, H), characterMaterials: mapping.CharacterMaterials,
            endfieldMatcapDataBindings: dedicated ? mapping.EndfieldMatcapBindings : []);
        Assert.False(renderer.IsWarp);
        var snapshot = project.CaptureRenderSnapshot(renderer.SceneInputRequests);
        if (dedicated)
        {
            var id = Assert.Single(mapping.EndfieldMatcapBindings).ObjectId;
            Assert.NotNull(snapshot.SceneInputs[id].SourceGeometry);
        }
        var plan = new OfflineFramePlan(W, H, new(30), 0, 0);
        var frame = new OfflineFrame(plan.GetSample(0), W, H, snapshot.Scene, null, null, null)
        { RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs, ResolvedCamera = Camera };
        using var image = await renderer.CaptureAsync(frame, Token);
        return image.Pixels.ToArray();
    }

    private static D3D12SurfaceStyleMap Styles(D3D12EndfieldMatcapBinding? matcap, bool rim, bool hair)
        => new([D3D12SurfaceStyle.EndfieldPmx], [new(hair ? D3D12CharacterMaterialRole.Hair : D3D12CharacterMaterialRole.Body,
            EndfieldMatcap: matcap, EndfieldRim: rim ? Rim : null, EndfieldHair: hair ? Hair : null)]);

    private static SceneTexture[] Textures() => [new("packed RG", 1, 1, [179, 153, 0, 0]),
        new("P gate", 1, 1, [40, 0, 0, 24]), new("nonwhite MatCap", 1, 1, [40, 180, 70, 180]),
        new("radial Green", 1, 1, [0, 173, 0, 0])];

    private static OriginalFrameScene Frame(float alpha = 1, bool straight = false)
    {
        Vector3[] points = [new(-.8f, -.8f, 0), new(.8f, -.8f, 0), new(.8f, .8f, 0), new(-.8f, .8f, 0)];
        var scene = new SceneSnapshot(points.Select(p => new SceneVertex(p, Vector3.UnitZ,
                new((p.X / .8f + 1) * .5f, (p.Y / .8f + 1) * .5f))).ToArray(), [0, 2, 1, 0, 3, 2],
            [new("explicit coupled receiver", 0, 6, new(.2f, .18f, .16f, alpha), Vector3.Zero, new(.02f), 4)],
            Textures(), camera: Camera, lightDirection: -Vector3.UnitZ,
            rawRadialSeeds: new(Enumerable.Repeat(Vector3.UnitY, 4).ToArray()));
        return new(new(scene, [new(new(.2f), DoubleSided: true)],
            new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero,
                AlphaOutput: straight ? FrameAlphaOutput.Straight : FrameAlphaOutput.LegacyMax));
    }

    private static Vector3 ChangedColor(SceneSnapshot scene, D3D12EndfieldMatcapBinding binding)
    {
        var vp = scene.World * scene.Camera.ViewProjection(W, H);
        Vector2 Project(int index)
        {
            var p = Vector4.Transform(new Vector4(scene.Vertices[(int)scene.Indices[index]].Position, 1), vp);
            return new(p.X / p.W, -p.Y / p.W);
        }
        var a = Project(0); var b = Project(1); var c = Project(2);
        bool front = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X) < 0;
        Vector3 mapped = MatcapOracle.PackedNormal(new(179f / 255, 153f / 255), Vector3.UnitZ,
            new(Vector3.UnitX, -1), 1, front, binding.NormalStrength);
        var gate = MatcapOracle.SourceGate(mapped, binding.LightControlDirection ?? Vector3.UnitZ,
            binding.ShadowHardness, 40f / 255, 24f / 255, binding.HighlightCoefficient).Gate;
        return MatcapOracle.MaterialColor(binding.BaseColor, binding.HighlightColor,
            new Vector4(40, 180, 70, 180) / 255, gate);
    }

    private static void AssertPixel(RenderedFrame before, RenderedFrame after, int x, int y, Vector3 delta, double tolerance = 1.6)
    {
        int index = (y * W + x) * 4;
        Assert.True(before.Rgba[index + 3] > 0);
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(Math.Abs(after.Rgba[index + channel] - Math.Clamp(before.Rgba[index + channel] + 255 * delta[channel], 0, 255)), 0, tolerance);
    }
    private static void AssertAlpha(RenderedFrame before, RenderedFrame after)
    { for (int index = 3; index < before.Rgba.Length; index += 4) Assert.Equal(before.Rgba[index], after.Rgba[index]); }

    private static PmxModel Model()
    {
        var template = PmxReader.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
        Vector3[] positions = [new(-.8f, -.8f, 0), new(.8f, -.8f, 0), new(.8f, .8f, 0), new(-.8f, .8f, 0)];
        return new()
        {
            Header = template.Header, Name = "portable MatCap GPU", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = positions.Select((p, index) => new PmxVertex
            {
                Position = p, Normal = -Vector3.UnitZ, Uv = new(index is 1 or 2 ? 1 : 0, index >= 2 ? 1 : 0),
                AdditionalUvs = new Vector4[template.Header.AdditionalUvCount], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 0
            }).ToArray(),
            Faces = [new(0, 2, 1), new(0, 3, 2)], Textures = [], Materials = [new()
            {
                Name = "explicit receiver", EnglishName = "", Diffuse = new(.2f, .18f, .16f, 1), Specular = Vector3.Zero,
                SpecularPower = 4, Ambient = new(.02f), EdgeColor = Vector4.Zero, EdgeSize = 0,
                DrawMode = PmxDrawModeFlags.BothFaces, TextureIndex = -1, SphereTextureIndex = -1,
                SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Common, ToonTextureIndex = 0, Memo = "", FaceVertexCount = 6
            }], Bones = template.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
    }
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, Camera.Eye, 30, 0),
        options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static SceneAnimationModelAsset Load(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(path); return new(model, Rest(model)); }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }
}
