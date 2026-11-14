using System.Numerics;
using System.Reflection;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class UnchangedDeformationStorageTests
{
    private delegate SceneSnapshot? Reuse(SceneSnapshot previous,ReadOnlySpan<SceneVertex> vertices);
    [Fact]
    public void ExactStorageReuseRetainsNewMetadataAndDistinguishesSignedZero()
    {
        var source=Mesh();var values=source.Vertices.ToArray();
        var previous=source.WithDeformedVertices(values);
        var metadata=source.WithMaterials(source.Materials.Select(m=>m with {Diffuse=new(.5f)}));
        var reuse=typeof(SceneSnapshot).GetMethod("ReuseUnchangedDeformation",BindingFlags.Instance|BindingFlags.NonPublic)!.CreateDelegate<Reuse>(metadata);
        var same=Assert.IsType<SceneSnapshot>(reuse(previous,values));
        Assert.Same(previous.VertexRevision,same.VertexRevision);
        Assert.Same(metadata.Materials,same.Materials);
        Assert.Equal(Vector4.One,previous.Materials[0].Diffuse);
        Assert.True(same.HasDynamicVertices);
        values[0]=values[0] with {Uv=new(BitConverter.Int32BitsToSingle(int.MinValue),0)};
        Assert.Null(reuse(previous,values));
        values[0]=values[0] with {Uv=Vector2.Zero,Normal=Vector3.UnitX};
        Assert.Null(reuse(previous,values));
        Assert.Throws<ArgumentException>(()=>reuse(previous,values.AsSpan(0,2)));
    }

    [Fact]
    public void MaterialOnlyOutlineUpdateSharesGeometryAndKeepsEveryMaterialBit()
    {
        var scene=Mesh();
        var data=new OriginalOutlineData(scene.Vertices.ToArray().Select(v=>new OriginalOutlineVertex(v.Position,v.Normal,
            OriginalOutlineVertexMode.Expand,1,0)),[new(0,Vector4.Zero)]);
        float negativeZero=BitConverter.Int32BitsToSingle(int.MinValue);
        var update=typeof(OriginalOutlineData).GetMethod("WithMaterials",BindingFlags.Instance|BindingFlags.NonPublic)!
            .CreateDelegate<Func<IReadOnlyList<OriginalOutlineMaterial>,OriginalOutlineData>>(data);
        var changed=update([new(negativeZero,new(negativeZero,0,0,0))]);
        Assert.Same(data.Vertices,changed.Vertices);
        Assert.Equal(int.MinValue,BitConverter.SingleToInt32Bits(changed.Materials[0].Width));
        Assert.Equal(int.MinValue,BitConverter.SingleToInt32Bits(changed.Materials[0].Color.X));
        Assert.Equal(0,BitConverter.SingleToInt32Bits(data.Materials[0].Width));
        Assert.Throws<ArgumentException>(()=>update([]));
        Assert.Throws<ArgumentException>(()=>update([new(0,new(float.NaN))]));
    }

    private static SceneSnapshot Mesh()=>new(
        [new(Vector3.Zero,Vector3.UnitZ,Vector2.Zero),new(Vector3.UnitX,Vector3.UnitZ,Vector2.UnitX),
            new(Vector3.UnitY,Vector3.UnitZ,Vector2.UnitY)],
        [0,1,2],[new("surface",0,3,Vector4.One,Vector3.Zero,Vector3.Zero,1)]);
}
