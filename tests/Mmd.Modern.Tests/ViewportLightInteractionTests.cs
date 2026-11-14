using System.Numerics;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class ViewportLightInteractionTests
{
    private static SceneCamera Camera => new(new(0,0,-12),Vector3.Zero,Vector3.UnitY,10,.1f,100);
    [Fact] public void PlaneDragFollowsScreenAndPreservesDepth()
    {
        var moved=LightViewportInteraction.Translate(Vector3.Zero,new(80,40),Camera,800,600);
        Assert.True(LightViewportInteraction.TryProject(moved,Camera,800,600,out var projected));
        Assert.InRange(projected.X,479.99f,480.01f);Assert.InRange(projected.Y,339.99f,340.01f);
        Assert.InRange(MathF.Abs(moved.Z),0,1e-4f);
    }
    [Fact] public void AxisDragAndRotationStayConstrained()
    {
        var moved=LightViewportInteraction.Translate(Vector3.One,new(80,40),Camera,800,600,LightDragAxis.X);
        Assert.Equal(1,moved.Y);Assert.Equal(1,moved.Z);Assert.NotEqual(1,moved.X);
        var rotation=LightViewportInteraction.Rotate(Quaternion.Identity,new(50,20),Camera);
        Assert.InRange(rotation.LengthSquared(),.99999f,1.00001f);
        Assert.NotEqual(-Vector3.UnitZ,Vector3.Transform(-Vector3.UnitZ,rotation));
    }
    [Fact] public void MarkersCanBePickedAndOverlayRejectsBadInputs()
    {
        var light=new NativeSceneLight(Guid.NewGuid(),"test",SceneLightKind.Point,Vector3.Zero,Quaternion.Identity,Vector3.One);
        var lights=new SceneLightingSnapshot([light]);
        Assert.Equal(light.Id,LightViewportInteraction.HitTest(lights,light.Id,new(400,300),Camera,800,600,1)?.LightId);
        Assert.Null(LightViewportInteraction.HitTest(lights,light.Id,new(0,0),Camera,800,600,1));
        var overlay=LightViewportInteraction.CreateOverlay(lights,light.Id,Camera,800,600,1);
        Assert.NotEmpty(overlay.Lines);
        Assert.Throws<ArgumentException>(()=>new ViewportOverlaySnapshot([new(new(float.NaN,0),Vector2.One,Vector4.One)]));
    }
}
