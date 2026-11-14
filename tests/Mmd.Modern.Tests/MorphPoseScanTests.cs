using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Animation;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class MorphPoseScanTests
{
    [Theory]
    [InlineData("vertexDeltas", float.NaN)]
    [InlineData("vertexDeltas", float.PositiveInfinity)]
    [InlineData("vertexUvDeltas", float.NaN)]
    [InlineData("vertexUvDeltas", float.NegativeInfinity)]
    [InlineData("boneTranslations", float.NaN)]
    [InlineData("boneTranslations", float.PositiveInfinity)]
    [InlineData("boneRotations", float.NaN)]
    [InlineData("boneRotations", float.NegativeInfinity)]
    [InlineData("boneRotations", 0f)]
    public void EarlierEffectsNeverHideLaterInvalidValues(string channel, float invalid)
    {
        Vector3[] vertices = [Vector3.UnitX, Vector3.Zero, Vector3.Zero];
        Vector2[] uvs = [Vector2.UnitX, Vector2.Zero, Vector2.Zero];
        Vector3[] translations = [Vector3.UnitX, Vector3.Zero, Vector3.Zero];
        Quaternion[] rotations = [new(0, 0, 2, 2), Quaternion.Identity, Quaternion.Identity];
        switch (channel)
        {
            case "vertexDeltas": vertices[2] = new(0, 0, invalid); break;
            case "vertexUvDeltas": uvs[2] = new(0, invalid); break;
            case "boneTranslations": translations[2] = new(0, invalid, 0); break;
            case "boneRotations": rotations[2] = new(0, 0, 0, invalid); break;
        }
        var error = Assert.Throws<ArgumentException>(() => MorphPose.FromOwnedArrays(vertices, translations, rotations, uvs));
        Assert.Equal(channel, error.ParamName);
        // Rotation normalization starts only after every earlier channel validates.
        Assert.Equal(channel == "boneRotations" ? Normalize(new(0, 0, 2, 2)) : new Quaternion(0, 0, 2, 2), rotations[0]);
        Assert.Equal(Quaternion.Identity, rotations[1]);
    }

    [Fact]
    public void ChannelFailuresKeepTheirOriginalPriorityAndDoNotNormalizeRotationsEarly()
    {
        Vector3[] vertices = [Vector3.UnitX, new(float.NaN)];
        Vector2[] uvs = [Vector2.One, new(float.PositiveInfinity)];
        Vector3[] translations = [Vector3.One, new(float.NegativeInfinity)];
        Quaternion[] rotations = [new(0, 0, 2, 2), default];
        AssertFailure("vertexDeltas");
        vertices[1] = Vector3.Zero;
        AssertFailure("vertexUvDeltas");
        uvs[1] = Vector2.Zero;
        AssertFailure("boneTranslations");
        translations[1] = Vector3.Zero;
        AssertFailure("boneRotations");
        Assert.Equal(Bytes(new Quaternion[] { Normalize(new(0, 0, 2, 2)), default }), Bytes(rotations));

        void AssertFailure(string parameter)
        {
            var error = Assert.Throws<ArgumentException>(() => MorphPose.FromOwnedArrays(vertices, translations, rotations, uvs));
            Assert.Equal(parameter, error.ParamName);
            if (parameter != "boneRotations") Assert.Equal(new Quaternion(0, 0, 2, 2), rotations[0]);
        }
    }

    [Fact]
    public void SignedZeroAndNormalizedAntipodalIdentitiesHaveNoEffectsAndKeepTheirBits()
    {
        float negativeZero = BitConverter.Int32BitsToSingle(int.MinValue);
        Vector3[] vertices = [new(negativeZero, 0, negativeZero), Vector3.Zero];
        Vector2[] uvs = [new(negativeZero, 0), new(0, negativeZero)];
        Vector3[] translations = [new(0, negativeZero, 0), Vector3.Zero, Vector3.Zero];
        Quaternion[] rotations = [new(negativeZero, 0, negativeZero, 7), new(0, negativeZero, 0, -3),
            new(float.Epsilon, -float.Epsilon, 0, float.MaxValue)];
        var pose = new MorphPose(vertices, translations, rotations, uvs);
        Assert.False(pose.HasVertexEffects); Assert.False(pose.HasUvEffects); Assert.False(pose.HasBoneEffects);
        Assert.Equal(Bytes(vertices), Bytes(pose.VertexDeltas));
        Assert.Equal(Bytes(uvs), Bytes(pose.VertexUvDeltas));
        Assert.Equal(Bytes(translations), Bytes(pose.BoneTranslations));
        Assert.Equal(Bytes(rotations.Select(Normalize).ToArray()), Bytes(pose.BoneRotations));
        var animation = new Quaternion(negativeZero, .25f, -.5f, 1);
        Assert.Equal(Bytes(new[] { animation }), Bytes(new[] { pose.ApplyBoneRotation(0, animation) }));
        Assert.Equal(Bytes(new[] { animation }), Bytes(new[] { pose.ApplyBoneRotation(1, animation) }));
    }

    [Fact]
    public void SubnormalDeltasAndRotationsKeepEffectsAfterExactNormalization()
    {
        Vector3[] vertices = [Vector3.Zero, new(float.Epsilon, 0, 0)];
        Vector2[] uvs = [Vector2.Zero, new(0, -float.Epsilon)];
        Vector3[] translations = [Vector3.Zero, Vector3.Zero];
        Quaternion[] rotations = [new(0, 0, 0, -float.Epsilon), new(float.Epsilon, 0, 0, float.Epsilon)];
        var pose = MorphPose.FromOwnedArrays(vertices, translations, rotations, uvs);
        Assert.True(pose.HasVertexEffects); Assert.True(pose.HasUvEffects); Assert.True(pose.HasBoneEffects);
        Assert.Equal(Bytes(new[] { Normalize(new(0, 0, 0, -float.Epsilon)), Normalize(new(float.Epsilon, 0, 0, float.Epsilon)) }),
            Bytes(pose.BoneRotations));
        Assert.Equal(Bytes(vertices), Bytes(pose.VertexDeltas));
        Assert.Equal(Bytes(uvs), Bytes(pose.VertexUvDeltas));
        Assert.True(new MorphPose([], [new(0, 0, -float.Epsilon)], [-Quaternion.Identity]).HasBoneEffects);
    }

    [Fact]
    public void ExistingTranslationEffectsStillNormalizeEveryRotationInOrder()
    {
        Quaternion[] source = [new(0, 0, 0, -2), new(1, -2, 3, 4), new(float.MaxValue, -float.MaxValue, float.MaxValue, float.MaxValue)];
        var expected = source.Select(Normalize).ToArray();
        var zeroUvs = new MorphPose.ImmutableZeroUvChannel(1);
        var first = MorphPose.FromOwnedArraysWithZeroUvs([Vector3.Zero], [Vector3.UnitY, Vector3.Zero, Vector3.Zero], source, zeroUvs);
        var firstBytes = Bytes(first.BoneRotations);
        var second = MorphPose.FromOwnedArraysWithZeroUvs([Vector3.Zero], [Vector3.Zero], [new(0, 0, 0, 5)], zeroUvs);
        Assert.True(first.HasBoneEffects); Assert.False(second.HasBoneEffects);
        Assert.Equal(Bytes(expected), firstBytes); Assert.Equal(firstBytes, Bytes(first.BoneRotations));
        Assert.Same(first.VertexUvDeltas, second.VertexUvDeltas);
        Assert.False(first.HasUvEffects); Assert.False(second.HasUvEffects);
    }

    // Retain the original double precision operation order as the bitwise oracle.
    private static Quaternion Normalize(Quaternion q)
    {
        double lengthSquared = (double)q.X * q.X + (double)q.Y * q.Y + (double)q.Z * q.Z + (double)q.W * q.W;
        double length = Math.Sqrt(lengthSquared);
        return new((float)(q.X / length), (float)(q.Y / length), (float)(q.Z / length), (float)(q.W / length));
    }

    private static byte[] Bytes<T>(IReadOnlyList<T> values) where T : struct
        => MemoryMarshal.AsBytes(values.ToArray().AsSpan()).ToArray();
}
