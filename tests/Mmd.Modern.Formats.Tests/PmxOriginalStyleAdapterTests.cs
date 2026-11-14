using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class PmxOriginalStyleAdapterTests
{
    [Theory]
    [InlineData(PmxSphereMode.Multiply,OriginalSphereMode.Multiply)]
    [InlineData(PmxSphereMode.Add,OriginalSphereMode.Add)]
    public void MapsMaterialFieldsSphereAndCpuToonWithoutProceduralRamp(PmxSphereMode input,OriginalSphereMode output)
    {
        var model=Model(input,PmxToonMode.Separate);
        var calls=new List<string>();
        var result=PmxOriginalStyleAdapter.FromRestPose(model,relative => {
            calls.Add(relative);
            return new(relative,1,2,[1,2,3,255, 255,128,64,0]);
        });
        var scene=result.Scene;
        Assert.Equal(new Vector3(255/256f,.5f,.25f),scene.Materials[0].ToonShadowColor);
        Assert.Equal(output,scene.Materials[0].SphereMode);
        Assert.True(scene.Materials[0].DoubleSided);
        Assert.Equal(model.Materials[0].Diffuse,scene.Snapshot.Materials[0].Diffuse);
        Assert.Equal(model.Materials[0].Ambient,scene.Snapshot.Materials[0].Ambient);
        Assert.Equal(model.Materials[0].Specular,scene.Snapshot.Materials[0].Specular);
        Assert.Equal(0,scene.Snapshot.Materials[0].Shininess);
        Assert.Equal(-1,scene.Snapshot.Materials[0].ToonTexture);
        Assert.Equal(2,scene.Snapshot.Textures.Count);
        Assert.Equal(3,calls.Count);
        Assert.All(scene.Snapshot.Textures,t=>Assert.Equal(SceneTextureKind.Image,t.Kind));
    }

    [Fact]
    public void CommonToonIsOptionalExplicitReferenceAndFallbackIsWhiteWithDiagnostic()
    {
        var model=Model(PmxSphereMode.Multiply,PmxToonMode.Common);
        var noAssets=PmxOriginalStyleAdapter.FromRestPose(model);
        Assert.Equal(Vector3.One,noAssets.Scene.Materials[0].ToonShadowColor);
        Assert.Contains(noAssets.Diagnostics,d=>d.Contains("white",StringComparison.OrdinalIgnoreCase));
        int common=-1;
        var assets=PmxOriginalStyleAdapter.FromRestPose(model,loadCommonToon:i=>{
            common=i;return new("user common",1,1,[64,128,192,255]);
        });
        Assert.Equal(2,common);
        Assert.Equal(new Vector3(.25f,.5f,.75f),assets.Scene.Materials[0].ToonShadowColor);
        Assert.Equal(OriginalSphereMode.None,assets.Scene.Materials[0].SphereMode);
    }

    [Fact]
    public void ClassicOptInRetainsTheEntireAuthoredToonImageAndModernDefaultStaysUnchanged()
    {
        var model=Model(PmxSphereMode.Multiply,PmxToonMode.Separate);
        var image=new SceneTexture("toon.bmp",1,3,[10,20,30,255, 40,50,60,128, 70,80,90,0]);
        SceneTexture? Load(string name)=>name=="toon.bmp"?image:null;
        var modern=PmxOriginalStyleAdapter.FromRestPose(model,Load).Scene;
        Assert.Equal(-1,modern.Snapshot.Materials[0].ToonTexture);
        Assert.Empty(modern.Snapshot.Textures);
        var classic=PmxOriginalStyleAdapter.FromRestPose(model,Load,retainToonTextures:true).Scene;
        Assert.Same(image,classic.Snapshot.Textures[classic.Snapshot.Materials[0].ToonTexture]);
        Assert.Equal(new byte[]{10,20,30,255,40,50,60,128,70,80,90,0},classic.Snapshot.Textures[0].Rgba.ToArray());
        Assert.Equal(new Vector3(70/256f,80/256f,90/256f),classic.Materials[0].ToonShadowColor);
    }

    [Fact]
    public void ClassicCommonToonRequiresAndRetainsAnExplicitImageProvider()
    {
        var model=Model(PmxSphereMode.None,PmxToonMode.Common);
        var failure=Assert.Throws<NotSupportedException>(()=>PmxOriginalStyleAdapter.FromRestPose(model,retainToonTextures:true));
        Assert.Contains("common toon 03",failure.Message,StringComparison.Ordinal);
        var image=new SceneTexture("caller common 03",1,2,[1,2,3,255,4,5,6,255]);
        var result=PmxOriginalStyleAdapter.FromRestPose(model,loadCommonToon:index=>index==2?image:null,retainToonTextures:true);
        Assert.Same(image,result.Scene.Snapshot.Textures[result.Scene.Snapshot.Materials[0].ToonTexture]);
        Assert.Throws<NotSupportedException>(()=>PmxOriginalStyleAdapter.FromRestPose(model,
            loadCommonToon:_=>SceneTexture.ProceduralToon(),retainToonTextures:true));
    }

    [Fact]
    public void AdditionalUvSphereModeBindsIndependentBaselineAndExplicitInitialState()
    {
        var model=Model(PmxSphereMode.SubTexture,PmxToonMode.Common);
        var result=PmxOriginalStyleAdapter.FromRestPose(model,_=>new("map",1,1,[255,255,255,255]),initialSubTextureAdditive:true);
        Assert.Equal(OriginalSphereMode.SubTexture,result.Scene.Materials[0].SphereMode);
        Assert.True(result.Scene.Materials[0].SubTextureAdditive);
        Assert.Equal(model.Header.AdditionalUvCount,result.Scene.Snapshot.AdditionalUvs!.ChannelCount);
        Assert.Equal(model.Vertices[0].AdditionalUvs[0],result.Scene.Snapshot.AdditionalUvs.Vertices[0].Uv1);
        Assert.Equal(model.Vertices[0].AdditionalUvs[3],result.Scene.Snapshot.AdditionalUvs.Vertices[0].Uv4);
        Assert.Contains(result.Diagnostics,d=>d.Contains("cross-model",StringComparison.Ordinal));
        var missing=PmxOriginalStyleAdapter.FromRestPose(model);
        Assert.Equal(OriginalSphereMode.None,missing.Scene.Materials[0].SphereMode);
    }

    [Theory,InlineData(PmxSphereMode.Multiply,false),InlineData(PmxSphereMode.Add,true)]
    public void ResolvedPrecedingSphereMaterialControlsSubTextureState(PmxSphereMode preceding,bool expected)
    {
        var source=Model(PmxSphereMode.SubTexture,PmxToonMode.Common);
        var m=source.Materials[0];
        PmxMaterial Material(PmxSphereMode mode)=>new() {
            Name=m.Name,EnglishName=m.EnglishName,Diffuse=m.Diffuse,Specular=m.Specular,SpecularPower=m.SpecularPower,
            Ambient=m.Ambient,DrawMode=m.DrawMode,EdgeColor=m.EdgeColor,EdgeSize=m.EdgeSize,TextureIndex=m.TextureIndex,
            SphereTextureIndex=m.SphereTextureIndex,SphereMode=mode,ToonMode=m.ToonMode,ToonTextureIndex=m.ToonTextureIndex,
            Memo=m.Memo,FaceVertexCount=m.FaceVertexCount };
        var model=new PmxModel {Header=source.Header,Name=source.Name,EnglishName=source.EnglishName,Comment=source.Comment,EnglishComment=source.EnglishComment,
            Vertices=source.Vertices,Faces=source.Faces.Concat(source.Faces).ToArray(),Materials=[Material(preceding),Material(PmxSphereMode.SubTexture)],Textures=source.Textures,
            Bones=source.Bones,Morphs=source.Morphs,DisplayFrames=source.DisplayFrames,RigidBodies=source.RigidBodies,Joints=source.Joints,SoftBodies=source.SoftBodies};
        var adapted=PmxOriginalStyleAdapter.FromRestPose(model,_=>new("map",1,1,[255,255,255,255]),initialSubTextureAdditive:!expected);
        Assert.Equal(expected,adapted.Scene.Materials[1].SubTextureAdditive);
    }

    private static PmxModel Model(PmxSphereMode sphere,PmxToonMode toon)
    {
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"Fixtures","rich-w1-uv4.pmx"));
        var source=PmxReader.Read(input);
        var material=new PmxMaterial {
            Name="surface",EnglishName="surface",Diffuse=new(.6f,.3f,.2f,.8f),Specular=new(.4f,.2f,.1f),SpecularPower=0,
            Ambient=new(.1f,.2f,.3f),DrawMode=PmxDrawModeFlags.BothFaces,EdgeColor=Vector4.Zero,EdgeSize=1,
            TextureIndex=0,SphereTextureIndex=1,SphereMode=sphere,ToonMode=toon,ToonTextureIndex=2,Memo="",FaceVertexCount=source.Faces.Count*3
        };
        return new() {Header=source.Header,Name=source.Name,EnglishName=source.EnglishName,Comment=source.Comment,EnglishComment=source.EnglishComment,
            Vertices=source.Vertices,Faces=source.Faces,Materials=[material],Textures=["diffuse.png","sphere.png","toon.bmp"],
            Bones=source.Bones,Morphs=source.Morphs,DisplayFrames=source.DisplayFrames,RigidBodies=source.RigidBodies,Joints=source.Joints,SoftBodies=source.SoftBodies};
    }
}
