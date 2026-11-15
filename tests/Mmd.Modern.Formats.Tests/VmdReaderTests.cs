using System.Numerics;
using System.Text;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class VmdReaderTests
{
    [Fact]
    public void ReadsCp932NamesRawXyzwAndAllInterpolationBytes()
    {
        byte[] interpolation = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        using MemoryStream fixture = Build(writer =>
        {
            writer.Write(1u);
            Bone(writer, "あいうえおかきA", 7, new(1, -2, 3), new(.1f, -.2f, .3f, .9f), interpolation);
            writer.Write(1u);
            Morph(writer, "笑い", 11, 1.25f);
        });

        VmdMotion motion = VmdReader.Read(fixture);

        Assert.Equal("初音ミク", motion.ModelName);
        VmdBoneKeyframe bone = Assert.Single(motion.BoneKeyframes);
        Assert.Equal("あいうえおかきA", bone.BoneName);
        Assert.Equal(7u, bone.Frame);
        Assert.Equal(new Vector3(1, -2, 3), bone.Translation);
        Assert.Equal(new Quaternion(.1f, -.2f, .3f, .9f), bone.Rotation);
        Assert.Equal(interpolation, bone.Interpolation);
        VmdMorphKeyframe morph = Assert.Single(motion.MorphKeyframes);
        Assert.Equal("笑い", morph.MorphName);
        Assert.Equal(11u, morph.Frame);
        Assert.Equal(1.25f, morph.Weight);
        Assert.Empty(motion.Diagnostics);
        Assert.True(fixture.CanRead);
    }

    [Fact]
    public void PreservesDuplicateFramesAndFileOrder()
    {
        using MemoryStream fixture = Build(writer =>
        {
            writer.Write(3u);
            foreach (uint frame in new uint[] { 9, 1, 9 })
                Bone(writer, "腕", frame, new(frame, 0, 0), Quaternion.Identity);
            writer.Write(3u);
            foreach (uint frame in new uint[] { 3, 0, 3 })
                Morph(writer, "笑い", frame, frame / 10f);
        });

        VmdMotion motion = VmdReader.Read(fixture);

        Assert.Equal(new uint[] { 9, 1, 9 }, motion.BoneKeyframes.Select(k => k.Frame));
        Assert.Equal(new uint[] { 3, 0, 3 }, motion.MorphKeyframes.Select(k => k.Frame));
        Assert.Equal(new float[] { 9, 1, 9 }, motion.BoneKeyframes.Select(k => k.Translation.X));
    }

    [Fact]
    public void CopiesInterpolationIntoReadOnlySnapshot()
    {
        byte[] source = new byte[64];
        var key = new VmdBoneKeyframe { Interpolation = source };
        source[0] = 99;
        Assert.Equal(0, key.Interpolation[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<byte>)key.Interpolation)[0] = 1);
        Assert.Throws<ArgumentException>(() => new VmdBoneKeyframe { Interpolation = new byte[63] });
    }

    [Fact]
    public void IgnoresFixedNameBytesAfterFirstNul()
    {
        using MemoryStream fixture = Build(writer =>
        {
            writer.Write(1u);
            writer.Write(new byte[] { (byte)'A', 0, 0x81, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff });
            writer.Write(0u);
            writer.Write(new byte[12]);
            writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
            writer.Write(new byte[64]);
        });
        Assert.Equal("A", Assert.Single(VmdReader.Read(fixture).BoneKeyframes).BoneName);
    }

    [Theory]
    [InlineData("Vocaloid Motion Data 0003")]
    [InlineData("Vocaloid Motion Data")]
    [InlineData("not a vmd")]
    public void RejectsUnsupportedHeader(string signature)
    {
        using MemoryStream fixture = Build(writer => writer.Write(0u), signature: signature);
        AssertError(fixture, "header.version", 0);
    }

    [Fact]
    public void RejectsTruncatedHeader()
    {
        using var fixture = new MemoryStream(new byte[29]);
        AssertError(fixture, "header.signature", 0);
    }

    [Fact]
    public void RejectsInvalidCp932Name()
    {
        using MemoryStream fixture = Build(writer => writer.Write(0u));
        fixture.GetBuffer()[30] = 0x81;
        fixture.GetBuffer()[31] = 0;
        AssertError(fixture, "header.modelName", 30);
    }

    [Theory]
    [InlineData(uint.MaxValue)]
    [InlineData(1u)]
    public void RejectsImpossibleBoneCountBeforeAllocation(uint count)
    {
        using MemoryStream fixture = Build(writer => writer.Write(count));
        AssertError(fixture, "bones.count", 50);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void RejectsNonFiniteTranslation(float value)
    {
        using MemoryStream fixture = Build(writer =>
        {
            writer.Write(1u);
            Bone(writer, "腕", 0, new(0, value, 0), Quaternion.Identity);
        });
        AssertError(fixture, "bones[0].translation.y", 77);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.NegativeInfinity)]
    public void RejectsNonFiniteQuaternion(float value)
    {
        using MemoryStream fixture = Build(writer =>
        {
            writer.Write(1u);
            Bone(writer, "腕", 0, Vector3.Zero, new(value, 0, 0, 1));
        });
        AssertError(fixture, "bones[0].rotation.x", 85);
    }

    [Fact]
    public void ZeroQuaternionIsReadAsIdentityAndReported()
    {
        using MemoryStream fixture = Build(writer =>
        {
            writer.Write(1u);
            Bone(writer, "腕", 0, Vector3.Zero, new());
        });
        VmdMotion motion = VmdReader.Read(fixture);
        Assert.Equal(Quaternion.Identity, Assert.Single(motion.BoneKeyframes).Rotation);
        Assert.Contains(motion.Diagnostics, d => d.Contains("bones[0].rotation") && d.Contains("identity"));
    }

    [Fact]
    public void NameTruncatedInsideADoubleByteCharacterKeepsTheCompletePrefix()
    {
        using MemoryStream fixture = Build(writer =>
        {
            writer.Write(1u);
            Bone(writer, "腕", 0, Vector3.Zero, Quaternion.Identity);
        });
        byte[] buffer = fixture.GetBuffer();
        // Fill the 15-byte bone name with seven full-width characters plus a dangling lead byte.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] seven = Encoding.GetEncoding(932).GetBytes("右腕捩り先端Ａ");
        Assert.Equal(14, seven.Length);
        seven.CopyTo(buffer, 54); buffer[68] = 0x81;
        VmdMotion motion = VmdReader.Read(fixture);
        Assert.Equal("右腕捩り先端Ａ", Assert.Single(motion.BoneKeyframes).BoneName);
        Assert.Contains(motion.Diagnostics, d => d.Contains("bones[0].name"));
    }

    [Fact]
    public void RejectsNonFiniteMorphWeight()
    {
        using MemoryStream fixture = Build(writer =>
        {
            writer.Write(0u); writer.Write(1u);
            Morph(writer, "笑い", 0, float.NaN);
        });
        AssertError(fixture, "morphs[0].weight", 77);
    }

    [Fact]
    public void ValidatesAndReportsAllNonPlaybackTailSections()
    {
        using MemoryStream fixture = Build(writer =>
        {
            writer.Write(0u); writer.Write(0u);
            writer.Write(1u); // Camera: frame, distance, interest, rotation, curve, angle, perspective.
            writer.Write(3u); writer.Write(-12f); writer.Write(new byte[24]);
            writer.Write(new byte[24]); writer.Write(45u); writer.Write((byte)0);
            writer.Write(1u); writer.Write(3u); writer.Write(new byte[24]); // Light.
            writer.Write(1u); writer.Write(3u); writer.Write((byte)2); writer.Write(1f); // Shadow.
            writer.Write(1u); writer.Write(3u); writer.Write((byte)1); writer.Write(1u);
            Name(writer, "左足ＩＫ", 20); writer.Write((byte)1);
        });

        VmdMotion motion = VmdReader.Read(fixture);

        Assert.Equal(4, motion.Diagnostics.Count);
        Assert.Contains(motion.Diagnostics, d => d.Contains("camera") && d.Contains("1") && d.Contains("not played"));
        Assert.Contains(motion.Diagnostics, d => d.Contains("light") && d.Contains("1"));
        Assert.Contains(motion.Diagnostics, d => d.Contains("shadow") && d.Contains("1"));
        Assert.Contains(motion.Diagnostics, d => d.Contains("IK") && d.Contains("1"));
        Assert.Equal(fixture.Length, fixture.Position);
    }

    [Fact]
    public void RejectsTruncatedOptionalSectionCount()
    {
        using MemoryStream fixture = Build(writer => { writer.Write(0u); writer.Write((byte)1); });
        AssertError(fixture, "morphs.count", 54);
    }

    [Fact]
    public void RejectsNonFiniteCameraInsteadOfSilentlySkipping()
    {
        using MemoryStream fixture = Build(writer =>
        {
            writer.Write(0u); writer.Write(0u); writer.Write(1u);
            writer.Write(0u); writer.Write(float.NaN); writer.Write(new byte[53]);
        });
        AssertError(fixture, "cameras[0].distance", 66);
    }

    [Fact]
    public void RejectsImpossibleNestedIkCount()
    {
        using MemoryStream fixture = Build(writer =>
        {
            for (int i = 0; i < 5; i++) writer.Write(0u);
            writer.Write(1u); writer.Write(0u); writer.Write((byte)1); writer.Write(uint.MaxValue);
        });
        AssertError(fixture, "ik[0].bones.count", 79);
    }

    [Fact]
    public void TrailingBytesAfterKnownSectionsAreIgnoredAndReported()
    {
        using MemoryStream fixture = Build(writer =>
        {
            for (int i = 0; i < 6; i++) writer.Write(0u);
            writer.Write((byte)42);
        });
        VmdMotion motion = VmdReader.Read(fixture);
        Assert.Contains(motion.Diagnostics, d => d.Contains("motion.end") && d.Contains("1 trailing byte"));
        Assert.Equal(fixture.Length, fixture.Position);
    }

    [Fact]
    public void ReadsNonSeekableShortReadsAndLeavesStreamOpen()
    {
        using MemoryStream fixture = Build(writer =>
        {
            writer.Write(1u); Bone(writer, "腕", uint.MaxValue, Vector3.Zero, Quaternion.Identity);
        });
        using var stream = new ShortReadStream(fixture);
        Assert.Equal(uint.MaxValue, Assert.Single(VmdReader.Read(stream).BoneKeyframes).Frame);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void ErrorOffsetsAreRelativeToStartingStreamPosition()
    {
        using var fixture = new MemoryStream();
        fixture.Write(new byte[17]);
        using MemoryStream vmd = Build(writer => writer.Write(1u));
        vmd.CopyTo(fixture);
        fixture.Position = 17;
        AssertError(fixture, "bones.count", 50);
    }

    private static void AssertError(Stream stream, string field, long offset)
    {
        VmdFormatException error = Assert.Throws<VmdFormatException>(() => VmdReader.Read(stream));
        Assert.Equal(field, error.Field);
        Assert.Equal(offset, error.Offset);
        Assert.Contains($"offset 0x{offset:X8}", error.Message);
        Assert.Contains($"field '{field}'", error.Message);
    }

    private static MemoryStream Build(Action<BinaryWriter> body, string signature = "Vocaloid Motion Data 0002")
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            Name(writer, signature, 30);
            Name(writer, "初音ミク", 20);
            body(writer);
        }
        stream.Position = 0;
        return stream;
    }

    private static void Bone(BinaryWriter writer, string name, uint frame, Vector3 translation, Quaternion rotation, byte[]? curve = null)
    {
        Name(writer, name, 15); writer.Write(frame);
        writer.Write(translation.X); writer.Write(translation.Y); writer.Write(translation.Z);
        writer.Write(rotation.X); writer.Write(rotation.Y); writer.Write(rotation.Z); writer.Write(rotation.W);
        writer.Write(curve ?? new byte[64]);
    }

    private static void Morph(BinaryWriter writer, string name, uint frame, float weight)
    {
        Name(writer, name, 15); writer.Write(frame); writer.Write(weight);
    }

    private static void Name(BinaryWriter writer, string text, int width)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] bytes = Encoding.GetEncoding(932).GetBytes(text);
        Assert.True(bytes.Length <= width);
        writer.Write(bytes); writer.Write(new byte[width - bytes.Length]);
    }

    private sealed class ShortReadStream(Stream source) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, Math.Min(count, 3));
        public override int Read(Span<byte> buffer) => source.Read(buffer[..Math.Min(buffer.Length, 3)]);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
