using System.Numerics;
using System.Runtime.InteropServices;

namespace Mmd.Modern.Core;

/// <summary>Independent format-neutral UV1..UV4 vectors, separate from the 32-byte base vertex.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SceneAdditionalUvVertex(Vector4 Uv1, Vector4 Uv2, Vector4 Uv3, Vector4 Uv4);

/// <summary>Immutable vertex-order additional UV stream. Absence of additional channels is represented by null.</summary>
public sealed class SceneAdditionalUvs
{
    private readonly SceneAdditionalUvVertex[] vertices;
    public int ChannelCount { get; }
    public ReadOnlySpan<SceneAdditionalUvVertex> Vertices => vertices;

    public SceneAdditionalUvs(int channelCount, ReadOnlySpan<SceneAdditionalUvVertex> vertices)
    {
        if (channelCount is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(channelCount));
        foreach (var value in vertices)
            if (!Finite(value.Uv1) || !Finite(value.Uv2) || !Finite(value.Uv3) || !Finite(value.Uv4))
                throw new ArgumentException("Additional UV vectors must be finite.", nameof(vertices));
        ChannelCount = channelCount;
        this.vertices = vertices.ToArray();
    }

    private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y)
        && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
