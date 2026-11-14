using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Resolved native-name parent. PMM ordinals retain provenance and BoneIndex addresses the live PMX skeleton.</summary>
public sealed record ScenePmmAccessoryParent(Guid ObjectId, int BoneIndex, int PmmModelIndex, int PmmBoneIndex, string BoneName);

public sealed partial class ScenePmmControlState
{
    // This document was privately round-tripped and is never returned to callers. Internal samplers
    // only read it, avoiding a full PMM parse for each accessory capture at the playback clock.
    internal Mmd.Modern.Formats.PmmDocument AccessoryDocument => document;
    public IReadOnlyList<byte> AccessoryIndices => Array.AsReadOnly(document.Accessories.Select(a => a.Index).ToArray());

    public PmmAccessoryPose AccessoryAt(int accessoryIndex, double frame, bool saved = false)
        => PmmTrackSampling.AccessoryAt(document, accessoryIndex, frame, saved);

    /// <summary>Resolves against retained native names and explicit replacement bindings; no solver is sampled.</summary>
    public ScenePmmAccessoryParent? ResolveAccessoryParent(PmmAccessoryPose pose)
    {
        if (pose.ParentModelIndex < 0) return null;
        if (!byModel.TryGetValue(pose.ParentModelIndex, out var target) || (uint)pose.ParentBoneIndex >= (uint)target.BoneIndices.Length)
            throw new InvalidDataException($"PMM accessory parent model #{pose.ParentModelIndex} bone #{pose.ParentBoneIndex} is unavailable.");
        if (ResolveMappedBone(target, pose.ParentBoneIndex) is { } mapped)
            return new(mapped.Id, mapped.Bone, pose.ParentModelIndex, pose.ParentBoneIndex, target.BindingNames![pose.ParentBoneIndex]);
        if (target.DetachedOrdinals?.Contains(pose.ParentBoneIndex) == true) return null;
        throw new InvalidDataException("Unavailable PMM accessory native-name binding.");
    }

    private static void ValidateAccessoryParentMetadata(PmmDocument source)
    {
        if (source.Accessories.Select(a => a.Index).Distinct().Count() != source.Accessories.Count)
            throw new InvalidDataException("PMM accessories require unique stored IDs.");
        var models = source.Models.ToDictionary(m => (int)m.Index);
        void RequireParent(int modelId, int bone, string location)
        {
            if (modelId < 0) return;
            if (!models.TryGetValue(modelId, out var model) || (uint)bone >= (uint)model.BoneNames.Count)
                throw new InvalidDataException($"PMM accessory parent at {location} refers to unavailable model #{modelId} bone #{bone}.");
        }
        foreach (var accessory in source.Accessories)
        {
            RequireParent(accessory.ParentModelIndex, accessory.ParentBoneIndex, $"saved accessory #{accessory.Index}");
            foreach (var key in new[] { accessory.InitialKeyframe }.Concat(accessory.Keyframes))
                RequireParent(key.ParentModelIndex, key.ParentBoneIndex, $"accessory #{accessory.Index} frame {key.Frame}");
        }
    }

    private static void ValidateAccessoryMappedControls(PmmDocument source, Action<int, int, string> requireBone)
    {
        foreach (var accessory in source.Accessories)
        {
            if (accessory.ParentModelIndex >= 0)
                requireBone(accessory.ParentModelIndex, accessory.ParentBoneIndex, $"saved accessory #{accessory.Index} parent");
            foreach (var key in new[] { accessory.InitialKeyframe }.Concat(accessory.Keyframes))
                if (key.ParentModelIndex >= 0)
                    requireBone(key.ParentModelIndex, key.ParentBoneIndex, $"accessory #{accessory.Index} parent at frame {key.Frame}");
        }
    }
}
