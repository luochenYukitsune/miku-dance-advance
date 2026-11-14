using System.Numerics;

namespace Mmd.Modern.Core;

/// <summary>Independent CPU reference for the opt-in toon diffuse budget. Production composition
/// runs in the surface pixel shader. The input incident signal has not been multiplied by receiver
/// albedo. This is a fixed stylized transfer in the existing linear-UNorm convention, not exposure,
/// a physical energy-conservation claim, or an implementation of MToon environment lighting.</summary>
public static class RayGiToonPaletteMath
{
    public const float ReferenceRadiance=1;
    public const float MaximumPaletteBandFill=.25f;

    /// <summary>Returns only the nonnegative diffuse increment. Original specular/add-sphere and
    /// alpha remain outside this budget. The no-darkening contract assumes experimental AO is off.</summary>
    public static Vector3 EvaluateDelta(Vector3 diffuseLit,Vector3 toonShadowColor,float control,
        Vector3 incident,float strength,bool insideShadowProjection=true)
    {
        if(!Finite(diffuseLit)||Vector3.Min(diffuseLit,Vector3.Zero)!=Vector3.Zero)
            throw new ArgumentOutOfRangeException(nameof(diffuseLit));
        if(!Finite(toonShadowColor)||Vector3.Min(toonShadowColor,Vector3.Zero)!=Vector3.Zero
            ||Vector3.Max(toonShadowColor,Vector3.One)!=Vector3.One)
            throw new ArgumentOutOfRangeException(nameof(toonShadowColor));
        if(!float.IsFinite(control)||control<0||control>1)throw new ArgumentOutOfRangeException(nameof(control));
        if(!Finite(incident)||Vector3.Min(incident,Vector3.Zero)!=Vector3.Zero)
            throw new ArgumentOutOfRangeException(nameof(incident));
        if(!float.IsFinite(strength)||strength<0||strength>100)throw new ArgumentOutOfRangeException(nameof(strength));
        if(!insideShadowProjection||strength==0||control==1||incident==Vector3.Zero)return Vector3.Zero;
        Vector3 factor=new Vector3(control)+toonShadowColor*(1-control);
        Vector3 budget=diffuseLit*Vector3.Max(Vector3.One-factor,Vector3.Zero);
        Vector3 mapped=Vector3.Clamp(incident*(strength/ReferenceRadiance),Vector3.Zero,Vector3.One);
        return budget*mapped;
    }

    /// <summary>PreservePaletteBands maps incident luminance to five neutral fill levels
    /// (0, 1/16, 1/8, 3/16, 1/4 of the original diffuse shadow-to-lit budget).
    /// It quantizes lighting only and never quantizes the authored material or texture RGB.</summary>
    public static Vector3 EvaluateDeltaWithResponse(Vector3 diffuseLit,Vector3 toonShadowColor,float control,
        Vector3 incident,float strength,bool insideShadowProjection=true,RayGiToonResponse response=RayGiToonResponse.Continuous)
    {
        if(!Enum.IsDefined(response))throw new ArgumentOutOfRangeException(nameof(response));
        // Reuse the unchanged public reference for validation and the existing continuous response.
        Vector3 continuous=EvaluateDelta(diffuseLit,toonShadowColor,control,incident,strength,insideShadowProjection);
        if(response==RayGiToonResponse.Continuous)return continuous;
        if(!insideShadowProjection||strength==0||control==1||incident==Vector3.Zero)return Vector3.Zero;
        float luminance=Vector3.Dot(incident,new Vector3(.2126f,.7152f,.0722f));
        float mapped=Math.Clamp(luminance*(strength/ReferenceRadiance),0,1);
        float fill=MathF.Floor(mapped*4+.5f)*(.25f*MaximumPaletteBandFill);
        Vector3 factor=new Vector3(control)+toonShadowColor*(1-control);
        return diffuseLit*Vector3.Max(Vector3.One-factor,Vector3.Zero)*fill;
    }

    private static bool Finite(Vector3 value)=>float.IsFinite(value.X)&&float.IsFinite(value.Y)&&float.IsFinite(value.Z);
}
