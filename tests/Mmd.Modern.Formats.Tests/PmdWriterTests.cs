using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class PmdWriterTests
{
    [Fact]
    public void FullModelRetainsAllRepresentedFieldsAndUnknownTrailer()
    {
        var model = Rich();
        byte[] bytes = PmdWriter.ToBytes(model);
        var read = PmdReader.Read(new MemoryStream(bytes));
        Assert.Equal(Json(model), Json(read));
        Assert.Equal(bytes, PmdWriter.ToBytes(read));
        Assert.Equal(new byte[] { 0x91, 0x82 }, read.UnknownTailData);
        Assert.Equal(new Vector3(-1, -2, -3), read.Vertices[0].Position);
        Assert.Equal(.37f, read.Vertices[0].Weight0);
    }

    [Fact]
    public void PrefixOnlyModelDoesNotInventAnimationOrExtensions()
    {
        var model = Prefix();
        var read = PmdReader.Read(new MemoryStream(PmdWriter.ToBytes(model)));
        Assert.False(read.HasAnimationTail); Assert.False(read.HasEnglishExtension);
        Assert.False(read.HasToonExtension); Assert.False(read.HasPhysicsExtension);
        Assert.Equal(Json(model), Json(read));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OptionalExtensionBoundariesRemainReadable(int extension)
    {
        var model = Rich() with { HasEnglishExtension = extension >= 1, HasToonExtension = extension >= 2,
            HasPhysicsExtension = false, RigidBodies = [], Joints = [], UnknownTailData = [], UnparsedTailBytes = 0,
            EnglishName = extension >= 1 ? "English" : "", EnglishComment = extension >= 1 ? "comment-en" : "" };
        if (extension == 0) model = model with { Bones = model.Bones.Select(b => b with { EnglishName = "" }).ToArray(),
            Morphs = model.Morphs.Select(m => m with { EnglishName = "" }).ToArray(),
            BoneDisplayFrames = model.BoneDisplayFrames.Select(f => f with { EnglishName = "" }).ToArray() };
        var read = PmdReader.Read(new MemoryStream(PmdWriter.ToBytes(model)));
        Assert.Equal(Json(model), Json(read));
    }

    [Theory]
    [InlineData("123456789012345678901")]
    [InlineData("🙂")]
    [InlineData("bad\0name")]
    public void OverlongUnencodableAndEmbeddedNulNamesRejectWithoutTouchingOutput(string name)
    {
        using var output = new MemoryStream(); output.Write([1, 2, 3]); output.Position = 1;
        Assert.Throws<InvalidDataException>(() => PmdWriter.Write(output, Prefix() with { Name = name }));
        Assert.Equal(new byte[] { 1, 2, 3 }, output.ToArray()); Assert.Equal(1, output.Position);
    }

    [Fact]
    public void WritesAtCurrentPositionAndLeavesCallerStreamOpen()
    {
        using var output = new MemoryStream(); output.WriteByte(0xCC);
        byte[] expected = PmdWriter.ToBytes(Prefix()); PmdWriter.Write(output, Prefix());
        Assert.True(output.CanWrite); Assert.Equal(new byte[] { 0xCC }.Concat(expected), output.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void InvalidOrUnrepresentedValuesRejectInsteadOfLosingData(int mode)
    {
        var model = Rich();
        model = mode switch
        {
            0 => model with { Vertices = [model.Vertices[0] with { Position = new(float.NaN, 0, 0) }, model.Vertices[1], model.Vertices[2]] },
            1 => model with { Vertices = [model.Vertices[0] with { Weight0 = .123f }, model.Vertices[1], model.Vertices[2]] },
            2 => model with { Materials = [model.Materials[0] with { StartIndex = 1 }] },
            3 => model with { HasPhysicsExtension = false },
            _ => model with { UnparsedTailBytes = 7 }
        };
        Assert.Throws<InvalidDataException>(() => PmdWriter.ToBytes(model));
    }

    [Fact]
    public void OmittedEnglishExtensionCannotDiscardEnglishMetadata()
        => Assert.Throws<InvalidDataException>(() => PmdWriter.ToBytes(Rich() with { HasEnglishExtension = false }));

    [Fact]
    public void OmittedAnimationTailCannotDiscardIkOrMorphData()
        => Assert.Throws<InvalidDataException>(() => PmdWriter.ToBytes(Rich() with { HasAnimationTail = false }));

    private static string Json(PmdModel model) => JsonSerializer.Serialize(model, new JsonSerializerOptions { IncludeFields = true });
    private static PmdModel Prefix() => new("モデル", [new(new(-1, -2, -3), Vector3.UnitY, new(.2f, .4f), 0, 1, .37f),
        new(Vector3.UnitX, Vector3.UnitY, Vector2.Zero, 0, 1, .5f), new(Vector3.UnitZ, Vector3.UnitY, Vector2.One, 0, 1, 1)],
        [0, 1, 2], [new(Vector4.One, Vector3.One, Vector3.Zero, 5, "tex.png", 0, 3) { EdgeFlag = 1, ToonIndex = 2 }],
        [new("センター", -1, Vector3.Zero) { TailIndex = 1, Type = 1 }, new("骨", 0, Vector3.UnitY) { Type = 0 }], 0)
        { Comment = "comment" };

    private static PmdModel Rich()
    {
        var model = Prefix();
        return model with { HasAnimationTail = true, HasEnglishExtension = true, HasToonExtension = true, HasPhysicsExtension = true,
            EnglishName = "English", EnglishComment = "comment-en",
            Bones = [model.Bones[0] with { EnglishName = "root-en" }, model.Bones[1] with { EnglishName = "bone-en" }],
            IkChains = [new(0, 1, 40, .3f, [1])],
            Morphs = [new("base", 0, [new(2, Vector3.UnitZ)]), new("笑い", 3, [new(0, new(-.2f, .3f, .4f))]) { EnglishName = "smile" }],
            MorphDisplay = [1], BoneDisplayFrames = [new("センター", [0]), new("body", [1]) { EnglishName = "body-en" }],
            RigidBodies = [new("body", -1, 3, 0x1234, 1, Vector3.One, new(-1, -2, -3), new(.1f, .2f, .3f), 1, .1f, .2f, .3f, .4f, 2)],
            Joints = [new("joint", 0, -1, Vector3.UnitY, Vector3.Zero, -Vector3.One, Vector3.One,
                -Vector3.One, Vector3.One, new(1, 2, 3), new(3, 2, 1))], UnknownTailData = [0x91, 0x82], UnparsedTailBytes = 2 };
    }
}
