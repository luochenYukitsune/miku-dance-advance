using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice.Direct3D12;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Borrowed inputs from this recording only. Resource owners and their existing
/// fence leases stay with the primary recorder; no previous frame graph is retained here.</summary>
internal sealed record D3D12EditorGeometry(SceneSnapshot Scene,ID3D12Resource Surface,ID3D12Resource Indices,
    D3D12GpuCache Cache,D3D12ExpandedPositions? OriginalPositions,ID3D12Resource? OriginalOutline,
    D3D12ExpandedPositions? ModernPositions,ID3D12Resource? ModernOutline,ID3D12Resource? AdditionalUvs,
    OriginalVisibilityBounds[]? SurfaceBounds,OriginalVisibilityBounds[]? OriginalBounds,OriginalVisibilityBounds[]? ModernBounds,
    ID3D12Resource? WidthSource=null);

public sealed partial class D3D12OriginalStyleRenderer
{
    internal D3D12EditorGeometry BorrowEditorGeometry(D3D12RecordingContext context,OriginalFrameScene frame,
        ID3D12Resource surface,ID3D12Resource indices,D3D12GpuCache cache)
    {
        if(!ReferenceEquals(context.Runtime,runtime)||!runtime.IsRecording)
            throw new InvalidOperationException("Editor geometry must be borrowed from the active primary recording.");
        var scene=frame.Surface.Snapshot;
        bool deferred=scene.HasDeferredDeformation||frame.Outline?.DeferredSurface is not null;
        bool hasModern=frame.Outline is not null&&frame.Materials.Any(f=>f.CharacterOutlineStyle is not null);
        D3D12ExpandedPositions? positions=null,modernPositions=null;
        ID3D12Resource? original=null,modern=null,widthSource=null;
        if(deferred)
        {
            bool needWidth=frame.Outline is not null&&frame.Materials.Any(f=>f.OutlineWidthScale!=1);
            if(frame.Outline is not null)
            {
                var orig=cache.GpuSkinning.GetOutlineStreams(frame,surface,modern:false,needWidthSource:needWidth);
                original=orig.Stream;widthSource=orig.WidthSource;
                if(hasModern)
                {
                    var mod=cache.GpuSkinning.GetOutlineStreams(frame,surface,modern:true,needWidthSource:false);
                    modern=mod.Stream;
                }
            }
        }
        else
        {
            positions=frame.Outline is null?null:GetEdgePositions(frame);
            original=positions is null?null:cache.ExpandedPositionBuffer("original-edge",positions);
            modernPositions=hasModern?GetOutlinePositions(frame):null;
            modern=modernPositions is null?null:GetCharacterOutlineBuffer(frame,cache,modernPositions);
        }
        ID3D12Resource? uv=null;
        if(HasSubTexture(frame))
        {
            var stream=scene.AdditionalUvs??throw new ArgumentException("SubTexture requires UV1.");
            uv=cache.BufferByIdentity("original-additional-uv",MemoryMarshal.AsBytes(stream.Vertices),ResourceStates.VertexAndConstantBuffer,stream);
        }
        bool cull=frame.Options?.EnableFrustumCulling==true;
        var surfaces=cull?GetSurfaceVisibilityBounds(scene):null;
        var originalBounds=cull&&!deferred&&positions is not null?GetOutlineVisibilityBounds(scene,positions,false):null;
        var modernBounds=cull&&!deferred&&modernPositions is not null?GetOutlineVisibilityBounds(scene,modernPositions,true):null;
        return new(scene,surface,indices,cache,positions,original,modernPositions,modern,uv,surfaces,originalBounds,modernBounds,widthSource);
    }

    private ID3D12Resource GetCharacterOutlineBuffer(OriginalFrameScene frame,D3D12GpuCache cache,D3D12ExpandedPositions positions)
    {
        var scene=frame.Surface.Snapshot;
        // Camera, world, lighting and material-colour wrappers share the immutable
        // vertex storage. Only consumed vertex/UV/expanded-position inputs rebuild.
        if(characterOutlineScene is null||!new D3D12VertexStreamIdentity(characterOutlineScene).Equals(new D3D12VertexStreamIdentity(scene))
            ||!ReferenceEquals(characterOutlineAdditionalUvs,scene.AdditionalUvs)||!ReferenceEquals(characterOutlinePositions,positions))
        {
            using var packingCpu=MeasureSceneCpu(SceneCpuStage.ModernPacking);
            var vertices=new CharacterOutlineVertex[positions.Length];
            for(int i=0;i<vertices.Length;i++)
            {
                var original=scene.GetVertex(i);
                Vector2 uv1=scene.AdditionalUvs is {} additional?new(additional.Vertices[i].Uv1.X,additional.Vertices[i].Uv1.Y):Vector2.Zero;
                vertices[i]=new(new(positions[i].X,positions[i].Y,positions[i].Z),original.Position,original.Normal,original.Uv,uv1);
            }
            characterOutlineScene=scene;characterOutlineAdditionalUvs=scene.AdditionalUvs;
            characterOutlinePositions=positions;characterOutlineVertices=vertices;CharacterOutlineStreamBuilds++;
        }
        var stream=characterOutlineVertices!;
        using var uploadCpu=MeasureSceneCpu(SceneCpuStage.ModernOutlineUpload);
        return cache.DynamicBuffer("character-outline-textured",MemoryMarshal.AsBytes(stream.AsSpan()),ResourceStates.VertexAndConstantBuffer,stream);
    }

}
