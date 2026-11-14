namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Explicit numerical highlight channel, independent of diffuse coverage.</summary>
public enum D3D12HairHighlightChannel { Blue, Alpha }

/// <summary>Explicit Star A response-ramp / Wuthering response-map hair data in the
/// composed frame texture table. UV0 uses linear-repeat, mip-zero UNorm sampling as
/// an MDA policy. Highlight B (or explicitly Wuthering A) and independent shadow R
/// replace one primary highlight; the resolved PMX specular color supplies the MDA
/// palette. This does not supply reference emission, MatCap or packed normals.</summary>
public sealed record D3D12StarMcHairBinding(
    int HighlightCombinedTextureIndex,
    int ShadowCombinedTextureIndex,
    D3D12HairHighlightChannel HighlightChannel = D3D12HairHighlightChannel.Blue,
    float HighlightCoefficient = 0f,
    float HighlightBrightness = 2f,
    float ShadowCoefficient = .5f,
    float ShadowSoftness = .5f,
    float Intensity = .1f)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(HighlightCombinedTextureIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(ShadowCombinedTextureIndex);
        if (!Enum.IsDefined(HighlightChannel))
            throw new ArgumentOutOfRangeException(nameof(HighlightChannel), "Unknown numerical hair highlight channel.");
        if (!float.IsFinite(HighlightCoefficient) || HighlightCoefficient is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(HighlightCoefficient), "Hair highlight coefficient must be finite and in [0,1].");
        if (!float.IsFinite(HighlightBrightness) || HighlightBrightness is < -1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(HighlightBrightness), "Hair highlight brightness must be finite and in [-1,10].");
        if (!float.IsFinite(ShadowCoefficient) || ShadowCoefficient is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(ShadowCoefficient), "Hair shadow coefficient must be finite and in [0,1].");
        if (!float.IsFinite(ShadowSoftness) || ShadowSoftness is < .1f or > 1)
            throw new ArgumentOutOfRangeException(nameof(ShadowSoftness), "Hair shadow softness must be finite and in [.1,1].");
        if (!float.IsFinite(Intensity) || Intensity is < 0 or > 16)
            throw new ArgumentOutOfRangeException(nameof(Intensity), "Hair highlight intensity must be finite and in [0,16].");
    }

    internal static bool ConsumedEquivalent(D3D12StarMcHairBinding? a,
        D3D12StarMcHairBinding? b, bool compareTextureIndices = true)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return (!compareTextureIndices || (a.HighlightCombinedTextureIndex == b.HighlightCombinedTextureIndex
                && a.ShadowCombinedTextureIndex == b.ShadowCombinedTextureIndex))
            && a.HighlightChannel == b.HighlightChannel && a.HighlightCoefficient == b.HighlightCoefficient
            && a.HighlightBrightness == b.HighlightBrightness && a.ShadowCoefficient == b.ShadowCoefficient
            && a.ShadowSoftness == b.ShadowSoftness && a.Intensity == b.Intensity;
    }
}
