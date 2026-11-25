using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class PmxBoundaryRegressionTests
{
    [Theory]
    [InlineData(1, -128, 129)]
    [InlineData(1, -2, 255)]
    [InlineData(2, -32768, 32769)]
    [InlineData(2, -2, 1)]
    [InlineData(4, -2, 1)]
    [InlineData(1, 99, 1)]
    public void RejectsInvalidVertexBoneAtOriginalField(int width, int index, int boneCount)
    {
        Probe probe = Make(new(SignedWidth: width, Bones: boneCount,
            OverrideField: "vertices[0].boneIndices[0]", OverrideIndex: index));
        Reject(probe, "vertices[0].boneIndices[0]");
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ValidatesEverySerializedSkinSlotAndPreservesUnusedSlots(byte weightType)
    {
        int lastSlot = weightType switch { 0 => 0, 1 or 3 => 1, _ => 3 };
        string field = $"vertices[0].boneIndices[{lastSlot}]";
        Reject(Make(new(WeightType: weightType, OverrideField: field, OverrideIndex: 9)), field);
        PmxVertex vertex = Read(Make(new(WeightType: weightType,
            OverrideField: field, OverrideIndex: -1))).Vertices[0];
        Assert.Equal(-1, vertex.BoneIndices[lastSlot]);
        Assert.All(vertex.BoneIndices.Skip(lastSlot + 1), value => Assert.Equal(-1, value));
    }

    [Theory]
    [InlineData(1, 256, 255)]
    [InlineData(2, 65536, 65535)]
    [InlineData(1, 255, 254)]
    [InlineData(2, 65535, 65534)]
    public void NarrowVertexIndicesRemainUnsigned(int width, int count, int index)
    {
        PmxModel model = Read(Make(new(VertexWidth: width, Vertices: count, LastFaceVertex: index)));
        Assert.Equal(index, model.Faces[0].C);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void MorphCountUsesExactRecordWidth(byte type)
    {
        foreach (var (vertexWidth, signedWidth) in new[] { (1, 4), (2, 2), (4, 1) })
        {
            Probe probe = Make(new(VertexWidth: vertexWidth, SignedWidth: signedWidth, MorphTypes: [type]));
            CheckMorphBoundary(probe);
        }
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(4, 1)]
    public void MaterialMorphUsesMaterialIndexWidth(int materialWidth, int morphWidth)
    {
        Probe probe = Make(new(MaterialWidth: materialWidth, MorphWidth: morphWidth, MorphTypes: [8]));
        CheckMorphBoundary(probe);
    }

    private static void CheckMorphBoundary(Probe probe)
    {
        int end = checked((int)probe.Offsets["displayFrames.count"]);
        Reject(probe with { Bytes = probe.Bytes[..(end - 1)] }, "morphs[0].count");
        Reject(probe with { Bytes = probe.Bytes[..end] }, "displayFrames.count");
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public void DeferredImpulseReferencesHaveOriginalFieldAndOffset(int width)
    {
        string field = "morphs[0].impulseOffsets[0].rigidBodyIndex";
        Reject(Make(new(SignedWidth: width, MorphTypes: [10],
            OverrideField: field, OverrideIndex: 99)), field);
        Reject(Make(new(SignedWidth: width, MorphTypes: [10],
            OverrideField: field, OverrideIndex: -2)), field);
        Reject(Make(new(SignedWidth: width, MorphTypes: [10],
            OverrideField: field, OverrideIndex: -1)), field);
        Assert.Equal(0, Read(Make(new(SignedWidth: width, MorphTypes: [10], RigidBodies: 1)))
            .Morphs[0].ImpulseOffsets[0].RigidBodyIndex);
    }

    [Theory]
    [InlineData("materials[0].textureIndex")]
    [InlineData("materials[0].sphereTextureIndex")]
    [InlineData("materials[0].toonTextureIndex")]
    [InlineData("bones[0].parentIndex")]
    [InlineData("bones[0].linkBoneIndex")]
    [InlineData("bones[0].appendBoneIndex")]
    [InlineData("bones[0].ikTargetBoneIndex")]
    [InlineData("bones[0].ikLinks[0].boneIndex")]
    [InlineData("morphs[0].groupOffsets[0].morphIndex")]
    [InlineData("morphs[1].boneOffsets[0].boneIndex")]
    [InlineData("morphs[2].materialOffsets[0].materialIndex")]
    [InlineData("morphs[3].flipOffsets[0].morphIndex")]
    [InlineData("morphs[4].impulseOffsets[0].rigidBodyIndex")]
    [InlineData("displayFrames[0].targets[0].index")]
    [InlineData("displayFrames[0].targets[1].index")]
    [InlineData("rigidBodies[0].boneIndex")]
    [InlineData("joints[0].rigidBodyAIndex")]
    [InlineData("joints[0].rigidBodyBIndex")]
    [InlineData("softBodies[0].materialIndex")]
    [InlineData("softBodies[0].anchors[0].rigidBodyIndex")]
    public void SignedIndexFamiliesRejectNegativeValuesOtherThanSentinel(string field)
    {
        foreach (int width in new[] { 1, 2, 4 })
        {
            Reject(Make(new(SignedWidth: width, BoneFeatures: true,
                MorphTypes: [0, 2, 8, 9, 10], DisplayTargets: true, RigidBodies: 1,
                Joint: true, SoftBody: true, OverrideField: field, OverrideIndex: -2)), field);
        }
    }

    [Theory]
    [InlineData("materials[0].textureIndex")]
    [InlineData("bones[0].parentIndex")]
    [InlineData("morphs[0].groupOffsets[0].morphIndex")]
    [InlineData("morphs[1].materialOffsets[0].materialIndex")]
    [InlineData("morphs[2].impulseOffsets[0].rigidBodyIndex")]
    public void SignedByteHighBitCannotAliasPositiveReference(string field)
    {
        // 0x80 would be in range as an unsigned value in every targeted section.
        byte[] morphs = [0, 8, 10, .. Enumerable.Repeat((byte)1, 126)];
        Reject(Make(new(Textures: 129, Bones: 129, RigidBodies: 129,
            MaterialFaces: [3, .. Enumerable.Repeat(0, 128)], MorphTypes: morphs,
            OverrideField: field, OverrideIndex: -128)), field);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public void AllowedMinusOneReferencesSurviveAllWidths(int width)
    {
        PmxModel model = Read(Make(new(SignedWidth: width, BoneFeatures: true,
            MorphTypes: [8], RigidBodies: 1, Joint: true)));
        Assert.Equal(-1, model.Materials[0].TextureIndex);
        Assert.Equal(-1, model.Materials[0].SphereTextureIndex);
        Assert.Equal(-1, model.Materials[0].ToonTextureIndex);
        Assert.Equal(-1, model.Bones[0].ParentIndex);
        Assert.Equal(-1, model.Bones[0].LinkBoneIndex);
        Assert.Equal(-1, model.Morphs[0].MaterialOffsets[0].MaterialIndex);
        Assert.Equal(-1, model.RigidBodies[0].BoneIndex);
        Assert.Equal(-1, model.Joints[0].RigidBodyAIndex);
        Assert.Equal(-1, model.Joints[0].RigidBodyBIndex);
    }

    [Fact]
    public void ReadsSixtyFourEmptyDisplayFrames() =>
        Assert.Equal(64, Read(Make(new(Frames: 64))).DisplayFrames.Count);

    [Fact]
    public void Pmx21RequiresSoftBodyCount() =>
        Reject(Make(new(WriteSoftCount: false)), "softBodies.count");

    [Fact]
    public void Pmx20EndsAfterJoints()
    {
        Assert.Empty(Read(Make(new(Version: 2.0f, WriteSoftCount: false))).SoftBodies);
        Reject(Make(new(Version: 2.0f)), "model.end");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RejectsTrailingBytesAfterCompletePmx21(bool softBody) =>
        Reject(Make(new(SoftBody: softBody, RigidBodies: softBody ? 1 : 0,
            TrailingBytes: [0xDE, 0xAD])), "model.end");

    [Theory]
    [InlineData(2)] [InlineData(999)] [InlineData(0)] [InlineData(-1)]
    public void MaterialRangesMustCoverGeometryExactly(int count) =>
        Reject(Make(new(MaterialFaces: [count])), "materials[0].faceVertexCount");

    [Fact]
    public void MaterialCountsCannotOverflowOrLeaveUnassignedFaces()
    {
        Reject(Make(new(MaterialFaces: [3, 2147483646])), "materials[1].faceVertexCount");
        Reject(Make(new(MaterialFaces: [])), "materials.count");
        Assert.Equal(new[] { 0, 3, 0 }, Read(Make(new(MaterialFaces: [0, 3, 0])))
            .Materials.Select(material => material.FaceVertexCount));
        Assert.Empty(Read(Make(new(MaterialFaces: [], FaceIndices: []))).Faces);
    }

    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(float.NegativeInfinity)]
    public void VersionMustBeFinite(float version) =>
        Reject(Make(new(Version: version)), "header.version");

    [Fact]
    public void NonSeekableStreamGetsSameDeferredError()
    {
        Probe probe = Make(new(OverrideField: "vertices[0].boneIndices[0]", OverrideIndex: 99));
        using var stream = new NonSeekableStream(probe.Bytes);
        PmxFormatException exception = Assert.Throws<PmxFormatException>(() => PmxReader.Read(stream));
        Assert.Equal("vertices[0].boneIndices[0]", exception.Field);
        Assert.Equal(probe.Offsets[exception.Field], exception.Offset);
    }

    private static PmxModel Read(Probe probe)
    {
        using var stream = new MemoryStream(probe.Bytes);
        return PmxReader.Read(stream);
    }

    private static void Reject(Probe probe, string field)
    {
        PmxFormatException exception = Assert.Throws<PmxFormatException>(() => Read(probe));
        Assert.Equal(field, exception.Field);
        Assert.Equal(probe.Offsets[field], exception.Offset);
        Assert.Contains(field, exception.Message);
        Assert.Contains("offset 0x", exception.Message);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed record Options(int Vertices = 3, int VertexWidth = 1, int SignedWidth = 1,
        int Bones = 1, int Textures = 0, int RigidBodies = 0, int Frames = 2,
        byte WeightType = 0, float Version = 2.1f, bool WriteSoftCount = true,
        int[]? MaterialFaces = null, int[]? FaceIndices = null, int LastFaceVertex = 2,
        byte[]? MorphTypes = null, bool BoneFeatures = false, bool DisplayTargets = false,
        bool Joint = false, bool SoftBody = false, string? OverrideField = null,
        int OverrideIndex = 0, byte[]? TrailingBytes = null, int? MaterialWidth = null, int? MorphWidth = null);

    private sealed record Probe(byte[] Bytes, Dictionary<string, long> Offsets);

    // Small independent writer: offsets come from the writer, never parser internals.
    private static Probe Make(Options? options = null)
    {
        Options o = options ?? new();
        var offsets = new Dictionary<string, long>(StringComparer.Ordinal);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        void Mark(string field) => offsets.Add(field, stream.Position);
        void Text() => writer.Write(0);
        void Floats(int count) { for (int i = 0; i < count; i++) writer.Write(0f); }
        void Index(string field, int value, int width = 0)
        {
            Mark(field);
            if (field == o.OverrideField) value = o.OverrideIndex;
            width = width == 0 ? o.SignedWidth : width;
            if (width == 1) writer.Write(unchecked((byte)value));
            else if (width == 2) writer.Write(unchecked((ushort)value));
            else writer.Write(value);
        }
        writer.Write("PMX "u8); Mark("header.version"); writer.Write(o.Version);
        writer.Write((byte)8); writer.Write((byte)1); writer.Write((byte)0);
        writer.Write((byte)o.VertexWidth);
        int materialWidth = o.MaterialWidth ?? o.SignedWidth;
        int morphWidth = o.MorphWidth ?? o.SignedWidth;
        writer.Write((byte)o.SignedWidth); // texture
        writer.Write((byte)materialWidth);
        writer.Write((byte)o.SignedWidth); // bone
        writer.Write((byte)morphWidth);
        writer.Write((byte)o.SignedWidth); // rigid body
        Text(); Text(); Text(); Text();
        writer.Write(o.Vertices);
        for (int i = 0; i < o.Vertices; i++)
        {
            Floats(8); writer.Write(o.WeightType);
            int slots = o.WeightType switch { 0 => 1, 1 or 3 => 2, _ => 4 };
            for (int slot = 0; slot < slots; slot++) Index($"vertices[{i}].boneIndices[{slot}]", 0);
            if (o.WeightType is 1 or 3) writer.Write(0.5f);
            if (o.WeightType is 2 or 4) { for (int slot = 0; slot < 4; slot++) writer.Write(0.25f); }
            if (o.WeightType == 3) Floats(9);
            writer.Write(1f);
        }
        int[] faces = o.FaceIndices ?? [0, 1, o.LastFaceVertex];
        writer.Write(faces.Length);
        for (int i = 0; i < faces.Length; i++) Index($"faces[{i / 3}].vertexIndices[{i % 3}]", faces[i], o.VertexWidth);
        writer.Write(o.Textures); for (int i = 0; i < o.Textures; i++) Text();
        int[] materialFaces = o.MaterialFaces ?? [3];
        Mark("materials.count"); writer.Write(materialFaces.Length);
        for (int i = 0; i < materialFaces.Length; i++)
        {
            string field = $"materials[{i}]";
            Text(); Text(); Floats(11); writer.Write((byte)0); Floats(5);
            Index(field + ".textureIndex", -1); Index(field + ".sphereTextureIndex", -1);
            writer.Write((byte)0); writer.Write((byte)0); Index(field + ".toonTextureIndex", -1);
            Text(); Mark(field + ".faceVertexCount"); writer.Write(materialFaces[i]);
        }
        writer.Write(o.Bones);
        for (int i = 0; i < o.Bones; i++)
        {
            string field = $"bones[{i}]";
            Text(); Text(); Floats(3); Index(field + ".parentIndex", -1); writer.Write(0);
            bool extra = o.BoneFeatures && i == 0;
            writer.Write((ushort)(extra ? 0x0121 : 0));
            if (!extra) Floats(3);
            else
            {
                Index(field + ".linkBoneIndex", -1); Index(field + ".appendBoneIndex", 0); writer.Write(1f);
                Index(field + ".ikTargetBoneIndex", 0); writer.Write(1); writer.Write(1f); writer.Write(1);
                Index(field + ".ikLinks[0].boneIndex", 0); writer.Write((byte)0);
            }
        }
        byte[] morphTypes = o.MorphTypes ?? [];
        writer.Write(morphTypes.Length);
        for (int i = 0; i < morphTypes.Length; i++)
        {
            string field = $"morphs[{i}]";
            byte type = morphTypes[i];
            Text(); Text(); writer.Write((byte)4); writer.Write(type);
            Mark(field + ".count"); writer.Write(1);
            switch (type)
            {
                case 0: case 9:
                    Index(field + (type == 0 ? ".groupOffsets[0].morphIndex" : ".flipOffsets[0].morphIndex"), 0, morphWidth);
                    writer.Write(1f); break;
                case 1: Index(field + ".positionOffsets[0].vertexIndex", 0, o.VertexWidth); Floats(3); break;
                case 2: Index(field + ".boneOffsets[0].boneIndex", 0); Floats(7); break;
                case >= 3 and <= 7:
                    Index(field + ".uvOffsets[0].vertexIndex", 0, o.VertexWidth); Floats(4); break;
                case 8: Index(field + ".materialOffsets[0].materialIndex", -1, materialWidth); writer.Write((byte)1); Floats(28); break;
                case 10: Index(field + ".impulseOffsets[0].rigidBodyIndex", 0); writer.Write((byte)0); Floats(6); break;
                default: throw new InvalidOperationException("Unsupported test morph");
            }
        }
        Mark("displayFrames.count"); writer.Write(o.Frames);
        for (int i = 0; i < o.Frames; i++)
        {
            Text(); Text(); writer.Write((byte)(i < 2 ? 1 : 0));
            bool targets = o.DisplayTargets && i == 0;
            writer.Write(targets ? 2 : 0);
            if (targets)
            {
                writer.Write((byte)0); Index("displayFrames[0].targets[0].index", 0);
                writer.Write((byte)1); Index("displayFrames[0].targets[1].index", 0, morphWidth);
            }
        }
        writer.Write(o.RigidBodies);
        for (int i = 0; i < o.RigidBodies; i++)
        {
            Text(); Text(); Index($"rigidBodies[{i}].boneIndex", -1);
            writer.Write((byte)0); writer.Write((ushort)0); writer.Write((byte)0);
            Floats(14); writer.Write((byte)0);
        }
        writer.Write(o.Joint ? 1 : 0);
        if (o.Joint)
        {
            Text(); Text(); writer.Write((byte)0);
            Index("joints[0].rigidBodyAIndex", -1); Index("joints[0].rigidBodyBIndex", -1); Floats(24);
        }
        if (o.WriteSoftCount)
        {
            Mark("softBodies.count"); writer.Write(o.SoftBody ? 1 : 0);
            if (o.SoftBody)
            {
                Text(); Text(); writer.Write((byte)0); Index("softBodies[0].materialIndex", 0, materialWidth);
                writer.Write((byte)0); writer.Write((ushort)0); writer.Write((byte)0);
                writer.Write(0); writer.Write(0); Floats(2); writer.Write(0);
                Floats(18); for (int i = 0; i < 4; i++) writer.Write(0); Floats(3);
                writer.Write(1); Index("softBodies[0].anchors[0].rigidBodyIndex", 0);
                Index("softBodies[0].anchors[0].vertexIndex", 0, o.VertexWidth); writer.Write((byte)0);
                writer.Write(1); Index("softBodies[0].pinVertexIndices[0]", 0, o.VertexWidth);
            }
        }
        else Mark("softBodies.count");
        // PMX 2.0 must stop before any 2.1 tail; 2.1 stops after its soft bodies.
        offsets["model.end"] = o.Version == 2.0f ? offsets["softBodies.count"] : stream.Position;
        if (o.TrailingBytes is not null) writer.Write(o.TrailingBytes);
        var probe = new Probe(stream.ToArray(), offsets);
        string? artifactDirectory = Environment.GetEnvironmentVariable("MMD_PMX_BOUNDARY_ARTIFACTS");
        if (!string.IsNullOrEmpty(artifactDirectory))
        {
            Directory.CreateDirectory(artifactDirectory);
            string name = Convert.ToHexString(SHA256.HashData(probe.Bytes)).ToLowerInvariant();
            File.WriteAllBytes(Path.Combine(artifactDirectory, name + ".pmx"), probe.Bytes);
            File.WriteAllText(Path.Combine(artifactDirectory, name + ".json"),
                JsonSerializer.Serialize(new { options = o, offsets }, new JsonSerializerOptions
                { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        }
        return probe;
    }
}
