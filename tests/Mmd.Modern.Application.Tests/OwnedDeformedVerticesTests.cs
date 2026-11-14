using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class OwnedDeformedVerticesTests
{
    private static readonly Func<SceneSnapshot, SceneVertex[], SceneSnapshot> Owned = typeof(SceneSnapshot)
        .GetMethod("WithOwnedDeformedVertices", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<Func<SceneSnapshot, SceneVertex[], SceneSnapshot>>();

    [Fact]
    public void OwnedDeformationMatchesPublicCopyBitwiseAndRetainsMetadata()
    {
        var source = Mesh(); var original = Bytes(source); var input = source.Vertices.ToArray();
        float negativeZero = BitConverter.Int32BitsToSingle(int.MinValue);
        input[0] = new(new(negativeZero, 3, -2), new(negativeZero, 3, 4), new(negativeZero, .75f));
        var expected = source.WithDeformedVertices(input); var owned = input.ToArray();
        var actual = Owned(source, owned);

        Assert.False(input.AsSpan().Overlaps(expected.Vertices));
        Assert.True(owned.AsSpan().Overlaps(actual.Vertices));
        Assert.Equal(Bytes(expected), Bytes(actual)); Assert.True(actual.HasDynamicVertices);
        Assert.Equal(source.Indices.ToArray(), actual.Indices.ToArray());
        Assert.True(source.Indices.Overlaps(actual.Indices));
        Assert.Same(source.Materials, actual.Materials); Assert.Same(source.Textures, actual.Textures);
        Assert.Same(source.Camera, actual.Camera); Assert.Equal(source.World, actual.World);
        Assert.Equal(source.LightDirection, actual.LightDirection);
        Assert.Same(source.AdditionalUvs, actual.AdditionalUvs); Assert.Same(source.RawRadialSeeds, actual.RawRadialSeeds);

        var savedPublic = Bytes(expected);
        input[0] = input[0] with { Position = new(float.NaN), Uv = new(float.PositiveInfinity) };
        Assert.Equal(savedPublic, Bytes(expected)); Assert.Equal(savedPublic, Bytes(actual));
        Assert.Equal(original, Bytes(source));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void OwnedDeformationPreservesPublicValidationOrderAndParameter(int invalid)
    {
        var source = Mesh(); var saved = Bytes(source); var input = source.Vertices.ToArray();
        input = invalid switch
        {
            0 => null!,
            // Topology must fail before inspecting even a nonfinite vertex.
            1 => [input[0] with { Position = new(float.NaN) }, input[1]],
            2 => [input[0] with { Position = new(float.NaN, 0, 0) }, input[1], input[2]],
            3 => [input[0] with { Normal = new(0, float.PositiveInfinity, 0) }, input[1], input[2]],
            4 => [input[0] with { Normal = new(float.MaxValue, 0, 0) }, input[1], input[2]],
            5 => [input[0] with { Uv = new(float.NaN, 0) }, input[1], input[2]],
            6 => [input[0] with { Uv = new(0, float.NegativeInfinity) }, input[1], input[2]],
            _ => [input[0] with { Position = new(0, 0, float.NegativeInfinity) }, input[1], input[2]]
        };
        var expected = Assert.ThrowsAny<ArgumentException>(() => source.WithDeformedVertices(input));
        var actual = Assert.ThrowsAny<ArgumentException>(() => Owned(source, input));
        Assert.Equal(expected.GetType(), actual.GetType()); Assert.Equal("deformed", actual.ParamName);
        Assert.Equal(expected.Message, actual.Message); Assert.Equal(saved, Bytes(source));
    }

    private static SceneSnapshot Mesh() => new(
        [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero), new(Vector3.UnitX, Vector3.UnitZ, Vector2.UnitX),
            new(Vector3.UnitY, Vector3.UnitZ, Vector2.UnitY)],
        [0, 1, 2], [new("surface", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 0)],
        [new("pixel", 1, 1, [255, 128, 0, 255])], world: Matrix4x4.CreateTranslation(1, 2, 3),
        additionalUvs: new(1, new SceneAdditionalUvVertex[3]),
        rawRadialSeeds: new([Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ]));
    private static byte[] Bytes(SceneSnapshot value) => MemoryMarshal.AsBytes(value.Vertices).ToArray();
}
