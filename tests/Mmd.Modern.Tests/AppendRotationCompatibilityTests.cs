using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Xunit;
namespace Mmd.Modern.Tests;

public sealed class AppendRotationCompatibilityTests
{
    [Fact] public void NonlocalGrantIncludesSourcesOwnAnimationAndAlreadyEvaluatedGrant()
    {
        RigBone[] bones=[new("origin",Vector3.Zero,-1),new("source",Vector3.Zero,-1,DeformLayer:1,Append:new(0,1,false,true,false)),
            new("target",Vector3.Zero,-1,DeformLayer:2,Append:new(1,1,false,true,false))];
        BoneTrack Track(string name,Quaternion rotation)=>new(name,[new(0,Vector3.Zero,rotation,BoneCurves.Linear)]);
        var rig=new CharacterRig(bones,Array.Empty<SceneVertex>(),Array.Empty<SkinBinding>());
        var animator=new CharacterAnimator(rig,new([Track("origin",Quaternion.CreateFromAxisAngle(Vector3.UnitX,.4f)),
            Track("source",Quaternion.CreateFromAxisAngle(Vector3.UnitZ,.2f))]));
        var sample=animator.Sample(0);
        foreach(var axis in new[]{Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ})
            Assert.InRange(Vector3.Distance(Vector3.TransformNormal(axis,sample.BoneWorldMatrices[1]),Vector3.TransformNormal(axis,sample.BoneWorldMatrices[2])),0,1e-5f);
        Assert.Contains(animator.Diagnostics,d=>d.Contains("schedule",StringComparison.OrdinalIgnoreCase));
    }
}
