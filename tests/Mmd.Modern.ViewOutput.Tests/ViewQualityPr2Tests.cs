using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class ViewQualityPr2Tests
{
    [Fact]
    public void FrameAntialiasingEnumPreservesLegacyValuesAndAppendsNewModes()
    {
        Assert.Equal(0, (int)FrameAntialiasing.Off);
        Assert.Equal(1, (int)FrameAntialiasing.Smaa1X);
        Assert.Equal(2, (int)FrameAntialiasing.Fxaa);
        Assert.Equal(3, (int)FrameAntialiasing.Taa);
        Assert.Equal(4, (int)FrameAntialiasing.Msaa2X);
        Assert.Equal(5, (int)FrameAntialiasing.Msaa4X);
        Assert.Equal(6, (int)FrameAntialiasing.Msaa8X);

        Assert.Equal(1, OriginalFrameMath.MsaaSampleCount(FrameAntialiasing.Off));
        Assert.Equal(1, OriginalFrameMath.MsaaSampleCount(FrameAntialiasing.Smaa1X));
        Assert.Equal(1, OriginalFrameMath.MsaaSampleCount(FrameAntialiasing.Fxaa));
        Assert.Equal(1, OriginalFrameMath.MsaaSampleCount(FrameAntialiasing.Taa));
        Assert.Equal(2, OriginalFrameMath.MsaaSampleCount(FrameAntialiasing.Msaa2X));
        Assert.Equal(4, OriginalFrameMath.MsaaSampleCount(FrameAntialiasing.Msaa4X));
        Assert.Equal(8, OriginalFrameMath.MsaaSampleCount(FrameAntialiasing.Msaa8X));

        Assert.False(OriginalFrameMath.IsMsaa(FrameAntialiasing.Taa));
        Assert.True(OriginalFrameMath.IsMsaa(FrameAntialiasing.Msaa2X));
        Assert.True(OriginalFrameMath.IsMsaa(FrameAntialiasing.Msaa4X));
        Assert.True(OriginalFrameMath.IsMsaa(FrameAntialiasing.Msaa8X));
    }

    [Fact]
    public void DefaultsKeepExactLegacyBehavior()
    {
        var quality = new ViewQualityOptions();
        Assert.False(quality.EffectiveSharpening.Enabled);
        Assert.Equal(0.2f, quality.EffectiveSharpening.Sharpness);
        Assert.Equal(1, quality.EffectiveAnisotropicFiltering);
        Assert.Null(quality.ShadowFilter);
        Assert.Equal(1f, quality.EffectiveShadowSoftness);
        Assert.False(quality.EffectiveScreenSpaceAo.Enabled);
        Assert.Equal(0.4f, quality.EffectiveScreenSpaceAo.Strength);
        Assert.Equal(1.5f, quality.EffectiveScreenSpaceAo.Radius);
        Assert.Equal(8, quality.EffectiveScreenSpaceAo.SampleCount);

        var frame = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false);
        Assert.Equal(1, frame.AnisotropicFiltering);
        Assert.Null(frame.ShadowFilter);
        Assert.Equal(1f, frame.ShadowSoftness);
        Assert.Null(frame.ScreenSpaceAo);
        OriginalFrameMath.Validate(frame);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)] [InlineData(8)] [InlineData(16)]
    public void ValidAnisotropicLevelsAreAccepted(int level)
    {
        new ViewQualityOptions { AnisotropicFiltering = level }.Validate();
        var frame = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false) { AnisotropicFiltering = level };
        OriginalFrameMath.Validate(frame);
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(3)] [InlineData(6)] [InlineData(12)] [InlineData(32)]
    public void InvalidAnisotropicLevelsAreRejected(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewQualityOptions { AnisotropicFiltering = level }.Validate());
        var frame = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false) { AnisotropicFiltering = level };
        Assert.Throws<ArgumentException>(() => OriginalFrameMath.Validate(frame));
    }

    [Theory]
    [InlineData(0f)] [InlineData(0.2f)] [InlineData(1f)] [InlineData(2f)]
    public void ValidSharpeningBoundsAreAccepted(float sharpness)
    {
        new ViewSharpeningOptions(true, sharpness).Validate();
        new ViewQualityOptions { Sharpening = new(true, sharpness) }.Validate();
    }

    [Theory]
    [InlineData(-0.01f)] [InlineData(2.01f)] [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)]
    public void InvalidSharpeningBoundsAreRejected(float sharpness)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewSharpeningOptions(true, sharpness).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewQualityOptions { Sharpening = new(false, sharpness) }.Validate());
    }

    [Theory]
    [InlineData(0f)] [InlineData(1f)] [InlineData(4f)]
    public void ValidShadowSoftnessAndFiltersAreAccepted(float softness)
    {
        foreach (var filter in Enum.GetValues<ShadowFilterMode>())
        {
            new ViewQualityOptions { ShadowFilter = filter, ShadowSoftness = softness }.Validate();
            var shadow = new OriginalSelfShadowOptions(Matrix4x4.Identity, 1024, 1024, true, false,
                OriginalTextureSampling.LinearClamp, OriginalDepthComparison.LessEqual)
            { FilterMode = filter, ShadowSoftness = softness };
            var frame = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false, SelfShadow: shadow)
            { ShadowFilter = filter, ShadowSoftness = softness };
            OriginalFrameMath.Validate(frame);
        }
    }

    [Theory]
    [InlineData(-0.1f)] [InlineData(4.1f)] [InlineData(float.NaN)] [InlineData(float.NegativeInfinity)]
    public void InvalidShadowSoftnessIsRejected(float softness)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewQualityOptions { ShadowSoftness = softness }.Validate());
        var frame = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false) { ShadowSoftness = softness };
        Assert.Throws<ArgumentException>(() => OriginalFrameMath.Validate(frame));
    }

    [Fact]
    public void InvalidShadowFilterEnumIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewQualityOptions { ShadowFilter = (ShadowFilterMode)99 }.Validate());
        var frame = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false) { ShadowFilter = (ShadowFilterMode)99 };
        Assert.Throws<ArgumentException>(() => OriginalFrameMath.Validate(frame));
    }

    [Fact]
    public void SelfShadowEffectiveFilterDerivesFromSamplingWhenUnset()
    {
        var point = new OriginalSelfShadowOptions(Matrix4x4.Identity, 1024, 1024, true, false,
            OriginalTextureSampling.PointClamp, OriginalDepthComparison.LessEqual);
        var linear = point with { Sampling = OriginalTextureSampling.LinearClamp };
        var pcf = linear with { FilterMode = ShadowFilterMode.PcfSoft, ShadowSoftness = 2.5f };

        Assert.Equal(ShadowFilterMode.Point, point.EffectiveFilterMode);
        Assert.Equal(ShadowFilterMode.Linear, linear.EffectiveFilterMode);
        Assert.Equal(ShadowFilterMode.PcfSoft, pcf.EffectiveFilterMode);

        var applied = new ViewQualityOptions { ShadowFilter = ShadowFilterMode.PcfSoft, ShadowSoftness = 2.0f }
            .ApplyTo(new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false, SelfShadow: point));
        Assert.Equal(ShadowFilterMode.PcfSoft, applied.SelfShadow!.EffectiveFilterMode);
        Assert.Equal(OriginalTextureSampling.LinearClamp, applied.SelfShadow.Sampling);
        Assert.Equal(2.0f, applied.SelfShadow.ShadowSoftness);

        // Overriding only ShadowFilter must preserve a scene SelfShadow's non-default ShadowSoftness (e.g. 2.5f).
        var filterOnlyApplied = new ViewQualityOptions { ShadowFilter = ShadowFilterMode.PcfSoft }
            .ApplyTo(new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false, SelfShadow: pcf));
        Assert.Equal(ShadowFilterMode.PcfSoft, filterOnlyApplied.SelfShadow!.EffectiveFilterMode);
        Assert.Equal(2.5f, filterOnlyApplied.SelfShadow.ShadowSoftness);
        Assert.Equal(2.5f, filterOnlyApplied.ResolveShadowSoftness(filterOnlyApplied.SelfShadow));

        // Explicitly overriding ShadowSoftness back to 1.0f replaces the scene value.
        var explicitReset = new ViewQualityOptions { ShadowFilter = ShadowFilterMode.PcfSoft, ShadowSoftness = 1.0f }
            .ApplyTo(new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false, SelfShadow: pcf));
        Assert.Equal(1.0f, explicitReset.SelfShadow!.ShadowSoftness);
        Assert.Equal(1.0f, explicitReset.ResolveShadowSoftness(explicitReset.SelfShadow));
    }

    [Fact]
    public void ScreenSpaceAoRejectsDisabledDepthWrites()
    {
        var noDepth = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, WriteDepth: false, Transp: false)
        {
            ScreenSpaceAo = new(Enabled: true, Strength: 0.4f),
        };
        Assert.Throws<NotSupportedException>(() => OriginalFrameMath.Validate(noDepth));
    }

    [Theory]
    [InlineData(0f, 0.1f, 4)]
    [InlineData(0.4f, 1.5f, 8)]
    [InlineData(1f, 8f, 16)]
    public void ValidScreenSpaceAoOptionsAreAccepted(float strength, float radius, int samples)
    {
        var ssao = new ScreenSpaceAoOptions(true, strength, radius, samples);
        ssao.Validate();
        new ViewQualityOptions { ScreenSpaceAo = ssao }.Validate();
    }

    [Theory]
    [InlineData(-0.01f, 1.5f, 8)]
    [InlineData(1.01f, 1.5f, 8)]
    [InlineData(float.NaN, 1.5f, 8)]
    [InlineData(0.4f, 0.05f, 8)]
    [InlineData(0.4f, 8.1f, 8)]
    [InlineData(0.4f, float.PositiveInfinity, 8)]
    [InlineData(0.4f, 1.5f, 2)]
    [InlineData(0.4f, 1.5f, 6)]
    [InlineData(0.4f, 1.5f, 32)]
    public void InvalidScreenSpaceAoOptionsAreRejected(float strength, float radius, int samples)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenSpaceAoOptions(true, strength, radius, samples).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewQualityOptions { ScreenSpaceAo = new(false, strength, radius, samples) }.Validate());
    }

    [Theory]
    [InlineData(FrameAntialiasing.Smaa1X)]
    [InlineData(FrameAntialiasing.Fxaa)]
    [InlineData(FrameAntialiasing.Taa)]
    [InlineData(FrameAntialiasing.Msaa2X)]
    [InlineData(FrameAntialiasing.Msaa4X)]
    [InlineData(FrameAntialiasing.Msaa8X)]
    public void TemporalUpscalingRejectsAllFrameAntialiasingModes(FrameAntialiasing aa)
    {
        var temporal = new ViewTemporalUpscalingOptions { Mode = ViewTemporalUpscalingMode.Fsr };
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(Antialiasing: aa, TemporalUpscaling: temporal).Validate());

        // Also rejected when inherited from scene options.
        var scene = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false, Antialiasing: aa);
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(TemporalUpscaling: temporal).ApplyTo(scene));
    }

    [Fact]
    public void TaaCompatibilityRulesAreEnforced()
    {
        // Rejects SSAA > 1, FSR1, TemporalUpscaling.
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(Antialiasing: FrameAntialiasing.Taa, SpatialSamples: 2).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(Antialiasing: FrameAntialiasing.Taa, Upscaling: new() { Mode = ViewUpscalingMode.Fsr1 }).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(Antialiasing: FrameAntialiasing.Taa, TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.XeSs }).Validate());

        // Inherited scene conflicts rejected on ApplyTo.
        var ssaaScene = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false, SpatialSamples: 4);
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(Antialiasing: FrameAntialiasing.Taa).ApplyTo(ssaaScene));
        var taaScene = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false, Antialiasing: FrameAntialiasing.Taa);
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(Upscaling: new() { Mode = ViewUpscalingMode.Fsr1 }).ApplyTo(taaScene));
        Assert.Throws<NotSupportedException>(() => OriginalFrameMath.Validate(taaScene with { SpatialSamples = 2 }));

        // Allowed combinations: TAA + standalone RCAS sharpening + SSAO + Diffusion + Aniso + PCF.
        var valid = new ViewQualityOptions(Antialiasing: FrameAntialiasing.Taa, SpatialSamples: 1, Diffusion: new(true, 0.5f, 0.002f))
        {
            Sharpening = new(true, 0.3f),
            AnisotropicFiltering = 16,
            ShadowFilter = ShadowFilterMode.PcfSoft,
            ShadowSoftness = 1.5f,
            ScreenSpaceAo = new(true, 0.4f, 1.5f, 8),
        };
        valid.Validate();
        _ = valid.ApplyTo(null);
    }

    [Theory]
    [InlineData(FrameAntialiasing.Msaa2X)]
    [InlineData(FrameAntialiasing.Msaa4X)]
    [InlineData(FrameAntialiasing.Msaa8X)]
    public void MsaaCompatibilityRulesAreEnforced(FrameAntialiasing msaa)
    {
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(Antialiasing: msaa, SpatialSamples: 2).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(Antialiasing: msaa, Upscaling: new() { Mode = ViewUpscalingMode.Fsr1 }).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(Antialiasing: msaa, TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.Dlss }).Validate());

        var scene = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false, Antialiasing: msaa, SpatialSamples: 2);
        Assert.Throws<NotSupportedException>(() => OriginalFrameMath.Validate(scene));

        // MSAA with GI and with SSAO (without GI occlusion) is valid.
        new ViewQualityOptions(Gi: new(LightingMode.SoftwareRayTracing, OcclusionStrength: 0f), Antialiasing: msaa)
        {
            ScreenSpaceAo = new(true, 0.5f, 2f, 8),
            Sharpening = new(true, 0.25f),
        }.Validate();
    }

    [Fact]
    public void ScreenSpaceAoAndRayGiOcclusionAreMutuallyExclusive()
    {
        var giWithAo = new RayGiOptions(Mode: LightingMode.SoftwareRayTracing, OcclusionStrength: 0.35f);
        var giNoAo = new RayGiOptions(Mode: LightingMode.SoftwareRayTracing, OcclusionStrength: 0f);
        var giOffWithAo = new RayGiOptions(Mode: LightingMode.Legacy, OcclusionStrength: 0.35f);

        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(Gi: giWithAo)
        {
            ScreenSpaceAo = new(Enabled: true, Strength: 0.4f),
        }.Validate());

        Assert.Throws<NotSupportedException>(() => OriginalFrameMath.Validate(
            new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false, RayGi: giWithAo)
            { ScreenSpaceAo = new(Enabled: true, Strength: 0.4f) }));

        // Inherited SSAO + View GI occlusion throws on ApplyTo.
        var ssaoScene = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false)
        { ScreenSpaceAo = new(Enabled: true) };
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(Gi: giWithAo).ApplyTo(ssaoScene));

        // Non-conflicting combinations are accepted.
        new ViewQualityOptions(Gi: giNoAo) { ScreenSpaceAo = new(Enabled: true, Strength: 0.4f) }.Validate();
        new ViewQualityOptions(Gi: giOffWithAo) { ScreenSpaceAo = new(Enabled: true, Strength: 0.4f) }.Validate();
        new ViewQualityOptions(Gi: giWithAo) { ScreenSpaceAo = new(Enabled: false, Strength: 0.4f) }.Validate();
    }

    [Fact]
    public void PresetJsonRoundTripsAllPr2FieldsAndPreservesLegacyBackwardsCompatibility()
    {
        var pr2Preview = new ViewRenderRequest(1920, 1080, new ViewQualityOptions(Antialiasing: FrameAntialiasing.Taa)
        {
            Sharpening = new(true, 0.35f),
            AnisotropicFiltering = 16,
            ShadowFilter = ShadowFilterMode.PcfSoft,
            ShadowSoftness = 1.75f,
            ScreenSpaceAo = new(true, 0.45f, 2.25f, 16),
        });
        var pr2Export = new ViewExportPreset(2560, 1440, new(60), new ViewQualityOptions(Antialiasing: FrameAntialiasing.Msaa4X)
        {
            Sharpening = new(false, 0.5f),
            AnisotropicFiltering = 8,
            ShadowFilter = ShadowFilterMode.Point,
            ShadowSoftness = 0.5f,
            ScreenSpaceAo = new(false, 0.2f, 1.0f, 4),
        });
        var def = new ViewOutputPresetDefinition("pr2.quality", "PR2 Quality", pr2Preview, pr2Export);
        byte[] bytes = ViewOutputPresetJson.Serialize(def);
        var roundTrip = ViewOutputPresetJson.Deserialize(bytes);

        Assert.Equal(pr2Preview, roundTrip.Preview);
        Assert.Equal(pr2Export, roundTrip.Export);

        // Removing all PR2 JSON fields simulates a legacy schema v1 preset.
        var root = JsonNode.Parse(bytes)!.AsObject();
        foreach (string section in new[] { "preview", "export" })
        {
            var q = root[section]!["quality"]!.AsObject();
            q.Remove("sharpening");
            q.Remove("anisotropicFiltering");
            q.Remove("shadowFilter");
            q.Remove("shadowSoftness");
            q.Remove("screenSpaceAo");
            q["antialiasing"] = "Smaa1X";
        }
        var legacy = ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString()));
        Assert.Null(legacy.Preview.Quality!.Sharpening);
        Assert.Null(legacy.Preview.Quality.AnisotropicFiltering);
        Assert.Null(legacy.Preview.Quality.ShadowFilter);
        Assert.Null(legacy.Preview.Quality.ShadowSoftness);
        Assert.Null(legacy.Preview.Quality.ScreenSpaceAo);
        Assert.Equal(1, legacy.Preview.Quality.EffectiveAnisotropicFiltering);
        Assert.False(legacy.Preview.Quality.EffectiveSharpening.Enabled);
        Assert.False(legacy.Preview.Quality.EffectiveScreenSpaceAo.Enabled);

        // Serializing defaults omits null PR2 properties.
        string defaultJson = Encoding.UTF8.GetString(ViewOutputPresetJson.Serialize(
            new ViewOutputPresetDefinition("defaults", "Defaults", new(1280, 720, new()), new(1280, 720, new(30), new()))));
        Assert.DoesNotContain("sharpening", defaultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("anisotropicFiltering", defaultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("shadowFilter", defaultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("shadowSoftness", defaultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("screenSpaceAo", defaultJson, StringComparison.Ordinal);
    }

    [Fact]
    public void PresetJsonRejectsInvalidPr2ValuesUnknownFieldsAndConflicts()
    {
        var def = new ViewOutputPresetDefinition("pr2.strict", "Strict",
            new(1280, 720, new ViewQualityOptions(Antialiasing: FrameAntialiasing.Fxaa)
            {
                Sharpening = new(true, 0.2f),
                AnisotropicFiltering = 4,
                ShadowFilter = ShadowFilterMode.PcfSoft,
                ShadowSoftness = 1.0f,
                ScreenSpaceAo = new(true, 0.4f, 1.5f, 8),
            }),
            new(1280, 720, new(30), new()));
        byte[] validBytes = ViewOutputPresetJson.Serialize(def);

        // Unknown field inside sharpening.
        var badSharp = JsonNode.Parse(validBytes)!.AsObject();
        badSharp["preview"]!["quality"]!["sharpening"]!["unknown"] = 1;
        Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(badSharp.ToJsonString())));

        // Unknown field inside screenSpaceAo.
        var badSsao = JsonNode.Parse(validBytes)!.AsObject();
        badSsao["preview"]!["quality"]!["screenSpaceAo"]!["unknown"] = true;
        Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(badSsao.ToJsonString())));

        // Numeric enum for shadowFilter rejected.
        var numEnum = JsonNode.Parse(validBytes)!.AsObject();
        numEnum["preview"]!["quality"]!["shadowFilter"] = 2;
        Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(numEnum.ToJsonString())));

        // Incompatible combination in JSON (TAA + SpatialSamples 2) wraps NotSupportedException.
        var conflict = JsonNode.Parse(validBytes)!.AsObject();
        conflict["preview"]!["quality"]!["antialiasing"] = "Taa";
        conflict["preview"]!["quality"]!["spatialSamples"] = 2;
        var error = Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(conflict.ToJsonString())));
        Assert.IsType<NotSupportedException>(error.InnerException);
    }
}
