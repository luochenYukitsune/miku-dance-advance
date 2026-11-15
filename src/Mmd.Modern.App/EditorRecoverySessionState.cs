using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App;

/// <summary>Detached, versioned editor state stored beside a recovery PMA. It never registers keys.</summary>
public sealed record EditorRecoverySessionState
{
    public const int MaximumJsonBytes = 16 * 1024 * 1024;
    internal const int MaximumTextLength = 4096;
    internal const int MaximumActors = 10000;
    internal const int MaximumChannels = 1000000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        IgnoreReadOnlyProperties = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    [JsonRequired] public int SchemaVersion { get; init; } = 1;
    public required EditorRecoveryAnimationState Animation { get; init; }
    public required EditorRecoverySceneState Scene { get; init; }
    [JsonRequired] public int SceneSelectionIndex { get; init; } = -1;
    [JsonRequired] public int HierarchyTab { get; init; }
    [JsonRequired] public double PixelsPerFrame { get; init; } = 8;
    [JsonRequired] public double FirstVisibleFrame { get; init; }
    /// <summary>Playback can pause between source frames while the timeline displays its integer floor.</summary>
    public double? ProjectFrame { get; init; }
    public ImmutableSortedDictionary<string, string> NumericInputText { get; init; } = ImmutableSortedDictionary<string, string>.Empty;
    public EditorRecoveryNativeLightingState? NativeLighting { get; init; }

    internal void ValidateNativeLighting(Mmd.Modern.Core.SceneLightingSnapshot committed)
        => NativeLighting?.ValidateAgainst(committed);

    public static EditorRecoverySessionState Capture(EditorAnimationViewModel animation,
        EditorSceneInspectorViewModel scene, IEnumerable<SceneActorMotion> actors)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(scene);
        var available = MaterializeActors(actors);
        var captured = animation.CaptureRecoveryState(available);
        var state = new EditorRecoverySessionState
        {
            Animation = captured with { SelectedKeyframes = ExistingSelection(captured, available) },
            Scene = scene.CaptureRecoveryState()
        };
        state.ValidateForActors(available);
        scene.ValidateRecoveryContext(state.Scene, available);
        return state;
    }

    public byte[] ToJsonBytes()
    {
        Validate();
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
            if (bytes.Length > MaximumJsonBytes) throw Invalid("Recovery editor state exceeds its size limit.");
            return bytes;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException)
        { throw Invalid("Recovery editor state could not be serialized.", error); }
    }

    public static EditorRecoverySessionState FromJsonBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumJsonBytes)
            throw Invalid("Recovery editor state has an invalid size.");
        try
        {
            // The serializer otherwise accepts repeated members using the last value. A recovery
            // envelope must have one unambiguous value for every field, including nested objects.
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicateMembers(document.RootElement);
            var state = JsonSerializer.Deserialize<EditorRecoverySessionState>(bytes, JsonOptions)
                ?? throw Invalid("Recovery editor state is missing.");
            state.Validate();
            return state;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException)
        { throw Invalid("Recovery editor state is malformed or uses an unsupported schema.", error); }
    }

    /// <summary>Keeps only selected keyframes that exist in the motions being stored. The caller may pass a
    /// projection of the live motion (a recovery copy without unwritable channels), whose keys are a subset.</summary>
    private static ImmutableArray<KeyframeAddress> ExistingSelection(EditorRecoveryAnimationState animation, IReadOnlyList<SceneActorMotion> actors)
    {
        if (animation.ObjectId is not { } selected || animation.SelectedKeyframes.IsDefaultOrEmpty) return animation.SelectedKeyframes;
        var actor = actors.FirstOrDefault(item => item.ObjectId == selected);
        if (actor is null) return animation.SelectedKeyframes;
        var addresses = KeyframeAddresses(actor);
        return animation.SelectedKeyframes.Where(addresses.Contains).ToImmutableArray();
    }

    private static HashSet<KeyframeAddress> KeyframeAddresses(SceneActorMotion actor)
    {
        var effective = actor.Detachment?.Filter(actor.Motion) ?? actor.Motion;
        return effective.BoneKeyframes.Select(key => KeyframeAddress.Bone(key.BoneName, key.Frame))
            .Concat(effective.MorphKeyframes.Select(key => KeyframeAddress.Morph(key.MorphName, key.Frame)))
            .Concat(actor.Motion.IkKeyframes.Select(key => KeyframeAddress.Ik(key.Frame))).ToHashSet();
    }

    public void ValidateForActors(IEnumerable<SceneActorMotion> actors)
    {
        Validate();
        var available = MaterializeActors(actors).ToDictionary(actor => actor.ObjectId);
        if (available.Count != Animation.Actors.Length) throw Invalid("Recovery actor count does not match the project.");
        foreach (var saved in Animation.Actors)
        {
            if (!available.TryGetValue(saved.ObjectId, out var actor))
                throw Invalid("Recovery state refers to an actor that is absent from the project.");
            if (actor.Model.Bones.Count != saved.BoneCount || actor.Model.Morphs.Count != saved.MorphCount
                || !StringComparer.Ordinal.Equals(saved.StructureSignature, ModelStructureSignature(actor.Model)))
                throw Invalid("Recovery bone or morph structure does not match the project.");
        }
        if (Animation.ObjectId is { } selected)
        {
            var addresses = KeyframeAddresses(available[selected]);
            if (Animation.SelectedKeyframes.Any(address => !addresses.Contains(address)))
                throw Invalid("Recovery keyframe selection does not exist in the project.");
        }
    }

    /// <summary>Validate both halves and stage the motion/draft replacement before changing either editor.</summary>
    public void Restore(EditorAnimationViewModel animation, EditorSceneInspectorViewModel scene,
        IEnumerable<SceneActorMotion> actors)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(scene);
        var available = MaterializeActors(actors);
        ValidateForActors(available);
        if (animation.IsBusy || scene.IsBusy) throw Invalid("The editor is busy and cannot restore recovery state.");
        scene.ValidateRecoveryContext(Scene, available);
        var applyAnimation = animation.PrepareRecoveryRestore(Animation, available);
        applyAnimation();
        scene.RestoreRecoveryState(Scene);
    }

    private void Validate()
    {
        if (SchemaVersion != 1 || Animation is null || Scene is null)
            throw Invalid("Recovery editor schema is unsupported or incomplete.");
        if (SceneSelectionIndex is < -1 or > 2 || HierarchyTab is < 0 or > 1
            || !double.IsFinite(PixelsPerFrame) || PixelsPerFrame is < .5 or > 64
            || !double.IsFinite(FirstVisibleFrame) || FirstVisibleFrame is < 0 or > int.MaxValue)
            throw Invalid("Recovery editor presentation state is invalid.");
        if (ProjectFrame is { } frame && (!double.IsFinite(frame) || frame < 0 || frame > uint.MaxValue
            || (int)Math.Min(int.MaxValue, frame) != Animation.CurrentFrame))
            throw Invalid("Recovery project clock does not match the displayed timeline frame.");
        if (NumericInputText is null || NumericInputText.Count > EditorRecoveryNumericInputs.Names.Count)
            throw Invalid("Recovery numeric input state is invalid.");
        foreach (var input in NumericInputText)
        {
            if (!EditorRecoveryNumericInputs.Names.Contains(input.Key))
                throw Invalid("Recovery numeric input is not supported.");
            ValidateText(input.Value);
        }
        Animation.Validate();
        Scene.Validate();
        NativeLighting?.Validate();
        if (Animation.CurrentFrame != Scene.CurrentFrame)
            throw Invalid("Recovery editor frames are inconsistent.");
    }

    internal static SceneActorMotion[] MaterializeActors(IEnumerable<SceneActorMotion> actors)
    {
        ArgumentNullException.ThrowIfNull(actors);
        var result = new List<SceneActorMotion>();
        var ids = new HashSet<Guid>();
        foreach (var actor in actors)
        {
            if (actor is null || actor.ObjectId == Guid.Empty || actor.Model is null || actor.Motion is null
                || !ids.Add(actor.ObjectId) || result.Count >= MaximumActors
                || actor.Model.Bones.Count > MaximumChannels || actor.Model.Morphs.Count > MaximumChannels)
                throw Invalid("The recovery project contains invalid or duplicate actor identities.");
            result.Add(actor);
        }
        return result.ToArray();
    }

    internal static string ModelStructureSignature(PmxModel model)
    {
        using var sha = SHA256.Create();
        using (var hashing = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
        using (var writer = new BinaryWriter(hashing, Encoding.UTF8))
        {
            writer.Write(model.Bones.Count);
            foreach (var bone in model.Bones)
            {
                ValidateText(bone.Name); ValidateText(bone.EnglishName);
                writer.Write(bone.Name); writer.Write(bone.EnglishName); writer.Write(bone.ParentIndex);
                writer.Write(bone.DeformDepth); writer.Write((int)bone.Flags);
                WriteVector(writer, bone.Position); WriteVector(writer, bone.PositionOffset);
                writer.Write(bone.LinkBoneIndex); writer.Write(bone.AppendBoneIndex); writer.Write(bone.AppendWeight);
                WriteVector(writer, bone.FixedAxis); WriteVector(writer, bone.LocalXAxis); WriteVector(writer, bone.LocalZAxis);
                writer.Write(bone.ExternalParentKey); writer.Write(bone.IkTargetBoneIndex);
                writer.Write(bone.IkIterationCount); writer.Write(bone.IkLimit); writer.Write(bone.IkLinks.Count);
                foreach (var link in bone.IkLinks)
                {
                    writer.Write(link.BoneIndex); writer.Write(link.EnableLimit);
                    WriteVector(writer, link.LimitMin); WriteVector(writer, link.LimitMax);
                }
            }
            writer.Write(model.Morphs.Count);
            foreach (var morph in model.Morphs)
            {
                ValidateText(morph.Name); ValidateText(morph.EnglishName);
                writer.Write(morph.Name); writer.Write(morph.EnglishName);
                writer.Write((int)morph.Type); writer.Write(morph.ControlPanel);
            }
        }
        return Convert.ToHexStringLower(sha.Hash!);
    }

    private static void WriteVector(BinaryWriter writer, Vector3 value)
    { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }

    internal static void ValidateText(string? text)
    {
        if (text is null || text.Length > MaximumTextLength) throw Invalid("Recovery editor text is missing or too long.");
    }

    internal static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    internal static InvalidDataException Invalid(string message, Exception? inner = null) => new(message, inner);

    private static void RejectDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid("Recovery JSON contains duplicate members.");
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateMembers(item);
    }
}

public sealed record EditorRecoveryAnimationState
{
    public required int CurrentFrame { get; init; }
    public required int MoveTargetFrame { get; init; }
    public required Guid? ObjectId { get; init; }
    public required int? SelectedBoneIndex { get; init; }
    public required int? SelectedMorphIndex { get; init; }
    public required EditorTimelineTrackMode TimelineTrackMode { get; init; }
    public required bool AnimationContextActive { get; init; }
    public required ImmutableArray<KeyframeAddress> SelectedKeyframes { get; init; }
    public required string TimelineRangeStartText { get; init; }
    public required string TimelineRangeEndText { get; init; }
    public required ImmutableArray<EditorRecoveryActorState> Actors { get; init; }

    internal void Validate()
    {
        if (CurrentFrame < 0 || MoveTargetFrame < 0 || !Enum.IsDefined(TimelineTrackMode)
            || Actors.IsDefault || Actors.Length > EditorRecoverySessionState.MaximumActors
            || SelectedKeyframes.IsDefault || SelectedKeyframes.Length > EditorRecoverySessionState.MaximumChannels)
            throw EditorRecoverySessionState.Invalid("Recovery animation state is invalid.");
        EditorRecoverySessionState.ValidateText(TimelineRangeStartText);
        EditorRecoverySessionState.ValidateText(TimelineRangeEndText);
        var identities = new Dictionary<Guid, EditorRecoveryActorState>();
        foreach (var actor in Actors)
        {
            if (actor is null || !identities.TryAdd(actor.ObjectId, actor))
                throw EditorRecoverySessionState.Invalid("Recovery actors contain duplicate or missing entries.");
            actor.Validate();
        }
        if (ObjectId is { } id)
        {
            if (!identities.TryGetValue(id, out var actor)
                || SelectedBoneIndex is { } bone && (bone < 0 || bone >= actor.BoneCount)
                || SelectedMorphIndex is { } morph && (morph < 0 || morph >= actor.MorphCount)
                || SelectedBoneIndex.HasValue && SelectedMorphIndex.HasValue)
                throw EditorRecoverySessionState.Invalid("Recovery channel selection is invalid.");
        }
        else if (SelectedBoneIndex.HasValue || SelectedMorphIndex.HasValue || SelectedKeyframes.Length != 0)
            throw EditorRecoverySessionState.Invalid("Recovery selection has no actor.");
        var addresses = new HashSet<KeyframeAddress>();
        foreach (var address in SelectedKeyframes)
        {
            EditorRecoverySessionState.ValidateText(address.Name);
            if (!Enum.IsDefined(address.Kind) || address.Kind == KeyframeTrackKind.Ik && address.Name.Length != 0
                || !addresses.Add(address))
                throw EditorRecoverySessionState.Invalid("Recovery keyframe selection is invalid.");
        }
    }
}

public sealed record EditorRecoveryActorState
{
    public required Guid ObjectId { get; init; }
    public required string StructureSignature { get; init; }
    public required int BoneCount { get; init; }
    public required int MorphCount { get; init; }
    public required ImmutableArray<EditorRecoveryBoneDraft> Bones { get; init; }
    public required ImmutableArray<EditorRecoveryMorphDraft> Morphs { get; init; }

    internal void Validate()
    {
        if (ObjectId == Guid.Empty || StructureSignature is null || StructureSignature.Length != 64
            || StructureSignature.Any(character => !char.IsAsciiHexDigit(character))
            || BoneCount is < 0 or > EditorRecoverySessionState.MaximumChannels
            || MorphCount is < 0 or > EditorRecoverySessionState.MaximumChannels
            || Bones.IsDefault || Morphs.IsDefault || Bones.Length > BoneCount || Morphs.Length > MorphCount)
            throw EditorRecoverySessionState.Invalid("Recovery actor structure is invalid.");
        var indices = new HashSet<int>();
        foreach (var bone in Bones)
        {
            if (bone is null || bone.Index < 0 || bone.Index >= BoneCount || !indices.Add(bone.Index))
                throw EditorRecoverySessionState.Invalid("Recovery bone draft index is invalid or duplicated.");
            var rotation = bone.SampledRotation;
            double length = (double)rotation.X * rotation.X + (double)rotation.Y * rotation.Y
                + (double)rotation.Z * rotation.Z + (double)rotation.W * rotation.W;
            if (!float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z)
                || !float.IsFinite(rotation.W) || !double.IsFinite(length) || length == 0)
                throw EditorRecoverySessionState.Invalid("Recovery sampled rotation is invalid.");
        }
        indices.Clear();
        foreach (var morph in Morphs)
            if (morph is null || morph.Index < 0 || morph.Index >= MorphCount || !indices.Add(morph.Index))
                throw EditorRecoverySessionState.Invalid("Recovery morph draft index is invalid or duplicated.");
    }
}

public sealed record EditorRecoveryBoneDraft(int Index, decimal? X, decimal? Y, decimal? Z,
    decimal? Pitch, decimal? Yaw, decimal? Roll, Quaternion SampledRotation, bool RotationEdited);
public sealed record EditorRecoveryMorphDraft(int Index, decimal? Weight);

public sealed record EditorRecoverySceneState
{
    public required int CurrentFrame { get; init; }
    public required int SceneSelectionIndex { get; init; }
    public required PmmCameraPose CameraPose { get; init; }
    public required PmmLightPose LightPose { get; init; }
    public required bool CameraDraft { get; init; }
    public required bool LightDraft { get; init; }
    public required decimal? CenterX { get; init; }
    public required decimal? CenterY { get; init; }
    public required decimal? CenterZ { get; init; }
    public required decimal? RotationX { get; init; }
    public required decimal? RotationY { get; init; }
    public required decimal? RotationZ { get; init; }
    public required decimal? Distance { get; init; }
    public required decimal? Fov { get; init; }
    public required bool Perspective { get; init; }
    public required decimal? ColorR { get; init; }
    public required decimal? ColorG { get; init; }
    public required decimal? ColorB { get; init; }
    public required decimal? Intensity { get; init; }
    public required decimal? DirectionX { get; init; }
    public required decimal? DirectionY { get; init; }
    public required decimal? DirectionZ { get; init; }

    internal void Validate()
    {
        if (CurrentFrame < 0 || SceneSelectionIndex is < 0 or > 2
            || !EditorRecoverySessionState.Finite(CameraPose.LookAt) || !EditorRecoverySessionState.Finite(CameraPose.Angle)
            || !float.IsFinite(CameraPose.Distance) || !float.IsFinite(CameraPose.Fov)
            || !float.IsFinite(CameraPose.ViewOffset.X) || !float.IsFinite(CameraPose.ViewOffset.Y)
            || CameraPose.ParentModelIndex < -1 || CameraPose.ParentBoneIndex < -1
            || CameraPose.ParentModelIndex >= 0 && CameraPose.ParentBoneIndex < 0
            || CameraPose.Perspective != Perspective || !EditorRecoverySessionState.Finite(LightPose.Color)
            || !EditorRecoverySessionState.Finite(LightPose.Direction))
            throw EditorRecoverySessionState.Invalid("Recovery scene pose is invalid.");
        // Nullable and out-of-range typed values are intentional drafts. They are not repaired,
        // registered or published until the ordinary inspector validation accepts them.
    }
}
