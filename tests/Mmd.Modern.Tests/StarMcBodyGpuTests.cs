using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class StarMcBodyGpuTests
{
    private const int Size = 97;
    private static readonly D3D12StarMcBodyBinding Binding = new(0, 1, 2, 3,
        D3D12MatcapColorEncoding.LinearRgb, NormalStrength: 1, DetailStrength: 1,
        HighlightCoefficient: .35f, ShadowColorStrength: 1, WarmBandStrength: .35f);

    [Fact, Trait("Category", "Gpu")]
    public void SpatialDetailUsesGreenSoftLightAndPreservesStraightCoverage()
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(detail: new("spatial detail G", 2, 1, [255, 0, 200, 0, 0, 255, 10, 255]));
        var neutral = Binding with { DetailStrength = 0, NormalStrength = 0, HighlightStrength = 0, ShadowColorStrength = 0 };
        var baseline = session.Capture(frame, Size, Size, surfaceStyles: Map(D3D12SurfaceStyle.WutheringPmx, neutral));
        var actual = session.Capture(frame, Size, Size, surfaceStyles: Map(D3D12SurfaceStyle.WutheringPmx, neutral with { DetailStrength = 1 }));
        foreach (int x in new[] { 36, 60 })
        {
            var snapshot = frame.Surface.Snapshot;
            // Orthographic quad is 1.6 units wide; pixel centers are unprojected
            // independently of shader interpolation or generated tangent buffers.
            Matrix4x4.Invert(snapshot.Camera.ViewProjection(Size, Size), out var inverse);
            var p = Vector4.Transform(new Vector4(2f * (x + .5f) / Size - 1, 0, 0, 1), inverse);
            float u = (p.X / p.W + .8f) / 1.6f;
            float g = MatcapOracle.BilinearRepeat(snapshot.Textures[1].Rgba, 2, 1, new(u, .5f), false).Y;
            int at = (48 * Size + x) * 4;
            for (int ch = 0; ch < 3; ch++)
            {
                float c = baseline.Rgba[at + ch] / 255f;
                float expected = c + .5f * (2 * g - 1) * c * (1 - c);
                Assert.InRange(Math.Abs(actual.Rgba[at + ch] - expected * 255), 0, 1.7f);
            }
        }
        AssertCoverage(baseline, actual);
        var ignored = Frame(detail: new("ignored detail RBA", 2, 1, [0, 0, 0, 255, 255, 255, 255, 0]));
        Assert.Equal(actual.Rgba, session.Capture(ignored, Size, Size,
            surfaceStyles: Map(D3D12SurfaceStyle.WutheringPmx, neutral with { DetailStrength = 1 })).Rgba);
    }

    [Fact, Trait("Category", "Gpu")]
    public void McPackedRgChangesBodyLightingAndBlueAlphaNeverBecomeNormalZ()
    {
        using var session = new D3D12FrameSession();
        var binding = Binding with { HighlightStrength = 0, DetailStrength = 0, ShadowCoefficient = .3f, ShadowSoftness = .4f };
        var flat = Frame(normal: new("flat RG", 1, 1, [128, 128, 0, 0]), light: Vector3.UnitX);
        var tilted = Frame(normal: new("tilted RG", 1, 1, [230, 128, 0, 0]), light: Vector3.UnitX);
        // For this planar +X tangent and +X light, packed R is exactly the
        // half-Lambert signal. The old coefficient .8 put both inputs above
        // the soft band; .3 keeps flat RG inside it and tilted RG fully lit.
        float edge = 1 - .3f * (220f / 255);
        float flatBlend = .05f + .95f * Math.Clamp(((128f / 255) - (edge - .4f)) / .4f, 0, 1);
        float tiltedBlend = .05f + .95f * Math.Clamp(((230f / 255) - (edge - .4f)) / .4f, 0, 1);
        Assert.InRange(flatBlend, .42f, .44f);
        Assert.Equal(1f, tiltedBlend);
        var first = session.Capture(flat, Size, Size, surfaceStyles: Map(D3D12SurfaceStyle.WutheringPmx, binding));
        var changed = session.Capture(tilted, Size, Size, surfaceStyles: Map(D3D12SurfaceStyle.WutheringPmx, binding));
        AssertVisibleDifference(first, changed);
        int center = (48 * Size + 48) * 4;
        for (int channel = 0; channel < 3; channel++)
        {
            float shadow = binding.EffectiveShadowColor[channel];
            float expected = changed.Rgba[center + channel] * (shadow + (1 - shadow) * flatBlend);
            Assert.InRange(Math.Abs(first.Rgba[center + channel] - expected), 0, 1.7f);
        }
        var ignored = Frame(normal: new("same RG ignored BA", 1, 1, [230, 128, 255, 255]), light: Vector3.UnitX);
        Assert.Equal(changed.Rgba, session.Capture(ignored, Size, Size, surfaceStyles: Map(D3D12SurfaceStyle.WutheringPmx, binding)).Rgba);
        AssertCoverage(first, changed);
    }

    [Theory, InlineData(D3D12SurfaceStyle.StarRailPmx), InlineData(D3D12SurfaceStyle.WutheringPmx)]
    [Trait("Category", "Gpu")]
    public void CameraNormalMatcapAndExplicitMetalSaturationWorkWithZeroPmxSpecular(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(normalDirection: Vector3.Normalize(new Vector3(.75f, .1f, -1)));
        var binding = Binding with { ShadowColorStrength = 0, DetailStrength = 0, NormalStrength = 0, HighlightCoefficient = 1 };
        var baseline = session.Capture(frame, Size, Size, surfaceStyles: Map(family, binding with { HighlightStrength = 0 }));
        var actual = session.Capture(frame, Size, Size, surfaceStyles: Map(family, binding));
        var rotated = session.Capture(frame, Size, Size, surfaceStyles: Map(family, binding with { RotationRadians = 1.7f, MetallicSaturation = .2f }));
        AssertVisibleDifference(baseline, actual);
        AssertVisibleDifference(actual, rotated);
        AssertCoverage(baseline, actual);
        var ignored = Frame(normalDirection: Vector3.Normalize(new Vector3(.75f, .1f, -1)),
            cap: new("same RGB unused alpha", 2, 2, [20, 190, 80, 255, 230, 50, 150, 0, 80, 10, 240, 255, 250, 220, 30, 0]));
        Assert.Equal(actual.Rgba, session.Capture(ignored, Size, Size, surfaceStyles: Map(family, binding)).Rgba);
    }

    [Theory, InlineData(D3D12SurfaceStyle.StarRailPmx), InlineData(D3D12SurfaceStyle.WutheringPmx)]
    [Trait("Category", "Gpu")]
    public void DirectionalFresnelRimChangesOnlyCoveredColor(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(normalDirection: Vector3.Normalize(new Vector3(.995f, 0, -.1f)));
        var binding = Binding with { DetailStrength = 0, HighlightStrength = 0, ShadowColorStrength = 0,
            RimStrength = .8f, RimSize = .8f, RimDirectionRadians = MathF.PI, RimColor = new(.7f, .3f, .1f) };
        var baseline = session.Capture(frame, Size, Size, surfaceStyles: Map(family, binding with { RimStrength = 0 }));
        var actual = session.Capture(frame, Size, Size, surfaceStyles: Map(family, binding));
        var rotated = session.Capture(frame, Size, Size, surfaceStyles: Map(family, binding with { RimDirectionRadians = 0 }));
        AssertVisibleDifference(baseline, actual);
        AssertVisibleDifference(actual, rotated);
        AssertCoverage(baseline, actual);
    }

    [Theory, InlineData(false, false), InlineData(false, true), InlineData(true, false), InlineData(true, true)]
    [Trait("Category", "Gpu")]
    public void ActualNativeGiPipelinesBindBodyResourcesAndRemoveCleanly(bool native, bool gi)
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(native: native, light: Vector3.Normalize(new Vector3(.6f, 0, -.8f)));
        var options = gi ? new RayGiOptions(LightingMode.SoftwareRayTracing, SamplesPerPixel: 2,
            Seed: 47, Composition: RayGiComposition.ToonPalette) : null;
        foreach (var family in new[] { D3D12SurfaceStyle.StarRailPmx, D3D12SurfaceStyle.WutheringPmx })
        {
            var baseline = session.Capture(frame, Size, Size, options, surfaceStyles: Map(family, null));
            var actual = session.Capture(frame, Size, Size, options, surfaceStyles: Map(family, Binding));
            AssertVisibleDifference(baseline, actual);
            AssertCoverage(baseline, actual);
            Assert.Equal(baseline.Rgba, session.Capture(frame, Size, Size, options, surfaceStyles: Map(family, null)).Rgba);
        }
    }

    private static D3D12SurfaceStyleMap Map(D3D12SurfaceStyle family, D3D12StarMcBodyBinding? binding)
        => new([family], [new(StarMcBody: binding)]);
    private static OriginalFrameScene Frame(SceneTexture? normal = null, SceneTexture? detail = null, SceneTexture? cap = null,
        Vector3? normalDirection = null, Vector3? light = null, bool native = false)
    {
        var n = normalDirection ?? -Vector3.UnitZ;
        SceneVertex[] vertices = [new(new(-.8f,-.8f,0),n,new(0,0)),new(new(.8f,-.8f,0),n,new(1,0)),
            new(new(.8f,.8f,0),n,new(1,1)),new(new(-.8f,.8f,0),n,new(0,1))];
        var material = new SceneMaterial("explicit body",0,6,new(.25f,.17f,.10f,.61f),Vector3.Zero,Vector3.Zero,4);
        SceneTexture[] textures = [normal ?? new("normal RG",1,1,[128,128,255,0]),
            detail ?? new("detail G",1,1,[0,128,0,0]),new("mask RB",1,1,[220,37,190,0]),
            cap ?? new("camera normal cap",2,2,[20,190,80,0,230,50,150,255,80,10,240,0,250,220,30,255])];
        var scene = new SceneSnapshot(vertices,[0,2,1,0,3,2],[material],textures,
            camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,2.6f,.1f,30),lightDirection:light ?? -Vector3.UnitZ);
        return new(new(scene,[new(new(.2f),DoubleSided:true)],new(Vector3.One,Vector3.One,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),
            options:new(OriginalAlphaBlendMode.Normal,true,false,BackgroundColor:Vector4.Zero,AlphaOutput:FrameAlphaOutput.Straight),
            lighting:native?new([new NativeSceneLight(Guid.NewGuid(),"fill",SceneLightKind.Directional,Vector3.Zero,
                Quaternion.Identity,new(.3f,.4f,.5f),.2f,30,CastShadows:false)]):null);
    }
    private static void AssertVisibleDifference(RenderedFrame a, RenderedFrame b)
    {
        int changed = 0;
        for (int i=0;i<a.Rgba.Length;i+=4)
            if (!a.Rgba.AsSpan(i,3).SequenceEqual(b.Rgba.AsSpan(i,3))) changed++;
        Assert.True(changed > 100, $"Only {changed} covered pixels changed.");
    }
    private static void AssertCoverage(RenderedFrame a, RenderedFrame b)
    {
        for(int i=0;i<a.Rgba.Length;i+=4)
        {
            Assert.Equal(a.Rgba[i+3],b.Rgba[i+3]);
            if(b.Rgba[i+3]==0) Assert.Equal(new byte[4],b.Rgba.AsSpan(i,4).ToArray());
        }
    }
}
