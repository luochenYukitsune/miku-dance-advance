using System.Numerics;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class PmmWriterTests
{
    [Theory]
    [InlineData(932, false)]
    [InlineData(932, true)]
    [InlineData(936, false)]
    [InlineData(936, true)]
    public void EveryRepresentedSectionAndOriginalStringBufferRoundTripsExactly(int codePage, bool rich)
    {
        byte[] bytes = PmmReaderTests.Fixture(codePage, rich).ToArray();
        var document = PmmReader.Read(new MemoryStream(bytes), new() { AnsiCodePage = codePage });
        Assert.Equal(bytes, PmmWriter.ToBytes(document));
    }

    [Fact]
    public void SparseObjectLinksAndEditedFrameValuesAreWrittenWithoutRenumbering()
    {
        byte[] bytes = PmmReaderTests.Fixture(rich: true).ToArray();
        var initial = PmmReader.Read(new MemoryStream(bytes));
        int root = (int)initial.Models[0].InitialBoneKeyframes[0].Offset;
        int extra = (int)initial.Models[0].BoneKeyframes[0].Offset;
        BitConverter.GetBytes(3141).CopyTo(bytes, root + 8);
        BitConverter.GetBytes(3141).CopyTo(bytes, root + 4);
        BitConverter.GetBytes(3141).CopyTo(bytes, extra);
        var document = PmmReader.Read(new MemoryStream(bytes));
        document.Models[0].BoneKeyframes[0].Frame = 17;
        var reread = PmmReader.Read(new MemoryStream(PmmWriter.ToBytes(document)));
        Assert.Equal(3141, reread.Models[0].BoneKeyframes[0].ObjectIndex);
        Assert.Equal(17, reread.Models[0].BoneKeyframes[0].Frame);
        Assert.Equal(3141, reread.Models[0].InitialBoneKeyframes[0].PreviousIndex);
        Assert.Equal(3141, reread.Models[0].InitialBoneKeyframes[0].NextIndex);
    }

    [Fact]
    public void UnknownTailAndNonzeroStringPaddingAreRetained()
    {
        byte[] bytes = PmmReaderTests.Fixture(rich: true).ToArray().Concat(new byte[] { 222, 173, 190, 239 }).ToArray();
        var document = PmmReader.Read(new MemoryStream(bytes));
        bytes[(int)document.Models[0].Path.Offset + 200] = 0x9a;
        document = PmmReader.Read(new MemoryStream(bytes));
        Assert.Equal(bytes, PmmWriter.ToBytes(document));
    }

    [Fact]
    public void NoOptionalTrailerRemainsAbsent()
    {
        byte[] bytes = PmmReaderTests.Fixture().ToArray()[..^1];
        var document = PmmReader.Read(new MemoryStream(bytes));
        Assert.Null(document.Settings.SelectionTrailerFlag);
        Assert.Equal(bytes, PmmWriter.ToBytes(document));
    }

    [Fact]
    public void WritesAtCurrentPositionAndLeavesCallerStreamOpen()
    {
        byte[] bytes = PmmReaderTests.Fixture().ToArray();
        var document = PmmReader.Read(new MemoryStream(bytes));
        using var target = new MemoryStream(); target.WriteByte(123);
        PmmWriter.Write(target, document);
        Assert.True(target.CanWrite);
        Assert.Equal(new byte[] { 123 }.Concat(bytes), target.ToArray());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9999)]
    public void InvalidFramesAndLinksNeverTouchCallerStream(int value)
    {
        var document = PmmReader.Read(PmmReaderTests.Fixture());
        if (value == -1) document.Models[0].BoneKeyframes[0].Frame = value;
        else document.Models[0].BoneKeyframes[0].NextIndex = value;
        using var target = new MemoryStream(); target.Write(new byte[] { 1, 2, 3 });
        Assert.Throws<PmmFormatException>(() => PmmWriter.Write(target, document));
        Assert.Equal(new byte[] { 1, 2, 3 }, target.ToArray()); Assert.Equal(3, target.Position);
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("invalid\0name")]
    public void NewStringsUseStrictAnsiWithoutReplacementOrEmbeddedNul(string value)
    {
        var original = PmmReader.Read(PmmReaderTests.Fixture());
        var changed = Copy(original, settings: new() { AudioPath = new(0, value, []) });
        Assert.Throws<PmmFormatException>(() => PmmWriter.ToBytes(changed));
    }

    [Fact]
    public void StaleRawStringRequiresExplicitTranscodeRatherThanSilentlyDiscardingBytes()
    {
        var original = PmmReader.Read(PmmReaderTests.Fixture());
        var changed = Copy(original, settings: new() { AudioPath = new(0, "changed", original.Settings.AudioPath.RawBytes) });
        Assert.Throws<PmmFormatException>(() => PmmWriter.ToBytes(changed));
    }

    [Fact]
    public void NonFiniteSceneValuesAndInvalidHeadersAreRejected()
    {
        var original = PmmReader.Read(PmmReaderTests.Fixture());
        Assert.Throws<PmmFormatException>(() => PmmWriter.ToBytes(Copy(original, header: new byte[30])));
        Assert.Throws<PmmFormatException>(() => PmmWriter.ToBytes(Copy(original, camera: new()
        { InitialKeyframe = new() { Interpolation = new byte[24], LookAt = new(float.NaN, 0, 0) } })));
    }

    static PmmDocument Copy(PmmDocument d, IReadOnlyList<byte>? header = null, PmmCamera? camera = null, PmmSettings? settings = null) => new()
    {
        Version=d.Version, AnsiCodePage=d.AnsiCodePage, HeaderBytes=header ?? d.HeaderBytes,
        OutputWidth=d.OutputWidth, OutputHeight=d.OutputHeight, TimelineWidth=d.TimelineWidth, CameraFov=d.CameraFov,
        PanelStates=d.PanelStates, SelectedModelIndex=d.SelectedModelIndex, Models=d.Models, Camera=camera ?? d.Camera,
        Light=d.Light, SelectedAccessoryIndex=d.SelectedAccessoryIndex, AccessoryHorizontalScroll=d.AccessoryHorizontalScroll,
        AccessoryNames=d.AccessoryNames, Accessories=d.Accessories, CurrentFrame=d.CurrentFrame, Settings=settings ?? d.Settings,
        Gravity=d.Gravity, SelfShadow=d.SelfShadow, UnknownTail=d.UnknownTail
    };
}
