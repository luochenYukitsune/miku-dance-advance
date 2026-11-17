using System.Collections.ObjectModel;
using System.Numerics;

namespace Mmd.Modern.Core;

public sealed partial class SceneLightingSnapshot
{
    private SceneLightingSnapshot? authoredLighting;
    private static readonly IReadOnlyDictionary<SceneLightParent, Matrix4x4> NoParents =
        new ReadOnlyDictionary<SceneLightParent, Matrix4x4>(new Dictionary<SceneLightParent, Matrix4x4>());

    /// <summary>The immutable local authoring values behind a resolved rendering snapshot.</summary>
    public SceneLightingSnapshot AuthoredLighting => authoredLighting ?? this;
    /// <summary>Same-frame parent matrices used to resolve this snapshot. Rendering-only metadata;
    /// not persisted and never sampled independently by a renderer or viewport gesture.</summary>
    public IReadOnlyDictionary<SceneLightParent, Matrix4x4> ParentTransforms { get; private set; } = NoParents;

    /// <summary>Resolves local lights once against already sampled scene transforms. The returned
    /// Lights and EffectiveLights are world-space; the authored snapshot remains unchanged.</summary>
    public SceneLightingSnapshot ResolveParents(Func<SceneLightParent, Matrix4x4> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        var source = AuthoredLighting;
        if (!source.Lights.Any(light => light.Parent is not null)) return source;
        var transforms = new Dictionary<SceneLightParent, Matrix4x4>();
        var world = source.Lights.Select(light =>
        {
            if (light.Parent is not { } parent) return light;
            if (!transforms.TryGetValue(parent, out var matrix))
                transforms.Add(parent, matrix = resolve(parent));
            return SceneLightTransform.ToWorld(light, matrix);
        }).ToArray();
        return new(world, source.Groups, source.Settings)
        {
            authoredLighting = source,
            ParentTransforms = new ReadOnlyDictionary<SceneLightParent, Matrix4x4>(transforms),
        };
    }
}

/// <summary>Light positions inherit parent placement and scale. Orientation inherits rotation;
/// range and area dimensions remain in scene units, so parenting does not unexpectedly resize illumination.</summary>
public static class SceneLightTransform
{
    public static NativeSceneLight ToWorld(NativeSceneLight local, Matrix4x4 parentWorld)
    {
        var rotation = ParentRotation(parentWorld);
        var result = local with { Position = Vector3.Transform(local.Position, parentWorld),
            Rotation = Quaternion.Normalize(Quaternion.Concatenate(local.Rotation, rotation)), Parent = null };
        SceneLightingSnapshot.ValidateLight(result);
        return result;
    }

    public static NativeSceneLight ToLocal(NativeSceneLight world, SceneLightParent parent, Matrix4x4 parentWorld)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var rotation = ParentRotation(parentWorld);
        if (!Matrix4x4.Invert(parentWorld, out var inverse))
            throw new ArgumentException("A light parent transform must be invertible.", nameof(parentWorld));
        var result = world with { Position = Vector3.Transform(world.Position, inverse),
            Rotation = Quaternion.Normalize(Quaternion.Concatenate(world.Rotation, Quaternion.Conjugate(rotation))), Parent = parent };
        SceneLightingSnapshot.ValidateLight(result);
        return result;
    }

    private static Quaternion ParentRotation(Matrix4x4 world)
    {
        if (world.M14 != 0 || world.M24 != 0 || world.M34 != 0 || world.M44 != 1
            || !Matrix4x4.Decompose(world, out var scale, out var rotation, out var translation)
            || !Finite(scale) || !Finite(translation) || scale.X == 0 || scale.Y == 0 || scale.Z == 0
            || !float.IsFinite(rotation.LengthSquared()) || rotation.LengthSquared() < .5f)
            throw new ArgumentException("A light parent requires a finite decomposable affine transform with nonzero scale.", nameof(world));
        return Quaternion.Normalize(rotation);
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
