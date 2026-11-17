using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>The modern renderer's fixed-resolution, tightly packed shadow face allocation.
/// Recording, memory admission and preview resource identity use this same layout.</summary>
public readonly record struct NativeLightShadowAtlasLayout(int FaceCount, int Columns, int Rows)
{
    public const int TileSize = 512;
    public int Width => Columns * TileSize;
    public int Height => Rows * TileSize;
    public static NativeLightShadowAtlasLayout Create(SceneLightingSnapshot? lighting)
    {
        int faces = lighting?.EffectiveLights.Where(Contributes).Where(light => light.CastShadows)
            .Sum(light => light.Kind is SceneLightKind.Point or SceneLightKind.Area ? 6 : 1) ?? 0;
        if (faces == 0) return default;
        int columns = (int)Math.Ceiling(Math.Sqrt(faces));
        return new(faces, columns, (faces + columns - 1) / columns);
    }
    internal static bool Contributes(NativeSceneLight light) => light.Enabled && light.Intensity > 0 && light.Color != Vector3.Zero;
    public (int X, int Y) TileOrigin(int face)
    {
        if ((uint)face >= (uint)FaceCount) throw new ArgumentOutOfRangeException(nameof(face));
        return (face % Columns * TileSize, face / Columns * TileSize);
    }
}
