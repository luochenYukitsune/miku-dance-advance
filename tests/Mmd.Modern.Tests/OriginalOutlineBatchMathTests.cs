using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class OriginalOutlineBatchMathTests
{
    private static readonly float NegativeZero = BitConverter.Int32BitsToSingle(int.MinValue);
    private static readonly float MinNormal = BitConverter.Int32BitsToSingle(0x00800000);
    private static readonly float[] Widths = [0, NegativeZero, float.Epsilon, 1e-40f, MinNormal, .18f, 1, float.MaxValue];

    [Fact]
    public void ExplicitValidationProcessUsesTheRequestedInstructionPath()
    {
        string? expected = Environment.GetEnvironmentVariable("MDA_OUTLINE_EXPECT_INTRINSICS");
        if (expected == "scalar") Assert.False(System.Runtime.Intrinsics.X86.Sse.IsSupported);
        if (expected == "simd") Assert.True(System.Runtime.Intrinsics.X86.Sse.IsSupported);
    }

    [Fact]
    public void EdgeNormalsPositionsScalesMaterialsAndBothModesMatchScalarBits()
    {
        Vector3[] normals = [Vector3.Zero, new(NegativeZero, 0, NegativeZero),
            new(float.Epsilon, -float.Epsilon, float.Epsilon), new(1e-40f, -1e-40f, MinNormal),
            new(float.MaxValue, float.MinValue, 0), new(1, -1, NegativeZero),
            new(BitConverter.Int32BitsToSingle(0x3f800001), BitConverter.Int32BitsToSingle(0x3f7ffffe), -1)];
        Vector3[] positions = [Vector3.Zero, new(NegativeZero, 0, NegativeZero),
            new(-1, float.Epsilon, -MinNormal), new(float.MaxValue, float.MinValue, 1)];
        float[] scales = [0, NegativeZero, float.Epsilon, .5f, 1, 2, float.MaxValue];
        var vertices = new List<OriginalOutlineVertex>();
        foreach (var normal in normals)
        foreach (var position in positions)
        foreach (var scale in scales)
        foreach (var mode in new[] { OriginalOutlineVertexMode.Expand, OriginalOutlineVertexMode.Inward })
        for (int material = 0; material < Widths.Length; material++)
            vertices.Add(new(position, normal, mode, scale, material));

        var source = vertices.ToArray();
        foreach (float modelBase in new[] { 0f, NegativeZero, float.Epsilon, 1e-40f, .0028f, .5f, 1, 2, float.MaxValue, float.PositiveInfinity, float.NaN })
            AssertScalarBits(source, modelBase, Widths);
    }

    [Fact]
    public void UnderflowOverflowAndLastAddMustNotBeReassociatedOrFused()
    {
        OriginalOutlineVertex[] vertices =
        [
            new(Vector3.Zero, new(float.Epsilon, 0, 0), OriginalOutlineVertexMode.Expand, 2, 0),
            new(Vector3.Zero, new(float.MaxValue, 0, 0), OriginalOutlineVertexMode.Expand, 0, 0),
            new(new(-1, 0, 0), new(BitConverter.Int32BitsToSingle(0x3f800001), 0, 0), OriginalOutlineVertexMode.Expand, 1, 1),
            new(new(NegativeZero, NegativeZero, NegativeZero), new(float.Epsilon, -float.Epsilon, 0), OriginalOutlineVertexMode.Inward, float.MaxValue, 0),
        ];
        float[] widths = [1, BitConverter.Int32BitsToSingle(0x3f7ffffe)];
        AssertScalarBits(vertices, .5f, widths); // epsilon * .5 rounds to zero before * 2.
        AssertScalarBits(vertices, 2, widths); // MaxValue * 2 overflows before * 0 creates NaN.
        AssertScalarBits(vertices, 1, widths); // Last product rounds to 1 before adding -1.
    }

    [Fact]
    public void RandomFiniteBitPatternsAndNonMultipleLengthsMatchScalarBits()
    {
        var random = new Random(1714);
        float Finite(bool nonnegative = false)
        {
            int bits = unchecked((int)random.NextInt64(0, 1L << 32));
            if ((bits & 0x7f800000) == 0x7f800000) bits ^= 0x00800000;
            if (nonnegative) bits &= int.MaxValue;
            return BitConverter.Int32BitsToSingle(bits);
        }
        var vertices = new OriginalOutlineVertex[8203];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = new(new(Finite(), Finite(), Finite()), new(Finite(), Finite(), Finite()),
                i % 19 == 0 ? OriginalOutlineVertexMode.Inward : OriginalOutlineVertexMode.Expand,
                Finite(nonnegative: true), i % Widths.Length);
        foreach (int length in new[] { 0, 1, 2, 3, 4, 5, 4095, 4096, 4097, vertices.Length })
            AssertScalarBits(vertices.AsSpan(0, length).ToArray(), .0028f, Widths);
    }

    [Fact]
    public void DestinationBoundsAndTailArePreserved()
    {
        OriginalOutlineVertex[] vertices = [new(Vector3.One, Vector3.UnitY, OriginalOutlineVertexMode.Expand, 1, 0)];
        var sentinel = new Vector4(13, 17, 19, 23);
        Vector4[] output = [sentinel, sentinel, sentinel];
        OriginalOutlineBatchMath.BuildPositions(vertices, .25f, [2], output.AsSpan(1, 1));
        Assert.Equal(sentinel, output[0]); Assert.Equal(sentinel, output[2]);
        var tooShort = Array.Empty<Vector4>();
        Assert.Throws<ArgumentException>(() => OriginalOutlineBatchMath.BuildPositions(vertices, 1, Widths, tooShort));
        OriginalOutlineBatchMath.BuildPositions([], 1, [], output);
        Assert.Equal(sentinel, output[0]); Assert.Equal(sentinel, output[2]);
    }

    [Fact]
    public void InvalidModeDoesNotSilentlyBecomeExpand()
    {
        OriginalOutlineVertex[] vertices = [new(Vector3.Zero, Vector3.UnitY, (OriginalOutlineVertexMode)17, 1, 0)];
        var output = new Vector4[1];
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => OriginalOutlineBatchMath.BuildPositions(vertices, 1, [1], output));
        Assert.Equal("vertex", exception.ParamName); // Original scalar oracle owns this rejection.
    }

    [Fact]
    public void MaterialSelectionIsPerVertexAndInputIsConsumedSynchronously()
    {
        OriginalOutlineVertex[] source = Enumerable.Range(0, 17).Select(i => new OriginalOutlineVertex(
            new(i, -i, i * .25f), new(.3f, -.2f, .7f),
            i % 3 == 0 ? OriginalOutlineVertexMode.Inward : OriginalOutlineVertexMode.Expand, 1, i % 3)).ToArray();
        float[] widths = [.2f, .4f, .8f];
        var output = new Vector4[source.Length];
        OriginalOutlineBatchMath.BuildPositions(source, .125f, widths, output);
        var held = MemoryMarshal.AsBytes(output.AsSpan()).ToArray();
        Array.Fill(source, default); Array.Fill(widths, 0);
        Assert.Equal(held, MemoryMarshal.AsBytes(output.AsSpan()).ToArray());
    }

    private static void AssertScalarBits(OriginalOutlineVertex[] source, float modelBase, float[] widths)
    {
        var expected = new Vector4[source.Length]; var actual = new Vector4[source.Length];
        for (int i = 0; i < source.Length; i++)
            expected[i] = new(OriginalOutlineMath.ExpandPosition(source[i], modelBase, widths[source[i].SelectedMaterialIndex]), 1);
        OriginalOutlineBatchMath.BuildPositions(source, modelBase, widths, actual);
        Assert.Equal(MemoryMarshal.AsBytes(expected.AsSpan()).ToArray(), MemoryMarshal.AsBytes(actual.AsSpan()).ToArray());
        foreach (var value in actual) Assert.Equal(0x3f800000, BitConverter.SingleToInt32Bits(value.W));
    }
}
