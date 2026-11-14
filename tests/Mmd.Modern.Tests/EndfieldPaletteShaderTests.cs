using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldPaletteShaderTests
{
    public static IEnumerable<object[]> Variants()
    {
        foreach (var variant in D3D12GenshinSurfaceTests.SurfaceVariants())
        foreach (bool hair in new[] { false, true })
        foreach (bool rim in new[] { false, true })
            yield return [.. variant, hair, rim];
    }

    [Theory, MemberData(nameof(Variants))]
    public void All48NativeGiMorphSubHairRimVariantsCompileAtSingleFinalEntry(
        string entry, bool native, bool gi, bool morph, bool sub, bool hair, bool rim)
    {
        byte[] bytes = OriginalFrameShaders.CompileEndfieldPaletteSurface(entry, native, gi, morph, sub, hair, rim);
        Assert.Equal(new byte[] { 68, 88, 66, 67 }, bytes[..4]);
        string source = OriginalFrameShaders.EndfieldPaletteSurfaceSource(entry, native, gi, morph, sub, hair, rim);
        string old = OriginalFrameShaders.EndfieldMatcapSurfaceSource(entry, native, gi, morph, sub, hair, rim);
        Assert.DoesNotContain("PaletteEmission", old);
        Assert.Equal(old, OriginalFrameShaders.EndfieldMatcapSurfaceSource(entry, native, gi, morph, sub, hair, rim));
        foreach (string slot in new[] { "b13", "t22", "t23", "s7" }) Assert.Contains($"register({slot})", source);
        Assert.Equal(1, Count(source, "color.rgb+=EndfieldMatcapDelta(v,primitive,front,color.rgb);"));
        Assert.Equal(2, Count(source, "EndfieldMatcapMappedNormal(")); // declaration and the existing call only
        Assert.Equal(1, Count(source, "return EndfieldMatcapComposeDelta(v,primitive,front,changed,uv,baseGeometric,basePerturbed,k,rangeQ,currentColor);"));
        Assert.Contains("PaletteEmissionMap.SampleLevel(MatcapSampler,uv,0).b", source);
        Assert.Contains("PaletteReceivedLightMap.SampleLevel(MatcapSampler,uv,0).rgb", source);
        Assert.Contains("partial+=(initial*(2*blue))*PaletteEmission.x;", source);
        Assert.Contains("return emission-initial*MatcapBaseBaseline.w-oldHair-oldRim;", source);
        Assert.Contains("float3 oldHair=initial*p;", source);
        Assert.Contains("*(MatcapBaseBaseline.w+r)-initial*MatcapBaseBaseline.w", source);
        Assert.Contains("precise float x=(u*scale+.000001)*.999999", source);
        Assert.Contains("saturation=saturate(saturation)", source);
        Assert.DoesNotContain("color.a*=", source);
        Assert.DoesNotContain("PaletteEmissionMap.SampleLevel(MatcapSampler,uv,0).a", source);
        if (!hair) Assert.DoesNotContain("RAW_RADIAL", source);
    }

    [Fact]
    public void HostConstantAbiMatchesThreeShaderVectors()
    {
        var host = typeof(D3D12OriginalStyleRenderer).GetNestedType("EndfieldPaletteConstants",
            System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(48, System.Runtime.InteropServices.Marshal.SizeOf(host));
        var binding = new D3D12EndfieldPaletteBinding(new(.2f,.3f,.4f), .6f,.7f,.8f,.9f);
        object value = host.GetMethod("From", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [binding,null])!;
        Assert.Equal(new System.Numerics.Vector4(.6f,.7f,.9f,0), host.GetProperty("Emission")!.GetValue(value));
        Assert.Equal(new System.Numerics.Vector4(.2f,.3f,.4f,.8f), host.GetProperty("Light")!.GetValue(value));
        Assert.Equal(System.Numerics.Vector4.Zero, host.GetProperty("Modes")!.GetValue(value));
    }

    [Fact]
    public void UnsupportedEntryFailsBeforeCompilation()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            OriginalFrameShaders.EndfieldPaletteSurfaceSource("PSDepth", false, false, false, false));

    private static int Count(string source, string text) => source.Split(text, StringSplitOptions.None).Length - 1;
}
