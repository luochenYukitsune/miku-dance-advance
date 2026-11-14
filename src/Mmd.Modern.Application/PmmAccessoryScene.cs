using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Application.MotionEditing;

namespace Mmd.Modern.Application;

public sealed record PmmAccessoryFrame(byte Index, OriginalFrameScene Frame, bool Visible, float Opacity, bool AddBlendEnabled)
{
    /// <summary>Exact source and panel inputs retained by the sampler; absent on legacy manual frames.</summary>
    public PmmAccessorySampleInputs? Inputs { get; init; }
}

/// <summary>Applies PMM accessory channels to an immutable neutral .x frame. Parent resolver receives stored
/// model ID and original bone-table index. The host resolves that identity to a current model-world bone matrix.
/// AddBlendEnabled is a required output contract; the renderer must implement it or reject it explicitly.</summary>
public static class PmmAccessoryScene
{
    public static PmmAccessoryFrame Sample(PmmDocument document, int accessoryIndex, OriginalFrameScene source, double frame,
        Func<int, int, Matrix4x4?>? resolveParent = null, bool saved = false)
    {
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(source);
        var accessory = document.Accessories.SingleOrDefault(a => a.Index == accessoryIndex)
            ?? throw new KeyNotFoundException($"Accessory #{accessoryIndex} does not exist.");
        var pose = PmmTrackSampling.AccessoryAt(document, accessoryIndex, frame, saved);
        if (!float.IsFinite(pose.Scale) || pose.Scale <= 0 || !float.IsFinite(pose.Opacity) || pose.Opacity is < 0 or > 1
            || !Finite(pose.Translation) || !Finite(pose.Rotation)) throw new InvalidDataException("Invalid accessory transform or opacity.");
        Matrix4x4 parent = Matrix4x4.Identity;
        if (pose.ParentModelIndex >= 0)
        {
            parent = resolveParent?.Invoke(pose.ParentModelIndex, pose.ParentBoneIndex)
                ?? throw new InvalidDataException($"Accessory #{accessoryIndex} parent model #{pose.ParentModelIndex} bone #{pose.ParentBoneIndex} is unavailable.");
        }
        var world = source.Surface.Snapshot.World * WorldTransform(pose, parent);
        if (!Matrix4x4.Invert(world, out _) || !Finite(world)) throw new InvalidDataException("Accessory parent produces an invalid world transform.");
        var inputs = new PmmAccessorySampleInputs(frame, saved, pose, source, parent, world);
        if (!pose.Visible)
        {
            var empty = SceneSnapshot.CreateEmpty(source.Surface.Snapshot.Camera, source.Surface.Snapshot.LightDirection);
            return new(accessory.Index, new(new(empty, [], source.Surface.Profile), OriginalOutlineData.Empty,
                source.OutlineOptions ?? new(Vector4.One, Matrix4x4.Identity, empty.Camera.Eye, 30, 0, false)), false, pose.Opacity, accessory.AddBlendEnabled)
                { Inputs = inputs };
        }
        var original = source.Surface.Snapshot;
        var materials = original.Materials.Select(m => m with { Diffuse = m.Diffuse with { W = m.Diffuse.W * pose.Opacity } }).ToArray();
        var snapshot = new SceneSnapshot(original.Vertices, original.Indices, materials, original.Textures, world, original.Camera, original.LightDirection,
            original.AdditionalUvs, original.RawRadialSeeds);
        var flags = source.Materials.Select(m => m with { CastGroundShadow = m.CastGroundShadow && pose.ShadowEnabled }).ToArray();
        return new(accessory.Index, new(new(snapshot, source.Surface.Materials, source.Surface.Profile), source.Outline, source.OutlineOptions,
            source.Options, flags, source.Lighting, source.EditorOverlay), true, pose.Opacity, accessory.AddBlendEnabled)
            { Inputs = inputs };
    }
    /// <summary>Original 9.32 accessory matrix builder 0x1400de8f0: scale*10, Rz*Rx*Ry, local translation,
    /// then the current bone world. Its native rest-translation * skin-matrix pair is already combined in parentWorld.
    /// Three isolated original function calls (including noncommuting rotations) are retained in stage evidence.</summary>
    public static Matrix4x4 WorldTransform(PmmAccessoryPose pose, Matrix4x4 parentWorld)
    {
        if (!float.IsFinite(pose.Scale) || pose.Scale <= 0 || !Finite(pose.Rotation) || !Finite(pose.Translation)
            || !Finite(parentWorld) || !Matrix4x4.Invert(parentWorld, out _)) throw new InvalidDataException("Invalid accessory world inputs.");
        var world = Matrix4x4.CreateScale(pose.Scale * 10f) * Matrix4x4.CreateRotationZ(pose.Rotation.Z)
            * Matrix4x4.CreateRotationX(pose.Rotation.X) * Matrix4x4.CreateRotationY(pose.Rotation.Y)
            * Matrix4x4.CreateTranslation(pose.Translation) * parentWorld;
        if (!Finite(world) || !Matrix4x4.Invert(world, out _)) throw new InvalidDataException("Accessory world transform is not representable.");
        return world;
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Matrix4x4 value) => value.M14 == 0 && value.M24 == 0 && value.M34 == 0 && value.M44 == 1
        && new[] { value.M11, value.M12, value.M13, value.M21, value.M22, value.M23, value.M31, value.M32, value.M33, value.M41, value.M42, value.M43 }.All(float.IsFinite);
}
