using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

/// <summary>A static cross-model attachment. Offset is applied before the target bone's scene-world transform.</summary>
public sealed record SceneOutsideParentBinding(Guid ObjectId, int BoneIndex, Guid ParentObjectId, int ParentBoneIndex, Matrix4x4 Offset);

public sealed partial class SceneAnimationSession
{
    private SceneOutsideParentBinding[] outsideBindings = [];
    private readonly Stack<SceneOutsideParentBinding[]> outsideUndo = new(), outsideRedo = new();
    public IReadOnlyList<SceneOutsideParentBinding> OutsideParents => Array.AsReadOnly(outsideBindings);
    public bool CanUndoOutsideParent => outsideUndo.Count != 0;
    public bool CanRedoOutsideParent => outsideRedo.Count != 0;

    /// <summary>Stable immutable snapshot of the latest bone transforms in composed scene space.</summary>
    public IReadOnlyList<Matrix4x4> GetBoneWorldMatrices(Guid objectId)
    {
        RequireHealthyWithoutPreviewCompletion(); int index = Index(objectId);
        _ = entries[index].Controller ?? throw new InvalidOperationException("Bind a motion before querying bone transforms.");
        CompletePreviewLeaves(); var entry = entries[index]; var controller = entry.Controller!;
        return Array.AsReadOnly(controller.BoneWorldMatrices.Select(world => AffineProduct(world * entry.Object.World)).ToArray());
    }

    /// <summary>Calculates an attachment from the latest live solved worlds without replacing,
    /// seeking or stepping this session, allocating a physical world, or changing its Undo history.
    /// A private animation-only probe supplies the subject's relative authored transform; it does
    /// not replace the live source/target worlds used for keep-world compensation.</summary>
    internal SceneOutsideParentBinding PrepareOutsideParentBinding(Guid objectId, int boneIndex, Guid parentObjectId, int parentBoneIndex,
        bool keepWorld = true, Matrix4x4? offset = null, CancellationToken token = default)
    {
        RequireHealthyWithoutPreviewCompletion(); token.ThrowIfCancellationRequested();
        if (keepWorld && offset.HasValue) throw new ArgumentException("Choose either a specified offset or keep-world compensation.", nameof(offset));
        var binding = new SceneOutsideParentBinding(objectId, boneIndex, parentObjectId, parentBoneIndex, offset ?? Matrix4x4.Identity);
        var next = outsideBindings.Where(item => item.ObjectId != objectId || item.BoneIndex != boneIndex).Append(binding).ToArray();
        _ = ValidateOutsideParents(next, entries);
        CompletePreviewLeaves(token);
        if (keepWorld)
        {
            var entry = entries[Index(objectId)];
            Matrix4x4 target = GetBoneWorldMatrices(parentObjectId)[parentBoneIndex];
            Matrix4x4 source, relative;
            if (boneIndex < 0)
            {
                var oldWhole = outsideBindings.SingleOrDefault(item => item.ObjectId == objectId && item.BoneIndex == -1);
                source = oldWhole is null ? entry.Object.World
                    : AffineProduct(oldWhole.Offset * GetBoneWorldMatrices(oldWhole.ParentObjectId)[oldWhole.ParentBoneIndex]);
                relative = Matrix4x4.Identity;
            }
            else
            {
                source = GetBoneWorldMatrices(objectId)[boneIndex];
                var motion = entry.Motion!;
                using var probe = new CharacterAnimationController(motion.Model, motion.Motion, entry.Object.Source.Surface,
                    entry.Object.Source.Outline!, allowEmptyMotion: true);
                var parents = EffectiveOutsideParents(objectId, entries, outsideBindings);
                parents[boneIndex] = Matrix4x4.Identity;
                probe.SetOutsideParents(parents);
                probe.SampleAt(Math.Min(CurrentFrame, probe.DurationFrames), motion.IkEnabled, motion.MorphsEnabled);
                relative = probe.BoneWorldMatrices[boneIndex];
            }
            if (!Matrix4x4.Invert(relative, out var inverseRelative) || !Matrix4x4.Invert(target, out var inverseTarget))
                throw new ArgumentException("Keep-world attachment requires invertible bone transforms.");
            binding = binding with { Offset = AffineProduct(inverseRelative * source * inverseTarget) };
        }
        token.ThrowIfCancellationRequested();
        return binding;
    }

    /// <summary>Atomically changes an attachment and restarts independent physics at the current pose.</summary>
    public void SetOutsideParent(Guid objectId, int boneIndex, Guid parentObjectId, int parentBoneIndex,
        bool keepWorld = true, Matrix4x4? offset = null, CancellationToken token = default)
    {
        var binding = PrepareOutsideParentBinding(objectId, boneIndex, parentObjectId, parentBoneIndex, keepWorld, offset, token);
        var next = outsideBindings.Where(item => item.ObjectId != objectId || item.BoneIndex != boneIndex).Append(binding).ToArray();
        var preservedWorlds = keepWorld ? GetBoneWorldMatrices(objectId) : null;
        if (next.SequenceEqual(outsideBindings)) return;
        var previous = outsideBindings;
        ReplaceGraph(next, entries.Where(entry => entry.Motion is not null).Select(entry => entry.Motion!).ToArray(), token,
            preservedWorlds is null ? null : candidates =>
            {
                var candidate = candidates.Single(item => item.Object.Id == objectId);
                var worlds = candidate.Controller!.BoneWorldMatrices;
                int first = boneIndex < 0 ? 0 : boneIndex, last = boneIndex < 0 ? worlds.Count : boneIndex + 1;
                for (int i = first; i < last; i++)
                    if (!SameWorld(preservedWorlds[i], AffineProduct(worlds[i] * candidate.Object.World)))
                        throw new InvalidOperationException($"Cannot preserve keep-world pose of bone {i}: IK or restarted physics changed the resulting transform. Use an explicit offset or disable physics before binding.");
            });
        outsideUndo.Push(previous); outsideRedo.Clear();
    }

    public void RemoveOutsideParent(Guid objectId, int boneIndex, CancellationToken token = default)
    {
        RequireHealthyWithoutPreviewCompletion(); token.ThrowIfCancellationRequested(); _ = Index(objectId);
        var next = outsideBindings.Where(item => item.ObjectId != objectId || item.BoneIndex != boneIndex).ToArray();
        if (next.Length == outsideBindings.Length) return;
        CompletePreviewLeaves(token);
        var previous = outsideBindings;
        ReplaceGraph(next, entries.Where(entry => entry.Motion is not null).Select(entry => entry.Motion!).ToArray(), token);
        outsideUndo.Push(previous); outsideRedo.Clear();
    }

    public bool UndoOutsideParent(CancellationToken token = default)
    {
        RequireHealthyWithoutPreviewCompletion(); token.ThrowIfCancellationRequested(); if (outsideUndo.Count == 0) return false;
        CompletePreviewLeaves(token);
        var next = outsideUndo.Peek(); var previous = outsideBindings;
        ReplaceGraph(next, entries.Where(entry => entry.Motion is not null).Select(entry => entry.Motion!).ToArray(), token);
        outsideUndo.Pop(); outsideRedo.Push(previous); return true;
    }

    public bool RedoOutsideParent(CancellationToken token = default)
    {
        RequireHealthyWithoutPreviewCompletion(); token.ThrowIfCancellationRequested(); if (outsideRedo.Count == 0) return false;
        CompletePreviewLeaves(token);
        var next = outsideRedo.Peek(); var previous = outsideBindings;
        ReplaceGraph(next, entries.Where(entry => entry.Motion is not null).Select(entry => entry.Motion!).ToArray(), token);
        outsideRedo.Pop(); outsideUndo.Push(previous); return true;
    }

    private void ReplaceGraph(SceneOutsideParentBinding[] bindings, SceneActorMotion[] motions, CancellationToken token,
        Action<Entry[]>? validateCandidate = null)
    {
        RequireProjectControlEdit();
        var byId = motions.ToDictionary(item => item.ObjectId);
        var next = entries.Select(entry => new Entry(entry.Object, byId.GetValueOrDefault(entry.Object.Id), null, null, entry.Object.Source)).ToArray();
        int[] order = ValidateOutsideParents(bindings, next);
        SharedCharacterPhysicsSession? candidateShared = null;
        var previousStatus = sharedStatus; double previousAccumulator = sharedAccumulator, previousSampleFrame = sharedSampleFrame;
        try
        {
            foreach (int index in order)
            {
                token.ThrowIfCancellationRequested(); var entry = next[index];
                if (entry.Motion is null) continue;
                next[index] = PrepareEntry(entry.Object, entry.Motion, CurrentFrame, token,
                    EffectiveOutsideParents(entry.Object.Id, next, bindings), next);
            }
            if (sharedPhysicsFactory is not null) candidateShared = CreateSharedWorld(next, CurrentFrame);
            validateCandidate?.Invoke(next);
            var frame = Compose(next, null); var nextStates = CaptureStates(next, CurrentFrame);
            token.ThrowIfCancellationRequested();
            var old = entries; entries = next; outsideBindings = bindings.ToArray();
            var oldShared = sharedWorld;
            if (candidateShared is not null) { sharedWorld = candidateShared; candidateShared = null; }
            published = frame; states = nextStates; DurationFrames = Duration(next);
            foreach (var entry in old) Release(entry);
            try { oldShared?.Dispose(); }
            catch (Exception exception) { cleanupDiagnostics.Add($"Old shared-world cleanup failed: {exception.Message}"); }
        }
        catch
        {
            candidateShared?.Dispose(); sharedStatus = previousStatus; sharedAccumulator = previousAccumulator; sharedSampleFrame = previousSampleFrame;
            foreach (var entry in next) Release(entry);
            throw;
        }
    }

    private static int[] ValidateOutsideParents(IReadOnlyList<SceneOutsideParentBinding> bindings, Entry[] input)
    {
        if (bindings.Count > 100_000) throw new ArgumentException("Too many outside-parent bindings.", nameof(bindings));
        var ids = input.Select((entry, index) => (entry.Object.Id, index)).ToDictionary(item => item.Id, item => item.index);
        var slots = new HashSet<(Guid, int)>();
        foreach (var binding in bindings)
        {
            if (binding is null || !ids.TryGetValue(binding.ObjectId, out int child) || !ids.TryGetValue(binding.ParentObjectId, out int parent)
                || child == parent || !slots.Add((binding.ObjectId, binding.BoneIndex)))
                throw new ArgumentException("Outside-parent slots require distinct known models and unique subjects.", nameof(bindings));
            var childModel = input[child].Motion?.Model; var parentModel = input[parent].Motion?.Model;
            if (childModel is null || parentModel is null || binding.BoneIndex < -1 || binding.BoneIndex >= childModel.Bones.Count
                || binding.ParentBoneIndex < 0 || binding.ParentBoneIndex >= parentModel.Bones.Count)
                throw new ArgumentException("Outside parents require bound models and valid bone indices.", nameof(bindings));
            ValidateAffine(binding.Offset);
        }
        var dependencies = bindings.GroupBy(item => ids[item.ObjectId]).ToDictionary(group => group.Key,
            group => group.Select(item => ids[item.ParentObjectId]).Distinct().ToArray());
        var done = new HashSet<int>(); var visiting = new HashSet<int>(); var order = new List<int>();
        void Visit(int index)
        {
            if (done.Contains(index)) return;
            if (!visiting.Add(index)) throw new ArgumentException("Outside-parent model dependency cycle.", nameof(bindings));
            if (dependencies.TryGetValue(index, out var parents)) foreach (int parent in parents) Visit(parent);
            visiting.Remove(index); done.Add(index); order.Add(index);
        }
        for (int i = 0; i < input.Length; i++) Visit(i);
        return order.ToArray();
    }

    private static Dictionary<int, Matrix4x4> EffectiveOutsideParents(Guid id, Entry[] input, IReadOnlyList<SceneOutsideParentBinding> bindings)
    {
        var entry = input.Single(item => item.Object.Id == id);
        if (!Matrix4x4.Invert(entry.Object.World, out var inverseWorld)) throw new ArgumentException("Model placement must be invertible.");
        var result = new Dictionary<int, Matrix4x4>();
        foreach (var binding in bindings.Where(item => item.ObjectId == id))
        {
            var target = input.Single(item => item.Object.Id == binding.ParentObjectId);
            var worlds = target.Controller?.BoneWorldMatrices ?? throw new InvalidOperationException("Parent must be sampled before its dependents.");
            result.Add(binding.BoneIndex, AffineProduct(binding.Offset * worlds[binding.ParentBoneIndex] * target.Object.World * inverseWorld));
        }
        return result;
    }

    private static void ValidateAffine(Matrix4x4 value)
    {
        if (!float.IsFinite(value.M11) || !float.IsFinite(value.M12) || !float.IsFinite(value.M13)
            || !float.IsFinite(value.M21) || !float.IsFinite(value.M22) || !float.IsFinite(value.M23)
            || !float.IsFinite(value.M31) || !float.IsFinite(value.M32) || !float.IsFinite(value.M33)
            || !float.IsFinite(value.M41) || !float.IsFinite(value.M42) || !float.IsFinite(value.M43)
            || value.M14 != 0 || value.M24 != 0 || value.M34 != 0 || value.M44 != 1 || !Matrix4x4.Invert(value, out _))
            throw new ArgumentException("Outside-parent offsets must be finite invertible affine matrices.");
    }

    private static bool SameWorld(Matrix4x4 a, Matrix4x4 b)
    {
        const float tolerance = 2e-4f;
        return MathF.Abs(a.M11-b.M11)<=tolerance && MathF.Abs(a.M12-b.M12)<=tolerance && MathF.Abs(a.M13-b.M13)<=tolerance
            && MathF.Abs(a.M21-b.M21)<=tolerance && MathF.Abs(a.M22-b.M22)<=tolerance && MathF.Abs(a.M23-b.M23)<=tolerance
            && MathF.Abs(a.M31-b.M31)<=tolerance && MathF.Abs(a.M32-b.M32)<=tolerance && MathF.Abs(a.M33-b.M33)<=tolerance
            && MathF.Abs(a.M41-b.M41)<=tolerance && MathF.Abs(a.M42-b.M42)<=tolerance && MathF.Abs(a.M43-b.M43)<=tolerance;
    }

    private static Matrix4x4 AffineProduct(Matrix4x4 value)
    {
        // Products/inverses of validated affine inputs remain affine despite float last-column drift.
        value.M14 = value.M24 = value.M34 = 0; value.M44 = 1;
        ValidateAffine(value); return value;
    }
}
