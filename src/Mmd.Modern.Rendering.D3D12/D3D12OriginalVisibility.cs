using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Counts actual scene recordings in the current GPU frame, including effect Scene redraws.
/// Bounds build counts are renderer-lifetime totals. Shadow/ground remain conservatively unculled.</summary>
public sealed record D3D12VisibilityStatistics(long FrameId,bool Enabled,int SurfaceTested,int SurfaceCulled,int SurfaceDrawn,
    int OutlineTested,int OutlineCulled,int OutlineDrawn,int ShadowDrawn,int GroundDrawn,long SurfaceBoundsBuilds,long OutlineBoundsBuilds);

public sealed partial class D3D12OriginalStyleRenderer
{
    private long visibilityFrameId=-1;
    private bool visibilityAvailable,visibilityEnabled;
    private int surfaceTested,surfaceCulled,surfaceDrawn,outlineTested,outlineCulled,outlineDrawn,shadowDrawn,groundDrawn;
    private long surfaceBoundsBuilds=0,outlineBoundsBuilds=0;
    private readonly D3D12VisibilityBoundsCache surfaceVisibilityCache=new(2);
    private OriginalVisibilityBounds[]? activeSurfaceBounds;
    // Original and modern hulls can coexist in one recording. Two bounded entries
    // prevent their alternating position streams from invalidating each other.
    private readonly D3D12VisibilityBoundsCache originalOutlineVisibilityCache=new(1),modernOutlineVisibilityCache=new(1);
    public D3D12VisibilityStatistics? LastVisibilityStatistics=>visibilityAvailable&&visibilityFrameId==runtime.FrameId
        ?new(visibilityFrameId,visibilityEnabled,surfaceTested,surfaceCulled,surfaceDrawn,outlineTested,outlineCulled,outlineDrawn,
            shadowDrawn,groundDrawn,surfaceBoundsBuilds,outlineBoundsBuilds):null;

    private void BeginVisibility(OriginalFrameScene frame)
    {
        if(visibilityFrameId!=runtime.FrameId)
        {
            visibilityFrameId=runtime.FrameId;
            surfaceTested=surfaceCulled=surfaceDrawn=outlineTested=outlineCulled=outlineDrawn=shadowDrawn=groundDrawn=0;
        }
        visibilityAvailable=frame.Options is not null;
        visibilityEnabled=frame.Options?.EnableFrustumCulling==true;
        activeSurfaceBounds=null;
    }

    private bool ShouldDrawSurface(OriginalFrameScene frame,int materialIndex,Matrix4x4 viewProjection)
    {
        if(visibilityEnabled)
        {
            surfaceTested++;
            var scene=frame.Surface.Snapshot;
            activeSurfaceBounds??=GetSurfaceVisibilityBounds(scene);
            if(!OriginalFrustumVisibility.IsPotentiallyVisible(activeSurfaceBounds[materialIndex],scene.World,viewProjection))
            {surfaceCulled++;return false;}
        }
        surfaceDrawn++;
        return true;
    }

    private bool ShouldDrawOutline(OriginalFrameScene frame,int materialIndex,D3D12ExpandedPositions positions,Matrix4x4 worldViewProjection)
    {
        if(!visibilityAvailable)return true;
        if(visibilityEnabled)
        {
            outlineTested++;
            var scene=frame.Surface.Snapshot;
            bool modern=frame.Materials[materialIndex].CharacterOutlineStyle is not null;
            var bounds=frame.Materials[materialIndex].OutlineWidthScale==1
                ?GetOutlineVisibilityBounds(scene,positions,modern)[materialIndex]
                :D3D12OutlineDrawScale.MaterialBounds(frame,materialIndex,positions);
            if(!OriginalFrustumVisibility.IsPotentiallyVisible(bounds,worldViewProjection))
            {outlineCulled++;return false;}
        }
        outlineDrawn++;
        return true;
    }

    private bool[] GetDeferredMaterialMask(SceneSnapshot scene)
        =>surfaceVisibilityCache.GetDeferredMaterialMask(scene);

    private bool ShouldDrawOutlineDeferred(OriginalFrameScene frame,int materialIndex,bool[] deferredMaterials,Matrix4x4 worldViewProjection)
    {
        if(!visibilityAvailable)return true;
        if(visibilityEnabled)
        {
            outlineTested++;
            if(!deferredMaterials[materialIndex])
            {
                var bounds=StaticOutlineMaterialBounds(frame,materialIndex);
                if(!OriginalFrustumVisibility.IsPotentiallyVisible(bounds,worldViewProjection))
                {outlineCulled++;return false;}
            }
        }
        outlineDrawn++;
        return true;
    }

    private static OriginalVisibilityBounds StaticOutlineMaterialBounds(OriginalFrameScene frame,int materialIndex)
    {
        var scene=frame.Surface.Snapshot;
        var staticOutline=frame.Outline!.StaticView;
        var range=scene.Materials[materialIndex];
        float modelBase=OriginalOutlineMath.ComputeModelBase(frame.OutlineOptions!);
        float scale=frame.Materials[materialIndex].OutlineWidthScale;
        var style=frame.Materials[materialIndex].CharacterOutlineStyle;
        if(style is not null)CharacterOutlineMath.Validate(style);
        var min=new Vector3(float.PositiveInfinity);var max=new Vector3(float.NegativeInfinity);
        for(int i=range.StartIndex;i<range.StartIndex+range.IndexCount;i++)
        {
            int vertex=(int)scene.Indices[i];
            var ov=staticOutline.Vertices[vertex];
            float width=style?.Width??staticOutline.Materials[ov.SelectedMaterialIndex].Width;
            var expanded=OriginalOutlineMath.ExpandPosition(ov,modelBase,width);
            var p=scale==1||ov.Mode==OriginalOutlineVertexMode.Inward?expanded:ov.Position+(expanded-ov.Position)*scale;
            if(!float.IsFinite(p.X)||!float.IsFinite(p.Y)||!float.IsFinite(p.Z))
                return new(new(float.NaN),new(float.NaN));
            min=Vector3.Min(min,p);max=Vector3.Max(max,p);
        }
        return new(min,max);
    }

    private OriginalVisibilityBounds[] GetSurfaceVisibilityBounds(SceneSnapshot scene)
    {
        using var lookupCpu=MeasureSceneCpu(SceneCpuStage.SurfaceBoundsLookup);
        bool current=surfaceVisibilityCache.TryGetCurrent(scene,null,out var bounds);
        if(sceneRecordCpuActive)sceneCpuBoundsMaterialRangeComparisons+=surfaceVisibilityCache.LastMaterialRangeComparisons;
        if(current)return bounds!;
        using var buildCpu=MeasureSceneCpu(SceneCpuStage.SurfaceBoundsBuild);
        surfaceVisibilityCache.MeasureCpu=sceneRecordCpuActive;
        try{bounds=surfaceVisibilityCache.GetSurface(scene);}
        finally{surfaceVisibilityCache.MeasureCpu=false;}
        if(sceneRecordCpuActive)sceneCpuBoundsMaterialRangeComparisons+=surfaceVisibilityCache.LastMaterialRangeComparisons;
        if(sceneRecordCpuActive)AccumulateBoundsCpu(surfaceVisibilityCache,outline:false);
        if(surfaceVisibilityCache.LastTopologyRebuilt||surfaceVisibilityCache.LastRebuiltMaterials>0)surfaceBoundsBuilds++;
        return bounds;
    }

    private OriginalVisibilityBounds[] GetOutlineVisibilityBounds(SceneSnapshot scene,D3D12ExpandedPositions positions,bool modern)
    {
        using var lookupCpu=MeasureSceneCpu(SceneCpuStage.OutlineBoundsLookup);
        var cache=modern?modernOutlineVisibilityCache:originalOutlineVisibilityCache;
        bool current=cache.TryGetCurrentExpanded(scene,positions,out var bounds);
        if(sceneRecordCpuActive)sceneCpuBoundsMaterialRangeComparisons+=cache.LastMaterialRangeComparisons;
        if(current)return bounds!;
        using var buildCpu=MeasureSceneCpu(SceneCpuStage.OutlineBoundsBuild);
        cache.MeasureCpu=sceneRecordCpuActive;
        try{bounds=cache.GetOutline(scene,positions);}
        finally{cache.MeasureCpu=false;}
        if(sceneRecordCpuActive)sceneCpuBoundsMaterialRangeComparisons+=cache.LastMaterialRangeComparisons;
        if(sceneRecordCpuActive)AccumulateBoundsCpu(cache,outline:true);
        if(cache.LastTopologyRebuilt||cache.LastRebuiltMaterials>0)outlineBoundsBuilds++;
        return bounds;
    }

    private void AccumulateBoundsCpu(D3D12VisibilityBoundsCache cache,bool outline)
    {
        AddBoundsCpuTicks(outline?SceneCpuStage.OutlineBoundsTopology:SceneCpuStage.SurfaceBoundsTopology,cache.LastTopologyTicks);
        AddBoundsCpuTicks(outline?SceneCpuStage.OutlineBoundsNewEntry:SceneCpuStage.SurfaceBoundsNewEntry,cache.LastNewEntryTicks);
        AddBoundsCpuTicks(outline?SceneCpuStage.OutlineBoundsInvalidation:SceneCpuStage.SurfaceBoundsInvalidation,cache.LastInvalidationTicks);
        AddBoundsCpuTicks(outline?SceneCpuStage.OutlineBoundsPreparation:SceneCpuStage.SurfaceBoundsPreparation,cache.LastPreparationTicks);
        AddBoundsCpuTicks(outline?SceneCpuStage.OutlineBoundsReduction:SceneCpuStage.SurfaceBoundsReduction,cache.LastReductionTicks);
        sceneCpuBoundsPositionComparisons+=cache.LastPositionComparisons;
        sceneCpuBoundsScannedIndices+=cache.LastScannedIndices;
        sceneCpuBoundsRebuiltMaterials+=cache.LastRebuiltMaterials;
        sceneCpuBoundsFullScanMaterials+=cache.LastRebuiltMaterials-cache.LastSparseScanMaterials;
        sceneCpuBoundsSparseScanMaterials+=cache.LastSparseScanMaterials;
        sceneCpuBoundsParallelMaterials+=cache.LastParallelMaterialCount;
        sceneCpuBoundsMarkedLeafIncidences+=cache.LastMarkedLeafIncidences;
    }

    private void AddBoundsCpuTicks(SceneCpuStage stage,long ticks)
    {
        if(ticks==0)return;
        ref var metric=ref sceneCpuMetrics[(int)stage];
        metric.Ticks+=ticks;metric.Calls++;
    }
}
