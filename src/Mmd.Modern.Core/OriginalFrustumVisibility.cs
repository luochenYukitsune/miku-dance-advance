using System.Numerics;

namespace Mmd.Modern.Core;

/// <summary>Conservative model-space bounds of the vertices referenced by one material.</summary>
public readonly record struct OriginalVisibilityBounds(Vector3 Minimum,Vector3 Maximum);

/// <summary>Homogeneous row-vector D3D clipping: x/y in [-w,w], z in [0,w].
/// Only a box strictly outside one common plane is rejected. Unknown inputs remain drawable.</summary>
public static class OriginalFrustumVisibility
{
    // Greater than the binary32 dot-product gamma(4), allowing either separate operations or FMA.
    // Errors scale with absolute products, so cancellation cannot erase the safety margin.
    private const double Roundoff=1.9073486328125e-6;
    private const double ClipMargin=1e-5;
    private static OriginalVisibilityBounds Unknown=>new(new(float.NaN),new(float.NaN));

    /// <summary>One shader multiply, used by the actual outline's precombined WorldViewProjection.</summary>
    public static bool IsPotentiallyVisible(OriginalVisibilityBounds bounds,Matrix4x4 transform)
        =>Visible(bounds,transform,null);

    /// <summary>Two shader multiplies, matching the surface's p*World followed by p*ViewProjection.</summary>
    public static bool IsPotentiallyVisible(OriginalVisibilityBounds bounds,Matrix4x4 world,Matrix4x4 viewProjection)
        =>Visible(bounds,world,viewProjection);

    public static OriginalVisibilityBounds[] SurfaceMaterialBounds(SceneSnapshot scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var result=new OriginalVisibilityBounds[scene.Materials.Count];
        for(int i=0;i<result.Length;i++)
        {
            var range=scene.Materials[i];var min=new Vector3(float.PositiveInfinity);var max=new Vector3(float.NegativeInfinity);
            for(int j=range.StartIndex;j<range.StartIndex+range.IndexCount;j++)
            {
                var p=scene.GetVertex((int)scene.Indices[j]).Position;min=Vector3.Min(min,p);max=Vector3.Max(max,p);
            }
            result[i]=new(min,max);
        }
        return result;
    }

    /// <summary>Bounds the actual expanded/inward stream, using each draw's original index range.</summary>
    public static OriginalVisibilityBounds[] PositionMaterialBounds(SceneSnapshot scene,ReadOnlySpan<Vector4> positions)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if(positions.Length!=scene.VertexCount) throw new ArgumentException("One actual position per scene vertex is required.",nameof(positions));
        var result=new OriginalVisibilityBounds[scene.Materials.Count];
        for(int i=0;i<result.Length;i++)
        {
            var range=scene.Materials[i];var min=new Vector3(float.PositiveInfinity);var max=new Vector3(float.NegativeInfinity);bool valid=true;
            for(int j=range.StartIndex;j<range.StartIndex+range.IndexCount;j++)
            {
                var p=positions[(int)scene.Indices[j]];
                if(!Finite(p.X)||!Finite(p.Y)||!Finite(p.Z)||p.W!=1) { valid=false;break; }
                var xyz=new Vector3(p.X,p.Y,p.Z);min=Vector3.Min(min,xyz);max=Vector3.Max(max,xyz);
            }
            result[i]=valid?new(min,max):Unknown;
        }
        return result;
    }

    private static bool Visible(OriginalVisibilityBounds bounds,Matrix4x4 first,Matrix4x4? second)
    {
        var min=bounds.Minimum;var max=bounds.Maximum;
        if(!Finite(min.X)||!Finite(min.Y)||!Finite(min.Z)||!Finite(max.X)||!Finite(max.Y)||!Finite(max.Z)
            ||min.X>max.X||min.Y>max.Y||min.Z>max.Z)return true;
        int outsideAllCorners=63;
        for(int i=0;i<8;i++)
        {
            var p=Transform(new((i&1)==0?min.X:max.X,(i&2)==0?min.Y:max.Y,(i&4)==0?min.Z:max.Z,1,0,0,0,0),first);
            if(second is {} next)p=Transform(p,next);
            if(!double.IsFinite(p.X+p.Y+p.Z+p.W+p.Ex+p.Ey+p.Ez+p.Ew))return true;
            int mask=0;
            if(p.X+p.W+p.Ex+p.Ew < -ClipMargin)mask|=1;
            if(p.W-p.X+p.Ex+p.Ew < -ClipMargin)mask|=2;
            if(p.Y+p.W+p.Ey+p.Ew < -ClipMargin)mask|=4;
            if(p.W-p.Y+p.Ey+p.Ew < -ClipMargin)mask|=8;
            if(p.Z+p.Ez < -ClipMargin)mask|=16;
            if(p.W-p.Z+p.Ew+p.Ez < -ClipMargin)mask|=32;
            outsideAllCorners&=mask;
            if(outsideAllCorners==0)return true;
        }
        return false;
    }

    private readonly record struct Point(double X,double Y,double Z,double W,double Ex,double Ey,double Ez,double Ew);
    private static Point Transform(Point p,Matrix4x4 m)
    {
        (double Value,double Error) Dot(float a,float b,float c,float d)
        {
            double ax=p.X*a,by=p.Y*b,cz=p.Z*c,dw=p.W*d;
            double magnitude=Math.Abs(ax)+Math.Abs(by)+Math.Abs(cz)+Math.Abs(dw);
            double propagated=p.Ex*Math.Abs(a)+p.Ey*Math.Abs(b)+p.Ez*Math.Abs(c)+p.Ew*Math.Abs(d);
            // A shader overflow/underflow or nonfinite calculation is never proof of invisibility.
            double error=magnitude+propagated>float.MaxValue*.5?double.PositiveInfinity
                :propagated+Roundoff*(magnitude+propagated)+1e-37;
            return (ax+by+cz+dw,error);
        }
        var x=Dot(m.M11,m.M21,m.M31,m.M41);var y=Dot(m.M12,m.M22,m.M32,m.M42);
        var z=Dot(m.M13,m.M23,m.M33,m.M43);var w=Dot(m.M14,m.M24,m.M34,m.M44);
        return new(x.Value,y.Value,z.Value,w.Value,x.Error,y.Error,z.Error,w.Error);
    }
    private static bool Finite(float value)=>float.IsFinite(value);
}
