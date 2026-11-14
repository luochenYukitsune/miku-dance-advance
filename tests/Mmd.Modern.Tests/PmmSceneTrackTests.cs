using System.Numerics;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Formats;
using Mmd.Modern.Core;
using Xunit;
namespace Mmd.Modern.Tests;

public sealed class PmmSceneTrackTests
{
    private static byte[] Linear()=>Enumerable.Range(0,6).SelectMany(_=>new byte[]{20,20,107,107}).ToArray();
    [Fact] public void CameraRestoresSavedValuesSeparatelyFromRegisteredKeysAndUsesLeftHandedProjection()
    {
        // PMM v2 uses -1 for an unparented camera; model index 0 is a real parent.
        // Native parented LookAt is a local eye, while this fixture tests an orbit camera.
        var document=new PmmDocument{CameraFov=30,Settings=new(){CameraLookAtModelIndex=-1,CameraLookAtBoneIndex=-1},
            Camera=new(){LookAt=new(3,4,5),Position=new(0,0,-20),Perspective=true,
            InitialKeyframe=new(){Distance=-10,Fov=30,Perspective=true,Interpolation=Linear()}}};
        var tracks=new PmmSceneTracks(document);var camera=tracks.SavedCamera.CreateCamera();
        Assert.Equal(-1,tracks.SavedCamera.ParentModelIndex);Assert.Equal(-20,tracks.SavedCamera.Distance);
        Assert.Equal(new Vector3(3,4,-15),camera.Eye);Assert.Equal(new Vector3(3,4,-14),camera.Target);
        Assert.True(camera.LeftHanded);Assert.Equal(30,camera.VerticalFovDegrees);Assert.Equal(-10,tracks.CameraAt(0).Distance);
        var clip=Vector4.Transform(new Vector4(4,4,5,1),camera.ViewProjection(1920,1080));
        Assert.True(clip.W>0&&clip.X/clip.W>0&&clip.Z/clip.W is >0 and <1);
    }
    [Fact] public void CameraUsesSixIndependentCurvesAndLightInterpolatesWithoutChangingSavedEditorState()
    {
        var curves=Linear();curves[0]=0;curves[1]=127;curves[2]=127;curves[3]=127;
        var source=new PmmDocument{CameraFov=30,Camera=new(){LookAt=new(999),InitialKeyframe=new(){Fov=30,Distance=-10,Perspective=true,Interpolation=curves},
            Keyframes=[new(){ObjectIndex=17,Frame=10,LookAt=new(10),Angle=new(1),Distance=-30,Fov=50,Perspective=true,Interpolation=Linear()}]},
            Light=new(){Color=new(.9f),Direction=-Vector3.UnitY,InitialKeyframe=new(){Color=new(.2f),Direction=new(1,-1,0)},Keyframes=[new(){Frame=10,Color=new(.8f),Direction=new(-1,-1,0)}]}};
        var tracks=new PmmSceneTracks(source);var pose=tracks.CameraAt(5);
        Assert.InRange(pose.LookAt.X,8.74f,8.76f);Assert.InRange(pose.LookAt.Y,4.999f,5.001f);
        Assert.Equal(-20,pose.Distance);Assert.Equal(40,pose.Fov);Assert.Equal(new Vector3(.5f),pose.Angle);
        Assert.Equal(new Vector3(999),tracks.SavedCamera.LookAt);Assert.Equal(new Vector3(.9f),tracks.SavedLight.Color);
        Assert.InRange(tracks.LightAt(5).Color.X,.4999f,.5001f);Assert.Equal(-Vector3.UnitY,tracks.LightAt(5).Direction);
        Assert.Throws<ArgumentOutOfRangeException>(()=>tracks.CameraAt(double.NaN));
    }
    [Fact] public void CameraAdjacentKeyCutUsesTheExactNewKeyAtItsFrame()
    {
        var tracks=new PmmSceneTracks(new(){Camera=new(){InitialKeyframe=new(){Fov=30,Distance=-10,Interpolation=Linear()},Keyframes=[new(){Frame=1,Fov=60,Distance=-50,Interpolation=Linear()}]}});
        Assert.Equal(-10,tracks.CameraAt(.9).Distance);Assert.Equal(-50,tracks.CameraAt(1).Distance);
    }
    [Fact] public void ProjectCommandLineCannotAccidentallyOverlayAnotherModelMotionOrRoom()
    {
        var options=FrameViewportOptions.Parse(["--frame-viewer","--project","a.pmm"]);Assert.Equal("a.pmm",options.ProjectPath);
        Assert.Throws<ArgumentException>(()=>FrameViewportOptions.Parse(["--frame-viewer","--project","a.pmm","--model","b.pmx"]));
        Assert.Throws<ArgumentException>(()=>FrameViewportOptions.Parse(["--frame-viewer","--project","a.pmm","--motion","b.vmd"]));
        Assert.Throws<ArgumentException>(()=>FrameViewportOptions.Parse(["--frame-viewer","--project","a.pmm","--demo-room"]));
    }
    [Fact] public void ProjectBackgroundIsExplicitAndLegacyDefaultRemainsUnspecified()
    {
        var options=new OriginalFrameOptions(OriginalAlphaBlendMode.Normal,true,false);
        Assert.Null(options.BackgroundColor);OriginalFrameMath.Validate(options with{BackgroundColor=Vector4.One});
        Assert.Throws<ArgumentException>(()=>OriginalFrameMath.Validate(options with{BackgroundColor=new(float.NaN,0,0,1)}));
    }
}
