using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Application;
using Mmd.Modern.Formats;
using System.Numerics;
using Xunit;
namespace Mmd.Modern.Tests;

public sealed class AnimationIntegrationTests
{
    [Fact] public void MmdImportSeedsRestUsesNextCurvesAndLastDuplicateWins()
    {
        byte[] curves=new byte[64];
        for(int c=0;c<4;c++){curves[c*16]=0;curves[c*16+4]=0;curves[c*16+8]=127;curves[c*16+12]=127;}
        // Distinguish channel Y in its redundant block from Saba's first-block layout.
        curves[16]=0;curves[20]=127;curves[24]=127;curves[28]=127;
        VmdBoneKeyframe Key(uint frame,float x)=>new(){BoneName="root",Frame=frame,Translation=new(x,x,0),Rotation=Quaternion.Identity,Interpolation=curves};
        var clip=MmdBoneMotionAdapter.Convert(new(){BoneKeyframes=[Key(10,2),Key(10,10)]});
        var track=Assert.Single(clip.Tracks);Assert.Equal(new uint[]{0,10},track.Keys.Select(k=>k.Frame));
        Assert.Equal(10,track.Keys[1].Translation.X);Assert.Equal(SegmentCurveOwnership.Next,clip.CurveOwnership);
        var rig=new CharacterRig([new("root",Vector3.Zero,-1)],[new(Vector3.Zero,Vector3.UnitY,Vector2.Zero)],
            [new(SkinningKind.Bdef1,0,-1,-1,-1,new(1,0,0,0))]);
        var evaluator=new CharacterAnimator(rig,clip);
        var halfway=evaluator.Sample(5).Vertices[0].Position;
        Assert.InRange(halfway.X,4.999f,5.001f);Assert.InRange(halfway.Y,8.74f,8.76f);
        Assert.Equal(Vector3.Zero,evaluator.Sample(0).Vertices[0].Position);
        Assert.Equal(new Vector3(10,10,0),evaluator.Sample(100).Vertices[0].Position);
        Assert.Equal(halfway,evaluator.Sample(5).Vertices[0].Position);
        var overrideZero=MmdBoneMotionAdapter.Convert(new(){BoneKeyframes=[Key(0,3),Key(10,10)]});
        Assert.Equal(3,overrideZero.Tracks[0].Keys[0].Translation.X);
    }
    [Fact] public void PlaybackClockDoesNotDriftPausedAndSeekDoesNotAccumulateOldTime()
    {
        var clock=new FramePlaybackClock();clock.SetDuration(90);clock.Advance(TimeSpan.FromSeconds(5));Assert.Equal(0,clock.Frame);
        clock.Play();clock.Advance(TimeSpan.FromSeconds(.5));Assert.Equal(15,clock.Frame);clock.Pause();
        clock.Advance(TimeSpan.FromHours(1));Assert.Equal(15,clock.Frame);clock.Seek(3);Assert.Equal(3,clock.Frame);
        clock.Play();clock.Advance(TimeSpan.FromSeconds(.1));Assert.InRange(clock.Frame,5.99999,6.00001);
    }
    [Fact] public void PlaybackClampsAtEndAndRejectsInvalidTime()
    {
        var clock=new FramePlaybackClock();clock.SetDuration(60);clock.Play();clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(60,clock.Frame);Assert.False(clock.IsPlaying);clock.Play();Assert.Equal(0,clock.Frame);
        clock.Seek(-10);Assert.Equal(0,clock.Frame);clock.Seek(100);Assert.Equal(60,clock.Frame);
        Assert.Throws<ArgumentOutOfRangeException>(()=>clock.Seek(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(()=>clock.Advance(TimeSpan.FromTicks(-1)));
    }
    [Fact] public void DynamicSnapshotKeepsStaticDataAndCopiesCallerVertices()
    {
        var snapshot=new SceneSnapshot([new(Vector3.Zero,Vector3.UnitY,Vector2.Zero),new(Vector3.UnitX,Vector3.UnitY,Vector2.Zero),new(Vector3.UnitZ,Vector3.UnitY,Vector2.Zero)],
            [0,1,2],[new("mat",0,3,Vector4.One,Vector3.Zero,Vector3.Zero,1)]);
        var method=typeof(SceneSnapshot).GetMethod("WithDeformedVertices");Assert.NotNull(method);
        var source=snapshot.Vertices.ToArray();source[0]=source[0] with{Position=new(0,2,0)};
        var changed=Assert.IsType<SceneSnapshot>(method.Invoke(snapshot,[source]));
        source[0]=source[0] with{Position=new(9,9,9)};
        Assert.Equal(new Vector3(0,2,0),changed.Vertices[0].Position);Assert.Equal(Vector3.Zero,snapshot.Vertices[0].Position);
        Assert.Same(snapshot.Materials,changed.Materials);Assert.Same(snapshot.Textures,changed.Textures);
        Assert.Equal(snapshot.Indices.ToArray(),changed.Indices.ToArray());
        Assert.Equal(true,typeof(SceneSnapshot).GetProperty("HasDynamicVertices")!.GetValue(changed));
    }
}
