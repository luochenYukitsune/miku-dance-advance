using System.Numerics;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class XTopologyTests
{
    private const string Concave = """
        xof 0303txt 0032
        Mesh notch {5;0;0;0;,2;0;0;,1;0.5;0;,2;2;0;,0;2;0;;1;5;0,1,2,3,4;;}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcaveSimplePolygonPreservesWindingAndArea(bool reverse)
    {
        var document = XAccessoryTests.Read(reverse ? Concave.Replace("0,1,2,3,4", "4,3,2,1,0") : Concave);
        Assert.Equal(9, document.Vertices.Count);
        double area = 0;
        for (int i = 0; i < document.Vertices.Count; i += 3)
        {
            Vector3 a = document.Vertices[i].Position, b = document.Vertices[i + 1].Position, c = document.Vertices[i + 2].Position;
            float signed = Vector3.Cross(b - a, c - a).Z;
            Assert.True(reverse ? signed < 0 : signed > 0);
            area += signed / 2;
        }
        Assert.Equal(reverse ? -3d : 3d, area, 6);
    }

    [Fact]
    public void ProjectionHandlesConcavePolygonOutsideXyPlane()
    {
        var document = XAccessoryTests.Read(Concave.Replace("2;0;0", "0;2;0").Replace("1;0.5;0", "0;1;0.5")
            .Replace("2;2;0", "0;2;2").Replace("0;2;0;;", "0;0;2;;"));
        Assert.Equal(9, document.Vertices.Count);
        Assert.All(document.Vertices, vertex => Assert.Equal(Vector3.UnitX, vertex.Normal));
    }

    [Fact]
    public void LargeExtentDoesNotEraseSmallConcaveNotchNearOrigin()
    {
        var document = XAccessoryTests.Read("""
            xof 0303txt 0032
            Mesh {7;0;0;0;,1000000000;0;0;,1000000000;1000000000;0;,0;1000000000;0;,0;2;0;,1;1;0;,0;1;0;;1;7;0,1,2,3,4,5,6;;}
            """);
        Assert.Equal(15, document.Vertices.Count);
        decimal exactArea = 0;
        var evidence = new List<object>();
        for (int i = 0; i < document.Vertices.Count; i += 3)
        {
            Vector3 a = document.Vertices[i].Position, b = document.Vertices[i + 1].Position, c = document.Vertices[i + 2].Position;
            // Promote before subtraction: float(1e9 - 1) equals float(1e9),
            // which otherwise collapses the final valid triangle to zero area.
            double determinant = ((double)b.X - a.X) * ((double)c.Y - a.Y)
                - ((double)b.Y - a.Y) * ((double)c.X - a.X);
            decimal exactDeterminant = ((decimal)b.X - (decimal)a.X) * ((decimal)c.Y - (decimal)a.Y)
                - ((decimal)b.Y - (decimal)a.Y) * ((decimal)c.X - (decimal)a.X);
            evidence.Add(new { A = new[] { a.X, a.Y }, B = new[] { b.X, b.Y }, C = new[] { c.X, c.Y },
                DoubleDeterminant = determinant, ExactDeterminant = exactDeterminant });
            Assert.True(determinant > 0, $"Triangle {i / 3} has determinant {determinant:R}: {a}, {b}, {c}.");
            Assert.True(exactDeterminant > 0);
            exactArea += exactDeterminant / 2;
        }
        // Decimal retains the half-unit notch that double(1e18 - 0.5) cannot represent.
        Assert.Equal(999_999_999_999_999_999.5m, exactArea);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Triangles = evidence, ExactArea = exactArea }));
    }

    [Fact]
    public void SparseVertexColorsRemainAlignedAfterTriangleExpansion()
    {
        var document = XAccessoryTests.Read(Concave.Replace("1;5;0,1,2,3,4;;}", """
            1;5;0,1,2,3,4;;
            MeshVertexColors {2;0;1;0;0;0.5;;,2;0;0.25;1;1;;;}
            }
            """));
        var colors = document.VertexColors;
        Assert.Equal(document.Vertices.Count, colors.Count);
        for (int i = 0; i < colors.Count; i++)
            Assert.Equal(document.Vertices[i].Position switch
            {
                { X: 0, Y: 0 } => new Vector4(1, 0, 0, .5f),
                { X: 1, Y: .5f } => new Vector4(0, .25f, 1, 1),
                _ => Vector4.One
            }, colors[i]);
        Assert.DoesNotContain(document.Diagnostics, item => item.Contains("Ignored .x mesh block: MeshVertexColors", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2;0;1;0;0;1;;,0;0;1;0;1;;;")]
    [InlineData("1;5;1;0;0;1;;;")]
    [InlineData("1;0;NaN;0;0;1;;;")]
    public void MalformedVertexColorsReject(string colors)
        => Assert.Throws<InvalidDataException>(() => XAccessoryTests.Read(Concave.Replace("1;5;0,1,2,3,4;;}", "1;5;0,1,2,3,4;;MeshVertexColors {" + colors + "}}")));

    [Fact]
    public void SelfIntersectingPolygonRejects()
        => Assert.Throws<InvalidDataException>(() => XAccessoryTests.Read("""
            xof 0303txt 0032
            Mesh {5;0;0;0;,2;2;0;,0;2;0;,2;0;0;,3;1;0;;1;5;0,1,2,3,4;;}
            """));

    [Fact]
    public void NonPlanarPolygonStillRejectsExplicitly()
        => Assert.Throws<NotSupportedException>(() => XAccessoryTests.Read(Concave.Replace("1;0.5;0", "1;0.5;0.2")));

    [Fact]
    public void ConcaveExpansionStillHonorsGlobalBudget()
        => Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Concave)),
            new() { AnsiCodePage = 65001, MaxTriangleVertices = 6 }));

    [Fact]
    public void TriangulationWorkIsBoundedAcrossFaces()
        => Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Concave)),
            new() { AnsiCodePage = 65001, MaxTriangulationOperations = 1 }));

    [Fact]
    public void TextTokenLengthAndCountAreBounded()
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(XAccessoryTests.Triangle);
        Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(bytes), new() { MaxTokens = 3 }));
        Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(bytes), new() { MaxTokenCharacters = 3 }));
    }
}
