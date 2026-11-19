using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class PmxWriterTests
{
    // Independent PMX 2.0 literal: 17-byte header, four empty strings,
    // nine empty sections. No serializer or fixture builder supplies these bytes.
    private static byte[] EmptyLiteral => Convert.FromHexString(
        "504D582000000040080100010101010101" +
        "00000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000");

    [Fact]
    public void EmptyModelMatchesIndependentLiteralAndLeavesCallerStreamOpen()
    {
        PmxModel model = Read(EmptyLiteral);
        using var output = new MemoryStream();
        output.WriteByte(0xCC);
        PmxWriter.Write(output, model);
        Assert.Equal(new byte[] { 0xCC }.Concat(EmptyLiteral), output.ToArray());
        Assert.True(output.CanWrite);
        Assert.Equal(69, EmptyLiteral.Length);
    }

    [Fact]
    public void RichFixturePreservesAllRepresentedSectionsAndOriginalBytes()
    {
        byte[] original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "rich-w1-uv4.pmx"));
        PmxModel model = Read(original);
        string before = JsonSerializer.Serialize(model);
        byte[] exported = PmxWriter.ToBytes(model);
        Assert.Equal(before, JsonSerializer.Serialize(Read(exported)));
        Assert.Equal(before, JsonSerializer.Serialize(model));
        Assert.Equal(original, exported);
        Assert.Contains(model.Vertices, x => x.WeightType == PmxVertexWeightType.Qdef);
        Assert.Equal(8, model.Morphs.Select(x => x.Type).Distinct().Count());
        Assert.NotEmpty(model.SoftBodies);
    }

    [Theory]
    [InlineData(PmxMorphType.AdditionalUv1)]
    [InlineData(PmxMorphType.AdditionalUv2)]
    [InlineData(PmxMorphType.AdditionalUv3)]
    [InlineData(PmxMorphType.AdditionalUv4)]
    public void EachAdditionalUvMorphChannelRetainsAllFourComponents(PmxMorphType type)
    {
        var rich = Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rich-w1-uv4.pmx")));
        var model = Copy(rich, morphs: rich.Morphs.Append(new PmxMorph { Name = "channel", EnglishName = "", ControlPanel = 0, Type = type,
            UvOffsets = [new(0, new(.1f, -.2f, .3f, -.4f))] }).ToArray());
        var restored = Read(PmxWriter.ToBytes(model));
        Assert.Equal(type, restored.Morphs[^1].Type);
        Assert.Equal(model.Morphs[^1].UvOffsets, restored.Morphs[^1].UvOffsets);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void UnicodeNamesUseExplicitEncodingAndByteLengths(byte encoding)
    {
        PmxModel source = Read(EmptyLiteral);
        PmxModel model = Copy(source, header: Header(encoding: (PmxTextEncoding)encoding), name: "中😀");
        byte[] bytes = PmxWriter.ToBytes(model);
        Assert.Equal(encoding, bytes[9]);
        Assert.Equal("中😀", Read(bytes).Name);
        Assert.Equal(encoding == 0 ? 6 : 7, BitConverter.ToInt32(bytes, 17));
    }

    [Theory]
    [InlineData(1, 256, 255)]
    [InlineData(2, 65536, 65535)]
    [InlineData(4, 257, 256)]
    public void VertexIndexHighBitIsUnsigned(int width, int count, int index)
    {
        PmxModel empty = Read(EmptyLiteral);
        var vertices = Enumerable.Repeat(Vertex(), count).ToArray();
        var material = Material();
        PmxModel model = Copy(empty, header: Header(vertexWidth: width), vertices: vertices,
            faces: [new(0, 0, index)], materials: [material]);
        Assert.Equal(index, Read(PmxWriter.ToBytes(model)).Faces[0].C);
    }

    [Theory]
    [InlineData(1, 128)]
    [InlineData(2, 32768)]
    public void SignedReferenceOverflowCannotAliasSentinel(int width, int index)
    {
        PmxModel source = Read(EmptyLiteral);
        var bones = Enumerable.Repeat(Bone(), index + 1).ToArray();
        var vertex = Vertex(index);
        PmxModel model = Copy(source, header: Header(boneWidth: width), vertices: [vertex], bones: bones);
        Assert.Contains("boneIndices", Assert.Throws<PmxFormatException>(() => PmxWriter.ToBytes(model)).Field);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("headerSize")]
    [InlineData("indexWidth")]
    [InlineData("encoding")]
    [InlineData("additionalUv")]
    [InlineData("nul")]
    [InlineData("surrogate")]
    [InlineData("nonFinite")]
    [InlineData("badIndex")]
    [InlineData("unusedWeight")]
    [InlineData("unusedMorph")]
    public void InvalidInputFailsBeforeTouchingCallerStream(string kind)
    {
        PmxModel empty = Read(EmptyLiteral);
        PmxModel model = kind switch
        {
            "version" => Copy(empty, header: Header(version: 3)),
            "headerSize" => Copy(empty, header: Header(headerSize: 9)),
            "indexWidth" => Copy(empty, header: Header(vertexWidth: 3)),
            "encoding" => Copy(empty, header: Header(encoding: (PmxTextEncoding)9)),
            "additionalUv" => Copy(empty, header: Header(additionalUv: 5)),
            "nul" => Copy(empty, name: "a\0b"),
            "surrogate" => Copy(empty, name: "\uD800"),
            "nonFinite" => Copy(empty, vertices: [Vertex(position: new(float.NaN, 0, 0))]),
            "badIndex" => Copy(empty, vertices: [Vertex(8)]),
            "unusedWeight" => Copy(empty, vertices: [Vertex(weights: [1, 1, 0, 0])]),
            "unusedMorph" => Copy(empty, morphs: [new PmxMorph
            {
                Name = "x", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Group,
                PositionOffsets = [new(0, Vector3.One)],
            }]),
            _ => throw new InvalidOperationException(),
        };
        using var output = new MemoryStream();
        output.Write([1, 2, 3]);
        output.Position = 1;
        Assert.Throws<PmxFormatException>(() => PmxWriter.Write(output, model));
        Assert.Equal(1, output.Position);
        Assert.Equal(new byte[] { 1, 2, 3 }, output.ToArray());
    }

    [Fact]
    public void Version20RefusesQdefFlipImpulseAndSoftBodyInsteadOfDroppingThem()
    {
        PmxModel rich = Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "rich-w1-uv4.pmx")));
        PmxModel downgraded = Copy(rich, header: Header());
        Assert.Throws<PmxFormatException>(() => PmxWriter.ToBytes(downgraded));
        var empty = Read(EmptyLiteral);
        foreach (PmxMorphType type in new[] { PmxMorphType.Flip, PmxMorphType.Impulse })
        {
            Assert.Throws<PmxFormatException>(() => PmxWriter.ToBytes(Copy(empty,
                morphs: [new() { Name = "", EnglishName = "", ControlPanel = 0, Type = type }])));
        }
        Assert.Throws<PmxFormatException>(() => PmxWriter.ToBytes(Copy(empty, softBodies: rich.SoftBodies)));
    }

    [Fact]
    public void NearVersionFloatIsNormalizedAndVersion20WithQdefIsReadAsWritable21()
    {
        PmxModel empty = Read(EmptyLiteral);
        // 2.0999997 used to load as a non-2.1 model (soft bodies skipped) that the writer then refused.
        byte[] near = PmxWriter.ToBytes(Copy(empty, header: Header(version: 2.1f)));
        BitConverter.TryWriteBytes(near.AsSpan(4, 4), BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(2.1f) - 1));
        Assert.Equal(2.1f, Read(near).Header.Version);
        // A file labelled 2.0 that uses QDEF is accepted for compatibility and recorded as 2.1, so it can be saved.
        PmxModel rich = Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rich-w1-uv4.pmx")));
        byte[] labelled20 = PmxWriter.ToBytes(rich);
        BitConverter.TryWriteBytes(labelled20.AsSpan(4, 4), 2.0f);
        // The soft-body section (its trailing int32 count) exists only in 2.1, so remove it for a true 2.0 layout.
        labelled20 = PmxWriter.ToBytes(Copy(rich, softBodies: []))[..^4];
        BitConverter.TryWriteBytes(labelled20.AsSpan(4, 4), 2.0f);
        PmxModel read = Read(labelled20);
        Assert.Equal(2.1f, read.Header.Version);
        Assert.NotEmpty(PmxWriter.ToBytes(read));
    }

    [Fact]
    public void NonFiniteFloatsAreRejectedAtReadTimeWithTheirField()
    {
        PmxModel empty = Read(EmptyLiteral);
        byte[] bytes = PmxWriter.ToBytes(Copy(empty, vertices: [Vertex(position: new(1.5f, 0, 0))]));
        int x = -1;
        for (int i = 0; i + 4 <= bytes.Length; i++) if (BitConverter.ToSingle(bytes, i) == 1.5f) { x = i; break; }
        Assert.True(x > 0);
        BitConverter.TryWriteBytes(bytes.AsSpan(x, 4), float.NaN);
        var error = Assert.Throws<PmxFormatException>(() => Read(bytes));
        Assert.Contains("vertices[0].position", error.Message);
    }

    [Fact]
    public void MaterialMorphCountIsBoundedByItsRealElementSizeBeforeAllocating()
    {
        PmxModel empty = Read(EmptyLiteral);
        var material = new PmxMaterial
        {
            Name = "", EnglishName = "", Diffuse = Vector4.One, Specular = default, SpecularPower = 0, Ambient = default,
            DrawMode = default, EdgeColor = default, EdgeSize = 0, TextureIndex = -1, SphereTextureIndex = -1,
            SphereMode = default, ToonMode = default, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3,
        };
        var morph = new PmxMorph
        {
            Name = "m", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Material,
            MaterialOffsets = [new(0, PmxMaterialMorphOperation.Multiply, Vector4.One, Vector3.One, 1, Vector3.One,
                Vector4.One, 1, Vector4.One, Vector4.One, Vector4.One)],
        };
        byte[] bytes = PmxWriter.ToBytes(Copy(empty, vertices: [Vertex(), Vertex(), Vertex()], faces: [new(0, 1, 2)],
            materials: [material], morphs: [morph]));
        Assert.Single(Read(bytes).Morphs[0].MaterialOffsets);
        // The offset count is the int32 right after the type byte 8 that follows the "m" name and control panel.
        int count = -1;
        for (int i = 0; i + 4 < bytes.Length; i++)
            if (bytes[i] == 1 && bytes[i + 1] == 8 && BitConverter.ToInt32(bytes, i + 2) == 1) { count = i + 2; break; }
        Assert.True(count > 0);
        // 20 offsets need ~2.3 KB; the remaining input holds one. The old 2-byte floor accepted this count.
        BitConverter.TryWriteBytes(bytes.AsSpan(count, 4), 20);
        var error = Assert.Throws<PmxFormatException>(() => Read(bytes));
        // The count itself is rejected before allocating; the old 2-byte floor failed later on morphs[0] offsets.
        Assert.Equal("morphs[0].count", error.Field);
    }

    [Theory]
    [InlineData(0xFF, true)]
    [InlineData(0x0A, false)]
    public void CommonToonNoneByteNormalizesToSeparateNoneAndOtherOutOfRangeBytesFailAtReadTime(byte value, bool accepted)
    {
        PmxModel empty = Read(EmptyLiteral);
        var material = new PmxMaterial
        {
            Name = "", EnglishName = "", Diffuse = Vector4.One, Specular = default,
            SpecularPower = 0, Ambient = default, DrawMode = default, EdgeColor = default,
            EdgeSize = 0, TextureIndex = -1, SphereTextureIndex = -1, SphereMode = default,
            ToonMode = PmxToonMode.Common, ToonTextureIndex = 7, Memo = "", FaceVertexCount = 3,
        };
        PmxModel model = Copy(empty, vertices: [Vertex(), Vertex(), Vertex()], faces: [new(0, 1, 2)], materials: [material]);
        byte[] bytes = PmxWriter.ToBytes(model);
        // The common toon byte is the only 0x07 that directly follows the toon-mode byte 0x01.
        int offset = -1;
        for (int i = 1; i < bytes.Length; i++) if (bytes[i - 1] == 0x01 && bytes[i] == 0x07) { Assert.Equal(-1, offset); offset = i; }
        Assert.True(offset > 0);
        bytes[offset] = value;
        if (!accepted)
        {
            var error = Assert.Throws<PmxFormatException>(() => Read(bytes));
            Assert.Contains("toonTextureIndex", error.Message);
            return;
        }
        PmxMaterial read = Read(bytes).Materials[0];
        Assert.Equal(PmxToonMode.Separate, read.ToonMode);
        Assert.Equal(-1, read.ToonTextureIndex);
        // The normalized model can be written back (previously "common toon must be 0-9").
        Assert.Equal(-1, Read(PmxWriter.ToBytes(Read(bytes))).Materials[0].ToonTextureIndex);
    }

    private static PmxModel Read(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        return PmxReader.Read(input);
    }

    private static PmxHeader Header(float version = 2, int headerSize = 8,
        PmxTextEncoding encoding = PmxTextEncoding.Utf8, int additionalUv = 0,
        int vertexWidth = 1, int boneWidth = 1) => new()
    {
        Version = version, HeaderDataSize = headerSize, TextEncoding = encoding,
        AdditionalUvCount = additionalUv, VertexIndexSize = vertexWidth, BoneIndexSize = boneWidth,
        TextureIndexSize = 1, MaterialIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1,
    };

    private static PmxVertex Vertex(int bone = -1, Vector3 position = default, float[]? weights = null) => new()
    {
        Position = position, Normal = Vector3.UnitY, Uv = default, AdditionalUvs = [],
        WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [bone, -1, -1, -1],
        BoneWeights = weights ?? [1, 0, 0, 0], EdgeScale = 1,
    };

    private static PmxBone Bone() => new()
    {
        Name = "", EnglishName = "", Position = default, ParentIndex = -1, DeformDepth = 0,
        Flags = PmxBoneFlags.None,
    };

    private static PmxMaterial Material() => new()
    {
        Name = "", EnglishName = "", Diffuse = Vector4.One, Specular = default,
        SpecularPower = 0, Ambient = default, DrawMode = default, EdgeColor = default,
        EdgeSize = 0, TextureIndex = -1, SphereTextureIndex = -1, SphereMode = default,
        ToonMode = default, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3,
    };

    private static PmxModel Copy(PmxModel x, PmxHeader? header = null, string? name = null,
        IReadOnlyList<PmxVertex>? vertices = null, IReadOnlyList<PmxTriangle>? faces = null,
        IReadOnlyList<PmxMaterial>? materials = null, IReadOnlyList<PmxBone>? bones = null,
        IReadOnlyList<PmxMorph>? morphs = null, IReadOnlyList<PmxSoftBody>? softBodies = null) => new()
    {
        Header = header ?? x.Header, Name = name ?? x.Name, EnglishName = x.EnglishName,
        Comment = x.Comment, EnglishComment = x.EnglishComment, Vertices = vertices ?? x.Vertices,
        Faces = faces ?? x.Faces, Textures = x.Textures, Materials = materials ?? x.Materials,
        Bones = bones ?? x.Bones, Morphs = morphs ?? x.Morphs, DisplayFrames = x.DisplayFrames,
        RigidBodies = x.RigidBodies, Joints = x.Joints, SoftBodies = softBodies ?? x.SoftBodies,
    };
}
