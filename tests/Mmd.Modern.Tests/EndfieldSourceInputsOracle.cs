using System.Numerics;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.Tests;

// Independent scalar/vector numerical specification, with explicit zero-normal/power policy.
internal static class EndfieldSourceInputsOracle
{
    internal static Vector3 Normalize(Vector3 v)
    {
        float scale=MathF.Max(MathF.Abs(v.X),MathF.Max(MathF.Abs(v.Y),MathF.Abs(v.Z)));
        return scale==0?Vector3.Zero:Vector3.Normalize(v/scale);
    }

    internal static Vector3 Normal(Vector3 geometric,Vector3 perturbed,float strength)
        =>Normalize(geometric+(perturbed-geometric)*strength);

    internal static float Facing(Vector3 normal,Vector3 incoming,float blend)
    {
        float d=Math.Clamp(MathF.Abs(Vector3.Dot(Normalize(normal),Normalize(incoming))),0,1);
        if(blend==.5f) return 1-d;
        blend=Math.Clamp(blend,0,.99999f);
        float power=blend<.5f?blend*2:.5f/(1-blend);
        return 1-(power==0?1:MathF.Pow(d,power));
    }

    internal static float DirectionalQ(Vector3 normal,D3D12EndfieldDirectionalShadowBinding b)
    {
        float input=Vector3.Dot(normal,b.WorldDirection)*.5f+.5f;
        float subtract=b.Width-.5f;
        float maximum=1-subtract;
        float minimum=maximum*b.Width;
        float q=maximum==minimum?0:((input-minimum)/(maximum-minimum))*1.5f;
        return Math.Clamp(q,0,1.5f);
    }

    internal static Vector3 Base(Vector3 m,Vector3 geometric,Vector3 perturbed,Vector3 incoming,float blend)
    {
        float f=Facing(Normal(geometric,perturbed,1),incoming,blend);
        return m*(1-f)+m*m*f;
    }

    internal static Vector3 Evaluate(D3D12EndfieldBaseImageBinding binding,bool front,
        Vector3 geometric,Vector3 perturbed,Vector3 incoming,Vector3? sampledBase=null,float? sampledDetail=null,float? sampledQ=null)
    {
        Vector3 x=sampledBase??binding.BaseImageLinearRgb;
        if(binding.FacingBase is {} facing) x=Base(x,geometric,perturbed,incoming,facing.Blend);
        float? q=binding.DirectionalShadow is {} direction
            ?DirectionalQ(Normal(geometric,perturbed,direction.NormalStrength),direction):sampledQ;
        return EndfieldBaseImageOracle.Evaluate(binding,front,x,sampledDetail,q);
    }
}
