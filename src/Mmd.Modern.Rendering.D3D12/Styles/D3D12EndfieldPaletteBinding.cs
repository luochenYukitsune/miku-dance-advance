using System.Numerics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Explicit source of a numerical Blue channel or linear received-light RGB.</summary>
public enum D3D12EndfieldPaletteInputMode { Uniform, Texture, RendererDiffuse }

/// <summary>Downstream partial emission, authored received light and saved UV wave.
/// This child inherits its parent's selected effective UV and BaseColor (Cpre), and
/// requires CoupleEndfieldPalette. ReceivedLightLinearRgb is an authored boundary,
/// not Blender ShaderToRGB. RendererDiffuse instead evaluates current MDA primary/native
/// lights and their geometric visibility with the parent's packed normal at strength one.
/// Only its additional surface contribution spends the remaining native/GI light budget.
/// Texture modes
/// select numerical Blue or LinearRgb, bilinear-repeat at mip zero; alpha is unused.
/// This changes surface RGB only, not coverage or scene/GI emission.</summary>
public sealed record D3D12EndfieldPaletteBinding(
    Vector3 ReceivedLightLinearRgb,
    float EmissionStrength = 0,
    float UniformEmissionBlue = 0,
    float LightInfluence = 0,
    float Density = 0,
    D3D12EndfieldPaletteInputMode EmissionInputMode = D3D12EndfieldPaletteInputMode.Uniform,
    int? EmissionCombinedTextureIndex = null,
    D3D12EndfieldPaletteInputMode ReceivedLightInputMode = D3D12EndfieldPaletteInputMode.Uniform,
    int? ReceivedLightCombinedTextureIndex = null,
    D3D12EndfieldBaseImageBinding? BaseImage = null)
{
    internal bool ConsumesEmissionTexture => EmissionInputMode == D3D12EndfieldPaletteInputMode.Texture && EmissionStrength != 0;
    internal bool ConsumesReceivedLightTexture => ReceivedLightInputMode == D3D12EndfieldPaletteInputMode.Texture && Math.Clamp(LightInfluence, 0, 1) > 0;

    public void Validate()
    {
        BaseImage?.Validate();
        Scalar(ReceivedLightLinearRgb.X, 16, nameof(ReceivedLightLinearRgb));
        Scalar(ReceivedLightLinearRgb.Y, 16, nameof(ReceivedLightLinearRgb));
        Scalar(ReceivedLightLinearRgb.Z, 16, nameof(ReceivedLightLinearRgb));
        Scalar(UniformEmissionBlue, 16, nameof(UniformEmissionBlue));
        Scalar(EmissionStrength, 32, nameof(EmissionStrength));
        Scalar(LightInfluence, 32, nameof(LightInfluence));
        Scalar(Density, 32, nameof(Density));
        Input(EmissionInputMode, EmissionCombinedTextureIndex, nameof(EmissionInputMode), nameof(EmissionCombinedTextureIndex));
        Input(ReceivedLightInputMode, ReceivedLightCombinedTextureIndex, nameof(ReceivedLightInputMode), nameof(ReceivedLightCombinedTextureIndex));
        if (EmissionInputMode == D3D12EndfieldPaletteInputMode.RendererDiffuse)
            throw new ArgumentException("Renderer-derived diffuse is a received-light provider, not an emission-mask input.", nameof(EmissionInputMode));
    }

    internal static bool ConsumedEquivalent(D3D12EndfieldPaletteBinding? a,
        D3D12EndfieldPaletteBinding? b, bool compareTextureIndices = true)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Density != b.Density || a.EmissionStrength != b.EmissionStrength
            || !D3D12EndfieldBaseImageBinding.ConsumedEquivalent(a.BaseImage,b.BaseImage,compareTextureIndices)) return false;
        if (a.EmissionStrength != 0 && (a.EmissionInputMode != b.EmissionInputMode
            || (a.EmissionInputMode == D3D12EndfieldPaletteInputMode.Uniform
                ? a.UniformEmissionBlue != b.UniformEmissionBlue
                : compareTextureIndices && a.EmissionCombinedTextureIndex != b.EmissionCombinedTextureIndex))) return false;
        float af = Math.Clamp(a.LightInfluence, 0, 1), bf = Math.Clamp(b.LightInfluence, 0, 1);
        return af == bf && (af == 0 || a.ReceivedLightInputMode == b.ReceivedLightInputMode
            && (a.ReceivedLightInputMode == D3D12EndfieldPaletteInputMode.Uniform
                ? a.ReceivedLightLinearRgb == b.ReceivedLightLinearRgb
                : a.ReceivedLightInputMode == D3D12EndfieldPaletteInputMode.RendererDiffuse
                    || !compareTextureIndices || a.ReceivedLightCombinedTextureIndex == b.ReceivedLightCombinedTextureIndex));
    }

    private static void Input(D3D12EndfieldPaletteInputMode mode, int? index, string modeName, string indexName)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(modeName);
        if (mode == D3D12EndfieldPaletteInputMode.Texture)
        {
            if (index is null or < 0) throw new ArgumentOutOfRangeException(indexName, "A texture input requires an explicitly selected nonnegative texture index.");
        }
        else if (index is not null) throw new ArgumentException("A non-texture input cannot carry a texture index.", indexName);
    }

    private static void Scalar(float value, float bound, string name)
    {
        if (!float.IsFinite(value) || value < -bound || value > bound)
            throw new ArgumentOutOfRangeException(name, $"Explicit palette inputs must be finite and in [-{bound},{bound}].");
    }
}
