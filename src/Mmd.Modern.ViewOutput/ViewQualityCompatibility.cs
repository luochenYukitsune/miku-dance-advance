using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput;

public sealed record ViewQualityCompatibilityIssue(
    string Code,
    string LocalizationKey,
    string DefaultMessage,
    string Category);

public sealed record ViewQualityCompatibilityReport(IReadOnlyList<ViewQualityCompatibilityIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

/// <summary>Evaluates ViewQualityOptions compatibility rules and produces clear localized issue descriptors.</summary>
public static class ViewQualityCompatibility
{
    public static ViewQualityCompatibilityReport Evaluate(ViewQualityOptions quality)
    {
        ArgumentNullException.ThrowIfNull(quality);
        var issues = new List<ViewQualityCompatibilityIssue>();

        var shadow = quality.EffectiveShadow;
        if (!ViewShadowOptions.IsSupportedResolution(shadow.Resolution))
        {
            issues.Add(new(
                "ShadowResolution",
                "Mda.Text.Preview.Conflict.ShadowResolution",
                "自阴影分辨率必须为 0（关闭）、512、1024、2048 或 4096。",
                "Shadow"));
        }
        if (!ViewShadowOptions.IsSupportedSampling(shadow.Sampling))
        {
            issues.Add(new(
                "ShadowSampling",
                "Mda.Text.Preview.Conflict.ShadowSampling",
                "自阴影过滤方式必须为平滑过滤（LinearClamp）或像素硬边（PointClamp）。",
                "Shadow"));
        }
        if (quality.ShadowFilter is { } shadowFilter && !Enum.IsDefined(shadowFilter))
        {
            issues.Add(new(
                "ShadowFilter",
                "Mda.Text.Preview.Conflict.ShadowFilter",
                "自阴影过滤模式不受支持。",
                "Shadow"));
        }
        if (quality.ShadowSoftness is { } shadowSoftness && (!float.IsFinite(shadowSoftness) || shadowSoftness is < 0f or > 4f))
        {
            issues.Add(new(
                "ShadowSoftness",
                "Mda.Text.Preview.Conflict.ShadowSoftness",
                "PCF 阴影柔和度必须在 0 到 4 之间。",
                "Shadow"));
        }
        if (quality.AnisotropicFiltering is { } aniso && !OriginalFrameMath.IsValidAnisotropicFiltering(aniso))
        {
            issues.Add(new(
                "AnisotropicFiltering",
                "Mda.Text.Preview.Conflict.AnisotropicFiltering",
                "各向异性过滤倍率必须为 1×、2×、4×、8× 或 16×。",
                "Shadow"));
        }

        if (!float.IsFinite(quality.RenderScale) || quality.RenderScale is < 0.5f or > 1.5f)
        {
            issues.Add(new(
                "RenderScaleRange",
                "Mda.Text.Preview.Conflict.RenderScaleRange",
                "渲染比例必须在 50% 到 150%（0.5–1.5）之间。",
                "Antialiasing"));
        }

        var sharpening = quality.EffectiveSharpening;
        if (!float.IsFinite(sharpening.Sharpness) || sharpening.Sharpness is < 0f or > 2f)
        {
            issues.Add(new(
                "SharpeningRange",
                "Mda.Text.Preview.Conflict.SharpeningRange",
                "RCAS 锐化衰减必须在 0 到 2 之间。",
                "Antialiasing"));
        }

        if (!float.IsFinite(quality.PreviewOutlineWidthScale) || quality.PreviewOutlineWidthScale is < 0f or > 4f)
        {
            issues.Add(new(
                "OutlineScaleRange",
                "Mda.Text.Preview.Conflict.OutlineScaleRange",
                "预览描边倍率必须在 0 到 4 之间。",
                "Shadow"));
        }

        if (!Enum.IsDefined(quality.PipelineMode))
        {
            issues.Add(new(
                "PipelineMode",
                "Mda.Text.Preview.Conflict.PipelineMode",
                "渲染管线模式不受支持。",
                "Gi"));
        }

        var pt = quality.EffectivePathTracing;
        if (!Enum.IsDefined(pt.ShadingMode)
            || pt.SamplesPerFrame is < 1 or > 64
            || pt.MaxBounces is < 1 or > 8
            || pt.MaxAccumulatedFrames is < 1 or > 4096
            || pt.InteractiveSamplesPerFrame is < 1 or > 16
            || !float.IsFinite(pt.FireflyClamp) || pt.FireflyClamp is < 1f or > 1000f
            || !Enum.IsDefined(pt.Denoiser))
        {
            issues.Add(new(
                "PathTracingRange",
                "Mda.Text.Preview.Conflict.PathTracingRange",
                "路径追踪参数超出支持范围。",
                "Gi"));
        }

        var ssao = quality.EffectiveScreenSpaceAo;
        if (!float.IsFinite(ssao.Strength) || ssao.Strength is < 0f or > 1f
            || !float.IsFinite(ssao.Radius) || ssao.Radius is < 0.1f or > 8f
            || ssao.SampleCount is not (4 or 8 or 16))
        {
            issues.Add(new(
                "SsaoRange",
                "Mda.Text.Preview.Conflict.SsaoRange",
                "屏幕空间 AO 强度须在 0–1、半径在 0.1–8、采样数为 4/8/16。",
                "Shadow"));
        }

        bool hasTemporal = quality.EffectiveTemporalUpscaling.Mode != ViewTemporalUpscalingMode.Off;
        bool hasSpatialUpscaling = quality.EffectiveUpscaling.Mode != ViewUpscalingMode.Off;
        bool hasAa = quality.Antialiasing is not (null or FrameAntialiasing.Off);
        bool hasSsaa = quality.SpatialSamples is > 1;
        bool isPathTracing = quality.PipelineMode == RenderPipelineMode.PathTracing;

        if (isPathTracing && (hasAa || hasSsaa || hasSpatialUpscaling || hasTemporal))
        {
            issues.Add(new(
                "PathTracingExcludesRasterAaAndUpscaling",
                "Mda.Text.Preview.Conflict.PathTracingExcludesRasterAaAndUpscaling",
                "完整路径追踪已自带子像素抗锯齿，启用时必须关闭传统抗锯齿、精细采样、FSR 1 与时域重建。",
                "Gi"));
        }

        if (hasTemporal && (hasSpatialUpscaling || hasAa || hasSsaa))
        {
            issues.Add(new(
                "TemporalWithSpatial",
                "Mda.Text.Preview.Conflict.TemporalWithSpatial",
                "时域重建启用时，空间超分、常规抗锯齿和精细采样（SSAA > 1×）必须关闭。",
                "Antialiasing"));
        }

        if (quality.Antialiasing == FrameAntialiasing.Taa && (hasSsaa || hasSpatialUpscaling))
        {
            issues.Add(new(
                "TaaConflict",
                "Mda.Text.Preview.Conflict.TaaConflict",
                "内置 TAA 不能与精细采样（SSAA > 1×）或 FSR 1 空间超分同时启用。",
                "Antialiasing"));
        }

        if (quality.Antialiasing is { } msaa && OriginalFrameMath.IsMsaa(msaa) && (hasSsaa || hasSpatialUpscaling))
        {
            issues.Add(new(
                "MsaaConflict",
                "Mda.Text.Preview.Conflict.MsaaConflict",
                "硬件 MSAA 不能与精细采样（SSAA > 1×）或 FSR 1 空间超分同时启用。",
                "Antialiasing"));
        }

        if (quality.RenderScale != 1f)
        {
            if (hasSpatialUpscaling || hasTemporal)
            {
                issues.Add(new(
                    "RenderScaleWithUpscaling",
                    "Mda.Text.Preview.Conflict.RenderScaleWithUpscaling",
                    "渲染比例不为 100% 时，不能与空间超分或时域重建同时启用。",
                    "Antialiasing"));
            }
            if (hasSsaa)
            {
                issues.Add(new(
                    "RenderScaleWithSsaa",
                    "Mda.Text.Preview.Conflict.RenderScaleWithSsaa",
                    "渲染比例不为 100% 时，不能与大于 1× 的精细采样（SSAA）同时启用。",
                    "Antialiasing"));
            }
        }

        var gi = quality.EffectiveGi;
        if (!isPathTracing && ssao.Enabled && gi.Enabled && gi.OcclusionStrength > 0f)
        {
            issues.Add(new(
                "SsaoWithRayGiOcclusion",
                "Mda.Text.Preview.Conflict.SsaoWithRayGiOcclusion",
                "屏幕空间 AO 与光追 GI 环境光遮蔽（强度 > 0）互斥，请只保留一项。",
                "Shadow"));
        }

        if (!isPathTracing && gi.ToonResponse != RayGiToonResponse.Continuous && gi.Composition != RayGiComposition.ToonPalette)
        {
            issues.Add(new(
                "GiToonResponseRequiresPalette",
                "Mda.Text.Preview.Conflict.GiToonResponseRequiresPalette",
                "卡通色阶响应仅在受光方式设为「卡通色阶」时可用。",
                "Gi"));
        }
        if (!isPathTracing && gi.Denoiser != RayGiDenoiser.Off && gi.Composition != RayGiComposition.ToonPalette)
        {
            issues.Add(new(
                "GiDenoiserRequiresPalette",
                "Mda.Text.Preview.Conflict.GiDenoiserRequiresPalette",
                "GI 降噪仅在受光方式设为「卡通色阶」时可用。",
                "Gi"));
        }
        if (!isPathTracing && (!float.IsFinite(gi.RayEpsilon) || !float.IsFinite(gi.MaxRayDistance)
            || gi.RayEpsilon <= 0f || gi.MaxRayDistance <= 0f || gi.RayEpsilon >= gi.MaxRayDistance))
        {
            issues.Add(new(
                "GiRayEpsilon",
                "Mda.Text.Preview.Conflict.GiRayEpsilon",
                "光线偏移（RayEpsilon）必须大于 0 且小于最大光线距离（MaxRayDistance）。",
                "Gi"));
        }
        if (!isPathTracing && (!float.IsFinite(gi.OcclusionRadius) || gi.OcclusionRadius <= 0f
            || (gi.OcclusionStrength > 0f && gi.OcclusionRadius <= gi.RayEpsilon)))
        {
            issues.Add(new(
                "GiOcclusionRadius",
                "Mda.Text.Preview.Conflict.GiOcclusionRadius",
                "启用环境光遮蔽时，遮蔽半径（OcclusionRadius）必须大于光线偏移（RayEpsilon）。",
                "Gi"));
        }

        return new(issues);
    }
}
