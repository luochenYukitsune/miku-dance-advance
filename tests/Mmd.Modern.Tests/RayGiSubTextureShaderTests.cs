using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RayGiSubTextureShaderTests
{
    [Theory]
    [InlineData(false,false,false)]
    [InlineData(false,true,true)]
    [InlineData(true,false,true)]
    [InlineData(true,true,false)]
    public void OptionalUvTableOnlyChangesSharedCandidateCoordinates(bool hardware,bool morph,bool native)
    {
        string ordinary=D3D12RayGiShaders.Source(hardware,morph,native);
        string subTexture=D3D12RayGiShaders.Source(hardware,morph,native,true);
        Assert.DoesNotContain("register(t10)",ordinary);
        Assert.Contains("StructuredBuffer<GiSubTextureUv> GiSubTextureUvs:register(t10)",subTexture);
        Assert.Contains("material.SphereTexture.w==3?GiSubTextureUvAt(triangleIndex,bary):GiSphereUv(tri,bary)",subTexture);
        Assert.Contains("row.Uv01.xy*w.x+row.Uv01.zw*w.y+row.Uv2.xy*w.z",subTexture);
        static string Albedo(string source)
        {
            int start=source.IndexOf("float3 GetAlbedo(",StringComparison.Ordinal);
            return source[start..source.IndexOf("bool AcceptCandidate(",start,StringComparison.Ordinal)];
        }
        Assert.Equal(Albedo(ordinary),Albedo(subTexture));
        Assert.Equal(ordinary[ordinary.IndexOf("[numthreads(8,8,1)]",StringComparison.Ordinal)..],
            subTexture[subTexture.IndexOf("[numthreads(8,8,1)]",StringComparison.Ordinal)..]);
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public void EffectiveMode3BranchValidatesMultiplicativeBudget(bool diffuse,bool inheritedAdd)
    {
        var original=Frame(diffuse,inheritedAdd);
        var factors=OriginalMaterialTextureFactors.Identity with {Sphere=new(new(4,1,1,1),Vector4.Zero)};
        var frame=D3D12GiTextureFactorTests.WithFactors(original,0,factors);
        if(diffuse&&inheritedAdd)D3D12RayGiRenderer.Validate(frame,D3D12GiTextureFactorTests.Options());
        else Assert.Throws<NotSupportedException>(()=>D3D12RayGiRenderer.Validate(frame,D3D12GiTextureFactorTests.Options()));
    }

    [Theory]
    [InlineData(-.1f)]
    [InlineData(1.1f)]
    [InlineData(float.MaxValue)]
    public void TwoMapToonWeightHasExplicitCoverageDomain(float weight)
    {
        var factors=OriginalMaterialTextureFactors.Identity with {Toon=new(new(1,1,1,weight),Vector4.Zero)};
        var twoMap=D3D12GiTextureFactorTests.WithFactors(Frame(true,false),0,factors);
        Assert.Throws<NotSupportedException>(()=>D3D12RayGiRenderer.Validate(twoMap,D3D12GiTextureFactorTests.Options()));
        // The no-diffuse technique restores raw opacity and never consumes Toon W.
        var noMap=D3D12GiTextureFactorTests.WithFactors(Frame(false,false),0,factors);
        D3D12RayGiRenderer.Validate(noMap,D3D12GiTextureFactorTests.Options());
    }

    private static OriginalFrameScene Frame(bool diffuse,bool inheritedAdd)
    {
        var source=D3D12GiTextureFactorTests.Fixture();
        var mesh=source.Surface.Snapshot;
        var materials=mesh.Materials.ToArray();
        materials[0]=materials[0] with {Diffuse=new(1e38f,.6f,.6f,1),DiffuseTexture=diffuse?0:-1,Ambient=Vector3.Zero};
        var bindings=source.Surface.Materials.ToArray();
        bindings[0]=bindings[0] with {SphereMode=OriginalSphereMode.SubTexture,SphereTexture=2,SubTextureAdditive=inheritedAdd};
        var uvs=new SceneAdditionalUvs(1,Enumerable.Repeat(new SceneAdditionalUvVertex(new(.25f,.5f,0,0),default,default,default),mesh.Vertices.Length).ToArray());
        var surface=new OriginalStyleScene(mesh.WithMaterials(materials).WithAdditionalUvs(uvs),bindings,
            source.Surface.Profile with {LightAmbient=Vector3.One});
        var light=new NativeSceneLight(Guid.NewGuid(),"native overflow control",SceneLightKind.Directional,
            Vector3.Zero,Quaternion.Identity,Vector3.One,1,CastShadows:false);
        return new(surface,options:source.Options,materials:source.Materials,lighting:new([light]));
    }
}
