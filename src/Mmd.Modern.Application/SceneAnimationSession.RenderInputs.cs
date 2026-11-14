using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public sealed partial class SceneAnimationSession
{
    /// <summary>Captures only material metadata for this owner's accepted CurrentFrame. It does not
    /// complete preview leaves or publish a geometry frame; recipe-only preparation reuses the normal morph path.</summary>
    internal SceneObjectMaterialInputs CaptureMaterialInputs(Guid objectId, CancellationToken cancellationToken = default)
    {
        RequireHealthyWithoutPreviewCompletion(); cancellationToken.ThrowIfCancellationRequested();
        var entry = entries[Index(objectId)];
        SceneObjectMaterialInputs result;
        if (entry.Controller is not { } controller)
            result = new(objectId, CurrentFrame, 0, true, entry.Object.Source.Surface,
                entry.Object.Source.Outline, entry.Object.Source.Materials);
        else
        {
            var motion = entry.Motion ?? throw new InvalidOperationException("A material controller requires its owned motion.");
            double local = Math.Min(CurrentFrame, controller.DurationFrames);
            bool pending = previewLeaves.TryGetValue(objectId, out double pendingFrame);
            if (pending && pendingFrame != local)
                throw new InvalidOperationException("Material inputs do not match the accepted preview leaf clock.");
            var captured = controller.CaptureMaterialPreparation(local, motion.MorphsEnabled, pending);
            var prepared = captured.Prepared;
            result = new(objectId, CurrentFrame, local, captured.Visible, prepared.Surface, prepared.Outline,
                PmxOriginalFrameAdapter.FromEvaluatedMaterials(motion.Model, prepared.Surface, prepared.Outline));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Reads the current immutable local pose, retaining its World before scene assembly
    /// normalizes/bakes normals. Completes deferred visual leaves without advancing the physical clock.</summary>
    internal (SceneSnapshot Geometry, OriginalOutlineData? Outline, OriginalOutlineOptions? OutlineOptions) CaptureSourceFrame(Guid objectId)
    {
        RequireHealthyWithoutPreviewCompletion(); int index = Index(objectId);
        CompletePreviewLeaves(); var entry = entries[index];
        var source = entry.LocalFrame.Surface.Snapshot;
        return (entry.Object.World == Matrix4x4.Identity ? source : source.WithWorld(source.World * entry.Object.World),
            entry.LocalFrame.Outline, entry.LocalFrame.OutlineOptions);
    }

    /// <summary>Reads only the requested already solved matrices. Unlike the public diagnostic full-skeleton
    /// accessor, this does not enumerate/copy unrequested bones or allocate a controller.
    /// Any deferred visual leaves are completed before the requested transforms are copied.</summary>
    internal Dictionary<int, Matrix4x4> CaptureSelectedBoneWorldMatrices(Guid objectId, IReadOnlyList<int> boneIndices)
    {
        RequireHealthyWithoutPreviewCompletion(); int entryIndex = Index(objectId); var entry = entries[entryIndex];
        var result = new Dictionary<int, Matrix4x4>(boneIndices.Count);
        var controller = entry.Controller;
        if (boneIndices.Count != 0 && controller is null)
            throw new InvalidOperationException("Selected bone render inputs require an existing motion-backed controller; use explicit fixed world axes for static content.");
        var count = controller?.BoneWorldMatrices.Count ?? 0;
        foreach (int index in boneIndices)
        {
            if ((uint)index >= (uint)count)
                throw new ArgumentOutOfRangeException(nameof(boneIndices), "A selected bone index is outside the bound model.");
        }
        CompletePreviewLeaves(); entry = entries[entryIndex];
        if (boneIndices.Count == 0) return result;
        var worlds = entry.Controller!.BoneWorldMatrices;
        foreach (int index in boneIndices)
        {
            result.Add(index, AffineProduct(worlds[index] * entry.Object.World));
        }
        return result;
    }
}
