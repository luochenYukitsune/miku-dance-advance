using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput;

public enum PreviewPerformanceLoadTier
{
    Light = 0,
    Moderate = 1,
    High = 2,
    Heavy = 3,
}

/// <summary>Derived internal render dimensions, estimated render-buffer VRAM footprint, and load tier for a preview quality draft.</summary>
public sealed record PreviewQualityResourceEstimate(
    int ViewportWidth,
    int ViewportHeight,
    int RenderWidth,
    int RenderHeight,
    long EstimatedRenderBufferBytes,
    double EstimatedRenderBufferMiB,
    ulong? AdapterBudgetBytes,
    double? BudgetUsageRatio,
    bool IsOverBudget,
    PreviewPerformanceLoadTier LoadTier,
    bool IsClassicEngine)
{
    public double? AdapterBudgetMiB => AdapterBudgetBytes is { } bytes ? bytes / (1024d * 1024d) : null;
}

/// <summary>Pure C# render-buffer resolution and VRAM estimator for preview quality configurations.
/// Estimates render-pipeline targets and history buffers only; excludes model geometry and source textures.</summary>
public static class PreviewQualityResourceEstimator
{
    private const long InFlightFrames = 3;
    private const long Mebibyte = 1024L * 1024L;

    public static PreviewQualityResourceEstimate Estimate(
        ViewQualityOptions quality,
        int viewportWidth,
        int viewportHeight,
        ulong? adapterBudgetBytes = null,
        bool isClassicEngine = false)
    {
        ArgumentNullException.ThrowIfNull(quality);

        int vw = Math.Clamp(viewportWidth > 0 ? viewportWidth : 1280, 1, 4096);
        int vh = Math.Clamp(viewportHeight > 0 ? viewportHeight : 720, 1, 4096);
        ulong? normalizedBudget = adapterBudgetBytes is > 0 ? adapterBudgetBytes : null;

        if (isClassicEngine)
        {
            long classicBytes = (long)vw * vh * 12L;
            double classicMiB = classicBytes / (double)Mebibyte;
            double? classicRatio = normalizedBudget is { } cb ? classicBytes / (double)cb : null;
            bool classicOver = normalizedBudget is { } cb2 && (ulong)classicBytes > cb2;
            return new(
                vw, vh, vw, vh,
                classicBytes, classicMiB,
                normalizedBudget, classicRatio, classicOver,
                PreviewPerformanceLoadTier.Light,
                IsClassicEngine: true);
        }

        bool pathTracing = quality.PipelineMode == RenderPipelineMode.PathTracing;
        var temporal = quality.EffectiveTemporalUpscaling;
        var upscaling = quality.EffectiveUpscaling;
        int rawSpatialSamples = quality.SpatialSamples ?? 1;
        bool hasTemporal = !pathTracing && temporal.Mode != ViewTemporalUpscalingMode.Off;
        bool hasFsr1 = !pathTracing && !hasTemporal && upscaling.Mode == ViewUpscalingMode.Fsr1;

        var (rw, rh) = DeriveRenderDimensions(vw, vh, quality, pathTracing, hasTemporal, hasFsr1, rawSpatialSamples);

        long renderPixels = (long)rw * rh;
        long displayPixels = (long)vw * vh;
        long bytes = checked(renderPixels * 8L * InFlightFrames + displayPixels * 4L * InFlightFrames);

        var aa = quality.Antialiasing ?? FrameAntialiasing.Off;
        int msaaSamples = !pathTracing && !hasTemporal ? OriginalFrameMath.MsaaSampleCount(aa) : 1;
        int effectiveSsaa = !pathTracing && !hasTemporal && msaaSamples == 1 ? Math.Clamp(rawSpatialSamples, 1, 32) : 1;

        if (pathTracing)
        {
            var pt = quality.EffectivePathTracing;
            // PersistentHistoryBuffers: Accumulation, GuidePosDepth, GuideNormalMat, GuideAlbedo, Denoised (16 Bpp each)
            bytes = checked(bytes + renderPixels * 80L + 24L * Mebibyte);
            if (pt.Denoiser != RayGiDenoiser.Off)
                bytes = checked(bytes + renderPixels * 32L * InFlightFrames);
        }
        else
        {
            if (msaaSamples > 1)
                bytes = checked(bytes + renderPixels * 8L * msaaSamples * InFlightFrames);
            if (effectiveSsaa > 1)
                bytes = checked(bytes + renderPixels * 8L * InFlightFrames);

            if (!hasTemporal)
            {
                bytes = aa switch
                {
                    FrameAntialiasing.Smaa1X => checked(bytes + renderPixels * 12L * InFlightFrames),
                    FrameAntialiasing.Fxaa => checked(bytes + renderPixels * 4L * InFlightFrames),
                    FrameAntialiasing.Taa => checked(bytes + renderPixels * (4L * 8L + 8L * InFlightFrames)),
                    _ => bytes,
                };
            }

            if (hasTemporal)
                bytes = checked(bytes + displayPixels * 28L + renderPixels * 16L);
            else if (hasFsr1)
                bytes = checked(bytes + displayPixels * (upscaling.EnableRcas ? 20L : 4L) * InFlightFrames);
            else if (rw != vw || rh != vh)
                bytes = checked(bytes + displayPixels * 4L * InFlightFrames);

            int shadowRes = quality.EffectiveShadow.Resolution;
            if (shadowRes > 0)
            {
                long shadowPixels = (long)shadowRes * shadowRes;
                bytes = checked(bytes + shadowPixels * 8L * InFlightFrames);
            }

            if (quality.EffectiveScreenSpaceAo.IsActive)
                bytes = checked(bytes + renderPixels * (5L + (msaaSamples > 1 ? 4L : 0L)) * InFlightFrames);

            var gi = quality.EffectiveGi;
            if (gi.Enabled)
            {
                bool halfRes = gi.PreviewAcceleration && rw >= 2 && rh >= 2;
                int traceW = halfRes ? (rw + 1) / 2 : rw;
                int traceH = halfRes ? (rh + 1) / 2 : rh;
                long tracePixels = (long)traceW * traceH;
                bytes = checked(bytes + tracePixels * 52L * effectiveSsaa * InFlightFrames);
                if (gi.Denoiser == RayGiDenoiser.TemporalSpatial)
                    bytes = checked(bytes + tracePixels * 128L * effectiveSsaa);
            }
        }

        if (quality.EffectiveDiffusion.Enabled)
            bytes = checked(bytes + displayPixels * 8L * InFlightFrames);
        if (quality.EffectiveSharpening.Enabled)
            bytes = checked(bytes + displayPixels * 4L * InFlightFrames);

        double mib = bytes / (double)Mebibyte;
        double? ratio = normalizedBudget is { } budget ? bytes / (double)budget : null;
        bool isOverBudget = normalizedBudget is { } limit && (ulong)bytes > limit;
        var loadTier = ComputeLoadTier(quality, renderPixels, pathTracing, hasTemporal, msaaSamples, effectiveSsaa);

        return new(
            vw, vh, rw, rh,
            bytes, mib,
            normalizedBudget, ratio, isOverBudget,
            loadTier,
            IsClassicEngine: false);
    }

    private static (int RenderWidth, int RenderHeight) DeriveRenderDimensions(
        int displayWidth,
        int displayHeight,
        ViewQualityOptions quality,
        bool pathTracing,
        bool hasTemporal,
        bool hasFsr1,
        int rawSpatialSamples)
    {
        if (hasTemporal)
        {
            var t = quality.EffectiveTemporalUpscaling;
            double scale = t.Mode switch
            {
                ViewTemporalUpscalingMode.Fsr => t.QualityPreset switch
                {
                    ViewTemporalQualityPreset.NativeAa => 1.0,
                    ViewTemporalQualityPreset.Quality => 1.5,
                    ViewTemporalQualityPreset.Balanced => 1.7,
                    ViewTemporalQualityPreset.Performance => 2.0,
                    ViewTemporalQualityPreset.UltraPerformance => 3.0,
                    _ => 1.0,
                },
                ViewTemporalUpscalingMode.XeSs => t.XeSsQuality switch
                {
                    ViewXeSsQuality.NativeAa => 1.0,
                    ViewXeSsQuality.UltraQualityPlus => 1.3,
                    ViewXeSsQuality.UltraQuality => 1.3,
                    ViewXeSsQuality.Quality => 1.5,
                    ViewXeSsQuality.Balanced => 1.7,
                    ViewXeSsQuality.Performance => 2.0,
                    ViewXeSsQuality.UltraPerformance => 2.3,
                    _ => 1.0,
                },
                ViewTemporalUpscalingMode.Dlss => t.DlssQuality switch
                {
                    ViewDlssQuality.NativeAa => 1.0,
                    ViewDlssQuality.UltraQuality => 1.3,
                    ViewDlssQuality.Quality => 1.5,
                    ViewDlssQuality.Balanced => 1.7,
                    ViewDlssQuality.Performance => 2.0,
                    ViewDlssQuality.UltraPerformance => 3.0,
                    _ => 1.0,
                },
                _ => 1.0,
            };
            return (Math.Max(1, (int)Math.Floor(displayWidth / scale)),
                    Math.Max(1, (int)Math.Floor(displayHeight / scale)));
        }

        if (hasFsr1)
        {
            double scale = quality.EffectiveUpscaling.QualityPreset switch
            {
                ViewFsr1QualityPreset.UltraQuality => 1.3,
                ViewFsr1QualityPreset.Quality => 1.5,
                ViewFsr1QualityPreset.Balanced => 1.7,
                ViewFsr1QualityPreset.Performance => 2.0,
                _ => 1.5,
            };
            return (Math.Max(1, (int)Math.Floor(displayWidth / scale)),
                    Math.Max(1, (int)Math.Floor(displayHeight / scale)));
        }

        float renderScale = float.IsFinite(quality.RenderScale) ? Math.Clamp(quality.RenderScale, 0.5f, 1.5f) : 1f;
        if (!pathTracing && rawSpatialSamples > 1)
            renderScale = 1f;
        if (renderScale == 1f)
            return (displayWidth, displayHeight);

        float clamped = Math.Min(renderScale, 4096f / Math.Max(displayWidth, displayHeight));
        return (Math.Clamp((int)MathF.Round(displayWidth * clamped), 1, 4096),
                Math.Clamp((int)MathF.Round(displayHeight * clamped), 1, 4096));
    }

    private static PreviewPerformanceLoadTier ComputeLoadTier(
        ViewQualityOptions quality,
        long renderPixels,
        bool pathTracing,
        bool hasTemporal,
        int msaaSamples,
        int effectiveSsaa)
    {
        double resFactor = Math.Clamp(renderPixels / (1280d * 720d), 0.35, 6.0);
        if (pathTracing)
        {
            var pt = quality.EffectivePathTracing;
            double denoiserWeight = pt.Denoiser == RayGiDenoiser.TemporalSpatial ? 1.5 : 0.5;
            double ptCost = resFactor * (pt.SamplesPerFrame * 2.5 + pt.MaxBounces * 1.2 + denoiserWeight);
            return ptCost >= 7.0 ? PreviewPerformanceLoadTier.Heavy : PreviewPerformanceLoadTier.High;
        }

        double cost = resFactor;
        if (quality.PipelineMode == RenderPipelineMode.ClusteredForward)
            cost *= 1.15;

        cost *= msaaSamples switch
        {
            8 => 2.4,
            4 => 1.7,
            2 => 1.3,
            _ => 1.0,
        };
        cost *= effectiveSsaa;

        if (!hasTemporal)
        {
            cost += quality.Antialiasing switch
            {
                FrameAntialiasing.Taa => 0.45 * resFactor,
                FrameAntialiasing.Smaa1X => 0.35 * resFactor,
                FrameAntialiasing.Fxaa => 0.1 * resFactor,
                _ => 0.0,
            };
        }

        int shadowRes = quality.EffectiveShadow.Resolution;
        if (shadowRes >= 4096) cost += 0.9;
        else if (shadowRes >= 2048) cost += 0.45;
        else if (shadowRes > 0) cost += 0.2;

        var shadowFilter = quality.ShadowFilter
            ?? (quality.EffectiveShadow.Sampling == OriginalTextureSampling.PointClamp ? ShadowFilterMode.Point : ShadowFilterMode.Linear);
        if (shadowRes > 0 && shadowFilter == ShadowFilterMode.PcfSoft)
            cost += 0.35;

        if (quality.EffectiveScreenSpaceAo.IsActive)
            cost += (quality.EffectiveScreenSpaceAo.SampleCount >= 16 ? 0.9 : 0.45) * resFactor;

        var gi = quality.EffectiveGi;
        if (gi.Enabled)
        {
            double giBase = gi.PreviewAcceleration ? 2.2 : 4.2;
            double sppFactor = Math.Clamp(gi.SamplesPerPixel / 16.0, 0.5, 4.0);
            cost += giBase * resFactor * effectiveSsaa * sppFactor;
        }

        if (quality.EffectiveDiffusion.Enabled)
            cost += 0.3 * resFactor;

        if (cost >= 4.5) return PreviewPerformanceLoadTier.Heavy;
        if (cost >= 2.75) return PreviewPerformanceLoadTier.High;
        if (cost >= 1.65) return PreviewPerformanceLoadTier.Moderate;
        return PreviewPerformanceLoadTier.Light;
    }
}
