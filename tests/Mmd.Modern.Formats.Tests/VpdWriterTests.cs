using System.Globalization;
using System.Numerics;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class VpdWriterTests
{
    [Theory]
    [InlineData(VpdTextEncoding.Cp932)]
    [InlineData(VpdTextEncoding.Utf8)]
    public void RoundTripsValuesNamesIndicesAndMorphsWithoutNormalizing(VpdTextEncoding encoding)
    {
        var pose = new VpdPose
        {
            ParentFileName = "初音ミク.osm",
            Bones = new[]
            {
                new VpdBonePose { Index = 1, BoneName = "右腕", Translation = new(float.Epsilon, -1.2345678f, float.MaxValue), Rotation = new(.1f, -.2f, .3f, -.9f) },
                new VpdBonePose { Index = 0, BoneName = "センター", Rotation = new(0, 0, 0, 2) },
            },
            Morphs = new[] { new VpdMorphPose { Index = 7, MorphName = "笑い", Weight = 1.2345678f } },
        };
        using var stream = new MemoryStream();
        CultureInfo old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            VpdWriter.Write(stream, pose, encoding);
            Assert.True(stream.CanWrite);
            stream.Position = 0;
            VpdPose result = VpdReader.Read(stream);
            Assert.Equal(pose.ParentFileName, result.ParentFileName);
            Assert.Equal(pose.Bones, result.Bones);
            Assert.Equal(pose.Morphs, result.Morphs);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void WritesCanonicalCp932HeaderAndBoneCount()
    {
        VpdPose pose = VpdReader.Read(VpdReaderTests.Input(VpdReaderTests.One));
        using var stream = new MemoryStream();
        VpdWriter.Write(stream, pose);
        byte[] result = stream.ToArray();
        Assert.Equal((byte)'V', result[0]);
        Assert.Contains((byte)0x83, result); // CP932 katakana, not UTF8.
        stream.Position = 0;
        Assert.Single(VpdReader.Read(stream, new() { Encoding = VpdTextEncoding.Cp932 }).Bones);
    }

    [Fact]
    public void RejectsUnencodableNamesBeforeWritingAnyBytes()
    {
        using var stream = new MemoryStream();
        var pose = new VpdPose { ParentFileName = "pose😀.osm" };
        Assert.Throws<VpdFormatException>(() => VpdWriter.Write(stream, pose));
        Assert.Equal(0, stream.Length);
        VpdWriter.Write(stream, pose, VpdTextEncoding.Utf8);
        stream.Position = 0;
        Assert.Equal(pose.ParentFileName, VpdReader.Read(stream).ParentFileName);
    }

    [Theory]
    [InlineData("bad\nname")]
    [InlineData("bad//comment")]
    [InlineData("bad{name")]
    [InlineData("bad;name")]
    public void RejectsStructuralCharactersBeforeWriting(string name)
    {
        using var stream = new MemoryStream();
        var pose = new VpdPose { Bones = new[] { new VpdBonePose { BoneName = name } } };
        Assert.Throws<VpdFormatException>(() => VpdWriter.Write(stream, pose));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void RejectsInvalidIndicesAndNonfiniteValuesBeforeWriting()
    {
        VpdPose[] poses =
        {
            new() { Bones = new[] { new VpdBonePose { Index = 1 } } },
            new() { Bones = new[] { new VpdBonePose(), new VpdBonePose() } },
            new() { Bones = new[] { new VpdBonePose { Rotation = default } } },
            new() { Bones = new[] { new VpdBonePose { Translation = new(float.NaN, 0, 0) } } },
            new() { Morphs = new[] { new VpdMorphPose { Index = -1 } } },
            new() { Morphs = new[] { new VpdMorphPose { Weight = float.PositiveInfinity } } },
        };
        foreach (VpdPose pose in poses)
        {
            using var stream = new MemoryStream();
            Assert.Throws<VpdFormatException>(() => VpdWriter.Write(stream, pose));
            Assert.Equal(0, stream.Length);
        }
    }

    [Fact]
    public void PoseCopiesCollectionsIntoReadOnlySnapshot()
    {
        VpdBonePose[] bones = { new() { BoneName = "original" } };
        VpdMorphPose[] morphs = { new() { MorphName = "original" } };
        var pose = new VpdPose { Bones = bones, Morphs = morphs };
        bones[0] = new(); morphs[0] = new();
        Assert.Equal("original", pose.Bones[0].BoneName);
        Assert.Equal("original", pose.Morphs[0].MorphName);
        Assert.Throws<NotSupportedException>(() => ((IList<VpdBonePose>)pose.Bones)[0] = new());
    }
}
