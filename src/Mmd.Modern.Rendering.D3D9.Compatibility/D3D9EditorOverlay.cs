using System.Numerics;
using System.Runtime.InteropServices;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>Preview-only primitives, composed after the original MME scene. Sizes are physical pixels.</summary>
public enum D3D9EditorOverlayShape { Segment, Circle, Square, Wedge }

public sealed record D3D9EditorOverlayPrimitive(Vector2 Start, Vector2 End, Vector4 Color,
    float Width = 2.5f, D3D9EditorOverlayShape Shape = D3D9EditorOverlayShape.Segment,
    float Radius = 5, Vector2? DeviceDepth = null);

[StructLayout(LayoutKind.Sequential)]
internal struct NativeEditorOverlayPrimitive
{
    internal Vector2 Start, End;
    internal Vector4 Color;
    internal float Width, Radius;
    internal Vector2 Depth;
    internal uint Shape, Flags;

    internal static NativeEditorOverlayPrimitive[] Freeze(IEnumerable<D3D9EditorOverlayPrimitive> primitives)
    {
        ArgumentNullException.ThrowIfNull(primitives);
        var rows = primitives.Take(1025).ToArray();
        if (rows.Length > 1024 || rows.Any(row => row is null || !Finite(row.Start) || !Finite(row.End)
            || !Finite(row.Color) || !float.IsFinite(row.Width) || row.Width <= 0
            || !float.IsFinite(row.Radius) || row.Radius <= 0 || !Enum.IsDefined(row.Shape)
            || row.DeviceDepth is { } depth && (!Finite(depth) || depth.X is < 0 or > 1
                || depth.Y is < 0 or > 1 || row.Shape != D3D9EditorOverlayShape.Segment)))
            throw new ArgumentException("Editor overlay primitives require finite values, positive sizes, valid shapes and clipped segment depth within the 1024 primitive budget.", nameof(primitives));
        return rows.Select(row => new NativeEditorOverlayPrimitive
        {
            Start = row.Start, End = row.End, Color = row.Color, Width = row.Width, Radius = row.Radius,
            Depth = row.DeviceDepth ?? Vector2.Zero, Shape = (uint)row.Shape, Flags = row.DeviceDepth is null ? 0U : 1U
        }).ToArray();
    }

    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y)
        && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
