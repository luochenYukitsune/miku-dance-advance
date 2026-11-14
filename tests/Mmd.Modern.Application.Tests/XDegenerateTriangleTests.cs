using System.Numerics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class XDegenerateTriangleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoincidentTriangleCornersRetainOrderAttributesAndMaterials(bool suppliedNormals)
    {
        // Self-authored control-mesh fixture: distinct indexed corners coincide in
        // position, but retain separate UVs, indexed normals and face materials.
        const string normals = "MeshNormals {2;0;1;0;,1;0;0;;2;3;0,1,0;,3;1,0,1;;}";
        var document = XAccessoryTests.Read($$"""
            xof 0303txt 0032
            Mesh control {
              4;2;3;4;,2;3;4;,2;3;4;,2;3;4;;
              2;3;0,1,2;,3;1,0,3;;
              MeshTextureCoords {4;0;0;,1;0;,0;1;,1;1;;}
              {{(suppliedNormals ? normals : "")}}
              MeshMaterialList {2;2;0,1;;
                Material first {0;0;0;0;;5;0;0;0;;0;0;0;;}
                Material second {0.2;0.3;0.4;1;;8;0;0;0;;0;0;0;;}
              }
            }
            """);

        Assert.Equal(6, document.Vertices.Count);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, document.Indices);
        Assert.All(document.Vertices, vertex => Assert.Equal(new Vector3(2, 3, 4), vertex.Position));
        Assert.Equal(new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY, Vector2.UnitX, Vector2.Zero, Vector2.One },
            document.Vertices.Select(vertex => vertex.Uv));
        Assert.Equal(suppliedNormals
                ? new[] { Vector3.UnitY, Vector3.UnitX, Vector3.UnitY, Vector3.UnitX, Vector3.UnitY, Vector3.UnitX }
                : Enumerable.Repeat(Vector3.Zero, 6),
            document.Vertices.Select(vertex => vertex.Normal));
        Assert.Collection(document.Materials,
            range => { Assert.Equal(0, range.StartIndex); Assert.Equal(3, range.IndexCount); Assert.Equal("first", range.Material.Name);
                Assert.Equal(Vector4.Zero, range.Material.Diffuse); Assert.Equal(5f, range.Material.SpecularPower); },
            range => { Assert.Equal(3, range.StartIndex); Assert.Equal(3, range.IndexCount); Assert.Equal("second", range.Material.Name);
                Assert.Equal(new Vector4(.2f, .3f, .4f, 1), range.Material.Diffuse); Assert.Equal(8f, range.Material.SpecularPower); });
    }
}
