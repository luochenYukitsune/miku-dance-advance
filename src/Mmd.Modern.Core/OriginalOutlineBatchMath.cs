using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Mmd.Modern.Core;

/// <summary>Allocation-free batches of the existing original outline arithmetic.
/// The source and selected material widths are consumed synchronously. The caller owns the output.</summary>
public static class OriginalOutlineBatchMath
{
    /// <summary>Writes one Vector4 with W=1 per source vertex. Inputs normally come from validated
    /// OriginalOutlineData chunks. This does not compute or alter the model-base arithmetic.
    /// Destination must have at least source.Length values and must not alias either input.</summary>
    public static void BuildPositions(ReadOnlySpan<OriginalOutlineVertex> source, float modelBase,
        ReadOnlySpan<float> selectedMaterialWidths, Span<Vector4> destination)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException("Destination must contain one position per outline vertex.", nameof(destination));
        if (source.IsEmpty) return;
        // Preserve the original scalar handling of an overflowing/NaN model base. This is rare,
        // and avoids doing 0 * Infinity in the scratch SIMD lane when no XYZ lane needs it.
        if (Sse.IsSupported && float.IsFinite(modelBase))
            BuildSse(source, modelBase, selectedMaterialWidths, destination);
        else
            BuildScalar(source, modelBase, selectedMaterialWidths, destination);
    }

    private static void BuildScalar(ReadOnlySpan<OriginalOutlineVertex> source, float modelBase,
        ReadOnlySpan<float> selectedMaterialWidths, Span<Vector4> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            var vertex = source[i];
            float width = selectedMaterialWidths[vertex.SelectedMaterialIndex];
            destination[i] = new(OriginalOutlineMath.ExpandPosition(vertex, modelBase, width), 1);
        }
    }

    // Separate the unsupported-intrinsic body from the scalar fallback. No source packing,
    // native interop, raw struct loads, unsafe code, allocation, or retained caller spans.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BuildSse(ReadOnlySpan<OriginalOutlineVertex> source, float modelBase,
        ReadOnlySpan<float> selectedMaterialWidths, Span<Vector4> destination)
    {
        var b = Vector128.Create(modelBase);
        var inward = Vector128.Create(.005f);
        for (int i = 0; i < source.Length; i++)
        {
            var vertex = source[i];
            float width = selectedMaterialWidths[vertex.SelectedMaterialIndex];
            var p = Vector128.Create(vertex.Position.X, vertex.Position.Y, vertex.Position.Z, 0);
            var n = Vector128.Create(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z, 0);
            Vector128<float> result;
            if (vertex.Mode == OriginalOutlineVertexMode.Inward)
            {
                // Match p - round32(n * .005f). Do not replace subtraction with adding -n.
                result = Sse.Subtract(p, Sse.Multiply(n, inward));
            }
            else if (vertex.Mode == OriginalOutlineVertexMode.Expand)
            {
                // Separate SSE intrinsics retain each original binary32 rounding boundary.
                // Do not precompute base * scale * width, fuse multiply/add, normalize normals,
                // or skip the arithmetic for a zero factor: Inf * 0 can be the original result.
                var offset = Sse.Multiply(n, b);
                offset = Sse.Multiply(offset, Vector128.Create(vertex.VertexScale));
                offset = Sse.Multiply(offset, Vector128.Create(width));
                result = Sse.Add(p, offset);
            }
            else
            {
                // Unlike the prior isolated prototype, preserve the original invalid-mode error.
                destination[i] = new(OriginalOutlineMath.ExpandPosition(vertex, modelBase, width), 1);
                continue;
            }
            // The fourth SIMD lane is scratch; it is never the homogeneous output component.
            destination[i] = new(result.GetElement(0), result.GetElement(1), result.GetElement(2), 1);
        }
    }
}
