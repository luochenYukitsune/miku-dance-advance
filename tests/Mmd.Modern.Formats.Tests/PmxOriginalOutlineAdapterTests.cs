using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class PmxOriginalOutlineAdapterTests
{
    [Fact]
    public void ActualPmxFieldsMapRawVertexScaleAndPerMaterialEdgeFlagColorWidth()
    {
        var model=Model([Material(2,false,3),Material(1,true,3),Material(1,true,3)]);
        var result=PmxOriginalOutlineAdapter.FromRestPose(model);
        Assert.Equal(new[]{1,2,1,1},result.Vertices.Select(v=>v.SelectedMaterialIndex));
        Assert.Equal(new[]{0f,.5f,2f,1f},result.Vertices.Select(v=>v.VertexScale));
        Assert.All(result.Vertices,v=>Assert.Equal(OriginalOutlineVertexMode.Expand,v.Mode));
        Assert.Equal(new Vector3(0,2,0),result.Vertices[0].Normal);
        Assert.Equal(Vector3.Zero,result.Vertices[1].Normal);
        Assert.False(result.Materials[0].DrawEnabled); Assert.True(result.Materials[1].DrawEnabled);
        Assert.Equal(new Vector4(.1f,.2f,.3f,0),result.Materials[1].Color);
        Assert.Equal(2,result.Materials[0].Width);
    }

    [Fact]
    public void MalformedMaterialPartitionAndInvalidFaceFailExplicitly()
    {
        Assert.Throws<ArgumentException>(()=>PmxOriginalOutlineAdapter.FromRestPose(Model([Material(1,true,6)])));
        Assert.Throws<ArgumentException>(()=>PmxOriginalOutlineAdapter.FromRestPose(Model([Material(1,true,9)],[new(0,1,9),new(0,2,3),new(0,3,1)])));
    }

    private static PmxMaterial Material(float width,bool edge,int count)=>new() {
        Name="edge",EnglishName="edge",Diffuse=Vector4.One,Specular=Vector3.Zero,SpecularPower=1,Ambient=Vector3.Zero,
        DrawMode=edge?PmxDrawModeFlags.DrawEdge:PmxDrawModeFlags.GroundShadow,EdgeColor=new(.1f,.2f,.3f,0),EdgeSize=width,
        TextureIndex=-1,SphereTextureIndex=-1,SphereMode=PmxSphereMode.None,ToonMode=PmxToonMode.Common,ToonTextureIndex=0,Memo="",FaceVertexCount=count };
    private static PmxModel Model(IReadOnlyList<PmxMaterial> materials,IReadOnlyList<PmxTriangle>? faces=null)
    {
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"Fixtures","rich-w1-uv4.pmx"));
        var source=PmxReader.Read(input);
        var vertices=new List<PmxVertex>();
        foreach(float scale in new[]{0f,.5f,2f,1f}) vertices.Add(new() {Position=new(vertices.Count,0,0),Normal=vertices.Count==1?Vector3.Zero:new(0,2,0),Uv=Vector2.Zero,
            AdditionalUvs=[],WeightType=PmxVertexWeightType.Bdef1,BoneIndices=[0,-1,-1,-1],BoneWeights=[1,0,0,0],EdgeScale=scale});
        return new() {Header=source.Header,Name=source.Name,EnglishName=source.EnglishName,Comment=source.Comment,EnglishComment=source.EnglishComment,
            Vertices=vertices,Faces=faces??[new(0,1,2),new(0,2,3),new(0,3,1)],Materials=materials,Textures=[],Bones=source.Bones,Morphs=source.Morphs,
            DisplayFrames=source.DisplayFrames,RigidBodies=source.RigidBodies,Joints=source.Joints,SoftBodies=source.SoftBodies};
    }
}
