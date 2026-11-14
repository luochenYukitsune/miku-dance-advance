using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;
namespace Mmd.Modern.Tests;

public sealed class PmxCharacterPhysicsAdapterTests
{
    [Fact] public void PreservesPmxShapeModeMasksAndSkipsInvalidJointPairs()
    {
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"fixtures","rich-w1-uv4.pmx"));var source=PmxReader.Read(input);
        PmxRigidBody Body(PmxRigidBodyShape shape,PmxRigidBodyOperation mode)=>new(){Name="body",EnglishName="",BoneIndex=0,Group=3,CollisionGroup=0x15,
            Shape=shape,ShapeSize=new(.5f,1,2),Translation=new(0,11,0),Rotation=new(.1f,.2f,.3f),Mass=1,TranslationDamping=.1f,RotationDamping=.2f,
            Repulsion=.3f,Friction=.4f,Operation=mode};
        PmxJoint Joint(int a,int b)=>new(){Name="joint",EnglishName="",Type=PmxJointType.SpringDof6,RigidBodyAIndex=a,RigidBodyBIndex=b,
            Translation=new(0,11,0),Rotation=new(.3f,.2f,.1f),TranslationLowerLimit=new(-1),TranslationUpperLimit=new(1),RotationLowerLimit=new(-.2f),
            RotationUpperLimit=new(.2f),SpringTranslationFactor=new(3),SpringRotationFactor=new(4)};
        var model=new PmxModel {Header=source.Header,Name="test",EnglishName="",Comment="",EnglishComment="",Vertices=[],Faces=[],Textures=[],Materials=[],
            Bones=[new(){Name="root",EnglishName="",Position=new(0,10,0),ParentIndex=-1,DeformDepth=0,Flags=PmxBoneFlags.None}],Morphs=[],DisplayFrames=[],SoftBodies=[],
            RigidBodies=[Body(PmxRigidBodyShape.Box,PmxRigidBodyOperation.Static),Body(PmxRigidBodyShape.Capsule,PmxRigidBodyOperation.DynamicAndBoneMerge)],
            Joints=[Joint(0,1),Joint(-1,1),Joint(0,0)]};
        var adapted=PmxCharacterPhysicsAdapter.Convert(model);var definition=adapted.Definition;
        Assert.Equal(2,definition.Bodies.Count);Assert.Equal(PhysicsShapeKind.Box,definition.Bodies[0].Shape);
        Assert.Equal(PhysicsBodyMode.Kinematic,definition.Bodies[0].Mode);Assert.Equal(PhysicsBodyMode.DynamicAligned,definition.Bodies[1].Mode);
        Assert.Equal((ushort)0x15,definition.Bodies[0].CollisionMask);Assert.Equal(3,definition.Bodies[0].CollisionGroup);
        Assert.Equal(new Vector3(.1f,.2f,.3f),definition.Bodies[0].Rotation);
        var joint=Assert.Single(definition.Joints);Assert.Equal(new Vector3(3),joint.LinearSpring);Assert.Equal(new Vector3(4),joint.AngularSpring);
        Assert.Equal(2,adapted.Diagnostics.Count);
        Assert.InRange(Vector3.Distance(definition.BodyRestWorldTransforms[0].Translation,new(0,11,0)),0,.00001f);
    }
}
