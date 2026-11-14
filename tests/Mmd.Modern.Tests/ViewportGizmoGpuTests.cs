using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class ViewportGizmoGpuTests
{
    [Fact] public void ActualOverlayDrawsOnGpuAndIsExcludedFromExportedCaptures()
    {
        var frame=EngineShowcaseScene.Create().Frame;
        var overlay=new ViewportOverlaySnapshot([
            new(new(.2f,.2f),new(.8f,.8f),new(1,0,0,1)),
            new(new(.3f,.3f),new(.3f,.3f),new(1,0,0,1),1,ViewportOverlayShape.Circle,5),
            new(new(.5f,.3f),new(.5f,.3f),new(1,0,0,1),1,ViewportOverlayShape.Square,5),
            new(new(.3f,.6f),new(.6f,.6f),new(1,0,0,1),1,ViewportOverlayShape.Wedge,3.5f)]);
        var editing=new OriginalFrameScene(frame.Surface,frame.Outline,frame.OutlineOptions,frame.Options,frame.Materials,editorOverlay:overlay);
        using var session=new D3D12FrameSession();
        using(var output=session.Render(editing,160,120)){Assert.Equal(1,session.EditorOverlayDrawCalls);Assert.Equal(0,output.Statistics.ReadbackCount);}
        var baseline=session.Capture(frame,160,120);
        Assert.Equal(baseline.Rgba,session.Capture(editing,160,120).Rgba);Assert.Equal(0,session.EditorOverlayDrawCalls);
    }
}

public sealed class ViewportOverlayPrimitiveContractTests
{
    [Fact] public void ExistingSegmentsKeepTheirDefaultAndOffscreenCoordinatesRemainUnclamped()
    {
        var line=new ViewportOverlayLine(new(-.2f,.3f),new(1.2f,.7f),Vector4.One);
        Assert.Equal(2.5f,line.Width);Assert.Equal(ViewportOverlayShape.Segment,line.Shape);
        Assert.Same(line,new ViewportOverlaySnapshot([line]).Lines[0]);
    }

    [Fact] public void EveryPrimitiveRetainsTheFull1024ItemBudget()
    {
        var primitives=Enumerable.Range(0,1024).Select(i=>new ViewportOverlayLine(
            Vector2.Zero,Vector2.One,Vector4.One,1,(ViewportOverlayShape)(i%4),5)).ToArray();
        Assert.Equal(1024,ViewportOverlaySnapshot.MaxLines);
        Assert.Equal(1024,new ViewportOverlaySnapshot(primitives).Lines.Count);
        Assert.Throws<ArgumentException>(()=>new ViewportOverlaySnapshot(primitives.Append(primitives[0])));
        Assert.True((1+ViewportOverlaySnapshot.MaxLines*3)*16<65536);
    }

    [Theory]
    [InlineData(0,5)] [InlineData(-1,5)] [InlineData(float.NaN,5)] [InlineData(float.PositiveInfinity,5)]
    [InlineData(1,0)] [InlineData(1,-1)] [InlineData(1,float.NaN)] [InlineData(1,float.PositiveInfinity)]
    public void RejectsNonpositiveOrNonfinitePixelSizes(float width,float radius)
        =>Assert.Throws<ArgumentException>(()=>new ViewportOverlaySnapshot([
            new(Vector2.Zero,Vector2.One,Vector4.One,width,ViewportOverlayShape.Circle,radius)]));

    [Fact] public void RejectsUnknownPrimitiveKinds()
        =>Assert.Throws<ArgumentException>(()=>new ViewportOverlaySnapshot([
            new(Vector2.Zero,Vector2.One,Vector4.One,1,(ViewportOverlayShape)4)]));
}
