using System.Collections.ObjectModel;
using System.Numerics;

namespace Mmd.Modern.Core;

public enum OriginalSphereMode { None, Multiply, Add, SubTexture }
public enum OriginalTextureSampling { PointClamp, LinearClamp, PointWrap, LinearWrap }

/// <summary>Explicit selected inputs, not claims about MMD UI defaults or camera construction.
/// SphereRotation uses row vectors. SceneSnapshot.LightDirection is incoming L; propagation is -L.</summary>
public sealed record OriginalStyleProfile(Vector3 LightAmbient, Vector3 LightSpecular, Matrix4x4 SphereRotation,
    OriginalTextureSampling TextureSampling = OriginalTextureSampling.LinearWrap)
{
    public static OriginalStyleProfile SelectedReference { get; } = new(new(.602f),new(.602f),Matrix4x4.Identity);
}

public sealed record OriginalStyleMaterialBinding(Vector3 ToonShadowColor, int SphereTexture = -1,
    OriginalSphereMode SphereMode = OriginalSphereMode.None, bool DoubleSided = false)
{
    public OriginalMaterialTextureFactors TextureFactors { get; init; } = OriginalMaterialTextureFactors.Identity;
    /// <summary>Explicit shared-effect state for PMX mode 3. Hosts importing multiple models must resolve draw-order inheritance.</summary>
    public bool SubTextureAdditive { get; init; }
}

/// <summary>Immutable format-neutral character surface slice. In-bounds shadow visibility=1 only;
/// no edge, ground/self shadow, transparency sorting, attachment technique or morph evaluation.</summary>
public sealed class OriginalStyleScene
{
    public SceneSnapshot Snapshot { get; }
    public ReadOnlyCollection<OriginalStyleMaterialBinding> Materials { get; }
    public OriginalStyleProfile Profile { get; }

    public OriginalStyleScene(SceneSnapshot snapshot, IEnumerable<OriginalStyleMaterialBinding> materials, OriginalStyleProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(materials);
        Snapshot=snapshot; Materials=Array.AsReadOnly(materials.ToArray()); Profile=profile??OriginalStyleProfile.SelectedReference;
        if(Materials.Count!=snapshot.Materials.Count) throw new ArgumentException("One OriginalStyle binding per scene material is required.");
        foreach(var m in Materials)
            if(m is null || !Finite(m.ToonShadowColor) || !m.TextureFactors.IsFinite || !Enum.IsDefined(m.SphereMode)
                || m.SphereTexture < -1 || m.SphereTexture >= snapshot.Textures.Count
                || (m.SphereMode == OriginalSphereMode.None) != (m.SphereTexture == -1))
                throw new ArgumentException("Invalid sphere texture, sphere mode, toon colour or texture factors.");
        if (Materials.Any(m => m.SphereMode == OriginalSphereMode.SubTexture) && snapshot.AdditionalUvs is null)
            throw new ArgumentException("SubTexture requires an explicit vertex-order UV1 stream.");
        var r=Profile.SphereRotation;
        float[] values=[r.M11,r.M12,r.M13,r.M14,r.M21,r.M22,r.M23,r.M24,r.M31,r.M32,r.M33,r.M34,r.M41,r.M42,r.M43,r.M44];
        if(!Finite(Profile.LightAmbient)||!Finite(Profile.LightSpecular)||!Enum.IsDefined(Profile.TextureSampling)
            || values.Any(v=>!float.IsFinite(v)) || r.M14!=0 || r.M24!=0 || r.M34!=0 || r.M41!=0 || r.M42!=0 || r.M43!=0 || r.M44!=1
            || !Matrix4x4.Invert(r,out _)) throw new ArgumentException("Profile requires finite colours and an invertible explicit normal rotation matrix.");
    }
    private static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
}

public readonly record struct OriginalHostColors(Vector4 EgColor,Vector4 SpecularPower);

/// <summary>Independent scalar/vector expression of the observed SM3 character surface contract.
/// No original shader text or machine code is used. UNorm target conversion is separate.
/// Only default texture/sphere morph factors, in-bounds visibility=1 and ordinary alpha are supported.</summary>
public static class OriginalStyleMath
{
    /// <summary>Allocate clamped Eg between the fixed ambient and diffuse lighting inputs.
    /// Only the ambient share is eligible for optional GI near-field occlusion.</summary>
    public static Vector3 ResolveAmbientShare(SceneMaterial material, OriginalStyleProfile profile)
    {
        // Double avoids overflow when finite artist inputs multiply or sum beyond float range.
        static float Share(float ambient,float lighting,float diffuse)
        {
            double a=Math.Max((double)ambient,0),d=Math.Max((double)lighting*diffuse,0),sum=a+d;
            return sum==0?0:(float)(a/sum);
        }
        return new(Share(material.Ambient.X,profile.LightAmbient.X,material.Diffuse.X),
            Share(material.Ambient.Y,profile.LightAmbient.Y,material.Diffuse.Y),Share(material.Ambient.Z,profile.LightAmbient.Z,material.Diffuse.Z));
    }

    /// <summary>The opt-in scene response has no fixed PMX ambient allocation for near-field AO to remove.</summary>
    public static Vector3 ResolveAmbientShare(SceneMaterial material, OriginalStyleProfile profile, bool useSceneLighting)
        => useSceneLighting ? Vector3.Zero : ResolveAmbientShare(material, profile);

    public static OriginalHostColors ResolveHostColors(SceneMaterial material, OriginalStyleProfile profile)
    {
        Vector3 rgb=Vector3.Min(profile.LightAmbient*new Vector3(material.Diffuse.X,material.Diffuse.Y,material.Diffuse.Z)+material.Ambient,Vector3.One);
        return new(new(rgb,MathF.Min(material.Diffuse.W,1)),new(profile.LightSpecular*material.Specular,material.Shininess==0?.1f:material.Shininess));
    }

    /// <summary>Retains the original API and arithmetic for compatibility inputs. Explicit indoor scenes
    /// receive the actual main-light RGB, while their source PMX ambient remains authored data.</summary>
    public static OriginalHostColors ResolveHostColors(SceneMaterial material, OriginalStyleProfile profile, bool useSceneLighting)
    {
        if (!useSceneLighting) return ResolveHostColors(material, profile);
        Vector3 rgb = Vector3.Min(profile.LightAmbient * new Vector3(material.Diffuse.X, material.Diffuse.Y, material.Diffuse.Z), Vector3.One);
        return new(new(rgb, MathF.Min(material.Diffuse.W, 1)),
            new(profile.LightSpecular * material.Specular, material.Shininess == 0 ? .1f : material.Shininess));
    }

    public static Vector3 ReadToonShadowColor(SceneTexture image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int offset=(image.Height-1)*image.Width*4;
        var p=image.Rgba;
        return new(p[offset]/256f,p[offset+1]/256f,p[offset+2]/256f);
    }

    public static float Facing(Vector3 normal,Vector3 incomingLight)
        =>Math.Clamp(Vector3.Dot(Vector3.Normalize(normal),Vector3.Normalize(incomingLight))*3,0,1);

    /// <summary>Combined CPU helper. GPU rotates raw normal in VS, interpolates xy, then maps to UV in PS.</summary>
    public static Vector2 SphereUv(Vector3 rawVertexNormal,Matrix4x4 rotation)
    {
        var p=Vector3.TransformNormal(rawVertexNormal,rotation);
        return new(.5f*p.X+.5f,-.5f*p.Y+.5f);
    }

    public static Vector4 EvaluateSurface(Vector4 eg,Vector4 specularPower,Vector3 toon,Vector3 worldNormal,
        Vector3 worldPosition,Vector3 eye,Vector3 incomingLight,Vector4 diffuseSample,Vector4 sphereSample,
        OriginalSphereMode sphereMode,bool hasDiffuseTexture=true)
    {
        if(!Enum.IsDefined(sphereMode)) throw new ArgumentOutOfRangeException(nameof(sphereMode));
        if(sphereMode==OriginalSphereMode.SubTexture) throw new NotSupportedException("Use the explicit SubTexture shader contract; this ordinary sphere helper has no inherited additive state or toon alpha.");
        if(!hasDiffuseTexture) diffuseSample=Vector4.One;
        var n=Vector3.Normalize(worldNormal); var l=Vector3.Normalize(incomingLight);
        var view=Vector3.Normalize(eye-worldPosition); var half=Vector3.Normalize(l+view);
        float facing=Math.Clamp(Vector3.Dot(n,l)*3,0,1);
        float power=MathF.Pow(MathF.Max(0,Vector3.Dot(n,half)),specularPower.W);
        var highlight=Vector3.Clamp(power*new Vector3(specularPower.X,specularPower.Y,specularPower.Z),Vector3.Zero,Vector3.One);
        var baseColor=new Vector3(eg.X,eg.Y,eg.Z)*new Vector3(diffuseSample.X,diffuseSample.Y,diffuseSample.Z);
        var sp=new Vector3(sphereSample.X,sphereSample.Y,sphereSample.Z);
        if(sphereMode==OriginalSphereMode.Multiply) baseColor*=sp;
        if(sphereMode==OriginalSphereMode.Add) baseColor+=sp;
        // Preserve the two observed technique groupings; CPU/SM5 agreement is not SM3 bit equality.
        Vector3 result=hasDiffuseTexture ? baseColor*(new Vector3(facing)+toon*(1-facing))+highlight*facing
            : baseColor*facing+baseColor*toon*(1-facing)+highlight*facing;
        return new(result,eg.W*diffuseSample.W*(sphereMode==OriginalSphereMode.None?1:sphereSample.W));
    }
}
