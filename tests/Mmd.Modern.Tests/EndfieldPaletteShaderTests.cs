using System.Numerics;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldPaletteShaderTests
{
    public static IEnumerable<object[]> Variants()
    {
        foreach (var variant in T8dTestHelpers.SurfaceVariants())
        foreach (bool hair in new[] { false, true })
        foreach (bool rim in new[] { false, true })
            yield return [.. variant, hair, rim];
    }

    [Theory, MemberData(nameof(Variants))]
    public void All48NativeGiMorphSubHairRimVariantsCompileAtSingleFinalEntry(
        string entry, bool native, bool gi, bool morph, bool sub, bool hair, bool rim)
    {
        string moduleId = T8dTestHelpers.ResolveEndfieldModuleId(hair, rim, matcap: true, palette: true, baseImage: false);
        string oldModuleId = T8dTestHelpers.ResolveEndfieldModuleId(hair, rim, matcap: true, palette: false, baseImage: false);
        byte[] bytes = T8dTestHelpers.CompileEndfieldPixel(moduleId, entry, native, gi, morph, sub);
        Assert.Equal(new byte[] { 68, 88, 66, 67 }, bytes[..4]);
        string source = T8dTestHelpers.AssembleEndfieldPixelSource(moduleId, entry, native, gi, morph, sub);
        string old = T8dTestHelpers.AssembleEndfieldPixelSource(oldModuleId, entry, native, gi, morph, sub);
        Assert.DoesNotContain("PaletteEmission", old);
        Assert.Equal(old, T8dTestHelpers.AssembleEndfieldPixelSource(oldModuleId, entry, native, gi, morph, sub));
        foreach (string slot in new[] { "b0, space2", "t4, space2", "t5, space2", "s0, space2" }) Assert.Contains($"register({slot})", source);
        Assert.Equal(1, Count(source, "color.rgb += EndfieldMatcapDelta(v, mdaPrimitive, mdaFront, camRight, camUp, camForward, camBackward, isOrtho);"));
        Assert.Equal(2, Count(source, "EndfieldMatcapMappedNormal(")); // declaration and the existing call only
        Assert.Equal(1, Count(source, "return EndfieldMatcapComposeDelta(v, primitive, front, changed, uv, baseGeometric, basePerturbed, k, rangeQ, camRight, camUp, camForward, camBackward, isOrtho);"));
        Assert.Contains("PaletteEmissionMap.SampleLevel(MdaLinearWrapSampler, uv, 0).b", source);
        Assert.Contains("PaletteReceivedLightMap.SampleLevel(MdaLinearWrapSampler, uv, 0).rgb", source);
        Assert.Contains("partial += (initial * (2 * blue)) * PaletteEmission.x;", source);
        Assert.Contains("return emission - initial * MatcapBaseBaseline.w - oldHair - oldRim;", source);
        Assert.Contains("float3 oldHair = MatcapBaseBaseline.rgb * p;", source);
        Assert.Contains("* (MatcapBaseBaseline.w + r) - MatcapBaseBaseline.rgb * MatcapBaseBaseline.w", source);
        Assert.Contains("precise float x = (u * scale + .000001) * .999999;", source);
        Assert.Contains("saturation = saturate(saturation);", source);
        Assert.DoesNotContain("color.a*=", source);
        Assert.DoesNotContain("color.a *=", source);
        Assert.DoesNotContain("PaletteEmissionMap.SampleLevel(MdaLinearWrapSampler, uv, 0).a", source);
        if (hair) Assert.Contains("#define MDA_STYLE_GEOM_RAW_RADIAL_SEEDS 1", source);
        else Assert.DoesNotContain("MDA_STYLE_GEOM_RAW_RADIAL_SEEDS", source);
    }

    [Fact]
    public void HostConstantAbiMatchesThreeShaderVectors()
    {
        var matcap = T8dTestHelpers.EndfieldMatcap(coupleEndfieldPalette: true);
        var palette = new EndfieldPaletteSpec(new(.2f, .3f, .4f), .6f, .7f, .8f, .9f).ToBinding();
        var draw = T8dTestHelpers.BuildEndfieldDrawData([matcap, palette])!;
        var vecs = T8dTestHelpers.ReadCbvVectors(draw);
        Assert.Equal(10, vecs.Length); // 7 matcap vectors + 3 palette vectors
        Assert.Equal(new Vector4(.6f, .7f, .9f, 0), vecs[7]);
        Assert.Equal(new Vector4(.2f, .3f, .4f, .8f), vecs[8]);
        Assert.Equal(Vector4.Zero, vecs[9]);
    }

    [Fact]
    public void UnsupportedEntryFailsBeforeCompilation()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            T8dTestHelpers.AssembleEndfieldPixelSource("matcap-palette", "PSDepth", false, false, false, false));

    private static int Count(string source, string text) => source.Split(text, StringSplitOptions.None).Length - 1;
}
