using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Discrete model visibility, independent of bone IK switches. An existing track clamps to
/// its first/last key; no track means visible. Same-frame records use source-order last-wins.</summary>
public sealed class MmdModelVisibilityTrack
{
    private readonly (uint Frame, bool Visible)[] keys;
    public bool HasTrack => keys.Length != 0;
    public MmdModelVisibilityTrack(VmdMotion motion)
    {
        ArgumentNullException.ThrowIfNull(motion);
        keys = motion.IkKeyframes.GroupBy(key => key.Frame).Select(group => group.Last())
            .OrderBy(key => key.Frame).Select(key => (key.Frame, key.Visible)).ToArray();
    }
    public bool Evaluate(double frame)
    {
        if (!double.IsFinite(frame) || frame < 0 || frame > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(frame));
        if (keys.Length == 0) return true;
        int low = 0, high = keys.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (keys[middle].Frame <= frame) low = middle + 1; else high = middle;
        }
        return keys[Math.Max(0, low - 1)].Visible;
    }
}
