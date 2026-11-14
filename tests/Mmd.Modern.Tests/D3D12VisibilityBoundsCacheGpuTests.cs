using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12VisibilityBoundsCacheGpuTests
{
    [Theory]
    [InlineData(false,1)] [InlineData(true,1)] [InlineData(false,1024)]
    public void PartialPositionNormalUvWorldAndCameraUpdatesMatchUnculledImagesByteForByte(bool modern,int repetitions)
    {
        var source=D3D12OriginalOutlineTests.Box();
        var options=D3D12OriginalFrameTests.Options() with {EnableFrustumCulling=true};
        var flags=source.Materials.Select(m=>modern?m with {CharacterOutlineStyle=new(2,new(.2f,.3f,.4f,1))}:m).ToArray();
        using var cached=new D3D12FrameSession();using var unculled=new D3D12FrameSession();
        var scene=source.Surface.Snapshot;var outline=source.Outline!;
        if(repetitions>1)
        {
            var indices=scene.Indices.ToArray();var repeated=new List<uint>();var materials=new List<SceneMaterial>();
            foreach(var material in scene.Materials)
            {
                int start=repeated.Count;
                for(int i=0;i<repetitions;i++)repeated.AddRange(indices.AsSpan(material.StartIndex,material.IndexCount).ToArray());
                materials.Add(material with {StartIndex=start,IndexCount=material.IndexCount*repetitions});
            }
            scene=new SceneSnapshot(scene.Vertices.ToArray(),repeated.ToArray(),materials).WithCamera(scene.Camera).WithWorld(scene.World);
        }
        for(int step=0;step<6;step++)
        {
            var vertices=scene.Vertices.ToArray();var edges=outline.Vertices.ToArray();
            if(step<3)
            {
                vertices[0]=vertices[0] with {Position=vertices[0].Position+new Vector3(.2f,.1f,-.05f)};
                edges[0]=edges[0] with {Position=vertices[0].Position};
            }
            else if(step==3)
            {
                vertices[0]=vertices[0] with {Normal=Vector3.Normalize(new(.2f,.3f,-1)),Uv=new(.25f,.75f)};
                edges[0]=edges[0] with {Normal=vertices[0].Normal};
            }
            scene=scene.WithDeformedVertices(vertices);outline=new(edges,outline.Materials);
            if(step==4)scene=scene.WithWorld(Matrix4x4.CreateTranslation(.3f,0,0));
            if(step==5)scene=scene.WithCamera(scene.Camera with {Eye=scene.Camera.Eye+new Vector3(.1f,0,0)});
            var surface=new OriginalStyleScene(scene,source.Surface.Materials,source.Surface.Profile);
            var frame=new OriginalFrameScene(surface,outline,source.OutlineOptions,options,flags);
            var uncull=new OriginalFrameScene(surface,outline,source.OutlineOptions,options with {EnableFrustumCulling=false},flags);
            var expected=unculled.Capture(uncull,128,96);var actual=cached.Capture(frame,128,96);
            Assert.Equal(expected.Rgba,actual.Rgba);
            using var independent=new D3D12FrameSession();
            Assert.Equal(independent.Capture(frame,128,96).Rgba,actual.Rgba);
        }
    }
}
