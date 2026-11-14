using System.Numerics;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>WorldGravity is the non-noisy normalized acceleration. PmmGravityNoise owns the temporal noise state.</summary>
public readonly record struct PmmGravityPose(float Acceleration, Vector3 Direction, bool NoiseEnabled, int Noise,
    Vector3 WorldGravity);

/// <summary>Immutable, solver-independent PMM physics controls. Bone flags use preceding-key STEP.
/// Gravity scalar/vector channels use the original linear evaluator; noise enable uses preceding-key hold.</summary>
public sealed class PmmPhysicsControlTracks
{
    private readonly record struct GravityKey(int Frame, PmmGravityPose Pose);
    private readonly record struct BoneKey(int Frame, bool Disabled);
    private sealed record ModelTrack(BoneKey[][] Bones, IReadOnlyList<int> Saved);
    private readonly GravityKey[] gravity;
    private readonly Dictionary<int, ModelTrack> models = [];

    public PmmGravityPose SavedGravity { get; }
    public uint DurationFrames { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    public PmmPhysicsControlTracks(PmmDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Gravity is not { InitialKeyframe: not null, Keyframes: not null } source || document.Models is null)
            throw new InvalidDataException("PMM physics controls require non-null gravity and model records.");
        SavedGravity = Pose(source.Acceleration, source.Direction, source.NoiseEnabled, source.Noise);
        var gravityKeys = new List<GravityKey>();
        foreach (var key in new[] { source.InitialKeyframe }.Concat(source.Keyframes))
        {
            if (key is null || key.Frame < 0)
                throw new InvalidDataException("PMM gravity keys require nonnegative frames and non-null records.");
            // Validate all records before eliminating duplicates, including records that will not be sampled.
            gravityKeys.Add(new(key.Frame, Pose(key.Acceleration, key.Direction, key.NoiseEnabled, key.Noise)));
        }
        gravity = gravityKeys.GroupBy(k => k.Frame).Select(group => group.Last()).OrderBy(k => k.Frame).ToArray();
        int duration = gravity[^1].Frame;
        foreach (var model in document.Models)
        {
            if (model is null || models.ContainsKey(model.Index))
                throw new InvalidDataException("PMM physics controls require unique non-null model records.");
            if (model.BoneNames is null || model.BoneStates is null || model.InitialBoneKeyframes is null || model.BoneKeyframes is null
                || model.BoneStates.Count != model.BoneNames.Count || model.BoneStates.Any(state => state is null)
                || model.InitialBoneKeyframes.Concat(model.BoneKeyframes).Any(key => key is null))
                throw new InvalidDataException("PMM physics saved state and key counts must match the bone name table.");
            var resolved = PmmMotionAdapter.ResolveTracks(model.InitialBoneKeyframes, model.BoneKeyframes, model.BoneNames.Count, "physics bone");
            var bones = new BoneKey[model.BoneNames.Count][];
            foreach (var group in resolved.GroupBy(pair => pair.Track))
            {
                bones[group.Key] = group.GroupBy(pair => pair.Key.Frame).Select(sameFrame => sameFrame.Last().Key)
                    .OrderBy(key => key.Frame).Select(key => new BoneKey(key.Frame, key.PhysicsDisabled)).ToArray();
                duration = Math.Max(duration, bones[group.Key][^1].Frame);
            }
            int[] saved = model.BoneStates.Select((state, index) => (state, index)).Where(pair => pair.state.PhysicsDisabled)
                .Select(pair => pair.index).ToArray();
            models.Add(model.Index, new(bones, Array.AsReadOnly(saved)));
        }
        DurationFrames = (uint)duration;
        var diagnostics = new List<string>();
        if (SavedGravity.NoiseEnabled || gravityKeys.Any(key => key.Pose.NoiseEnabled))
            diagnostics.Add("PMM gravity noise requires the stateful PmmGravityNoise runtime; a sampled WorldGravity value alone does not execute noise.");
        Diagnostics = diagnostics.AsReadOnly();
    }

    public PmmGravityPose GravityAt(double frame)
    {
        ValidateTime(frame);
        int lo = 0, hi = gravity.Length;
        while (lo < hi)
        {
            int middle = lo + (hi - lo) / 2;
            if (gravity[middle].Frame <= frame) lo = middle + 1; else hi = middle;
        }
        int index = Math.Max(0, lo - 1);
        var left = gravity[index];
        if (frame <= left.Frame || index == gravity.Length - 1) return left.Pose;
        var right = gravity[index + 1];
        // Original FUN_14005d310: subtract -> multiply -> add in binary32, never normalized-direction Lerp.
        // Keep scalar operations separate; Vector3.Lerp can use a different algebraic/JIT instruction order.
        float weight = (float)(frame - left.Frame) / (float)(right.Frame - left.Frame);
        float Interpolate(float a, float b) { float difference = b - a; float scaled = difference * weight; return scaled + a; }
        var a = left.Pose; var b = right.Pose;
        int delta = unchecked(b.Noise - a.Noise);
        float scaledNoise = (float)delta * weight;
        // cvttss2si returns INT_MIN for unrepresentable inputs. PMM retains the full int32 domain.
        int integralNoise = scaledNoise >= 2147483648f || scaledNoise < -2147483648f ? int.MinValue : (int)scaledNoise;
        return Pose(Interpolate(a.Acceleration, b.Acceleration),
            new(Interpolate(a.Direction.X, b.Direction.X), Interpolate(a.Direction.Y, b.Direction.Y), Interpolate(a.Direction.Z, b.Direction.Z)),
            a.NoiseEnabled, unchecked(integralNoise + a.Noise));
    }

    /// <summary>Returns PMM name-table indices. The host must resolve names to PMX bones before changing bodies.</summary>
    public IReadOnlyList<int> DisabledBonesAt(int modelIndex, double frame, bool saved = false)
    {
        ValidateTime(frame);
        if (!models.TryGetValue(modelIndex, out var model))
            throw new KeyNotFoundException($"PMM physics model #{modelIndex} does not exist.");
        if (saved) return model.Saved;
        var disabled = new List<int>();
        for (int bone = 0; bone < model.Bones.Length; bone++)
        {
            var keys = model.Bones[bone];
            int lo = 0, hi = keys.Length;
            while (lo < hi)
            {
                int middle = lo + (hi - lo) / 2;
                if (keys[middle].Frame <= frame) lo = middle + 1; else hi = middle;
            }
            if (keys[Math.Max(0, lo - 1)].Disabled) disabled.Add(bone);
        }
        return disabled.AsReadOnly();
    }

    private static PmmGravityPose Pose(float acceleration, Vector3 direction, bool noiseEnabled, int noise)
    {
        try { return new(acceleration, direction, noiseEnabled, noise, PmmPhysicsSettings.ToGravity(acceleration, direction)); }
        catch (ArgumentOutOfRangeException error)
        { throw new InvalidDataException("PMM gravity requires finite representable world acceleration and direction.", error); }
    }

    private static void ValidateTime(double frame)
    {
        if (!double.IsFinite(frame) || frame < 0) throw new ArgumentOutOfRangeException(nameof(frame));
    }
}
