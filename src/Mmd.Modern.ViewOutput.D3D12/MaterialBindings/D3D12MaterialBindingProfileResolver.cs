using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Detached source-local inputs for the existing View renderers. No device, evaluated
/// skeleton or composed-frame texture ordinal is created while restoring a model mapping.</summary>
public sealed class D3D12ResolvedMaterialBindingProfile
{
    public IReadOnlyList<D3D12ViewCharacterMaterialBinding> CharacterMaterials { get; }
    public IReadOnlyList<D3D12ViewOutlineWidthBinding> OutlineWidths { get; }
    public IReadOnlyList<D3D12ViewFaceSdfBinding> FaceBindings { get; }
    public IReadOnlyList<D3D12ViewGenshinHairBinding> GenshinHairBindings { get; }
    public IReadOnlyList<D3D12ViewStarMcHairBinding> StarMcHairBindings { get; }
    public IReadOnlyList<D3D12ViewGenshinHairAtlasBinding> GenshinHairAtlasBindings { get; }
    public IReadOnlyList<D3D12ViewEndfieldHairBinding> EndfieldHairBindings { get; }
    public IReadOnlyList<D3D12ViewEndfieldMatcapBinding> EndfieldMatcapBindings { get; }

    public IReadOnlyList<D3D12ViewGenshinMetallicBinding> GenshinMetallicBindings { get; }

    public IReadOnlyList<D3D12ViewStarMcBodyBinding> StarMcBodyBindings { get; }

    internal D3D12ResolvedMaterialBindingProfile(List<D3D12ViewCharacterMaterialBinding> materials,
        List<D3D12ViewFaceSdfBinding> faces, List<D3D12ViewGenshinHairBinding> genshin,
        List<D3D12ViewStarMcHairBinding> starMc, List<D3D12ViewGenshinHairAtlasBinding> atlas,
        List<D3D12ViewEndfieldHairBinding> endfield, List<D3D12ViewEndfieldMatcapBinding> matcap,
        List<D3D12ViewGenshinMetallicBinding> metallic, List<D3D12ViewStarMcBodyBinding> bodies,
        List<D3D12ViewOutlineWidthBinding> outlineWidths)
    {
        StarMcBodyBindings = D3D12ViewStarMcBodyBindings.Detach(bodies);
        CharacterMaterials = D3D12ViewCharacterMaterials.Detach(materials);
        OutlineWidths = Array.AsReadOnly(outlineWidths.ToArray());
        FaceBindings = D3D12ViewFaceSdfBindings.Detach(faces);
        GenshinHairBindings = D3D12ViewGenshinHairBindings.Detach(genshin);
        StarMcHairBindings = D3D12ViewStarMcHairBindings.Detach(starMc);
        GenshinHairAtlasBindings = D3D12ViewGenshinHairAtlasBindings.Detach(atlas);
        EndfieldHairBindings = D3D12ViewEndfieldHairBindings.Detach(endfield);
        EndfieldMatcapBindings = D3D12ViewEndfieldMatcapBindings.Detach(matcap);
        GenshinMetallicBindings = D3D12ViewGenshinMetallicBindings.Detach(metallic);
        D3D12ViewFaceSdfBindings.ValidateCharacterMaterials(CharacterMaterials, FaceBindings);
    }
}

/// <summary>Validates an explicit immutable rest input before creating any renderer. A profile is
/// model-specific rather than a general style preset. All saved image guards are checked even for
/// dormant families; selecting MMD never injects a dedicated numerical surface payload.</summary>
public static class D3D12MaterialBindingProfileResolver
{
    public static D3D12ResolvedMaterialBindingProfile Resolve(D3D12MaterialBindingProfile profile,
        OriginalFrameScene sourceRest, Guid objectId, string pipelineId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(sourceRest);
        if (objectId == Guid.Empty) throw new ArgumentException("A model mapping requires an explicit object identity.", nameof(objectId));
        if (pipelineId is not ("mmd" or "genshin" or "star-rail" or "wuthering-waves" or "endfield"))
            throw new ArgumentException("The caller must select a known pipeline; it is not inferred from material names.", nameof(pipelineId));
        var layout = D3D12MaterialBindingLayout.Capture(sourceRest);
        layout.ValidateFingerprint(profile.LayoutFingerprint, profile.LayoutVersion);
        var materials = new List<D3D12ViewCharacterMaterialBinding>();
        var faces = new List<D3D12ViewFaceSdfBinding>();
        var genshin = new List<D3D12ViewGenshinHairBinding>();
        var starMc = new List<D3D12ViewStarMcHairBinding>();
        var atlases = new List<D3D12ViewGenshinHairAtlasBinding>();
        var endfield = new List<D3D12ViewEndfieldHairBinding>();
        var matcaps = new List<D3D12ViewEndfieldMatcapBinding>();
        var metallics = new List<D3D12ViewGenshinMetallicBinding>();
        var bodies = new List<D3D12ViewStarMcBodyBinding>();
        var outlineWidths = new List<D3D12ViewOutlineWidthBinding>();
        foreach (var outline in profile.OutlineWidths)
        {
            if ((uint)outline.LocalMaterialIndex >= (uint)layout.MaterialCount)
                throw new ArgumentException("The selected outline material is outside the explicit source-rest layout.", nameof(profile));
            outlineWidths.Add(new(objectId, outline.LocalMaterialIndex, outline.Scale));
        }
        foreach (var row in profile.Materials)
        {
            if ((uint)row.LocalMaterialIndex >= (uint)layout.MaterialCount)
                throw new ArgumentException("The selected material is outside the explicit source-rest layout.", nameof(profile));
            materials.Add(new(objectId, row.LocalMaterialIndex,
                new(row.Role, row.FaceNormalShadeStrength, row.FaceGeometricShadowStrength,
                    EyeGeometricShadowStrength: row.EyeGeometricShadowStrength, PmxMaterialTreatment: row.PmxMaterialTreatment,
                    EndfieldRim: pipelineId == "endfield" ? row.EndfieldRim : null)));
            if (row.Face is { } face)
            {
                layout.ValidateTexture(face.Primary);
                if (face.Secondary is { } secondary) layout.ValidateTexture(secondary);
                if (face.PipelineId == pipelineId)
                    faces.Add(new(objectId, row.LocalMaterialIndex, face.Primary.SourceSceneTextureOrdinal,
                        new D3D12ViewFaceHeadBasis.FixedWorld(face.HeadWorldForward, face.HeadWorldRight),
                        face.Transition, face.Offset, face.Encoding, face.Secondary?.SourceSceneTextureOrdinal ?? -1));
            }
            if (row.GenshinHair is { } hair)
            {
                layout.ValidateTexture(hair.Texture);
                if (pipelineId == "genshin")
                    genshin.Add(new(objectId, row.LocalMaterialIndex, hair.Texture.SourceSceneTextureOrdinal,
                        hair.GlobalSpecularScale, hair.Shininess, hair.Multiplier, hair.Intensity));
            }
            if (row.GenshinHairAtlas is { } atlas)
            {
                layout.ValidateTexture(atlas.Lightmap); layout.ValidateTexture(atlas.Atlas);
                if (pipelineId == "genshin")
                    atlases.Add(new(objectId, row.LocalMaterialIndex, atlas.Lightmap.SourceSceneTextureOrdinal,
                        atlas.Atlas.SourceSceneTextureOrdinal, atlas.AtlasEncoding, atlas.ExplicitRowId, atlas.DayNight,
                        atlas.ShadowPosition, atlas.ShadowRampWidth, atlas.VertexWidth, atlas.VertexAo,
                        atlas.UseLightmapAo, atlas.UseVertexColorAo));
            }
            if (row.StarMcHair is { } otherHair)
            {
                layout.ValidateTexture(otherHair.Highlight); layout.ValidateTexture(otherHair.Shadow);
                if (otherHair.PipelineId == pipelineId)
                    starMc.Add(new(objectId, row.LocalMaterialIndex, otherHair.Highlight.SourceSceneTextureOrdinal,
                        otherHair.Shadow.SourceSceneTextureOrdinal, otherHair.HighlightChannel, otherHair.HighlightCoefficient,
                        otherHair.HighlightBrightness, otherHair.ShadowCoefficient, otherHair.ShadowSoftness, otherHair.Intensity));
            }
            if (row.StarMcBody is { } body)
            {
                layout.ValidateTexture(body.Normal); layout.ValidateTexture(body.Detail);
                layout.ValidateTexture(body.Mask); layout.ValidateTexture(body.Matcap);
                D3D12ViewEndfieldMatcapBindings.ValidateUv(sourceRest.Surface.Snapshot, body.UvChannel);
                if (body.PipelineId == pipelineId)
                    bodies.Add(new(objectId, row.LocalMaterialIndex, body.Normal.SourceSceneTextureOrdinal,
                        body.Detail.SourceSceneTextureOrdinal, body.Mask.SourceSceneTextureOrdinal, body.Matcap.SourceSceneTextureOrdinal,
                        body.MatcapEncoding, body.UvChannel, body.NormalStrength, body.DetailStrength, body.MetallicSaturation,
                        body.HighlightCoefficient, body.HighlightStrength, body.RotationRadians, body.ShadowCoefficient,
                        body.ShadowSoftness, body.ShadowColorStrength, body.WarmBandStrength, body.ShadowColor,
                        body.RimColor, body.RimStrength, body.RimSize, body.RimDirectionRadians));
            }
            if (row.EndfieldHair is { } radialHair)
            {
                layout.ValidateTexture(radialHair.Texture);
                if (sourceRest.Surface.Snapshot.RawRadialSeeds is null)
                    throw new ArgumentException("Saved Endfield hair requires an explicitly authored source radial seed stream.", nameof(sourceRest));
                if (pipelineId == "endfield")
                    endfield.Add(new(objectId, row.LocalMaterialIndex, radialHair.Texture.SourceSceneTextureOrdinal,
                        radialHair.PaletteColor, radialHair.Brightness));
            }
            if (row.GenshinMetallic is { } metallic)
            {
                layout.ValidateTexture(metallic.Lightmap); layout.ValidateTexture(metallic.Matcap);
                if (metallic.Specular?.Ramp is { } ramp) layout.ValidateTexture(ramp);
                if (pipelineId == "genshin")
                    metallics.Add(new(objectId, row.LocalMaterialIndex, metallic.Lightmap.SourceSceneTextureOrdinal,
                        metallic.Matcap.SourceSceneTextureOrdinal, metallic.MatcapEncoding, metallic.DarkColor,
                        metallic.LightColor, metallic.ShadowMultiplyColor, metallic.EnableMetallics, metallic.TileScale,
                        metallic.Brightness, metallic.LitMode, metallic.UniformLitFactor, metallic.Specular?.ToBinding()));
            }
            if (row.EndfieldMatcap is { } matcap)
            {
                layout.ValidateTexture(matcap.Normal); layout.ValidateTexture(matcap.P); layout.ValidateTexture(matcap.Matcap);
                if (matcap.Palette?.EmissionTexture is { } emission) layout.ValidateTexture(emission);
                if (matcap.Palette?.ReceivedLightTexture is { } light) layout.ValidateTexture(light);
                if (matcap.Palette?.BaseImage?.BaseTexture is { } baseImage) layout.ValidateTexture(baseImage);
                if (matcap.Palette?.BaseImage?.DetailTexture is { } detail) layout.ValidateTexture(detail);
                if (matcap.Palette?.BaseImage?.ShadowTexture is { } shadow) layout.ValidateTexture(shadow);
                D3D12ViewEndfieldMatcapBindings.ValidateUv(sourceRest.Surface.Snapshot, matcap.UvChannel);
                if (pipelineId == "endfield")
                    matcaps.Add(new(objectId, row.LocalMaterialIndex, matcap.Normal.SourceSceneTextureOrdinal,
                        matcap.P.SourceSceneTextureOrdinal, matcap.Matcap.SourceSceneTextureOrdinal, matcap.MatcapEncoding,
                        matcap.UvChannel, matcap.BaseColor, matcap.HighlightColor, matcap.NormalStrength,
                        matcap.RotationRadians, matcap.HighlightCoefficient, matcap.ShadowHardness,
                        matcap.LightControlDirection, matcap.BaselineStrength, matcap.CoupleEndfieldPalette, matcap.Palette?.ToBinding(), matcap.HighlightRange));
            }
        }
        return new(materials, faces, genshin, starMc, atlases, endfield, matcaps, metallics, bodies, outlineWidths);
    }
}
