using System.Numerics;
using System.Text;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class PmdCompletionTests
{
    [Fact]
    public void FullTailRetainsEnglishToonIkMorphAndPhysics()
    {
        using var input = new MemoryStream(Fixture());
        var model = PmdReader.Read(input);
        Assert.Equal("comment", model.Comment);
        Assert.Equal("English", model.EnglishName);
        Assert.Equal("root-en", model.Bones[0].EnglishName);
        Assert.Equal("smile-en", model.Morphs[1].EnglishName);
        Assert.Equal("body-en", model.BoneDisplayFrames[1].EnglishName);
        Assert.Equal("toon01.bmp", model.ToonTextures[0]);
        Assert.Equal(0, model.UnparsedTailBytes);
        Assert.Equal(input.Length, input.Position);
        Assert.Equal(new[] { 0 }, model.IkChains[0].Links);
        Assert.Equal(.25f, model.IkChains[0].AngleLimit);
        Assert.Equal(1, model.RigidBodies[0].BoneIndex);
        Assert.Equal(new Vector3(1, 2, 3), model.RigidBodies[0].Position);
        Assert.Equal(0x1234, model.RigidBodies[0].CollisionMask);
        Assert.Equal(1, model.Joints[0].RigidBodyB);
    }

    [Fact]
    public void PmdAdapterResolvesBaseMorphIndicesAndBoneRelativeBodies()
    {
        var source = PmdReader.Read(new MemoryStream(Fixture()));
        var model = PmdPmxAdapter.Convert(source);
        Assert.Equal("English", model.EnglishName);
        Assert.Single(model.Morphs);
        Assert.Equal(2, model.Morphs[0].PositionOffsets[0].VertexIndex);
        Assert.Equal(new Vector3(.5f, 0, 0), model.Morphs[0].PositionOffsets[0].Position);
        Assert.Equal(new Vector3(1, 12, 3), model.RigidBodies[0].Translation);
        Assert.Equal(PmxRigidBodyOperation.DynamicAndBoneMerge, model.RigidBodies[0].Operation);
        Assert.Equal(1f, model.Bones[1].IkLimit);
        Assert.Equal(0, model.Bones[1].IkTargetBoneIndex);
        Assert.Equal(PmxSphereMode.Add, model.Materials[0].SphereMode);
        Assert.Equal("a.png", model.Textures[model.Materials[0].TextureIndex]);
        Assert.Equal("b.spa", model.Textures[model.Materials[0].SphereTextureIndex]);
        Assert.Equal(0, model.DisplayFrames[0].Targets[0].Index);
        using var bytes = new MemoryStream(PmxWriter.ToBytes(model));
        Assert.Equal(2, PmxReader.Read(bytes).RigidBodies.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(123)]
    public void TruncatedPhysicsTailIsRejected(int removed)
    {
        byte[] bytes = Fixture();
        Assert.Throws<InvalidDataException>(() => PmdReader.Read(new MemoryStream(bytes, 0, bytes.Length - removed)));
    }

    [Fact]
    public void UnknownTrailerIsPreservedInsteadOfSilentlyDropped()
    {
        var source = Fixture().Concat(new byte[] { 0xA1, 0xB2 }).ToArray();
        var model = PmdReader.Read(new MemoryStream(source));
        Assert.Equal(new byte[] { 0xA1, 0xB2 }, model.UnknownTailData);
        Assert.Equal(2, model.UnparsedTailBytes);
        Assert.Throws<NotSupportedException>(() => PmdPmxAdapter.Convert(model));
    }

    [Fact]
    public void LegacyMandatoryTailWithoutOptionalExtensionsUsesCommonToons()
    {
        var model = PmdReader.Read(new MemoryStream(Fixture(optional: false)));
        Assert.Equal("toon10.bmp", model.ToonTextures[9]);
        Assert.Equal("", model.EnglishName);
        Assert.Empty(model.RigidBodies);
        Assert.True(model.HasAnimationTail);
    }

    [Fact]
    public void BaseMorphAbsolutePositionsReplaceVertexSectionBeforeOtherMorphs()
    {
        var source = PmdReader.Read(new MemoryStream(Fixture()));
        var absolute = new Vector3(7, -3, 2);
        source = source with { Morphs = [source.Morphs[0] with { Vertices = [new(2, absolute)] }, source.Morphs[1]] };
        var model = PmdPmxAdapter.Convert(source);
        Assert.Equal(absolute, model.Vertices[2].Position);
        Assert.Equal(source.Vertices[0].Position, model.Vertices[0].Position);
        Assert.Equal(new Vector3(.5f, 0, 0), model.Morphs[0].PositionOffsets[0].Position);
    }

    [Fact]
    public void OutOfRangeMorphBaseReferenceIsRejected()
    {
        var source = PmdReader.Read(new MemoryStream(Fixture()));
        var bad = source with { Morphs = [source.Morphs[0], source.Morphs[1] with { Vertices = [new(4, Vector3.Zero)] }] };
        Assert.Throws<InvalidDataException>(() => PmdPmxAdapter.Convert(bad));
    }

    [Theory]
    [InlineData(5, 1f, (ushort)1)]
    [InlineData(9, .25f, (ushort)25)]
    [InlineData(9, -.5f, unchecked((ushort)-50))]
    public void LegacyRotationInheritanceUsesTheCorrectSourceAndPercentage(byte type, float weight, ushort raw)
    {
        // The type-9 ratio is a signed int16 percentage; -50 % used to become 654.86.
        var source = PmdReader.Read(new MemoryStream(Fixture()));
        var target = source.Bones[0] with { Type = type, TailIndex = 1, IkParent = raw };
        var model = PmdPmxAdapter.Convert(source with { Bones = [target, source.Bones[1]], IkChains = [] });
        Assert.True(model.Bones[0].Flags.HasFlag(PmxBoneFlags.AppendRotate));
        Assert.Equal(1, model.Bones[0].AppendBoneIndex);
        Assert.Equal(weight, model.Bones[0].AppendWeight);
        Assert.True(model.Bones[0].DeformDepth > model.Bones[1].DeformDepth);
        Assert.Equal(type != 9, model.Bones[0].Flags.HasFlag(PmxBoneFlags.Visible));
        using var stream = new MemoryStream(PmxWriter.ToBytes(model));
        Assert.Equal(weight, PmxReader.Read(stream).Bones[0].AppendWeight);
    }

    [Fact]
    public void TailIndexZeroMeansNoChildLikeFFFF()
    {
        // PMD tail 0 is "no child" (nanoem maps it to not-found); it must not link the bone to bone 0.
        var source = PmdReader.Read(new MemoryStream(Fixture()));
        var bones = new[] { source.Bones[0], source.Bones[1] with { Type = 0, TailIndex = 0 } };
        var model = PmdPmxAdapter.Convert(source with { Bones = bones, IkChains = [] });
        Assert.Equal(-1, model.Bones[1].LinkBoneIndex);
        Assert.False(model.Bones[1].Flags.HasFlag(PmxBoneFlags.TargetShowMode));
    }

    [Theory]
    [InlineData("ED40", "纊")]
    [InlineData("82A082", "あ")]
    public void HeaderTextDecodesDuplicateSpellingsAndDropsATruncatedLeadByte(string hex, string expected)
    {
        byte[] bytes = Fixture();
        // The model name is the 20-byte field right after the 7-byte "Pmd" + version header.
        var name = new byte[20]; Convert.FromHexString(hex).CopyTo(name, 0);
        name.CopyTo(bytes, 7);
        Assert.Equal(expected, PmdReader.Read(new MemoryStream(bytes)).Name);
    }

    [Fact]
    public void RotationInheritanceCycleIsRejectedWithoutRecursing()
    {
        var source = PmdReader.Read(new MemoryStream(Fixture()));
        var bones = new[] { source.Bones[0] with { Type = 5, IkParent = 1 }, source.Bones[1] with { Type = 5, IkParent = 0 } };
        Assert.Throws<InvalidDataException>(() => PmdPmxAdapter.Convert(source with { Bones = bones, IkChains = [] }));
    }

    private static byte[] Fixture(bool optional = true)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        void Text(string value, int width) { byte[] bytes = Encoding.ASCII.GetBytes(value); w.Write(bytes); w.Write(new byte[width - bytes.Length]); }
        void V(Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        w.Write("Pmd"u8); w.Write(1f); Text("test", 20); Text("comment", 256);
        w.Write(3u);
        for (int i = 0; i < 3; i++) { V(new(i, 0, 0)); V(Vector3.UnitY); w.Write(0f); w.Write(0f); w.Write((ushort)0); w.Write((ushort)1); w.Write((byte)50); w.Write((byte)0); }
        w.Write(3u); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2);
        w.Write(1u); V(Vector3.One); w.Write(1f); w.Write(8f); V(Vector3.One); V(Vector3.Zero); w.Write((byte)0); w.Write((byte)1); w.Write(3u); Text("a.png*b.spa", 20);
        w.Write((ushort)2);
        Text("root", 20); w.Write(ushort.MaxValue); w.Write((ushort)1); w.Write((byte)1); w.Write((ushort)0); V(Vector3.Zero);
        Text("ik", 20); w.Write(ushort.MaxValue); w.Write((ushort)0); w.Write((byte)2); w.Write((ushort)0); V(new(0, 10, 0));
        w.Write((ushort)1); w.Write((ushort)1); w.Write((ushort)0); w.Write((byte)1); w.Write((ushort)8); w.Write(.25f); w.Write((ushort)0);
        w.Write((ushort)2); Text("base", 20); w.Write(1u); w.Write((byte)0); w.Write(2u); V(new(2, 0, 0));
        Text("smile", 20); w.Write(1u); w.Write((byte)3); w.Write(0u); V(new(.5f, 0, 0));
        w.Write((byte)1); w.Write((ushort)1);
        w.Write((byte)1); Text("body", 50); w.Write(1u); w.Write((ushort)0); w.Write((byte)1);
        if (!optional) return stream.ToArray();
        w.Write((byte)1); Text("English", 20); Text("english comment", 256); Text("root-en", 20); Text("ik-en", 20); Text("smile-en", 20); Text("body-en", 50);
        for (int i = 1; i <= 10; i++) Text($"toon{i:00}.bmp", 100);
        w.Write(2u);
        for (int i = 0; i < 2; i++)
        {
            Text($"rigid{i}", 20); w.Write((ushort)1); w.Write((byte)3); w.Write((ushort)0x1234); w.Write((byte)1);
            V(Vector3.One); V(new(1, 2, 3)); V(Vector3.Zero); w.Write(2f); w.Write(.1f); w.Write(.2f); w.Write(.3f); w.Write(.4f); w.Write((byte)2);
        }
        w.Write(1u); Text("joint", 20); w.Write(0u); w.Write(1u);
        for (int i = 0; i < 8; i++) V(Vector3.Zero);
        return stream.ToArray();
    }
}
