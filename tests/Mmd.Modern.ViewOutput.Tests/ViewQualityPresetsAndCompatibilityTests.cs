using System.Text;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class ViewQualityPresetsAndCompatibilityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void ViewShadowOptionsAcceptsSupportedResolutionsAndSampling(int resolution)
    {
        new ViewShadowOptions(resolution, OriginalTextureSampling.LinearClamp).Validate();
        new ViewShadowOptions(resolution, OriginalTextureSampling.PointClamp).Validate();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    [InlineData(3000)]
    [InlineData(8192)]
    public void ViewShadowOptionsRejectsUnsupportedResolutions(int resolution)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewShadowOptions(resolution).Validate());
    }

    [Fact]
    public void ViewShadowOptionsRejectsWrapSampling()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ViewShadowOptions(2048, OriginalTextureSampling.LinearWrap).Validate());
    }

    [Fact]
    public void RenderScaleMutualExclusionAndRangesAreEnforced()
    {
        new ViewQualityOptions { RenderScale = 0.5f }.Validate();
        new ViewQualityOptions { RenderScale = 1.5f }.Validate();
        new ViewQualityOptions { PreviewOutlineWidthScale = 0f }.Validate();
        new ViewQualityOptions { PreviewOutlineWidthScale = 4f }.Validate();

        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewQualityOptions { RenderScale = 0.49f }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewQualityOptions { RenderScale = 1.51f }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewQualityOptions { RenderScale = float.NaN }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewQualityOptions { PreviewOutlineWidthScale = -0.1f }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewQualityOptions { PreviewOutlineWidthScale = 4.1f }.Validate());

        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(
            Upscaling: new() { Mode = ViewUpscalingMode.Fsr1 }) { RenderScale = 0.8f }.Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(
            TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.Fsr }) { RenderScale = 0.8f }.Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(
            SpatialSamples: 2) { RenderScale = 1.25f }.Validate());
    }

    [Fact]
    public void CompatibilityEvaluatorReportsAllConflictsWithLocalizationKeys()
    {
        var conflicted = new ViewQualityOptions(
            Gi: new RayGiOptions(
                Mode: LightingMode.SoftwareRayTracing,
                MaxRayDistance: 10f,
                RayEpsilon: 1f,
                OcclusionStrength: 0.5f,
                OcclusionRadius: 0.5f,
                Composition: RayGiComposition.Additive,
                Denoiser: RayGiDenoiser.Spatial)
            {
                ToonResponse = RayGiToonResponse.PreservePaletteBands
            },
            Antialiasing: FrameAntialiasing.Smaa1X,
            SpatialSamples: 2,
            TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.Fsr })
        {
            RenderScale = 1.2f,
            Shadow = new ViewShadowOptions(123, OriginalTextureSampling.LinearWrap),
            PreviewOutlineWidthScale = 5f
        };

        var report = ViewQualityCompatibility.Evaluate(conflicted);
        Assert.False(report.IsValid);
        var codes = report.Issues.Select(i => i.Code).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ShadowResolution", codes);
        Assert.Contains("ShadowSampling", codes);
        Assert.Contains("OutlineScaleRange", codes);
        Assert.Contains("TemporalWithSpatial", codes);
        Assert.Contains("RenderScaleWithUpscaling", codes);
        Assert.Contains("RenderScaleWithSsaa", codes);
        Assert.Contains("GiToonResponseRequiresPalette", codes);
        Assert.Contains("GiDenoiserRequiresPalette", codes);
        Assert.Contains("GiOcclusionRadius", codes);
        Assert.All(report.Issues, issue => Assert.StartsWith("Mda.Text.Preview.Conflict.", issue.LocalizationKey, StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltInPresetsMatchSpecAndAlwaysDisableDiffusion()
    {
        foreach (var kind in Enum.GetValues<BuiltInPreviewPresetKind>())
        {
            var preset = ViewQualityPresets.Create(kind);
            preset.Validate();
            Assert.False(preset.EffectiveDiffusion.Enabled);
            Assert.True(preset.AdaptiveVramSampling);
            Assert.Equal(1f, preset.RenderScale);
            Assert.True(preset.PreviewOutlineEnabled);
            Assert.Equal(1f, preset.PreviewOutlineWidthScale);
            Assert.True(ViewQualityPresets.TryMatchBuiltIn(preset, out var matched));
            Assert.Equal(kind, matched);
        }

        var perf = ViewQualityPresets.Create(BuiltInPreviewPresetKind.Performance);
        Assert.Equal(LightingMode.Legacy, perf.EffectiveGi.Mode);
        Assert.Equal(SmaaQuality.Low, perf.SmaaQuality);
        Assert.Equal(1024, perf.EffectiveShadow.Resolution);
        Assert.False(perf.UseEnvironmentMipmaps);

        var balanced = ViewQualityPresets.Create(BuiltInPreviewPresetKind.Balanced);
        Assert.Equal(SmaaQuality.High, balanced.SmaaQuality);
        Assert.Equal(2048, balanced.EffectiveShadow.Resolution);
        Assert.False(balanced.UseEnvironmentMipmaps);

        var high = ViewQualityPresets.Create(BuiltInPreviewPresetKind.High);
        Assert.Equal(SmaaQuality.Ultra, high.SmaaQuality);
        Assert.Equal(2, high.SpatialSamples);
        Assert.Equal(4096, high.EffectiveShadow.Resolution);
        Assert.True(high.UseEnvironmentMipmaps);

        var ultraDxr = ViewQualityPresets.Create(BuiltInPreviewPresetKind.UltraRayTraced, hardwareGiAvailable: true, softwareGiAvailable: true);
        Assert.Equal(LightingMode.HardwareRayTracing, ultraDxr.EffectiveGi.Mode);
        Assert.True(ultraDxr.EffectiveGi.PreviewAcceleration);
        Assert.Equal(RayGiComposition.ToonPalette, ultraDxr.EffectiveGi.Composition);
        Assert.Equal(RayGiToonResponse.PreservePaletteBands, ultraDxr.EffectiveGi.ToonResponse);
        Assert.Equal(RayGiDenoiser.TemporalSpatial, ultraDxr.EffectiveGi.Denoiser);
        Assert.Equal(0.35f, ultraDxr.EffectiveGi.OcclusionStrength);
        Assert.Equal(1.0f, ultraDxr.EffectiveGi.OcclusionRadius);
        Assert.Equal(4096, ultraDxr.EffectiveShadow.Resolution);
        Assert.True(ultraDxr.UseEnvironmentMipmaps);

        var ultraSw = ViewQualityPresets.Create(BuiltInPreviewPresetKind.UltraRayTraced, hardwareGiAvailable: false, softwareGiAvailable: true);
        Assert.Equal(LightingMode.SoftwareRayTracing, ultraSw.EffectiveGi.Mode);
        Assert.True(ViewQualityPresets.TryMatchBuiltIn(ultraSw, out var matchedSw));
        Assert.Equal(BuiltInPreviewPresetKind.UltraRayTraced, matchedSw);

        var ultraLegacy = ViewQualityPresets.Create(BuiltInPreviewPresetKind.UltraRayTraced, hardwareGiAvailable: false, softwareGiAvailable: false);
        Assert.Equal(LightingMode.Legacy, ultraLegacy.EffectiveGi.Mode);
        Assert.True(ViewQualityPresets.TryMatchBuiltIn(ultraLegacy, out var matchedLegacy));
        Assert.Equal(BuiltInPreviewPresetKind.UltraRayTraced, matchedLegacy);

        // Null Shadow (inherit scene) must stay distinct from explicit ViewShadowOptions.Default.
        Assert.False(ViewQualityPresets.AreEquivalent(new ViewQualityOptions(), new ViewQualityOptions { Shadow = ViewShadowOptions.Default }));
    }

    [Fact]
    public void PresetJsonRoundTripsNewFieldsAndKeepsLegacyCompatibility()
    {
        var rich = new ViewQualityOptions(
            Gi: new RayGiOptions(LightingMode.SoftwareRayTracing, 5.5f, 12, 42, 750f, 0.005f,
                null, null, 1.5f, 0.4f, 1.25f, RayGiComposition.ToonPalette, RayGiDenoiser.TemporalSpatial)
            {
                ToonResponse = RayGiToonResponse.PreservePaletteBands,
                PreviewAcceleration = true
            },
            Antialiasing: FrameAntialiasing.Smaa1X,
            SmaaQuality: SmaaQuality.Ultra,
            SpatialSamples: 1,
            SurfaceOrder: SurfaceDrawOrder.Original)
        {
            Shadow = new ViewShadowOptions(512, OriginalTextureSampling.PointClamp),
            UseEnvironmentMipmaps = true,
            AdaptiveVramSampling = false,
            RenderScale = 0.75f,
            PreviewOutlineEnabled = false,
            PreviewOutlineWidthScale = 2.25f
        };

        var def = new ViewOutputPresetDefinition("pr1.rich", "PR1 Rich",
            new(1280, 720, rich), new(1920, 1080, new(30), rich));
        var roundTripped = ViewOutputPresetJson.Deserialize(ViewOutputPresetJson.Serialize(def));
        Assert.Equal(rich, roundTripped.Preview.Quality);
        Assert.Equal(rich, roundTripped.Export.Quality);

        // Strip all PR1 new fields to verify legacy JSON deserialization defaults.
        var root = JsonNode.Parse(ViewOutputPresetJson.Serialize(def))!.AsObject();
        foreach (string key in new[] { "shadow", "useEnvironmentMipmaps", "adaptiveVramSampling",
            "renderScale", "previewOutlineEnabled", "previewOutlineWidthScale" })
        {
            root["preview"]!["quality"]!.AsObject().Remove(key);
            root["export"]!["quality"]!.AsObject().Remove(key);
        }
        var legacy = ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var legacyQuality = legacy.Preview.EffectiveQuality;
        Assert.Null(legacyQuality.Shadow);
        Assert.Equal(2048, legacyQuality.EffectiveShadow.Resolution);
        Assert.Equal(OriginalTextureSampling.LinearClamp, legacyQuality.EffectiveShadow.Sampling);
        Assert.False(legacyQuality.UseEnvironmentMipmaps);
        Assert.True(legacyQuality.AdaptiveVramSampling);
        Assert.Equal(1f, legacyQuality.RenderScale);
        Assert.True(legacyQuality.PreviewOutlineEnabled);
        Assert.Equal(1f, legacyQuality.PreviewOutlineWidthScale);

        // Invalid shadow sampling enum in JSON is rejected.
        var bad = JsonNode.Parse(ViewOutputPresetJson.Serialize(def))!.AsObject();
        bad["preview"]!["quality"]!["shadow"]!["sampling"] = "LinearWrap";
        Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(bad.ToJsonString())));
    }
}
