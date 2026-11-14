using System.Numerics;

namespace Mmd.Modern.Animation;

/// <summary>Format-neutral PMX grant. Weight may be negative or greater than one.</summary>
public sealed record AppendTransform(int SourceBoneIndex, float Weight, bool Local, bool Rotate, bool Translate);

/// <summary>XYZ limits are radians in the unconverted model coordinate system.</summary>
public sealed record IkLinkDefinition(int BoneIndex, Vector3? MinimumAngles = null, Vector3? MaximumAngles = null);

public enum IkAnglePolicy { Mmd, Saba }

/// <summary>Bone scheduling is independent of the IK angular/limit policy.</summary>
public enum BoneEvaluationSchedule { Saba, Mmd }

/// <summary>Absolute raw motion transform, relative to the bone's rest position and before morphs, append or IK.</summary>
public readonly record struct BoneLocalTransform(Vector3 Translation, Quaternion Rotation);

public sealed class IkDefinition
{
    public int TargetBoneIndex { get; }
    public int Iterations { get; }
    public float AngleLimit { get; }
    public IReadOnlyList<IkLinkDefinition> Links { get; }
    public IkAnglePolicy AnglePolicy { get; }

    public IkDefinition(int targetBoneIndex, int iterations, float angleLimit, IEnumerable<IkLinkDefinition> links,
        IkAnglePolicy anglePolicy = IkAnglePolicy.Mmd)
    {
        ArgumentNullException.ThrowIfNull(links);
        if (!Enum.IsDefined(anglePolicy)) throw new ArgumentOutOfRangeException(nameof(anglePolicy));
        TargetBoneIndex = targetBoneIndex; Iterations = iterations; AngleLimit = angleLimit; AnglePolicy = anglePolicy;
        // An invalid or hostile rig must fail before sampling can start unbounded work.
        if (iterations is < 0 or > 2048) throw new ArgumentOutOfRangeException(nameof(iterations), "IK iterations must be between 0 and 2048.");
        if (!float.IsFinite(angleLimit) || angleLimit < 0 || angleLimit > 2 * MathF.PI)
            throw new ArgumentOutOfRangeException(nameof(angleLimit), "IK step angle must be in [0, 2pi].");
        var copy = links.Take(257).ToArray();
        if (copy.Length > 256) throw new ArgumentException("IK supports at most 256 links.", nameof(links));
        if (copy.Any(link => link is null)) throw new ArgumentException("Null IK link.", nameof(links));
        Links = Array.AsReadOnly(copy);
    }
}

/// <summary>IK overrides use controller indices. Outside parents are target bone-world transforms;
/// keys >= 0 replace that bone's ordinary parent and rest offset, while -1 attaches the whole model's roots.</summary>
public sealed record CharacterEvaluationOptions(bool EnableIk = true, IReadOnlyDictionary<int, bool>? IkEnabledOverrides = null,
    MorphPose? Morphs = null, IReadOnlyDictionary<int, Matrix4x4>? OutsideParents = null,
    IReadOnlyCollection<int>? PhysicsDrivenBoneIndices = null,
    IReadOnlyDictionary<int, BoneLocalTransform>? LocalBoneOverrides = null);
