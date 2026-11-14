using System.Numerics;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

public sealed record PmxCharacterPhysicsAdaptation(CharacterPhysicsDefinition Definition,IReadOnlyList<string> Diagnostics);

/// <summary>Only format mapping: numerical solver selection and lifetime belong to the caller.</summary>
public static class PmxCharacterPhysicsAdapter
{
    public static PmxCharacterPhysicsAdaptation Convert(PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var diagnostics=new List<string>();var joints=new List<PhysicsJointDefinition>();
        foreach(var joint in model.Joints)
        {
            if(joint.RigidBodyAIndex<0||joint.RigidBodyAIndex>=model.RigidBodies.Count||joint.RigidBodyBIndex<0||joint.RigidBodyBIndex>=model.RigidBodies.Count
                ||joint.RigidBodyAIndex==joint.RigidBodyBIndex)
            {diagnostics.Add($"Joint '{joint.Name}' has no valid pair of rigid bodies; skipped like Saba.");continue;}
            joints.Add(new(joint.Name,joint.RigidBodyAIndex,joint.RigidBodyBIndex,(byte)joint.Type,joint.Translation,joint.Rotation,
                joint.TranslationLowerLimit,joint.TranslationUpperLimit,joint.RotationLowerLimit,joint.RotationUpperLimit,joint.SpringTranslationFactor,joint.SpringRotationFactor));
        }
        if(model.SoftBodies.Count>0)diagnostics.Add("PMX soft bodies require the separate soft-body backend; Kinematic Near is supported there, while physical Near and rigid reverse impulses require reverse feedback.");
        var definition=new CharacterPhysicsDefinition(model.Bones.Select(b=>new PhysicsBoneDefinition(b.ParentIndex,Matrix4x4.CreateTranslation(b.Position))),
            model.RigidBodies.Select(b=>new PhysicsBodyDefinition(b.Name,b.BoneIndex,(PhysicsShapeKind)b.Shape,b.ShapeSize,b.Translation,b.Rotation,b.Mass,
                b.TranslationDamping,b.RotationDamping,b.Repulsion,b.Friction,b.Group,b.CollisionGroup,(PhysicsBodyMode)b.Operation)),joints);
        return new(definition,diagnostics.AsReadOnly());
    }
}
