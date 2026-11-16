using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using System.Numerics;

namespace Mmd.Modern.Application;

/// <summary>PMX rig semantics at the format/domain boundary, independent of GUI and GPU objects.</summary>
public static class PmxCharacterRigAdapter
{
    public static CharacterRig Convert(PmxModel model,SceneSnapshot rest)=>Convert(model,rest,out _);

    /// <summary>Many distributed models carry data that PMXEditor and MMD tolerate: duplicate bone names,
    /// append/IK references of -1, repeated IK links, swapped limits and weights that are slightly off.
    /// These are repaired here, at the format boundary, so the strict rig invariants still hold and the model
    /// loads instead of failing as a whole. Each repair is reported. Safety budgets (IK iterations, link counts,
    /// non-finite positions) are not relaxed.</summary>
    public static CharacterRig Convert(PmxModel model,SceneSnapshot rest,out IReadOnlyList<string> repairs)
    {
        var features=UnsupportedRigFeatures.None;
        if(model.Bones.Any(b=>b.Flags.HasFlag(PmxBoneFlags.DeformOuterParent)))features|=UnsupportedRigFeatures.ExternalParent;
        if(model.RigidBodies.Count!=0)features|=UnsupportedRigFeatures.Physics;
        var log=new RepairLog();
        int count=model.Bones.Count;
        var names=new HashSet<string>(StringComparer.Ordinal);
        // Generated names must avoid every original name, not only those already visited: with ["A","A","A#1"] the
        // second bone may not take "A#1" from the third, whose authored name is unique.
        var authored=model.Bones.Select(b=>b.Name).Where(name=>!string.IsNullOrWhiteSpace(name)).ToHashSet(StringComparer.Ordinal);
        RigBone Bone(PmxBone b,int index)
        {
            string name=b.Name;
            if(string.IsNullOrWhiteSpace(name)||!names.Add(name))
            {
                // MMD binds motion to the first bone with a name; later duplicates get a unique, unmatchable name.
                string original=name;name=$"{name}#{index}";
                while(authored.Contains(name)||!names.Add(name))name+="#";
                log.Add($"bone {index} '{original}' renamed to '{name}' (duplicate or empty name)");
            }
            int parent=b.ParentIndex;
            if(parent<-1||parent>=count||parent==index){log.Add($"bone {index} parent {parent} treated as root");parent=-1;}
            bool rotate=b.Flags.HasFlag(PmxBoneFlags.AppendRotate),translate=b.Flags.HasFlag(PmxBoneFlags.AppendTranslate);
            AppendTransform? append=null;
            if(rotate||translate)
            {
                if(b.AppendBoneIndex<0||b.AppendBoneIndex>=count||b.AppendBoneIndex==index||!float.IsFinite(b.AppendWeight)||MathF.Abs(b.AppendWeight)>1000)
                    log.Add($"bone {index} append source {b.AppendBoneIndex} (weight {b.AppendWeight}) ignored");
                else append=new(b.AppendBoneIndex,b.AppendWeight,b.Flags.HasFlag(PmxBoneFlags.AppendLocal),rotate,translate);
            }
            IkDefinition? ik=null;
            if(b.Flags.HasFlag(PmxBoneFlags.Ik))
            {
                if(b.IkTargetBoneIndex<0||b.IkTargetBoneIndex>=count)log.Add($"bone {index} IK target {b.IkTargetBoneIndex} invalid; IK ignored");
                else ik=CreateIk(b,index,count,log);
            }
            return new(name,b.Position,parent,b.DeformDepth,b.Flags.HasFlag(PmxBoneFlags.DeformAfterPhysics),append,ik,
                b.Flags.HasFlag(PmxBoneFlags.FixedAxis)?b.FixedAxis:null);
        }
        var bones=model.Bones.Select(Bone).ToArray();
        RemoveParentCycles(bones,log);
        RemoveAppendCycles(bones,log);
        var bindings=model.Vertices.Select((v,i)=>Binding(v,i,count,log)).ToArray();
        repairs=log.Lines;
        return new(bones,rest.Vertices.ToArray(),bindings,features,BoneEvaluationSchedule.Mmd);
    }

    private sealed class RepairLog
    {
        private const int Limit=24;
        private readonly List<string> lines=[];private int total;
        public void Add(string line){total++;if(lines.Count<Limit)lines.Add("Model repaired: "+line+".");}
        public IReadOnlyList<string> Lines=>total>Limit?[..lines,$"Model repaired: {total-Limit} more repair(s) omitted."]:lines.ToArray();
    }

    private static void RemoveParentCycles(RigBone[] bones,RepairLog log)
    {
        var state=new byte[bones.Length];
        for(int i=0;i<bones.Length;i++)
        {
            int current=i;var path=new List<int>();
            while(current>=0&&state[current]==0){state[current]=1;path.Add(current);current=bones[current].ParentIndex;}
            if(current>=0&&state[current]==1)
            {
                int closing=path[^1];bones[closing]=bones[closing] with{ParentIndex=-1};
                log.Add($"bone {closing} parent cycle broken; treated as root");
            }
            foreach(int visited in path)state[visited]=2;
        }
    }

    private static void RemoveAppendCycles(RigBone[] bones,RepairLog log)
    {
        var state=new byte[bones.Length];
        for(int i=0;i<bones.Length;i++)
        {
            int current=i;var path=new List<int>();
            while(current>=0&&state[current]==0){state[current]=1;path.Add(current);current=bones[current].Append?.SourceBoneIndex??-1;}
            if(current>=0&&state[current]==1)
            {
                // Break the cycle at the bone that closes it.
                int closing=path[^1];bones[closing]=bones[closing] with{Append=null};
                log.Add($"bone {closing} append removed (append references form a cycle)");
            }
            foreach(int visited in path)state[visited]=2;
        }
    }

    private static SkinBinding Binding(PmxVertex v,int index,int boneCount,RepairLog log)
    {
        var kind=(SkinningKind)v.WeightType;
        int slots=kind switch{SkinningKind.Bdef1=>1,SkinningKind.Bdef4 or SkinningKind.Qdef=>4,_=>2};
        Span<int> bone=[v.BoneIndices[0],v.BoneIndices[1],v.BoneIndices[2],v.BoneIndices[3]];
        Span<float> weight=[v.BoneWeights[0],v.BoneWeights[1],v.BoneWeights[2],v.BoneWeights[3]];
        bool changed=false;double sum=0;
        for(int slot=0;slot<4;slot++)
        {
            if(bone[slot]<-1||bone[slot]>=boneCount){bone[slot]=-1;changed=true;}
            float w=weight[slot];
            if(slot>=slots||!float.IsFinite(w)||w<=0||bone[slot]<0)
            {
                // Unused slots, negative/NaN weights and weights on a missing bone contribute nothing.
                if(w!=0){weight[slot]=0;changed=true;}
                continue;
            }
            if(w>1){weight[slot]=1;changed=true;}
            sum+=weight[slot];
        }
        if(sum<=0)
        {
            int first=-1;for(int slot=0;slot<slots&&first<0;slot++)if(bone[slot]>=0&&bone[slot]<boneCount)first=slot;
            if(first<0){first=0;bone[0]=0;}
            weight.Clear();weight[first]=1;sum=1;changed=true;
        }
        else if(Math.Abs(sum-1)>1e-4)changed=true;
        if(changed)
        {
            for(int slot=0;slot<4;slot++)weight[slot]=(float)(weight[slot]/sum);
            log.Add($"vertex {index} skin weights normalized");
        }
        Vector3 c=v.SdefC,r0=v.SdefR0,r1=v.SdefR1;
        if(kind==SkinningKind.Sdef&&!(Finite(c)&&Finite(r0)&&Finite(r1)))
        {kind=SkinningKind.Bdef2;c=r0=r1=default;log.Add($"vertex {index} SDEF parameters not finite; skinned as BDEF2");}
        return new(kind,bone[0],bone[1],bone[2],bone[3],new(weight[0],weight[1],weight[2],weight[3]),c,r0,r1);
    }
    private static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);

    private static IkDefinition CreateIk(PmxBone bone,int index,int boneCount,RepairLog log)
    {
        var links=new List<IkLinkDefinition>();var seen=new HashSet<int>();
        foreach(var link in bone.IkLinks)
        {
            if(link.BoneIndex<0||link.BoneIndex>=boneCount||!seen.Add(link.BoneIndex))
            {log.Add($"bone {index} IK link {link.BoneIndex} skipped (invalid or repeated)");continue;}
            Vector3? min=null,max=null;
            if(link.EnableLimit)
            {
                if(!Finite(link.LimitMin)||!Finite(link.LimitMax))
                    log.Add($"bone {index} IK link {link.BoneIndex} limits not finite; unlimited");
                else
                {
                    var a=Vector3.Clamp(link.LimitMin,new(-MathF.Tau),new(MathF.Tau));var b=Vector3.Clamp(link.LimitMax,new(-MathF.Tau),new(MathF.Tau));
                    min=Vector3.Min(a,b);max=Vector3.Max(a,b);
                    if(min!=link.LimitMin||max!=link.LimitMax)log.Add($"bone {index} IK link {link.BoneIndex} limits reordered or clamped");
                }
            }
            links.Add(new(link.BoneIndex,min,max));
        }
        // Validate the raw values before normalization; scaling or the original ushort storage
        // must not hide values outside the existing evaluator's safety budget.
        var raw=new IkDefinition(bone.IkTargetBoneIndex,bone.IkIterationCount,bone.IkLimit,links);
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
