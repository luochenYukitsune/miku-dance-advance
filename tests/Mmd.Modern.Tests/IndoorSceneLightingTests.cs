using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class IndoorSceneLightingTests
{
    private static readonly Dictionary<Guid, CharacterMorphChannels> Morphs = [];

    [Fact]
    public void SceneResponseSeparatesFixedPmxAmbientFromActualMainLightWithoutChangingAlpha()
    {
        var material = new SceneMaterial("explicit", 0, 3, new(.8f,.6f,.4f,.3f), new(.2f), new(.502f), 16);
        var off = new OriginalStyleProfile(Vector3.Zero, Vector3.Zero, Matrix4x4.Identity);
        var lit = off with { LightAmbient = new(.6f) };
        Assert.Equal(OriginalStyleMath.ResolveHostColors(material, off), OriginalStyleMath.ResolveHostColors(material, off, false));
        Assert.Equal(new Vector3(.502f), Rgb(OriginalStyleMath.ResolveHostColors(material, off).EgColor));
        Assert.Equal(new Vector4(0,0,0,.3f), OriginalStyleMath.ResolveHostColors(material, off, true).EgColor);
        Assert.InRange(Vector4.Distance(new(.48f,.36f,.24f,.3f), OriginalStyleMath.ResolveHostColors(material, lit, true).EgColor),0,1e-6f);
        Assert.Equal(Vector3.Zero, OriginalStyleMath.ResolveAmbientShare(material, lit, true));
        Assert.Equal(OriginalStyleMath.ResolveAmbientShare(material, lit), OriginalStyleMath.ResolveAmbientShare(material, lit, false));
    }

    [Fact]
    public void ViewFitsCombinedWorldGeometryAfterActualSampledLightDirection()
    {
        var source = Indoor();
        var light = new PmmLightPose(new(.4f,.5f,.6f), new(.8f,-1,.2f));
        var result = D3D12ViewScene.Resolve(source, null, light, null, Morphs, new());
        var shadow = Assert.IsType<OriginalSelfShadowOptions>(result.Options!.SelfShadow);
        Assert.Equal(ModernDirectionalShadowProjection.Fit(result.Surface.Snapshot), shadow.LightViewProjection);
        Assert.NotEqual(ModernDirectionalShadowProjection.Fit(source.Surface.Snapshot), shadow.LightViewProjection);
        Assert.Equal(light.Color, result.Surface.Profile.LightAmbient);
        Assert.True(shadow.Generate);
        Assert.False(shadow.PartialFar);
        Assert.Null(source.Options?.SelfShadow);
    }

    [Fact]
    public void ExplicitOffAndLegacyDefaultsDoNotAcquireSceneShadowMaps()
    {
        var source = Indoor();
        var off = D3D12ViewScene.Resolve(source, null, null, new(0, float.NaN), Morphs, new());
        Assert.Null(off.Options!.SelfShadow);
        var disabled = new OriginalFrameScene(source.Surface, options: new(OriginalAlphaBlendMode.Normal, true, false,
            SelfShadow: new(Matrix4x4.Identity, 512, 512, false, false, OriginalTextureSampling.PointClamp, OriginalDepthComparison.LessEqual)),
            materials: source.Materials);
        Assert.False(D3D12ViewScene.Resolve(disabled, null, null, null, Morphs, new()).Options!.SelfShadow!.Generate);
        var legacy = SceneModelShadingPolicy.Apply(D3D12OriginalOutlineTests.Box(), new ModelPurpose(ModelRole.Environment, false));
        Assert.Null(D3D12ViewScene.Resolve(legacy, null, null, null, Morphs, new()).Options!.SelfShadow);
    }

    [Fact]
    public void IndoorGiUsesActualMainRgbInsteadOfImplicitIrradianceThreeAndKeepsExplicitInputs()
    {
        var indoor = Indoor();
        var dark = new OriginalFrameScene(new(indoor.Surface.Snapshot, indoor.Surface.Materials,
            indoor.Surface.Profile with { LightAmbient = Vector3.Zero }), options: indoor.Options, materials: indoor.Materials);
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing);
        Assert.Equal(Vector3.Zero, D3D12SceneLighting.ResolveGi(dark, gi)!.LightIrradiance);
        Assert.Null(gi.DirectionalIrradiance);
        var specified = gi with { DirectionalIrradiance = new Vector3(2) };
        Assert.Same(specified, D3D12SceneLighting.ResolveGi(dark, specified));
        Assert.Same(RayGiOptions.Off, D3D12SceneLighting.ResolveGi(dark, RayGiOptions.Off));
        var legacy = D3D12OriginalOutlineTests.Box();
        Assert.Same(gi, D3D12SceneLighting.ResolveGi(legacy, gi));
    }

    private static OriginalFrameScene Indoor()
    {
        var raw = D3D12OriginalOutlineTests.Box();
        var source = new OriginalFrameScene(raw.Surface, raw.Outline, raw.OutlineOptions, raw.Options,
            raw.Materials.Select(f => f with { CastSelfShadow = true, ReceiveSelfShadow = true }));
        return SceneModelShadingPolicy.Apply(source, new ModelPurpose(ModelRole.Environment, true));
    }
    private static Vector3 Rgb(Vector4 v) => new(v.X,v.Y,v.Z);
}

[Trait("Category", "Gpu")]
public sealed class IndoorSceneLightingGpuTests
{
    private const int Width = 257, Height = 193;

    [Fact]
    public void ActualGpuSceneResponseRemovesFixedAmbientAndWhiteToonWithoutChangingLegacy()
    {
        using var session = new D3D12FrameSession();
        var scene = Plane(false, false);
        var legacy = new OriginalFrameScene(scene.Surface, options: scene.Options,
            materials: scene.Materials.Select(f => f with { UseSceneLighting = false }));
        var old = session.Capture(legacy, Width, Height);
        var dark = session.Capture(scene, Width, Height);
        Assert.Contains(old.Rgba.Where((_, index) => index % 4 != 3), v => v > 20);
        Assert.All(dark.Rgba.Where((_, index) => index % 4 != 3), v => Assert.Equal(0, v));
        Assert.Equal(old.Rgba, session.Capture(legacy, Width, Height).Rgba);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReceiverPlanePcfAvoidsFalseShadowsButKeepsRealOccludersAcrossCubeFaces(bool cubeSeam)
    {
        using var session = new D3D12FrameSession();
        var scene = Plane(cubeSeam, false);
        var light = Light();
        var clear = session.Capture(WithLight(scene, light with { CastShadows = false }), Width, Height);
        var shadowed = session.Capture(WithLight(scene, light), Width, Height);
        int candidates = 0, errors = 0;
        for (int y = 8; y < Height-8; y++) for (int x = 8; x < Width-8; x++)
        {
            int p = (y*Width+x)*4;
            if (clear.Rgba[p] < 5) continue;
            candidates++;
            if (Math.Abs(clear.Rgba[p]-shadowed.Rgba[p]) > 2) errors++;
        }
        Assert.True(candidates > 5000);
        Assert.True(errors <= Math.Max(4,candidates/1000), $"single planar receiver has no actual occluder: {errors}/{candidates} false-shadow pixels");
        var occluded = session.Capture(WithLight(Plane(cubeSeam, true), light), Width, Height);
        int center = ((Height/2)*Width+Width/2)*4;
        Assert.True(shadowed.Rgba[center]-occluded.Rgba[center] >= 20,
            $"real hidden caster retains its geometric shadow: clear={shadowed.Rgba[center]}, blocked={occluded.Rgba[center]}");
    }

    private static NativeSceneLight Light() => new(new("a6e85388-9c6f-4c9f-9d93-587c0b8b51a3"),"explicit plane light",SceneLightKind.Point,
        Vector3.Zero,Quaternion.Identity,Vector3.One,800,220);
    private static OriginalFrameScene WithLight(OriginalFrameScene frame,NativeSceneLight light) => new(frame.Surface,
        options:frame.Options,materials:frame.Materials,lighting:new([light]));
    private static OriginalFrameScene Plane(bool seam,bool blocker)
    {
        Vector3 center = new(0,0,20), u = Vector3.Normalize(new Vector3(1,0,4)), v = Vector3.UnitY;
        Vector3 normal = Vector3.Normalize(new Vector3(4,0,-1));
        var vertices = new List<SceneVertex> {
            new(center-8*u-6*v,normal,Vector2.Zero),new(center+8*u-6*v,normal,Vector2.UnitX),
            new(center+8*u+6*v,normal,Vector2.One),new(center-8*u+6*v,normal,Vector2.UnitY) };
        var indices = new List<uint> { 0,1,2,0,2,3 };
        var materials = new List<SceneMaterial> { new("sloping receiver",0,6,new(.35f,.35f,.35f,1),Vector3.Zero,new(.5f),1) };
        var flags = new List<OriginalFrameMaterialFlags> { new(true,true,false) { UseSceneLighting = true } };
        if(blocker)
        {
            vertices.AddRange([new(new(-.3f,-.3f,10),-Vector3.UnitZ,Vector2.Zero),new(new(.3f,-.3f,10),-Vector3.UnitZ,Vector2.Zero),
                new(new(.3f,.3f,10),-Vector3.UnitZ,Vector2.Zero),new(new(-.3f,.3f,10),-Vector3.UnitZ,Vector2.Zero)]);
            indices.AddRange([4,5,6,4,6,7]);
            materials.Add(new("hidden real caster",6,6,Vector4.One,Vector3.Zero,Vector3.Zero,1));
            flags.Add(new(true,false,false,SurfaceEnabled:false) { UseSceneLighting = true });
        }
        var world = seam ? Matrix4x4.CreateRotationY(MathF.PI/4) : Matrix4x4.Identity;
        var camera = new SceneCamera(Vector3.Transform(center+normal*25,world),Vector3.Transform(center,world),Vector3.UnitY,14,.1f,200);
        var mesh = new SceneSnapshot(vertices.ToArray(),indices.ToArray(),materials,world:world,camera:camera);
        var surface = new OriginalStyleScene(mesh,materials.Select(_=>new OriginalStyleMaterialBinding(Vector3.One,DoubleSided:true)),
            new(Vector3.Zero,Vector3.Zero,Matrix4x4.Identity));
        return new(surface,options:new(OriginalAlphaBlendMode.Normal,true,false,BackgroundColor:new(0,0,0,1)),materials:flags);
    }
}
