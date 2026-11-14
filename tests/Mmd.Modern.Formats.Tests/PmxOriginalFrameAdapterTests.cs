using System.Numerics;
using Mmd.Modern.Core;
using Xunit;
namespace Mmd.Modern.Formats.Tests;

public sealed class PmxOriginalFrameAdapterTests
{
    [Fact] public void GatesUseIndependentPmxFlagsEdgeAlphaAndExactPoint98()
    {
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"Fixtures","rich-w1-uv4.pmx"));var source=PmxReader.Read(input);
        PmxDrawModeFlags all=PmxDrawModeFlags.CastSelfShadow|PmxDrawModeFlags.ReceiveSelfShadow|PmxDrawModeFlags.GroundShadow;
        PmxMaterial M(PmxDrawModeFlags flags,float edgeAlpha,float diffuseAlpha=1)=>new() {
            Name="frame",EnglishName="frame",Diffuse=new(1,1,1,diffuseAlpha),Specular=Vector3.Zero,SpecularPower=1,Ambient=Vector3.Zero,
            DrawMode=flags,EdgeColor=new(0,0,0,edgeAlpha),EdgeSize=1,TextureIndex=-1,SphereTextureIndex=-1,SphereMode=PmxSphereMode.None,
            ToonMode=PmxToonMode.Common,ToonTextureIndex=0,Memo="",FaceVertexCount=0};
        var model=new PmxModel {Header=source.Header,Name=source.Name,EnglishName=source.EnglishName,Comment=source.Comment,EnglishComment=source.EnglishComment,
            Vertices=source.Vertices,Faces=source.Faces,Materials=[M(all|PmxDrawModeFlags.BothFaces,1),M(all,0),M(all|PmxDrawModeFlags.DrawEdge,1,.98f),M(all,1,MathF.BitIncrement(.98f))],Textures=source.Textures,
            Bones=source.Bones,Morphs=source.Morphs,DisplayFrames=source.DisplayFrames,RigidBodies=source.RigidBodies,Joints=source.Joints,SoftBodies=source.SoftBodies};
        var flags=PmxOriginalFrameAdapter.FromMaterials(model);
        Assert.Equal(new OriginalFrameMaterialFlags(true,true,true),flags[0]);
        Assert.Equal(new OriginalFrameMaterialFlags(false,true,false),flags[1]);
        Assert.Equal(new OriginalFrameMaterialFlags(false,true,true,ShadowDoubleSided:true),flags[2]);
        Assert.True(flags[3].CastSelfShadow);
        Assert.False(flags[0].ShadowDoubleSided); // BothFaces does not control caster culling.
    }
}
