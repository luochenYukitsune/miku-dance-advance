using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput;

public enum BuiltInPreviewPresetKind
{
    Performance,
    Balanced,
    High,
    UltraRayTraced
}

/// <summary>Built-in preview quality presets. All presets leave Diffusion disabled by default.</summary>
public static class ViewQualityPresets
{
    public static ViewQualityOptions Create(BuiltInPreviewPresetKind kind, bool hardwareGiAvailable = true, bool softwareGiAvailable = true)
    {
        return kind switch
        {
            BuiltInPreviewPresetKind.Performance => new(
                Gi: RayGiOptions.Off,
                Antialiasing: FrameAntialiasing.Smaa1X,
                SmaaQuality: SmaaQuality.Low,
                SpatialSamples: 1,
                Upscaling: ViewUpscalingOptions.Off,
                TemporalUpscaling: ViewTemporalUpscalingOptions.Off,
                Diffusion: ViewDiffusionOptions.Off,
                SurfaceOrder: SurfaceDrawOrder.OpaqueFirst)
            {
                Shadow = new ViewShadowOptions(1024, OriginalTextureSampling.LinearClamp),
                UseEnvironmentMipmaps = false,
                AdaptiveVramSampling = true,
                RenderScale = 1f,
                PreviewOutlineEnabled = true,
                PreviewOutlineWidthScale = 1f
            },

            BuiltInPreviewPresetKind.Balanced => new(
                Gi: RayGiOptions.Off,
                Antialiasing: FrameAntialiasing.Smaa1X,
                SmaaQuality: SmaaQuality.High,
                SpatialSamples: 1,
                Upscaling: ViewUpscalingOptions.Off,
                TemporalUpscaling: ViewTemporalUpscalingOptions.Off,
                Diffusion: ViewDiffusionOptions.Off,
                SurfaceOrder: SurfaceDrawOrder.OpaqueFirst)
            {
                Shadow = new ViewShadowOptions(2048, OriginalTextureSampling.LinearClamp),
                UseEnvironmentMipmaps = false,
                AdaptiveVramSampling = true,
                RenderScale = 1f,
                PreviewOutlineEnabled = true,
                PreviewOutlineWidthScale = 1f
            },

            BuiltInPreviewPresetKind.High => new(
                Gi: RayGiOptions.Off,
                Antialiasing: FrameAntialiasing.Smaa1X,
                SmaaQuality: SmaaQuality.Ultra,
                SpatialSamples: 2,
                Upscaling: ViewUpscalingOptions.Off,
                TemporalUpscaling: ViewTemporalUpscalingOptions.Off,
                Diffusion: ViewDiffusionOptions.Off,
                SurfaceOrder: SurfaceDrawOrder.OpaqueFirst)
            {
                Shadow = new ViewShadowOptions(4096, OriginalTextureSampling.LinearClamp),
                UseEnvironmentMipmaps = true,
                AdaptiveVramSampling = true,
                RenderScale = 1f,
                PreviewOutlineEnabled = true,
                PreviewOutlineWidthScale = 1f
            },

            BuiltInPreviewPresetKind.UltraRayTraced => CreateUltraRayTraced(hardwareGiAvailable, softwareGiAvailable),

            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static ViewQualityOptions CreateUltraRayTraced(bool hardwareGiAvailable, bool softwareGiAvailable)
    {
        var giMode = hardwareGiAvailable
            ? LightingMode.HardwareRayTracing
            : softwareGiAvailable
                ? LightingMode.SoftwareRayTracing
                : LightingMode.Legacy;
        var gi = giMode == LightingMode.Legacy
            ? RayGiOptions.Off
            : new RayGiOptions(
                Mode: giMode,
                Strength: 4f,
                SamplesPerPixel: 16,
                OcclusionStrength: 0.35f,
                OcclusionRadius: 1.0f,
                Composition: RayGiComposition.ToonPalette,
                Denoiser: RayGiDenoiser.TemporalSpatial)
            {
                ToonResponse = RayGiToonResponse.PreservePaletteBands,
                PreviewAcceleration = true
            };

        return new(
            Gi: gi,
            Antialiasing: FrameAntialiasing.Smaa1X,
            SmaaQuality: SmaaQuality.Ultra,
            SpatialSamples: 1,
            Upscaling: ViewUpscalingOptions.Off,
            TemporalUpscaling: ViewTemporalUpscalingOptions.Off,
            Diffusion: ViewDiffusionOptions.Off,
            SurfaceOrder: SurfaceDrawOrder.OpaqueFirst)
        {
            Shadow = new ViewShadowOptions(4096, OriginalTextureSampling.LinearClamp),
            UseEnvironmentMipmaps = true,
            AdaptiveVramSampling = true,
            RenderScale = 1f,
            PreviewOutlineEnabled = true,
            PreviewOutlineWidthScale = 1f
        };
    }

    public static bool TryMatchBuiltIn(ViewQualityOptions quality, out BuiltInPreviewPresetKind kind)
    {
        ArgumentNullException.ThrowIfNull(quality);
        foreach (var candidate in new[]
        {
            BuiltInPreviewPresetKind.Performance,
            BuiltInPreviewPresetKind.Balanced,
            BuiltInPreviewPresetKind.High
        })
        {
            if (AreEquivalent(quality, Create(candidate)))
            {
                kind = candidate;
                return true;
            }
        }
        if (AreEquivalent(quality, Create(BuiltInPreviewPresetKind.UltraRayTraced, hardwareGiAvailable: true, softwareGiAvailable: true))
            || AreEquivalent(quality, Create(BuiltInPreviewPresetKind.UltraRayTraced, hardwareGiAvailable: false, softwareGiAvailable: true))
            || AreEquivalent(quality, Create(BuiltInPreviewPresetKind.UltraRayTraced, hardwareGiAvailable: false, softwareGiAvailable: false)))
        {
            kind = BuiltInPreviewPresetKind.UltraRayTraced;
            return true;
        }
        kind = default;
        return false;
    }

    public static bool AreEquivalent(ViewQualityOptions a, ViewQualityOptions b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Normalize(a) == Normalize(b);
    }

    private static ViewQualityOptions Normalize(ViewQualityOptions q) => new(
        Gi: q.EffectiveGi with { TemporalHistoryGeneration = 0, TemporalCameraCutKey = 0, ResetTemporalHistory = false },
        Antialiasing: q.Antialiasing ?? FrameAntialiasing.Off,
        SmaaQuality: q.SmaaQuality ?? SmaaQuality.High,
        SpatialSamples: q.SpatialSamples ?? 1,
        Upscaling: q.EffectiveUpscaling,
        TemporalUpscaling: q.EffectiveTemporalUpscaling,
        Diffusion: q.EffectiveDiffusion,
        SurfaceOrder: q.SurfaceOrder)
    {
        Shadow = q.Shadow,
        UseEnvironmentMipmaps = q.UseEnvironmentMipmaps,
        AdaptiveVramSampling = q.AdaptiveVramSampling,
        RenderScale = q.RenderScale,
        PreviewOutlineEnabled = q.PreviewOutlineEnabled,
        PreviewOutlineWidthScale = q.PreviewOutlineWidthScale
    };
}
