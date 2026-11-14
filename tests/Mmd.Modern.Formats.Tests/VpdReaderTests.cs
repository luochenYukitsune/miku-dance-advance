using System.Numerics;
using System.Text;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class VpdReaderTests
{
    internal const string One = "Vocaloid Pose Data file\n\n初音ミク.osm; // parent\n1; // bones\n\nBone0{センター\n  1,-2,3.5; // xyz\n  .1,-.2,.3,.9; // xyzw\n}\n";

    [Theory]
    [InlineData(932, false)]
    [InlineData(65001, false)]
    [InlineData(65001, true)]
    public void ReadsJapaneseNamesAndRawXyzw(int codePage, bool bom)
    {
        using MemoryStream stream = Input(One, codePage, bom);
        VpdPose pose = VpdReader.Read(stream);
        Assert.Equal("初音ミク.osm", pose.ParentFileName);
        VpdBonePose bone = Assert.Single(pose.Bones);
        Assert.Equal(0, bone.Index);
        Assert.Equal("センター", bone.BoneName);
        Assert.Equal(new Vector3(1, -2, 3.5f), bone.Translation);
        Assert.Equal(new Quaternion(.1f, -.2f, .3f, .9f), bone.Rotation);
        Assert.Empty(pose.Morphs);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void ReadsOutOfOrderIndicesCommentsWhitespaceAndMorphExtension()
    {
        string text = "Vocaloid Pose Data file\r\n // ignored\r\n model.osm;\r\n2;\r\n\tBone1{右腕\r\n 1e-3,+2,-3;\r\n 0,0,0,-2;\r\n }\r\n\r\nBone0{左腕\r\n 0,0,0;\r\n0,0,0,1;\r\n}\r\nMorph7{笑い\r\n 1.25; // weight\r\n}\r\n";
        VpdPose pose = VpdReader.Read(Input(text));
        Assert.Equal(new[] { 1, 0 }, pose.Bones.Select(b => b.Index));
        Assert.Equal(.001f, pose.Bones[0].Translation.X);
        Assert.Equal(-2, pose.Bones[0].Rotation.W);
        VpdMorphPose morph = Assert.Single(pose.Morphs);
        Assert.Equal(7, morph.Index);
        Assert.Equal("笑い", morph.MorphName);
        Assert.Equal(1.25f, morph.Weight);
    }

    [Fact]
    public void ReadsEmptyPoseAndEmptyParentFileName()
    {
        VpdPose pose = VpdReader.Read(Input("Vocaloid Pose Data file\n;\n0;\n // end"));
        Assert.Equal("", pose.ParentFileName);
        Assert.Empty(pose.Bones);
    }

    [Theory]
    [InlineData("Vocaloid Pose Data file", "Vocaloid Pose Data wrong", "header.signature")]
    [InlineData("初音ミク.osm;", "初音ミク.osm", "header.parentFileName")]
    [InlineData("1; // bones", "-1;", "bones.count")]
    [InlineData("1; // bones", "2000000000;", "bones.count")]
    [InlineData("Bone0{", "Bone1{", "bones[0].index")]
    [InlineData("Bone0{", "Bone-1{", "bones[0].index")]
    [InlineData("Bone0{", "BoneX{", "bones[0].index")]
    [InlineData("1,-2,3.5;", "1,-2;", "bones[0].translation")]
    [InlineData("1,-2,3.5;", "1,-2,3.5", "bones[0].translation")]
    [InlineData("1,-2,3.5;", "1,-2,NaN;", "bones[0].translation")]
    [InlineData("1,-2,3.5;", "1,-2,1e100;", "bones[0].translation")]
    [InlineData(".1,-.2,.3,.9;", "0,0,0,0;", "bones[0].rotation")]
    [InlineData(".1,-.2,.3,.9;", "0,0,0,Infinity;", "bones[0].rotation")]
    [InlineData(".1,-.2,.3,.9;", "0,0,1;", "bones[0].rotation")]
    [InlineData("}\n", "", "bones[0].end")]
    [InlineData("}\n", "} junk\n", "bones[0].end")]
    public void RejectsMalformedFieldsWithContext(string from, string to, string field)
    {
        var error = Assert.Throws<VpdFormatException>(() => VpdReader.Read(Input(One.Replace(from, to))));
        Assert.Equal(field, error.Field);
        Assert.True(error.LineNumber > 0);
        Assert.Contains(field, error.Message);
    }

    [Fact]
    public void RejectsDuplicateIndicesAndUnrecognizedTail()
    {
        string two = One.Replace("1; // bones", "2;") + "Bone0{other\n0,0,0;\n0,0,0,1;\n}\n";
        Assert.Throws<VpdFormatException>(() => VpdReader.Read(Input(two)));
        Assert.Throws<VpdFormatException>(() => VpdReader.Read(Input(One + "garbage")));
        Assert.Throws<VpdFormatException>(() => VpdReader.Read(Input(One + "Morph0{笑\nNaN;\n}")));
        Assert.Throws<VpdFormatException>(() => VpdReader.Read(Input(One + "Morph0{笑\n1;\n}\nMorph0{other\n0;\n}")));
    }

    [Fact]
    public void RejectsTruncationsWithoutLeakingIndexExceptions()
    {
        foreach (int length in new[] { 0, 10, One.IndexOf("1; //"), One.IndexOf("Bone0"), One.IndexOf("  1,-"), One.IndexOf("  .1,"), One.IndexOf("}\n") })
            Assert.Throws<VpdFormatException>(() => VpdReader.Read(Input(One[..length])));
    }

    [Fact]
    public void RejectsInvalidEncodingAndLimits()
    {
        Assert.Throws<VpdFormatException>(() => VpdReader.Read(new MemoryStream(new byte[] { 0xef, 0xbb, 0xbf, 0xff })));
        Assert.Throws<VpdFormatException>(() => VpdReader.Read(new MemoryStream(new byte[] { 0x81 }), new() { Encoding = VpdTextEncoding.Cp932 }));
        Assert.Throws<VpdFormatException>(() => VpdReader.Read(Input(One), new() { MaxInputBytes = 30 }));
        Assert.Throws<VpdFormatException>(() => VpdReader.Read(Input(One), new() { MaxElementCount = 0 }));
        Assert.Throws<VpdFormatException>(() => VpdReader.Read(Input(One + "Morph0{x\n1;\n}"), new() { MaxElementCount = 1 }));
    }

    [Fact]
    public void SupportsNonSeekableCurrentPositionAndDoesNotChangeInput()
    {
        byte[] bytes = Input("prefix" + One).ToArray();
        byte[] before = bytes.ToArray();
        using var source = new MemoryStream(bytes);
        source.Position = 6;
        using var stream = new NonSeekable(source);
        Assert.Single(VpdReader.Read(stream).Bones);
        Assert.True(source.CanRead);
        Assert.Equal(before, bytes);
    }

    internal static MemoryStream Input(string text, int codePage = 932, bool bom = false)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = Encoding.GetEncoding(codePage);
        byte[] bytes = encoding.GetBytes(text);
        return new MemoryStream(bom ? new byte[] { 0xef, 0xbb, 0xbf }.Concat(bytes).ToArray() : bytes);
    }

    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
