using System.Numerics;
using Mmd.Modern.Core;
using Vortice.Direct3D12;

namespace Mmd.Modern.Rendering.D3D12;

internal static class CharacterOutlineRasterization
{
    // Positive raster depth units place the modern hull just behind coplanar cloth.
    // D24 uses fixed-point depth steps; D32 scales steps to the primitive's depth exponent.
    internal const int DepthBias = 16;
    internal static RasterizerDescription Biased(RasterizerDescription raster)
    {
        raster.DepthBias = DepthBias;
        raster.SlopeScaledDepthBias = 0;
        return raster;
    }
}

public sealed partial class D3D12OriginalStyleRenderer
{
    // Original ground projection and modern hull have independent single-snapshot caches.
    private readonly D3D12OutlinePositionCache characterPositionCache=new();

    private D3D12ExpandedPositions GetOutlinePositions(OriginalFrameScene frame)
    {
        using var positionsCpu=MeasureSceneCpu(SceneCpuStage.ModernPositions);
        var positions=characterPositionCache.GetChunks(frame.Outline!,frame.OutlineOptions!,frame.Materials);
        if(sceneRecordCpuActive)sceneCpuModernExpandedVertices+=characterPositionCache.LastExpandedVertices;
        return positions;
    }
}
