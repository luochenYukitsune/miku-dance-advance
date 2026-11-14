using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class OriginalShadowTransformTests
{
    [Theory]
    [InlineData(1, .25f, .5f, false)]
    [InlineData(2, .375f, 1f, true)]
    public void NeutralFixtureMatchesClosedFormMatrix(int mode, float k, float a, bool partialFar)
    {
        var result=OriginalShadowTransform.Create(Matrix4x4.Identity,Vector3.Zero,new(0,-1,0),.125f,mode);
        // Independent binary32 golden: (.125f*.15f)*50 survives Z's 2/-1/+1/.5
        // as 0x3f700001, not the real-arithmetic value .9375.
        var expected=new Matrix4x4(k,0,0,0, 0,0,-.01875f,0, 0,k,k*a*.5f,k*a, 0,-1,Float(0x3f700001u),1);
        Assert.Equal(expected,result.LightViewProjection);
        Assert.Equal(partialFar,result.PartialFar);
        Assert.Equal(0,result.Facing);
        Assert.Equal(k,result.ProjectionScale);
        Assert.Equal(a,result.WarpA);
        Assert.Equal(-1,result.WarpB);
    }

    [Fact]
    public void UnnormalisedOutgoingDirectionRetainsOriginalEyeDistance()
    {
        var unit=OriginalShadowTransform.Create(Matrix4x4.Identity,Vector3.Zero,new(0,-1,0),.125f,1);
        var longer=OriginalShadowTransform.Create(Matrix4x4.Identity,Vector3.Zero,new(0,-4,0),.125f,1);
        Assert.Equal(unit.Facing,longer.Facing);
        Assert.Equal(unit.ProjectionScale,longer.ProjectionScale);
        Assert.Equal(0x3f700001u,BitConverter.SingleToUInt32Bits(unit.LightViewProjection.M43));
        Assert.Equal(0x40700001u,BitConverter.SingleToUInt32Bits(longer.LightViewProjection.M43));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(2)] [InlineData(99)]
    public void OtherModesRetainPartialFarProfileEvenWhenModeIsZero(int mode)
    {
        var selected=OriginalShadowTransform.Create(Matrix4x4.Identity,new(1,2,3),new(0,-1,0),.02f,mode);
        var reference=OriginalShadowTransform.Create(Matrix4x4.Identity,new(1,2,3),new(0,-1,0),.02f,2);
        Assert.True(selected.PartialFar);
        Assert.Equal(reference,selected);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void PointProjectionMatchesIndependentGeometricOracleAndAppliesWorldOnce(int mode)
    {
        var world=Matrix4x4.CreateScale(1.1f,.7f,1.3f)*Matrix4x4.CreateRotationY(.37f)*Matrix4x4.CreateTranslation(4,-3,2);
        var camera=new Vector3(3,7,-2);
        var direction=new Vector3(.3f,-2,.7f);
        const float scale=.03125f;
        var transform=OriginalShadowTransform.Create(world,camera,direction,scale,mode);
        var identity=OriginalShadowTransform.Create(Matrix4x4.Identity,camera,direction,scale,mode);
        foreach(var point in new[] {new Vector3(0),new Vector3(2,-5,8),new Vector3(-3,7,-2),new Vector3(23,14,6)})
        {
            var actual=Vector4.Transform(new Vector4(point,1),transform.LightViewProjection);
            var expected=GeometricClipOracle(point,world,camera,direction,scale,mode);
            AssertVectorNear(expected,actual,2e-5);
            var worldPoint=Vector4.Transform(new Vector4(point,1),world);
            AssertVectorNear(Vector4.Transform(worldPoint,identity.LightViewProjection),actual,2e-5);
            Assert.True(Vector4.Distance(actual,Vector4.Transform(worldPoint,transform.LightViewProjection))>.01f);
        }
    }

    [Theory]
    [InlineData(1, -.95)] [InlineData(2, -.95)]
    [InlineData(1, -.85)] [InlineData(2, -.85)]
    [InlineData(1, 0)] [InlineData(2, 0)]
    [InlineData(1, .85)] [InlineData(2, .85)]
    [InlineData(1, .95)] [InlineData(2, .95)]
    public void AllProfileRegionsMatchIndependentDoublePrecisionPointOracle(int mode, double facing)
    {
        var direction=new Vector3(.1f,(float)-Math.Sqrt(1-facing*facing-.01),(float)-facing);
        var camera=new Vector3(-2,4,7);
        var world=Matrix4x4.CreateTranslation(3,1,-2);
        var actual=OriginalShadowTransform.Create(world,camera,direction,.02f,mode);
        foreach(var point in new[] {new Vector3(0),new Vector3(-7,2,9),new Vector3(8,-3,-5)})
            AssertVectorNear(GeometricClipOracle(point,world,camera,direction,.02,mode),Vector4.Transform(new Vector4(point,1),actual.LightViewProjection),3e-5);
    }

    [Theory]
    [MemberData(nameof(BoundaryFixtures))]
    public void ExactFloatBoundaryProfilesMatchIndependentFixedBitOracle(int mode,uint f,uint y,uint z,uint k,uint a,uint b)
    {
        var result=OriginalShadowTransform.Create(Matrix4x4.Identity,Vector3.Zero,new(0,Float(y),Float(z)),Float(0x3c3851ecu),mode);
        Assert.Equal(f,BitConverter.SingleToUInt32Bits(result.Facing));
        Assert.Equal(k,BitConverter.SingleToUInt32Bits(result.ProjectionScale));
        Assert.Equal(a,BitConverter.SingleToUInt32Bits(result.WarpA));
        Assert.Equal(b,BitConverter.SingleToUInt32Bits(result.WarpB));
    }

    public static IEnumerable<object[]> BoundaryFixtures=>new[]
    {
        // Generated once by independent Python struct-rounded scalar oracle; no product calls.
        new object[] { 1, 0xbf666665u, 0xbedf2cfcu, 0x3f666665u, 0x3c3851f8u, 0x3dcccd00u, 0xbdcccd00u },
        new object[] { 2, 0xbf666665u, 0xbedf2cfcu, 0x3f666665u, 0x3c385203u, 0x3dcccd00u, 0xbdcccd00u },
        new object[] { 1, 0xbf666666u, 0xbedf2cf7u, 0x3f666666u, 0x3c3851ecu, 0x3dcccce0u, 0xbdcccd00u },
        new object[] { 2, 0xbf666666u, 0xbedf2cf7u, 0x3f666666u, 0x3c3851ecu, 0x3dcccd00u, 0xbdcccd00u },
        new object[] { 1, 0xbf666667u, 0xbedf2cf3u, 0x3f666667u, 0x3c3851ecu, 0x3dccccc8u, 0xbdccccc8u },
        new object[] { 2, 0xbf666667u, 0xbedf2cf3u, 0x3f666667u, 0x3c3851ecu, 0x3dccccc8u, 0xbdccccc8u },
        new object[] { 1, 0xbf4cccccu, 0xbf19999bu, 0x3f4cccccu, 0x3cb851ecu, 0x3f000000u, 0xbf800000u },
        new object[] { 2, 0xbf4cccccu, 0xbf19999bu, 0x3f4cccccu, 0x3d0a3d71u, 0x3f800000u, 0xbf800000u },
        new object[] { 1, 0xbf4ccccdu, 0xbf199999u, 0x3f4ccccdu, 0x3cb851ecu, 0x3f000000u, 0xbf800000u },
        new object[] { 2, 0xbf4ccccdu, 0xbf199999u, 0x3f4ccccdu, 0x3d0a3d71u, 0x3f800000u, 0xbf800000u },
        new object[] { 1, 0xbf4cccceu, 0xbf199998u, 0x3f4cccceu, 0x3cb851e6u, 0x3efffff8u, 0xbf7ffff0u },
        new object[] { 2, 0xbf4cccceu, 0xbf199998u, 0x3f4cccceu, 0x3d0a3d6bu, 0x3f7ffff0u, 0xbf7ffff0u },
        new object[] { 1, 0x3f4cccccu, 0xbf19999bu, 0xbf4cccccu, 0x3cb851ecu, 0x3f000000u, 0xbf800000u },
        new object[] { 2, 0x3f4cccccu, 0xbf19999bu, 0xbf4cccccu, 0x3d0a3d71u, 0x3f800000u, 0xbf800000u },
        new object[] { 1, 0x3f4ccccdu, 0xbf199999u, 0xbf4ccccdu, 0x3cb851ecu, 0x3f000000u, 0xbf800000u },
        new object[] { 2, 0x3f4ccccdu, 0xbf199999u, 0xbf4ccccdu, 0x3d0a3d71u, 0x3f800000u, 0xbf800000u },
        new object[] { 1, 0x3f4cccceu, 0xbf199998u, 0xbf4cccceu, 0x3cb851e6u, 0x3efffff8u, 0xbf7ffff0u },
        new object[] { 2, 0x3f4cccceu, 0xbf199998u, 0xbf4cccceu, 0x3d0a3d6bu, 0x3f7ffff0u, 0xbf7ffff0u },
        new object[] { 1, 0x3f666665u, 0xbedf2cfcu, 0xbf666665u, 0x3c3851f8u, 0x3dcccd00u, 0xbdcccd00u },
        new object[] { 2, 0x3f666665u, 0xbedf2cfcu, 0xbf666665u, 0x3c385203u, 0x3dcccd00u, 0xbdcccd00u },
        new object[] { 1, 0x3f666666u, 0xbedf2cf7u, 0xbf666666u, 0x3c3851ecu, 0x3dcccce0u, 0xbdcccd00u },
        new object[] { 2, 0x3f666666u, 0xbedf2cf7u, 0xbf666666u, 0x3c3851ecu, 0x3dcccd00u, 0xbdcccd00u },
        new object[] { 1, 0x3f666667u, 0xbedf2cf3u, 0xbf666667u, 0x3c3851ecu, 0x3dccccc8u, 0xbdccccc8u },
        new object[] { 2, 0x3f666667u, 0xbedf2cf3u, 0xbf666667u, 0x3c3851ecu, 0x3dccccc8u, 0xbdccccc8u },
    };

    [Fact]
    public void RejectsNonfiniteInputsAndDegenerateDirectionsWithoutInventingFallback()
    {
        foreach(var direction in new[] {Vector3.Zero,Vector3.UnitZ,-Vector3.UnitZ,new Vector3(float.NaN,1,0),new Vector3(0,float.PositiveInfinity,0),new Vector3(float.MaxValue,0,0)})
            Assert.ThrowsAny<ArgumentException>(()=>OriginalShadowTransform.Create(Matrix4x4.Identity,Vector3.Zero,direction,.02f,1));
        foreach(float scale in new[] {0f,-.1f,float.NaN,float.PositiveInfinity,float.Epsilon,float.MaxValue})
            Assert.ThrowsAny<ArgumentException>(()=>OriginalShadowTransform.Create(Matrix4x4.Identity,Vector3.Zero,new(0,-1,0),scale,1));
        Assert.ThrowsAny<ArgumentException>(()=>OriginalShadowTransform.Create(Matrix4x4.Identity,new(float.NaN,0,0),new(0,-1,0),.02f,1));
        Assert.ThrowsAny<ArgumentException>(()=>OriginalShadowTransform.Create(Matrix4x4.Identity,new(float.MaxValue),new(0,-1,0),.02f,1));
        var invalid=Matrix4x4.Identity;invalid.M31=float.NegativeInfinity;
        Assert.ThrowsAny<ArgumentException>(()=>OriginalShadowTransform.Create(invalid,Vector3.Zero,new(0,-1,0),.02f,1));
    }

    private static float Float(uint bits)=>BitConverter.UInt32BitsToSingle(bits);
    private static void AssertVectorNear(Vector4 expected,Vector4 actual,double tolerance)
    {
        Assert.InRange(Math.Abs(actual.X-expected.X),0,tolerance);
        Assert.InRange(Math.Abs(actual.Y-expected.Y),0,tolerance);
        Assert.InRange(Math.Abs(actual.Z-expected.Z),0,tolerance);
        Assert.InRange(Math.Abs(actual.W-expected.W),0,tolerance);
    }

    // Independent point oracle: double geometry, dot products, then a direct rational
    // projection. It never builds or multiplies a light matrix or calls the builder.
    private static Vector4 GeometricClipOracle(Vector3 point,Matrix4x4 world,Vector3 camera,Vector3 outgoing,double scale,int mode)
    {
        double wx=point.X*(double)world.M11+point.Y*(double)world.M21+point.Z*(double)world.M31+world.M41;
        double wy=point.X*(double)world.M12+point.Y*(double)world.M22+point.Z*(double)world.M32+world.M42;
        double wz=point.X*(double)world.M13+point.Y*(double)world.M23+point.Z*(double)world.M33+world.M43;
        double dl=Math.Sqrt((double)outgoing.X*outgoing.X+(double)outgoing.Y*outgoing.Y+(double)outgoing.Z*outgoing.Z);
        double zx=outgoing.X/dl,zy=outgoing.Y/dl,zz=outgoing.Z/dl;
        double xl=Math.Sqrt(zx*zx+zy*zy),xx=-zy/xl,xy=zx/xl;
        double yx=-zz*xy,yy=zz*xx,yz=zx*xy-zy*xx;
        double dx=wx-(camera.X-50d*outgoing.X),dy=wy-(camera.Y-50d*outgoing.Y),dz=wz-(camera.Z-50d*outgoing.Z);
        double f=-zz,af=Math.Abs(f),t=Math.Clamp((.9-af)/.1,0,1);
        double k=scale*(1+(mode==1?1:2)*t);
        double a=af>.9?1-af:(mode==1?.1+.4*t:.1+.9*t);
        double b=af>.9?af-1:-.1-.9*t;
        double x=k*(dx*xx+dy*xy),y=k*(dx*yx+dy*yy+dz*yz),z=.15*scale*(dx*zx+dy*zy+dz*zz);
        double w=1+a*y;
        return new((float)x,(float)(y+b),(float)(z+.5*a*y),(float)w);
    }
}
