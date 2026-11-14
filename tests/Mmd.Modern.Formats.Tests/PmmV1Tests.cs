using System.Numerics;
using System.Text;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class PmmV1Tests
{
    [Fact]
    public void ReadsIndependentV1LayoutWithSixPanelsAndOpaqueTail()
    {
        byte[] bytes = Fixture();
        var document = PmmReader.Read(new MemoryStream(bytes));
        Assert.Equal(1, document.Version);
        Assert.Equal(6, document.PanelStates.Count);
        Assert.Empty(document.Models);
        Assert.Equal(23, document.CurrentFrame);
        Assert.Equal("music.wav", document.Settings.AudioPath.Value);
        Assert.Equal(new byte[] { 0xde, 0xad, 0xbe, 0xef }, document.UnknownTail);
        Assert.Equal(-1, document.Camera.InitialKeyframe.ParentModelIndex);
        Assert.Equal(60, document.Settings.PreferredFps);
    }

    [Theory]
    [InlineData(932, false)]
    [InlineData(932, true)]
    [InlineData(936, false)]
    [InlineData(936, true)]
    public void ExternalMetadataDefinesRealV1TracksAndWriterInversePreservesBytes(int codePage, bool accessory)
    {
        byte[] bytes = Fixture(model: true, accessory: accessory, codePage: codePage);
        PmmV1ModelReference? seen = null;
        var document = PmmReader.Read(new MemoryStream(bytes), new()
        {
            AnsiCodePage = codePage,
            V1ModelMetadataResolver = reference => { seen = reference; return Metadata(); }
        });
        Assert.NotNull(seen); Assert.Equal(0, seen.ModelPosition); Assert.Equal(7, seen.StoredModelIndex);
        Assert.Equal("character.pmd", seen.Path.Value);
        var model = Assert.Single(document.Models);
        Assert.Equal("table-name", Assert.Single(document.V1ModelNames).Value);
        Assert.Equal("model-name", model.Name.Value); Assert.Equal("bone", Assert.Single(model.BoneNames).Value);
        Assert.Equal("morph", Assert.Single(model.MorphNames).Value); Assert.Equal(new[] { 0 }, model.ConstraintBoneIndices);
        Assert.Equal(11, Assert.Single(model.BoneKeyframes).ObjectIndex); Assert.Equal(11, model.InitialBoneKeyframes[0].NextIndex);
        Assert.Equal(new Vector3(1, 2, 3), model.BoneKeyframes[0].Translation);
        Assert.False(model.BoneKeyframes[0].PhysicsDisabled);
        Assert.Equal(0x12345678, model.BoneStates[0].V1UnknownValue);
        Assert.Equal(new Vector3(4, 5, 6), model.BoneStates[0].Translation);
        Assert.True(model.BoneStates[0].Dirty); Assert.Equal(2, model.BoneStates[0].SelectedTrackCount);
        Assert.False(model.ConstraintStates[0].Enabled); Assert.True(model.InitialModelKeyframe.ConstraintEnabled[0]);
        Assert.Equal(19, Assert.Single(document.Camera.Keyframes).ObjectIndex);
        Assert.Equal(accessory ? 1 : 0, document.Accessories.Count);
        if (accessory) Assert.False(document.Accessories[0].AddBlendEnabled);
        Assert.Equal(bytes, PmmWriter.ToBytes(document));
    }

    [Fact]
    public void MissingOrNullMetadataDoesNotGuessTrackLengths()
    {
        foreach (PmmReadOptions options in new[] { new PmmReadOptions(), new() { V1ModelMetadataResolver = _ => null } })
        {
            var error = Assert.Throws<PmmFormatException>(() => PmmReader.Read(new MemoryStream(Fixture(model: true)), options));
            Assert.Contains("metadata", error.Field);
            Assert.Contains("0001", error.Message);
        }
    }

    [Fact]
    public void EveryRecognizedV1ByteIsMandatoryAndTailIsOpaque()
    {
        byte[] bytes = Fixture(model: true, accessory: true);
        var options = new PmmReadOptions { V1ModelMetadataResolver = _ => Metadata() };
        var full = PmmReader.Read(new MemoryStream(bytes), options);
        for (int length = 0; length < full.ParsedLength; length++)
        {
            var error = Assert.Throws<PmmFormatException>(() => PmmReader.Read(new MemoryStream(bytes, 0, length, false), options));
            Assert.InRange(error.Offset, 0, length);
        }
        var noTail = PmmReader.Read(new MemoryStream(bytes[..(int)full.ParsedLength]), options);
        Assert.Empty(noTail.UnknownTail); Assert.Equal(bytes[..(int)full.ParsedLength], PmmWriter.ToBytes(noTail));
    }

    [Fact]
    public void MetadataIsAnImmutableSnapshotAndRejectsInvalidTargets()
    {
        string[] bones = ["bone"], morphs = ["morph"]; int[] constraints = [0];
        var metadata = new PmmV1ModelMetadata(bones, morphs, constraints);
        bones[0] = "changed"; morphs[0] = "changed"; constraints[0] = 7;
        Assert.Equal("bone", metadata.BoneNames[0]); Assert.Equal("morph", metadata.MorphNames[0]); Assert.Equal(0, metadata.ConstraintBoneIndices[0]);
        Assert.Throws<ArgumentException>(() => new PmmV1ModelMetadata(["bone"], [], [1]));
        Assert.Throws<ArgumentException>(() => new PmmV1ModelMetadata(["bone"], [], [0, 0]));
        Assert.Throws<ArgumentException>(() => new PmmV1ModelMetadata(["bad\0name"], [], []));
        Assert.Throws<PmmFormatException>(() => PmmReader.Read(new MemoryStream(Fixture(model: true)), new()
            { MaxElementCount = 0, V1ModelMetadataResolver = _ => metadata }));
    }

    [Fact]
    public void InvalidSparseLinksAndNonFiniteValuesRemainRejected()
    {
        byte[] bytes = Fixture(model: true); var options = new PmmReadOptions { V1ModelMetadataResolver = _ => Metadata() };
        var document = PmmReader.Read(new MemoryStream(bytes), options);
        int extra = (int)document.Models[0].BoneKeyframes[0].Offset;
        foreach ((int offset, int value) in new[] { (extra + 8, 99), (extra + 32, 0x7fc00000) })
        {
            byte[] bad = bytes.ToArray(); BitConverter.GetBytes(value).CopyTo(bad, offset);
            Assert.Throws<PmmFormatException>(() => PmmReader.Read(new MemoryStream(bad), options));
        }
    }

    [Fact]
    public void EditedFrameIsWrittenWithOriginalMetadataAndInvalidCandidateIsAtomic()
    {
        var options = new PmmReadOptions { V1ModelMetadataResolver = _ => Metadata() };
        var document = PmmReader.Read(new MemoryStream(Fixture(model: true)), options);
        document.Models[0].BoneKeyframes[0].Frame = 15;
        var reread = PmmReader.Read(new MemoryStream(PmmWriter.ToBytes(document)), options);
        Assert.Equal(15, reread.Models[0].BoneKeyframes[0].Frame);
        document.Models[0].BoneKeyframes[0].NextIndex = 999;
        using var target = new MemoryStream(); target.WriteByte(123);
        Assert.Throws<PmmFormatException>(() => PmmWriter.Write(target, document)); Assert.Equal(new byte[] { 123 }, target.ToArray());
    }

    [Theory]
    [InlineData("camera-parent")]
    [InlineData("gravity")]
    [InlineData("gravity-initial")]
    [InlineData("shadow-initial")]
    [InlineData("settings")]
    public void V1WriterNeverSilentlyDropsV2OnlyData(string unsupported)
    {
        var original = PmmReader.Read(new MemoryStream(Fixture()));
        var document = new PmmDocument
        {
            Version = 1, HeaderBytes = original.HeaderBytes, PanelStates = original.PanelStates,
            Camera = unsupported == "camera-parent" ? new()
            {
                InitialKeyframe = new() { ParentModelIndex = 0, ParentBoneIndex = 0, Interpolation = new byte[24] }
            } : original.Camera,
            Light = original.Light,
            Settings = unsupported == "settings" ? new() { EdgeColorRgb = [0, 0, 0] } : original.Settings,
            Gravity = unsupported == "gravity" ? new() { Acceleration = 9.8f }
                : unsupported == "gravity-initial" ? new() { InitialKeyframe = new() { Acceleration = 9.8f } } : original.Gravity,
            SelfShadow = unsupported == "shadow-initial" ? new() { InitialKeyframe = new() { Distance = .1f } } : original.SelfShadow,
            UnknownTail = original.UnknownTail
        };
        using var target = new MemoryStream(); target.WriteByte(77);
        Assert.Throws<PmmFormatException>(() => PmmWriter.Write(target, document));
        Assert.Equal(new byte[] { 77 }, target.ToArray());
    }

    internal static PmmV1ModelMetadata Metadata() => new(["bone"], ["morph"], [0]);

    internal static byte[] Fixture(bool model = false, bool accessory = false, int codePage = 936)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(codePage);
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8, true);
        void Fixed(string text, int size) { byte[] data = new byte[size]; encoding.GetBytes(text).CopyTo(data, 0); w.Write(data); }
        void V(float x = 0, float y = 0, float z = 0) { w.Write(x); w.Write(y); w.Write(z); }
        void Base(bool extra = false, int next = 0, int id = 0, int frame = 0) { if (extra) w.Write(id); w.Write(frame); w.Write(0); w.Write(next); }
        Fixed("Polygon Movie maker 0001", 30);
        w.Write(1280); w.Write(720); w.Write(250); w.Write(30f); w.Write(new byte[] { 1, 0, 1, 0, 1, 0 });
        w.Write((byte)0); w.Write((byte)(model ? 1 : 0));
        if (model)
        {
            Fixed("table-name", 20); w.Write((byte)7); Fixed("model-name", 20); Fixed("character.pmd", 256);
            w.Write((byte)3); w.Write(true); w.Write(0); for (int i = 0; i < 4; i++) w.Write(0);
            w.Write((byte)2); w.Write(new byte[] { 1, 0 }); w.Write(4); w.Write(10);
            void Bone(bool extra) { Base(extra, extra ? 0 : 11, 11, extra ? 10 : 0); w.Write(new byte[16]); V(1, 2, 3); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); w.Write(true); }
            Bone(false); w.Write(1); Bone(true);
            Base(); w.Write(.25f); w.Write(false); w.Write(0);
            Base(); w.Write(true); w.Write(true); w.Write(false); w.Write(0);
            V(4, 5, 6); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); w.Write(0x12345678); w.Write(true); w.Write((byte)2);
            w.Write(.75f); w.Write(false);
        }
        void Camera(bool extra) { Base(extra, extra ? 0 : 19, 19, extra ? 10 : 0); w.Write(-45f); V(0, 10); V(); w.Write(new byte[24]); w.Write((byte)0); w.Write(30); w.Write(true); }
        Camera(false); w.Write(1); Camera(true); V(0, 10); V(0, 10, 45); V(); w.Write((byte)0);
        Base(); V(1, 1, 1); V(0, -1); w.Write(false); w.Write(0); V(1, 1, 1); V(0, -1);
        w.Write((byte)255); w.Write(0); w.Write((byte)(accessory ? 1 : 0));
        if (accessory)
        {
            Fixed("prop", 100); w.Write((byte)4); Fixed("prop", 100); Fixed("prop.x", 256); w.Write((byte)0);
            Base(); w.Write((byte)1); w.Write(-1); w.Write(-1); V(1, 2, 3); V(); w.Write(2f); w.Write(true); w.Write(false); w.Write(0);
            w.Write((byte)1); w.Write(-1); w.Write(-1); V(4, 5, 6); w.Write(3f); V(); w.Write(true);
        }
        w.Write(23); w.Write(0); w.Write(735); w.Write(0); w.Write(new byte[4]); w.Write(0); w.Write(300); w.Write(true); Fixed("music.wav", 256);
        w.Write(0); w.Write(0); w.Write(1f); Fixed("", 256); w.Write(0);
        w.Write(0); w.Write(0); w.Write(1f); Fixed("", 256); w.Write(new byte[4]); w.Write(60f); w.Write(2); w.Write(-1); w.Write(.75f);
        w.Write(new byte[] { 0xde, 0xad, 0xbe, 0xef }); w.Flush();
        return stream.ToArray();
    }
}
