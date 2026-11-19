using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Original asymmetric numerical masks; the scalar oracle evaluates 16
/// spline taps independently of the shader's four bilinear fetches.</summary>
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12GenshinFaceSdfTests
{
    private const int Width = 129, Height = 97;
    private static readonly Vector2 Uv = new(.23f, .42f);
    private static readonly byte[] Lightmap = [24, 200, 64, 0, 96, 80, 192, 32, 220, 150, 16, 64, 64, 24, 240, 96,
        180, 32, 128, 128, 80, 170, 48, 160, 160, 220, 96, 192, 48, 100, 200, 255];
    private static readonly byte[] Mask = [23, 160, 41, 0, 47, 220, 79, 80,
        83, 180, 107, 120, 113, 120, 139, 200, 149, 240, 167, 30, 181, 80, 193, 250];

    [Fact]
    public void ExplicitFactoryUsesAuditedShapeDefaultsWithoutChangingLegacyDefaults()
    {
        var mode = TestStylePlugins.Genshin.MaterialModes.Single(m => m.ModeId == "face-sdf");
        Assert.Equal("GenshinSoftFace", mode.Parameters.Single(p => p.Key == "encoding").DefaultString);
        Assert.Equal(0.001, mode.Parameters.Single(p => p.Key == "transition").DefaultNumber);
        Assert.Equal(0.55, mode.Parameters.Single(p => p.Key == "offset").DefaultNumber);
        var defaultBinding = new StyleMaterialBindingData("genshin", "face-sdf",
            new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal) { ["primary"] = 0, ["secondary"] = 1 }),
            BoneBinding: new StyleBoneAxisBinding(StyleBoneTrackingMode.FixedWorld, FixedWorldForward: -Vector3.UnitZ, FixedWorldRight: Vector3.UnitX));
        Assert.True(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Face, Frame(), defaultBinding).IsValid);
        var defaultDraw = T8bTestHelpers.BuildGenshinDrawData(D3D12CharacterMaterialRole.Face, 1f, 1f, 0.2f, defaultBinding)!;
        Assert.Equal(0.001f, BinaryPrimitives.ReadSingleLittleEndian(defaultDraw.ConstantBufferData.Span[32..36]));
        Assert.Equal(0.55f, BinaryPrimitives.ReadSingleLittleEndian(defaultDraw.ConstantBufferData.Span[36..40]));

        var binding = TestStylePlugins.FaceSdf("genshin", 0, 1, "GenshinSoftFace", .001f, .55f);
        Map(binding).Validate(Frame());
        var map = Map(binding);
        Assert.Equal("face-sdf", map.ResolveShaderModule(0)!.ModuleId);
        Assert.Equal(2, map.GetPluginDraw(0)!.TextureBindings!.Count);
        Assert.True(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Face, Frame(), WithTransition(binding, 0f)).IsValid);

        var starMode = TestStylePlugins.StarRail.MaterialModes.Single(m => m.ModeId == "face-sdf");
        Assert.Equal(1.0, starMode.Parameters.Single(p => p.Key == "transition").DefaultNumber);
        Assert.Equal(1.0, starMode.Parameters.Single(p => p.Key == "offset").DefaultNumber);
        Assert.False(T8bTestHelpers.ValidateStyle(TestStylePlugins.StarRail, StyleMaterialRole.Face, Frame(),
            TestStylePlugins.FaceSdf("star-rail", 0, transition: 0f, offset: 1f)).IsValid);

        Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Body, Frame(), binding).IsValid);
        Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Eye, Frame(), binding).IsValid);
        Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Face, Frame(), WithSlots(binding, 0, 2)).IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => Map(WithSlots(binding, 0, 2)).Validate(Frame()));
        Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Face, Frame(), WithSlots(binding, 0, -1)).IsValid);
        Assert.Throws<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate([],
            [TestStylePlugins.Bind(Guid.NewGuid(), 0, WithSlots(binding, 0, -1))], TestStylePlugins.AllViewCatalog));
        Assert.True(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Face, Frame(), WithSlots(binding, 0, 0)).IsValid);
        Map(WithSlots(binding, 0, 0)).Validate(Frame());
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void GenshinShapeCannotBeMisinterpretedByAnotherFamily(string styleId)
    {
        var genshinBinding = Binding();
        if (styleId == "mmd")
        {
            var draw = T8bTestHelpers.BuildGenshinDrawData(D3D12CharacterMaterialRole.Face, 1f, 1f, 0.2f, genshinBinding);
            Assert.Throws<NotSupportedException>(() => new D3D12SurfaceStyleMap(["mmd", "mmd"],
                [new(D3D12CharacterMaterialRole.Face, 1, 1, PluginDraw: draw), new()], TestStylePlugins.AllPresetCatalog).Validate(Frame()));
        }
        else
        {
            Assert.False(T8bTestHelpers.ValidateStyle(TestStylePlugins.GetStyle(styleId), StyleMaterialRole.Face, Frame(), genshinBinding).IsValid);
            string foreignEncoding = styleId == "endfield" ? "EndfieldRgbCmM" : "GrayRed";
            Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Face, Frame(),
                TestStylePlugins.FaceSdf("genshin", 0, 1, encoding: foreignEncoding)).IsValid);
        }
    }

    [Theory]
    [InlineData(-.001f)]
    [InlineData(101f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidSoftnessIsRejectedBeforeRecording(float value)
    {
        var invalid = WithTransition(Binding(), value);
        Assert.False(T8bTestHelpers.ValidateGenshin(StyleMaterialRole.Face, Frame(), invalid).IsValid);
        Assert.ThrowsAny<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate([],
            [TestStylePlugins.Bind(Guid.NewGuid(), 0, invalid)], TestStylePlugins.AllViewCatalog));
    }

    [Fact]
    public void DetachedEqualityConsumesPrimaryRgbAndMaskGreenAlphaAndSupportsRebasedIndices()
    {
        var binding = Binding(); var map = Map(binding); var frame = Frame();
        var captured = map.CaptureResolvedInputs(frame);
        foreach (var changed in new[] { (Texture: 0, Channel: 0), (Texture: 0, Channel: 1),
            (Texture: 0, Channel: 2), (Texture: 1, Channel: 1), (Texture: 1, Channel: 3) })
        {
            byte[] primary = [.. Lightmap], secondary = [.. Mask];
            (changed.Texture == 0 ? primary : secondary)[changed.Channel]++;
            Assert.False(D3D12SurfaceStyleMap.Equivalent(captured, map.CaptureResolvedInputs(Frame(primary, secondary))));
        }
        Assert.True(D3D12SurfaceStyleMap.Equivalent(captured, map.CaptureResolvedInputs(Frame([.. Lightmap], [.. Mask]))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(captured, map.CaptureResolvedInputs(frame)));
        var original = frame.Surface.Snapshot;
        var reordered = new SceneSnapshot(original.Vertices, original.Indices, original.Materials,
            original.Textures.Reverse(), world: original.World, camera: original.Camera, lightDirection: original.LightDirection);
        var rebasedFrame = new OriginalFrameScene(new(reordered, frame.Surface.Materials, frame.Surface.Profile), options: frame.Options);
        var rebasedMap = Map(WithSlots(binding, 1, 0));
        rebasedMap.Validate(rebasedFrame);
        Assert.False(D3D12SurfaceStyleMap.Equivalent(map, rebasedMap));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(captured, rebasedMap.CaptureResolvedInputs(rebasedFrame)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(captured, Map(WithTransition(binding, .3f)).CaptureResolvedInputs(frame)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(captured, Map(WithOffset(binding, .6f)).CaptureResolvedInputs(frame)));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(binding, 0), Map(null, 0)));
        Assert.Equal("default", Map(binding, 0).ResolveShaderModule(0)!.ModuleId);
        Assert.Null(Map(binding, 0).GetPluginDraw(0)?.TextureBindings);
    }

    public static IEnumerable<object[]> Variants()
    {
        foreach (bool native in new[] { false, true })
        foreach (bool gi in new[] { false, true })
        foreach (var variant in new[] { (Morph: false, Sub: false), (Morph: true, Sub: false), (Morph: true, Sub: true) })
            yield return [native, gi, variant.Morph, variant.Sub];
    }

    [Theory, MemberData(nameof(Variants))]
    public void AllGenshinSoftVariantsCompileWithSharedLightConsumersAndCubicRepeat(bool native, bool gi, bool morph, bool sub)
    {
        string entry = native ? gi ? "PSSurfaceGiNative" : "PSSurfaceNative" : gi ? "PSSurfaceGi" : "PSSurface";
        var shader = T8bTestHelpers.CompileGenshinPixel("face-sdf", entry, native, gi, morph, sub);
        Assert.Equal(new byte[] { 68, 88, 66, 67 }, shader[..4]);
        string source = T8bTestHelpers.AssembleGenshinPixelSource("face-sdf", entry, native, gi, morph, sub);
        Assert.Contains("cbuffer PluginStyleConstants : register(b0, space2)", source);
        Assert.Contains("FaceSdfMap : register(t0, space2)", source); Assert.Contains("FaceCombinedMap : register(t1, space2)", source);
        Assert.Contains("image.GetDimensions(width, height)", source);
        Assert.Contains("MdaLinearWrapSampler : register(s0, space2)", source);
        Assert.Contains("float2 horizontal = l.xz;", source);
        Assert.Contains("if (side <= 0) faceUv.x = -faceUv.x;", source);
        Assert.Contains("FaceCubicRepeat(FaceCombinedMap, v.Uv)", source);
        Assert.Contains("float3(.2126, .7152, .0722)", source);
        Assert.Contains("upper == lower ? 0 : saturate", source);
        Assert.DoesNotContain("GenshinLightResponse", source);
        Assert.Contains("MdaStyleLightResponse(v, n, l, visibility)", source);
        Assert.Contains("highlight * originalC", source);
        if (native) Assert.Contains("#define NATIVE_LIGHT_RESPONSE(v, n, l) MdaStyleLightResponse(v, n, l, 1)", source);
        if (gi) Assert.Contains("MdaStyleLightResponse(v, n, l, visibility)", source);
        if (sub) Assert.Contains("originalC < 1) alpha *= originalC + ToonShadowColor.w * (1 - originalC)", source);
    }

    [Fact, Trait("Category", "Gpu")]
    public void ColoredAsymmetricDifferentSizedMasksUseCubicRepeatFixedWorldPlaneAndExactSideTie()
    {
        using var session = new D3D12FrameSession(); var binding = Binding();
        foreach (var light in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ,
            Vector3.UnitY, Vector3.Normalize(new Vector3(.3f, .8f, -.6f)) })
        foreach (var forward in new[] { -Vector3.UnitZ, Vector3.Normalize(new Vector3(0, 1, -1)), Vector3.UnitY })
        {
            // Forward=Y is valid with right=X and is zero only after fixed-plane projection.
            var selected = WithForward(binding, forward);
            var frame = Frame(light: light);
            AssertFace(session.Capture(frame, Width, Height, surfaceStyles: Map(selected)),
                Expected(Lightmap, Mask, Uv, selected, light));
        }
        foreach (var uv in new[] { new Vector2(-.23f, .42f), new Vector2(1.23f, -.58f), new Vector2(.001f, .999f) })
        {
            var frame = Frame(uv: uv);
            AssertFace(session.Capture(frame, Width, Height, surfaceStyles: Map(binding)),
                Expected(Lightmap, Mask, uv, binding, Vector3.UnitX));
        }
        var ordinary = session.Capture(Frame(), Width, Height, surfaceStyles: Map(null));
        var shaped = session.Capture(Frame(), Width, Height, surfaceStyles: Map(binding));
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            Assert.Equal(ordinary.Rgba[(y * Width + x) * 4 + 3], shaped.Rgba[(y * Width + x) * 4 + 3]);
            if (y >= Height / 2) Assert.Equal(Pixel(ordinary, x, y), Pixel(shaped, x, y));
        }
        Assert.Equal(ordinary.Rgba, session.Capture(Frame(), Width, Height, surfaceStyles: Map(null)).Rgba);
        Assert.Equal(session.Capture(Frame(), Width, Height, surfaceStyles: Map(null, 0)).Rgba,
            session.Capture(Frame(), Width, Height, surfaceStyles: Map(binding, 0)).Rgba);
        // Public indices remain per-frame data; parameter changes must reuse finite shader/PSO keys.
        var warm = session.Statistics!; long readbacks = session.Runtime.Readbacks;
        for (int i = 0; i < 6; i++)
        {
            using var rendered = session.Render(Frame(), Width, Height, surfaceStyles: Map(WithOffset(binding, .5f + .01f * i)));
            Assert.Equal(warm.ShaderCompilations, rendered.Statistics.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations, rendered.Statistics.PipelineCreations);
            Assert.Equal(0, rendered.Statistics.ReadbackCount);
        }
        Assert.Equal(readbacks, session.Runtime.Readbacks);
    }

    [Fact, Trait("Category", "Gpu")]
    public void ZeroSoftnessCompatiblePowerAlphaMasksAndPmxCoverageRemainDistinct()
    {
        using var session = new D3D12FrameSession(); var binding = Binding();
        foreach (var pair in new[] { (Position: 1.5f, Softness: .1f), (Position: 2f, Softness: .1f),
            (Position: .55f, Softness: 0f), (Position: .55f, Softness: .00001f) })
        foreach (byte value in new byte[] { 0, 255 })
        {
            byte[] primary = Enumerable.Repeat(new byte[] { value, value, value, 0 }, 8).SelectMany(x => x).ToArray();
            byte[] mask = Enumerable.Repeat(new byte[] { 25, 255, 75, 0 }, 6).SelectMany(x => x).ToArray();
            var selected = WithTransition(WithOffset(binding, pair.Position), pair.Softness);
            AssertFace(session.Capture(Frame(primary, mask, Vector3.UnitZ), Width, Height, surfaceStyles: Map(selected)),
                Expected(primary, mask, Uv, selected, Vector3.UnitZ));
        }
        // Low-alpha extra term, equality .55 and high-alpha gate are mask values,
        // independently cubic sampled; they cannot become diffuse coverage.
        foreach (byte alpha in new byte[] { 0, 64, 140, 141, 220 })
        {
            byte[] mask = Enumerable.Repeat(new byte[] { 17, 110, 91, alpha }, 6).SelectMany(x => x).ToArray();
            var frame = Frame(mask: mask, light: Vector3.UnitZ);
            AssertFace(session.Capture(frame, Width, Height, surfaceStyles: Map(binding)),
                Expected(Lightmap, mask, Uv, binding, Vector3.UnitZ));
        }
        foreach (bool sub in new[] { false, true })
        {
            var frame = Frame(subTexture: sub, highlight: true);
            var selected = WithSlots(binding, sub ? 2 : 0, sub ? 3 : 1);
            var ordinary = session.Capture(frame, Width, Height, surfaceStyles: Map(null));
            var shaped = session.Capture(frame, Width, Height, surfaceStyles: Map(selected));
            for (int p = 3; p < shaped.Rgba.Length; p += 4) Assert.Equal(ordinary.Rgba[p], shaped.Rgba[p]);
            Assert.Equal(ordinary.Rgba, session.Capture(frame, Width, Height, surfaceStyles: Map(null)).Rgba);
            if (!sub)
            {
                var darkOrdinary = session.Capture(Frame(), Width, Height, surfaceStyles: Map(null));
                var darkShaped = session.Capture(Frame(), Width, Height, surfaceStyles: Map(binding));
                var a = Pixel(ordinary, Width / 2, Height / 3); var b = Pixel(shaped, Width / 2, Height / 3);
                var c = Pixel(darkOrdinary, Width / 2, Height / 3); var d = Pixel(darkShaped, Width / 2, Height / 3);
                Assert.True(a[0] > c[0] + 8);
                for (int channel = 0; channel < 3; channel++)
                    Assert.InRange(Math.Abs((a[channel] - c[channel]) - (b[channel] - d[channel])), 0, 1);
            }
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void SecondaryGreenAlphaChangesResetSubmittedHistoriesAndAbortDoesNotCommitNewData()
    {
        using var session = new D3D12FrameSession();
        Assert.True(session.QueryRayGiCapabilities().SoftwareAvailable);
        string loader = Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
            ?? throw new InvalidOperationException("The explicit GPU test requires the real FSR loader.");
        var temporal = new D3D12TemporalUpscalingOptions
            { Mode = D3D12TemporalUpscalingMode.Fsr, QualityPreset = D3D12FsrQualityPreset.NativeAa, LoaderPath = loader };
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4, Seed: 37,
            Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial);
        var initial = TemporalFrame(); byte[] mask = [.. Mask]; mask[1] += 32; mask[3] += 32;
        var changed = TemporalFrame(mask); var map = Map(Binding());
        Render(initial, 1, "FirstFrame", "FirstFrame"); Render(initial, 2, "None", "None");
        long submitted = session.Runtime.Submissions, readbacks = session.Runtime.Readbacks;
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(changed, Width, Height, gi,
            (_, _) => throw new OperationCanceledException("Abort changed Genshin packed data before Submit."),
            temporalUpscaling: temporal, surfaceStyles: map));
        Assert.Equal(submitted, session.Runtime.Submissions); Assert.False(session.Runtime.IsRecording);
        Render(initial, 3, "None", "AbortedFrame");
        Render(changed, 1, "ExplicitReset", "ExplicitRequest"); Render(changed, 2, "None", "None");
        Render(initial, 1, "ExplicitReset", "ExplicitRequest"); Render(initial, 2, "None", "None");
        Assert.Equal(readbacks, session.Runtime.Readbacks);

        void Render(OriginalFrameScene frame, ulong frames, string giReason, string aaReason)
        {
            using var output = session.Render(frame, Width, Height, gi, temporalUpscaling: temporal, surfaceStyles: map);
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Equal(frames, session.RayGiStatistics!.TemporalHistoryFrames);
            Assert.Equal(giReason, session.RayGiStatistics.TemporalResetReason);
            Assert.Equal(aaReason, output.Statistics.TemporalUpscaling!.ResetReason);
        }
    }

    private static StyleMaterialBindingData Binding() => TestStylePlugins.FaceSdf("genshin", 0, 1, "GenshinSoftFace", .4f, .55f);
    private static StyleMaterialBindingData WithSlots(StyleMaterialBindingData b, int primary, int secondary)
        => b with { TextureSlots = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal) { ["primary"] = primary, ["secondary"] = secondary }) };
    private static StyleMaterialBindingData WithTransition(StyleMaterialBindingData b, float transition)
    {
        var d = new Dictionary<string, double>(b.NumberValues!, StringComparer.Ordinal) { ["transition"] = transition };
        return b with { NumberValues = new ReadOnlyDictionary<string, double>(d) };
    }
    private static StyleMaterialBindingData WithOffset(StyleMaterialBindingData b, float offset)
    {
        var d = new Dictionary<string, double>(b.NumberValues!, StringComparer.Ordinal) { ["offset"] = offset };
        return b with { NumberValues = new ReadOnlyDictionary<string, double>(d) };
    }
    private static StyleMaterialBindingData WithForward(StyleMaterialBindingData b, Vector3 forward)
        => b with { BoneBinding = b.BoneBinding! with { FixedWorldForward = forward } };

    private static D3D12SurfaceStyleMap Map(StyleMaterialBindingData? binding, float strength = 1)
        => T8bTestHelpers.CreateGenshinTwoMap("genshin", D3D12CharacterMaterialRole.Face, binding is null ? null : [binding],
            faceNormalShadeStrength0: strength, faceGeometricShadowStrength0: 1f);

    private static OriginalFrameScene TemporalFrame(byte[]? mask = null)
    {
        var source = Frame(mask: mask); var snapshot = source.Surface.Snapshot;
        return new(new(snapshot.WithCamera(snapshot.Camera with { VerticalFovDegrees = 45 }), source.Surface.Materials, source.Surface.Profile),
            options: source.Options! with { BackgroundColor = new(0, 0, 0, 1) });
    }

    private static OriginalFrameScene Frame(byte[]? lightmap = null, byte[]? mask = null, Vector3? light = null,
        Vector2? uv = null, bool subTexture = false, bool highlight = false)
    {
        var vertices = new List<SceneVertex>();
        foreach (float y in new[] { .55f, -.55f })
            vertices.AddRange([new(new(-.8f, y - .35f, 0), Vector3.UnitX, uv ?? Uv),
                new(new(.8f, y - .35f, 0), Vector3.UnitX, uv ?? Uv),
                new(new(.8f, y + .35f, 0), Vector3.UnitX, uv ?? Uv),
                new(new(-.8f, y + .35f, 0), Vector3.UnitX, uv ?? Uv)]);
        var textures = new List<SceneTexture>();
        if (subTexture) textures.AddRange([new("raw coverage", 1, 1, [255, 255, 255, 128]),
            new("original subtexture", 1, 1, [128, 128, 128, 128])]);
        textures.Add(new("original nonsquare colored lightmap", 4, 2, lightmap ?? Lightmap));
        textures.Add(new("original differently sized GA mask", 2, 3, mask ?? Mask));
        var material = new OriginalStyleMaterialBinding(new(.2f), subTexture ? 1 : -1,
            subTexture ? OriginalSphereMode.SubTexture : OriginalSphereMode.None, true);
        var extra = subTexture ? new SceneAdditionalUvs(1, Enumerable.Repeat(new SceneAdditionalUvVertex(
            new(.25f, .25f, 0, 0), default, default, default), 8).ToArray()) : null;
        var specular = highlight ? new Vector3(.4f, .3f, .2f) : Vector3.Zero;
        var scene = new SceneSnapshot(vertices.ToArray(), [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6],
            [new("explicit face", 0, 6, new(.6f, .4f, .2f, 1), specular, Vector3.Zero, 1, subTexture ? 0 : -1),
                new("unchanged body", 6, 6, new(.6f, .4f, .2f, 1), specular, Vector3.Zero, 1, subTexture ? 0 : -1)],
            textures, camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 20),
            lightDirection: light ?? Vector3.UnitX, additionalUvs: extra);
        return new(new(scene, [material, material], new(Vector3.One, highlight ? Vector3.One : Vector3.Zero,
                Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero));
    }

    private static float Expected(byte[] lightmap, byte[] mask, Vector2 uv, StyleMaterialBindingData binding, Vector3 light)
    {
        var rawF = binding.BoneBinding?.FixedWorldForward ?? -Vector3.UnitZ;
        var rawR = binding.BoneBinding?.FixedWorldRight ?? Vector3.UnitX;
        var forward = Vector3.Normalize(rawF);
        var right = Vector3.Normalize(rawR);
        right -= forward * Vector3.Dot(forward, right);
        right = right.LengthSquared() < 1e-8f
            ? Vector3.Normalize(Vector3.Cross(forward, MathF.Abs(forward.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX))
            : Vector3.Normalize(right);
        float transition = (float)binding.NumberValues!["transition"];
        float offset = (float)binding.NumberValues["offset"];
        var horizontal = new Vector2(light.X, light.Z);
        if (horizontal != Vector2.Zero) horizontal = Vector2.Normalize(horizontal);
        float d = Vector2.Dot(new(forward.X, forward.Z), horizontal);
        if (Vector2.Dot(new(right.X, right.Z), horizontal) <= 0) uv.X = -uv.X;
        var color = Cubic(lightmap, 4, 2, uv);
        // The packed mask is always sampled at original UV, not the mirrored UV.
        Vector2 originalUv = uv;
        if (Vector2.Dot(new(right.X, right.Z), horizontal) <= 0) originalUv.X = -originalUv.X;
        var combined = Cubic(mask, 2, 3, originalUv);
        float a = MathF.Min((d + 1) * .5f, .999f);
        float e = MathF.Pow(1.5f - offset, 3);
        float p = e == 0 ? 1 : a == 0 ? 0 : MathF.Pow(a, e);
        float lower = p - transition, upper = p + transition;
        float luminance = Vector3.Dot(new(color.X, color.Y, color.Z), new(.2126f, .7152f, .0722f));
        float t = lower == upper ? 0 : Math.Clamp((luminance - lower) / (upper - lower), 0, 1);
        float extra = Math.Clamp(((1 - p) - .71f) / (.8f - .71f), 0, 1)
            * Math.Clamp((combined.W - (combined.W > .55f ? 1 : 0)) * 5, 0, 1);
        return Math.Clamp(t * t * (3 - 2 * t) * combined.Y + extra, 0, 1);
    }

    private static Vector4 Cubic(byte[] pixels, int width, int height, Vector2 uv)
    {
        double x = uv.X * width - .5, y = uv.Y * height - .5;
        int bx = (int)Math.Floor(x), by = (int)Math.Floor(y);
        double fx = x - bx, fy = y - by; var result = Vector4.Zero;
        // Centered cubic B-spline basis from its piecewise definition, not the
        // product's polynomial weight vector or four filtered sampling positions.
        static double Weight(double distance)
        {
            double t = Math.Abs(distance);
            return t < 1 ? (4 - 6 * t * t + 3 * t * t * t) / 6 : t < 2 ? Math.Pow(2 - t, 3) / 6 : 0;
        }
        static int Repeat(int value, int length) => (value % length + length) % length;
        for (int j = -1; j <= 2; j++) for (int i = -1; i <= 2; i++)
        {
            int p = (Repeat(by + j, height) * width + Repeat(bx + i, width)) * 4;
            result += new Vector4(pixels[p], pixels[p + 1], pixels[p + 2], pixels[p + 3])
                * (float)(Weight(fx - i) * Weight(fy - j) / 255);
        }
        return result;
    }

    private static void AssertFace(RenderedFrame image, float band)
        => D3D12OriginalFrameTests.AssertPixel(image, Width / 2, Height / 3, new(new Vector3(.6f, .4f, .2f) * (.2f + .8f * band), 1));
    private static byte[] Pixel(RenderedFrame image, int x, int y) => image.Rgba.AsSpan((y * Width + x) * 4, 4).ToArray();
}
