using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

public sealed partial class SceneAnimationSession
{
    /// <summary>Uses the current solved pose without seeking or advancing a second clock.</summary>
    public Matrix4x4 ResolveLightParentTransform(SceneLightParent parent)
    {
        RequireHealthy();
        return ResolveLightParentTransform(entries, parent);
    }

    private static void ValidateLightParents(Entry[] input, SceneLightingSnapshot lights)
    {
        var ids = input.Select(entry => entry.Object.Id).ToHashSet();
        if (lights.Lights.Any(light => light.Receivers?.ObjectIds.Any(id => !ids.Contains(id)) == true))
            throw new ArgumentException("Light receivers must reference models in the same scene.", nameof(lights));
        foreach (var parent in lights.Lights.Select(light => light.Parent).OfType<SceneLightParent>().Distinct())
        {
            var entry = input.SingleOrDefault(entry => entry.Object.Id == parent.ObjectId)
                ?? throw new ArgumentException("A light parent must reference a model in the same scene.", nameof(lights));
            if (parent.BoneIndex is { } bone && (uint)bone >= (uint)(entry.Motion?.Model.Bones.Count ?? 0))
                throw new ArgumentException("A light's parent bone must exist in a model with an owned pose.", nameof(lights));
        }
    }

    private static Matrix4x4 ResolveLightParentTransform(Entry[] input, SceneLightParent parent)
    {
        var entry = input.SingleOrDefault(entry => entry.Object.Id == parent.ObjectId)
            ?? throw new ArgumentException("A light parent must reference a model in the same scene.", nameof(parent));
        if (parent.BoneIndex is not { } bone) return entry.Object.World;
        var worlds = entry.Controller?.BoneWorldMatrices;
        if (worlds is null || (uint)bone >= (uint)worlds.Count)
            throw new ArgumentException("A light's parent bone has no solved pose.", nameof(parent));
        return AffineProduct(worlds[bone] * entry.Object.World);
    }

    private static SceneLightingSnapshot ResolveLighting(Entry[] input, SceneLightingSnapshot lights)
    {
        ValidateLightParents(input, lights);
        return lights.ResolveParents(parent => ResolveLightParentTransform(input, parent));
    }
}

public sealed partial class SceneProjectSession
{
    public Matrix4x4 ResolveLightParentTransform(SceneLightParent parent)
    {
        RequireIdle();
        return animation.ResolveLightParentTransform(parent);
    }
}
