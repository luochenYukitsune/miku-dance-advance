using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
[Trait("Category","Gpu")]
public sealed class D3D12SubTextureGiReceiverTests
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
    public void GeneratedCurrentPrimaryShadowControlsEffectiveReceiverOpacity(LightingMode mode,bool noDiffuse,bool inheritedAdd)
    {
        using var session=new D3D12FrameSession();
        var support=session.QueryRayGiCapabilities();
        Assert.True(mode==LightingMode.HardwareRayTracing?support.HardwareAvailable:support.SoftwareAvailable,support.UnavailableReason);
        var options=D3D12GiTextureFactorTests.Options(mode,true) with {SamplesPerPixel=1,Strength=0};
        var shadowed=Fixture(noDiffuse,inheritedAdd,true);
        var white=Fixture(noDiffuse,inheritedAdd,false);
        var shadow=Capture(session,shadowed,options);
        Assert.Equal(1,session.VisibilityStatistics!.ShadowDrawn);
        var unshadowed=Capture(session,white,options);
        int center=Height/2*Width+Width/2;
        // The caster is hidden from camera/GI, but still generates the primary map.
        // UV1 raw S.a is one, while actual two-map alpha becomes Toon W (.2).
        Assert.Equal(noDiffuse?unshadowed.Normal[center].W:0,shadow.Normal[center].W);
        Assert.InRange(unshadowed.Normal[center].W,1f,2f);
        Assert.InRange(shadow.Color.Rgba[center*4+3],noDiffuse?254:50,noDiffuse?255:52);
        Assert.Equal(255,unshadowed.Color.Rgba[center*4+3]);
        Assert.True(RayGiScene.FromOriginal(shadowed).Bvh.TraceAny(new(new(0,0,-.1f),Vector3.UnitZ,.001f,.5f)));
        Assert.Equal(session.Capture(shadowed,Width,Height).Rgba,shadow.Color.Rgba);
        // The current map is recorded once across spatial samples, with no regular readback.
        var ssaa=new OriginalFrameScene(shadowed.Surface,options:shadowed.Options! with {SpatialSamples=2},materials:shadowed.Materials);
        using var output=session.Render(ssaa,Width,Height,rayGiOptions:options);
        Assert.Equal(1,session.VisibilityStatistics!.ShadowDrawn);
        Assert.Equal(2,session.RayGiStatistics!.DispatchCount);
        Assert.Equal(0,output.Statistics.ReadbackCount);
    }

    internal static OriginalFrameScene Fixture(bool noDiffuse,bool additive,bool receive)
    {
        var source=D3D12OriginalFrameTests.Shadow(receive);var mesh=source.Surface.Snapshot;
        var materials=mesh.Materials.ToArray();materials[0]=materials[0] with {DiffuseTexture=noDiffuse?-1:0};
        var uv=new SceneAdditionalUvs(1,Enumerable.Repeat(new SceneAdditionalUvVertex(new(.25f,.75f,19,23),default,default,default),mesh.Vertices.Length).ToArray());
        var snapshot=new SceneSnapshot(mesh.Vertices,mesh.Indices,materials,[new("white",1,1,[255,255,255,255])],
            mesh.World,mesh.Camera,mesh.LightDirection,additionalUvs:uv);
        var bindings=source.Surface.Materials.ToArray();bindings[0]=bindings[0] with {SphereTexture=0,SphereMode=OriginalSphereMode.SubTexture,
            SubTextureAdditive=additive,TextureFactors=OriginalMaterialTextureFactors.Identity with {Toon=new(new(1,1,1,.2f),Vector4.Zero)}};
        var flags=source.Materials.ToArray();flags[1]=flags[1] with {SurfaceEnabled=false};
        return new(new(snapshot,bindings,source.Surface.Profile),options:source.Options! with {BackgroundColor=Vector4.Zero},materials:flags);
    }

    private static (RenderedFrame Color,Vector4[] Normal) Capture(D3D12FrameSession session,OriginalFrameScene frame,RayGiOptions options)
    {
        string directory=Path.Combine(AppContext.BaseDirectory,"subtexture-gi-receiver-artifacts",Guid.NewGuid().ToString("N"));
        string? previous=Environment.GetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS");
        try
        {
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS",directory);
            var color=session.Capture(frame,Width,Height,rayGiOptions:options);
            var file=Directory.GetFiles(directory,"normal-id-*.bin",SearchOption.AllDirectories).Single();
            return (color,MemoryMarshal.Cast<byte,Vector4>(File.ReadAllBytes(file)).ToArray());
        }
        finally {Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS",previous);}
    }
}
