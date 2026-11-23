using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12PmxGenericHairTests
{
    private const int Width = 129, Height = 97;
    private static readonly Vector3 Color = new(.4f, .2f, .1f);

    public static IEnumerable<object[]> Families()
    {
        foreach (var family in T8cTestHelpers.ModernStyleIds) yield return [family];
    }

    public static IEnumerable<object[]> InvalidHairParameters()
    {
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            yield return [new D3D12PmxMaterialTreatment(HairHighlightStrength: value), "HairHighlightStrength"];
            yield return [new D3D12PmxMaterialTreatment(HairHighlightWidth: value), "HairHighlightWidth"];
        }
        yield return [new D3D12PmxMaterialTreatment(HairHighlightStrength: -.001f), "HairHighlightStrength"];
        yield return [new D3D12PmxMaterialTreatment(HairHighlightStrength: .401f), "HairHighlightStrength"];
        yield return [new D3D12PmxMaterialTreatment(HairHighlightWidth: .019f), "HairHighlightWidth"];
        yield return [new D3D12PmxMaterialTreatment(HairHighlightWidth: .401f), "HairHighlightWidth"];
    }

    [Theory, MemberData(nameof(InvalidHairParameters))]
    public void HairControlsRejectNonFiniteAndOutOfRangeInputs(D3D12PmxMaterialTreatment treatment, string parameter)
        => Assert.Equal(parameter, Assert.Throws<ArgumentOutOfRangeException>(treatment.Validate).ParamName);

    [Theory, InlineData(0, .02f), InlineData(.4f, .4f)]
    public void InclusiveHairBoundsValidateWithoutClamping(float strength, float width)
    {
        var treatment = new D3D12PmxMaterialTreatment(HairHighlightStrength: strength, HairHighlightWidth: width);
        treatment.Validate();
        Assert.Equal(strength, treatment.HairHighlightStrength);
        Assert.Equal(width, treatment.HairHighlightWidth);
    }

    [Fact]
    public void InactiveWidthIsNotConsumedWhileActiveStrengthAndWidthAre()
    {
        var off = new D3D12PmxMaterialTreatment();
        Assert.Equal(0, off.HairHighlightStrength);
        Assert.Equal(.12f, off.HairHighlightWidth);
        Assert.True(D3D12PmxMaterialTreatment.ConsumedEquivalent(off, off with { HairHighlightWidth = .4f }));
        var on = off with { HairHighlightStrength = .3f };
        Assert.False(D3D12PmxMaterialTreatment.ConsumedEquivalent(off, on));
        Assert.True(D3D12PmxMaterialTreatment.ConsumedEquivalent(on, on with { }));
        Assert.False(D3D12PmxMaterialTreatment.ConsumedEquivalent(on, on with { HairHighlightWidth = .4f }));
        Assert.False(D3D12PmxMaterialTreatment.ConsumedEquivalent(on, on with { HairHighlightStrength = .2f }));
    }

    [Theory, MemberData(nameof(Families)), Trait("Category", "Gpu")]
    public void OrientedUvProducesANonzeroBandOnZeroSpecularHairAndPreservesStraightAlpha(string family)
    {
        using var session = new D3D12FrameSession();
        var frame = Surface(alpha: .6f);
        var baseline = session.Capture(frame, Width, Height, surfaceStyles: Map(family));
        var actual = session.Capture(frame, Width, Height, surfaceStyles: Map(family, .3f));
        // At the center T=Y, h=n=l=-Z: lobe and band equal one.
        AssertPixel(baseline, Color, .6f);
        AssertPixel(actual, Color * 1.3f, .6f);
        Assert.True(Pixel(actual)[0] > Pixel(baseline)[0] + 20);
        AssertCoverage(baseline, actual);
        Assert.Equal(baseline.Rgba, session.Capture(frame, Width, Height,
            surfaceStyles: Map(family, 0, .4f)).Rgba);
    }

    [Theory, MemberData(nameof(Families)), Trait("Category", "Gpu")]
    public void SelectedPresetHonorsHairBandWidthAndStrengthOnTheStyledPigment(string family)
    {
        using var session = new D3D12FrameSession();
        var objectId = Guid.NewGuid();
        RenderedFrame Draw(OriginalFrameScene frame, float strength, float width)
        {
            var styled = D3D12ViewRenderingStyles.Resolve(frame, TestStylePlugins.AllPresetCatalog,
                [new SceneRenderingStyleBinding(objectId, new(family), ModelRole.Character, 0, frame.Materials.Count, true)],
                [new D3D12ViewCharacterMaterial(objectId, 0, D3D12CharacterMaterialRole.Hair,
                    PmxTreatment: new(HairHighlightStrength: strength, HairHighlightWidth: width))],
                styleCatalog: TestStylePlugins.AllViewCatalog);
            return session.Capture(styled.Scene, Width, Height, surfaceStyles: styled.SurfaceStyles);
        }

        // At the central texel the half-vector has a nonzero tangent component. Only the wide band reaches it.
        var light = new Vector3(0, .6f, -.8f);
        var frame = Surface(alpha: .6f, lightDirection: light);
        var off = Draw(frame, 0, .4f);
        var narrow = Draw(frame, .4f, .02f);
        var wide = Draw(frame, .4f, .4f);
        var before = Pixel(off);
        var after = Pixel(wide);
        Assert.Equal(before, Pixel(narrow));
        Assert.True(after[0] >= before[0] + 10, $"{family}: {before[0]} -> {after[0]}");
        Assert.InRange(after[1] / (float)after[0], before[1] / (float)before[0] - .015f,
            before[1] / (float)before[0] + .015f);
        Assert.InRange(after[2] / (float)after[0], before[2] / (float)before[0] - .015f,
            before[2] / (float)before[0] + .015f);
        AssertCoverage(off, wide);

        var shadow = Surface(shadow: true, lightDirection: light);
        Assert.Equal(Pixel(Draw(shadow, 0, .4f)), Pixel(Draw(shadow, .4f, .4f)));
    }

    [Theory, MemberData(nameof(Families)), Trait("Category", "Gpu")]
    public void DegenerateUvAndProjectedTangentPassThroughExactly(string family)
    {
        using var session = new D3D12FrameSession();
        foreach (var frame in new[] { Surface(degenerateUv: true), Surface(projectedTangentDegenerate: true) })
        {
            var baseline = session.Capture(frame, Width, Height, surfaceStyles: Map(family));
            var actual = session.Capture(frame, Width, Height, surfaceStyles: Map(family, .4f));
            Assert.Equal(baseline.Rgba, actual.Rgba);
            Assert.True(Pixel(actual)[0] > 0, "An exact pass must retain visible surface color.");
        }
    }

    [Theory, MemberData(nameof(Families)), Trait("Category", "Gpu")]
    public void TinyAuthoredUvScalePreservesTheSameNonzeroBand(string family)
    {
        using var session = new D3D12FrameSession();
        var map = Map(family, .3f);
        var ordinary = session.Capture(Surface(), Width, Height, surfaceStyles: map);
        var tinyUv = session.Capture(Surface(uvScale: 1e-5f), Width, Height, surfaceStyles: map);
        AssertPixel(ordinary, Color * 1.3f);
        AssertPixel(tinyUv, Color * 1.3f);
        Assert.Equal(Pixel(ordinary), Pixel(tinyUv));
        AssertCoverage(ordinary, tinyUv);
    }

    [Theory, MemberData(nameof(Families)), Trait("Category", "Gpu")]
    public void ComputedSelfShadowSuppressesTheProceduralBandWithTheAuthoredHighlight(string family)
    {
        using var session = new D3D12FrameSession();
        var frame = Surface(shadow: true);
        string[] families = [family, T8cTestHelpers.MmdStyleId];
        D3D12SurfaceStyleMap ShadowMap(float strength) => T8cTestHelpers.CreateDirectMap(families,
            [new(D3D12CharacterMaterialRole.Hair, PmxMaterialTreatment: new(HairHighlightStrength: strength)), new()]);
        var baseline = session.Capture(frame, Width, Height, surfaceStyles: ShadowMap(0));
        var actual = session.Capture(frame, Width, Height, surfaceStyles: ShadowMap(.3f));
        AssertPixel(baseline, Color * .2f);
        Assert.Equal(Pixel(baseline), Pixel(actual));
        var noCaster = new OriginalFrameScene(frame.Surface, options: frame.Options,
            materials: [frame.Materials[0], frame.Materials[1] with { CastSelfShadow = false }]);
        var lit = session.Capture(noCaster, Width, Height, surfaceStyles: ShadowMap(.3f));
        AssertPixel(lit, Color * 1.3f);
        AssertCoverage(baseline, actual);
    }

    [Theory, MemberData(nameof(Families))]
    public void ShaderGuardsDerivativeDegeneracyAndKeepsExistingHighlightVisibility(string family)
    {
        string source = T8cTestHelpers.AssemblePluginSurfaceSource(family, "default", "PSSurface", false, false, false, false);
        Assert.Contains("if(abs(determinant)<1e-8) return 0;", source);
        Assert.Contains("if(uvScale<=0) return 0;", source);
        Assert.Contains("float2 ux=duvdx/uvScale, uy=duvdy/uvScale;", source);
        Assert.Contains("float3 tangent=dpdy*ux.x-dpdx*uy.x;", source);
        Assert.Contains("if(tangentScale<=0) return 0;", source);
        Assert.Contains("tangent/=tangentScale;", source);
        Assert.Contains("if(length2<1e-12) return 0;", source);
        Assert.Contains("float3 tint=max(baseColor,0)/max(1,max(baseColor.r,max(baseColor.g,baseColor.b)));", source);
        Assert.True(source.IndexOf("if(length2<1e-12) return 0;", StringComparison.Ordinal)
            < source.IndexOf("tangent*=rsqrt(length2);", StringComparison.Ordinal));
        Assert.Contains("if (PmxMaterialTreatmentParams.z > 0) highlight += PmxGenericHair(v, n, h, l, baseColor, hairBand);", source);
        Assert.Contains("+ highlight * originalC;", source);
    }

    private static OriginalFrameScene Surface(float alpha = 1, bool degenerateUv = false,
        bool projectedTangentDegenerate = false, bool shadow = false, float uvScale = 1, Vector3? lightDirection = null)
    {
        var normal = projectedTangentDegenerate ? Vector3.UnitY : -Vector3.UnitZ;
        SceneVertex[] quad = [new(new(-.8f, -.6f, 0), normal, new(0, 0)), new(new(.8f, -.6f, 0), normal, new(1, 0)),
            new(new(.8f, .6f, 0), normal, new(1, 1)), new(new(-.8f, .6f, 0), normal, new(0, 1))];
        if (degenerateUv) quad = quad.Select(v => v with { Uv = new(.25f) }).ToArray();
        else if (uvScale != 1) quad = quad.Select(v => v with { Uv = v.Uv * uvScale }).ToArray();
        var vertices = shadow ? quad.Concat(quad.Select(v => v with { Position = v.Position + Vector3.UnitZ })).ToArray() : quad;
        uint[] indices = shadow ? [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6] : [0, 2, 1, 0, 3, 2];
        var material = new SceneMaterial("explicit generic hair", 0, 6, new(Color, alpha), Vector3.Zero, Vector3.Zero, 1);
        var scene = new SceneSnapshot(vertices, indices, shadow ? [material, material with { StartIndex = 6 }] : [material],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 20), lightDirection: lightDirection ?? -Vector3.UnitZ);
        var bindings = Enumerable.Repeat(new OriginalStyleMaterialBinding(new(.2f), DoubleSided: true), shadow ? 2 : 1);
        var projection = Matrix4x4.CreateLookAt(new(0, 0, 5), Vector3.Zero, Vector3.UnitY)
            * Matrix4x4.CreateOrthographic(4, 4, .1f, 20);
        return new(new(scene, bindings, new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero,
                SelfShadow: shadow ? new(projection, 64, 64, true, false, OriginalTextureSampling.PointClamp, OriginalDepthComparison.LessEqual) : null,
                AlphaOutput: FrameAlphaOutput.Straight),
            materials: shadow ? [new(false, true, false), new(true, false, false, SurfaceEnabled: false, ShadowDoubleSided: true)]
                : [new(false, false, false)]);
    }

    private static D3D12SurfaceStyleMap Map(string family, float strength = 0, float width = .12f)
        => T8cTestHelpers.CreateDirectMap([family], [new(D3D12CharacterMaterialRole.Hair,
            PmxMaterialTreatment: new(HairHighlightStrength: strength, HairHighlightWidth: width))]);
    private static byte[] Pixel(RenderedFrame frame) => frame.Rgba.AsSpan((Height / 2 * Width + Width / 2) * 4, 4).ToArray();
    private static void AssertPixel(RenderedFrame frame, Vector3 rgb, float alpha = 1)
    {
        var pixel = Pixel(frame);
        for (int channel = 0; channel < 4; channel++)
        {
            float expected = channel == 3 ? alpha : rgb[channel];
            Assert.True(Math.Abs(pixel[channel] - Math.Clamp(expected, 0, 1) * 255) <= 1.1f,
                $"channel {channel}: actual=({string.Join(',', pixel)}), expected=({rgb}, {alpha}) * 255");
        }
    }
    private static void AssertCoverage(RenderedFrame a, RenderedFrame b)
    { for (int i = 3; i < a.Rgba.Length; i += 4) Assert.Equal(a.Rgba[i], b.Rgba[i]); }
}
