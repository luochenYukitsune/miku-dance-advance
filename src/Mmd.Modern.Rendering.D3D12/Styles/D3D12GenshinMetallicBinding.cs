using System.Numerics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>The source local-group Lit Factor input. PrimaryResponse is an explicit MDA
/// adapter using the accepted Genshin normal band and primary real-shadow visibility.</summary>
public enum D3D12GenshinMetallicLitMode { PrimaryResponse, Uniform }

/// <summary>Explicit Genshin-v3.4 R&gt;.9 metallic MatCap palette adapter. Numerical lightmap
/// uses current UV0; ChannelPacked MatCap uses camera-derived UV. Both use mip-zero
/// Cubic B-spline Repeat. MatCap RGB is decoded before filtering; its Alpha is ignored.
/// Colors are source numeric values, converted once with plain Gamma2.2, not IEC sRGB.
/// Existing normalized world shading normal is the explicit GeometryNormal input;
/// source normal maps, secondary UVs and metallic specular are separate contracts.</summary>
public sealed record D3D12GenshinMetallicBinding(
    int LightmapCombinedTextureIndex,
    int MatcapCombinedTextureIndex,
    D3D12MatcapColorEncoding MatcapEncoding,
    Vector3 DarkColor,
    Vector3 LightColor,
    Vector3 ShadowMultiplyColor,
    float EnableMetallics = 1,
    float TileScale = 1,
    float Brightness = 1,
    D3D12GenshinMetallicLitMode LitMode = D3D12GenshinMetallicLitMode.PrimaryResponse,
    float UniformLitFactor = 1,
    D3D12GenshinMetallicSpecularBinding? Specular = null)
{
    /// <summary>Fixed explicit UV0 in this bounded adapter; no inferred secondary UV.</summary>
    public int UvChannel => 0;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(LightmapCombinedTextureIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(MatcapCombinedTextureIndex);
        if (!Enum.IsDefined(MatcapEncoding)) throw new ArgumentOutOfRangeException(nameof(MatcapEncoding));
        if (!Enum.IsDefined(LitMode)) throw new ArgumentOutOfRangeException(nameof(LitMode));
        Specular?.Validate();
        Color(DarkColor, nameof(DarkColor)); Color(LightColor, nameof(LightColor));
        Color(ShadowMultiplyColor, nameof(ShadowMultiplyColor));
        Scalar(EnableMetallics, nameof(EnableMetallics)); Scalar(TileScale, nameof(TileScale));
        Scalar(Brightness, nameof(Brightness)); Scalar(UniformLitFactor, nameof(UniformLitFactor));
    }

    internal static bool ConsumedEquivalent(D3D12GenshinMetallicBinding? a,
        D3D12GenshinMetallicBinding? b, bool compareTextureIndices = true)
        => ReferenceEquals(a, b) || a is not null && b is not null
            && (!compareTextureIndices || a.LightmapCombinedTextureIndex == b.LightmapCombinedTextureIndex
                && a.MatcapCombinedTextureIndex == b.MatcapCombinedTextureIndex)
            && a.MatcapEncoding == b.MatcapEncoding && a.DarkColor == b.DarkColor
            && a.LightColor == b.LightColor && a.ShadowMultiplyColor == b.ShadowMultiplyColor
            && a.EnableMetallics == b.EnableMetallics && a.TileScale == b.TileScale
            && a.Brightness == b.Brightness && a.LitMode == b.LitMode
            && (a.LitMode != D3D12GenshinMetallicLitMode.Uniform || a.UniformLitFactor == b.UniformLitFactor)
            && D3D12GenshinMetallicSpecularBinding.ConsumedEquivalent(a.Specular,b.Specular,compareTextureIndices);

    private static void Color(Vector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)
            || value.X is < -16 or > 16 || value.Y is < -16 or > 16 || value.Z is < -16 or > 16)
            throw new ArgumentOutOfRangeException(name, "Source numeric palette channels must be finite and in [-16,16].");
    }

    private static void Scalar(float value, string name)
    {
        if (!float.IsFinite(value) || value is < -32 or > 32)
            throw new ArgumentOutOfRangeException(name, "Explicit metallic scalar must be finite and in [-32,32].");
    }
}
