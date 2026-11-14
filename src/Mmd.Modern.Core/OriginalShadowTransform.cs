using System.Numerics;
using System.Runtime.CompilerServices;

namespace Mmd.Modern.Core;

/// <summary>Explicit reconstruction of the original host's row-vector LH light transform.
/// World is already included in LightViewProjection; consumers must not multiply it again.
/// Mode 1 selects the normal comparison; every other integer selects PartialFar, not global disable.
/// The diagnostic coefficients describe the selected mathematical profile, not camera UI state.</summary>
public readonly record struct OriginalShadowTransform(
    Matrix4x4 LightViewProjection, bool PartialFar,
    float Facing, float ProjectionScale, float WarpA, float WarpB)
{
    public static OriginalShadowTransform Create(Matrix4x4 world, Vector3 cameraPosition,
        Vector3 outgoingLightDirection, float shadowScale, int mode)
    {
        if(!Finite(world))throw new ArgumentException("World must be finite.",nameof(world));
        if(!Finite(cameraPosition))throw new ArgumentException("Camera position must be finite.",nameof(cameraPosition));
        if(!Finite(outgoingLightDirection))throw new ArgumentException("Outgoing light direction must be finite.",nameof(outgoingLightDirection));
        if(!float.IsFinite(shadowScale)||shadowScale<=0)throw new ArgumentOutOfRangeException(nameof(shadowScale),"Shadow scale must be finite and positive.");

        // The host offsets by the raw D, not by normalize(D). Its independent
        // normalized copy is used only for the piecewise facing profile.
        var direction=Normalise(outgoingLightDirection,nameof(outgoingLightDirection));
        var eye=new Vector3(
            Subtract(cameraPosition.X,Multiply(outgoingLightDirection.X,50)),
            Subtract(cameraPosition.Y,Multiply(outgoingLightDirection.Y,50)),
            Subtract(cameraPosition.Z,Multiply(outgoingLightDirection.Z,50)));
        if(!Finite(eye))throw new ArgumentException("Light eye overflows the supported finite transform.",nameof(outgoingLightDirection));
        var forward=Normalise(new(
            Subtract(cameraPosition.X,eye.X),Subtract(cameraPosition.Y,eye.Y),Subtract(cameraPosition.Z,eye.Z)),nameof(cameraPosition));
        var right=Normalise(Cross(Vector3.UnitZ,forward),nameof(outgoingLightDirection));
        var up=Cross(forward,right);
        var view=new Matrix4x4(
            right.X,up.X,forward.X,0,
            right.Y,up.Y,forward.Y,0,
            right.Z,up.Z,forward.Z,0,
            -Dot(right,eye),-Dot(up,eye),-Dot(forward,eye),1);

        // Preserve source scalar instruction grouping, including f's multiply-zero
        // expression and the non-symmetric rounding of A versus -B at |f|=.9.
        float f=Subtract(Add(Multiply(direction.Y,0),Multiply(direction.X,0)),Multiply(direction.Z,1));
        float k,a,b;
        if(f<-.9f||f>.9f)
        {
            k=shadowScale;
            a=Subtract(1,MathF.Abs(f));
            b=-a;
        }
        else if(f<-.8f)
        {
            k=mode==1
                ?Add(Multiply(Add(Multiply(Subtract(f,.1f),10),10),shadowScale),shadowScale)
                :Multiply(Add(Multiply(f,20),19),shadowScale);
            a=mode==1?Add(Multiply(f,4),3.7f):Add(Multiply(f,9),8.2f);
            b=Subtract(Multiply(f,-9),8.2f);
        }
        else if(f>.8f)
        {
            k=mode==1
                ?Add(Multiply(Subtract(10,Multiply(Add(f,.1f),10)),shadowScale),shadowScale)
                :Multiply(Subtract(19,Multiply(f,20)),shadowScale);
            a=mode==1?Subtract(3.7f,Multiply(f,4)):Subtract(8.2f,Multiply(f,9));
            b=Subtract(Multiply(f,9),8.2f);
        }
        else
        {
            k=mode==1?Add(shadowScale,shadowScale):Multiply(shadowScale,3);
            a=mode==1?.5f:1;
            b=-1;
        }

        float depthScale=Multiply(shadowScale,.15f);
        if(!float.IsFinite(k)||!float.IsFinite(depthScale)||depthScale<=0)
            throw new ArgumentOutOfRangeException(nameof(shadowScale),"Shadow projection underflows or overflows.");
        var projection=new Matrix4x4(k,0,0,0, 0,k,0,0, 0,0,depthScale,0, 0,0,0,1);
        var warp=Matrix4x4.Identity;warp.M24=a;warp.M42=b;
        // Deliberately retain each original matrix multiplication in order. This
        // does not promise cross-runtime/D3DX bit identity of general matrices.
        var light=world*view;
        light*=projection;
        light*=Matrix4x4.CreateScale(1,1,2);
        light*=Matrix4x4.CreateTranslation(0,0,-1);
        light*=warp;
        light*=Matrix4x4.CreateTranslation(0,0,1);
        light*=Matrix4x4.CreateScale(1,1,.5f);
        if(!Finite(light))throw new ArgumentException("Inputs overflow the supported finite light transform.");
        return new(light,mode!=1,f,k,a,b);
    }

    private static Vector3 Normalise(Vector3 value,string parameterName)
    {
        float length=MathF.Sqrt(Dot(value,value));
        if(!float.IsFinite(length)||length<=0)
            throw new ArgumentException("Direction or look-at basis is degenerate in binary32.",parameterName);
        return new(value.X/length,value.Y/length,value.Z/length);
    }

    private static Vector3 Cross(Vector3 a,Vector3 b)=>new(
        Subtract(Multiply(a.Y,b.Z),Multiply(a.Z,b.Y)),
        Subtract(Multiply(a.Z,b.X),Multiply(a.X,b.Z)),
        Subtract(Multiply(a.X,b.Y),Multiply(a.Y,b.X)));
    private static float Dot(Vector3 a,Vector3 b)=>Add(Add(Multiply(a.X,b.X),Multiply(a.Y,b.Y)),Multiply(a.Z,b.Z));
    [MethodImpl(MethodImplOptions.NoInlining)] private static float Multiply(float a,float b)=>a*b;
    [MethodImpl(MethodImplOptions.NoInlining)] private static float Add(float a,float b)=>a+b;
    [MethodImpl(MethodImplOptions.NoInlining)] private static float Subtract(float a,float b)=>a-b;
    private static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
    private static bool Finite(Matrix4x4 m)=>
        float.IsFinite(m.M11)&&float.IsFinite(m.M12)&&float.IsFinite(m.M13)&&float.IsFinite(m.M14)&&
        float.IsFinite(m.M21)&&float.IsFinite(m.M22)&&float.IsFinite(m.M23)&&float.IsFinite(m.M24)&&
        float.IsFinite(m.M31)&&float.IsFinite(m.M32)&&float.IsFinite(m.M33)&&float.IsFinite(m.M34)&&
        float.IsFinite(m.M41)&&float.IsFinite(m.M42)&&float.IsFinite(m.M43)&&float.IsFinite(m.M44);
}
