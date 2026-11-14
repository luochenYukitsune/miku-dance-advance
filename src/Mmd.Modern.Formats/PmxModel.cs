// Ported from saba (https://github.com/benikabocha/saba) @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT License, Copyright (c) 2016-2017 benikabocha
//
// Data model for PMX 2.0 / 2.1, ported from saba's PMXFile.h. The C++ structs
// become immutable-ish C# objects using System.Numerics; raw PMX semantics are
// preserved (index -1 means "none", PMX 2.1 extensions included).

using System.Numerics;

namespace Mmd.Modern.Formats;

public enum PmxTextEncoding : byte
{
    Utf16LittleEndian = 0,
    Utf8 = 1,
}

public enum PmxVertexWeightType : byte
{
    Bdef1 = 0,
    Bdef2 = 1,
    Bdef4 = 2,
    Sdef = 3,
    Qdef = 4,
}

[Flags]
public enum PmxDrawModeFlags : byte
{
    None = 0,
    BothFaces = 0x01,
    GroundShadow = 0x02,
    CastSelfShadow = 0x04,
    ReceiveSelfShadow = 0x08,
    DrawEdge = 0x10,
    VertexColor = 0x20,
    DrawPoint = 0x40,
    DrawLine = 0x80,
}

public enum PmxSphereMode : byte
{
    None = 0,
    Multiply = 1,
    Add = 2,
    SubTexture = 3,
}

public enum PmxToonMode : byte
{
    Separate = 0,
    Common = 1,
}

[Flags]
public enum PmxBoneFlags : ushort
{
    None = 0,
    TargetShowMode = 0x0001,
    AllowRotate = 0x0002,
    AllowTranslate = 0x0004,
    Visible = 0x0008,
    AllowControl = 0x0010,
    Ik = 0x0020,
    AppendLocal = 0x0080,
    AppendRotate = 0x0100,
    AppendTranslate = 0x0200,
    FixedAxis = 0x0400,
    LocalAxis = 0x0800,
    DeformAfterPhysics = 0x1000,
    DeformOuterParent = 0x2000,
}

public enum PmxMorphType : byte
{
    Group = 0,
    Position = 1,
    Bone = 2,
    Uv = 3,
    AdditionalUv1 = 4,
    AdditionalUv2 = 5,
    AdditionalUv3 = 6,
    AdditionalUv4 = 7,
    Material = 8,
    Flip = 9,
    Impulse = 10,
}

public enum PmxMaterialMorphOperation : byte
{
    Multiply = 0,
    Add = 1,
}

public enum PmxDisplayFrameType : byte
{
    Default = 0,
    Special = 1,
}

public enum PmxDisplayTargetType : byte
{
    Bone = 0,
    Morph = 1,
}

public enum PmxRigidBodyShape : byte
{
    Sphere = 0,
    Box = 1,
    Capsule = 2,
}

public enum PmxRigidBodyOperation : byte
{
    Static = 0,
    Dynamic = 1,
    DynamicAndBoneMerge = 2,
}

public enum PmxJointType : byte
{
    SpringDof6 = 0,
    Dof6 = 1,
    P2P = 2,
    ConeTwist = 3,
    Slider = 4,
    Hinge = 5,
}

public enum PmxSoftBodyType : byte
{
    TriMesh = 0,
    Rope = 1,
}

[Flags]
public enum PmxSoftBodyFlags : byte
{
    None = 0,
    BLink = 0x01,
    Cluster = 0x02,
    HybridLink = 0x04,
}

/// <summary>PMX header and global index/encoding settings.</summary>
public sealed class PmxHeader
{
    public required float Version { get; init; }

    /// <summary>The header setting-count byte (8 for PMX 2.0/2.1).</summary>
    public required int HeaderDataSize { get; init; }

    public required PmxTextEncoding TextEncoding { get; init; }

    /// <summary>Number of additional UV channels, 0-4.</summary>
    public required int AdditionalUvCount { get; init; }

    public required int VertexIndexSize { get; init; }

    public required int TextureIndexSize { get; init; }

    public required int MaterialIndexSize { get; init; }

    public required int BoneIndexSize { get; init; }

    public required int MorphIndexSize { get; init; }

    public required int RigidBodyIndexSize { get; init; }

    public bool IsVersion21 => Version >= 2.1f;
}

/// <summary>A parsed PMX file (model information plus every section of the file).</summary>
public sealed class PmxModel
{
    public required PmxHeader Header { get; init; }

    public required string Name { get; init; }

    public required string EnglishName { get; init; }

    public required string Comment { get; init; }

    public required string EnglishComment { get; init; }

    public required IReadOnlyList<PmxVertex> Vertices { get; init; }

    public required IReadOnlyList<PmxTriangle> Faces { get; init; }

    public required IReadOnlyList<string> Textures { get; init; }

    public required IReadOnlyList<PmxMaterial> Materials { get; init; }

    public required IReadOnlyList<PmxBone> Bones { get; init; }

    public required IReadOnlyList<PmxMorph> Morphs { get; init; }

    public required IReadOnlyList<PmxDisplayFrame> DisplayFrames { get; init; }

    public required IReadOnlyList<PmxRigidBody> RigidBodies { get; init; }

    public required IReadOnlyList<PmxJoint> Joints { get; init; }

    public required IReadOnlyList<PmxSoftBody> SoftBodies { get; init; }
}

public readonly record struct PmxTriangle(int A, int B, int C);

public sealed class PmxVertex
{
    public required Vector3 Position { get; init; }

    public required Vector3 Normal { get; init; }

    public required Vector2 Uv { get; init; }

    /// <summary>Additional UV channels; length equals <see cref="PmxHeader.AdditionalUvCount"/>.</summary>
    public required IReadOnlyList<Vector4> AdditionalUvs { get; init; }

    public required PmxVertexWeightType WeightType { get; init; }

    /// <summary>Four bone slots; unused slots are -1. BDEF1/BDEF2/SDEF use the first two.</summary>
    public required IReadOnlyList<int> BoneIndices { get; init; }

    /// <summary>Four weights; BDEF1 is normalized to 1, BDEF2/SDEF store w and 1-w.</summary>
    public required IReadOnlyList<float> BoneWeights { get; init; }

    public Vector3 SdefC { get; init; }

    public Vector3 SdefR0 { get; init; }

    public Vector3 SdefR1 { get; init; }

    public required float EdgeScale { get; init; }
}

public sealed class PmxMaterial
{
    public required string Name { get; init; }

    public required string EnglishName { get; init; }

    public required Vector4 Diffuse { get; init; }

    public required Vector3 Specular { get; init; }

    public required float SpecularPower { get; init; }

    public required Vector3 Ambient { get; init; }

    public required PmxDrawModeFlags DrawMode { get; init; }

    public required Vector4 EdgeColor { get; init; }

    public required float EdgeSize { get; init; }

    public required int TextureIndex { get; init; }

    public required int SphereTextureIndex { get; init; }

    public required PmxSphereMode SphereMode { get; init; }

    public required PmxToonMode ToonMode { get; init; }

    public required int ToonTextureIndex { get; init; }

    public required string Memo { get; init; }

    public required int FaceVertexCount { get; init; }
}

public readonly record struct PmxIkLink(
    int BoneIndex,
    bool EnableLimit,
    Vector3 LimitMin,
    Vector3 LimitMax);

public sealed class PmxBone
{
    public required string Name { get; init; }

    public required string EnglishName { get; init; }

    public required Vector3 Position { get; init; }

    public required int ParentIndex { get; init; }

    public required int DeformDepth { get; init; }

    public required PmxBoneFlags Flags { get; init; }

    /// <summary>Connection offset used when <see cref="PmxBoneFlags.TargetShowMode"/> is clear.</summary>
    public Vector3 PositionOffset { get; init; }

    /// <summary>Connection bone used when <see cref="PmxBoneFlags.TargetShowMode"/> is set; -1 otherwise.</summary>
    public int LinkBoneIndex { get; init; } = -1;

    public int AppendBoneIndex { get; init; } = -1;

    public float AppendWeight { get; init; }

    public Vector3 FixedAxis { get; init; }

    public Vector3 LocalXAxis { get; init; }

    public Vector3 LocalZAxis { get; init; }

    public int ExternalParentKey { get; init; }

    public int IkTargetBoneIndex { get; init; } = -1;

    public int IkIterationCount { get; init; }

    public float IkLimit { get; init; }

    public IReadOnlyList<PmxIkLink> IkLinks { get; init; } = Array.Empty<PmxIkLink>();
}

public sealed class PmxMorph
{
    public required string Name { get; init; }

    public required string EnglishName { get; init; }

    public required byte ControlPanel { get; init; }

    public required PmxMorphType Type { get; init; }

    public IReadOnlyList<GroupOffset> GroupOffsets { get; init; } = Array.Empty<GroupOffset>();

    public IReadOnlyList<PositionOffset> PositionOffsets { get; init; } = Array.Empty<PositionOffset>();

    public IReadOnlyList<BoneOffset> BoneOffsets { get; init; } = Array.Empty<BoneOffset>();

    public IReadOnlyList<UvOffset> UvOffsets { get; init; } = Array.Empty<UvOffset>();

    public IReadOnlyList<MaterialOffset> MaterialOffsets { get; init; } = Array.Empty<MaterialOffset>();

    public IReadOnlyList<GroupOffset> FlipOffsets { get; init; } = Array.Empty<GroupOffset>();

    public IReadOnlyList<ImpulseOffset> ImpulseOffsets { get; init; } = Array.Empty<ImpulseOffset>();

    public readonly record struct GroupOffset(int MorphIndex, float Weight);

    public readonly record struct PositionOffset(int VertexIndex, Vector3 Position);

    public readonly record struct BoneOffset(int BoneIndex, Vector3 Position, Quaternion Rotation);

    public readonly record struct UvOffset(int VertexIndex, Vector4 Uv);

    public readonly record struct MaterialOffset(
        int MaterialIndex,
        PmxMaterialMorphOperation Operation,
        Vector4 Diffuse,
        Vector3 Specular,
        float SpecularPower,
        Vector3 Ambient,
        Vector4 EdgeColor,
        float EdgeSize,
        Vector4 TextureFactor,
        Vector4 SphereTextureFactor,
        Vector4 ToonTextureFactor);

    public readonly record struct ImpulseOffset(
        int RigidBodyIndex,
        bool Local,
        Vector3 TranslationVelocity,
        Vector3 RotationTorque);
}

public readonly record struct PmxDisplayTarget(PmxDisplayTargetType Type, int Index);

public sealed class PmxDisplayFrame
{
    public required string Name { get; init; }

    public required string EnglishName { get; init; }

    public required PmxDisplayFrameType Type { get; init; }

    public required IReadOnlyList<PmxDisplayTarget> Targets { get; init; }
}

public sealed class PmxRigidBody
{
    public required string Name { get; init; }

    public required string EnglishName { get; init; }

    public required int BoneIndex { get; init; }

    public required byte Group { get; init; }

    public required ushort CollisionGroup { get; init; }

    public required PmxRigidBodyShape Shape { get; init; }

    public required Vector3 ShapeSize { get; init; }

    public required Vector3 Translation { get; init; }

    public required Vector3 Rotation { get; init; }

    public required float Mass { get; init; }

    public required float TranslationDamping { get; init; }

    public required float RotationDamping { get; init; }

    public required float Repulsion { get; init; }

    public required float Friction { get; init; }

    public required PmxRigidBodyOperation Operation { get; init; }
}

public sealed class PmxJoint
{
    public required string Name { get; init; }

    public required string EnglishName { get; init; }

    public required PmxJointType Type { get; init; }

    public required int RigidBodyAIndex { get; init; }

    public required int RigidBodyBIndex { get; init; }

    public required Vector3 Translation { get; init; }

    public required Vector3 Rotation { get; init; }

    public required Vector3 TranslationLowerLimit { get; init; }

    public required Vector3 TranslationUpperLimit { get; init; }

    public required Vector3 RotationLowerLimit { get; init; }

    public required Vector3 RotationUpperLimit { get; init; }

    public required Vector3 SpringTranslationFactor { get; init; }

    public required Vector3 SpringRotationFactor { get; init; }
}

public readonly record struct PmxSoftBodyConfig(
    float Vcf, float Dp, float Dg, float Lf, float Pr, float Vc,
    float Df, float Mt, float Chr, float Khr, float Shr, float Ahr);

public readonly record struct PmxSoftBodyClusterConfig(
    float SrhrCl, float SkhrCl, float SshrCl,
    float SrSpltCl, float SkSpltCl, float SsSpltCl);

public readonly record struct PmxSoftBodyIterations(int V, int P, int D, int C);

public readonly record struct PmxSoftBodyMaterialConfig(float Lst, float Ast, float Vst);

public readonly record struct PmxSoftBodyAnchor(int RigidBodyIndex, int VertexIndex, bool NearMode);

public sealed class PmxSoftBody
{
    public required string Name { get; init; }

    public required string EnglishName { get; init; }

    public required PmxSoftBodyType Type { get; init; }

    public required int MaterialIndex { get; init; }

    public required byte Group { get; init; }

    public required ushort CollisionGroup { get; init; }

    public required PmxSoftBodyFlags Flags { get; init; }

    public required int BLinkLength { get; init; }

    public required int ClusterCount { get; init; }

    public required float TotalMass { get; init; }

    public required float CollisionMargin { get; init; }

    public required int AeroModel { get; init; }

    public required PmxSoftBodyConfig Config { get; init; }

    public required PmxSoftBodyClusterConfig ClusterConfig { get; init; }

    public required PmxSoftBodyIterations Iterations { get; init; }

    public required PmxSoftBodyMaterialConfig MaterialConfig { get; init; }

    public required IReadOnlyList<PmxSoftBodyAnchor> Anchors { get; init; }

    public required IReadOnlyList<int> PinVertexIndices { get; init; }
}
