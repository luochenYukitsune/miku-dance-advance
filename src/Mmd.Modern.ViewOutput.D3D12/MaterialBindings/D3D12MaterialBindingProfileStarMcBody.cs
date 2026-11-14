using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Explicit source image guards for Star/MC Body. Numerical maps are never inferred
/// from diffuse artwork; the normal/detail selections stay guarded even on the Star branch.</summary>
public sealed record D3D12MaterialBindingProfileStarMcBody(string PipelineId,
    D3D12MaterialBindingProfileTexture Normal, D3D12MaterialBindingProfileTexture Detail,
    D3D12MaterialBindingProfileTexture Mask, D3D12MaterialBindingProfileTexture Matcap,
    D3D12MatcapColorEncoding MatcapEncoding, int UvChannel = 0, float NormalStrength = 1f,
    float DetailStrength = 1f, float MetallicSaturation = 1f, float HighlightCoefficient = 0f,
    float HighlightStrength = 1f, float RotationRadians = 0f, float ShadowCoefficient = .5f,
    float ShadowSoftness = .1f, float ShadowColorStrength = 1f, float WarmBandStrength = .25f,
    Vector3? ShadowColor = null, Vector3? RimColor = null, float RimStrength = 0f,
    float RimSize = .75f, float RimDirectionRadians = 0f, string GeneratorRevision = "mikk-v1")
{
    public void Validate()
    {
        if (PipelineId is not ("star-rail" or "wuthering-waves"))
            throw new ArgumentException("Body inputs require an explicit Star or MC pipeline.", nameof(PipelineId));
        foreach (var texture in new[] { Normal, Detail, Mask, Matcap })
        {
            ArgumentNullException.ThrowIfNull(texture); texture.Validate();
            if (texture.Kind != SceneTextureKind.Image)
                throw new ArgumentException("Body inputs require explicit original Image resources.");
        }
        if (GeneratorRevision != "mikk-v1")
            throw new ArgumentException("Body inputs require the supported mikk-v1 tangent generator.", nameof(GeneratorRevision));
        ToBinding().Validate();
    }

    internal D3D12StarMcBodyBinding ToBinding() => new(Normal.SourceSceneTextureOrdinal,
        Detail.SourceSceneTextureOrdinal, Mask.SourceSceneTextureOrdinal, Matcap.SourceSceneTextureOrdinal,
        MatcapEncoding, UvChannel, NormalStrength, DetailStrength, MetallicSaturation, HighlightCoefficient,
        HighlightStrength, RotationRadians, ShadowCoefficient, ShadowSoftness, ShadowColorStrength,
        WarmBandStrength, ShadowColor, RimColor, RimStrength, RimSize, RimDirectionRadians);
}
