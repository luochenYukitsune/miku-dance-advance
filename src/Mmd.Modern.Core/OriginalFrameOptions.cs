using System.Numerics;
using System.Runtime.CompilerServices;
namespace Mmd.Modern.Core;

public enum OriginalAlphaBlendMode { Normal, Additive }
public enum SurfaceDrawOrder { Original, OpaqueFirst }
/// <summary>Legacy MAX alpha preserves original frame bytes. Straight accumulates source-over coverage,
/// filters premultiplied RGBA and resolves to unassociated RGB only at final output.</summary>
public enum FrameAlphaOutput { LegacyMax, Straight }
public enum OriginalDepthComparison { Less, LessEqual }
/// <summary>Single-frame post antialiasing. No temporal history or reconstruction.</summary>
public enum FrameAntialiasing { Off, Smaa1X }
/// <summary>Upstream single-frame SMAA preset, independent of spatial supersampling.</summary>
public enum SmaaQuality { Low, Medium, High, Ultra }
/// <summary>Resolved neutral eligibility flags. Already deformed geometry and default texture/sphere morph factors only.</summary>
public sealed record OriginalFrameMaterialFlags(bool CastSelfShadow,bool ReceiveSelfShadow,bool CastGroundShadow,
    bool SurfaceEnabled=true,bool ShadowDoubleSided=false,bool CullTransparentSurface=false)
{
    /// <summary>Stable scene-instance identity for modern additional-light receiver selection.
    /// Null is unbound standalone geometry; this does not alter material or shadow eligibility.</summary>
    public Guid? LightReceiverObjectId { get; init; }
    /// <summary>Derived from an explicit indoor Environment purpose. Uses actual scene lights without the
    /// fixed PMX ambient or toon shadow floor; source materials and character responses remain unchanged.</summary>
    public bool UseSceneLighting { get; init; }
    /// <summary>Explicit character policy for additional direct diffuse lights. The original primary light,
    /// authored highlights, additive sphere and alpha retain their existing response; dormant without native lights.
    /// Protection applies to finite nonnegative diffuse endpoints/increments and toon RGB in [0,1]; other domains retain legacy arithmetic.</summary>
    public bool PreserveDirectLightPalette { get; init; }
    /// <summary>Null inherits the frame GI response; explicit model purpose can select each material independently.</summary>
    public RayGiToonResponse? GiToonResponseOverride { get; init; }
    /// <summary>Explicit modern-character main-light palette response. Null preserves the original shader response.</summary>
    public CharacterToonResponse? CharacterToonResponse { get; init; }
    /// <summary>Explicit modern character edge styling; null preserves source material width, color and drawing eligibility.</summary>
    public CharacterOutlineStyle? CharacterOutlineStyle { get; init; }
    /// <summary>Per-draw outline width multiplier. One preserves the selected outline inputs;
    /// zero disables this draw without changing shared vertex width selection or material eligibility.</summary>
    public float OutlineWidthScale { get; init; } = 1f;
}
/// <summary>Explicit selected deformed model-position to light-clip transform (already includes frame World), size and filter.
/// Renderer does not multiply World again. This is not a reconstruction of the original camera/warp builder.</summary>
public sealed record OriginalSelfShadowOptions(Matrix4x4 LightViewProjection,int Width,int Height,bool Generate,bool PartialFar,
    OriginalTextureSampling Sampling,OriginalDepthComparison DepthComparison);
/// <summary>Selected resolved colour. Original ground fixed-function material/lighting resolution is not yet implemented.</summary>
public sealed record OriginalGroundShadowOptions(Vector3 ProjectionDirection,Vector4 Color,bool AlphaBlend,bool UseStencil);
/// <summary>Selected inherited state for the SM3 frame path. Null self shadow binds a constant white depth resource;
/// this is not the original global-disable fixed-function path. No GI, RT or bloom is enabled here.</summary>
public sealed record OriginalFrameOptions(OriginalAlphaBlendMode BlendMode,bool WriteDepth,bool Transp,
    OriginalSelfShadowOptions? SelfShadow=null,OriginalGroundShadowOptions? GroundShadow=null,
    SceneCamera? CameraOverride=null,Matrix4x4? SphereRotationOverride=null,bool EnableFrustumCulling=true,RayGiOptions? RayGi=null,
    FrameAntialiasing Antialiasing=FrameAntialiasing.Off,Vector4? BackgroundColor=null,
    SmaaQuality SmaaQuality=SmaaQuality.High,int SpatialSamples=1,FrameAlphaOutput AlphaOutput=FrameAlphaOutput.LegacyMax,
    SurfaceDrawOrder SurfaceOrder=SurfaceDrawOrder.Original);

public readonly record struct OriginalShadowProjection(bool Inside,Vector2 Uv,float Depth);
public static class OriginalFrameMath
{
    public static OriginalShadowProjection Project(Vector4 clip)
    {
        var q=new Vector3(clip.X,clip.Y,clip.Z)/clip.W;
        var uv=new Vector2(.5f+.5f*q.X,.5f-.5f*q.Y);
        return new(uv.X>=0&&uv.X<=1&&uv.Y>=0&&uv.Y<=1,uv,q.Z);
    }
    public static float Visibility(float projectedDepth,float sampledDepth,float uvY,bool partialFar)
    {
        float delta=MathF.Max(Subtract(projectedDepth,sampledDepth),0);
        float weighted=Multiply(delta,partialFar?8000:1500);
        if(partialFar)weighted=Multiply(weighted,uvY);
        return Subtract(1,Math.Clamp(Subtract(weighted,.3f),0,1));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static float Multiply(float a,float b)=>a*b;
    [MethodImpl(MethodImplOptions.NoInlining)] private static float Subtract(float a,float b)=>a-b;
    public static Vector3 ProjectGround(Vector3 edgePosition,Vector3 direction,Matrix4x4? frameWorld=null)
    {
        var projected=new Vector3(edgePosition.X-edgePosition.Y*direction.X/direction.Y,.1f,edgePosition.Z-edgePosition.Y*direction.Z/direction.Y);
        return frameWorld is {} world?Vector3.Transform(projected,world):projected;
    }
    public static Vector4 Blend(Vector4 source,Vector4 destination,OriginalAlphaBlendMode mode)
    {
        if(!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var rgb=new Vector3(source.X,source.Y,source.Z)*source.W+new Vector3(destination.X,destination.Y,destination.Z)*(mode==OriginalAlphaBlendMode.Additive?1:1-source.W);
        return new(rgb,MathF.Max(source.W,destination.W));
    }
    public static Vector4 EvaluateSurface(Vector4 eg,Vector4 specularPower,Vector3 toon,Vector3 normal,Vector3 worldPosition,
        Vector3 eye,Vector3 light,Vector4 diffuse,Vector4 sphere,OriginalSphereMode sphereMode,bool hasDiffuse,
        OriginalShadowProjection projection,float sampledDepth,bool partialFar,bool transp)
    {
        if(!Enum.IsDefined(sphereMode)) throw new ArgumentOutOfRangeException(nameof(sphereMode));
        if(sphereMode==OriginalSphereMode.SubTexture) throw new NotSupportedException("The ordinary frame helper has no SubTexture additive state or toon alpha; use its explicit shader contract.");
        if(!hasDiffuse) diffuse=Vector4.One;
        var n=Vector3.Normalize(normal); var l=Vector3.Normalize(light);
        var half=Vector3.Normalize(l+Vector3.Normalize(eye-worldPosition));
        var highlight=Vector3.Clamp(MathF.Pow(MathF.Max(0,Vector3.Dot(n,half)),specularPower.W)*new Vector3(specularPower.X,specularPower.Y,specularPower.Z),Vector3.Zero,Vector3.One);
        var color=new Vector3(eg.X,eg.Y,eg.Z)*new Vector3(diffuse.X,diffuse.Y,diffuse.Z);
        var sp=new Vector3(sphere.X,sphere.Y,sphere.Z);
        if(sphereMode==OriginalSphereMode.Multiply) color*=sp;
        if(sphereMode==OriginalSphereMode.Add) color+=sp;
        float alpha=eg.W*diffuse.W*(sphereMode==OriginalSphereMode.None?1:sphere.W);
        if(!projection.Inside) return new(color+highlight,alpha);
        float c=MathF.Min(Math.Clamp(Vector3.Dot(n,l)*3,0,1),Visibility(projection.Depth,sampledDepth,projection.Uv.Y,partialFar));
        var rgb=hasDiffuse?color*(new Vector3(c)+toon*(1-c))+highlight*c:color*c+color*toon*(1-c)+highlight*c;
        return new(rgb,transp?.5f:alpha);
    }
    public static void Validate(OriginalFrameOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if(!Enum.IsDefined(options.BlendMode)) throw new ArgumentException("Unknown frame blend mode.");
        if(!Enum.IsDefined(options.SurfaceOrder)) throw new ArgumentException("Unknown surface draw order.");
        if(!Enum.IsDefined(options.AlphaOutput)) throw new ArgumentException("Unknown frame alpha output mode.");
        if(options.AlphaOutput==FrameAlphaOutput.Straight&&options.BlendMode!=OriginalAlphaBlendMode.Normal)
            throw new NotSupportedException("Straight-alpha output requires source-over blending; legacy additive blending is not representable by this output contract.");
        if(!Enum.IsDefined(options.Antialiasing)) throw new ArgumentException("Unknown frame antialiasing mode.");
        if(!Enum.IsDefined(options.SmaaQuality)) throw new ArgumentException("Unknown SMAA quality preset.");
        if(options.SpatialSamples is not (1 or 2 or 4 or 8 or 16 or 32)) throw new ArgumentException("Spatial samples must be 1, 2, 4, 8, 16 or 32.");
        if(options.BackgroundColor is {} background&&!OriginalOutlineMath.Finite(background))throw new ArgumentException("Background color must be finite.");
        if(options.AlphaOutput==FrameAlphaOutput.Straight&&options.BackgroundColor is {} clear
            &&(clear.X<0||clear.X>1||clear.Y<0||clear.Y>1||clear.Z<0||clear.Z>1||clear.W<0||clear.W>1))
            throw new ArgumentException("Straight-alpha background components must be between zero and one.");
        if(options.SelfShadow is {} s)
        {
            var m=s.LightViewProjection;
            if(s.Width is <1 or >4096||s.Height is <1 or >4096||!Enum.IsDefined(s.DepthComparison)||s.Sampling is not (OriginalTextureSampling.PointClamp or OriginalTextureSampling.LinearClamp)
                ||!OriginalOutlineMath.Finite(new(m.M11,m.M12,m.M13,m.M14))||!OriginalOutlineMath.Finite(new(m.M21,m.M22,m.M23,m.M24))
                ||!OriginalOutlineMath.Finite(new(m.M31,m.M32,m.M33,m.M34))||!OriginalOutlineMath.Finite(new(m.M41,m.M42,m.M43,m.M44))||!Matrix4x4.Invert(m,out _))
                throw new ArgumentException("Shadow requires a finite invertible explicit light projection, size 1..4096 and clamp sampling.");
        }
        if(options.GroundShadow is {} g && (!OriginalOutlineMath.Finite(new(g.ProjectionDirection,0))||g.ProjectionDirection.Y==0||!OriginalOutlineMath.Finite(g.Color)))
            throw new ArgumentException("Ground shadow requires finite resolved colour and direction with nonzero Y.");
        options.CameraOverride?.Validate();
        if(options.SphereRotationOverride is {} r) _=new OriginalStyleSceneRotationValidator(r);
    }
    private readonly struct OriginalStyleSceneRotationValidator
    {
        public OriginalStyleSceneRotationValidator(Matrix4x4 r)
        {
            if(!OriginalOutlineMath.Finite(new(r.M11,r.M12,r.M13,r.M14))||!OriginalOutlineMath.Finite(new(r.M21,r.M22,r.M23,r.M24))
                ||!OriginalOutlineMath.Finite(new(r.M31,r.M32,r.M33,r.M34))||!OriginalOutlineMath.Finite(new(r.M41,r.M42,r.M43,r.M44))
                ||r.M14!=0||r.M24!=0||r.M34!=0||r.M41!=0||r.M42!=0||r.M43!=0||r.M44!=1||!Matrix4x4.Invert(r,out _))
                throw new ArgumentException("Invalid selected sphere rotation override.");
        }
    }
}
