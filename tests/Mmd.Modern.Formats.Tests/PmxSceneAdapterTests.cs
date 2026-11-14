using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class PmxSceneAdapterTests
{
    [Fact]
    public void RestPosePreservesGeometryUvMaterialRangesAndTextureDiagnostics()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rich-w1-uv4.pmx"));
        var model = PmxReader.Read(stream);
        var result = PmxSceneAdapter.FromRestPose(model);
        Assert.Equal(model.Vertices.Count, result.Snapshot.Vertices.Length);
        Assert.Equal(model.Faces.Count * 3, result.Snapshot.Indices.Length);
        Assert.Equal(model.Vertices[0].Uv, result.Snapshot.Vertices[0].Uv);
        Assert.Equal(model.Materials.Sum(m => m.FaceVertexCount), result.Snapshot.Materials.Sum(m => m.IndexCount));
        Assert.Contains(result.Diagnostics, d => d.Contains("rest pose", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics, d => d.Contains("not loaded", StringComparison.OrdinalIgnoreCase));
        var resolved = PmxSceneAdapter.FromRestPose(model, _ => new SceneTexture("resolved", 1, 1, [255, 255, 255, 255]));
        Assert.NotEmpty(resolved.Snapshot.Textures);
        Assert.Contains(resolved.Snapshot.Materials, m => m.DiffuseTexture >= 0);
        for (int i = 0; i < model.Materials.Count; i++)
            if (model.Materials[i].ToonMode == PmxToonMode.Separate && model.Materials[i].ToonTextureIndex >= 0)
                Assert.Equal(new System.Numerics.Vector4(0,-1,0,1), resolved.Snapshot.Materials[i].ToonUvTransform);
    }

    [Fact]
    public void RestPoseUsesUintIndicesAndRejectsInvalidFacesAndRanges()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rich-w1-uv4.pmx"));
        var source = PmxReader.Read(stream);
        var vertices = Enumerable.Repeat(source.Vertices[0], 65536).Concat(source.Vertices).ToArray();
        var faces = source.Faces.Select(f => new PmxTriangle(f.A+65536,f.B+65536,f.C+65536)).ToArray();
        var adapted = PmxSceneAdapter.FromRestPose(Copy(source, vertices, faces));
        Assert.Equal((uint)(source.Faces[0].A + 65536), adapted.Snapshot.Indices[0]);
        faces[0] = new(-1,0,1);
        Assert.Throws<ArgumentException>(() => PmxSceneAdapter.FromRestPose(Copy(source, vertices, faces)));
        faces[0] = new(vertices.Length,0,1);
        Assert.Throws<ArgumentException>(() => PmxSceneAdapter.FromRestPose(Copy(source, vertices, faces)));
        Assert.Throws<ArgumentException>(() => PmxSceneAdapter.FromRestPose(Copy(source, source.Vertices, source.Faces.Skip(1).ToArray())));
    }

    private static PmxModel Copy(PmxModel source, IReadOnlyList<PmxVertex> vertices, IReadOnlyList<PmxTriangle> faces) => new()
    {
        Header = source.Header, Name = source.Name, EnglishName = source.EnglishName, Comment = source.Comment, EnglishComment = source.EnglishComment,
        Vertices = vertices, Faces = faces, Materials = source.Materials, Textures = source.Textures, Bones = source.Bones, Morphs = source.Morphs,
        DisplayFrames = source.DisplayFrames, RigidBodies = source.RigidBodies, Joints = source.Joints, SoftBodies = source.SoftBodies,
    };
}
