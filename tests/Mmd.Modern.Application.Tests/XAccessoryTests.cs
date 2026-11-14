using System.Numerics;
using System.Text;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Application.MotionEditing;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class XAccessoryTests
{
    internal const string Triangle = """
        xof 0303txt 0032
        Frame root {
          FrameTransformMatrix { 1,0,0,0, 0,1,0,0, 0,0,1,0, 2,3,4,1;; }
          Mesh mesh {
            3; 0;0;0;, 1;0;0;, 0;1;0;;
            1; 3;0,1,2;;
            MeshNormals { 1; 0;0;1;; 1; 3;0,0,0;; }
            MeshTextureCoords { 3; 0;0;,1;0;,0;1;; }
            MeshMaterialList { 1; 1; 0;;
              Material mat { 1;0.5;0.25;0.8;; 8; 0.1;0.2;0.3;; 0;0;0;; TextureFilename { "image.png"; } }
            }
          }
        }
        """;
    internal static XTextDocument Read(string text) => XTextReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(text)), new() { AnsiCodePage = 65001 });
    [Fact]
    public void NestedFrameMaterialAndTextureReachNeutralSceneWithoutCoordinateFlips()
    {
        var document = Read(Triangle);
        var texture = new SceneTexture("image", 1, 1, new byte[] { 255, 255, 255, 255 });
        var result = XAccessoryAdapter.FromDocument(document, path => { Assert.Equal("image.png", path); return texture; });
        var snapshot = result.Frame.Surface.Snapshot;
        Assert.Equal(new Vector3(2, 3, 4), snapshot.Vertices[0].Position);
        Assert.Equal(Vector3.UnitZ, snapshot.Vertices[0].Normal);
        Assert.Equal(Vector2.UnitX, snapshot.Vertices[1].Uv);
        Assert.Equal(new Vector4(1, .5f, .25f, .8f), Assert.Single(snapshot.Materials).Diffuse);
        Assert.Same(texture, Assert.Single(snapshot.Textures)); Assert.NotNull(result.Frame.Outline);
    }
    [Fact]
    public void QuadTriangulatesAndSeparateNormalIndicesSplitVertices()
    {
        var document = Read("""
            xof 0303txt 0032
            Mesh quad { 4; 0;0;0;,1;0;0;,1;1;0;,0;1;0;; 1;4;0,1,2,3;; }
            """);
        var frame = XAccessoryAdapter.FromDocument(document).Frame;
        Assert.Equal(6, frame.Surface.Snapshot.Indices.Length);
        Assert.All(frame.Surface.Snapshot.Vertices.ToArray(), vertex => Assert.Equal(Vector3.UnitZ, vertex.Normal));
    }
    [Theory]
    [InlineData("xof 0303txt 0016")]
    public void UnsupportedStorageRejectsExplicitly(string header)
        => Assert.Throws<NotSupportedException>(() => Read(header));
    [Fact]
    public void InvalidIndicesTruncationAndInputLimitReject()
    {
        Assert.Throws<InvalidDataException>(() => Read(Triangle.Replace("3;0,1,2", "3;0,1,9")));
        Assert.Throws<InvalidDataException>(() => Read(Triangle[..^5]));
        Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(Triangle)), new() { MaxInputBytes = 32 }));
    }
    [Fact]
    public void AccessoryParentOpacityVisibilityAndSourceRemainSeparate()
    {
        var source = XAccessoryAdapter.FromDocument(Read(Triangle)).Frame;
        var pmm = new PmmDocument { Accessories = [new() { Index = 7, Scale = 2, Translation = Vector3.UnitX,
            OpacityAndVisibility = 101, ParentModelIndex = 3, ParentBoneIndex = 4, ShadowEnabled = true }] };
        var sample = PmmAccessoryScene.Sample(pmm, 7, source, 0, (_, _) => Matrix4x4.CreateTranslation(0, 10, 0), saved: true);
        Assert.True(sample.Visible); Assert.Equal(.5f, sample.Opacity); Assert.Equal(new Vector3(41, 70, 80),
            Vector3.Transform(source.Surface.Snapshot.Vertices[0].Position, sample.Frame.Surface.Snapshot.World));
        Assert.Equal(.4f, sample.Frame.Surface.Snapshot.Materials[0].Diffuse.W);
        Assert.Equal(.8f, source.Surface.Snapshot.Materials[0].Diffuse.W);
        Assert.Throws<InvalidDataException>(() => PmmAccessoryScene.Sample(pmm, 7, source, 0, (_, _) => null, saved: true));
    }
    [Fact]
    public void HiddenAccessoryPublishesEmptyGeometry()
    {
        var source = XAccessoryAdapter.FromDocument(Read(Triangle)).Frame;
        var pmm = new PmmDocument { Accessories = [new() { Index = 1, Scale = 1, OpacityAndVisibility = 0, ParentModelIndex = -1 }] };
        var sample = PmmAccessoryScene.Sample(pmm, 1, source, 0, saved: true);
        Assert.False(sample.Visible); Assert.Empty(sample.Frame.Surface.Snapshot.Vertices.ToArray());
    }
    [Fact]
    public void NamedMaterialReferenceAndNestedChildMatrixResolveInFileCoordinates()
    {
        var document = Read("""
            xof 0303txt 0032
            Material shared { 0.2;0.3;0.4;1;;0;0;0;0;;0;0;0;; }
            Frame outer { FrameTransformMatrix { 1,0,0,0,0,1,0,0,0,0,1,0,10,0,0,1;; }
              Frame inner { FrameTransformMatrix { 1,0,0,0,0,1,0,0,0,0,1,0,0,5,0,1;; }
                Mesh { 3;0;0;0;,1;0;0;,0;1;0;;1;3;0,1,2;; MeshMaterialList {1;1;0;;{shared}} }
              }
            }
            """);
        Assert.Equal(new Vector3(10, 5, 0), document.Vertices[0].Position);
        Assert.Equal(new Vector4(.2f, .3f, .4f, 1), Assert.Single(document.Materials).Material.Diffuse);
    }
    [Fact]
    public void IndexedNormalSeamsArePreservedAndInvalidNormalTopologyRejects()
    {
        const string text = """
            xof 0303txt 0032
            Mesh { 4;0;0;0;,1;0;0;,0;1;0;,0;0;1;; 2;3;0,1,2;,3;0,3,1;;
              MeshNormals {2;0;0;1;,0;1;0;;2;3;0,0,0;,3;1,1,1;;}
            }
            """;
        var document = Read(text);
        Assert.Equal(Vector3.UnitZ, document.Vertices[0].Normal);
        Assert.Equal(Vector3.UnitY, document.Vertices[3].Normal);
        Assert.Throws<InvalidDataException>(() => Read(text.Replace("3;1,1,1", "3;1,1,9")));
    }
    [Fact]
    public void ConcavePolygonsTriangulateAndNonAffineInputsReject()
    {
        Assert.Equal(9, Read("""
            xof 0303txt 0032
            Mesh { 5;0;0;0;,2;0;0;,1;0.5;0;,2;2;0;,0;2;0;;1;5;0,1,2,3,4;;}
            """).Indices.Count);
        Assert.Throws<InvalidDataException>(() => Read(Triangle.Replace("2,3,4,1", "2,3,4,0")));
    }
    [Fact]
    public void NoncommutingAccessoryTransformMatchesOriginal932FunctionGolden()
    {
        var pose = new PmmAccessoryPose(new(2, -3, 4), new(.31f, -.47f, .63f), 1.7f, 1, true, true, 0, 0);
        // Original input supplies rest=(5,6,-7) and skin translation=(11,-2,7); these form bone world=(16,4,0).
        var matrix = PmmAccessoryScene.WorldTransform(pose, Matrix4x4.CreateTranslation(16, 4, 0));
        float[] expected = [10.863293647766113f,9.53805923461914f,8.945068359375f,0,
            -10.827254295349121f,13.08169937133789f,-.7998123168945312f,0,
            -7.332079887390137f,-5.1859965324401855f,14.434197425842285f,0,18,1,4,1];
        float[] actual = [matrix.M11,matrix.M12,matrix.M13,matrix.M14,matrix.M21,matrix.M22,matrix.M23,matrix.M24,
            matrix.M31,matrix.M32,matrix.M33,matrix.M34,matrix.M41,matrix.M42,matrix.M43,matrix.M44];
        for (int i = 0; i < expected.Length; i++) Assert.InRange(MathF.Abs(actual[i] - expected[i]), 0, 4e-6f);
    }
}
