namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Explicit UV0 Non-Color R/B numerical data in the composed frame texture table.
/// R controls nonmetallic highlight amplitude and B its strict threshold. Sampling is
/// fixed cubic B-spline repeat, independently of diffuse coverage and its sampler.
/// The authored PMX specular color supplies the MDA palette; metallic and ramps are separate.</summary>
public sealed record D3D12GenshinNonMetallicSpecularBinding(
    int CombinedTextureIndex,
    float GlobalSpecularScale = 1f,
    float Shininess = 10f,
    float Multiplier = .1f,
    float Intensity = 1f)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(CombinedTextureIndex);
        if (!float.IsFinite(GlobalSpecularScale) || GlobalSpecularScale is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(GlobalSpecularScale), "Global specular scale must be finite and in [0,2].");
        if (!float.IsFinite(Shininess) || Shininess is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(Shininess), "Nonmetallic shininess must be finite and in [0,64].");
        if (!float.IsFinite(Multiplier) || Multiplier is < 0 or > 16)
            throw new ArgumentOutOfRangeException(nameof(Multiplier), "Nonmetallic multiplier must be finite and in [0,16].");
        if (!float.IsFinite(Intensity) || Intensity is < 0 or > 16)
            throw new ArgumentOutOfRangeException(nameof(Intensity), "Nonmetallic intensity must be finite and in [0,16].");
    }

    internal static bool ConsumedEquivalent(D3D12GenshinNonMetallicSpecularBinding? a,
        D3D12GenshinNonMetallicSpecularBinding? b, bool compareTextureIndex = true)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return (!compareTextureIndex || a.CombinedTextureIndex == b.CombinedTextureIndex)
            && a.GlobalSpecularScale == b.GlobalSpecularScale && a.Shininess == b.Shininess
            && a.Multiplier == b.Multiplier && a.Intensity == b.Intensity;
    }
}
