using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterPhysicsControllerTests
{
    private static CharacterAnimationController Controller()
    {
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"fixtures","rich-w1-uv4.pmx"));
        var source=PmxReader.Read(input);
        var model=new PmxModel {Header=source.Header,Name="test",EnglishName="",Comment="",EnglishComment="",
            Vertices=source.Vertices.Select(v=>new PmxVertex{Position=v.Position,Normal=v.Normal,Uv=v.Uv,AdditionalUvs=v.AdditionalUvs,
                WeightType=PmxVertexWeightType.Bdef1,BoneIndices=[0,-1,-1,-1],BoneWeights=[1,0,0,0],EdgeScale=v.EdgeScale}).ToArray(),
            Faces=source.Faces,Textures=source.Textures,Materials=source.Materials,Bones=source.Bones,
            Morphs=[],DisplayFrames=[],SoftBodies=[],Joints=[],RigidBodies=[new(){Name="dynamic",EnglishName="",BoneIndex=0,
                Group=0,CollisionGroup=0,Shape=PmxRigidBodyShape.Sphere,ShapeSize=new(.2f),Translation=source.Bones[0].Position,
                Rotation=Vector3.Zero,Mass=1,TranslationDamping=0,RotationDamping=0,Repulsion=0,Friction=0,Operation=PmxRigidBodyOperation.Dynamic}]};
        var motion=new VmdMotion{BoneKeyframes=[new(){BoneName=model.Bones[0].Name,Frame=0,Translation=Vector3.Zero,Rotation=Quaternion.Identity,Interpolation=new byte[64]},
            new(){BoneName=model.Bones[0].Name,Frame=30,Translation=Vector3.Zero,Rotation=Quaternion.Identity,Interpolation=new byte[64]}]};
        return new(model,motion,PmxOriginalStyleAdapter.FromRestPose(model).Scene,PmxOriginalOutlineAdapter.FromRestPose(model));
    }

    [Fact] public void PhysicsChangesSurfaceAndOutlinePausedSampleReusesAndOffRestoresExact()
    {
        using var controller=Controller();var off=controller.Sample(0);var backend=new FallingBackend();
        controller.EnablePhysics(backend,0);var moved=controller.Advance(1,1.0/30);
        Assert.True(controller.PhysicsEnabled);Assert.Equal(4,backend.Steps);
        Assert.NotEqual(off.Surface.Snapshot.Vertices.ToArray(),moved.Surface.Snapshot.Vertices.ToArray());
        for(int i=0;i<moved.Outline.Vertices.Count;i++)
        {Assert.Equal(moved.Surface.Snapshot.Vertices[i].Position,moved.Outline.Vertices[i].Position);Assert.Equal(moved.Surface.Snapshot.Vertices[i].Normal,moved.Outline.Vertices[i].Normal);}
        Assert.Same(moved.Surface,controller.Sample(1).Surface);Assert.Equal(4,backend.Steps);
        controller.DisablePhysics();Assert.True(backend.Disposed);Assert.False(controller.PhysicsEnabled);
        Assert.Equal(off.Surface.Snapshot.Vertices.ToArray(),controller.Sample(0).Surface.Snapshot.Vertices.ToArray());
    }

    [Fact] public void ResetAndSeekClearPhysicsHistoryAndDisposeIsIdempotent()
    {
        var controller=Controller();var backend=new FallingBackend();controller.EnablePhysics(backend,0);
        var first=controller.Advance(1,1.0/30);controller.ResetPhysics(0);var replay=controller.Advance(1,1.0/30);
        Assert.Equal(first.Surface.Snapshot.Vertices.ToArray(),replay.Surface.Snapshot.Vertices.ToArray());
        var seek=controller.Sample(20);Assert.Equal(0,controller.PhysicsLastStepCount);
        Assert.Equal(controller.Sample(0).Surface.Snapshot.Vertices.ToArray(),seek.Surface.Snapshot.Vertices.ToArray());
        controller.Dispose();controller.Dispose();Assert.True(backend.Disposed);
        Assert.Throws<ObjectDisposedException>(()=>controller.Sample(0));
    }

    [Fact] public void FailedEnableKeepsExistingSampleAndDisposesCandidate()
    {
        using var controller=Controller();var before=controller.Sample(0);var bad=new FallingBackend{FailInitialize=true};
        Assert.Throws<InvalidOperationException>(()=>controller.EnablePhysics(bad,0));
        Assert.True(bad.Disposed);Assert.False(controller.PhysicsEnabled);Assert.Same(before.Surface,controller.Sample(0).Surface);
    }

    private sealed class FallingBackend:ICharacterPhysicsBackend
    {
        private Matrix4x4[] worlds=[];public int Steps;public bool Disposed,FailInitialize;
        public string Name=>"deterministic fixture";public IReadOnlyList<string> Diagnostics=>Array.Empty<string>();
        public void Initialize(CharacterPhysicsDefinition definition,ReadOnlySpan<Matrix4x4> input)
        {if(FailInitialize)throw new InvalidOperationException("fixture initialize failure");worlds=input.ToArray();Steps=0;}
        public void SetKinematicTarget(int index,Matrix4x4 world,float seconds)=>worlds[index]=world;
        public Matrix4x4 GetBodyWorldTransform(int index)=>worlds[index];
        public void Step(float seconds){Steps++;for(int i=0;i<worlds.Length;i++)worlds[i].Translation+=new Vector3(0,-seconds,0);}
        public void Dispose()=>Disposed=true;
    }
}
