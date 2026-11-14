using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12GeometryStorageCacheTests
{
    [Fact]
    public void LightingCameraWorldAndMaterialColourWrappersReuseCpuStreamsBoundsAndWarmUploads()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        using var renderer=new D3D12OriginalStyleRenderer(runtime);
        var frame=Frame();var source=frame.Surface.Snapshot;
        D3D12EditorGeometry? first=null;long warmedUploads=0;
        for(int i=0;i<12;i++)
        {
            var scene=source.WithLighting(Vector3.Normalize(new(.1f+i*.01f,.3f,-1)))
                .WithCamera(source.Camera with {Eye=source.Camera.Eye+new Vector3(i*.02f,0,0)})
                .WithWorld(Matrix4x4.CreateTranslation(i*.03f,0,0))
                .WithMaterials(source.Materials.Select(m=>m with {Diffuse=new(1,.5f+i*.01f,.7f,1)}));
            var input=Copy(frame,scene);var context=runtime.Begin();
            var geometry=Borrow(context,renderer,cache,input);
            first??=geometry;
            Assert.Same(first.OriginalPositions,geometry.OriginalPositions);
            Assert.Same(first.ModernPositions,geometry.ModernPositions);
            Assert.Same(first.SurfaceBounds,geometry.SurfaceBounds);
            Assert.Same(first.OriginalBounds,geometry.OriginalBounds);
            Assert.Same(first.ModernBounds,geometry.ModernBounds);
            Assert.Equal(1,renderer.CharacterOutlineStreamBuilds);
            if(i==2)warmedUploads=runtime.BufferUploads;
            if(i>2)Assert.Equal(warmedUploads,runtime.BufferUploads);
            runtime.Submit();
        }
        runtime.WaitForIdle();
    }

    [Theory]
    [InlineData("position")]
    [InlineData("normal")]
    [InlineData("base-uv")]
    [InlineData("additional-uv")]
    [InlineData("width")]
    [InlineData("model-base")]
    public void ConsumedVertexUvWidthAndModelBaseChangesRebuildTheModernStream(string channel)
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        using var renderer=new D3D12OriginalStyleRenderer(runtime);
        var frame=Frame();var context=runtime.Begin();Borrow(context,renderer,cache,frame);runtime.Submit();
        var source=frame.Surface.Snapshot;var changed=frame;
        if(channel is "position" or "normal" or "base-uv")
        {
            var vertices=source.Vertices.ToArray();
            vertices[0]=channel switch {
                "position"=>vertices[0] with {Position=vertices[0].Position+new Vector3(.1f,0,0)},
                "normal"=>vertices[0] with {Normal=vertices[0].Normal+new Vector3(.1f,0,0)},
                _=>vertices[0] with {Uv=new(.25f,.75f)} };
            changed=Copy(frame,source.WithDeformedVertices(vertices));
        }
        else if(channel=="additional-uv")
        {
            var values=source.AdditionalUvs!.Vertices.ToArray();values[0]=values[0] with {Uv1=new(.25f,.75f,0,0)};
            changed=Copy(frame,source.WithAdditionalUvs(new(1,values)));
        }
        else if(channel=="width")changed=new(frame.Surface,frame.Outline,frame.OutlineOptions,frame.Options,
            frame.Materials.Select(f=>f with {CharacterOutlineStyle=f.CharacterOutlineStyle! with {Width=3}}));
        else changed=new(frame.Surface,frame.Outline,frame.OutlineOptions! with {ModelWidth=frame.OutlineOptions.ModelWidth+5},frame.Options,frame.Materials);
        context=runtime.Begin();Borrow(context,renderer,cache,changed);runtime.Submit();
        Assert.Equal(2,renderer.CharacterOutlineStreamBuilds);
        runtime.WaitForIdle();
    }

    [Fact]
    public void ChangedMaterialRangesInvalidateExactLocalBoundsWithoutRepackingVertices()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        using var renderer=new D3D12OriginalStyleRenderer(runtime);
        var box=Frame();var s=box.Surface.Snapshot;
        var material=s.Materials[0];
        var scene=new SceneSnapshot(s.Vertices,s.Indices,
            [material with {IndexCount=18},material with {Name="second",StartIndex=18,IndexCount=18}],
            s.Textures,s.World,s.Camera,s.LightDirection,additionalUvs:s.AdditionalUvs);
        var outline=new OriginalOutlineData(box.Outline!.Vertices,[box.Outline.Materials[0],box.Outline.Materials[0]]);
        var frame=new OriginalFrameScene(new(scene,[box.Surface.Materials[0],box.Surface.Materials[0]],box.Surface.Profile),
            outline,box.OutlineOptions,box.Options,[box.Materials[0],box.Materials[0]]);
        var context=runtime.Begin();var before=Borrow(context,renderer,cache,frame);
        var shifted=scene.WithMaterials([material with {IndexCount=6},material with {Name="second",StartIndex=6,IndexCount=30}]);
        var after=Borrow(context,renderer,cache,Copy(frame,shifted));
        Assert.NotSame(before.SurfaceBounds,after.SurfaceBounds);
        Assert.NotSame(before.OriginalBounds,after.OriginalBounds);
        Assert.NotSame(before.ModernBounds,after.ModernBounds);
        Assert.Equal(OriginalFrustumVisibility.SurfaceMaterialBounds(shifted),after.SurfaceBounds);
        var controlPositions=Enumerable.Range(0,after.ModernPositions!.Length).Select(i=>after.ModernPositions[i]).ToArray();
        Assert.Equal(OriginalFrustumVisibility.PositionMaterialBounds(shifted,controlPositions),after.ModernBounds);
        Assert.False(after.ModernPositions.IsMaterialized);
        Assert.Equal(1,renderer.CharacterOutlineStreamBuilds);
        runtime.Wait(runtime.Submit());
    }

    [Fact]
    public void ReusedLocalGeometryStillUsesCurrentLightingCameraAndWorldDrawConstants()
    {
        var frame=Frame();var s=frame.Surface.Snapshot;
        var variants=new[] {s,s.WithLighting(Vector3.UnitZ),
            s.WithCamera(s.Camera with {Eye=s.Camera.Eye+new Vector3(1.5f,0,0)}),
            s.WithWorld(Matrix4x4.CreateTranslation(100,0,0))};
        using var session=new D3D12FrameSession();
        for(int warm=0;warm<3;warm++)session.Capture(frame,128,96);
        long uploads=session.Statistics!.BufferUploads;
        var bounds=session.VisibilityStatistics!;var results=new List<byte[]>();
        foreach(var scene in variants)
        {
            var input=Copy(frame,scene);var actual=session.Capture(input,128,96).Rgba;
            using var independent=new D3D12FrameSession();
            Assert.Equal(independent.Capture(input,128,96).Rgba,actual);
            Assert.Equal(uploads,session.Statistics!.BufferUploads);
            Assert.Equal(bounds.SurfaceBoundsBuilds,session.VisibilityStatistics!.SurfaceBoundsBuilds);
            Assert.Equal(bounds.OutlineBoundsBuilds,session.VisibilityStatistics.OutlineBoundsBuilds);
            results.Add(actual);
        }
        Assert.NotEqual(results[0],results[1]);Assert.NotEqual(results[0],results[2]);Assert.NotEqual(results[0],results[3]);
    }

    private static OriginalFrameScene Frame()
    {
        var box=D3D12OriginalOutlineTests.Box();var source=box.Surface.Snapshot;
        var uv=new SceneAdditionalUvs(1,Enumerable.Repeat(new SceneAdditionalUvVertex(new(.5f,.5f,0,0),Vector4.Zero,Vector4.Zero,Vector4.Zero),source.Vertices.Length).ToArray());
        var scene=source.WithAdditionalUvs(uv);
        return new(new(scene,[box.Surface.Materials[0] with {ToonShadowColor=new(.1f)}],box.Surface.Profile),
            box.Outline,box.OutlineOptions,D3D12OriginalFrameTests.Options() with {EnableFrustumCulling=true},
            box.Materials.Select(f=>f with {CharacterOutlineStyle=new(2,new(1,0,0,.5f))}));
    }

    private static OriginalFrameScene Copy(OriginalFrameScene frame,SceneSnapshot scene)
        =>new(new(scene,frame.Surface.Materials,frame.Surface.Profile),frame.Outline,frame.OutlineOptions,frame.Options,frame.Materials,frame.Lighting,frame.EditorOverlay);

    private static D3D12EditorGeometry Borrow(D3D12RecordingContext context,D3D12OriginalStyleRenderer renderer,D3D12GpuCache cache,OriginalFrameScene frame)
    {
        var s=frame.Surface.Snapshot;
        var vb=cache.BufferByIdentity("test-source",MemoryMarshal.AsBytes(s.Vertices),ResourceStates.VertexAndConstantBuffer,new D3D12VertexStreamIdentity(s));
        return renderer.BorrowEditorGeometry(context,frame,vb,cache.IndexBuffer(s),cache);
    }
}
