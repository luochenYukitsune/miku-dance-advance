using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12RayGiSubTextureVisibilityTests
{
    private const int Width=65,Height=49;

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing,false,false)]
    [InlineData(LightingMode.SoftwareRayTracing,false,true)]
    [InlineData(LightingMode.SoftwareRayTracing,true,false)]
    [InlineData(LightingMode.SoftwareRayTracing,true,true)]
    [InlineData(LightingMode.HardwareRayTracing,false,false)]
    [InlineData(LightingMode.HardwareRayTracing,false,true)]
    [InlineData(LightingMode.HardwareRayTracing,true,false)]
    [InlineData(LightingMode.HardwareRayTracing,true,true)]
    public void Uv1RawCutoutMatchesIndependentOpaqueAndAbsentWallControls(LightingMode mode,bool diffuse,bool additive)
    {
        using var session=new D3D12FrameSession();Require(session,mode);
        var opaque=Fixture(.25f,diffuse,additive);
        var transparent=Fixture(.75f,diffuse,additive);
        var controlBindings=opaque.Surface.Materials.ToArray();
        controlBindings[1]=controlBindings[1] with {SphereMode=OriginalSphereMode.None,SphereTexture=-1};
        var solid=new OriginalFrameScene(new(opaque.Surface.Snapshot,controlBindings,opaque.Surface.Profile),
            options:opaque.Options,materials:opaque.Materials,lighting:opaque.Lighting);
        var flags=opaque.Materials.ToArray();flags[1]=flags[1] with {SurfaceEnabled=false};
        var absent=new OriginalFrameScene(solid.Surface,options:solid.Options,materials:flags,lighting:solid.Lighting);
        var ray=new RayGiRay(new(0,0,-.01f),-Vector3.UnitZ,.001f,50);
        Assert.True(RayGiScene.FromOriginal(opaque).Bvh.TraceAny(ray));
        Assert.False(RayGiScene.FromOriginal(transparent).Bvh.TraceAny(ray));
        // UV0 is (0,0), and ordinary sphere projection is (.5,.5). The two
        // independent controls contain neither a mode3 material nor its UV lookup.
        var options=D3D12GiTextureFactorTests.Options(mode,true) with {SamplesPerPixel=32};
        var solidPixels=session.Capture(solid,Width,Height,rayGiOptions:options);
        var absentPixels=session.Capture(absent,Width,Height,rayGiOptions:options);
        var opaquePixels=session.Capture(opaque,Width,Height,rayGiOptions:options);
        var transparentPixels=session.Capture(transparent,Width,Height,rayGiOptions:options);
        AssertPixels(solidPixels,opaquePixels);
        AssertPixels(absentPixels,transparentPixels);
        Assert.NotEqual(Center(opaquePixels),Center(transparentPixels));
        Assert.Equal(mode,session.RayGiStatistics!.Effective);
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing,OriginalTextureSampling.PointClamp,false)]
    [InlineData(LightingMode.SoftwareRayTracing,OriginalTextureSampling.PointWrap,true)]
    [InlineData(LightingMode.SoftwareRayTracing,OriginalTextureSampling.LinearClamp,false)]
    [InlineData(LightingMode.SoftwareRayTracing,OriginalTextureSampling.LinearWrap,true)]
    [InlineData(LightingMode.HardwareRayTracing,OriginalTextureSampling.PointClamp,false)]
    [InlineData(LightingMode.HardwareRayTracing,OriginalTextureSampling.PointWrap,true)]
    [InlineData(LightingMode.HardwareRayTracing,OriginalTextureSampling.LinearClamp,false)]
    [InlineData(LightingMode.HardwareRayTracing,OriginalTextureSampling.LinearWrap,true)]
    public void GpuUsesSelectedUv1AddressingAndIgnoresUnconsumedChannels(LightingMode mode,OriginalTextureSampling sampling,bool accepted)
    {
        using var session=new D3D12FrameSession();Require(session,mode);
        var actual=Fixture(1.25f,true,true,sampling,extraChannel:1000);
        var expected=Fixture(accepted ? .25f : .75f,true,true,sampling);
        var ray=new RayGiRay(new(0,0,-.01f),-Vector3.UnitZ,.001f,50);
        Assert.Equal(accepted,RayGiScene.FromOriginal(actual).Bvh.TraceAny(ray));
        var options=D3D12GiTextureFactorTests.Options(mode) with {SamplesPerPixel=16};
        AssertPixels(session.Capture(expected,Width,Height,rayGiOptions:options),session.Capture(actual,Width,Height,rayGiOptions:options));
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void RepeatedUvPosesAndAbortedFirstUploadRecoverWithBoundedCache(LightingMode mode)
    {
        using var session=new D3D12FrameSession();Require(session,mode);
        var selected=Fixture(.25f,true,false);
        var options=D3D12GiTextureFactorTests.Options(mode,true) with {SamplesPerPixel=4};
        Assert.Throws<OperationCanceledException>(()=>session.RenderForPresent(selected,33,25,options,
            (_,_)=>throw new OperationCanceledException("Abort initial SubTexture UV upload")));
        Assert.False(session.Runtime.IsRecording);
        var recovered=session.Capture(selected,33,25,rayGiOptions:options);
        using(var fresh=new D3D12FrameSession())Assert.Equal(fresh.Capture(selected,33,25,rayGiOptions:options).Rgba,recovered.Rgba);
        // Native shadows retain one resource in each of the three fence slots. A single
        // synchronous capture warms only one slot; establish the fixed ring before
        // checking whether changing UV snapshots grows the active cache.
        for(int i=0;i<3;i++)using(session.Render(selected,33,25,rayGiOptions:options)){}
        int cached=session.Statistics!.CachedResources;
        string initialKeys=CacheKeys(session);
        for(int slot=0;slot<3;slot++)Assert.Contains("original-native-shadow:"+slot,initialKeys);
        for(int i=0;i<8;i++)
        {
            using var output=session.Render(Fixture(.2f+i*.01f,true,false),33,25,rayGiOptions:options);
            Assert.Equal(0,output.Statistics.ReadbackCount);
            Assert.True(cached==output.Statistics.CachedResources,
                $"Pose {i}: expected {cached}, actual {output.Statistics.CachedResources}. Before {initialKeys}; after {CacheKeys(session)}");
        }
        Assert.Equal(recovered.Rgba,session.Capture(selected,33,25,rayGiOptions:options).Rgba);
        session.WaitForIdle();Assert.InRange(session.Runtime.LiveFrameResources,0,3);
    }

    private static string CacheKeys(D3D12FrameSession session)
    {
        const System.Reflection.BindingFlags hidden=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
        var renderer=typeof(D3D12FrameSession).GetField("renderer",hidden)!.GetValue(session)!;
        var cache=renderer.GetType().GetField("cache",hidden)!.GetValue(renderer)!;
        return string.Join(";",new[]{"objects","dynamicBuffers","replaceableObjects"}.Select(field=>
            field+"="+string.Join(",",((System.Collections.IDictionary)cache.GetType().GetField(field,hidden)!.GetValue(cache)!).Keys.Cast<object>())));
    }

    private static OriginalFrameScene Fixture(float uv,bool diffuse,bool additive,OriginalTextureSampling sampling=OriginalTextureSampling.PointClamp,float extraChannel=100)
    {
        var source=D3D12GiTextureFactorTests.Fixture();var mesh=source.Surface.Snapshot;
        var materials=mesh.Materials.ToArray();materials[1]=materials[1] with {DiffuseTexture=diffuse?1:-1,AlphaCutoff=.4f};
        var textures=mesh.Textures.ToArray();textures[2]=new("UV1 raw alpha",2,1,[255,255,255,255,255,255,255,0]);
        var extra=new SceneAdditionalUvs(4,Enumerable.Range(0,mesh.Vertices.Length).Select(i=>
            new SceneAdditionalUvVertex(new(uv,.5f,extraChannel+i,-extraChannel-i),new(extraChannel+77),new(extraChannel+88),new(extraChannel+99))).ToArray());
        var snapshot=new SceneSnapshot(mesh.Vertices,mesh.Indices,materials,textures,mesh.World,mesh.Camera,mesh.LightDirection,additionalUvs:extra);
        var bindings=source.Surface.Materials.ToArray();
        bindings[1]=bindings[1] with {SphereTexture=2,SphereMode=OriginalSphereMode.SubTexture,SubTextureAdditive=additive,
            TextureFactors=OriginalMaterialTextureFactors.Identity with {Diffuse=new(new(.75f,.75f,.75f,1),Vector4.Zero)}};
        var light=new NativeSceneLight(Guid.NewGuid(),"UV1 secondary direct",SceneLightKind.Directional,
            Vector3.Zero,Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI),new(.2f,.3f,.4f),.2f,CastShadows:true);
        return new(new(snapshot,bindings,source.Surface.Profile with {TextureSampling=sampling}),options:source.Options,
            materials:source.Materials,lighting:new([light]));
    }

    private static void Require(D3D12FrameSession session,LightingMode mode)
    {
        var support=session.QueryRayGiCapabilities();
        Assert.True(mode==LightingMode.HardwareRayTracing?support.HardwareAvailable:support.SoftwareAvailable,support.UnavailableReason);
        Assert.False(session.IsWarp);
    }
    private static byte[] Center(RenderedFrame frame)=>frame.Rgba.AsSpan((Height/2*Width+Width/2)*4,4).ToArray();
    private static void AssertPixels(RenderedFrame expected,RenderedFrame actual)
    {
        Assert.Equal(expected.Rgba.Length,actual.Rgba.Length);
        for(int i=0;i<actual.Rgba.Length;i++)Assert.InRange(Math.Abs(expected.Rgba[i]-actual.Rgba[i]),0,1);
    }
}
