using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Per-controller step tracks. Model visibility is not an IK enable switch.</summary>
public sealed class MmdIkMotionAdapter
{
    private readonly Dictionary<int,KeyValuePair<uint,bool>[]> tracks=[];
    public uint DurationFrames {get;}
    public IReadOnlyList<string> Diagnostics {get;}
    public bool HasMatchedSwitches=>tracks.Count!=0;
    public MmdIkMotionAdapter(IReadOnlyDictionary<string,int> controllerBones,VmdMotion motion)
    {
        var imported=new Dictionary<int,Dictionary<uint,bool>>();var diagnostics=new List<string>();
        foreach(var key in motion.IkKeyframes)
        {
            DurationFrames=Math.Max(DurationFrames,key.Frame);
            foreach(var item in key.Switches)
            {
                if(!controllerBones.TryGetValue(item.BoneName,out int index))
                {diagnostics.Add($"IK switch '{item.BoneName}' has no matching IK controller.");continue;}
                if(!imported.TryGetValue(index,out var values))imported[index]=values=[];
                values[key.Frame]=item.Enabled;
            }
        }
        foreach(var (index,values) in imported)tracks[index]=values.OrderBy(p=>p.Key).ToArray();
        if(motion.IkKeyframes.Count>0)diagnostics.Add("VMD model visibility is retained in Formats and is not applied by this viewer; it never changes IK enable state.");
        Diagnostics=diagnostics.Distinct(StringComparer.Ordinal).ToArray();
    }
    public IReadOnlyDictionary<int,bool> Evaluate(uint frame)
    {
        var values=new Dictionary<int,bool>();
        foreach(var (index,keys) in tracks)
        {
            int low=0,high=keys.Length;
            while(low<high){int mid=low+(high-low)/2;if(keys[mid].Key<=frame)low=mid+1;else high=mid;}
            // Original PMX creates an enabled frame-zero seed. No override means enabled.
            if(low>0)values[index]=keys[low-1].Value;
        }
        return values;
    }
}
