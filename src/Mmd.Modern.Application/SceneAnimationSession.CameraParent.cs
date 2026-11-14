using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

public sealed partial class SceneAnimationSession
{
    /// <summary>Current discrete target, mapped by native bone name rather than source skeleton position.</summary>
    public ScenePmmCameraParent? CameraParent
    {
        get { RequireAlive(); return pmmControls?.CameraParentAt(CurrentFrame); }
    }

    /// <summary>Latest solved bone transform including object placement. Does not seek, advance, or reset physics.</summary>
    public Matrix4x4? CameraParentTransform
    {
        get { RequireHealthy(); return CameraPose is { } pose ? ResolveCameraParentTransform(pose) : null; }
    }

    /// <summary>Resolves an explicit saved/draft parent against the current already solved actor poses.</summary>
    public Matrix4x4? ResolveCameraParentTransform(PmmCameraPose pose)
    {
        RequireHealthy();
        if (pose.ParentModelIndex < 0) return null;
        var parent = pmmControls?.ResolveCameraParent(pose)
            ?? throw new InvalidDataException("A parented camera requires explicit PMM actor and skeleton provenance.");
        var entry = entries.SingleOrDefault(e => e.Object.Id == parent.ObjectId)
            ?? throw new InvalidDataException($"PMM camera parent model #{parent.PmmModelIndex} is missing from the scene.");
        var worlds = entry.Controller?.BoneWorldMatrices;
        if (worlds is null || (uint)parent.BoneIndex >= (uint)worlds.Count)
            throw new InvalidDataException($"PMM camera parent model #{parent.PmmModelIndex} bone #{parent.PmmBoneIndex} has no solved pose.");
        return AffineProduct(worlds[parent.BoneIndex] * entry.Object.World);
    }

    /// <summary>Effective world camera; authored CameraPose remains parent-local for editing and persistence.</summary>
    public SceneCamera? Camera => CameraPose?.CreateCamera(CameraParentTransform);
}
