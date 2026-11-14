using System.Numerics;

namespace Mmd.Modern.Animation;

/// <summary>Which endpoint supplies a segment's four interpolation curves.</summary>
public enum SegmentCurveOwnership { Previous, Next }

public readonly record struct BoneCurves(BezierCurve X, BezierCurve Y, BezierCurve Z, BezierCurve Rotation)
{
    public static BoneCurves Linear { get; } = new(BezierCurve.Linear, BezierCurve.Linear, BezierCurve.Linear, BezierCurve.Linear);
}

public sealed record BoneKeyframe
{
    public uint Frame { get; }
    public Vector3 Translation { get; }
    public Quaternion Rotation { get; }
    public BoneCurves Curves { get; }

    public BoneKeyframe(uint frame, Vector3 translation, Quaternion rotation, BoneCurves curves)
    {
        if (!AnimationValidation.Finite(translation)) throw new ArgumentException("Key translation must be finite.", nameof(translation));
        double lengthSquared = (double)rotation.X * rotation.X + (double)rotation.Y * rotation.Y
            + (double)rotation.Z * rotation.Z + (double)rotation.W * rotation.W;
        if (!double.IsFinite(lengthSquared) || lengthSquared == 0) throw new ArgumentException("Key rotation must be finite and nonzero.", nameof(rotation));
        double length = Math.Sqrt(lengthSquared);
        Frame = frame; Translation = translation; Curves = curves;
        Rotation = new((float)(rotation.X / length), (float)(rotation.Y / length), (float)(rotation.Z / length), (float)(rotation.W / length));
    }
}

public sealed class BoneTrack
{
    private readonly BoneKeyframe[] keys;
    public string BoneName { get; }
    public IReadOnlyList<BoneKeyframe> Keys { get; }
    internal BoneKeyframe[] KeyArray => keys;

    public BoneTrack(string boneName, IEnumerable<BoneKeyframe> keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boneName);
        ArgumentNullException.ThrowIfNull(keys);
        BoneName = boneName;
        this.keys = keys.ToArray();
        if (this.keys.Any(key => key is null)) throw new ArgumentException("Null keyframe.", nameof(keys));
        Array.Sort(this.keys, (a, b) => a.Frame.CompareTo(b.Frame));
        for (int i = 1; i < this.keys.Length; i++)
            if (this.keys[i - 1].Frame == this.keys[i].Frame) throw new ArgumentException("Duplicate bone keyframe number.", nameof(keys));
        Keys = Array.AsReadOnly(this.keys);
    }
}

/// <summary>Immutable format-neutral bone motion. Next-endpoint curves are the explicit default policy.</summary>
public sealed class MotionClip
{
    public IReadOnlyList<BoneTrack> Tracks { get; }
    public SegmentCurveOwnership CurveOwnership { get; }
    public uint DurationFrames { get; }

    public MotionClip(IEnumerable<BoneTrack> tracks, SegmentCurveOwnership curveOwnership = SegmentCurveOwnership.Next)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (!Enum.IsDefined(curveOwnership)) throw new ArgumentOutOfRangeException(nameof(curveOwnership));
        var copy = tracks.ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in copy)
        {
            if (track is null || !names.Add(track.BoneName)) throw new ArgumentException("Null or duplicate bone track.", nameof(tracks));
            if (track.Keys.Count > 0) DurationFrames = Math.Max(DurationFrames, track.Keys[^1].Frame);
        }
        Tracks = Array.AsReadOnly(copy); CurveOwnership = curveOwnership;
    }
}

internal static class AnimationValidation
{
    internal static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    internal static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
