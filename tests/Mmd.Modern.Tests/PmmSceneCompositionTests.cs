using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Xunit;
namespace Mmd.Modern.Tests;

public sealed class PmmSceneCompositionTests
{
    private static OriginalFrameScene Triangle(string name,float x)
    {
        SceneVertex[] v=[new(new(x,0,0),Vector3.UnitZ,Vector2.Zero),new(new(x+1,0,0),Vector3.UnitZ,Vector2.UnitX),new(new(x,1,0),Vector3.UnitZ,Vector2.UnitY)];
        var texture=new SceneTexture(name,1,1,new byte[]{255,255,255,255});
        var scene=new OriginalStyleScene(new(v,[0,1,2],[new(name,0,3,Vector4.One,Vector3.Zero,Vector3.Zero,1,0)],[texture]),[new(Vector3.One)]);
        var outline=new OriginalOutlineData(v.Select(a=>new OriginalOutlineVertex(a.Position,a.Normal,OriginalOutlineVertexMode.Expand,1,0)),[new(.1f,Vector4.One)]);
        return new(scene,outline,new(Vector4.One,Matrix4x4.Identity,new(0,0,-5),30,1));
    }
    [Fact] public void MultipleModelsKeepSourceOrderAndRemapAllMaterialTextureAndOutlineIndices()
    {
        var a=Triangle("a",0);var b=Triangle("b",3);var merged=ProjectSceneAssembler.Combine([a,b],new(.5f),Vector3.UnitY);
        Assert.Equal(new uint[]{0,1,2,3,4,5},merged.Surface.Snapshot.Indices.ToArray());
        Assert.Equal(6,merged.Surface.Snapshot.Vertices.Length);Assert.Equal(2,merged.Surface.Snapshot.Materials.Count);
        Assert.Equal(3,merged.Surface.Snapshot.Materials[1].StartIndex);Assert.Equal(1,merged.Surface.Snapshot.Materials[1].DiffuseTexture);
        Assert.Equal(1,merged.Outline!.Vertices[3].SelectedMaterialIndex);Assert.Equal(b.Surface.Snapshot.Vertices[0],merged.Surface.Snapshot.Vertices[3]);
        Assert.Equal(new Vector3(.5f),merged.Surface.Profile.LightAmbient);Assert.Equal(Vector3.UnitY,merged.Surface.Snapshot.LightDirection);
        var later=ProjectSceneAssembler.WithVertices(merged,[a,b]);Assert.True(later.Surface.Snapshot.HasDynamicVertices);
        Assert.Same(merged.Surface.Snapshot.Materials,later.Surface.Snapshot.Materials);
        Assert.False(merged.Surface.Snapshot.HasDynamicVertices);
        var visibleAgain=ProjectSceneAssembler.Combine([later],Vector3.One,Vector3.UnitY);
        Assert.True(visibleAgain.Surface.Snapshot.HasDynamicVertices);
    }
    [Fact] public void MergedVertexUpdatesDoNotChangeRestTopologyOrOtherCallerFrames()
    {
        var a=Triangle("a",0);var b=Triangle("b",3);var baseline=ProjectSceneAssembler.Combine([a,b],Vector3.One,Vector3.UnitY);
        var posed=b.Surface.Snapshot.Vertices.ToArray();for(int i=0;i<posed.Length;i++)posed[i]=posed[i] with{Position=posed[i].Position+new Vector3(5,0,0)};
        var moved=new OriginalFrameScene(new(b.Surface.Snapshot.WithDeformedVertices(posed),b.Surface.Materials),b.Outline,b.OutlineOptions,materials:b.Materials);
        var changed=ProjectSceneAssembler.WithVertices(baseline,[a,moved]);
        Assert.Equal(new Vector3(8,0,0),changed.Surface.Snapshot.Vertices[3].Position);
        Assert.Equal(new Vector3(3,0,0),baseline.Surface.Snapshot.Vertices[3].Position);
        Assert.Equal(baseline.Surface.Snapshot.Indices.ToArray(),changed.Surface.Snapshot.Indices.ToArray());
        Assert.Throws<ArgumentException>(()=>ProjectSceneAssembler.WithVertices(baseline,[a]));
    }
    [Fact] public void PerspectiveCameraHasForeshorteningAndOrthographicDefaultKeepsOldMatrix()
    {
        var orthographic=new SceneCamera(new(0,0,-10),Vector3.Zero,Vector3.UnitY,20,.1f,100);
        var perspective=orthographic with{VerticalFovDegrees=30};
        var matrix=perspective.ViewProjection(1920,1080);
        var near=Vector4.Transform(new Vector4(1,0,0,1),matrix);var far=Vector4.Transform(new Vector4(1,0,10,1),matrix);
        Assert.True(MathF.Abs(near.X/near.W)>MathF.Abs(far.X/far.W));
        Assert.Equal(Matrix4x4.CreateLookAt(orthographic.Eye,orthographic.Target,orthographic.Up)*Matrix4x4.CreateOrthographic(20*1920f/1080,20,.1f,100),orthographic.ViewProjection(1920,1080));
        Assert.Throws<ArgumentOutOfRangeException>(()=>(perspective with{VerticalFovDegrees=180}).ViewProjection(10,10));
    }
    [Fact] public void LegitimateZeroPmxNormalsRemainZeroThroughSurfaceAndAnimationInsteadOfBeingInvented()
    {
        SceneVertex[] vertices=[new(Vector3.Zero,Vector3.Zero,Vector2.Zero),new(Vector3.UnitX,Vector3.UnitZ,Vector2.Zero),new(Vector3.UnitY,Vector3.UnitZ,Vector2.Zero)];
        var scene=new SceneSnapshot(vertices,[0,1,2],[new("normal",0,3,Vector4.One,Vector3.Zero,Vector3.Zero,1)]);
        var rig=new Mmd.Modern.Animation.CharacterRig([new("root",Vector3.Zero,-1)],vertices,Enumerable.Repeat(new Mmd.Modern.Animation.SkinBinding(Mmd.Modern.Animation.SkinningKind.Bdef1,0,-1,-1,-1,new(1,0,0,0)),3));
        var sample=new Mmd.Modern.Animation.CharacterAnimator(rig,new([])).Sample(0);
        Assert.Equal(Vector3.Zero,sample.Vertices[0].Normal);Assert.Equal(Vector3.Zero,scene.WithDeformedVertices(sample.Vertices).Vertices[0].Normal);
        vertices[0]=vertices[0] with{Normal=new(float.NaN,0,0)};Assert.Throws<ArgumentException>(()=>scene.WithDeformedVertices(vertices));
    }
    [Fact] public void ProjectMaterialAndUvChangesKeepStaticTopologyAndReachTheCombinedFrame()
    {
        var a=Triangle("a",0);var b=Triangle("b",3);var baseline=ProjectSceneAssembler.Combine([a,b],Vector3.One,Vector3.UnitY);
        var vertices=b.Surface.Snapshot.Vertices.ToArray();vertices[0]=vertices[0] with{Uv=new(.5f,.25f)};
        var changed=b.Surface.Snapshot.WithDeformedVertices(vertices).WithMaterials([b.Surface.Snapshot.Materials[0] with{Diffuse=new(1,1,1,.2f)}]);
        Assert.True(changed.HasDynamicVertices);Assert.Same(b.Surface.Snapshot.Textures,changed.Textures);
        var frame=ProjectSceneAssembler.WithVertices(baseline,[a,new(new(changed,b.Surface.Materials),b.Outline,b.OutlineOptions,materials:b.Materials)]);
        Assert.Equal(.2f,frame.Surface.Snapshot.Materials[1].Diffuse.W);Assert.Equal(3,frame.Surface.Snapshot.Materials[1].StartIndex);
        Assert.Equal(1,frame.Surface.Snapshot.Materials[1].DiffuseTexture);Assert.Equal(new Vector2(.5f,.25f),frame.Surface.Snapshot.Vertices[3].Uv);
        Assert.Equal(1,baseline.Surface.Snapshot.Materials[1].Diffuse.W);Assert.Equal(baseline.Surface.Snapshot.Indices.ToArray(),frame.Surface.Snapshot.Indices.ToArray());
    }
}
