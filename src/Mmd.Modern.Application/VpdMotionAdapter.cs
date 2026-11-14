// Pose name lookup reference: Saba MMDModel::LoadPose, MMDModel.cpp:172-218
// @29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT, Copyright (c) 2016-2017 benikabocha.
// This managed adapter keeps the project's native coordinates and delegates editing to MotionEditor.
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Converts a VPD snapshot to named records for an explicit MotionEditor registration.
/// Record indices never select PMX bones; registration conflict confirmation and undo remain caller-controlled.</summary>
public static class VpdMotionAdapter
{
    public static VmdMotion Convert(VpdPose pose, PmxModel model, uint frame = 0)
    {
        ArgumentNullException.ThrowIfNull(pose);
        ArgumentNullException.ThrowIfNull(model);
        var sourceBones = pose.Bones.ToArray(); var sourceMorphs = pose.Morphs.ToArray();
        var indices = new HashSet<int>(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bone in sourceBones)
        {
            if (bone is null || bone.Index < 0 || !indices.Add(bone.Index))
                throw new ArgumentException("VPD bone records need distinct nonnegative indices.", nameof(pose));
            ValidateName(bone.BoneName, names, "bone");
            var t = bone.Translation; var q = bone.Rotation;
            if (!float.IsFinite(t.X) || !float.IsFinite(t.Y) || !float.IsFinite(t.Z)
                || !float.IsFinite(q.X) || !float.IsFinite(q.Y) || !float.IsFinite(q.Z) || !float.IsFinite(q.W)
                || (q.X == 0 && q.Y == 0 && q.Z == 0 && q.W == 0))
                throw new ArgumentException($"VPD bone '{bone.BoneName}' requires finite values and a nonzero quaternion.", nameof(pose));
        }
        indices.Clear(); names.Clear();
        foreach (var morph in sourceMorphs)
        {
            if (morph is null || morph.Index < 0 || !indices.Add(morph.Index) || !float.IsFinite(morph.Weight))
                throw new ArgumentException("VPD morph records need distinct nonnegative indices and finite weights.", nameof(pose));
            ValidateName(morph.MorphName, names, "morph");
        }
        if (model.Bones is null || model.Morphs is null)
            throw new ArgumentException("Target model requires bone and morph collections.", nameof(model));
        var boneCounts = Counts(model.Bones.Select(b => b?.Name), "bone");
        var morphCounts = Counts(model.Morphs.Select(m => m?.Name), "morph");
        var diagnostics = new List<string>();
        bool Matched(string name, Dictionary<string, int> counts, string kind)
        {
            if (!counts.TryGetValue(name, out int count))
            {
                diagnostics.Add($"VPD {kind} '{name}' has no exact PMX name match and was not registered.");
                return false;
            }
            if (count != 1) throw new ArgumentException($"VPD {kind} '{name}' matches multiple PMX records; index fallback is forbidden.", nameof(model));
            return true;
        }
        byte[] linear = PmmMotionAdapter.ConvertBoneInterpolation(
            [20, 20, 107, 107, 20, 20, 107, 107, 20, 20, 107, 107, 20, 20, 107, 107]);
        var bones = sourceBones.Where(b => Matched(b.BoneName, boneCounts, "bone")).Select(b => new VmdBoneKeyframe
        { BoneName = b.BoneName, Frame = frame, Translation = b.Translation, Rotation = b.Rotation, Interpolation = linear }).ToArray();
        var morphs = sourceMorphs.Where(m => Matched(m.MorphName, morphCounts, "morph")).Select(m => new VmdMorphKeyframe
        { MorphName = m.MorphName, Frame = frame, Weight = m.Weight }).ToArray();
        if (bones.Length == 0 && morphs.Length == 0)
            throw new NotSupportedException("VPD has no exact bone or morph names matching the PMX model.");
        return new()
        {
            ModelName = model.Name, BoneKeyframes = Array.AsReadOnly(bones), MorphKeyframes = Array.AsReadOnly(morphs),
            Diagnostics = diagnostics.AsReadOnly()
        };
    }

    private static void ValidateName(string name, HashSet<string> names, string kind)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0') || !names.Add(name))
            throw new ArgumentException($"VPD {kind} names must be nonempty, NUL-free and distinct.", nameof(name));
    }

    private static Dictionary<string, int> Counts(IEnumerable<string?> names, string kind)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string? name in names)
        {
            if (name is null || name.Contains('\0'))
                throw new ArgumentException($"Target PMX {kind} records need valid names.", nameof(names));
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        return counts;
    }
}
