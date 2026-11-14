// PMM v2 layouts adapted from nanoem/ext/document.c and document_p.h
// @ 30acffaa29f5d2eb9e997d69418f2e4b97b5894f, MIT, Copyright (c) 2015-2023 hkrn.
using System.Numerics;

namespace Mmd.Modern.Formats;

/// <summary>Decoding is explicit: PMM contains ANSI bytes, without a code-page marker.</summary>
public sealed class PmmReadOptions
{
    public int AnsiCodePage { get; init; } = 936;
    public int MaxElementCount { get; init; } = 2_000_000;
    public int MaxInputBytes { get; init; } = 512 * 1024 * 1024;
    /// <summary>PMM 0001 omits track counts/names. Caller resolves exact original model metadata;
    /// Formats never opens model files. Missing metadata fails before consuming variable records.</summary>
    public Func<PmmV1ModelReference, PmmV1ModelMetadata?>? V1ModelMetadataResolver { get; init; }
}

public sealed record PmmV1ModelReference(PmmString Path, int ModelPosition, byte StoredModelIndex);

/// <summary>Ordered model metadata, captured independently from the PMM stream.</summary>
public sealed class PmmV1ModelMetadata
{
    public IReadOnlyList<string> BoneNames { get; }
    public IReadOnlyList<string> MorphNames { get; }
    public IReadOnlyList<int> ConstraintBoneIndices { get; }
    public PmmV1ModelMetadata(IEnumerable<string> boneNames, IEnumerable<string> morphNames, IEnumerable<int> constraintBoneIndices)
    {
        ArgumentNullException.ThrowIfNull(boneNames); ArgumentNullException.ThrowIfNull(morphNames); ArgumentNullException.ThrowIfNull(constraintBoneIndices);
        string[] bones = boneNames.ToArray(), morphs = morphNames.ToArray(); int[] constraints = constraintBoneIndices.ToArray();
        if (bones.Concat(morphs).Any(name => name is null || name.Contains('\0')))
            throw new ArgumentException("Metadata names cannot be null or contain NUL.");
        if (constraints.Any(index => index < 0 || index >= bones.Length) || constraints.Distinct().Count() != constraints.Length)
            throw new ArgumentException("Constraint targets must be distinct valid bone indices.");
        BoneNames = Array.AsReadOnly(bones); MorphNames = Array.AsReadOnly(morphs); ConstraintBoneIndices = Array.AsReadOnly(constraints);
    }
}

/// <summary>Includes the entire fixed buffer (including NUL padding), or variable payload.</summary>
public sealed record PmmString(long Offset, string Value, IReadOnlyList<byte> RawBytes)
{
    public override string ToString() => Value;
}

public sealed class PmmDocument
{
    public int Version { get; init; } = 2;
    public int AnsiCodePage { get; init; }
    public IReadOnlyList<byte> HeaderBytes { get; init; } = [];
    public int OutputWidth { get; init; }
    public int OutputHeight { get; init; }
    public int TimelineWidth { get; init; }
    public float CameraFov { get; init; }
    public IReadOnlyList<byte> PanelStates { get; init; } = [];
    public byte SelectedModelIndex { get; init; }
    public IReadOnlyList<PmmModel> Models { get; init; } = [];
    /// <summary>PMM 0001's separate 20-byte model-name table, retained byte-for-byte.</summary>
    public IReadOnlyList<PmmString> V1ModelNames { get; init; } = [];
    public PmmCamera Camera { get; init; } = new();
    public PmmLight Light { get; init; } = new();
    public byte SelectedAccessoryIndex { get; init; }
    public int AccessoryHorizontalScroll { get; init; }
    public IReadOnlyList<PmmString> AccessoryNames { get; init; } = [];
    public IReadOnlyList<PmmAccessory> Accessories { get; init; } = [];
    public int CurrentFrame { get; init; }
    public PmmSettings Settings { get; init; } = new();
    public PmmGravity Gravity { get; init; } = new();
    public PmmSelfShadow SelfShadow { get; init; } = new();
    /// <summary>Offset after all recognized fields, before unknown tail.</summary>
    public long ParsedLength { get; init; }
    public long BytesConsumed { get; init; }
    public IReadOnlyList<byte> UnknownTail { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

public sealed class PmmModel
{
    public long Offset { get; init; }
    public byte Index { get; init; }
    public PmmString Name { get; init; } = new(0, "", []);
    public PmmString EnglishName { get; init; } = new(0, "", []);
    public PmmString Path { get; init; } = new(0, "", []);
    public byte FixedTrackCount { get; init; }
    public IReadOnlyList<PmmString> BoneNames { get; init; } = [];
    public IReadOnlyList<PmmString> MorphNames { get; init; } = [];
    public IReadOnlyList<int> ConstraintBoneIndices { get; init; } = [];
    public IReadOnlyList<int> OutsideParentSubjectBoneIndices { get; init; } = [];
    public byte DrawOrderIndex { get; init; }
    public bool Visible { get; init; }
    public int SelectedBoneIndex { get; init; }
    public IReadOnlyList<int> SelectedMorphIndices { get; init; } = [];
    public IReadOnlyList<byte> ExpansionStates { get; init; } = [];
    public int VerticalScroll { get; init; }
    public int LastFrame { get; init; }
    public IReadOnlyList<PmmBoneKeyframe> InitialBoneKeyframes { get; init; } = [];
    public IReadOnlyList<PmmBoneKeyframe> BoneKeyframes { get; init; } = [];
    public IReadOnlyList<PmmMorphKeyframe> InitialMorphKeyframes { get; init; } = [];
    public IReadOnlyList<PmmMorphKeyframe> MorphKeyframes { get; init; } = [];
    public PmmModelKeyframe InitialModelKeyframe { get; init; } = new();
    public IReadOnlyList<PmmModelKeyframe> ModelKeyframes { get; init; } = [];
    public IReadOnlyList<PmmBoneState> BoneStates { get; init; } = [];
    public IReadOnlyList<PmmMorphState> MorphStates { get; init; } = [];
    public IReadOnlyList<PmmConstraintState> ConstraintStates { get; init; } = [];
    public IReadOnlyList<PmmOutsideParentState> OutsideParentStates { get; init; } = [];
    public bool BlendEnabled { get; init; }
    public float EdgeWidth { get; init; }
    public bool SelfShadowEnabled { get; init; }
    public byte TransformOrderIndex { get; init; }
}

/// <summary>Links address ObjectIndex (which may be sparse after deleted frames), not RecordIndex.
/// Zero is the no-link sentinel and also the first initial record.</summary>
public abstract class PmmKeyframe
{
    public long Offset { get; set; }
    public int RecordIndex { get; set; }
    /// <summary>Raw file index for additional records; synthesized track index for initial records.</summary>
    public int ObjectIndex { get; set; }
    public int Frame { get; set; }
    public int PreviousIndex { get; set; }
    public int NextIndex { get; set; }
    public bool Selected { get; init; }
}

public sealed class PmmBoneKeyframe : PmmKeyframe
{
    public IReadOnlyList<byte> Interpolation { get; init; } = [];
    public Vector3 Translation { get; init; }
    public Quaternion Rotation { get; init; }
    public bool PhysicsDisabled { get; init; }
}
public sealed class PmmMorphKeyframe : PmmKeyframe { public float Weight { get; init; } }
public sealed class PmmModelKeyframe : PmmKeyframe
{
    public bool Visible { get; init; }
    public IReadOnlyList<bool> ConstraintEnabled { get; init; } = [];
    public IReadOnlyList<PmmOutsideParent> OutsideParents { get; init; } = [];
}
public sealed record PmmBoneState(long Offset, Vector3 Translation, Quaternion Rotation, bool Dirty, bool PhysicsDisabled, byte SelectedTrackCount)
{
    /// <summary>Uninterpreted int32 preceding dirty/selected flags in PMM 0001.</summary>
    public int V1UnknownValue { get; init; }
}
public sealed record PmmMorphState(long Offset, float Weight);
public sealed record PmmConstraintState(long Offset, bool Enabled);
public sealed record PmmOutsideParent(long Offset, int ModelIndex, int BoneIndex);
public sealed record PmmOutsideParentState(long Offset, int BeginFrame, int EndFrame, PmmOutsideParent Parent);

public sealed class PmmCameraKeyframe : PmmKeyframe
{
    public float Distance { get; init; }
    public Vector3 LookAt { get; init; }
    public Vector3 Angle { get; init; }
    public int ParentModelIndex { get; init; }
    public int ParentBoneIndex { get; init; }
    public IReadOnlyList<byte> Interpolation { get; init; } = [];
    public bool Perspective { get; init; }
    public int Fov { get; init; }
}
public sealed class PmmCamera
{
    public long Offset { get; init; }
    public PmmCameraKeyframe InitialKeyframe { get; init; } = new();
    public IReadOnlyList<PmmCameraKeyframe> Keyframes { get; init; } = [];
    public Vector3 LookAt { get; init; }
    public Vector3 Position { get; init; }
    public Vector3 Angle { get; init; }
    public bool Perspective { get; init; }
}
public sealed class PmmLightKeyframe : PmmKeyframe
{
    public Vector3 Color { get; init; }
    public Vector3 Direction { get; init; }
}
public sealed class PmmLight
{
    public long Offset { get; init; }
    public PmmLightKeyframe InitialKeyframe { get; init; } = new();
    public IReadOnlyList<PmmLightKeyframe> Keyframes { get; init; } = [];
    public Vector3 Color { get; init; }
    public Vector3 Direction { get; init; }
}
public sealed class PmmAccessoryKeyframe : PmmKeyframe
{
    public byte OpacityAndVisibility { get; init; }
    public bool Visible => (OpacityAndVisibility & 1) != 0;
    public float Opacity => (100 - ((OpacityAndVisibility & 0xfe) >> 1)) * .01f;
    public int ParentModelIndex { get; init; }
    public int ParentBoneIndex { get; init; }
    public Vector3 Translation { get; init; }
    public Vector3 Rotation { get; init; }
    public float Scale { get; init; }
    public bool ShadowEnabled { get; init; }
}
public sealed class PmmAccessory
{
    public long Offset { get; init; }
    public byte Index { get; init; }
    public PmmString Name { get; init; } = new(0, "", []);
    public PmmString Path { get; init; } = new(0, "", []);
    public byte DrawOrderIndex { get; init; }
    public PmmAccessoryKeyframe InitialKeyframe { get; init; } = new();
    public IReadOnlyList<PmmAccessoryKeyframe> Keyframes { get; init; } = [];
    public byte OpacityAndVisibility { get; init; }
    public bool Visible => (OpacityAndVisibility & 1) != 0;
    public float Opacity => (100 - ((OpacityAndVisibility & 0xfe) >> 1)) * .01f;
    public int ParentModelIndex { get; init; }
    public int ParentBoneIndex { get; init; }
    public Vector3 Translation { get; init; }
    public float Scale { get; init; }
    public Vector3 Rotation { get; init; }
    public bool ShadowEnabled { get; init; }
    public bool AddBlendEnabled { get; init; }
}
public sealed class PmmGravityKeyframe : PmmKeyframe
{
    public bool NoiseEnabled { get; init; }
    public int Noise { get; init; }
    public float Acceleration { get; init; }
    public Vector3 Direction { get; init; }
}
public sealed class PmmGravity
{
    public long Offset { get; init; }
    public float Acceleration { get; init; }
    public int Noise { get; init; }
    public Vector3 Direction { get; init; }
    public bool NoiseEnabled { get; init; }
    public PmmGravityKeyframe InitialKeyframe { get; init; } = new();
    public IReadOnlyList<PmmGravityKeyframe> Keyframes { get; init; } = [];
}
public sealed class PmmSelfShadowKeyframe : PmmKeyframe
{
    public byte Mode { get; init; }
    public float Distance { get; init; }
}
public sealed class PmmSelfShadow
{
    public long Offset { get; init; }
    public bool Enabled { get; init; }
    public float Distance { get; init; }
    public PmmSelfShadowKeyframe InitialKeyframe { get; init; } = new();
    public IReadOnlyList<PmmSelfShadowKeyframe> Keyframes { get; init; } = [];
}
public sealed record PmmModelSelection(long Offset, byte ModelIndex, int SelectionIndex);

public sealed class PmmSettings
{
    public long Offset { get; init; }
    public int HorizontalScroll { get; init; }
    public int HorizontalScrollThumb { get; init; }
    public int EditingMode { get; init; }
    public byte CameraLookMode { get; init; }
    public bool LoopEnabled { get; init; }
    public bool BeginFrameEnabled { get; init; }
    public bool EndFrameEnabled { get; init; }
    public int BeginFrame { get; init; }
    public int EndFrame { get; init; }
    public bool AudioEnabled { get; init; }
    public PmmString AudioPath { get; init; } = new(0, "", []);
    public int VideoOffsetX { get; init; }
    public int VideoOffsetY { get; init; }
    public float VideoScale { get; init; }
    public PmmString VideoPath { get; init; } = new(0, "", []);
    public int VideoEnabledRaw { get; init; }
    public int ImageOffsetX { get; init; }
    public int ImageOffsetY { get; init; }
    public float ImageScale { get; init; }
    public PmmString ImagePath { get; init; } = new(0, "", []);
    public bool ImageEnabled { get; init; }
    public bool InformationShown { get; init; }
    public bool GridAndAxisShown { get; init; }
    public bool GroundShadowShown { get; init; }
    public float PreferredFps { get; init; }
    public int ScreenCaptureMode { get; init; }
    public int AccessoryIndexAfterModels { get; init; }
    public float GroundShadowBrightness { get; init; }
    public bool TranslucentGroundShadowEnabled { get; init; }
    public byte PhysicsSimulationMode { get; init; }
    public IReadOnlyList<int> EdgeColorRgb { get; init; } = [];
    public bool BlackBackgroundEnabled { get; init; }
    public int CameraLookAtModelIndex { get; init; }
    public int CameraLookAtBoneIndex { get; init; }
    public IReadOnlyList<byte> UnknownMatrix { get; init; } = [];
    public byte FollowingLookAtRaw { get; init; }
    public byte UnknownBooleanRaw { get; init; }
    public bool PhysicsGroundEnabled { get; init; }
    public int CurrentFrameText { get; init; }
    public byte? SelectionTrailerFlag { get; init; }
    public IReadOnlyList<PmmModelSelection> ModelSelections { get; init; } = [];
}
