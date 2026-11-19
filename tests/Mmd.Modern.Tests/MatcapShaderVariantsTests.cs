using Xunit;

namespace Mmd.Modern.Tests;

public sealed class MatcapShaderVariantsTests
{
    public static IEnumerable<object[]> Variants()
    {
        foreach (var variant in T8dTestHelpers.SurfaceVariants())
        foreach (bool hair in new[] { false, true })
        foreach (bool rim in new[] { false, true })
            yield return [.. variant, hair, rim];
    }

    [Theory, MemberData(nameof(Variants))]
    public void EveryNativeGiMorphSubAndCoupledVariantCompilesOnceAtFinalEntry(
        string entry, bool native, bool gi, bool morph, bool sub, bool hair, bool rim)
    {
        string moduleId = T8dTestHelpers.ResolveEndfieldModuleId(hair, rim, matcap: true, palette: false, baseImage: false);
        var bytes = T8dTestHelpers.CompileEndfieldPixel(moduleId, entry, native, gi, morph, sub);
        Assert.Equal(new byte[] { 68, 88, 66, 67 }, bytes[..4]);
        string source = T8dTestHelpers.AssembleEndfieldPixelSource(moduleId, entry, native, gi, morph, sub);
        Assert.Contains($"float4 {entry}(SurfaceOutput v, uint mdaPrimitive : SV_PrimitiveID, bool mdaFront : SV_IsFrontFace)", source);
        Assert.Equal(1, source.Split("color.rgb += EndfieldMatcapDelta(v, mdaPrimitive, mdaFront, camRight, camUp, camForward, camBackward, isOrtho);", StringSplitOptions.None).Length - 1);
        foreach (string register in new[] { "b0, space2", "t1, space2", "t2, space2", "t3, space2", "t12, space2", "s0, space2" })
            Assert.Contains($"register({register})", source);
        Assert.Contains("MatcapNormalMap.SampleLevel(MdaLinearWrapSampler, uv, 0).rg", source);
        Assert.Contains("capRgb * sampled.a", source);
        Assert.Contains("float3 nc = float3(dot(mapped, camRight), dot(mapped, camUp), -dot(mapped, camForward));", source);
        Assert.DoesNotContain("color.a*=", source);
        Assert.DoesNotContain("color.a *=", source);
        if (hair) Assert.Contains("float EndfieldRadialScalar(SurfaceOutput v, float3 camBackward, bool isOrtho)", source);
        if (rim) Assert.Contains("EndfieldRimMask(v, front, camRight, camUp, camForward, camBackward, isOrtho)", source);
    }

    [Fact]
    public void UnsupportedEntryFailsExplicitlyBeforeShaderCompilation()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            T8dTestHelpers.AssembleEndfieldPixelSource("matcap", "PSDepth", false, false, false, false));
}
