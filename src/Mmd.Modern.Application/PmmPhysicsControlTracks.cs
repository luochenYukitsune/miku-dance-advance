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
    private static readonly IReadOnlyList<int> EmptyBoneIndices = Array.AsReadOnly(Array.Empty<int>());
    private readonly record struct GravityKey(int Frame, PmmGravityPose Pose);
    private readonly record struct BoneKey(int Frame, bool Disabled);
    private readonly record struct Transition(int Frame, int ActiveSlot, bool Disabled);

    private sealed class ModelTrack
    {
        public readonly IReadOnlyList<int> Saved;
        public readonly int[] ActiveBoneIndices;
        private readonly BoneKey[][] activeBoneKeys;
        public readonly int[] IntervalStarts;
        private readonly IReadOnlyList<int>?[] intervals;
        private readonly bool eager;

        public ModelTrack(
            IReadOnlyList<int> saved,
            int[] activeBoneIndices,
            BoneKey[][] activeBoneKeys,
            int[] intervalStarts,
            IReadOnlyList<int>?[] intervals,
            bool eager)
        {
            Saved = saved;
            ActiveBoneIndices = activeBoneIndices;
            this.activeBoneKeys = activeBoneKeys;
            IntervalStarts = intervalStarts;
            this.intervals = intervals;
            this.eager = eager;
        }

        public IReadOnlyList<int> At(double frame)
        {
            if (IntervalStarts.Length == 1) return intervals[0]!;
            int idx = FindIntervalIndex(IntervalStarts, frame);
            var cached = eager ? intervals[idx] : Volatile.Read(ref intervals[idx]);
            if (cached is not null) return cached;
            var computed = ComputeIntervalAt(IntervalStarts[idx]);
            return Interlocked.CompareExchange(ref intervals[idx], computed, null) ?? computed;
        }

        private IReadOnlyList<int> ComputeIntervalAt(int intervalFrame)
        {
            List<int>? disabled = null;
            for (int s = 0; s < ActiveBoneIndices.Length; s++)
            {
                var keys = activeBoneKeys[s];
                int lo = 0, hi = keys.Length;
                while (lo < hi)
                {
                    int middle = lo + (hi - lo) / 2;
                    if (keys[middle].Frame <= intervalFrame) lo = middle + 1; else hi = middle;
                }
                if (keys[Math.Max(0, lo - 1)].Disabled) (disabled ??= []).Add(ActiveBoneIndices[s]);
            }
            return disabled is null ? EmptyBoneIndices : Array.AsReadOnly(disabled.ToArray());
        }
    }

    private readonly GravityKey[] gravity;
    private readonly bool hasConstantGravity;
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
        hasConstantGravity = AllGravityBitEqual(gravity);
        int duration = gravity[^1].Frame;
        foreach (var model in document.Models)
        {
            if (model is null || models.ContainsKey(model.Index))
                throw new InvalidDataException("PMM physics controls require unique non-null model records.");
            if (model.BoneNames is null || model.BoneStates is null || model.InitialBoneKeyframes is null || model.BoneKeyframes is null
                || model.BoneStates.Count != model.BoneNames.Count || model.BoneStates.Any(state => state is null)
                || model.InitialBoneKeyframes.Concat(model.BoneKeyframes).Any(key => key is null))
                throw new InvalidDataException("PMM physics saved state and key counts must match the bone name table.");
            int boneCount = model.BoneNames.Count;
            var resolved = PmmMotionAdapter.ResolveTracks(model.InitialBoneKeyframes, model.BoneKeyframes, boneCount, "physics bone");
            var counts = new int[boneCount];
            for (int i = 0; i < resolved.Count; i++) counts[resolved[i].Track]++;
            var singleKeys = new BoneKey[boneCount];
            var multiKeys = new List<(int Frame, bool Disabled, int Order)>?[boneCount];
            for (int i = 0; i < resolved.Count; i++)
            {
                var (key, track) = resolved[i];
                if (counts[track] == 1)
                    singleKeys[track] = new(key.Frame, key.PhysicsDisabled);
                else
                    (multiKeys[track] ??= new List<(int, bool, int)>(counts[track])).Add((key.Frame, key.PhysicsDisabled, i));
            }
            var activeBones = new List<int>();
            var activeKeys = new List<BoneKey[]>();
            var initialDisabled = new List<bool>();
            var transitions = new List<Transition>();
            for (int bone = 0; bone < boneCount; bone++)
            {
                if (counts[bone] == 1)
                {
                    var single = singleKeys[bone];
                    duration = Math.Max(duration, single.Frame);
                    if (!single.Disabled) continue;
                    activeBones.Add(bone);
                    activeKeys.Add([single]);
                    initialDisabled.Add(true);
                    continue;
                }
                var items = multiKeys[bone]!;
                items.Sort(static (x, y) => x.Frame != y.Frame ? x.Frame.CompareTo(y.Frame) : x.Order.CompareTo(y.Order));
                int uniqueCount = 0;
                bool anyDisabled = false;
                for (int k = 0; k < items.Count; k++)
                {
                    if (k + 1 < items.Count && items[k + 1].Frame == items[k].Frame) continue;
                    if (items[k].Disabled) anyDisabled = true;
                    items[uniqueCount++] = items[k];
                }
                duration = Math.Max(duration, items[uniqueCount - 1].Frame);
                if (!anyDisabled) continue;
                int activeSlot = activeBones.Count;
                activeBones.Add(bone);
                var keys = new BoneKey[uniqueCount];
                for (int k = 0; k < uniqueCount; k++) keys[k] = new(items[k].Frame, items[k].Disabled);
                activeKeys.Add(keys);
                bool current = keys[0].Disabled;
                initialDisabled.Add(current);
                for (int k = 1; k < keys.Length; k++)
                {
                    if (keys[k].Disabled == current) continue;
                    current = keys[k].Disabled;
                    transitions.Add(new(keys[k].Frame, activeSlot, current));
                }
            }
            int[] saved = model.BoneStates.Select((state, index) => (state, index)).Where(pair => pair.state.PhysicsDisabled)
                .Select(pair => pair.index).ToArray();
            var savedList = saved.Length == 0 ? EmptyBoneIndices : Array.AsReadOnly(saved);
            models.Add(model.Index, BuildModelTrack(savedList, activeBones, activeKeys, initialDisabled, transitions));
        }
        DurationFrames = (uint)duration;
        var diagnostics = new List<string>();
        if (SavedGravity.NoiseEnabled || gravityKeys.Any(key => key.Pose.NoiseEnabled))
            diagnostics.Add("PMM gravity noise requires the stateful PmmGravityNoise runtime; a sampled WorldGravity value alone does not execute noise.");
        Diagnostics = diagnostics.AsReadOnly();
    }

    private static ModelTrack BuildModelTrack(
        IReadOnlyList<int> savedList,
        List<int> activeBones,
        List<BoneKey[]> activeKeys,
        List<bool> initialDisabled,
        List<Transition> transitions)
    {
        int[] activeBoneArray = activeBones.Count == 0 ? [] : activeBones.ToArray();
        if (transitions.Count == 0)
        {
            int count = 0;
            for (int s = 0; s < initialDisabled.Count; s++)
                if (initialDisabled[s]) count++;
            IReadOnlyList<int> initial;
            if (count == 0)
                initial = EmptyBoneIndices;
            else
            {
                var arr = new int[count];
                int pos = 0;
                for (int s = 0; s < initialDisabled.Count; s++)
                    if (initialDisabled[s]) arr[pos++] = activeBoneArray[s];
                initial = Array.AsReadOnly(arr);
            }
            return new(savedList, activeBoneArray, [], [0], [initial], eager: true);
        }

        transitions.Sort(static (x, y) => x.Frame != y.Frame ? x.Frame.CompareTo(y.Frame) : x.ActiveSlot.CompareTo(y.ActiveSlot));
        int distinctTransitions = 1;
        for (int i = 1; i < transitions.Count; i++)
            if (transitions[i].Frame != transitions[i - 1].Frame) distinctTransitions++;
        var intervalStarts = new int[1 + distinctTransitions];
        intervalStarts[0] = 0;
        int write = 1;
        intervalStarts[write++] = transitions[0].Frame;
        for (int i = 1; i < transitions.Count; i++)
            if (transitions[i].Frame != transitions[i - 1].Frame) intervalStarts[write++] = transitions[i].Frame;

        const long MaxEagerMatrixEntries = 250_000;
        long matrixSize = (long)intervalStarts.Length * activeBoneArray.Length;
        if (matrixSize > MaxEagerMatrixEntries)
            return new(savedList, activeBoneArray, activeKeys.ToArray(), intervalStarts, new IReadOnlyList<int>?[intervalStarts.Length], eager: false);

        var intervals = new IReadOnlyList<int>?[intervalStarts.Length];
        var state = new bool[activeBoneArray.Length];
        int disabledCount = 0;
        for (int s = 0; s < state.Length; s++)
        {
            if (initialDisabled[s]) { state[s] = true; disabledCount++; }
        }
        IReadOnlyList<int> Snapshot()
        {
            if (disabledCount == 0) return EmptyBoneIndices;
            var arr = new int[disabledCount];
            int pos = 0;
            for (int s = 0; s < state.Length; s++)
                if (state[s]) arr[pos++] = activeBoneArray[s];
            return Array.AsReadOnly(arr);
        }
        intervals[0] = Snapshot();
        int tIdx = 0;
        for (int iv = 1; iv < intervalStarts.Length; iv++)
        {
            int frame = intervalStarts[iv];
            while (tIdx < transitions.Count && transitions[tIdx].Frame == frame)
            {
                var tr = transitions[tIdx++];
                if (state[tr.ActiveSlot] != tr.Disabled)
                {
                    state[tr.ActiveSlot] = tr.Disabled;
                    disabledCount += tr.Disabled ? 1 : -1;
                }
            }
            intervals[iv] = Snapshot();
        }
        return new(savedList, activeBoneArray, [], intervalStarts, intervals, eager: true);
    }

    internal ReadOnlySpan<int> GetIntervalStarts(int modelIndex) => models[modelIndex].IntervalStarts;

    internal IReadOnlyList<int> GetActiveBoneIndices(int modelIndex) => models[modelIndex].ActiveBoneIndices;

    internal bool HasConstantGravity => hasConstantGravity;

    internal bool TryGetConstantGravitySegment(double frame, out int segmentIndex, out PmmGravityPose pose)
    {
        if (hasConstantGravity)
        {
            segmentIndex = 0;
            pose = gravity[0].Pose;
            return true;
        }
        if (frame >= gravity[^1].Frame)
        {
            segmentIndex = gravity.Length;
            pose = gravity[^1].Pose;
            return true;
        }
        if (frame <= gravity[0].Frame)
        {
            segmentIndex = 0;
            pose = gravity[0].Pose;
            return true;
        }
        int index = FindGravityIndex(frame);
        var left = gravity[index];
        var right = gravity[index + 1];
        if (BitEqual(left.Pose, right.Pose))
        {
            segmentIndex = index + 1;
            pose = left.Pose;
            return true;
        }
        segmentIndex = -1;
        pose = default;
        return false;
    }

    public PmmGravityPose GravityAt(double frame)
    {
        ValidateTime(frame);
        if (hasConstantGravity) return gravity[0].Pose;
        int index = FindGravityIndex(frame);
        var left = gravity[index];
        if (frame <= left.Frame || index == gravity.Length - 1) return left.Pose;
        var right = gravity[index + 1];
        if (BitEqual(left.Pose, right.Pose)) return left.Pose;
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
        return model.At(frame);
    }

    private int FindGravityIndex(double frame)
    {
        int lo = 0, hi = gravity.Length;
        while (lo < hi)
        {
            int middle = lo + (hi - lo) / 2;
            if (gravity[middle].Frame <= frame) lo = middle + 1; else hi = middle;
        }
        return Math.Max(0, lo - 1);
    }

    private static int FindIntervalIndex(int[] starts, double frame)
    {
        int lo = 0, hi = starts.Length;
        while (lo < hi)
        {
            int middle = lo + (hi - lo) / 2;
            if (starts[middle] <= frame) lo = middle + 1; else hi = middle;
        }
        return Math.Max(0, lo - 1);
    }

    private static bool AllGravityBitEqual(GravityKey[] keys)
    {
        if (keys.Length <= 1) return true;
        var first = keys[0].Pose;
        for (int i = 1; i < keys.Length; i++)
            if (!BitEqual(first, keys[i].Pose)) return false;
        return true;
    }

    private static bool BitEqual(PmmGravityPose a, PmmGravityPose b)
        => BitConverter.SingleToInt32Bits(a.Acceleration) == BitConverter.SingleToInt32Bits(b.Acceleration)
        && BitConverter.SingleToInt32Bits(a.Direction.X) == BitConverter.SingleToInt32Bits(b.Direction.X)
        && BitConverter.SingleToInt32Bits(a.Direction.Y) == BitConverter.SingleToInt32Bits(b.Direction.Y)
        && BitConverter.SingleToInt32Bits(a.Direction.Z) == BitConverter.SingleToInt32Bits(b.Direction.Z)
        && a.NoiseEnabled == b.NoiseEnabled && a.Noise == b.Noise;

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
