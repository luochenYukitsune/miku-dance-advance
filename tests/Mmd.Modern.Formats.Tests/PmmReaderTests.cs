using System.Text;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class PmmReaderTests
{
    [Fact]
    public void ReadsCompleteV2WithRawChinesePathsAndLinkedTracks()
    {
        using var stream = Fixture();
        PmmDocument document = PmmReader.Read(stream);
        PmmModel model = Assert.Single(document.Models);
        Assert.Equal("角色.pmx", model.Path.Value);
        Assert.Equal("骨", Assert.Single(model.BoneNames).Value);
        Assert.Equal(1, Assert.Single(model.BoneKeyframes).RecordIndex);
        Assert.Equal(1, model.InitialBoneKeyframes[0].NextIndex);
        Assert.Equal(0, model.BoneKeyframes[0].PreviousIndex);
        Assert.Equal(42, document.CurrentFrame);
        Assert.Equal("音乐.wav", document.Settings.AudioPath.Value);
        Assert.Equal(42, document.Settings.CurrentFrameText);
        Assert.Equal(64, document.Settings.UnknownMatrix.Count);
        Assert.Empty(document.UnknownTail);
        Assert.Equal(stream.Length, document.BytesConsumed);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryTruncationIsRejectedWithOffset(bool rich)
    {
        byte[] bytes = Fixture(rich: rich).ToArray();
        // Selection trailer is optional. All bytes before it are mandatory.
        int optionalTrailerOffset = bytes.Length - (rich ? 6 : 1);
        for (int length = 0; length < bytes.Length; length++)
        {
            if (length == optionalTrailerOffset) continue;
            using var truncated = new MemoryStream(bytes, 0, length, false);
            PmmFormatException error = Assert.Throws<PmmFormatException>(() => PmmReader.Read(truncated));
            Assert.InRange(error.Offset, 0, length);
            Assert.NotEmpty(error.Field);
        }
    }

    [Fact]
    public void HugeAndNegativeCountsAreRejectedBeforeAllocation()
    {
        foreach (int count in new[] { -1, int.MaxValue })
        {
            byte[] bytes = Fixture().ToArray();
            // Header 53, model selector/count 2, index 1, two empty strings 2,
            // path 256, fixed-tracks byte 1 => bone count at 315.
            BitConverter.GetBytes(count).CopyTo(bytes, 315);
            var error = Assert.Throws<PmmFormatException>(() => PmmReader.Read(new MemoryStream(bytes)));
            Assert.Contains("count", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PreservesUnknownTailAndSupportsExplicitCp932()
    {
        byte[] bytes = Fixture(932).ToArray().Concat(new byte[] { 0xde, 0xad }).ToArray();
        PmmDocument document = PmmReader.Read(new MemoryStream(bytes), new PmmReadOptions { AnsiCodePage = 932 });
        Assert.Equal("骨", document.Models[0].BoneNames[0].Value);
        Assert.Equal(new byte[] { 0xde, 0xad }, document.UnknownTail);
    }

    [Fact]
    public void RejectsDanglingAndCyclicLinks()
    {
        byte[] bytes = Fixture().ToArray();
        PmmDocument valid = PmmReader.Read(new MemoryStream(bytes));
        long offset = valid.Models[0].BoneKeyframes[0].Offset;
        foreach (int previous in new[] { 99, 1 })
        {
            byte[] changed = bytes.ToArray();
            BitConverter.GetBytes(previous).CopyTo(changed, (int)offset + 8);
            Assert.Throws<PmmFormatException>(() => PmmReader.Read(new MemoryStream(changed)));
        }
    }

    [Fact]
    public void ChangingV2MagicAloneIsNotAValidV1Conversion()
    {
        byte[] bytes = Fixture().ToArray();
        bytes[23] = (byte)'1';
        Assert.Throws<PmmFormatException>(() => PmmReader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void ReadsAnimatedCameraLightAccessoryGravityAndSelfShadow()
    {
        using var stream = Fixture(rich: true);
        PmmDocument document = PmmReader.Read(stream);
        Assert.Equal(11, Assert.Single(document.Camera.Keyframes).ObjectIndex);
        Assert.Equal(12, Assert.Single(document.Light.Keyframes).ObjectIndex);
        Assert.Equal(13, Assert.Single(document.Gravity.Keyframes).ObjectIndex);
        Assert.Equal(14, Assert.Single(document.SelfShadow.Keyframes).ObjectIndex);
        PmmAccessory accessory = Assert.Single(document.Accessories);
        Assert.Equal("小物", accessory.Name.Value);
        Assert.Equal("小物.x", accessory.Path.Value);
        Assert.Equal(15, Assert.Single(accessory.Keyframes).ObjectIndex);
        Assert.Equal(.5f, accessory.Opacity);
        Assert.True(accessory.Visible);
        Assert.Equal(3f, accessory.Scale);
        Assert.True(accessory.AddBlendEnabled);
        Assert.Equal(123, Assert.Single(document.Settings.ModelSelections).SelectionIndex);
        Assert.Equal(stream.Length, document.ParsedLength);
    }

    [Fact]
    public void SupportsSparseIdsAndIgnoresInitialZeroPreviousForOwnership()
    {
        byte[] bytes = Fixture().ToArray();
        PmmDocument initial = PmmReader.Read(new MemoryStream(bytes));
        int root = (int)initial.Models[0].InitialBoneKeyframes[0].Offset;
        int extra = (int)initial.Models[0].BoneKeyframes[0].Offset;
        BitConverter.GetBytes(3141).CopyTo(bytes, root + 8);
        BitConverter.GetBytes(3141).CopyTo(bytes, root + 4);
        BitConverter.GetBytes(3141).CopyTo(bytes, extra);
        PmmDocument changed = PmmReader.Read(new MemoryStream(bytes));
        Assert.Equal(3141, changed.Models[0].BoneKeyframes[0].ObjectIndex);
        Assert.Equal(1, changed.Models[0].BoneKeyframes[0].RecordIndex);
        Assert.Equal(3141, changed.Models[0].InitialBoneKeyframes[0].PreviousIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialMorphPreviousLinksDoNotChangeForwardTrackOwnership(bool previousPointsToTail)
    {
        byte[] bytes = Fixture(morphCount: 3, animatedMorph: true).ToArray();
        PmmModel original = PmmReader.Read(new MemoryStream(bytes)).Models[0];
        int animatedRoot = (int)original.InitialMorphKeyframes[1].Offset;
        int emptyRoot = (int)original.InitialMorphKeyframes[2].Offset;
        // PMM roots can retain a self/stale previous value. Only their forward
        // chains identify registered frames; the raw previous value must survive.
        BitConverter.GetBytes(previousPointsToTail ? 3 : 1).CopyTo(bytes, animatedRoot + 4);
        BitConverter.GetBytes(2).CopyTo(bytes, emptyRoot + 4);

        PmmDocument document = PmmReader.Read(new MemoryStream(bytes));
        PmmModel model = Assert.Single(document.Models);
        Assert.Equal(3, model.InitialMorphKeyframes.Count);
        Assert.Equal(previousPointsToTail ? 3 : 1, model.InitialMorphKeyframes[1].PreviousIndex);
        Assert.Equal(3, model.InitialMorphKeyframes[1].NextIndex);
        Assert.Equal(2, model.InitialMorphKeyframes[2].PreviousIndex);
        Assert.Equal(0, model.InitialMorphKeyframes[2].NextIndex);
        PmmMorphKeyframe extra = Assert.Single(model.MorphKeyframes);
        Assert.Equal(3, extra.ObjectIndex);
        Assert.Equal(1, extra.PreviousIndex);
        Assert.Equal(10, extra.Frame);
        Assert.Equal(.75f, extra.Weight);
        Assert.Equal(bytes, PmmWriter.ToBytes(document));
    }

    [Theory]
    [InlineData("extraPreviousSelf")]
    [InlineData("extraNextSelf")]
    [InlineData("initialNextSelf")]
    [InlineData("initialNextOtherTrack")]
    [InlineData("disagreeingPrevious")]
    [InlineData("danglingPrevious")]
    [InlineData("negativePrevious")]
    [InlineData("disconnectedExtra")]
    public void MorphRootPreviousToleranceDoesNotAcceptMalformedForwardTracks(string damage)
    {
        byte[] bytes = Fixture(morphCount: 3, animatedMorph: true).ToArray();
        PmmModel original = PmmReader.Read(new MemoryStream(bytes)).Models[0];
        int root = (int)original.InitialMorphKeyframes[1].Offset;
        int extra = (int)original.MorphKeyframes[0].Offset;
        (int offset, int value) = damage switch
        {
            "extraPreviousSelf" => (extra + 8, 3),
            "extraNextSelf" => (extra + 12, 3),
            "initialNextSelf" => (root + 8, 1),
            "initialNextOtherTrack" => (root + 8, 2),
            "disagreeingPrevious" => (extra + 8, 2),
            "danglingPrevious" => (root + 4, 99),
            "negativePrevious" => (root + 4, -1),
            "disconnectedExtra" => (root + 8, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(damage))
        };
        BitConverter.GetBytes(value).CopyTo(bytes, offset);
        PmmFormatException error = Assert.Throws<PmmFormatException>(() => PmmReader.Read(new MemoryStream(bytes)));
        Assert.Equal("models[0].morphFrames.links", error.Field);
    }

    [Fact]
    public void RejectsDisconnectedAndDuplicateIdsAndNonFiniteValues()
    {
        byte[] bytes = Fixture().ToArray();
        PmmDocument initial = PmmReader.Read(new MemoryStream(bytes));
        int root = (int)initial.Models[0].InitialBoneKeyframes[0].Offset;
        int extra = (int)initial.Models[0].BoneKeyframes[0].Offset;
        foreach ((int offset, int value) in new[] { (root + 8, 0), (extra, 0), (extra + 32, 0x7fc00000) })
        {
            byte[] bad = bytes.ToArray(); BitConverter.GetBytes(value).CopyTo(bad, offset);
            Assert.Throws<PmmFormatException>(() => PmmReader.Read(new MemoryStream(bad)));
        }
        Assert.Throws<PmmFormatException>(() => PmmReader.Read(new MemoryStream(bytes), new PmmReadOptions { MaxInputBytes = 30 }));
        Assert.Throws<PmmFormatException>(() => PmmReader.Read(new MemoryStream(bytes), new PmmReadOptions { MaxElementCount = 0 }));
    }

    internal static MemoryStream Fixture(int codePage = 936, bool rich = false, int morphCount = 1, bool animatedMorph = false)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = Encoding.GetEncoding(codePage);
        var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8, true);
        void Fixed(string s, int n) { byte[] b = new byte[n]; encoding.GetBytes(s).CopyTo(b, 0); w.Write(b); }
        void Name(string s) { byte[] b = encoding.GetBytes(s); w.Write((byte)b.Length); w.Write(b); }
        void V3(float x = 0, float y = 0, float z = 0) { w.Write(x); w.Write(y); w.Write(z); }
        void Base(bool extra = false, int frame = 0, int prev = 0, int next = 0, int id = 1) { if (extra) w.Write(id); w.Write(frame); w.Write(prev); w.Write(next); }
        void Bone(bool extra) { Base(extra, extra ? 10 : 0, 0, extra ? 0 : 1); w.Write(new byte[16]); V3(); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); w.Write(false); w.Write(false); }
        Fixed("Polygon Movie maker 0002", 30);
        w.Write(1280); w.Write(720); w.Write(250); w.Write(30f); w.Write(new byte[7]);
        w.Write((byte)0); w.Write((byte)1); w.Write((byte)0); Name(""); Name(""); Fixed("角色.pmx", 256); w.Write((byte)0);
        w.Write(1); Name("骨"); w.Write(morphCount); for (int i = 0; i < morphCount; i++) Name(i == 0 ? "笑" : $"笑{i}"); w.Write(1); w.Write(0); w.Write(1); w.Write(0);
        w.Write((byte)0); w.Write(true); w.Write(0); for (int i = 0; i < 4; i++) w.Write(0); w.Write((byte)0); w.Write(0); w.Write(10);
        Bone(false); w.Write(1); Bone(true);
        for (int i = 0; i < morphCount; i++) { Base(next: animatedMorph && i == 1 ? morphCount : 0); w.Write(.5f); w.Write(false); }
        w.Write(animatedMorph ? 1 : 0);
        if (animatedMorph) { Base(true, 10, prev: 1, id: morphCount); w.Write(.75f); w.Write(false); }
        Base(); w.Write(true); w.Write(true); w.Write(-1); w.Write(-1); w.Write(false); w.Write(0);
        V3(); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); w.Write(new byte[3]); for (int i = 0; i < morphCount; i++) w.Write(.5f); w.Write(true);
        w.Write(0); w.Write(10); w.Write(-1); w.Write(-1);
        w.Write(true); w.Write(1f); w.Write(true); w.Write((byte)0);
        void Camera(bool extra) { Base(extra, extra ? 10 : 0, next: rich && !extra ? 11 : 0, id: 11); w.Write(-45f); V3(0, 10); V3(); w.Write(-1); w.Write(-1); w.Write(new byte[24]); w.Write((byte)0); w.Write(30); w.Write(false); }
        Camera(false); w.Write(rich ? 1 : 0); if (rich) Camera(true);
        V3(0, 10); V3(0, 10, 45); V3(); w.Write((byte)0);
        void Light(bool extra) { Base(extra, extra ? 10 : 0, next: rich && !extra ? 12 : 0, id: 12); V3(1, 1, 1); V3(0, -1); w.Write(false); }
        Light(false); w.Write(rich ? 1 : 0); if (rich) Light(true); V3(1, 1, 1); V3(0, -1);
        w.Write((byte)255); w.Write(0); w.Write((byte)(rich ? 1 : 0));
        if (rich)
        {
            Fixed("小物", 100); w.Write((byte)0); Fixed("小物", 100); Fixed("小物.x", 256); w.Write((byte)0);
            void Accessory(bool extra) { Base(extra, extra ? 10 : 0, next: extra ? 0 : 15, id: 15); w.Write((byte)101); w.Write(-1); w.Write(-1); V3(1, 2, 3); V3(4, 5, 6); w.Write(2f); w.Write(true); w.Write(false); }
            Accessory(false); w.Write(1); Accessory(true);
            w.Write((byte)101); w.Write(-1); w.Write(-1); V3(7, 8, 9); w.Write(3f); V3(10, 11, 12); w.Write(true); w.Write(true);
        }
        w.Write(42); w.Write(0); w.Write(735); w.Write(0); w.Write(new byte[4]); w.Write(0); w.Write(300); w.Write(true); Fixed("音乐.wav", 256);
        w.Write(0); w.Write(0); w.Write(1f); Fixed("", 256); w.Write(0);
        w.Write(0); w.Write(0); w.Write(1f); Fixed("", 256); w.Write(new byte[4]); w.Write(60f); w.Write(2); w.Write(-1); w.Write(1f); w.Write(false); w.Write((byte)3);
        void Gravity(bool extra) { Base(extra, extra ? 10 : 0, next: rich && !extra ? 13 : 0, id: 13); w.Write(false); w.Write(0); w.Write(9.8f); V3(0, -1); w.Write(false); }
        w.Write(9.8f); w.Write(0); V3(0, -1); w.Write(false); Gravity(false); w.Write(rich ? 1 : 0); if (rich) Gravity(true);
        void Shadow(bool extra) { Base(extra, extra ? 10 : 0, next: rich && !extra ? 14 : 0, id: 14); w.Write((byte)1); w.Write(.01125f); w.Write(false); }
        w.Write(true); w.Write(.01125f); Shadow(false); w.Write(rich ? 1 : 0); if (rich) Shadow(true);
        w.Write(0); w.Write(0); w.Write(0); w.Write(false); w.Write(-1); w.Write(-1); w.Write(new byte[64]); w.Write(false); w.Write(false); w.Write(true); w.Write(42); w.Write(rich);
        if (rich) { w.Write((byte)0); w.Write(123); }
        stream.Position = 0;
        return stream;
    }
}
