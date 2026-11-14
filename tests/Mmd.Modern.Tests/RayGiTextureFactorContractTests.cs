using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RayGiTextureFactorContractTests
{
    [Fact]
    public void OptionalFactorRowsPreserveMaterialAbiAndAreCopiedInMaterialOrder()
    {
        Assert.Equal(96,Marshal.SizeOf<RayGiMaterialGpu>());
        Assert.Equal(32,Marshal.SizeOf<RayGiDiffuseTextureFactorGpu>());
        Assert.Equal(16,Marshal.OffsetOf<RayGiDiffuseTextureFactorGpu>(nameof(RayGiDiffuseTextureFactorGpu.Add)).ToInt32());
        var source=RayGiScene.FromOriginal(RayGiBvhTests.Style());
        Assert.True(source.DiffuseTextureFactors.IsEmpty);
        RayGiDiffuseTextureFactorGpu[] rows=[new(new(.5f,1,1.5f,.25f),new(.1f,.2f,.3f,.75f))];
        var scene=new RayGiScene(source.Triangles,source.Materials,source.Texels,rows);
        var saved=rows[0];rows[0]=default;
        Assert.Equal(saved,scene.DiffuseTextureFactors[0]);
        Assert.Equal(source.Materials.ToArray(),scene.Materials.ToArray());
        Assert.Equal(source.Triangles.ToArray(),scene.Triangles.ToArray());
        Assert.Throws<ArgumentException>(()=>new RayGiScene(source.Triangles,source.Materials,source.Texels,
            new RayGiDiffuseTextureFactorGpu[2]));
        Assert.Throws<ArgumentException>(()=>new RayGiScene(source.Triangles,source.Materials,source.Texels,
            [new(new(float.NaN),Vector4.Zero)]));
    }

    [Fact]
    public void AdapterSkipsMissingMapFactorsAndCameraChangesShareItsSideTable()
    {
        var source=RayGiBvhTests.Style(texture:new("tex",1,1,[64,128,192,128]));
        var factor=new OriginalTextureFactor(new(.5f,.75f,1,.2f),new(.1f,.2f,-.1f,.8f));
        var style=new OriginalStyleScene(source.Snapshot,
            [source.Materials[0] with {TextureFactors=OriginalMaterialTextureFactors.Identity with {Diffuse=factor}}],source.Profile);
        var scene=RayGiScene.FromOriginal(style);
        Assert.Equal(factor.Multiply,scene.DiffuseTextureFactors[0].Multiply);
        Assert.Equal(factor.Add,scene.DiffuseTextureFactors[0].Add);
        var turned=RayGiScene.FromOriginal(new OriginalFrameScene(style,options:new(OriginalAlphaBlendMode.Normal,true,false,
            SphereRotationOverride:Matrix4x4.CreateRotationY(.3f))));
        Assert.Equal(scene.MaterialIdentity,turned.MaterialIdentity);
        var field=typeof(RayGiScene).GetField("diffuseTextureFactors",BindingFlags.Instance|BindingFlags.NonPublic)!;
        Assert.Same(field.GetValue(scene),field.GetValue(turned));
        var identity=RayGiScene.FromOriginal(source);
        Assert.Equal(identity.GeometryIdentity,scene.GeometryIdentity);
        Assert.NotEqual(identity.MaterialIdentity,scene.MaterialIdentity);
        var ray=new RayGiRay(new(0,0,2),-Vector3.UnitZ,0,10);
        Assert.Equal(identity.Bvh.TraceAny(ray),scene.Bvh.TraceAny(ray));
        var noMap=new OriginalStyleScene(source.Snapshot.WithMaterials([source.Snapshot.Materials[0] with {DiffuseTexture=-1}]),
            style.Materials,style.Profile);
        Assert.True(RayGiScene.FromOriginal(noMap).DiffuseTextureFactors.IsEmpty);
    }

    [Fact]
    public void MaterialOrderedSideRowsNeverChangeRawVisibilityOrMaterialBytes()
    {
        var original=D3D12GiTextureFactorTests.Fixture();
        var changed=D3D12GiTextureFactorTests.WithFactors(original,0,OriginalMaterialTextureFactors.Identity with {
            Diffuse=new(new(.5f,.75f,1,0),new(0,0,0,0))});
        changed=D3D12GiTextureFactorTests.WithFactors(changed,1,OriginalMaterialTextureFactors.Identity with {
            Diffuse=new(new(2,1,.5f,.25f),new(.1f,.2f,.3f,.75f))});
        var before=RayGiScene.FromOriginal(original);var after=RayGiScene.FromOriginal(changed);
        Assert.Equal(2,after.DiffuseTextureFactors.Length);
        Assert.Equal(new Vector4(.5f,.75f,1,0),after.DiffuseTextureFactors[0].Multiply);
        Assert.Equal(new Vector4(.1f,.2f,.3f,.75f),after.DiffuseTextureFactors[1].Add);
        Assert.Equal(before.Materials.ToArray(),after.Materials.ToArray());
        Assert.Equal(before.Texels.ToArray(),after.Texels.ToArray());
        for(uint triangle=0;triangle<before.Triangles.Length;triangle++)
            foreach(var direction in new[]{Vector3.UnitZ,-Vector3.UnitZ})
                Assert.Equal(before.AcceptCandidate(triangle,new(.2f,.3f),direction),after.AcceptCandidate(triangle,new(.2f,.3f),direction));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VariantChangesOnlyAlbedoWhileAcceptedIdentityAndVisibilitySourceStayIntact(bool hardware)
    {
        string original=D3D12RayGiShaders.Source(hardware);
        Assert.Equal(original,D3D12RayGiShaders.Source(hardware,false));
        string morph=D3D12RayGiShaders.Source(hardware,true);
        string Visibility(string source)
        {
            int start=source.IndexOf("bool AcceptCandidate(",StringComparison.Ordinal);
            int end=source.IndexOf("float3 GiGeometricNormal(",start,StringComparison.Ordinal);
            return source[start..end];
        }
        Assert.Equal(Visibility(original),Visibility(morph));
        Assert.EndsWith(D3D12RayGiShaders.Integrator,morph);
        Assert.Contains("GiDiffuseTextureFactors:register(t8)",morph);
        Assert.DoesNotContain("GiDiffuseTextureFactors:register(t8)",original);
        Assert.Contains("precise float3 effective=weighted+(1-weight)",morph);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void InvalidConsumedFactorDomainsRejectButOffPreservesAuthoredData(int invalid)
    {
        var source=D3D12GiTextureFactorTests.Fixture();
        var value=OriginalMaterialTextureFactors.Identity;
        value=invalid switch {
            0=>value with {Diffuse=new(new(1,1,1,float.MaxValue),new(0,0,0,float.MaxValue))},
            1=>value with {Diffuse=new(new(float.MaxValue,float.MaxValue,float.MaxValue,1),new(float.MaxValue,float.MaxValue,float.MaxValue,0))},
            2=>value with {Diffuse=new(Vector4.One,new(-2,-2,-2,0))},
            _=>value with {Toon=new(new(8,8,8,0),Vector4.Zero)} };
        var frame=D3D12GiTextureFactorTests.WithFactors(source,0,value);
        D3D12RayGiRenderer.Validate(frame,RayGiOptions.Off);
        Assert.Throws<NotSupportedException>(()=>D3D12RayGiRenderer.Validate(frame,D3D12GiTextureFactorTests.Options()));
        if(invalid>=2)
            D3D12RayGiRenderer.Validate(frame,D3D12GiTextureFactorTests.Options() with {Composition=RayGiComposition.Additive});
    }

    [Fact]
    public void NativeDiffuseSphereOverflowRejectsIndependentlyOfAZeroHostEg()
    {
        var source=D3D12GiTextureFactorTests.Fixture(OriginalSphereMode.Multiply);
        var materials=source.Surface.Snapshot.Materials.ToArray();
        materials[0]=materials[0] with {Diffuse=new(1e38f,.6f,.6f,1),DiffuseTexture=-1,Ambient=Vector3.Zero};
        var bindings=source.Surface.Materials.ToArray();
        bindings[0]=bindings[0] with {TextureFactors=OriginalMaterialTextureFactors.Identity with {
            Sphere=new(new(4,1,1,1),Vector4.Zero)}};
        var profile=source.Surface.Profile with {LightAmbient=Vector3.Zero};
        var mesh=source.Surface.Snapshot;var textures=mesh.Textures.ToArray();
        textures[2]=new("white sphere",1,1,[255,255,255,255]);
        var surface=new OriginalStyleScene(new(mesh.Vertices,mesh.Indices,materials,textures,
            mesh.World,mesh.Camera,mesh.LightDirection),bindings,profile);
        var light=new NativeSceneLight(Guid.NewGuid(),"native overflow control",SceneLightKind.Directional,
            Vector3.Zero,Quaternion.Identity,Vector3.One,1,CastShadows:false);
        var native=new OriginalFrameScene(surface,options:source.Options,materials:source.Materials,lighting:new([light]));
        var noNative=new OriginalFrameScene(surface,options:source.Options,materials:source.Materials);
        Assert.Equal(Vector4.UnitW,OriginalStyleMath.ResolveHostColors(materials[0],profile).EgColor);
        var options=D3D12GiTextureFactorTests.Options();
        D3D12RayGiRenderer.Validate(noNative,options);
        D3D12RayGiRenderer.Validate(native,RayGiOptions.Off);
        Assert.Throws<NotSupportedException>(()=>D3D12RayGiRenderer.Validate(native,options));
        var finite=D3D12GiTextureFactorTests.WithFactors(native,0,OriginalMaterialTextureFactors.Identity with {
            Sphere=new(new(2,1,1,1),Vector4.Zero)});
        D3D12RayGiRenderer.Validate(finite,options);
        // The accepted all-identity domain is not narrowed by the new nonidentity check.
        D3D12RayGiRenderer.Validate(D3D12GiTextureFactorTests.WithFactors(native,0,OriginalMaterialTextureFactors.Identity),options);
    }
}
