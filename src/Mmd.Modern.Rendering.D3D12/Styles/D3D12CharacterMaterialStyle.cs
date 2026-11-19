using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>An explicitly selected material role, independent of names, colors and project data.</summary>
public enum D3D12CharacterMaterialRole { Body, Face, Eye, Hair }

/// <summary>
/// Generic per-material rendering parameters and optional plugin draw data.
/// Face/Eye controls attenuate normal and geometric shadow contrast; ordinary Body/Hair treatment
/// and stylized shading adapt PMX materials without dedicated textures.
/// </summary>
public sealed record D3D12CharacterMaterialStyle(
    D3D12CharacterMaterialRole Role = D3D12CharacterMaterialRole.Body,
    float FaceNormalShadeStrength = .35f,
    float FaceGeometricShadowStrength = 1f,
    float EyeGeometricShadowStrength = .2f,
    D3D12PmxMaterialTreatment? PmxMaterialTreatment = null,
    D3D12StylizedShading? StylizedShading = null,
    CharacterToonResponse? ToonResponse = null,
    bool PreserveDirectLightPalette = false,
    D3D12PluginDrawData? PluginDraw = null)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Role)) throw new ArgumentOutOfRangeException(nameof(Role), "Unknown character material role.");
        if (!float.IsFinite(FaceNormalShadeStrength) || FaceNormalShadeStrength is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(FaceNormalShadeStrength), "Face normal shade strength must be finite and between zero and one.");
        if (!float.IsFinite(FaceGeometricShadowStrength) || FaceGeometricShadowStrength is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(FaceGeometricShadowStrength), "Face geometric shadow strength must be finite and between zero and one.");
        if (!float.IsFinite(EyeGeometricShadowStrength) || EyeGeometricShadowStrength is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(EyeGeometricShadowStrength), "Eye geometric shadow strength must be finite and between zero and one.");
        PmxMaterialTreatment?.Validate();
        if (PmxMaterialTreatment is { HairHighlightStrength: > 0 } && Role != D3D12CharacterMaterialRole.Hair)
            throw new ArgumentException("Generic hair highlights require an explicit Hair material role.", nameof(PmxMaterialTreatment));
        if (PmxMaterialTreatment is not null && Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair))
            throw new ArgumentException("Ordinary-PMX material treatment requires explicitly selected Body or Hair.", nameof(PmxMaterialTreatment));
        StylizedShading?.Validate();
        if (ToonResponse is not null) CharacterToonMath.Validate(ToonResponse);
        PluginDraw?.Validate();
        if (PluginDraw is { TreatmentOverride: not null } && !string.Equals(PluginDraw.ShaderModuleId, "default", StringComparison.Ordinal))
            throw new ArgumentException("Ordinary-PMX material treatment override cannot be combined with a dedicated plugin shader module.", nameof(PluginDraw));
    }
}
