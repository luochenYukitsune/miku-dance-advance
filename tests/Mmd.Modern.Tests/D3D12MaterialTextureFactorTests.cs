using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12MaterialTextureFactorTests
{
    private const int Width=129,Height=97,Center=(48*Width+64)*4;
    private static readonly Vector4 Background=new(20/255f,24/255f,32/255f,1);

    [Fact]
    public void AcceptedIdentityShaderSourceBytesRemainUnchanged()
    {
        // Surface/style literals remain at dd9e153; GI includes b00d31b's palette-band receiver.
        // NativeSource includes e9ff9d6's explicit scene receiver-plane PCF correction, verified
        // by IndoorSceneLightingGpuTests and the retained RoomLight02 actual-station captures.
        // Identity-factor variants must preserve these accepted literals; only line endings normalize.
        var assembly=typeof(D3D12OriginalStyleRenderer).Assembly;
        foreach(var item in new[] {
            ("OriginalFrameShaders","Source","afd46ca86380b2e4033737ec0f91cad29ed61eed7da58994a178a07f6b45dd37"),
            ("OriginalFrameShaders","GiSource","0652d09be883cd8cbfee190bc8aab766b4aa9b5f5938eff6731a61e15d40e1b3"),
            ("OriginalFrameShaders","NativeSource","e3825419ced8d31b85ad716bd212f0e13c6b8589222f6ce25e6aa46240d3b8d3"),
            ("OriginalStyleShaders","Source","2551ead7464322716fbb188f711f68a85e6d47bd802d221cb006a8cad5c4592e") })
        {
            var type=assembly.GetType("Mmd.Modern.Rendering.D3D12."+item.Item1,true)!;
            string source=(string)type.GetField(item.Item2,BindingFlags.Static|BindingFlags.NonPublic)!.GetRawConstantValue()!;
            string hash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.ReplaceLineEndings("\n"))));
            Assert.Equal(item.Item3,hash);
        }
    }

    [Fact]
    public void ToonHostMatchesRetainedIndependentFloat32FixturesExactly()
    {
        // Values from toon-host-fixtures.json, independently rounded after each SSE operation.
        var method=typeof(D3D12OriginalStyleRenderer).GetMethod("ResolveTextureMorphToon",BindingFlags.Static|BindingFlags.NonPublic)!;
        var factor=new OriginalTextureFactor(new(.5f,.25f,1.25f,.125f),new(.125f,.25f,-.125f,-.25f));
        var factors=OriginalMaterialTextureFactors.Identity with {Toon=factor};
        var cached=new OriginalStyleMaterialBinding(new(205/256f,225/256f,255/256f)) {TextureFactors=factors};
        Assert.Equal(new Vector3(.525390625f,.4697265625f,1.1201171875f),(Vector3)method.Invoke(null,[cached])!);
        Assert.Equal(new Vector3(.625f,.5f,1.125f),(Vector3)method.Invoke(null,[cached with {ToonShadowColor=Vector3.One}])!);
        var weightOnly=cached with {TextureFactors=OriginalMaterialTextureFactors.Identity with {
            Toon=new(new(1,1,1,0),Vector4.Zero)}};
        Assert.Equal(cached.ToonShadowColor,(Vector3)method.Invoke(null,[weightOnly])!);
    }

    [Fact]
    public void FactorChangeInvalidatesTemporalHistoryButEqualPacketReusesIt()
    {
        var options=new D3D12TemporalUpscalingOptions();var history=new D3D12TemporalHistory();
        var source=D3D12OriginalStyleTests.Fixture(OriginalSphereMode.Multiply,1);
        var before=new OriginalFrameScene(source,options:new(OriginalAlphaBlendMode.Normal,true,false));
        var changed=new OriginalFrameScene(WithFactors(source,Factors(0)),options:before.Options);
        history.Commit(before,options,0,8);
        Assert.Equal("None",history.ResetReason(before,options));
        Assert.Equal("SceneStructureChanged",history.ResetReason(changed,options));
        history.Commit(changed,options,0,8);
        var equal=new OriginalFrameScene(WithFactors(source,Factors(0)),options:before.Options);
        Assert.Equal("None",history.ResetReason(equal,options));
        Assert.Equal("SceneStructureChanged",history.ResetReason(before,options));
        Assert.True(before.Surface.Materials[0].TextureFactors.IsIdentity);
    }

    [Theory,InlineData((byte)255),InlineData((byte)128),InlineData((byte)0),Trait("Category","Gpu")]
    public void MaterialColorMorphChangesRasterWhileTemporalCoverageKeepsRawAlpha(byte alpha)
    {
        var source=D3D12OriginalStyleTests.Fixture(OriginalSphereMode.Multiply,1);
        var old=source.Snapshot;
        var mesh=new SceneSnapshot(old.Vertices,old.Indices,[old.Materials[0] with {Diffuse=new(.6f,.3f,.2f,1)}],
            [new("diffuse",1,1,[128,192,64,alpha]),new("sphere",1,1,[64,128,192,255])],
            old.World,old.Camera,old.LightDirection);
        var baseline=new OriginalStyleScene(mesh,source.Materials,source.Profile);
        var changed=WithFactors(baseline,Factors(1));
        var options=new OriginalFrameOptions(OriginalAlphaBlendMode.Normal,true,false);
        var before=new OriginalFrameScene(baseline,options:options);
        var after=new OriginalFrameScene(changed,options:options);
        using(var renderer=new D3D12OriginalStyleRenderer())
        {
            var color=renderer.Render(after,Width,Height);
            AssertCenter(color,Reference(changed,true));
            if(alpha>0)Assert.NotEqual(renderer.Render(before,Width,Height).Rgba,color.Rgba);
        }
        // This is the FrameSession character-input producer; no vendor SDK needed.
        using var runtime=new D3D12GpuRuntime();using var temporal=new D3D12TemporalInputPass(runtime);
        var expected=CaptureTemporal(runtime,temporal,before,before);
        var actual=CaptureTemporal(runtime,temporal,after,before);
        Assert.Equal(expected.Depth,actual.Depth);Assert.Equal(expected.Motion,actual.Motion);Assert.Equal(expected.Reactive,actual.Reactive);
        Assert.Equal(alpha==255,actual.Depth[48*Width+64]<1);
        Assert.Equal(Vector2.Zero,actual.Motion[48*Width+64]);
        Assert.InRange(actual.Reactive[48*Width+64],alpha==128?127:0,alpha==128?129:0);
    }

    public static IEnumerable<object[]> ColorCases()
    {
        foreach(bool full in new[]{false,true})
        foreach(var mode in new[]{OriginalSphereMode.None,OriginalSphereMode.Multiply,OriginalSphereMode.Add})
        for(int factors=0;factors<3;factors++) yield return [full,mode,factors];
    }

    [Theory,MemberData(nameof(ColorCases)),Trait("Category","Gpu")]
    public void DiffuseAndSphereMatchIndependentRecoveredScalarReference(bool full,OriginalSphereMode mode,int factors)
    {
        using var renderer=new D3D12OriginalStyleRenderer();
        var scene=WithFactors(D3D12OriginalStyleTests.Fixture(mode,1),Factors(factors));
        Vector4 expected=Reference(scene,full);
        var image=Capture(renderer,scene,full);
        AssertCenter(image,expected);
        Assert.Empty(renderer.LastDebugMessages);
    }

    [Theory,InlineData(false),InlineData(true),Trait("Category","Gpu")]
    public void MissingMapsIgnoreTheirColorFactors(bool full)
    {
        using var renderer=new D3D12OriginalStyleRenderer();
        var source=D3D12OriginalStyleTests.Fixture(OriginalSphereMode.None,1);
        var mesh=source.Snapshot;
        mesh=new(mesh.Vertices,mesh.Indices,[mesh.Materials[0] with {DiffuseTexture=-1}],mesh.Textures,
            mesh.World,mesh.Camera,mesh.LightDirection);
        var factor=new OriginalTextureFactor(new(-8,12,-3,2),new(4,-6,1,-.5f));
        var scene=new OriginalStyleScene(mesh,[source.Materials[0] with {
            TextureFactors=new(factor,factor,OriginalTextureFactor.Identity)}],source.Profile);
        AssertCenter(Capture(renderer,scene,full),Reference(scene,full));
        Assert.Equal(Capture(renderer,new OriginalStyleScene(mesh,source.Materials,source.Profile),full).Rgba,
            Capture(renderer,scene,full).Rgba);
    }

    [Theory,InlineData(false),InlineData(true),Trait("Category","Gpu")]
    public void ZeroColorWeightsKeepRawAlphaAndCutout(bool full)
    {
        using var renderer=new D3D12OriginalStyleRenderer();
        var scene=WithFactors(D3D12OriginalStyleTests.Fixture(OriginalSphereMode.Multiply,1),Factors(1));
        AssertCenter(Capture(renderer,scene,full),Reference(scene,full));
        if(!full) Assert.InRange(Capture(renderer,scene,false).Rgba[Center+3],103,105);
        var old=scene.Snapshot;
        var cutout=new OriginalStyleScene(new(old.Vertices,old.Indices,
            [old.Materials[0] with {AlphaCutoff=.45f}],old.Textures,old.World,old.Camera,old.LightDirection),scene.Materials,scene.Profile);
        AssertCenter(Capture(renderer,cutout,full),Background);
    }

    [Theory,InlineData(false),InlineData(true),Trait("Category","Gpu")]
    public void ToonRgbIsHostMultiplyThenAddWithoutWeightGateOrHostClamp(bool full)
    {
        using var renderer=new D3D12OriginalStyleRenderer();
        var source=D3D12OriginalStyleTests.Fixture(OriginalSphereMode.None,0);
        // The red toon result is >1 but the final red is <1: prematurely clamping
        // the host colour changes the visible result. Both W components are zero.
        var factor=new OriginalTextureFactor(new(1.5f,-.25f,.5f,0),new(.2f,.4f,.125f,0));
        var scene=WithFactors(source,OriginalMaterialTextureFactors.Identity with {Toon=factor});
        AssertCenter(Capture(renderer,scene,full),Reference(scene,full));
        // No-toon in the original host supplies white and still applies the factor.
        var white=new OriginalStyleScene(scene.Snapshot,[scene.Materials[0] with {ToonShadowColor=Vector3.One}],scene.Profile);
        AssertCenter(Capture(renderer,white,full),Reference(white,full));
        var changedW=WithFactors(scene,scene.Materials[0].TextureFactors with {
            Toon=factor with {Multiply=new(factor.Multiply.X,factor.Multiply.Y,factor.Multiply.Z,8),
                Add=new(factor.Add.X,factor.Add.Y,factor.Add.Z,-12)}});
        Assert.Equal(Capture(renderer,scene,full).Rgba,Capture(renderer,changedW,full).Rgba);
    }

    [Theory,InlineData(false),InlineData(true),Trait("Category","Gpu")]
    public void ExplicitIdentityAndReturnFromMorphRestoreEveryAcceptedByte(bool full)
    {
        using var renderer=new D3D12OriginalStyleRenderer();
        var source=D3D12OriginalStyleTests.Fixture(OriginalSphereMode.Multiply,1/6f);
        var expected=Capture(renderer,source,full);
        var explicitIdentity=WithFactors(source,OriginalMaterialTextureFactors.Identity);
        Assert.Equal(expected.Rgba,Capture(renderer,explicitIdentity,full).Rgba);
        Assert.NotEqual(expected.Rgba,Capture(renderer,WithFactors(source,Factors(0)),full).Rgba);
        Assert.Equal(expected.Rgba,Capture(renderer,source,full).Rgba);
        Assert.Equal(expected.Rgba,Capture(renderer,explicitIdentity,full).Rgba);
        using var fresh=new D3D12OriginalStyleRenderer();
        Assert.Equal(expected.Rgba,Capture(fresh,explicitIdentity,full).Rgba);
    }

    [Theory,InlineData(false),InlineData(true),Trait("Category","Gpu")]
    public void TranspChangesOnlyInsideProjectionAlphaAndOutsideSkipsToon(bool outside)
    {
        using var renderer=new D3D12OriginalStyleRenderer();
        var scene=WithFactors(D3D12OriginalStyleTests.Fixture(OriginalSphereMode.Multiply,0),Factors(0));
        var projection=Matrix4x4.Identity;
        if(outside)projection.M41=3;
        var options=new OriginalFrameOptions(OriginalAlphaBlendMode.Normal,true,true,
            new(projection,8,8,false,false,OriginalTextureSampling.PointClamp,OriginalDepthComparison.LessEqual));
        var frame=new OriginalFrameScene(scene,options:options);
        AssertCenter(renderer.Render(frame,Width,Height),Reference(scene,true,transp:!outside,outside:outside));
    }

    [Theory,InlineData(OriginalSphereMode.None),InlineData(OriginalSphereMode.Multiply),InlineData(OriginalSphereMode.Add),Trait("Category","Gpu")]
    public void NativeDirectLightUsesTheSameEffectiveDiffuseAndMultiplySphere(OriginalSphereMode mode)
    {
        using var renderer=new D3D12OriginalStyleRenderer();
        var scene=WithFactors(D3D12OriginalStyleTests.Fixture(mode,1),Factors(0));
        Vector3 lightColor=new(.5f,.75f,1);
        var light=new NativeSceneLight(Guid.NewGuid(),"factor fixture",SceneLightKind.Directional,Vector3.Zero,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI),lightColor,.2f,CastShadows:false);
        var frame=new OriginalFrameScene(scene,options:new(OriginalAlphaBlendMode.Normal,true,false),lighting:new([light]));
        AssertCenter(renderer.Render(frame,Width,Height),Reference(scene,true,nativeColor:lightColor*.2f));
        Assert.Empty(renderer.LastDebugMessages);
    }

    private static OriginalStyleScene WithFactors(OriginalStyleScene scene,OriginalMaterialTextureFactors factors)
        =>new(scene.Snapshot,[scene.Materials[0] with {TextureFactors=factors}],scene.Profile);

    private static OriginalMaterialTextureFactors Factors(int index)=>index switch {
        0=>new(new(new(.5f,1.25f,-.5f,.25f),new(.2f,-.1f,.4f,.35f)),
            new(new(.8f,.6f,1.2f,.5f),new(.05f,.1f,-.1f,.2f)),OriginalTextureFactor.Identity),
        1=>new(new(new(.25f,2,-.5f,-.5f),new(.3f,-.1f,.4f,.5f)),
            new(new(-3,4,2,.25f),new(.2f,-.3f,.4f,-.25f)),OriginalTextureFactor.Identity),
        2=>new(new(new(1.4f,.8f,1.8f,1.25f),new(-.1f,.05f,.2f,.25f)),
            new(new(1.2f,.9f,1.3f,.75f),new(-.05f,.1f,-.1f,.5f)),OriginalTextureFactor.Identity),
        _=>throw new ArgumentOutOfRangeException(nameof(index))};

    private static RenderedFrame Capture(D3D12OriginalStyleRenderer renderer,OriginalStyleScene scene,bool full)
        =>full?renderer.Render(new OriginalFrameScene(scene,options:new(OriginalAlphaBlendMode.Normal,true,false)),Width,Height)
            :renderer.Render(scene,Width,Height);

    // An independent scalar reference transcribed from retained original math, not
    // OriginalStyleMath/PmxTextureFactor.Apply or the generated HLSL helpers.
    private static Vector4 Reference(OriginalStyleScene scene,bool blend,bool transp=false,bool outside=false,Vector3 nativeColor=default)
    {
        var material=scene.Snapshot.Materials[0];var binding=scene.Materials[0];var factors=binding.TextureFactors;
        bool diffusePresent=material.DiffuseTexture>=0,spherePresent=binding.SphereMode!=OriginalSphereMode.None;
        var diffuse=diffusePresent?Read(scene.Snapshot.Textures[material.DiffuseTexture]):Vector4.One;
        var sphere=spherePresent?Read(scene.Snapshot.Textures[binding.SphereTexture]):Vector4.One;
        float alpha=material.Diffuse.W*diffuse.W*sphere.W;
        if(transp)alpha=.5f;
        if(alpha<material.AlphaCutoff)return Background;
        float facing=outside?1:Math.Clamp(Vector3.Dot(scene.Snapshot.Vertices[0].Normal,scene.Snapshot.LightDirection)*3,0,1);
        Vector3 rgb=default;
        for(int channel=0;channel<3;channel++)
        {
            float texel=diffuse[channel];
            if(diffusePresent)
            {
                float weight=factors.Diffuse.Multiply.W+factors.Diffuse.Add.W;
                texel=((texel*factors.Diffuse.Multiply[channel])+factors.Diffuse.Add[channel])*weight+(1-weight);
            }
            float eg=MathF.Min(scene.Profile.LightAmbient[channel]*material.Diffuse[channel]+material.Ambient[channel],1);
            float baseColor=eg*texel;
            float sphereColor=(sphere[channel]*factors.Sphere.Multiply[channel]+factors.Sphere.Add[channel]);
            float sphereWeight=factors.Sphere.Multiply.W+factors.Sphere.Add.W;
            float sphereMultiply=sphereColor*sphereWeight+(1-sphereWeight);
            if(binding.SphereMode==OriginalSphereMode.Multiply)baseColor*=sphereMultiply;
            if(binding.SphereMode==OriginalSphereMode.Add)baseColor+=sphereColor*sphereWeight;
            float toon=binding.ToonShadowColor[channel]*factors.Toon.Multiply[channel]+factors.Toon.Add[channel];
            float result=baseColor*(facing+toon*(1-facing));
            float native=material.Diffuse[channel]*texel*nativeColor[channel];
            if(binding.SphereMode==OriginalSphereMode.Multiply)native*=sphereMultiply;
            result+=native;
            // RGBA8_UNorm clamps the shader source at the output merger BEFORE blending.
            // Keep host/shader intermediate colours unclamped; only model this format boundary here.
            // Direct3D functional specification 17.5 (Blending Precision).
            rgb[channel]=blend?Math.Clamp(result,0,1)*Math.Clamp(alpha,0,1)
                +Background[channel]*(1-Math.Clamp(alpha,0,1)):result;
        }
        return new(rgb,blend?1:alpha);
    }

    private static Vector4 Read(SceneTexture texture)=>new(texture.Rgba[0]/255f,texture.Rgba[1]/255f,texture.Rgba[2]/255f,texture.Rgba[3]/255f);

    private static (float[] Depth,Vector2[] Motion,byte[] Reactive) CaptureTemporal(D3D12GpuRuntime runtime,
        D3D12TemporalInputPass pass,OriginalFrameScene current,OriginalFrameScene previous)
    {
        var context=runtime.Begin();
        try
        {
            var input=pass.Record(context,current,previous,Width,Height);
            var depth=Copy(input.Depth);var motion=Copy(input.MotionVectors);var reactive=Copy(input.ReactiveMask!);
            runtime.Wait(runtime.Submit());
            return (Readback<float>(depth,4),Readback<Vector2>(motion,8),Readback<byte>(reactive,1));

            (ID3D12Resource Resource,PlacedSubresourceFootPrint Footprint,int Bytes) Copy(ID3D12Resource source)
            {
                var footprint=new PlacedSubresourceFootPrint[1];
                runtime.Device.GetCopyableFootprints(source.Description,0,1,0,footprint,new int[1],new ulong[1],out ulong size);
                var buffer=context.Own(runtime.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer(size),ResourceStates.CopyDest));
                context.Commands.ResourceBarrierTransition(source,D3D12TemporalInputs.ShaderReadState,ResourceStates.CopySource);
                context.Commands.CopyTextureRegion(new TextureCopyLocation(buffer,footprint[0]),0,0,0,new TextureCopyLocation(source,0));
                return (buffer,footprint[0],checked((int)size));
            }
            T[] Readback<T>((ID3D12Resource Resource,PlacedSubresourceFootPrint Footprint,int Bytes) source,int stride) where T:unmanaged
            {
                var result=new T[Width*Height];var data=source.Resource.Map<byte>(0,source.Bytes);
                try {for(int y=0;y<Height;y++)MemoryMarshal.Cast<byte,T>(data.Slice(y*source.Footprint.Footprint.RowPitch,Width*stride))
                    .CopyTo(result.AsSpan(y*Width,Width));}
                finally {source.Resource.Unmap(0);}
                return result;
            }
        }
        finally {runtime.Abort();}
    }

    private static void AssertCenter(RenderedFrame frame,Vector4 expected)
    {
        for(int channel=0;channel<4;channel++)
            Assert.InRange(MathF.Abs(frame.Rgba[Center+channel]-Math.Clamp(expected[channel],0,1)*255),0,1.1f);
    }
}
