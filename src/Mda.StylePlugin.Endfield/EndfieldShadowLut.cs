using System.Numerics;
using System.Runtime.InteropServices;

namespace Mda.StylePlugin.Endfield;

/// <summary>
/// Engine-defined 3x257 FP16-quantized shadow ColorBand lookup table matching D3D12EndfieldShadowLut verbatim.
/// </summary>
internal static class EndfieldShadowLut
{
    internal const int RowCount = 3;
    internal const int TexelsPerRow = 257;
    internal const int ElementCount = RowCount * TexelsPerRow;
    internal const int ElementStrideBytes = 16;
    internal const string DataKey = "endfield-shadow-lut-v1";

    private static readonly byte[] PackedBytes = CreateBytes();
    internal static ReadOnlyMemory<byte> Bytes => PackedBytes;

    private static byte[] CreateBytes()
    {
        var table = new Vector4[ElementCount];
        for (int row = 0; row < RowCount; row++)
        {
            Vector3 left = row == 2 ? Vector3.One : new Vector3(0.5f);
            Vector3 middle = row switch
            {
                0 => new Vector3(0.800000011920929f),
                1 => new Vector3(1f, 0.8531801104545593f, 0.6814245581626892f),
                _ => new Vector3(1f, 0.8999999761581421f, 0.75f)
            };
            for (int i = 0; i <= 256; i++)
            {
                float x = i / 256f;
                float factor = x <= 0.5f ? (x - 0.5f) / -0.5f : (x - 1f) / -0.5f;
                Vector3 a = x <= 0.5f ? middle : Vector3.One;
                Vector3 b = x <= 0.5f ? left : middle;
                Vector3 color = (1f - factor) * a + factor * b;
                table[row * TexelsPerRow + i] = new Vector4((float)(Half)color.X, (float)(Half)color.Y, (float)(Half)color.Z, 1f);
            }
        }
        return MemoryMarshal.AsBytes(table.AsSpan()).ToArray();
    }
}
