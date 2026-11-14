using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12CulledOutlineWorkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullyCulledHullSkipsUploadAndModernPackingThenReentersWithCurrentGeometry(bool modern)
    {
        using var session=new D3D12FrameSession();
        var source=D3D12OriginalOutlineTests.Box();
        var snapshot=source.Surface.Snapshot;
        var hiddenCamera=snapshot.Camera with {Eye=snapshot.Camera.Eye+new Vector3(100,0,0),
            Target=snapshot.Camera.Target+new Vector3(100,0,0)};
        OriginalFrameScene Frame(bool hidden,bool culling,OriginalFrameScene pose)
            =>new(pose.Surface,pose.Outline,pose.OutlineOptions,D3D12OriginalFrameTests.Options() with
                {EnableFrustumCulling=culling,CameraOverride=hidden?hiddenCamera:null},
                pose.Materials.Select(m=>m with {CharacterOutlineStyle=modern?new(1.5f,new(1,0,0,1)):null}));
        var invisible=session.Capture(Frame(true,true,source),67,49);
        Assert.Equal(1,session.VisibilityStatistics!.OutlineCulled);
        Assert.Equal(0,session.VisibilityStatistics.OutlineDrawn);
        var cpu=session.Statistics!.SceneRecordCpu!;
        if(modern)Assert.Equal(0,cpu.ModernPacking.Calls);
        else Assert.Equal(0,cpu.OriginalOutlineUpload.Calls);
        using(var uncull=new D3D12FrameSession())
            Assert.Equal(invisible.Rgba,uncull.Capture(Frame(true,false,source),67,49).Rgba);

        session.Capture(Frame(false,true,source),67,49);
        var vertices=snapshot.Vertices.ToArray().Select(v=>v with {Position=v.Position+new Vector3(.3f,.1f,0)}).ToArray();
        var moved=new OriginalFrameScene(new OriginalStyleScene(snapshot.WithDeformedVertices(vertices),
            source.Surface.Materials,source.Surface.Profile),
            new OriginalOutlineData(source.Outline!.Vertices.Select(v=>v with {Position=v.Position+new Vector3(.3f,.1f,0)}),source.Outline.Materials),
            source.OutlineOptions);
        session.Capture(Frame(true,true,moved),67,49);
        cpu=session.Statistics!.SceneRecordCpu!;
        if(modern)Assert.Equal(0,cpu.ModernPacking.Calls);
        else Assert.Equal(0,cpu.OriginalOutlineUpload.Calls);
        var reentered=session.Capture(Frame(false,true,moved),67,49);
        Assert.Equal(1,session.VisibilityStatistics!.OutlineDrawn);
        using var fresh=new D3D12FrameSession();
        Assert.Equal(fresh.Capture(Frame(false,true,moved),67,49).Rgba,reentered.Rgba);
    }
}
