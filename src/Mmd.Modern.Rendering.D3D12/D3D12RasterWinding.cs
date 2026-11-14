using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>PMX front faces use opposite projected winding under left- and right-handed
/// cameras. World reflections still reverse which faces are front-facing.
/// Light-space shadow passes use their own projection contract and must not inherit this camera bit.</summary>
internal static class D3D12RasterWinding
{
    internal static bool FrontCounterClockwise(OriginalFrameScene frame)
    {
        var scene = frame.Surface.Snapshot;
        var camera = frame.Options?.CameraOverride ?? scene.Camera;
        return !camera.LeftHanded;
    }
}
