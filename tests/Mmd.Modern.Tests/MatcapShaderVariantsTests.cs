using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class MatcapShaderVariantsTests
{
    public static IEnumerable<object[]> Variants()
    {
        foreach (var variant in D3D12GenshinSurfaceTests.SurfaceVariants())
        foreach (bool hair in new[] { false, true })
        foreach (bool rim in new[] { false, true })
            yield return [.. variant, hair, rim];
    }

    [Theory, MemberData(nameof(Variants))]
    public void EveryNativeGiMorphSubAndCoupledVariantCompilesOnceAtFinalEntry(
        string entry, bool native, bool gi, bool morph, bool sub, bool hair, bool rim)
    {
        var bytes = OriginalFrameShaders.CompileEndfieldMatcapSurface(entry, native, gi, morph, sub, hair, rim);
        Assert.Equal(new byte[] { 68, 88, 66, 67 }, bytes[..4]);
        string source = OriginalFrameShaders.EndfieldMatcapSurfaceSource(entry, native, gi, morph, sub, hair, rim);
        Assert.Contains($"float4 {entry}(SurfaceOutput v,uint primitive:SV_PrimitiveID,bool front:SV_IsFrontFace)", source);
        Assert.Equal(1, source.Split("color.rgb+=EndfieldMatcapDelta(v,primitive,front,color.rgb);", StringSplitOptions.None).Length - 1);
        foreach (string register in new[] { "b10", "t15", "t16", "t17", "t18", "s7" })
            Assert.Contains($"register({register})", source);
        Assert.Contains("MatcapNormalMap.SampleLevel(MatcapSampler,uv,0).rg", source);
        Assert.Contains("sampled.rgb*sampled.a", source);
        Assert.Contains("float3 nc=float3(dot(mapped,MatcapCameraRight.xyz),dot(mapped,MatcapCameraUp.xyz),-dot(mapped,MatcapCameraForward.xyz))", source);
        Assert.DoesNotContain("color.a*=", source);
        if (hair) Assert.Contains("float EndfieldMatcapRadialScalar(SurfaceOutput v)", source);
        if (rim) Assert.Contains("EndfieldRimMask(v,front)", source);
    }

    [Fact]
    public void UnsupportedEntryFailsExplicitlyBeforeShaderCompilation()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            OriginalFrameShaders.EndfieldMatcapSurfaceSource("PSDepth", false, false, false, false));
}
