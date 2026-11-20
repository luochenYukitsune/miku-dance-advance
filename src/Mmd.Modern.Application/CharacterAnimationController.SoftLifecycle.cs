using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public sealed partial class CharacterAnimationController
{
    // Immutable frozen display baseline. A capable coupled provider also projects its live collider;
    // the solver keeps its original saved nodes, velocities and remainder.
    // Advance remains forbidden until an explicit seek/reset restarts it at a new sampled pose.
    private SceneVertex[]? frozenSoftVertices;
    private int[]? softVertexIndices;
    private int[] SoftVertexIndices => softVertexIndices ??= PmxSoftBodyAdapter.Convert(model).Definitions
        .SelectMany(definition => definition.VertexIndices).Distinct().ToArray();

    private Matrix4x4 AttachmentFrame(int bone, IReadOnlyDictionary<int, Matrix4x4>? bindings)
    {
        if (bindings is null) return Matrix4x4.Identity;
        for (int subject = bone; subject >= 0; subject = model.Bones[subject].ParentIndex)
            if (bindings.TryGetValue(subject, out var frame)) return frame;
        return bindings.TryGetValue(-1, out var whole) ? whole : Matrix4x4.Identity;
    }

    private (OriginalStyleScene Surface, OriginalOutlineData Outline) RefreshFrozenPose(Dictionary<int, Matrix4x4>? next)
    {
        var anchorWorlds = frozenBoneWorlds ?? BoneWorldMatrices.ToArray();
        // Null is a meaningful baseline: PMM may attach a previously unattached stopped actor.
        var anchorParents = frozenBoneWorlds is not null ? frozenOutsideParents : outsideParents;
        var worlds = FrozenWorlds(next);
        var coupledDelta = CoupledFrozenDelta(worlds);
        var prepared = PrepareMorphs(DurationFrames, sampledMorphs);
        var pose = animator.SkinWorldPose(worlds, Evaluation(DurationFrames, sampledIk, sampledMorphs).Morphs);
        var baseline = SoftBodiesEnabled ? frozenSoftVertices ?? sampled.Surface.Snapshot.Vertices.ToArray() : null;
        var vertices = baseline is not null
            ? ReprojectFrozenSoftVertices(anchorWorlds, pose, baseline)
            : pose.Deformation is not null && !SoftBodiesEnabled && CoupledSoftBodies is null
                ? null
                : pose.Vertices;
        var collisionSurface = CoupledSoftBodies is { SupportsFrozenCollisionSurface: true } soft
            ? soft.PrepareFrozenSurface(vertices!) : ((SceneVertex[] Vertices, Vector3[] Targets)?)null;
        // Scene construction and all node maps must succeed before changing the borrowed solver history.
        var scene = CreateScene(pose, prepared, vertices);
        if (collisionSurface is { } surface) CoupledSoftBodies!.PublishFrozenSurface(surface);
        else if (coupledDelta is { } delta && delta != Matrix4x4.Identity) CoupledSoftBodies!.Rebase(delta);
        var result = StorePreparedScene(pose, DurationFrames, sampledIk, sampledMorphs, prepared, scene);
        outsideParents = next; frozenBoneWorlds = anchorWorlds; frozenOutsideParents = anchorParents; frozenSoftVertices = baseline;
        return result;
    }

    private Matrix4x4[] FrozenWorlds(Dictionary<int, Matrix4x4>? next)
    {
        var anchorWorlds = frozenBoneWorlds ?? BoneWorldMatrices.ToArray();
        var anchorParents = frozenBoneWorlds is not null ? frozenOutsideParents : outsideParents;
        var worlds = new Matrix4x4[anchorWorlds.Length];
        for (int bone = 0; bone < worlds.Length; bone++)
        {
            var oldParent = AttachmentFrame(bone, anchorParents); var newParent = AttachmentFrame(bone, next);
            if (oldParent == newParent) { worlds[bone] = anchorWorlds[bone]; continue; }
            // Preserve exact cancellation where the completed local bone frame is identity.
            if (anchorWorlds[bone] == oldParent) { worlds[bone] = newParent; continue; }
            if (!Matrix4x4.Invert(oldParent, out var inverse))
                throw new NotSupportedException("Frozen outside-parent baseline is not invertible.");
            worlds[bone] = anchorWorlds[bone] * inverse * newParent;
            worlds[bone].M14 = worlds[bone].M24 = worlds[bone].M34 = 0; worlds[bone].M44 = 1;
        }
        return worlds;
    }

    private SceneVertex[] ReprojectFrozenSoftVertices(Matrix4x4[] anchorWorlds, CharacterSample pose, SceneVertex[] baseline)
    {
        var oldMaps = animator.CalculateVertexSkinTransforms(anchorWorlds);
        var newMaps = animator.CalculateVertexSkinTransforms(pose.BoneWorldMatrices);
        var vertices = pose.Vertices.ToArray();
        foreach (int id in SoftVertexIndices)
        {
            if (!FiniteMap(oldMaps[id]) || !FiniteMap(newMaps[id])
                || !Matrix4x4.Invert(oldMaps[id], out var inverseOld) || !FiniteMap(inverseOld)
                || !Matrix4x4.Invert(newMaps[id], out _))
                throw new NotSupportedException($"Frozen soft vertex {id} has a singular or invalid skin map; seek before refreshing.");
            if (oldMaps[id] == newMaps[id])
            {
                vertices[id] = vertices[id] with { Position = baseline[id].Position, Normal = baseline[id].Normal };
                continue;
            }
            var delta = inverseOld * newMaps[id];
            if (!FiniteMap(delta) || !Matrix4x4.Invert(delta, out var inverseDelta) || !FiniteMap(inverseDelta))
                throw new NotSupportedException($"Frozen soft vertex {id} has an invalid deformation map; seek before refreshing.");
            var position = Vector3.Transform(baseline[id].Position, delta);
            var normal = Vector3.TransformNormal(baseline[id].Normal, Matrix4x4.Transpose(inverseDelta));
            if (!FiniteVector(position) || !FiniteVector(normal))
                throw new NotSupportedException($"Frozen soft vertex {id} overflows its projected coordinates.");
            float scale = MathF.Max(MathF.Abs(normal.X), MathF.Max(MathF.Abs(normal.Y), MathF.Abs(normal.Z)));
            normal = scale > 0 ? Vector3.Normalize(normal / scale) : Vector3.Zero;
            vertices[id] = vertices[id] with { Position = position, Normal = normal };
        }
        return vertices;
    }

    private SceneVertex[] RetainFrozenSoftVertices(CharacterSample pose)
    {
        var vertices = pose.Vertices.ToArray(); var previous = sampled.Surface.Snapshot.Vertices;
        foreach (int id in SoftVertexIndices)
            vertices[id] = vertices[id] with { Position = previous[id].Position, Normal = previous[id].Normal };
        return vertices;
    }

    private HashSet<int>? constraintSensitiveOutsideParentSubjects;
    private HashSet<int> ConstraintSensitiveOutsideParentSubjects
        => constraintSensitiveOutsideParentSubjects ??= BuildConstraintSensitiveOutsideParentSubjects();

    private HashSet<int> BuildConstraintSensitiveOutsideParentSubjects()
    {
        var sensitive = new HashSet<int>();
        void MarkSplitters(IEnumerable<int> nodes)
        {
            HashSet<int>? intersection = null;
            var union = new HashSet<int>();
            foreach (int node in nodes)
            {
                if ((uint)node >= (uint)model.Bones.Count) continue;
                var ancestors = new HashSet<int>();
                for (int cur = node; (uint)cur < (uint)model.Bones.Count && ancestors.Add(cur); cur = model.Bones[cur].ParentIndex) { }
                union.UnionWith(ancestors);
                if (intersection is null) intersection = new HashSet<int>(ancestors);
                else intersection.IntersectWith(ancestors);
            }
            if (intersection is not null) union.ExceptWith(intersection);
            sensitive.UnionWith(union);
        }
        for (int i = 0; i < model.Bones.Count; i++)
        {
            var bone = model.Bones[i];
            if (bone.Flags.HasFlag(PmxBoneFlags.Ik))
            {
                var group = new List<int>(bone.IkLinks.Count + 2) { i, bone.IkTargetBoneIndex };
                foreach (var link in bone.IkLinks) group.Add(link.BoneIndex);
                MarkSplitters(group);
            }
            if ((bone.Flags & (PmxBoneFlags.AppendRotate | PmxBoneFlags.AppendTranslate)) != 0)
                MarkSplitters([i, bone.AppendBoneIndex]);
        }
        return sensitive;
    }

    internal bool HasAnyOutsideParents => outsideParents is { Count: > 0 };

    internal bool CanDeferFrozenAttachmentBoundary(
        IReadOnlyList<SceneOutsideParentBinding> bindings,
        IReadOnlyList<ScenePmmGroundOutsideParent> groundParents,
        bool trackWindowChanged)
    {
        if (trackWindowChanged || SoftBodiesEnabled || model.SoftBodies.Count != 0
            || (sampledFrame != DurationFrames && sharedRigidBoneFrame != DurationFrames)
            || BoneWorldMatrices.Count != model.Bones.Count)
            return false;
        var current = outsideParents;
        int nonRootCount = 0;
        for (int i = 0; i < bindings.Count; i++)
        {
            int bone = bindings[i].BoneIndex;
            if (bone < 0) continue;
            if (current is null || !current.ContainsKey(bone) || ConstraintSensitiveOutsideParentSubjects.Contains(bone))
                return false;
            nonRootCount++;
        }
        for (int i = 0; i < groundParents.Count; i++)
        {
            int bone = groundParents[i].BoneIndex;
            if (bone < 0) continue;
            if (current is null || !current.ContainsKey(bone) || ConstraintSensitiveOutsideParentSubjects.Contains(bone))
                return false;
            for (int b = 0; b < bindings.Count; b++)
                if (bindings[b].BoneIndex == bone) return false;
            nonRootCount++;
        }
        int currentNonRootCount = 0;
        if (current is not null)
            foreach (int key in current.Keys)
                if (key >= 0) currentNonRootCount++;
        return nonRootCount == currentNonRootCount;
    }

    internal bool RequiresFullEvaluationForFrozenOutsideParents(IReadOnlyDictionary<int, Matrix4x4>? next, bool trackWindowChanged)
    {
        if (trackWindowChanged) return true;
        if (outsideParents is not null)
        {
            foreach (var (key, previous) in outsideParents)
            {
                if (key < 0) continue;
                if (next is null || !next.TryGetValue(key, out var value)) return true;
                if (value != previous && ConstraintSensitiveOutsideParentSubjects.Contains(key)) return true;
            }
        }
        if (next is not null)
        {
            foreach (int key in next.Keys)
                if (key >= 0 && (outsideParents is null || !outsideParents.ContainsKey(key))) return true;
        }
        return false;
    }

    private static bool FiniteVector(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool FiniteMap(Matrix4x4 value) => FiniteVector(new(value.M11, value.M12, value.M13))
        && FiniteVector(new(value.M21, value.M22, value.M23)) && FiniteVector(new(value.M31, value.M32, value.M33))
        && FiniteVector(value.Translation) && value.M14 == 0 && value.M24 == 0 && value.M34 == 0
        && float.IsFinite(value.M44) && MathF.Abs(value.M44 - 1) < 1e-4f;
}
