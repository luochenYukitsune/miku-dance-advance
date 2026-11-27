using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

internal static class D3D12ViewQuality
{
    internal static void ValidateDiffusion(OriginalFrameScene scene, ViewQualityOptions quality)
    {
        var options = quality.EffectiveDiffusion;
        options.Validate();
        if (!options.Enabled || options.Strength == 0) return;
        if (scene.Options?.AlphaOutput != FrameAlphaOutput.Straight
            && scene.Options?.BackgroundColor is { W: not 1f })
            throw new NotSupportedException("Native Diffusion requires opaque input or explicit straight-alpha coverage.");
    }

    internal static D3D12DiffusionOptions Diffusion(ViewQualityOptions quality)
    {
        quality.Validate();
        var options = quality.EffectiveDiffusion;
        return new(options.Enabled, options.Strength, options.Extent);
    }

    /// <summary>GI owns separate history resources, but follows the same host seek and camera-cut markers as AA.
    /// Preview acceleration is strictly isolated to realtime view rendering and always stripped on offline capture/export.</summary>
    internal static RayGiOptions Gi(ViewQualityOptions quality, ViewTemporalFrameState? state = null,
        bool allowPreviewAcceleration = false)
    {
        quality.Validate(); state ??= new(); state.Validate();
        if (quality.PipelineMode == RenderPipelineMode.PathTracing)
            return RayGiOptions.Off;
        var options = quality.EffectiveGi;
        bool previewAcceleration = allowPreviewAcceleration && options.PreviewAcceleration;
        if (options.Denoiser != RayGiDenoiser.TemporalSpatial)
            return previewAcceleration == options.PreviewAcceleration
                ? options
                : options with { PreviewAcceleration = previewAcceleration };
        return options with
        {
            TemporalHistoryGeneration = state.HistoryGeneration,
            TemporalCameraCutKey = state.CameraCutKey,
            ResetTemporalHistory = state.ResetHistory,
            PreviewAcceleration = previewAcceleration
        };
    }

    internal static PathTracingOptions? PathTracing(ViewQualityOptions quality, ViewTemporalFrameState? state = null,
        bool interactive = false, PathTracingOptions? scenePathTracing = null)
    {
        quality.Validate(); state ??= new(); state.Validate();
        if (quality.PipelineMode != RenderPipelineMode.PathTracing) return null;
        var effective = quality.PathTracing ?? scenePathTracing ?? PathTracingOptions.Default;
        effective.Validate();
        return effective with
        {
            HistoryGeneration = state.HistoryGeneration,
            CameraCutKey = state.CameraCutKey,
            ResetHistory = state.ResetHistory,
            Interactive = interactive,
        };
    }

    internal static D3D12TemporalUpscalingOptions Temporal(ViewQualityOptions quality,
        ViewTemporalFrameState? state = null, string? loaderPath = null, string? xessLibraryPath = null,
        string? dlssBridgePath = null, string? dlssSdkDirectory = null)
    {
        quality.Validate(); state ??= new(); state.Validate();
        var options = quality.EffectiveTemporalUpscaling;
        return new()
        {
            Mode = options.Mode switch
            {
                ViewTemporalUpscalingMode.Off => D3D12TemporalUpscalingMode.Off,
                ViewTemporalUpscalingMode.Fsr => D3D12TemporalUpscalingMode.Fsr,
                ViewTemporalUpscalingMode.XeSs => D3D12TemporalUpscalingMode.XeSs,
                ViewTemporalUpscalingMode.Dlss => D3D12TemporalUpscalingMode.Dlss,
                _ => throw new ArgumentOutOfRangeException(nameof(options.Mode))
            },
            QualityPreset = options.QualityPreset switch
            {
                ViewTemporalQualityPreset.NativeAa => D3D12FsrQualityPreset.NativeAa,
                ViewTemporalQualityPreset.Quality => D3D12FsrQualityPreset.Quality,
                ViewTemporalQualityPreset.Balanced => D3D12FsrQualityPreset.Balanced,
                ViewTemporalQualityPreset.Performance => D3D12FsrQualityPreset.Performance,
                ViewTemporalQualityPreset.UltraPerformance => D3D12FsrQualityPreset.UltraPerformance,
                _ => throw new ArgumentOutOfRangeException(nameof(options.QualityPreset))
            },
            XeSsQuality = options.XeSsQuality switch
            {
                ViewXeSsQuality.UltraPerformance => D3D12XeSsQuality.UltraPerformance,
                ViewXeSsQuality.Performance => D3D12XeSsQuality.Performance,
                ViewXeSsQuality.Balanced => D3D12XeSsQuality.Balanced,
                ViewXeSsQuality.Quality => D3D12XeSsQuality.Quality,
                ViewXeSsQuality.UltraQuality => D3D12XeSsQuality.UltraQuality,
                ViewXeSsQuality.UltraQualityPlus => D3D12XeSsQuality.UltraQualityPlus,
                ViewXeSsQuality.NativeAa => D3D12XeSsQuality.AntiAliasing,
                _ => throw new ArgumentOutOfRangeException(nameof(options.XeSsQuality))
            },
            DlssQuality = options.DlssQuality switch
            {
                ViewDlssQuality.Performance => D3D12DlssQuality.Performance,
                ViewDlssQuality.Balanced => D3D12DlssQuality.Balanced,
                ViewDlssQuality.Quality => D3D12DlssQuality.Quality,
                ViewDlssQuality.UltraPerformance => D3D12DlssQuality.UltraPerformance,
                ViewDlssQuality.UltraQuality => D3D12DlssQuality.UltraQuality,
                ViewDlssQuality.NativeAa => D3D12DlssQuality.NativeAa,
                _ => throw new ArgumentOutOfRangeException(nameof(options.DlssQuality))
            },
            // Leave an unspecified provider path null so its own backend environment setting is used.
            // Each explicit component path belongs only to its selected provider.
            LoaderPath = options.Mode switch
            {
                ViewTemporalUpscalingMode.XeSs => xessLibraryPath,
                ViewTemporalUpscalingMode.Dlss => dlssBridgePath,
                _ => loaderPath
            },
            DlssSdkDirectory = options.Mode == ViewTemporalUpscalingMode.Dlss ? dlssSdkDirectory : null,
            FrameTimeMilliseconds = state.FrameTimeMilliseconds ?? options.FrameTimeMilliseconds,
            ResetHistory = state.ResetHistory,
            HistoryGeneration = state.HistoryGeneration,
            CameraCutKey = state.CameraCutKey
        };
    }

    internal static D3D12SpatialUpscalingOptions Upscaling(ViewQualityOptions quality)
    {
        quality.Validate();
        var options = quality.EffectiveUpscaling;
        return new()
        {
            Mode = options.Mode switch
            {
                ViewUpscalingMode.Off => D3D12SpatialUpscalingMode.Off,
                ViewUpscalingMode.Fsr1 => D3D12SpatialUpscalingMode.Fsr1,
                _ => throw new ArgumentOutOfRangeException(nameof(options.Mode))
            },
            QualityPreset = options.QualityPreset switch
            {
                ViewFsr1QualityPreset.UltraQuality => D3D12Fsr1QualityPreset.UltraQuality,
                ViewFsr1QualityPreset.Quality => D3D12Fsr1QualityPreset.Quality,
                ViewFsr1QualityPreset.Balanced => D3D12Fsr1QualityPreset.Balanced,
                ViewFsr1QualityPreset.Performance => D3D12Fsr1QualityPreset.Performance,
                _ => throw new ArgumentOutOfRangeException(nameof(options.QualityPreset))
            },
            EnableRcas = options.EnableRcas,
            RcasSharpness = options.RcasSharpness
        };
    }
}
