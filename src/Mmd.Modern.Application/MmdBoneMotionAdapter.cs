using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>MMD import policy, kept separate from the format-neutral animation evaluator.</summary>
public static class MmdBoneMotionAdapter
{
    public static MotionClip Convert(VmdMotion motion)
    {
        ArgumentNullException.ThrowIfNull(motion);
        BoneTrack Track(IGrouping<string,VmdBoneKeyframe> group)
        {
            // Original import overwrites an existing frame in file order. Every bone starts at rest on frame zero.
            var keys=new Dictionary<uint,BoneKeyframe> { [0]=new(0,Vector3.Zero,Quaternion.Identity,BoneCurves.Linear) };
            foreach(var key in group)
            {
                // MMD reads each channel from its redundant 16-byte VMD block.
                // See keyframe-rea/evidence.md; standard VMD repeats the relevant bytes in these blocks.
                BezierCurve Curve(int c)
                {
                    int offset=16*c;
                    return BezierCurve.FromMmd(key.Interpolation[offset],key.Interpolation[offset+4],
                        key.Interpolation[offset+8],key.Interpolation[offset+12]);
                }
                keys[key.Frame]=new(key.Frame,key.Translation,key.Rotation,new(Curve(0),Curve(1),Curve(2),Curve(3)));
            }
            return new(group.Key,keys.Values);
        }
        return new(motion.BoneKeyframes.GroupBy(k=>k.BoneName,StringComparer.Ordinal).Select(Track),SegmentCurveOwnership.Next);
    }
}
