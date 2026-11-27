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

        if (!float.IsFinite(quality.RenderScale) || quality.RenderScale is < 0.5f or > 1.5f)
        {
            issues.Add(new(
                "RenderScaleRange",
                "Mda.Text.Preview.Conflict.RenderScaleRange",
                "渲染比例必须在 50% 到 150%（0.5–1.5）之间。",
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

        bool hasTemporal = quality.EffectiveTemporalUpscaling.Mode != ViewTemporalUpscalingMode.Off;
        bool hasSpatialUpscaling = quality.EffectiveUpscaling.Mode != ViewUpscalingMode.Off;
        bool hasAa = quality.Antialiasing is not (null or FrameAntialiasing.Off);
        bool hasSsaa = quality.SpatialSamples is > 1;

        if (hasTemporal && (hasSpatialUpscaling || hasAa || hasSsaa))
        {
            issues.Add(new(
                "TemporalWithSpatial",
                "Mda.Text.Preview.Conflict.TemporalWithSpatial",
                "时域重建启用时，空间超分、边缘平滑（SMAA）和精细采样（SSAA > 1×）必须关闭。",
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
        if (gi.ToonResponse != RayGiToonResponse.Continuous && gi.Composition != RayGiComposition.ToonPalette)
        {
            issues.Add(new(
                "GiToonResponseRequiresPalette",
                "Mda.Text.Preview.Conflict.GiToonResponseRequiresPalette",
                "卡通色阶响应仅在受光方式设为「卡通色阶」时可用。",
                "Gi"));
        }
        if (gi.Denoiser != RayGiDenoiser.Off && gi.Composition != RayGiComposition.ToonPalette)
        {
            issues.Add(new(
                "GiDenoiserRequiresPalette",
                "Mda.Text.Preview.Conflict.GiDenoiserRequiresPalette",
                "GI 降噪仅在受光方式设为「卡通色阶」时可用。",
                "Gi"));
        }
        if (!float.IsFinite(gi.RayEpsilon) || !float.IsFinite(gi.MaxRayDistance)
            || gi.RayEpsilon <= 0f || gi.MaxRayDistance <= 0f || gi.RayEpsilon >= gi.MaxRayDistance)
        {
            issues.Add(new(
                "GiRayEpsilon",
                "Mda.Text.Preview.Conflict.GiRayEpsilon",
                "光线偏移（RayEpsilon）必须大于 0 且小于最大光线距离（MaxRayDistance）。",
                "Gi"));
        }
        if (!float.IsFinite(gi.OcclusionRadius) || gi.OcclusionRadius <= 0f
            || (gi.OcclusionStrength > 0f && gi.OcclusionRadius <= gi.RayEpsilon))
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
