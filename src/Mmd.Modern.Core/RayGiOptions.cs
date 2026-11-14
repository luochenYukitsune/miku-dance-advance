using System.Numerics;

namespace Mmd.Modern.Core;

/// <summary>How the shared one-bounce signal is composed with the original toon surface.
/// ToonPalette is an explicit art-directed diffuse budget, not a physically conserved lighting model.</summary>
public enum RayGiComposition { Additive, ToonPalette }

/// <summary>Explicit surface response for ToonPalette incident radiance. Continuous retains the original mapping.</summary>
public enum RayGiToonResponse { Continuous, PreservePaletteBands }

/// <summary>Optional filtering of incident GI only; original toon surfaces are never filtered.</summary>
public enum RayGiDenoiser { Off, Spatial, TemporalSpatial }

/// <summary>Explicit static one-bounce diffuse transport settings. Legacy is the unchanged default.
/// Secondary surfaces use binary alpha cutoff; translucent receiver samples do not receive GI.</summary>
public sealed record RayGiOptions(
    LightingMode Mode=LightingMode.Legacy,
    float Strength=4,
    int SamplesPerPixel=16,
    uint Seed=1,
    float MaxRayDistance=1000,
    float RayEpsilon=.002f,
    Vector3? DirectionalLightDirection=null,
    Vector3? DirectionalIrradiance=null,
    float DirectScale=1,
    float OcclusionStrength=0,
    float OcclusionRadius=1,
    RayGiComposition Composition=RayGiComposition.Additive,
    RayGiDenoiser Denoiser=RayGiDenoiser.Off)
{
    /// <summary>Optional neutral, quantized fill of at most one quarter of the original shadow-to-lit palette budget.</summary>
    public RayGiToonResponse ToonResponse {get;init;}=RayGiToonResponse.Continuous;
    /// <summary>Advance for seek, export-owner changes and other discontinuities. Capture alone does not reset GI.</summary>
    public long TemporalHistoryGeneration {get;init;}
    /// <summary>Application-provided camera-cut identity. Ordinary camera motion reprojects history.</summary>
    public long TemporalCameraCutKey {get;init;}
    /// <summary>Explicitly reject history for this submitted frame.</summary>
    public bool ResetTemporalHistory {get;init;}
    public static RayGiOptions Off {get;}=new();
    public Vector3 LightDirection=>DirectionalLightDirection??Vector3.Normalize(new Vector3(-.5f,1,-.5f));
    public Vector3 LightIrradiance=>DirectionalIrradiance??new Vector3(3);
    public bool Enabled=>Mode!=LightingMode.Legacy;
    /// <summary>Occlusion shares GI closest rays, so its finite radius cannot exceed the transport distance.</summary>
    public float EffectiveOcclusionRadius=>MathF.Min(OcclusionRadius,MaxRayDistance);
    public void Validate()
    {
        if(!Enum.IsDefined(Mode))throw new ArgumentOutOfRangeException(nameof(Mode));
        if(!Enum.IsDefined(Composition))throw new ArgumentOutOfRangeException(nameof(Composition));
        if(!Enum.IsDefined(Denoiser))throw new ArgumentOutOfRangeException(nameof(Denoiser));
        if(!Enum.IsDefined(ToonResponse))throw new ArgumentOutOfRangeException(nameof(ToonResponse));
        if(ToonResponse!=RayGiToonResponse.Continuous&&Composition!=RayGiComposition.ToonPalette)
            throw new ArgumentException("The selected toon response requires ToonPalette composition.",nameof(ToonResponse));
        if(Denoiser!=RayGiDenoiser.Off&&Composition!=RayGiComposition.ToonPalette)
            throw new ArgumentException("GI denoising requires ToonPalette incident radiance.",nameof(Denoiser));
        if(!float.IsFinite(Strength)||Strength<0||Strength>100)throw new ArgumentOutOfRangeException(nameof(Strength));
        if(SamplesPerPixel is <1 or >1024)throw new ArgumentOutOfRangeException(nameof(SamplesPerPixel));
        if(!float.IsFinite(MaxRayDistance)||MaxRayDistance<=0)throw new ArgumentOutOfRangeException(nameof(MaxRayDistance));
        if(!float.IsFinite(RayEpsilon)||RayEpsilon<=0||RayEpsilon>=MaxRayDistance)throw new ArgumentOutOfRangeException(nameof(RayEpsilon));
        if(!Finite(LightDirection)||LightDirection.LengthSquared()<1e-12f)throw new ArgumentOutOfRangeException(nameof(DirectionalLightDirection));
        if(!Finite(LightIrradiance)||Vector3.Min(LightIrradiance,Vector3.Zero)!=Vector3.Zero)throw new ArgumentOutOfRangeException(nameof(DirectionalIrradiance));
        if(!float.IsFinite(DirectScale)||DirectScale<0||DirectScale>4)throw new ArgumentOutOfRangeException(nameof(DirectScale));
        if(!float.IsFinite(OcclusionStrength)||OcclusionStrength<0||OcclusionStrength>1)throw new ArgumentOutOfRangeException(nameof(OcclusionStrength));
        if(!float.IsFinite(OcclusionRadius)||OcclusionRadius<=0||(OcclusionStrength>0&&OcclusionRadius<=RayEpsilon))throw new ArgumentOutOfRangeException(nameof(OcclusionRadius));
    }
    private static bool Finite(Vector3 value)=>float.IsFinite(value.X)&&float.IsFinite(value.Y)&&float.IsFinite(value.Z);
}
