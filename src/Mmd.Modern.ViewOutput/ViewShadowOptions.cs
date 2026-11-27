using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput;

/// <summary>Per-view self-shadow map resolution and sampling override. Resolution 0 disables self-shadow.</summary>
public sealed record ViewShadowOptions(int Resolution = 2048, OriginalTextureSampling Sampling = OriginalTextureSampling.LinearClamp)
{
    public static ViewShadowOptions Default { get; } = new();

    public static bool IsSupportedResolution(int resolution) => resolution is 0 or 512 or 1024 or 2048 or 4096;

    public static bool IsSupportedSampling(OriginalTextureSampling sampling)
        => sampling is OriginalTextureSampling.LinearClamp or OriginalTextureSampling.PointClamp;

    public void Validate()
    {
        if (!IsSupportedResolution(Resolution))
            throw new ArgumentOutOfRangeException(nameof(Resolution), "Shadow resolution must be 0, 512, 1024, 2048, or 4096.");
        if (!IsSupportedSampling(Sampling))
            throw new ArgumentOutOfRangeException(nameof(Sampling), "Shadow sampling must be LinearClamp or PointClamp.");
    }
}
