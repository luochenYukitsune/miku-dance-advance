namespace Mmd.Modern.Rendering.D3D12;

/// <summary>An explicitly selected material role, independent of names, colors and project data.</summary>
public enum D3D12CharacterMaterialRole { Body, Face, Eye, Hair }

/// <summary>Independent explicit Face and Eye geometric-shadow contrast controls.
/// Zero normal shade retains the lit normal response; geometric shadow one retains
/// the computed cast shadow. Eye retains the authored normal response and separately
/// reduces received cast-shadow contrast. Hair is ordinary unless its family-specific
/// numerical binding is explicitly supplied. Ordinary Body/Hair treatment is a separate
/// explicit opt-in, retaining authored coverage and the established Face/Eye response.</summary>
public sealed record D3D12CharacterMaterialStyle(
    D3D12CharacterMaterialRole Role = D3D12CharacterMaterialRole.Body,
    float FaceNormalShadeStrength = .35f,
    float FaceGeometricShadowStrength = 1f,
    D3D12FaceSdfBinding? FaceSdf = null,
    float EyeGeometricShadowStrength = .2f,
    D3D12GenshinNonMetallicSpecularBinding? GenshinNonMetallicSpecular = null,
    D3D12StarMcHairBinding? StarMcHair = null,
    D3D12PmxMaterialTreatment? PmxMaterialTreatment = null,
    D3D12EndfieldHairBinding? EndfieldHair = null,
    D3D12EndfieldRimBinding? EndfieldRim = null,
    D3D12GenshinHairShadowAtlasBinding? GenshinHairShadowAtlas = null,
    D3D12EndfieldMatcapBinding? EndfieldMatcap = null,
    D3D12GenshinMetallicBinding? GenshinMetallic = null,
    D3D12StarMcBodyBinding? StarMcBody = null)
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
        StarMcBody?.Validate();
        if (StarMcBody is not null && (Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair)
            || FaceSdf is not null || GenshinNonMetallicSpecular is not null || StarMcHair is not null
            || EndfieldHair is not null || EndfieldRim is not null || EndfieldMatcap is not null
            || GenshinHairShadowAtlas is not null || GenshinMetallic is not null || PmxMaterialTreatment is not null))
            throw new ArgumentException("Star/MC shared material requires explicit Body/Hair without another dedicated material or ordinary treatment.", nameof(StarMcBody));
        GenshinMetallic?.Validate();
        if(GenshinMetallic is {} metallic)
        {
            if(Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair)
                || FaceSdf is not null || StarMcHair is not null || EndfieldHair is not null
                || EndfieldRim is not null || EndfieldMatcap is not null || PmxMaterialTreatment is not null)
                throw new ArgumentException("Genshin metallic requires explicit Body/Hair without another family's data or ordinary treatment.",nameof(GenshinMetallic));
            if(GenshinNonMetallicSpecular is {} rb && rb.CombinedTextureIndex!=metallic.LightmapCombinedTextureIndex
                || GenshinHairShadowAtlas is {} atlas && atlas.LightmapCombinedTextureIndex!=metallic.LightmapCombinedTextureIndex)
                throw new ArgumentException("Co-bound Genshin metallic, R/B and atlas must use the same numerical UV0 lightmap.",nameof(GenshinMetallic));
        }
        GenshinHairShadowAtlas?.Validate();
        if (GenshinHairShadowAtlas is not null && (Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair)
            || FaceSdf is not null || StarMcHair is not null || EndfieldHair is not null
            || EndfieldRim is not null || EndfieldMatcap is not null || PmxMaterialTreatment is not null))
            throw new ArgumentException("Genshin shared atlas requires explicit Body/Hair without other-family dedicated material data or ordinary treatment.", nameof(GenshinHairShadowAtlas));
        FaceSdf?.Validate();
        GenshinNonMetallicSpecular?.Validate();
        StarMcHair?.Validate();
        EndfieldHair?.Validate();
        EndfieldRim?.Validate();
        EndfieldMatcap?.Validate();
        if (EndfieldMatcap is not null && (Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair)
            || FaceSdf is not null || GenshinNonMetallicSpecular is not null || StarMcHair is not null
            || GenshinHairShadowAtlas is not null || PmxMaterialTreatment is not null))
            throw new ArgumentException("Explicit Endfield MatCap requires Body or Hair without other-family dedicated material data or ordinary treatment.", nameof(EndfieldMatcap));
        if (EndfieldMatcap is { CoupleEndfieldPalette: true } matcap)
        {
            if (EndfieldRim is { } rim)
            {
                if (matcap.BaseColor != rim.BaseColor)
                    throw new ArgumentException("Coupled Endfield MatCap and rim require the same explicit BaseColor palette.", nameof(EndfieldMatcap));
                if (matcap.BaselineStrength != rim.RangeStrength + rim.RadialStrength)
                    throw new ArgumentException("Coupled Endfield MatCap BaselineStrength must equal rim RangeStrength + RadialStrength.", nameof(EndfieldMatcap));
                if (EndfieldHair is not null && rim.RadialStrength != 0)
                    throw new ArgumentException("Coupled Endfield MatCap with actual radial Hair requires rim RadialStrength zero to avoid counting radial strength twice.", nameof(EndfieldMatcap));
            }
            if (EndfieldHair is { } radial && matcap.BaseColor != radial.PaletteColor)
                throw new ArgumentException("Coupled Endfield MatCap and radial Hair require the same explicit BaseColor / PaletteColor.", nameof(EndfieldMatcap));
        }
        if (EndfieldRim is not null && (Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair)
            || FaceSdf is not null || GenshinNonMetallicSpecular is not null || StarMcHair is not null))
            throw new ArgumentException("Explicit Endfield rim requires Body or Hair without other-family dedicated material data.", nameof(EndfieldRim));
        PmxMaterialTreatment?.Validate();
        if (PmxMaterialTreatment is { HairHighlightStrength: > 0 } && Role != D3D12CharacterMaterialRole.Hair)
            throw new ArgumentException("Generic hair highlights require an explicit Hair material role.", nameof(PmxMaterialTreatment));
        if (PmxMaterialTreatment is not null && (Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair)
            || FaceSdf is not null || GenshinNonMetallicSpecular is not null || StarMcHair is not null || EndfieldHair is not null || EndfieldRim is not null || EndfieldMatcap is not null))
            throw new ArgumentException("Ordinary-PMX material treatment requires explicitly selected Body or Hair without dedicated material data.", nameof(PmxMaterialTreatment));
        if (EndfieldHair is not null && (Role != D3D12CharacterMaterialRole.Hair
            || FaceSdf is not null || GenshinNonMetallicSpecular is not null || StarMcHair is not null))
            throw new ArgumentException("Dedicated Endfield radial hair requires Hair without other dedicated material data.", nameof(EndfieldHair));
        if (FaceSdf is not null && GenshinNonMetallicSpecular is not null)
            throw new ArgumentException("Dedicated FaceSDF and hair nonmetallic specular cannot share a material.", nameof(GenshinNonMetallicSpecular));
        if (FaceSdf is not null && Role != D3D12CharacterMaterialRole.Face)
            throw new ArgumentException("Dedicated FaceSDF requires an explicitly selected Face material.", nameof(FaceSdf));
        if (GenshinNonMetallicSpecular is not null && Role != D3D12CharacterMaterialRole.Hair)
            throw new ArgumentException("Dedicated Genshin nonmetallic specular requires an explicitly selected Hair material.", nameof(GenshinNonMetallicSpecular));
        if (StarMcHair is not null && (FaceSdf is not null || GenshinNonMetallicSpecular is not null))
            throw new ArgumentException("Dedicated Star/Wuthering hair cannot share FaceSDF or Genshin nonmetallic specular data.", nameof(StarMcHair));
        if (StarMcHair is not null && Role != D3D12CharacterMaterialRole.Hair)
            throw new ArgumentException("Dedicated Star/Wuthering hair requires an explicitly selected Hair material.", nameof(StarMcHair));
    }
}
