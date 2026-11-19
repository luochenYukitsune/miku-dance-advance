using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Original numerical fixtures for the independently implemented packed shape.
/// No reference mask or claim of a complete Endfield palette/eye shader is included.</summary>
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12EndfieldFaceSdfTests
{
    private const int Width = 129, Height = 97;
    private static readonly byte[] Primary = [64, 204, 128, 0];
    private static readonly byte[] Secondary = [128, 26, 13, 0];
    private static readonly byte[] Mirrored = [192, 128, 32, 255];

    [Fact]
    public void EncodingIsExplicitAndExistingGrayDefaultsRemainCompatible()
    {
        var old = TestStylePlugins.FaceSdf("star-rail", 0);
        Assert.Equal("GrayRed", old.StringValues!["encoding"]);
        Assert.False(old.TextureSlots!.ContainsKey("secondary"));
        Assert.True(TestStylePlugins.StarRail.ValidateMaterial(new(Guid.Empty, 0, null, StyleMaterialRole.Face, [old], 1)).IsValid);
        Assert.True(T8dTestHelpers.ValidateEndfield(Binding(), StyleMaterialRole.Face, 2).IsValid);
        var map = Map(Binding());
        map.Validate(Frame());
        Assert.Equal("face-sdf", map.CharacterMaterials[0].PluginDraw?.ShaderModuleId);
        Assert.False(T8dTestHelpers.ValidateEndfield(T8dTestHelpers.EndfieldFaceSdf(0, -1), StyleMaterialRole.Face, 2).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield(T8dTestHelpers.EndfieldFaceSdf(0, 1, encoding: "Invalid"), StyleMaterialRole.Face, 2).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield(Binding(), StyleMaterialRole.Body, 2).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield(Binding(), StyleMaterialRole.Eye, 2).IsValid);
        // Both numerical inputs may deliberately share one compatible image.
        Map(T8dTestHelpers.EndfieldFaceSdf(0, 0, .1f, 1)).Validate(Frame());
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    public void PackedEncodingCannotBeMisinterpretedByAnotherFamily(string family)
    {
        if (family == "mmd")
        {
            var faceMap = Map(Binding());
            Assert.Throws<NotSupportedException>(() => new D3D12SurfaceStyleMap(["mmd", "mmd"],
                [faceMap.CharacterMaterials[0], new()], TestStylePlugins.AllPresetCatalog).Validate(Frame()));
        }
        else
        {
            var otherStyle = TestStylePlugins.GetStyle(family);
            var endfieldRgb = TestStylePlugins.FaceSdf(family, 0, 1, "EndfieldRgbCmM", .1f, 1);
            Assert.False(otherStyle.ValidateMaterial(new(Guid.Empty, 0, null, StyleMaterialRole.Face, [endfieldRgb], 2)).IsValid);
            Assert.False(T8dTestHelpers.ValidateEndfield(TestStylePlugins.FaceSdf("endfield", 0, -1, "GrayRed", .1f, 1), StyleMaterialRole.Face, 2).IsValid);
        }
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(2, 1)]
    [InlineData(0, -1)]
    [InlineData(0, 2)]
    public void BothCombinedIndicesMustExistBeforeRecording(int primary, int secondary)
        => Assert.False(T8dTestHelpers.ValidateEndfield(
            T8dTestHelpers.EndfieldFaceSdf(primary, secondary, .1f, 1), StyleMaterialRole.Face, 2).IsValid);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    public void EveryConsumedPackedChannelParticipatesInDetachedHistory(int texture, int channel)
    {
        var map = Map(Binding());
        var first = map.CaptureResolvedInputs(Frame());
        byte[] sdf = [.. Primary], cm = [.. Secondary];
        (texture == 0 ? sdf : cm)[channel]++;
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(sdf, cm))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame())));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    public void UnusedPackedChannelsDoNotChangeGpuOutputAndZeroStrengthIgnoresHistory(int texture, int channel)
    {
        var map = Map(Binding());
        var disabled = Map(Binding(), 0);
        byte[] sdf = [.. Primary], cm = [.. Secondary];
        (texture == 0 ? sdf : cm)[channel]++;
        Assert.True(D3D12SurfaceStyleMap.Equivalent(disabled.CaptureResolvedInputs(Frame()),
            disabled.CaptureResolvedInputs(Frame(sdf, cm))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(map.CaptureResolvedInputs(Frame()),
            map.CaptureResolvedInputs(new(new(TestStylePlugins.WithTextures(Frame().Surface.Snapshot,
                [new("renamed sdf", 4, 1, Frame().Surface.Snapshot.Textures[0].Rgba),
                 new("renamed cm", 4, 1, Frame().Surface.Snapshot.Textures[1].Rgba)]),
                Frame().Surface.Materials, Frame().Surface.Profile), options: Frame().Options))));
    }

    [Fact]
    public void CapturedEqualityUsesDataRatherThanRebasedIndicesAndZeroStrengthIgnoresData()
    {
        var frame = Frame(); var map = Map(Binding());
        var first = map.CaptureResolvedInputs(frame);
        var snapshot = frame.Surface.Snapshot;
        var reversed = new SceneSnapshot(snapshot.Vertices, snapshot.Indices, snapshot.Materials,
            snapshot.Textures.Reverse(), world: snapshot.World, camera: snapshot.Camera, lightDirection: snapshot.LightDirection);
        var rebased = new OriginalFrameScene(new(reversed, frame.Surface.Materials, frame.Surface.Profile), options: frame.Options);
        var other = Map(T8dTestHelpers.EndfieldFaceSdf(1, 0, .1f, 1), frame: rebased);
        other.Validate(rebased);
        Assert.False(D3D12SurfaceStyleMap.Equivalent(map, other));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, other.CaptureResolvedInputs(rebased)));
        var disabled = Map(Binding(), 0);
        Assert.Equal("default", disabled.CharacterMaterials[0].PluginDraw?.ShaderModuleId);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(disabled, Map(null, 0)));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(disabled, Map(T8dTestHelpers.EndfieldFaceSdf(0, 1, .1f, -1), 0)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Map(T8dTestHelpers.EndfieldFaceSdf(0, 1, .2f, 1)).CaptureResolvedInputs(frame)));
    }

    public static IEnumerable<object[]> Variants() => T8dTestHelpers.SurfaceVariants().Select(v => v[1..]);

    [Theory, MemberData(nameof(Variants))]
    public void EveryPackedShaderVariantCompilesWithSharedLightConsumersAndUnchangedCoverage(bool native, bool gi, bool morph, bool sub)
    {
        string entry = native ? gi ? "PSSurfaceGiNative" : "PSSurfaceNative" : gi ? "PSSurfaceGi" : "PSSurface";
        var shader = T8dTestHelpers.CompileEndfieldPixel("face-sdf", entry, native, gi, morph, sub);
        Assert.Equal(new byte[] { 68, 88, 66, 67 }, shader[..4]);
        string source = T8dTestHelpers.AssembleEndfieldPixelSource("face-sdf", entry, native, gi, morph, sub);
        Assert.Contains("cbuffer PluginStyleConstants : register(b0, space2)", source);
        Assert.Contains("Texture2D<float4> FaceSdfMap : register(t0, space2);", source);
        Assert.Contains("Texture2D<float4> FaceCombinedMap : register(t1, space2);", source);
        Assert.Contains("SamplerState MdaLinearWrapSampler : register(s0, space2);", source);
        Assert.Contains("if (r <= 0) faceUv.x = 1 - faceUv.x;", source);
        Assert.Contains("FaceCombinedMap.SampleLevel(MdaLinearWrapSampler, v.Uv, 0).rg;", source);
        Assert.Contains("saturate(saturate(sdf.r + sdf.b) * sdf.g - combined.g);", source);
        Assert.Contains("upper == lower ? 0 : saturate((value - lower) / (upper - lower));", source);
        Assert.DoesNotContain(".15 / FaceSdfParameters.x", source);
        Assert.Contains("MdaStyleLightResponse(v, n, l, visibility);", source);
        Assert.Contains("+ highlight * originalC;", source);
        if (native) Assert.Contains("#define NATIVE_LIGHT_RESPONSE(v, n, l) MdaStyleLightResponse(v, n, l, 1)", source);
        if (sub) Assert.Contains("originalC < 1) alpha *= originalC + ToonShadowColor.w * (1 - originalC);", source);
    }

    [Fact, Trait("Category", "Gpu")]
    public void OriginalPackedChannelsMirroringAndExactRangeCasesMatchIndependentNumericalExpectations()
    {
        using var session = new D3D12FrameSession();
        var original = Frame(); var ordinary = session.Capture(original, Width, Height, surfaceStyles: Map(null));
        var binding = Binding(); var actual = session.Capture(original, Width, Height, surfaceStyles: Map(binding));
        AssertFace(actual, Expected(Primary, Secondary, .5f, .1f));
        // A red-only shortcut, G/B omission, or ignored cmM cannot pass these interiors.
        foreach (var edit in new[] { (Texture: 0, Channel: 0), (Texture: 0, Channel: 1),
            (Texture: 0, Channel: 2), (Texture: 1, Channel: 0), (Texture: 1, Channel: 1) })
        {
            byte[] sdf = [.. Primary], cm = [.. Secondary];
            (edit.Texture == 0 ? sdf : cm)[edit.Channel] += 32;
            var image = session.Capture(Frame(sdf, cm), Width, Height, surfaceStyles: Map(binding));
            AssertFace(image, Expected(sdf, cm, .5f, .1f));
            Assert.NotEqual(Pixel(actual, Width / 2, Height / 3), Pixel(image, Width / 2, Height / 3));
        }
        byte[] saturatedSum = [200, 180, 128, 0];
        AssertFace(session.Capture(Frame(saturatedSum), Width, Height,
            surfaceStyles: Map(Binding(offset: 1.25f))), Expected(saturatedSum, Secondary, .75f, .1f));
        foreach (var direction in new[] { -Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitZ })
        {
            float f = Vector3.Dot(-Vector3.UnitZ, direction);
            // Keep center=.5 while checking both exact r=0 ties select mirrored SDF.
            var selected = Binding(offset: 1 + f * .5f);
            var image = session.Capture(Frame(light: direction), Width, Height, surfaceStyles: Map(selected));
            AssertFace(image, Expected(Mirrored, Secondary, .5f, .1f));
        }
        // cmM's mirrored texel deliberately differs. It must always use original UV.
        byte[] unusedSdf = [.. Primary]; unusedSdf[3] = 255;
        byte[] unusedCm = [.. Secondary]; unusedCm[2] = 242; unusedCm[3] = 255;
        Assert.Equal(actual.Rgba, session.Capture(Frame(unusedSdf, unusedCm), Width, Height, surfaceStyles: Map(binding)).Rgba);
        foreach (var range in new[] { (Offset: .375f, Expected: 0f),
            (Offset: MathF.BitIncrement(.375f), Expected: 1f), (Offset: .25f, Expected: 0f) })
        {
            var image = session.Capture(Frame([1, 1, 0, 0], [0, 0, 0, 0]), Width, Height,
                surfaceStyles: Map(Binding(offset: range.Offset, transition: .125f)));
            AssertFace(image, range.Expected);
        }
        var verticalFrame = Frame(light: Vector3.UnitY);
        Assert.Equal(session.Capture(verticalFrame, Width, Height, surfaceStyles: Map(null)).Rgba,
            session.Capture(verticalFrame, Width, Height, surfaceStyles: Map(binding)).Rgba);
        var tilted = Binding(worldForward: Vector3.Normalize(new(0, 1, -1)), offset: 1.5f);
        // World-Y is not vertical to this tilted head plane; f=1 and r=0 after projection.
        AssertFace(session.Capture(verticalFrame, Width, Height, surfaceStyles: Map(tilted)), Expected(Mirrored, Secondary, .5f, .1f));
        int covered = 0;
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            Assert.Equal(ordinary.Rgba[(y * Width + x) * 4 + 3], actual.Rgba[(y * Width + x) * 4 + 3]);
            if (y >= Height / 2) Assert.Equal(Pixel(ordinary, x, y), Pixel(actual, x, y));
            if (actual.Rgba[(y * Width + x) * 4 + 3] > 0) covered++;
        }
        Assert.True(covered > 300);
        Assert.Equal(ordinary.Rgba, session.Capture(original, Width, Height, surfaceStyles: Map(null)).Rgba);
        Assert.Equal(session.Capture(original, Width, Height, surfaceStyles: Map(null, 0)).Rgba,
            session.Capture(original, Width, Height, surfaceStyles: Map(binding, 0)).Rgba);
        // Endfield plus an actual GrayRed second draw share the packed root; the
        // second material must still use its own old t0-only shader and constants.
        var starFace = TestStylePlugins.FaceSdf("star-rail", 0);
        var mixed = T8dTestHelpers.CreateDirectSurfaceStyleMap(original, ["endfield", "star-rail"],
            [new(D3D12CharacterMaterialRole.Face, 1, 1), new(D3D12CharacterMaterialRole.Face, 1, 1)],
            [[binding], [starFace]]);
        var oldOnly = T8dTestHelpers.CreateDirectSurfaceStyleMap(original, ["endfield", "star-rail"],
            [new(D3D12CharacterMaterialRole.Face, 1, 1), new(D3D12CharacterMaterialRole.Face, 1, 1)],
            [null, [starFace]]);
        var beforeMixed = session.Capture(original, Width, Height, surfaceStyles: oldOnly);
        var packedMixed = session.Capture(original, Width, Height, surfaceStyles: mixed);
        for (int y = Height / 2; y < Height; y++) for (int x = 0; x < Width; x++)
            Assert.Equal(Pixel(beforeMixed, x, y), Pixel(packedMixed, x, y));
        Assert.Equal(beforeMixed.Rgba, session.Capture(original, Width, Height, surfaceStyles: oldOnly).Rgba);
        var antialiased = new OriginalFrameScene(original.Surface, options: original.Options! with
            { Antialiasing = FrameAntialiasing.Smaa1X, SmaaQuality = SmaaQuality.High, SpatialSamples = 2 });
        var aaOrdinary = session.Capture(antialiased, Width, Height, surfaceStyles: Map(null));
        var aaPacked = session.Capture(antialiased, Width, Height, surfaceStyles: Map(binding));
        AssertFace(aaPacked, Expected(Primary, Secondary, .5f, .1f));
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            Assert.Equal(aaOrdinary.Rgba[(y * Width + x) * 4 + 3], aaPacked.Rgba[(y * Width + x) * 4 + 3]);
            if (y >= Height / 2) Assert.Equal(Pixel(aaOrdinary, x, y), Pixel(aaPacked, x, y));
        }
        Assert.Equal(aaOrdinary.Rgba, session.Capture(antialiased, Width, Height, surfaceStyles: Map(null)).Rgba);
    }

    [Fact, Trait("Category", "Gpu")]
    public void NativePointUsesLocalLightDirectionAndPackedDataDoesNotChangeSubTextureAlphaOrAuthoredHighlight()
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(light: -Vector3.UnitX);
        // Authored in the original I / d^2 units, matched at the sampled face distance.
        var lit = new OriginalFrameScene(frame.Surface, options: frame.Options, lighting: new([
            SceneLightFalloff.FromLegacy(new NativeSceneLight(Guid.NewGuid(), "original packed point fixture", SceneLightKind.Point,
                Vector3.Zero, Quaternion.Identity, Vector3.One, .25f, 20, CastShadows: false, AimDistance: .8f))]));
        var off = session.Capture(frame, Width, Height, surfaceStyles: Map(Binding()));
        var on = session.Capture(lit, Width, Height, surfaceStyles: Map(Binding()));
        var left = Project(frame, new(-.6f, .55f, 0)); var right = Project(frame, new(.6f, .55f, 0));
        // Both local light branches differ from the uniform primary -X response.
        Assert.True(Pixel(on, left.X, left.Y)[0] > Pixel(off, left.X, left.Y)[0] + 2);
        Assert.True(Pixel(on, right.X, right.Y)[0] > Pixel(off, right.X, right.Y)[0] + 2);
        foreach (bool sub in new[] { false, true })
        {
            var covered = Frame(light: sub ? -Vector3.UnitX : Vector3.UnitX, subTexture: sub, highlight: true);
            var data = Binding(primary: sub ? 2 : 0, secondary: sub ? 3 : 1);
            var noData = session.Capture(covered, Width, Height, surfaceStyles: Map(null, frame: covered));
            var packed = session.Capture(covered, Width, Height, surfaceStyles: Map(data, frame: covered));
            for (int offset = 3; offset < packed.Rgba.Length; offset += 4) Assert.Equal(noData.Rgba[offset], packed.Rgba[offset]);
            Assert.Equal(session.Capture(covered, Width, Height, surfaceStyles: Map(null, 0, frame: covered)).Rgba,
                session.Capture(covered, Width, Height, surfaceStyles: Map(data, 0, frame: covered)).Rgba);
            Assert.Equal(noData.Rgba, session.Capture(covered, Width, Height, surfaceStyles: Map(null)).Rgba);
            if (!sub)
            {
                var noHighlightFrame = Frame();
                var noHighlightOrdinary = session.Capture(noHighlightFrame, Width, Height, surfaceStyles: Map(null));
                var noHighlightPacked = session.Capture(noHighlightFrame, Width, Height, surfaceStyles: Map(data));
                var a = Pixel(noData, Width / 2, Height / 3); var b = Pixel(packed, Width / 2, Height / 3);
                var c = Pixel(noHighlightOrdinary, Width / 2, Height / 3); var d = Pixel(noHighlightPacked, Width / 2, Height / 3);
                Assert.True(a[0] > c[0] + 8);
                for (int channel = 0; channel < 3; channel++)
                    Assert.InRange(Math.Abs((a[channel] - c[channel]) - (b[channel] - d[channel])), 0, 1);
            }
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void ChangedSecondaryDataResetsBothHistoriesAndAbortedFramesDoNotCommitEitherImage()
    {
        using var session = new D3D12FrameSession();
        Assert.True(session.QueryRayGiCapabilities().SoftwareAvailable);
        string loader = Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
            ?? throw new InvalidOperationException("The explicit GPU test requires the real FSR loader.");
        var temporal = new D3D12TemporalUpscalingOptions
            { Mode = D3D12TemporalUpscalingMode.Fsr, QualityPreset = D3D12FsrQualityPreset.NativeAa, LoaderPath = loader };
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4, Seed: 31,
            Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial);
        // FSR's installed provider requires an opaque composited perspective frame.
        // Keep the separate raw-alpha fixture unchanged for the coverage tests above.
        var initial = TemporalFrame(); byte[] cm = [.. Secondary]; cm[1] += 32;
        var changed = TemporalFrame(cm); var map = Map(Binding());
        Render(initial, 1, "FirstFrame", "FirstFrame"); Render(initial, 2, "None", "None");
        long submitted = session.Runtime.Submissions, readbacks = session.Runtime.Readbacks;
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(changed, Width, Height, gi,
            (_, _) => throw new OperationCanceledException("Abort changed Endfield cmM before Submit."),
            temporalUpscaling: temporal, surfaceStyles: map));
        Assert.Equal(submitted, session.Runtime.Submissions); Assert.False(session.Runtime.IsRecording);
        Render(initial, 3, "None", "AbortedFrame");
        Render(changed, 1, "ExplicitReset", "ExplicitRequest"); Render(changed, 2, "None", "None");
        Render(initial, 1, "ExplicitReset", "ExplicitRequest"); Render(initial, 2, "None", "None");
        Assert.Equal(readbacks, session.Runtime.Readbacks);
        var a = session.Capture(initial, Width, Height, surfaceStyles: map);
        var b = session.Capture(changed, Width, Height, surfaceStyles: map);
        Assert.NotEqual(Pixel(a, Width / 2, Height / 3), Pixel(b, Width / 2, Height / 3));
        Assert.Equal(a.Rgba, session.Capture(initial, Width, Height, surfaceStyles: map).Rgba);

        void Render(OriginalFrameScene frame, ulong frames, string giReason, string aaReason)
        {
            using var output = session.Render(frame, Width, Height, gi, temporalUpscaling: temporal, surfaceStyles: map);
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Equal(frames, session.RayGiStatistics!.TemporalHistoryFrames);
            Assert.Equal(giReason, session.RayGiStatistics.TemporalResetReason);
            Assert.Equal(aaReason, output.Statistics.TemporalUpscaling!.ResetReason);
        }
    }

    private static StyleMaterialBindingData Binding(int primary = 0, int secondary = 1, float transition = .1f, float offset = 1, Vector3? worldForward = null)
        => T8dTestHelpers.EndfieldFaceSdf(primary, secondary, transition, offset, worldForward: worldForward ?? -Vector3.UnitZ, worldRight: Vector3.UnitX);

    private static D3D12SurfaceStyleMap Map(StyleMaterialBindingData? binding, float strength = 1, OriginalFrameScene? frame = null)
        => T8dTestHelpers.CreateDirectSurfaceStyleMap(frame ?? Frame(), ["endfield", "endfield"],
            [new(D3D12CharacterMaterialRole.Face, strength, 1), new()],
            [binding is null ? null : [binding], null]);

    private static OriginalFrameScene TemporalFrame(byte[]? cm = null)
    {
        var source = Frame(cm: cm);
        var snapshot = source.Surface.Snapshot;
        var perspective = snapshot.WithCamera(snapshot.Camera with { VerticalFovDegrees = 45 });
        return new(new(perspective, source.Surface.Materials, source.Surface.Profile),
            options: source.Options! with { BackgroundColor = new(0, 0, 0, 1) });
    }

    private static OriginalFrameScene Frame(byte[]? sdf = null, byte[]? cm = null, Vector3? light = null,
        bool subTexture = false, bool highlight = false)
    {
        var vertices = new List<SceneVertex>();
        foreach (float y in new[] { .55f, -.55f })
            vertices.AddRange([new(new(-.8f, y - .35f, 0), Vector3.UnitX, new(.375f, .5f)),
                new(new(.8f, y - .35f, 0), Vector3.UnitX, new(.375f, .5f)),
                new(new(.8f, y + .35f, 0), Vector3.UnitX, new(.375f, .5f)),
                new(new(-.8f, y + .35f, 0), Vector3.UnitX, new(.375f, .5f))]);
        var textures = new List<SceneTexture>();
        if (subTexture) textures.AddRange([new("raw coverage", 1, 1, [255, 255, 255, 128]),
            new("original subtexture", 1, 1, [128, 128, 128, 128])]);
        // Exactly centered texels avoid depending on a particular filter's interpolation.
        textures.Add(new("original RGB numerical mask", 4, 1, [.. Primary, .. (sdf ?? Primary), .. Mirrored, .. Primary]));
        textures.Add(new("original cmRG numerical mask", 4, 1, [.. Secondary, .. (cm ?? Secondary), 240, 220, 242, 255, .. Secondary]));
        var material = new OriginalStyleMaterialBinding(new(.2f), subTexture ? 1 : -1,
            subTexture ? OriginalSphereMode.SubTexture : OriginalSphereMode.None, true);
        if (subTexture) material = material with { TextureFactors = OriginalMaterialTextureFactors.Identity with
        { Diffuse = new(new(.8f, .7f, .6f, 1), Vector4.Zero), Toon = new(new(1, 1, 1, .2f), Vector4.Zero) } };
        var extra = subTexture ? new SceneAdditionalUvs(1, Enumerable.Repeat(new SceneAdditionalUvVertex(
            new(.25f, .25f, 0, 0), default, default, default), 8).ToArray()) : null;
        var specular = highlight ? new Vector3(.4f, .3f, .2f) : Vector3.Zero;
        var scene = new SceneSnapshot(vertices.ToArray(), [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6],
            [new("explicit face", 0, 6, new(.6f, .4f, .2f, 1), specular, Vector3.Zero, 1, subTexture ? 0 : -1),
                new("unchanged body", 6, 6, new(.6f, .4f, .2f, 1), specular, Vector3.Zero, 1, subTexture ? 0 : -1)],
            textures, camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 20),
            lightDirection: light ?? Vector3.UnitX, additionalUvs: extra);
        // Host specular = material.Specular * profile.LightSpecular. Both must be
        // nonzero for the highlight-preservation witness to exercise a real lobe.
        return new(new(scene, [material, material], new(Vector3.One, highlight ? Vector3.One : Vector3.Zero,
                Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero));
    }

    // Independent scalar oracle written before the product helper: S=(R+B)*G,
    // cmG subtracts shape, cmR scales only the lower threshold. Exact zero maps to0.
    private static float Expected(byte[] sdf, byte[] cm, float center, float transition)
    {
        float value = Math.Clamp(Math.Clamp((sdf[0] + sdf[2]) / 255f, 0, 1) * sdf[1] / 255f - cm[1] / 255f, 0, 1);
        float scale = cm[0] == 0 ? 1 : cm[0] / 255f;
        float lower = Math.Clamp(scale * (center - transition), 0, 1), upper = center + transition;
        return upper == lower ? 0 : Math.Clamp((value - lower) / (upper - lower), 0, 1);
    }

    private static void AssertFace(RenderedFrame image, float band)
        => D3D12OriginalFrameTests.AssertPixel(image, Width / 2, Height / 3, new(new Vector3(.6f, .4f, .2f) * (.2f + .8f * band), 1));
    private static byte[] Pixel(RenderedFrame image, int x, int y) => image.Rgba.AsSpan((y * Width + x) * 4, 4).ToArray();
    private static (int X, int Y) Project(OriginalFrameScene frame, Vector3 point)
    {
        var clip = Vector4.Transform(new Vector4(point, 1), frame.Surface.Snapshot.Camera.ViewProjection(Width, Height));
        return ((int)((clip.X / clip.W * .5f + .5f) * Width), (int)((.5f - clip.Y / clip.W * .5f) * Height));
    }
}
