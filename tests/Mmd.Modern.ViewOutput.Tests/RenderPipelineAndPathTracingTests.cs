using System.Text;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class RenderPipelineAndPathTracingTests
{
    [Fact]
    public void EnumNumericContractsMatchSpecDefaults()
    {
        Assert.Equal(0, (int)RenderPipelineMode.Forward);
        Assert.Equal(1, (int)RenderPipelineMode.ClusteredForward);
        Assert.Equal(2, (int)RenderPipelineMode.PathTracing);

        Assert.Equal(0, (int)PathTracingShadingMode.ToonHybrid);
        Assert.Equal(1, (int)PathTracingShadingMode.Physical);

        var defaults = PathTracingOptions.Default;
        Assert.Equal(PathTracingShadingMode.ToonHybrid, defaults.ShadingMode);
        Assert.Equal(1, defaults.SamplesPerFrame);
        Assert.Equal(4, defaults.MaxBounces);
        Assert.True(defaults.ProgressiveAccumulation);
        Assert.Equal(256, defaults.MaxAccumulatedFrames);
        Assert.Equal(1, defaults.InteractiveSamplesPerFrame);
        Assert.Equal(10f, defaults.FireflyClamp);
        Assert.Equal(RayGiDenoiser.TemporalSpatial, defaults.Denoiser);
        Assert.True(defaults.UseHardwareRayTracing);
        defaults.Validate();
    }

    [Fact]
    public void PathTracingOptionsValidatesBoundariesAndInteractiveDownsampling()
    {
        new PathTracingOptions(PathTracingShadingMode.ToonHybrid, 1, 1, false, 1, 1, 1f, RayGiDenoiser.Off, false).Validate();
        new PathTracingOptions(PathTracingShadingMode.Physical, 64, 8, true, 4096, 16, 1000f, RayGiDenoiser.Spatial, true).Validate();

        Assert.Throws<ArgumentOutOfRangeException>(() => (new PathTracingOptions { ShadingMode = (PathTracingShadingMode)99 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (new PathTracingOptions { SamplesPerFrame = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (new PathTracingOptions { SamplesPerFrame = 65 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (new PathTracingOptions { MaxBounces = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (new PathTracingOptions { MaxBounces = 9 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (new PathTracingOptions { MaxAccumulatedFrames = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (new PathTracingOptions { MaxAccumulatedFrames = 4097 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (new PathTracingOptions { InteractiveSamplesPerFrame = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (new PathTracingOptions { InteractiveSamplesPerFrame = 17 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (new PathTracingOptions { FireflyClamp = 0.99f }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (new PathTracingOptions { FireflyClamp = 1000.1f }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (new PathTracingOptions { FireflyClamp = float.NaN }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (new PathTracingOptions { FireflyClamp = float.PositiveInfinity }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (new PathTracingOptions { Denoiser = (RayGiDenoiser)99 }).Validate());

        var highSpp = new PathTracingOptions(SamplesPerFrame: 16, InteractiveSamplesPerFrame: 2);
        Assert.Equal(16, highSpp.EffectiveSamplesPerFrame);
        Assert.Equal(2, (highSpp with { Interactive = true }).EffectiveSamplesPerFrame);
        var lowSpp = new PathTracingOptions(SamplesPerFrame: 1, InteractiveSamplesPerFrame: 4) { Interactive = true };
        Assert.Equal(1, lowSpp.EffectiveSamplesPerFrame);
    }

    [Fact]
    public void ClusteredForwardRetainsFullForwardCompatibility()
    {
        new ViewQualityOptions(
            Gi: new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: 2f, SamplesPerPixel: 4),
            Antialiasing: FrameAntialiasing.Smaa1X,
            SmaaQuality: SmaaQuality.Ultra,
            SpatialSamples: 4,
            Upscaling: new() { Mode = ViewUpscalingMode.Fsr1 },
            Diffusion: new(true, 0.5f, 0.002f),
            PipelineMode: RenderPipelineMode.ClusteredForward).Validate();

        new ViewQualityOptions(
            Antialiasing: FrameAntialiasing.Off,
            SpatialSamples: 1,
            TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.Fsr },
            PipelineMode: RenderPipelineMode.ClusteredForward).Validate();

        var applied = new ViewQualityOptions(
            Antialiasing: FrameAntialiasing.Smaa1X,
            SpatialSamples: 2,
            PipelineMode: RenderPipelineMode.ClusteredForward).ApplyTo(null);
        Assert.Equal(RenderPipelineMode.ClusteredForward, applied.PipelineMode);
        Assert.Equal(FrameAntialiasing.Smaa1X, applied.Antialiasing);
        Assert.Equal(2, applied.SpatialSamples);
        OriginalFrameMath.Validate(applied);

        // ClusteredForward composes cleanly with PR1/PR2 features: MSAA, TAA, FXAA, PCF, AF, SSAO, RenderScale, RCAS.
        var clusteredPr12 = new ViewQualityOptions(
            Antialiasing: FrameAntialiasing.Msaa4X,
            PipelineMode: RenderPipelineMode.ClusteredForward)
        {
            Shadow = new(2048, OriginalTextureSampling.LinearClamp),
            UseEnvironmentMipmaps = true,
            RenderScale = 0.75f,
            PreviewOutlineWidthScale = 1.25f,
            Sharpening = new(true, 0.35f),
            AnisotropicFiltering = 16,
            ShadowFilter = ShadowFilterMode.PcfSoft,
            ShadowSoftness = 1.5f,
            ScreenSpaceAo = new(true, 0.6f, 0.5f, 8),
        };
        clusteredPr12.Validate();
        var clusteredApplied = clusteredPr12.ApplyTo(null);
        Assert.Equal(RenderPipelineMode.ClusteredForward, clusteredApplied.PipelineMode);
        Assert.Equal(FrameAntialiasing.Msaa4X, clusteredApplied.Antialiasing);
        Assert.Equal(0.75f, clusteredApplied.RenderScale);
        Assert.Equal(16, clusteredApplied.AnisotropicFiltering);
        Assert.Equal(ShadowFilterMode.PcfSoft, clusteredApplied.ShadowFilter);
        Assert.Equal(1.5f, clusteredApplied.ShadowSoftness);
        Assert.True(clusteredApplied.ScreenSpaceAo?.IsActive);
        OriginalFrameMath.Validate(clusteredApplied);

        new ViewQualityOptions(Antialiasing: FrameAntialiasing.Taa, PipelineMode: RenderPipelineMode.ClusteredForward).Validate();
        new ViewQualityOptions(Antialiasing: FrameAntialiasing.Fxaa, PipelineMode: RenderPipelineMode.ClusteredForward).Validate();

        // PathTracing composes with RenderScale/Sharpening and takes over RayGi, ScreenSpaceAo, and raster SelfShadow.
        var ptPr12 = new ViewQualityOptions(
            Gi: new RayGiOptions(LightingMode.HardwareRayTracing, Strength: 3f, SamplesPerPixel: 4, OcclusionStrength: 0.5f),
            Antialiasing: FrameAntialiasing.Off,
            SpatialSamples: 1,
            PipelineMode: RenderPipelineMode.PathTracing,
            PathTracing: new PathTracingOptions(PathTracingShadingMode.Physical, SamplesPerFrame: 2, MaxBounces: 4))
        {
            RenderScale = 0.75f,
            Sharpening = new(true, 0.25f),
            ScreenSpaceAo = new(true, 0.5f, 0.4f, 8),
        };
        ptPr12.Validate();
        var ptApplied = ptPr12.ApplyTo(null);
        Assert.Equal(RenderPipelineMode.PathTracing, ptApplied.PipelineMode);
        Assert.Equal(0.75f, ptApplied.RenderScale);
        Assert.Equal(RayGiOptions.Off, ptApplied.RayGi);
        Assert.Equal(ScreenSpaceAoOptions.Off, ptApplied.ScreenSpaceAo);
        Assert.Null(ptApplied.SelfShadow);
        OriginalFrameMath.Validate(ptApplied);

        // Combined PR1+PR2+PR3 JSON round-trip preserves all fields cleanly.
        var preset = new ViewOutputPresetDefinition(
            "pr123-combined",
            "PR123 Combined",
            new ViewRenderRequest(1280, 720, clusteredPr12),
            new ViewExportPreset(1920, 1080, new(60), ptPr12));
        var roundTripped = ViewOutputPresetJson.Deserialize(ViewOutputPresetJson.Serialize(preset));
        Assert.Equal(RenderPipelineMode.ClusteredForward, roundTripped.Preview.EffectiveQuality.PipelineMode);
        Assert.Equal(FrameAntialiasing.Msaa4X, roundTripped.Preview.EffectiveQuality.Antialiasing);
        Assert.Equal(ShadowFilterMode.PcfSoft, roundTripped.Preview.EffectiveQuality.ShadowFilter);
        Assert.Equal(16, roundTripped.Preview.EffectiveQuality.AnisotropicFiltering);
        Assert.Equal(0.75f, roundTripped.Preview.EffectiveQuality.RenderScale);
        Assert.True(roundTripped.Preview.EffectiveQuality.EffectiveScreenSpaceAo.Enabled);
        Assert.Equal(RenderPipelineMode.PathTracing, roundTripped.Export.Quality!.PipelineMode);
        Assert.Equal(PathTracingShadingMode.Physical, roundTripped.Export.Quality.PathTracing!.ShadingMode);
        Assert.Equal(0.75f, roundTripped.Export.Quality.RenderScale);
        Assert.True(roundTripped.Export.Quality.EffectiveSharpening.Enabled);
    }

    [Fact]
    public void PathTracingCompatibilityMatrixEnforcesHardExclusionsAndTakesOverGi()
    {
        var validPt = new ViewQualityOptions(
            Gi: new RayGiOptions(LightingMode.HardwareRayTracing, Strength: 4f, SamplesPerPixel: 8),
            Antialiasing: FrameAntialiasing.Off,
            SpatialSamples: 1,
            Upscaling: ViewUpscalingOptions.Off,
            PipelineMode: RenderPipelineMode.PathTracing,
            PathTracing: new PathTracingOptions(PathTracingShadingMode.Physical, SamplesPerFrame: 4, MaxBounces: 6));
        validPt.Validate();

        // Standalone RayGi is taken over by PathTracing rather than rejected.
        var applied = validPt.ApplyTo(null);
        Assert.Equal(RenderPipelineMode.PathTracing, applied.PipelineMode);
        Assert.Equal(RayGiOptions.Off, applied.RayGi);
        Assert.Equal(PathTracingShadingMode.Physical, applied.PathTracing!.ShadingMode);
        OriginalFrameMath.Validate(applied);

        // PathTracing rejects raster AA (SMAA/FXAA/TAA/MSAA), spatial supersampling, spatial upscaling, and temporal upscaling.
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(
            Antialiasing: FrameAntialiasing.Smaa1X,
            PipelineMode: RenderPipelineMode.PathTracing).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(
            Antialiasing: FrameAntialiasing.Fxaa,
            PipelineMode: RenderPipelineMode.PathTracing).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(
            Antialiasing: FrameAntialiasing.Taa,
            PipelineMode: RenderPipelineMode.PathTracing).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(
            Antialiasing: FrameAntialiasing.Msaa2X,
            PipelineMode: RenderPipelineMode.PathTracing).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(
            Antialiasing: FrameAntialiasing.Msaa4X,
            PipelineMode: RenderPipelineMode.PathTracing).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(
            Antialiasing: FrameAntialiasing.Msaa8X,
            PipelineMode: RenderPipelineMode.PathTracing).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(
            SpatialSamples: 2,
            PipelineMode: RenderPipelineMode.PathTracing).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(
            Upscaling: new() { Mode = ViewUpscalingMode.Fsr1 },
            PipelineMode: RenderPipelineMode.PathTracing).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(
            TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.Fsr },
            PipelineMode: RenderPipelineMode.PathTracing).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(
            TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.XeSs },
            PipelineMode: RenderPipelineMode.PathTracing).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(
            TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.Dlss },
            PipelineMode: RenderPipelineMode.PathTracing).Validate());

        // Inherited scene SMAA / SSAA must also be rejected when PathTracing leaves overrides null.
        var sceneWithSmaa = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false,
            Antialiasing: FrameAntialiasing.Smaa1X);
        Assert.Throws<NotSupportedException>(() =>
            new ViewQualityOptions(PipelineMode: RenderPipelineMode.PathTracing).ApplyTo(sceneWithSmaa));

        var sceneWithSsaa = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false,
            SpatialSamples: 4);
        Assert.Throws<NotSupportedException>(() =>
            new ViewQualityOptions(PipelineMode: RenderPipelineMode.PathTracing).ApplyTo(sceneWithSsaa));

        // Explicit Off / 1 override resolves cleanly against inherited scene AA/SSAA.
        var explicitOff = new ViewQualityOptions(
            Antialiasing: FrameAntialiasing.Off,
            SpatialSamples: 1,
            PipelineMode: RenderPipelineMode.PathTracing).ApplyTo(sceneWithSmaa with { SpatialSamples = 4 });
        Assert.Equal(FrameAntialiasing.Off, explicitOff.Antialiasing);
        Assert.Equal(1, explicitOff.SpatialSamples);

        // OriginalFrameMath.Validate enforces the same contract directly.
        Assert.Throws<NotSupportedException>(() => OriginalFrameMath.Validate(
            new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false,
                Antialiasing: FrameAntialiasing.Smaa1X, PipelineMode: RenderPipelineMode.PathTracing)));
        Assert.Throws<NotSupportedException>(() => OriginalFrameMath.Validate(
            new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false,
                SpatialSamples: 2, PipelineMode: RenderPipelineMode.PathTracing)));
        Assert.Throws<ArgumentException>(() => OriginalFrameMath.Validate(
            new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false,
                PipelineMode: (RenderPipelineMode)99)));
    }

    [Fact]
    public void PresetJsonRoundTripsClusteredAndPathTracingWithStrictBackwardsCompatibility()
    {
        // 1. Default Forward omits new fields so existing JSON presets remain untouched.
        var forwardPreset = new ViewOutputPresetDefinition("fwd", "Forward",
            new(1280, 720, new ViewQualityOptions()),
            new(1920, 1080, new(30), new ViewQualityOptions()));
        string forwardJson = Encoding.UTF8.GetString(ViewOutputPresetJson.Serialize(forwardPreset));
        Assert.DoesNotContain("pipelineMode", forwardJson, StringComparison.Ordinal);
        Assert.DoesNotContain("pathTracing", forwardJson, StringComparison.Ordinal);
        var forwardCopy = ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(forwardJson));
        Assert.Equal(RenderPipelineMode.Forward, forwardCopy.Preview.Quality!.PipelineMode);
        Assert.Null(forwardCopy.Preview.Quality.PathTracing);

        // 2. ClusteredForward & PathTracing round-trip cleanly and strip transient runtime properties.
        var pt = new PathTracingOptions(
            ShadingMode: PathTracingShadingMode.Physical,
            SamplesPerFrame: 8,
            MaxBounces: 6,
            ProgressiveAccumulation: true,
            MaxAccumulatedFrames: 512,
            InteractiveSamplesPerFrame: 2,
            FireflyClamp: 25f,
            Denoiser: RayGiDenoiser.Spatial,
            UseHardwareRayTracing: false)
        {
            HistoryGeneration = 9,
            CameraCutKey = 14,
            ResetHistory = true,
            Interactive = true,
        };

        var richPreset = new ViewOutputPresetDefinition("pt-and-clustered", "PT & Clustered",
            new(1280, 720, new ViewQualityOptions(
                Antialiasing: FrameAntialiasing.Off,
                SpatialSamples: 1,
                PipelineMode: RenderPipelineMode.PathTracing,
                PathTracing: pt)),
            new(1920, 1080, new(60), new ViewQualityOptions(
                Antialiasing: FrameAntialiasing.Smaa1X,
                SpatialSamples: 2,
                PipelineMode: RenderPipelineMode.ClusteredForward)));

        byte[] bytes = ViewOutputPresetJson.Serialize(richPreset);
        string richJson = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("historyGeneration", richJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cameraCutKey", richJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resetHistory", richJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("effective", richJson, StringComparison.OrdinalIgnoreCase);

        var copy = ViewOutputPresetJson.Deserialize(bytes);
        Assert.Equal(RenderPipelineMode.PathTracing, copy.Preview.Quality!.PipelineMode);
        Assert.Equal(pt.StripRuntimeState(), copy.Preview.Quality.PathTracing);
        Assert.Equal(RenderPipelineMode.ClusteredForward, copy.Export.Quality!.PipelineMode);
        Assert.Null(copy.Export.Quality.PathTracing);

        // 3. Reject unknown fields, numeric enums, and incompatible combinations in JSON.
        var root = JsonNode.Parse(richJson)!.AsObject();
        root["preview"]!["quality"]!["pathTracing"]!["unknownField"] = 1;
        Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString())));

        root = JsonNode.Parse(richJson)!.AsObject();
        root["preview"]!["quality"]!["pipelineMode"] = 2;
        Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString())));

        root = JsonNode.Parse(richJson)!.AsObject();
        root["preview"]!["quality"]!["pathTracing"]!["shadingMode"] = 1;
        Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString())));

        root = JsonNode.Parse(richJson)!.AsObject();
        root["preview"]!["quality"]!["antialiasing"] = "Smaa1X";
        var incompatible = Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString())));
        Assert.IsType<NotSupportedException>(incompatible.InnerException);
    }
}
