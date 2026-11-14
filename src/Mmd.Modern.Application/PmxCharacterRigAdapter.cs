using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>PMX rig semantics at the format/domain boundary, independent of GUI and GPU objects.</summary>
public static class PmxCharacterRigAdapter
{
    public static CharacterRig Convert(PmxModel model,SceneSnapshot rest)
    {
        var features=UnsupportedRigFeatures.None;
        if(model.Bones.Any(b=>b.Flags.HasFlag(PmxBoneFlags.DeformOuterParent)))features|=UnsupportedRigFeatures.ExternalParent;
        if(model.RigidBodies.Count!=0)features|=UnsupportedRigFeatures.Physics;
        RigBone Bone(PmxBone b)
        {
            bool rotate=b.Flags.HasFlag(PmxBoneFlags.AppendRotate),translate=b.Flags.HasFlag(PmxBoneFlags.AppendTranslate);
            AppendTransform? append=rotate||translate?new(b.AppendBoneIndex,b.AppendWeight,b.Flags.HasFlag(PmxBoneFlags.AppendLocal),rotate,translate):null;
            IkDefinition? ik=b.Flags.HasFlag(PmxBoneFlags.Ik)?CreateIk(b):null;
            return new(b.Name,b.Position,b.ParentIndex,b.DeformDepth,b.Flags.HasFlag(PmxBoneFlags.DeformAfterPhysics),append,ik,
                b.Flags.HasFlag(PmxBoneFlags.FixedAxis)?b.FixedAxis:null);
        }
        return new(model.Bones.Select(Bone),rest.Vertices.ToArray(),model.Vertices.Select(v=>new SkinBinding((SkinningKind)v.WeightType,
            v.BoneIndices[0],v.BoneIndices[1],v.BoneIndices[2],v.BoneIndices[3],new(v.BoneWeights[0],v.BoneWeights[1],v.BoneWeights[2],v.BoneWeights[3]),
            v.SdefC,v.SdefR0,v.SdefR1)),features,BoneEvaluationSchedule.Mmd);
    }

    private static IkDefinition CreateIk(PmxBone bone)
    {
        // Validate the raw values before normalization; scaling or the original ushort storage
        // must not hide values outside the existing evaluator's safety budget.
        var raw=new IkDefinition(bone.IkTargetBoneIndex,bone.IkIterationCount,bone.IkLimit,
            bone.IkLinks.Select(link=>new IkLinkDefinition(link.BoneIndex,
                link.EnableLimit?link.LimitMin:null,link.EnableLimit?link.LimitMax:null)));
        float angle=raw.AngleLimit*.25f;
        int iterations=raw.Iterations;
        // Original 9.32 PMX loader, RVA ACFAD-AD018: quarter the angle, then raise the
        // iteration minimum using single-precision pi (bits 0x40490fd8), capped at 360.
        // Its zero-angle branch skips the division; a raw zero iteration count is still raised.
        if(angle!=0)
            iterations=Math.Max(iterations,(int)MathF.Min(360f,3.141592025756836f/angle));
        return new(raw.TargetBoneIndex,iterations,angle,raw.Links,raw.AnglePolicy);
    }
}
