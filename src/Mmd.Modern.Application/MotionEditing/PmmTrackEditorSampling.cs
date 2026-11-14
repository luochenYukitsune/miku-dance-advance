using System.Numerics;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application.MotionEditing;

/// <summary>Raw PMM accessory channels, without renderer units or loaded .x geometry.</summary>
public readonly record struct PmmAccessoryPose(Vector3 Translation, Vector3 Rotation, float Scale, float Opacity,
    bool Visible, bool ShadowEnabled, int ParentModelIndex, int ParentBoneIndex);

/// <summary>Backend-neutral PMM accessory sampling. Continuous local channels interpolate linearly; parent, visibility and shadow use preceding-key hold.
/// These explicit policies are testable; original MMD transition parity remains a separate oracle check.</summary>
public static class PmmTrackSampling
{
    public static PmmAccessoryPose AccessoryAt(PmmDocument document, int accessoryIndex, double frame, bool saved = false)
    {
        ArgumentNullException.ThrowIfNull(document); ValidateFrame(frame);
        var accessory = document.Accessories.SingleOrDefault(accessory => accessory.Index == accessoryIndex)
            ?? throw new KeyNotFoundException($"PMM stored accessory index {accessoryIndex} does not exist.");
        return saved ? new(accessory.Translation, accessory.Rotation, accessory.Scale, accessory.Opacity, accessory.Visible, accessory.ShadowEnabled, accessory.ParentModelIndex, accessory.ParentBoneIndex)
            : AccessoryAt(new[] { accessory.InitialKeyframe }.Concat(accessory.Keyframes).GroupBy(key => key.Frame).Select(group => group.Last()).OrderBy(key => key.Frame).ToArray(), frame);
    }
    public static IReadOnlyList<PmmOutsideParent> OutsideParentsAt(PmmDocument document, int modelIndex, double frame, bool saved = false)
    {
        ArgumentNullException.ThrowIfNull(document); ValidateFrame(frame);
        var model = PmmTrackEditorRecords.Model(document, modelIndex);
        var parents = saved ? model.OutsideParentStates.Select(state => state.Parent) : PmmTrackEditorRecords.ModelAt(model, frame).OutsideParents;
        return Array.AsReadOnly(parents.Select(parent => parent with { }).ToArray());
    }
    public static byte PackOpacity(float opacity, bool visible)
    {
        if (!float.IsFinite(opacity) || opacity < 0 || opacity > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
        int transparency = (int)MathF.Round((1 - opacity) * 100, MidpointRounding.AwayFromZero);
        return (byte)((transparency << 1) | (visible ? 1 : 0));
    }
    internal static PmmAccessoryPose AccessoryAt(PmmAccessoryKeyframe[] keys, double frame)
    {
        ValidateFrame(frame);
        var a = PmmTrackEditorRecords.At(keys, frame); int index = Array.IndexOf(keys, a);
        var b = frame <= a.Frame || index == keys.Length - 1 ? a : keys[index + 1];
        float weight = a == b ? 0 : (float)((frame - a.Frame) / (b.Frame - (double)a.Frame));
        return new(Vector3.Lerp(a.Translation, b.Translation, weight), Vector3.Lerp(a.Rotation, b.Rotation, weight),
            (1 - weight) * a.Scale + weight * b.Scale, (1 - weight) * a.Opacity + weight * b.Opacity,
            a.Visible, a.ShadowEnabled, a.ParentModelIndex, a.ParentBoneIndex);
    }
    static void ValidateFrame(double frame)
    { if (!double.IsFinite(frame) || frame < 0) throw new ArgumentOutOfRangeException(nameof(frame)); }
}
