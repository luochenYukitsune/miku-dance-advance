using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;
namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12AnimatedGeometryTests
{
    [Fact] public void ManyPosesHaveBoundedCachePauseReusesUploadsAndBackSeekIsExact()
    {
        var source=D3D12OriginalOutlineTests.Box();
        OriginalFrameScene Pose(float offset)
        {
            var vertices=source.Surface.Snapshot.Vertices.ToArray();
            for(int i=0;i<vertices.Length;i++)vertices[i]=vertices[i] with {Position=vertices[i].Position+new Vector3(offset,0,0)};
            var surface=new OriginalStyleScene(source.Surface.Snapshot.WithDeformedVertices(vertices),source.Surface.Materials,source.Surface.Profile);
            var edges=new OriginalOutlineData(source.Outline!.Vertices.Select((v,i)=>v with {Position=vertices[i].Position}),source.Outline.Materials);
            return new(surface,edges,source.OutlineOptions,source.Options,source.Materials);
        }
        using var session=new D3D12FrameSession();var first=Pose(0);
        byte[] expected=session.Capture(first,257,191).Rgba;
        int cache=0;long uploads=0;
        for(int frame=0;frame<240;frame++)
        {
            using var output=session.Render(Pose(frame*.002f),257,191);
            Assert.Equal(0,output.Statistics.ReadbackCount);Assert.Equal(1,output.Statistics.SubmissionCount);
            if(frame==6)cache=output.Statistics.CachedResources;
            if(frame>6)Assert.Equal(cache,output.Statistics.CachedResources);
        }
        var paused=Pose(.2f);
        for(int i=0;i<12;i++)
        {
            using var output=session.Render(paused,257,191);
            if(i==5)uploads=output.Statistics.BufferUploads;
            if(i>5)Assert.Equal(uploads,output.Statistics.BufferUploads);
        }
        Assert.NotEqual(expected,session.Capture(paused,257,191).Rgba);
        Assert.Equal(expected,session.Capture(first,257,191).Rgba);
        using var fresh=new D3D12FrameSession();Assert.Equal(expected,fresh.Capture(first,257,191).Rgba);
        session.WaitForIdle();
    }
}
