using System.Numerics;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class PmxStreamingWriterTests
{
    [Fact]
    public void StreamingRichFixtureMatchesLegacyBytesOnNonseekDestination()
    {
        byte[] original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rich-w1-uv4.pmx"));
        using var source = new MemoryStream(original, writable: false);
        PmxModel model = PmxReader.Read(source);
        using var destination = new WriteOnlyStream();
        PmxWriter.Write(destination, model, new());
        Assert.Equal(original, destination.Bytes);
        Assert.Equal(PmxWriter.ToBytes(model), destination.Bytes);
        Assert.False(destination.Disposed);
    }

    [Theory]
    [InlineData(PmxTextEncoding.Utf8)]
    [InlineData(PmxTextEncoding.Utf16LittleEndian)]
    public void UnicodeAcrossEncodingChunksMatchesLegacy(PmxTextEncoding encoding)
    {
        string prefix = new('中', encoding == PmxTextEncoding.Utf8 ? 1365 : 2047);
        PmxModel model = Model(name: prefix + "😀" + new string('a', 2048), encoding: encoding);
        using var destination = new WriteOnlyStream();
        PmxWriter.Write(destination, model, new());
        Assert.Equal(PmxWriter.ToBytes(model), destination.Bytes);
    }

    [Fact]
    public void BudgetCountsOnlyNewBytesAndAllowsExactLimitBeyondIntPositions()
    {
        PmxModel model = Model();
        int expected = PmxWriter.ToBytes(model).Length;
        using var destination = new WriteOnlyStream(reportedPosition: (long)int.MaxValue + 100);
        PmxWriter.Write(destination, model, new() { MaxOutputBytes = expected });
        Assert.Equal(expected, destination.Bytes.Length);
        using var largeBudgetDestination = new WriteOnlyStream();
        PmxWriter.Write(largeBudgetDestination, model, new() { MaxOutputBytes = long.MaxValue });
        Assert.Equal(destination.Bytes, largeBudgetDestination.Bytes);
        Assert.Equal(10L * 1024 * 1024 * 1024, new PmxWriteOptions().MaxOutputBytes);
    }

    [Fact]
    public void BudgetRejectsBeforeTouchingDestination()
    {
        PmxModel model = Model();
        using var destination = new MemoryStream();
        destination.Write([1, 2, 3]);
        destination.Position = 1;
        Assert.Throws<PmxFormatException>(() => PmxWriter.Write(destination, model,
            new() { MaxOutputBytes = PmxWriter.ToBytes(model).LongLength - 1 }));
        Assert.Equal(1, destination.Position);
        Assert.Equal(new byte[] { 1, 2, 3 }, destination.ToArray());
    }

    [Fact]
    public void CountingPreflightCrossesTwoGiBWithoutCreatingSerializedBytes()
    {
        // One shared 2 MiB string backing 2 GiB of logical UTF-8 output. The destination
        // fails on its first write, so the large output is only measured, never emitted.
        string shared = new('a', 1024 * 1024);
        var textures = Enumerable.Repeat(shared, 2048).ToArray();
        PmxModel model = Model(textures: textures);
        using var destination = new WriteOnlyStream(failAfter: 0);
        Assert.Throws<PmxFormatException>(() => PmxWriter.Write(destination, model,
            new() { MaxOutputBytes = int.MaxValue }));
        IOException failure = Assert.Throws<IOException>(() => PmxWriter.Write(destination, model,
            new() { MaxOutputBytes = 73L + textures.LongLength * (shared.Length + 4L) }));
        Assert.Same(destination.Failure, failure);
        Assert.Empty(destination.Bytes);
        Assert.False(destination.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidBudgetDoesNotWrite(long budget)
    {
        using var destination = new WriteOnlyStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => PmxWriter.Write(destination, Model(), new() { MaxOutputBytes = budget }));
        Assert.Empty(destination.Bytes);
        Assert.False(destination.Disposed);
    }

    [Theory]
    [InlineData("unicode")]
    [InlineData("version")]
    [InlineData("headerSize")]
    [InlineData("indexWidth")]
    [InlineData("encoding")]
    [InlineData("additionalUv")]
    [InlineData("nul")]
    [InlineData("nonFinite")]
    [InlineData("unusedWeight")]
    [InlineData("unusedMorph")]
    [InlineData("vertexBone")]
    [InlineData("face")]
    [InlineData("materialCoverage")]
    [InlineData("bone")]
    [InlineData("morph")]
    [InlineData("display")]
    [InlineData("rigidBody")]
    [InlineData("joint")]
    [InlineData("softBody")]
    public void InvalidModelsDoNotTouchDestination(string invalid)
    {
        PmxModel model = Model(invalid);
        using var destination = new MemoryStream();
        destination.Write([1, 2, 3]);
        destination.Position = 1;
        Assert.Throws<PmxFormatException>(() => PmxWriter.Write(destination, model, new()));
        Assert.Equal(1, destination.Position);
        Assert.Equal(new byte[] { 1, 2, 3 }, destination.ToArray());
    }

    [Fact]
    public void DestinationIoFailureLeavesCallerOpenAndPropagatesOriginalException()
    {
        using var destination = new WriteOnlyStream(failAfter: 20);
        IOException exception = Assert.Throws<IOException>(() => PmxWriter.Write(destination, Model(), new()));
        Assert.Same(destination.Failure, exception);
        Assert.False(destination.Disposed);
        Assert.InRange(destination.Bytes.Length, 1, 20);
    }

    [Fact]
    public void NullOptionsAndReadOnlyDestinationFailWithoutWriting()
    {
        using var destination = new WriteOnlyStream();
        Assert.Throws<ArgumentNullException>(() => PmxWriter.Write(destination, Model(), null!));
        Assert.Empty(destination.Bytes);
        using var readOnly = new MemoryStream([1, 2, 3], writable: false);
        Assert.Throws<ArgumentException>(() => PmxWriter.Write(readOnly, Model(), new()));
        Assert.Equal(0, readOnly.Position);
    }

    private static PmxModel Model(string? invalid = null, string name = "", PmxTextEncoding encoding = PmxTextEncoding.Utf8,
        IReadOnlyList<string>? textures = null) => new()
    {
        Header = new() { Version = invalid == "version" ? 3 : 2.1f, HeaderDataSize = invalid == "headerSize" ? 9 : 8,
            TextEncoding = invalid == "encoding" ? (PmxTextEncoding)9 : encoding, AdditionalUvCount = invalid == "additionalUv" ? 5 : 0,
            VertexIndexSize = invalid == "indexWidth" ? 3 : 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = invalid switch { "unicode" => "\uD800", "nul" => "a\0b", _ => name }, EnglishName = "", Comment = "", EnglishComment = "",
        Vertices = invalid is "vertexBone" or "materialCoverage" or "nonFinite" or "unusedWeight" ? [new() {
            Position = invalid == "nonFinite" ? new(float.NaN, 0, 0) : default, Normal = Vector3.UnitY, Uv = default,
            AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [invalid == "vertexBone" ? 0 : -1, -1, -1, -1],
            BoneWeights = [1, invalid == "unusedWeight" ? 1 : 0, 0, 0], EdgeScale = 1 }] : [],
        Faces = invalid is "face" or "materialCoverage" ? [new(0, 0, 0)] : [], Textures = textures ?? [], Materials = [],
        Bones = invalid == "bone" ? [new() { Name = "", EnglishName = "", Position = default, ParentIndex = 2, DeformDepth = 0, Flags = default }] : [],
        Morphs = invalid is "morph" or "unusedMorph" ? [new() { Name = "", EnglishName = "", ControlPanel = 0,
            Type = PmxMorphType.Group, GroupOffsets = invalid == "morph" ? [new(2, 1)] : [],
            PositionOffsets = invalid == "unusedMorph" ? [new(0, Vector3.One)] : [] }] : [],
        DisplayFrames = invalid == "display" ? [new() { Name = "", EnglishName = "", Type = (PmxDisplayFrameType)3, Targets = [] }] : [],
        RigidBodies = invalid == "rigidBody" ? [new() { Name = "", EnglishName = "", BoneIndex = -1, Group = 16,
            CollisionGroup = 0, Shape = default, ShapeSize = default, Translation = default, Rotation = default,
            Mass = 0, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = default }] : [],
        Joints = invalid == "joint" ? [new() { Name = "", EnglishName = "", Type = (PmxJointType)6,
            RigidBodyAIndex = -1, RigidBodyBIndex = -1, Translation = default, Rotation = default,
            TranslationLowerLimit = default, TranslationUpperLimit = default, RotationLowerLimit = default,
            RotationUpperLimit = default, SpringTranslationFactor = default, SpringRotationFactor = default }] : [],
        SoftBodies = invalid == "softBody" ? [new() { Name = "", EnglishName = "", Type = (PmxSoftBodyType)2,
            MaterialIndex = -1, Group = 0, CollisionGroup = 0, Flags = default, BLinkLength = 0, ClusterCount = 0,
            TotalMass = 0, CollisionMargin = 0, AeroModel = 0, Config = default, ClusterConfig = default,
            Iterations = default, MaterialConfig = default, Anchors = [], PinVertexIndices = [] }] : [],
    };

    private sealed class WriteOnlyStream(long? reportedPosition = null, int failAfter = int.MaxValue) : Stream
    {
        private readonly MemoryStream _bytes = new();
        public byte[] Bytes => _bytes.ToArray();
        public bool Disposed { get; private set; }
        public IOException Failure { get; } = new("injected destination failure");
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !Disposed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => reportedPosition ?? throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_bytes.Length + buffer.Length > failAfter) throw Failure;
            _bytes.Write(buffer);
        }
        public override void Flush() => throw new InvalidOperationException("Writer must not flush caller-owned output.");
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; if (disposing) _bytes.Dispose(); base.Dispose(disposing); }
    }
}
