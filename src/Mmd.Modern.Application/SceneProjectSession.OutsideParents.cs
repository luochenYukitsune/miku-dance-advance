using System.Numerics;

namespace Mmd.Modern.Application;

public sealed partial class SceneProjectSession
{
    /// <summary>Calculates an attachment from this project's current live solved worlds. Its
    /// animation/physical state and Undo history remain untouched until the host commits the result.
    /// The compensation is a detached candidate, not permission to bypass normal edit validation.</summary>
    public SceneOutsideParentBinding PrepareOutsideParent(Guid objectId, int boneIndex,
        Guid parentObjectId, int parentBoneIndex, bool keepWorld = true, CancellationToken cancellationToken = default)
    {
        RequireIdle();
        return animation.PrepareOutsideParentBinding(objectId, boneIndex, parentObjectId, parentBoneIndex,
            keepWorld: keepWorld, token: cancellationToken);
    }

    /// <summary>Commits one ordinary project transaction only if its candidate preserves the live
    /// solved world transforms of every requested channel. BoneIndex -1 checks the entire skeleton.
    /// Physics/IK restart mismatches reject before publication, without changing Undo or Redo.</summary>
    public void EditWithWorldPreservation(Action<SceneProjectEdit> edit,
        IReadOnlyList<(Guid ObjectId, int BoneIndex)> channels, CancellationToken cancellationToken = default)
    {
        RequireIdle(); ArgumentNullException.ThrowIfNull(edit); ArgumentNullException.ThrowIfNull(channels);
        cancellationToken.ThrowIfCancellationRequested();
        var preserved = new List<(Guid ObjectId, int BoneIndex, Matrix4x4 World)>();
        var liveWorlds = new Dictionary<Guid, IReadOnlyList<Matrix4x4>>();
        foreach (var (objectId, boneIndex) in channels.ToArray().Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!liveWorlds.TryGetValue(objectId, out var worlds))
                liveWorlds.Add(objectId, worlds = animation.GetBoneWorldMatrices(objectId));
            if (boneIndex < -1 || boneIndex >= worlds.Count)
                throw new ArgumentOutOfRangeException(nameof(channels), "Keep-world channels require valid bone indices or -1 for the entire skeleton.");
            if (boneIndex == -1)
                for (int index = 0; index < worlds.Count; index++) preserved.Add((objectId, index, worlds[index]));
            else preserved.Add((objectId, boneIndex, worlds[boneIndex]));
        }

        EditChecked(edit, candidate =>
        {
            var candidateWorlds = new Dictionary<Guid, IReadOnlyList<Matrix4x4>>();
            foreach (var (objectId, boneIndex, world) in preserved)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!candidateWorlds.TryGetValue(objectId, out var worlds))
                    candidateWorlds.Add(objectId, worlds = candidate.GetBoneWorldMatrices(objectId));
                if (boneIndex >= worlds.Count || !SamePreservedWorld(world, worlds[boneIndex]))
                    throw new InvalidOperationException($"Cannot preserve keep-world pose of object {objectId}, bone {boneIndex}: IK or restarted physics changed the resulting transform. Use an explicit offset or disable physics before binding.");
            }
        }, cancellationToken);
    }

    private static bool SamePreservedWorld(Matrix4x4 a, Matrix4x4 b)
    {
        const float tolerance = 2e-4f;
        return MathF.Abs(a.M11 - b.M11) <= tolerance && MathF.Abs(a.M12 - b.M12) <= tolerance && MathF.Abs(a.M13 - b.M13) <= tolerance
            && MathF.Abs(a.M21 - b.M21) <= tolerance && MathF.Abs(a.M22 - b.M22) <= tolerance && MathF.Abs(a.M23 - b.M23) <= tolerance
            && MathF.Abs(a.M31 - b.M31) <= tolerance && MathF.Abs(a.M32 - b.M32) <= tolerance && MathF.Abs(a.M33 - b.M33) <= tolerance
            && MathF.Abs(a.M41 - b.M41) <= tolerance && MathF.Abs(a.M42 - b.M42) <= tolerance && MathF.Abs(a.M43 - b.M43) <= tolerance;
    }
}
