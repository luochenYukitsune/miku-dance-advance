using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class OriginalStyleTests
{
    [Fact]
    public void HostColoursUsePmxDiffuseAndAmbientWithUpperClampAndZeroPowerOnly()
    {
        var m = Material() with { Diffuse = new(2, -.5f, .4f, 1.4f), Ambient = new(.2f, .1f, .3f), Specular = new(.2f, .3f, .4f), Shininess = 0 };
        var p = Profile() with { LightAmbient = new(.6f), LightSpecular = new(.5f, .6f, .7f) };
        var host = OriginalStyleMath.ResolveHostColors(m, p);
        Near(new(1, -.2f, .54f, 1), host.EgColor);
        Near(new(.1f, .18f, .28f, .1f), host.SpecularPower);
        Assert.Equal(2.5f, OriginalStyleMath.ResolveHostColors(m with { Shininess = 2.5f }, p).SpecularPower.W);
    }

    [Fact]
    public void ToonUsesBottomLeftRgbDividedBy256AndIgnoresAlpha()
    {
        var image = new SceneTexture("asymmetric toon", 2, 2,
            [1,2,3,0, 4,5,6,255, 255,128,64,7, 11,12,13,0]);
        Assert.Equal(new Vector3(255/256f, .5f, .25f), OriginalStyleMath.ReadToonShadowColor(image));
    }

    [Theory]
    [InlineData(-.25f, 0)] [InlineData(0, 0)] [InlineData(1f/6, .5f)] [InlineData(1f/3, 1)] [InlineData(1, 1)]
    public void FacingUsesNormalizedNormalTimesThree(float d, float expected)
    {
        Vector3 n = new(MathF.Sqrt(1-d*d), 0, -d);
        Assert.InRange(MathF.Abs(OriginalStyleMath.Facing(n * 7, -Vector3.UnitZ)-expected), 0, 1e-6f);
    }

    [Fact]
    public void SphereUsesRawVertexNormalAndRowVectorRotationBeforeInterpolation()
    {
        var rotate = Matrix4x4.CreateRotationZ(.7f) * Matrix4x4.CreateRotationY(.3f);
        Vector3 raw = new(.2f, .9f, -.7f);
        var projected = Vector3.TransformNormal(raw, rotate);
        Assert.Equal(new(.5f*projected.X+.5f, -.5f*projected.Y+.5f), OriginalStyleMath.SphereUv(raw, rotate));
        Assert.NotEqual(OriginalStyleMath.SphereUv(raw, rotate), OriginalStyleMath.SphereUv(Vector3.Normalize(raw), rotate));
        Assert.NotEqual(OriginalStyleMath.SphereUv(raw, rotate), OriginalStyleMath.SphereUv(raw, Matrix4x4.Transpose(rotate)));
    }

    [Theory]
    [InlineData(OriginalSphereMode.None)] [InlineData(OriginalSphereMode.Multiply)] [InlineData(OriginalSphereMode.Add)]
    public void SurfaceSpherePrecedesToonAndAlphaRemainsRawProduct(OriginalSphereMode mode)
    {
        Vector4 eg = new(.6f,.3f,.2f,.8f), tex = new(128/255f,192/255f,64/255f,160/255f), sphere = new(64/255f,128/255f,192/255f,208/255f);
        Vector3 toon = new(.8f,.6f,.4f), normal = new(MathF.Sqrt(35/36f),0,-1/6f);
        var actual = OriginalStyleMath.EvaluateSurface(eg, new(0,0,0,8), toon, normal, Vector3.Zero,
            new(0,0,-5), -Vector3.UnitZ, tex, sphere, mode);
        Vector3 baseColor = new(eg.X*tex.X,eg.Y*tex.Y,eg.Z*tex.Z), sp = new(sphere.X,sphere.Y,sphere.Z);
        if (mode == OriginalSphereMode.Multiply) baseColor *= sp;
        if (mode == OriginalSphereMode.Add) baseColor += sp;
        var expectedRgb = baseColor * (.5f * Vector3.One + .5f * toon);
        Near(new(expectedRgb, eg.W*tex.W*(mode == OriginalSphereMode.None ? 1 : sphere.W)), actual);
    }

    [Fact]
    public void MissingDiffuseTextureIgnoresSuppliedRgbAndAlphaSample()
    {
        Vector4 eg=new(.6f,.3f,.2f,.8f);
        var actual=OriginalStyleMath.EvaluateSurface(eg,new(0,0,0,8),Vector3.One,
            -Vector3.UnitZ,Vector3.Zero,new(0,0,-5),-Vector3.UnitZ,new(.1f,.2f,.3f,0),Vector4.One,OriginalSphereMode.None,false);
        Near(eg,actual);
    }

    [Fact]
    public void HighlightIsIndependentOfBaseAndClampedBeforeFacing()
    {
        var actual = OriginalStyleMath.EvaluateSurface(Vector4.Zero, new(2,.5f,.2f,8), Vector3.Zero,
            -Vector3.UnitZ, Vector3.Zero, new(0,0,-5), -Vector3.UnitZ, Vector4.One, Vector4.One, OriginalSphereMode.None);
        Near(new(1,.5f,.2f,0), actual);
    }

    [Fact]
    public void WrapperDefensivelyCopiesBindingsAndRejectsInvalidSphereReferences()
    {
        var mesh = D3D12MeshTests.Quad();
        OriginalStyleMaterialBinding[] bindings = [new(new(.8f), -1, OriginalSphereMode.None, true)];
        var wrapped = new OriginalStyleScene(mesh, bindings, Profile());
        bindings[0] = new(Vector3.Zero);
        Assert.Equal(new Vector3(.8f), wrapped.Materials[0].ToonShadowColor);
        Assert.Throws<ArgumentException>(() => new OriginalStyleScene(mesh, []));
        Assert.Throws<ArgumentException>(() => new OriginalStyleScene(mesh, [new(Vector3.One, 99, OriginalSphereMode.Add)]));
        Assert.Throws<ArgumentException>(() => new OriginalStyleScene(mesh, [new(Vector3.One)], Profile() with { SphereRotation = new Matrix4x4() }));
    }

    private static SceneMaterial Material() => new("fixture",0,6,Vector4.One,Vector3.Zero,Vector3.Zero,8);
    private static OriginalStyleProfile Profile() => new(Vector3.One,Vector3.One,Matrix4x4.Identity,OriginalTextureSampling.PointClamp);
    private static void Near(Vector4 expected, Vector4 actual) => Assert.InRange(Vector4.Distance(expected, actual),0,1e-6f);
}
