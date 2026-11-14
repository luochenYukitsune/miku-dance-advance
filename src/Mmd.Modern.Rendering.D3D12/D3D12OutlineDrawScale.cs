using System.Numerics;
using Mmd.Modern.Core;
using Vortice.Direct3D12;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Shared source inputs plus a per-draw multiplier. No material's factor enters the
/// selected-material width table or another draw's expanded stream; inward geometry keeps its rule.</summary>
internal static class D3D12OutlineDrawScale
{
    internal static Vector3 Position(OriginalOutlineVertex source, Vector4 expanded, float scale)
    {
        var position = new Vector3(expanded.X, expanded.Y, expanded.Z);
        return scale == 1 || source.Mode == OriginalOutlineVertexMode.Inward
            ? position : source.Position + (position - source.Position) * scale;
    }

    internal static OriginalVisibilityBounds MaterialBounds(OriginalFrameScene frame, int materialIndex,
        D3D12ExpandedPositions positions)
    {
        var scene = frame.Surface.Snapshot;
        var range = scene.Materials[materialIndex];
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        float scale = frame.Materials[materialIndex].OutlineWidthScale;
        for (int i = range.StartIndex; i < range.StartIndex + range.IndexCount; i++)
        {
            int vertex = (int)scene.Indices[i];
            var position = Position(frame.Outline!.Vertices[vertex], positions[vertex], scale);
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                return new(new(float.NaN), new(float.NaN));
            min = Vector3.Min(min, position); max = Vector3.Max(max, position);
        }
        return new(min, max);
    }

    private static Vector4 Source(OriginalOutlineVertex vertex)
        => new(vertex.Position, vertex.Mode == OriginalOutlineVertexMode.Expand ? 1 : 0);

    // Fixed stream names retain one current source payload per cache. Replacements use the
    // existing frame-fence leases, so material count and distinct scale values add no streams.
    internal static ID3D12Resource SourceBuffer(OriginalOutlineData data, D3D12GpuCache cache)
        => cache.BufferByIdentity("outline-width-source", ResourceStates.VertexAndConstantBuffer, data.Vertices,
            () => data.Vertices.Select(Source).ToArray());

    private readonly record struct SourcePairIdentity(object Current, object Previous);
    private readonly record struct SourcePair(Vector4 Current, Vector4 Previous);
    internal static ID3D12Resource SourcePairBuffer(OriginalOutlineData current, OriginalOutlineData previous, D3D12GpuCache cache)
        => cache.BufferByIdentity("outline-width-temporal-source", ResourceStates.VertexAndConstantBuffer,
            new SourcePairIdentity(current.Vertices, previous.Vertices), () =>
            {
                var result = new SourcePair[current.VertexCount];
                for (int i = 0; i < result.Length; i++) result[i] = new(Source(current.Vertices[i]), Source(previous.Vertices[i]));
                return result;
            });
}
