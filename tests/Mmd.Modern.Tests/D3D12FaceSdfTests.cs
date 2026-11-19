using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12FaceSdfTests
{
    private const int Width = 129, Height = 97;

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void ExplicitCombinedIndicesMustBelongToThisFrame(int index)
    {
        var binding = Binding(T8cTestHelpers.StarRailStyleId, primary: index);
        var validation = T8cTestHelpers.ValidateMaterialBinding(
            T8cTestHelpers.StarRailStyleId, binding, D3D12CharacterMaterialRole.Face, sourceTextureCount: 1);
        Assert.False(validation.IsValid);
        if (index >= 0)
            Assert.Throws<ArgumentOutOfRangeException>(() => Map(T8cTestHelpers.StarRailStyleId, binding).Validate(Fixture()));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void InvalidTransitionIsRejectedBeforeRecording(float value)
    {
        foreach (string styleId in new[] { T8cTestHelpers.StarRailStyleId, T8cTestHelpers.WutheringStyleId })
        {
            var binding = Binding(styleId, transition: value);
            Assert.False(T8cTestHelpers.ValidateMaterialBinding(styleId, binding, D3D12CharacterMaterialRole.Face, 1).IsValid);
        }
    }

    [Fact]
    public void ExplicitRoleFamilyChannelSamplerAndHeadBasisAreValidated()
    {
        var binding = Binding(T8cTestHelpers.StarRailStyleId);
        Assert.True(T8cTestHelpers.ValidateMaterialBinding(T8cTestHelpers.StarRailStyleId, binding, D3D12CharacterMaterialRole.Face, 1).IsValid);
        Assert.False(T8cTestHelpers.ValidateMaterialBinding(T8cTestHelpers.WutheringStyleId, Binding(T8cTestHelpers.WutheringStyleId), D3D12CharacterMaterialRole.Body, 1).IsValid);
        Assert.False(T8cTestHelpers.ValidateMaterialBinding(T8cTestHelpers.StarRailStyleId, binding, D3D12CharacterMaterialRole.Eye, 1).IsValid);
        Assert.False(T8cTestHelpers.ValidateMaterialBinding(T8cTestHelpers.StarRailStyleId, Binding(T8cTestHelpers.StarRailStyleId, encoding: "Invalid"), D3D12CharacterMaterialRole.Face, 1).IsValid);
        Assert.False(T8cTestHelpers.ValidateMaterialBinding(T8cTestHelpers.StarRailStyleId, Binding(T8cTestHelpers.StarRailStyleId, secondary: 0), D3D12CharacterMaterialRole.Face, 1).IsValid);
        Assert.False(T8cTestHelpers.ValidateMaterialBinding(T8cTestHelpers.StarRailStyleId, Binding(T8cTestHelpers.StarRailStyleId, forward: Vector3.Zero), D3D12CharacterMaterialRole.Face, 1).IsValid);
        Assert.False(T8cTestHelpers.ValidateMaterialBinding(T8cTestHelpers.StarRailStyleId, Binding(T8cTestHelpers.StarRailStyleId, right: Vector3.UnitZ), D3D12CharacterMaterialRole.Face, 1).IsValid);
        Assert.False(T8cTestHelpers.ValidateMaterialBinding(T8cTestHelpers.StarRailStyleId, Binding(T8cTestHelpers.StarRailStyleId, right: new(float.NaN, 0, 0)), D3D12CharacterMaterialRole.Face, 1).IsValid);
        Assert.False(T8cTestHelpers.ValidateMaterialBinding(T8cTestHelpers.StarRailStyleId, Binding(T8cTestHelpers.StarRailStyleId, offset: float.NaN), D3D12CharacterMaterialRole.Face, 1).IsValid);
        Assert.False(T8cTestHelpers.ValidateMaterialBinding(T8cTestHelpers.StarRailStyleId, Binding(T8cTestHelpers.StarRailStyleId, offset: 3), D3D12CharacterMaterialRole.Face, 1).IsValid);

        Assert.Throws<NotSupportedException>(() => T8cTestHelpers.CreateDirectMap(
            [T8cTestHelpers.MmdStyleId, T8cTestHelpers.MmdStyleId],
            [new(D3D12CharacterMaterialRole.Face, PluginDraw: T8cTestHelpers.BuildPluginDraw(T8cTestHelpers.StarRailStyleId, binding, D3D12CharacterMaterialRole.Face)), new()]).Validate(Fixture()));
        foreach (string family in new[] { T8cTestHelpers.GenshinStyleId, T8cTestHelpers.EndfieldStyleId })
            Assert.False(T8cTestHelpers.ValidateMaterialBinding(family, Binding(family, encoding: "GrayRed"), D3D12CharacterMaterialRole.Face, 2).IsValid);

        // The source MC template's broad transition=10 is valid, not silently changed to Star's inverse width.
        var wutheringBroad = Binding(T8cTestHelpers.WutheringStyleId, transition: 10);
        Assert.True(T8cTestHelpers.ValidateMaterialBinding(T8cTestHelpers.WutheringStyleId, wutheringBroad, D3D12CharacterMaterialRole.Face, 1).IsValid);
        var wutheringMap = Map(T8cTestHelpers.WutheringStyleId, wutheringBroad);
        wutheringMap.Validate(Fixture());
        Assert.Equal(10f, T8cTestHelpers.ReadFaceSdfConstants(wutheringMap.CharacterMaterials[0].PluginDraw!).Transition);
    }

    [Fact]
    public void ConsumptionEqualityIncludesMaskContentAndNormalizedHeadPoseButIgnoresDisabledInput()
    {
        var frame = Fixture();
        var basis = Binding(T8cTestHelpers.StarRailStyleId);
        var a = Map(T8cTestHelpers.StarRailStyleId, basis);
        var equal = Map(T8cTestHelpers.StarRailStyleId, Binding(T8cTestHelpers.StarRailStyleId, forward: -Vector3.UnitZ * 2, right: Vector3.UnitX * 3));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(a, equal));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(a, Map(T8cTestHelpers.StarRailStyleId, Binding(T8cTestHelpers.StarRailStyleId, right: -Vector3.UnitX))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(a, Map(T8cTestHelpers.StarRailStyleId, Binding(T8cTestHelpers.StarRailStyleId, transition: 2))));
        var captured = a.CaptureResolvedInputs(frame);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(captured, equal.CaptureResolvedInputs(Fixture())));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(captured, a.CaptureResolvedInputs(Fixture(invertMask: true))));
        // Reusing a public map captures new frame data separately; previous submitted state stays detached.
        Assert.True(D3D12SurfaceStyleMap.Equivalent(captured, a.CaptureResolvedInputs(frame)));
        var zero = Map(T8cTestHelpers.StarRailStyleId, basis, strength: 0);
        Assert.Equal("default", zero.CharacterMaterials[0].PluginDraw!.ShaderModuleId);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(zero, Map(T8cTestHelpers.StarRailStyleId, null, strength: 0)));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(zero, Map(T8cTestHelpers.StarRailStyleId, Binding(T8cTestHelpers.StarRailStyleId, offset: 0), strength: 0)));
    }

    public static IEnumerable<object[]> Variants()
    {
        foreach (string family in new[] { T8cTestHelpers.StarRailStyleId, T8cTestHelpers.WutheringStyleId })
        foreach (bool native in new[] { false, true })
        foreach (bool gi in new[] { false, true })
        foreach (var variant in new[] { (Morph: false, Sub: false), (Morph: true, Sub: false), (Morph: true, Sub: true) })
            yield return [family, native, gi, variant.Morph, variant.Sub];
    }

    [Theory, MemberData(nameof(Variants))]
    public void EveryDedicatedVariantCompilesAndAllActualLightConsumersUseTheSameFaceResponse(string family, bool native, bool gi, bool morph, bool sub)
    {
        string entry = native ? (gi ? "PSSurfaceGiNative" : "PSSurfaceNative") : gi ? "PSSurfaceGi" : "PSSurface";
        var shader = T8cTestHelpers.CompilePluginSurface(family, "face-sdf", entry, native, gi, morph, sub);
        Assert.Equal(new byte[] { 68, 88, 66, 67 }, shader[..4]);
        string source = T8cTestHelpers.AssemblePluginSurfaceSource(family, "face-sdf", entry, native, gi, morph, sub);
        string fallbackMethod = family == T8cTestHelpers.StarRailStyleId ? "StarRailLightResponse" : "WutheringRawLightResponse";
        Assert.Contains("cbuffer PluginStyleConstants : register(b0, space2)", source);
        Assert.Contains("Texture2D<float4> FaceSdfMap : register(t0, space2);", source);
        Assert.Contains("SamplerState MdaLinearWrapSampler : register(s0, space2);", source);
        Assert.Contains("MdaStyleLightResponse(v, n, l, visibility)", source);
        Assert.Equal(1, source.Split(fallbackMethod, StringSplitOptions.None).Length - 2);
        Assert.Contains("highlight * originalC", source);
        if (sub) Assert.Contains("originalC < 1) alpha *= originalC + ToonShadowColor.w * (1 - originalC)", source);
        if (family == T8cTestHelpers.StarRailStyleId) Assert.Contains(".15 / FaceSdfParameters.x", source);
        else Assert.Contains("float width = FaceSdfParameters.x", source);
    }

    [Theory, InlineData(T8cTestHelpers.StarRailStyleId), InlineData(T8cTestHelpers.WutheringStyleId)]
    [Trait("Category", "Gpu")]
    public void ActualGrayTextureAndWorldHeadBasisDriveOnlyTheExplicitFaceAndRestoreTheOrdinaryFrame(string family)
    {
        using var session = new D3D12FrameSession(); var frame = Fixture();
        var ordinary = Map(family, null);
        var before = session.Capture(frame, Width, Height, surfaceStyles: ordinary);
        var left = session.Capture(frame, Width, Height, surfaceStyles: Map(family, Binding(family)));
        var right = session.Capture(frame, Width, Height, surfaceStyles: Map(family, Binding(family, right: -Vector3.UnitX)));
        // UV .375 samples gray 64/255; mirroring by -U with Repeat samples 192/255.
        // At sideways light center=.5. Both families' selected halfwidths place these
        // values on exact opposite plateaus, independently of the unchanged +X normal.
        AssertPixel(left, Height / 3, new(.6f, .4f, .2f, 1));
        AssertPixel(right, Height / 3, new(.12f, .08f, .04f, 1));
        int changed = 0;
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            Assert.Equal(before.Rgba[(y * Width + x) * 4 + 3], right.Rgba[(y * Width + x) * 4 + 3]);
            if (y >= Height / 2) Assert.Equal(Pixel(before, x, y), Pixel(right, x, y));
            else if (!Pixel(left, x, y).SequenceEqual(Pixel(right, x, y))) changed++;
        }
        Assert.True(changed > 200);
        Assert.Equal(before.Rgba, session.Capture(frame, Width, Height, surfaceStyles: ordinary).Rgba);
        // No inferred mask is used at zero artistic shade strength.
        var zero = Map(family, Binding(family), 0);
        Assert.Equal(session.Capture(frame, Width, Height, surfaceStyles: Map(family, null, 0)).Rgba,
            session.Capture(frame, Width, Height, surfaceStyles: zero).Rgba);
        var warm = session.Statistics!; long readbacks = session.Runtime.Readbacks;
        for (int i = 0; i < 8; i++)
        {
            using var output = session.Render(frame, Width, Height, surfaceStyles: Map(family, Binding(family, offset: 1 + i * .01f)));
            Assert.Equal(warm.ShaderCompilations, output.Statistics.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations, output.Statistics.PipelineCreations);
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.InRange(output.Statistics.CachedResources, 0, warm.CachedResources + 2);
        }
        Assert.Equal(readbacks, session.Runtime.Readbacks);
    }

    [Theory, InlineData(T8cTestHelpers.StarRailStyleId), InlineData(T8cTestHelpers.WutheringStyleId)]
    [Trait("Category", "Gpu")]
    public void VerticalLightUsesExactOrdinaryFallbackAndFrontTieKeepsOriginalUv(string family)
    {
        using var session = new D3D12FrameSession(); var binding = Binding(family);
        var vertical = Fixture(light: Vector3.UnitY);
        Assert.Equal(session.Capture(vertical, Width, Height, surfaceStyles: Map(family, null)).Rgba,
            session.Capture(vertical, Width, Height, surfaceStyles: Map(family, binding)).Rgba);
        // A frontal tie retains .375 and the high enough gray mask, rather than
        // sampling U=0 (linear Repeat averages black and white at the seam).
        var front = Fixture(light: -Vector3.UnitZ);
        var full = session.Capture(front, Width, Height, surfaceStyles: Map(family, binding));
        AssertPixel(full, Height / 3, new(.6f, .4f, .2f, 1));
        // At this center, a collapsed U=0 would sample the repeated seam gray .5
        // and produce a non-dark value in either family. Retaining .375 stays dark.
        var tie = session.Capture(front, Width, Height, surfaceStyles: Map(family, Binding(family, offset: 1.45f)));
        AssertPixel(tie, Height / 3, new(.12f, .08f, .04f, 1));
        var back = session.Capture(Fixture(light: Vector3.UnitZ), Width, Height, surfaceStyles: Map(family, binding));
        AssertPixel(back, Height / 3, new(.12f, .08f, .04f, 1));
    }

    [Theory, InlineData(T8cTestHelpers.StarRailStyleId), InlineData(T8cTestHelpers.WutheringStyleId)]
    [Trait("Category", "Gpu")]
    public void MaskDataDoesNotBecomeCoverageAndSubTextureRawAlphaRemainsExact(string family)
    {
        using var session = new D3D12FrameSession(); var frame = Fixture(subTexture: true);
        var ordinary = session.Capture(frame, Width, Height, surfaceStyles: Map(family, null));
        var bound = session.Capture(frame, Width, Height, surfaceStyles: Map(family,
            Binding(family, primary: 2, right: -Vector3.UnitX)));
        int changed = 0, covered = 0;
        for (int offset = 0; offset < bound.Rgba.Length; offset += 4)
        {
            Assert.Equal(ordinary.Rgba[offset + 3], bound.Rgba[offset + 3]);
            if (bound.Rgba[offset + 3] != 0) covered++;
            if (ordinary.Rgba[offset] != bound.Rgba[offset]) changed++;
        }
        Assert.True(covered > 300); Assert.True(changed > 200);
        Assert.Equal(ordinary.Rgba, session.Capture(frame, Width, Height, surfaceStyles: Map(family, null)).Rgba);
    }

    [Theory, InlineData(T8cTestHelpers.StarRailStyleId), InlineData(T8cTestHelpers.WutheringStyleId)]
    [Trait("Category", "Gpu")]
    public void IntermediateSdfValuesUseTheAuditedLinearRangesRatherThanSmoothstep(string family)
    {
        using var session = new D3D12FrameSession(); var frame = Fixture();
        float transition = family == T8cTestHelpers.StarRailStyleId ? .3f : .1f;
        float offset = family == T8cTestHelpers.StarRailStyleId ? 1 : 1.27f;
        var actual = session.Capture(frame, Width, Height, surfaceStyles: Map(family, Binding(family, transition: transition, offset: offset)));
        // The exact texel red is 192/255. With Star's halfwidth .5 and center .5,
        // q0 is the texel value itself. MC's halfwidth .1 and center .77 give a
        // separate independently calculated MapRange/ColorRamp interior value.
        float q0 = family == T8cTestHelpers.StarRailStyleId ? 192f / 255 : ((192f / 255 - .77f) + .1f) / .2f;
        float band = family == T8cTestHelpers.StarRailStyleId ? (q0 - .6f) / .3f : (q0 - .35f) / .15f;
        band = Math.Clamp(band, 0, 1);
        var expected = new Vector3(.6f, .4f, .2f) * (.2f + .8f * band);
        AssertPixel(actual, Height / 3, new(expected, 1));
    }

    [Theory, InlineData(T8cTestHelpers.StarRailStyleId), InlineData(T8cTestHelpers.WutheringStyleId)]
    [Trait("Category", "Gpu")]
    public void NativePointUsesPerFragmentTowardLightInsteadOfThePrimaryDirection(string family)
    {
        using var session = new D3D12FrameSession();
        var source = Fixture(light: -Vector3.UnitX);
        var lit = new OriginalFrameScene(source.Surface, options: source.Options, lighting: new([
            new NativeSceneLight(Guid.NewGuid(), "explicit test point", SceneLightKind.Point,
                Vector3.Zero, Quaternion.Identity, Vector3.One, .2f, 20, CastShadows: false)]));
        var map = Map(family, Binding(family));
        var off = session.Capture(source, Width, Height, surfaceStyles: map);
        var on = session.Capture(lit, Width, Height, surfaceStyles: map);
        // The point is on the face plane at X=0. Negative-world-X pixels see +X
        // and sample mirrored white; positive-world-X pixels see -X and dark. Primary -X
        // stays dark throughout, so a primary-direction shortcut cannot pass.
        var bright = Project(source, new(-.6f, .55f, 0)); var dark = Project(source, new(.6f, .55f, 0));
        Assert.True(Pixel(on, bright.X, bright.Y)[0] > Pixel(off, bright.X, bright.Y)[0] + 8);
        Assert.Equal(Pixel(off, dark.X, dark.Y), Pixel(on, dark.X, dark.Y));
        Assert.Equal(off.Rgba, session.Capture(source, Width, Height, surfaceStyles: map).Rgba);
    }

    private static StyleMaterialBindingData Binding(
        string family = T8cTestHelpers.StarRailStyleId,
        int primary = 0,
        int secondary = -1,
        Vector3? forward = null,
        Vector3? right = null,
        float? transition = null,
        float offset = 1f,
        string? encoding = null)
        => T8cTestHelpers.FaceSdfBinding(
            family,
            primary,
            secondary,
            forward ?? -Vector3.UnitZ,
            right ?? Vector3.UnitX,
            transition ?? (family == T8cTestHelpers.WutheringStyleId ? .1f : 1f),
            offset,
            encoding);

    private static D3D12SurfaceStyleMap Map(string family, StyleMaterialBindingData? binding, float strength = 1)
        => T8cTestHelpers.CreateDirectMap(
            [family, family],
            [
                T8cTestHelpers.MaterialWithBinding(family, binding, D3D12CharacterMaterialRole.Face, strength, 1, buildWhenNull: true),
                new(),
            ]);

    private static OriginalFrameScene Fixture(bool invertMask = false, bool subTexture = false, Vector3? light = null)
    {
        var vertices = new List<SceneVertex>();
        foreach (float y in new[] { .55f, -.55f })
            vertices.AddRange([new(new(-.8f, y - .35f, 0), Vector3.UnitX, new(.375f, .5f)),
                new(new(.8f, y - .35f, 0), Vector3.UnitX, new(.375f, .5f)),
                new(new(.8f, y + .35f, 0), Vector3.UnitX, new(.375f, .5f)),
                new(new(-.8f, y + .35f, 0), Vector3.UnitX, new(.375f, .5f))]);
        byte[] mask = [0, 0, 0, 0, 64, 64, 64, 0, 192, 192, 192, 0, 255, 255, 255, 0];
        if (invertMask) for (int i = 0; i < mask.Length; i += 4) mask[i] = mask[i + 1] = mask[i + 2] = (byte)(255 - mask[i]);
        var textures = new List<SceneTexture>();
        if (subTexture) textures.AddRange([new("coverage", 1, 1, [255, 255, 255, 128]), new("subtexture", 1, 1, [128, 128, 128, 128])]);
        textures.Add(new("original asymmetric numerical fixture", 4, 1, mask));
        var binding = new OriginalStyleMaterialBinding(new(.2f), subTexture ? 1 : -1,
            subTexture ? OriginalSphereMode.SubTexture : OriginalSphereMode.None, true);
        if (subTexture) binding = binding with { TextureFactors = OriginalMaterialTextureFactors.Identity with
        { Diffuse = new(new(.8f, .7f, .6f, 1), Vector4.Zero), Toon = new(new(1, 1, 1, .2f), Vector4.Zero) } };
        var additional = subTexture ? new SceneAdditionalUvs(1, Enumerable.Repeat(new SceneAdditionalUvVertex(new(.25f, .25f, 0, 0), default, default, default), 8).ToArray()) : null;
        var scene = new SceneSnapshot(vertices.ToArray(), [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6],
            [new("explicit face", 0, 6, new(.6f, .4f, .2f, 1), Vector3.Zero, Vector3.Zero, 1, subTexture ? 0 : -1),
                new("ordinary body", 6, 6, new(.6f, .4f, .2f, 1), Vector3.Zero, Vector3.Zero, 1, subTexture ? 0 : -1)],
            textures, camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 20), lightDirection: light ?? Vector3.UnitX, additionalUvs: additional);
        return new(new(scene, [binding, binding], new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero));
    }

    private static byte[] Pixel(RenderedFrame frame, int x, int y) => frame.Rgba.AsSpan((y * Width + x) * 4, 4).ToArray();
    private static (int X, int Y) Project(OriginalFrameScene frame, Vector3 position)
    {
        var clip = Vector4.Transform(new Vector4(position, 1), frame.Surface.Snapshot.Camera.ViewProjection(Width, Height));
        return ((int)((clip.X / clip.W * .5f + .5f) * Width), (int)((.5f - clip.Y / clip.W * .5f) * Height));
    }
    private static void AssertPixel(RenderedFrame frame, int y, Vector4 expected)
        => D3D12OriginalFrameTests.AssertPixel(frame, Width / 2, y, expected);
}
