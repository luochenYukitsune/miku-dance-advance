using System.Buffers.Binary;
using System.Numerics;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class VmdWriterTests
{
    // Independent VMD0002 fixture: the six count offsets and all record bytes are literal.
    private static byte[] Fixture() => Convert.FromHexString(
        "566F63616C6F6964204D6F74696F6E204461746120303030320000000000" +
        "4100000000000000000000000000000000000000" +
        "01000000" +
        "42000000000000000000000000000007000000000000800000803F000000C0" +
        "0000803F000000400000404000008040" +
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F" +
        "202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F" +
        "010000004D00000000000000000000000000000900000000000080" +
        "010000000B0000000000A0C10000803F0000004000004040000080BF000000C0000040C0" +
        "A0A1A2A3A4A5A6A7A8A9AAABACADAEAFB0B1B2B3B4B5B6B72D00000001" +
        "010000000D0000000000803F0000003F0000803E000080BF000000C0000040C0" +
        "010000000F0000000200000080" +
        "01000000110000000001000000490000000000000000000000000000000000000001");

    [Fact]
    public void WritesLiteralLayoutAndRetainsEverySectionWithoutNormalization()
    {
        byte[] fixture = Fixture();
        Assert.Equal(336, fixture.Length);
        foreach (int offset in new[] { 50, 165, 192, 257, 289, 302 })
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(fixture.AsSpan(offset)));

        var expected = new VmdMotion
        {
            ModelName = "A",
            BoneKeyframes = [new() { BoneName = "B", Frame = 7, Translation = new(-0f, 1, -2),
                Rotation = new(1, 2, 3, 4), Interpolation = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray() }],
            MorphKeyframes = [new() { MorphName = "M", Frame = 9, Weight = -0f }],
            CameraKeyframes = [new() { Frame = 11, Distance = -20, Interest = new(1, 2, 3), Rotation = new(-1, -2, -3),
                Interpolation = Enumerable.Range(0xa0, 24).Select(i => (byte)i).ToArray(), ViewAngle = 45, Perspective = false }],
            LightKeyframes = [new() { Frame = 13, Color = new(1, .5f, .25f), Direction = new(-1, -2, -3) }],
            SelfShadowKeyframes = [new() { Frame = 15, Mode = 2, Distance = -0f }],
            IkKeyframes = [new() { Frame = 17, Visible = false, Switches = [new("I", true)] }],
        };
        Assert.Equal(fixture, VmdWriter.ToBytes(expected));

        using var stream = new MemoryStream(fixture);
        VmdMotion read = VmdReader.Read(stream);
        Assert.Equal(fixture, VmdWriter.ToBytes(read));
        Assert.Equal(new Quaternion(1, 2, 3, 4), Assert.Single(read.BoneKeyframes).Rotation);
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(read.BoneKeyframes[0].Translation.X));
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(Assert.Single(read.MorphKeyframes).Weight));
        VmdCameraKeyframe camera = Assert.Single(read.CameraKeyframes);
        Assert.Equal(11u, camera.Frame);
        Assert.Equal(-20, camera.Distance);
        Assert.Equal(new Vector3(1, 2, 3), camera.Interest);
        Assert.Equal(new Vector3(-1, -2, -3), camera.Rotation);
        Assert.Equal(expected.CameraKeyframes[0].Interpolation, camera.Interpolation);
        Assert.Equal(45u, camera.ViewAngle);
        Assert.False(camera.Perspective);
        VmdLightKeyframe light = Assert.Single(read.LightKeyframes);
        Assert.Equal(13u, light.Frame);
        Assert.Equal(new Vector3(1, .5f, .25f), light.Color);
        Assert.Equal(new Vector3(-1, -2, -3), light.Direction);
        VmdSelfShadowKeyframe shadow = Assert.Single(read.SelfShadowKeyframes);
        Assert.Equal(15u, shadow.Frame);
        Assert.Equal(2, shadow.Mode);
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(shadow.Distance));
        Assert.False(Assert.Single(read.IkKeyframes).Visible);
        Assert.Equal(new VmdIkSwitch("I", true), Assert.Single(read.IkKeyframes[0].Switches));
        Assert.Contains(read.Diagnostics, value => value.Contains("camera") && value.Contains("not played"));
    }

    [Fact]
    public void EmitsAllEmptySectionCountsAndDefaultPerspectiveUsesZeroByte()
    {
        byte[] empty = VmdWriter.ToBytes(new VmdMotion());
        Assert.Equal(74, empty.Length);
        Assert.All(empty.Skip(50), value => Assert.Equal(0, value));
        byte[] camera = VmdWriter.ToBytes(new VmdMotion { CameraKeyframes = [new()] });
        Assert.Equal(0, camera[122]);
        using var stream = new MemoryStream(camera);
        Assert.True(Assert.Single(VmdReader.Read(stream).CameraKeyframes).Perspective);
    }

    [Fact]
    public void KeepsDuplicateFramesAndUnsortedOrderAcrossAllSections()
    {
        uint[] frames = [9, 1, 9];
        var motion = new VmdMotion
        {
            BoneKeyframes = frames.Select(frame => new VmdBoneKeyframe { Frame = frame }).ToArray(),
            MorphKeyframes = frames.Select(frame => new VmdMorphKeyframe { Frame = frame, Weight = -2 }).ToArray(),
            CameraKeyframes = frames.Select(frame => new VmdCameraKeyframe { Frame = frame, ViewAngle = uint.MaxValue }).ToArray(),
            LightKeyframes = frames.Select(frame => new VmdLightKeyframe { Frame = frame, Color = new(-2, 3, 4) }).ToArray(),
            SelfShadowKeyframes = frames.Select(frame => new VmdSelfShadowKeyframe { Frame = frame, Mode = 0, Distance = -5 }).ToArray(),
            IkKeyframes = frames.Select(frame => new VmdIkKeyframe { Frame = frame,
                Switches = [new("a", false), new("a", true)] }).ToArray(),
        };
        byte[] bytes = VmdWriter.ToBytes(motion);
        using var stream = new MemoryStream(bytes);
        VmdMotion read = VmdReader.Read(stream);
        Assert.Equal(frames, read.BoneKeyframes.Select(k => k.Frame));
        Assert.Equal(frames, read.MorphKeyframes.Select(k => k.Frame));
        Assert.Equal(frames, read.CameraKeyframes.Select(k => k.Frame));
        Assert.Equal(frames, read.LightKeyframes.Select(k => k.Frame));
        Assert.Equal(frames, read.SelfShadowKeyframes.Select(k => k.Frame));
        Assert.Equal(frames, read.IkKeyframes.Select(k => k.Frame));
        Assert.Equal(bytes, VmdWriter.ToBytes(read));
    }

    [Fact]
    public void AcceptsFullCp932ByteWidthsIncludingDoubleByteBoundary()
    {
        string full20 = "あいうえおかきくけこ";
        string full15 = "あいうえおかきA";
        var motion = new VmdMotion
        {
            ModelName = full20,
            BoneKeyframes = [new() { BoneName = full15 }],
            MorphKeyframes = [new() { MorphName = full15 }],
            IkKeyframes = [new() { Switches = [new(full20, true)] }],
        };
        byte[] bytes = VmdWriter.ToBytes(motion);
        Assert.Equal(new byte[] { 0x82, 0xa0, 0x82, 0xa2 }, bytes[30..34]);
        Assert.Equal((byte)'A', bytes[68]);
        using var stream = new MemoryStream(bytes);
        VmdMotion read = VmdReader.Read(stream);
        Assert.Equal(full20, read.ModelName);
        Assert.Equal(full15, Assert.Single(read.BoneKeyframes).BoneName);
        Assert.Equal(full15, Assert.Single(read.MorphKeyframes).MorphName);
        Assert.Equal(full20, Assert.Single(Assert.Single(read.IkKeyframes).Switches).BoneName);
    }

    [Theory]
    [InlineData("あいうえおかきくけこA")]
    [InlineData("a\0b")]
    [InlineData("😀")]
    public void RejectsInvalidModelNamesBeforeWritingAnything(string name)
        => AssertNoWrite(new() { ModelName = name });

    [Theory]
    [InlineData("あいうえおかきく")]
    [InlineData("a\0b")]
    [InlineData("😀")]
    public void RejectsInvalidBoneAndMorphNamesBeforeWritingAnything(string name)
    {
        AssertNoWrite(new() { BoneKeyframes = [new() { BoneName = name }] });
        AssertNoWrite(new() { MorphKeyframes = [new() { MorphName = name }] });
    }

    [Theory]
    [InlineData("あいうえおかきくけこA")]
    [InlineData("a\0b")]
    [InlineData("😀")]
    public void RejectsInvalidIkNameAtEndOfFileBeforeWritingAnything(string name)
        => AssertNoWrite(new() { IkKeyframes = [new() { Switches = [new(name, true)] }] });

    [Fact]
    public void RejectsInvalidNumericValuesAndNullRecordsBeforeWritingAnything()
    {
        AssertNoWrite(new() { BoneKeyframes = [new() { Translation = new(float.NaN, 0, 0) }] });
        AssertNoWrite(new() { BoneKeyframes = [new() { Rotation = new(0, 0, 0, 0) }] });
        AssertNoWrite(new() { BoneKeyframes = [new() { Rotation = new(float.PositiveInfinity, 0, 0, 1) }] });
        AssertNoWrite(new() { MorphKeyframes = [new() { Weight = float.NegativeInfinity }] });
        AssertNoWrite(new() { CameraKeyframes = [new() { Distance = float.NaN }] });
        AssertNoWrite(new() { CameraKeyframes = [new() { Interest = new(0, float.NaN, 0) }] });
        AssertNoWrite(new() { CameraKeyframes = [new() { Rotation = new(0, 0, float.NaN) }] });
        AssertNoWrite(new() { LightKeyframes = [new() { Color = new(float.NaN, 0, 0) }] });
        AssertNoWrite(new() { LightKeyframes = [new() { Direction = new(0, float.PositiveInfinity, 0) }] });
        AssertNoWrite(new() { SelfShadowKeyframes = [new() { Mode = 3 }] });
        AssertNoWrite(new() { SelfShadowKeyframes = [new() { Distance = float.NaN }] });
        AssertNoWrite(new() { BoneKeyframes = [null!] });
        AssertNoWrite(new() { MorphKeyframes = null! });
        AssertNoWrite(new() { IkKeyframes = [null!] });
    }

    [Fact]
    public void CopiesCameraInterpolationAndRequiresExactly24Bytes()
    {
        byte[] source = new byte[24];
        var key = new VmdCameraKeyframe { Interpolation = source };
        source[0] = 99;
        Assert.Equal(0, key.Interpolation[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<byte>)key.Interpolation)[0] = 1);
        Assert.Throws<ArgumentException>(() => new VmdCameraKeyframe { Interpolation = new byte[23] });
        Assert.Throws<ArgumentException>(() => new VmdCameraKeyframe { Interpolation = new byte[25] });
    }

    [Fact]
    public void WritesAtCurrentPositionToNonSeekableStreamAndLeavesItOpen()
    {
        using var backing = new MemoryStream();
        backing.WriteByte(42);
        using var stream = new NonSeekableWriteStream(backing);
        VmdWriter.Write(stream, new VmdMotion());
        Assert.True(stream.CanWrite);
        Assert.False(stream.Disposed);
        Assert.Equal(75, backing.Length);
        Assert.Equal(42, backing.ToArray()[0]);
        Assert.Equal(VmdWriter.ToBytes(new VmdMotion()), backing.ToArray()[1..]);
    }

    [Fact]
    public void RejectsUnreadableArgumentsWithoutTakingStreamOwnership()
    {
        Assert.Throws<ArgumentNullException>(() => VmdWriter.ToBytes(null!));
        Assert.Throws<ArgumentNullException>(() => VmdWriter.Write(null!, new VmdMotion()));
        using var stream = new MemoryStream(new byte[1], writable: false);
        Assert.Throws<ArgumentException>(() => VmdWriter.Write(stream, new VmdMotion()));
        Assert.True(stream.CanRead);
    }

    private static void AssertNoWrite(VmdMotion motion)
    {
        using var stream = new MemoryStream();
        stream.Write([42, 43, 44]);
        long position = stream.Position;
        Assert.ThrowsAny<ArgumentException>(() => VmdWriter.Write(stream, motion));
        Assert.Equal(position, stream.Position);
        Assert.Equal(new byte[] { 42, 43, 44 }, stream.ToArray());
    }

    private sealed class NonSeekableWriteStream(Stream destination) : Stream
    {
        public bool Disposed { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !Disposed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => destination.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => destination.Write(buffer);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => destination.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
