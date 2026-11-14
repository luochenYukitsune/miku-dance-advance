using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class SpatialSamplingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void SamplesAreDistinctDeterministicAndExactlyCenteredInsidePixel(int count)
    {
        var points = new HashSet<Vector2>();
        var sum = Vector2.Zero;
        for (int i = 0; i < count; i++)
        {
            Vector2 offset = SpatialSampling.JitterPixels(count, i);
            Assert.Equal(offset, SpatialSampling.JitterPixels(count, i));
            Assert.InRange(offset.X, -.5f, .5f);
            Assert.InRange(offset.Y, -.5f, .5f);
            Assert.True(points.Add(offset));
            sum += offset;
        }
        Assert.Equal(count, points.Count);
        Assert.Equal(Vector2.Zero, sum);
        if (count == 1) Assert.Equal(Vector2.Zero, SpatialSampling.JitterPixels(1, 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(64)]
    [InlineData(int.MaxValue)]
    public void RejectsUnsupportedSampleCount(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SpatialSampling.JitterPixels(count, 0));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void RejectsSampleIndexOutsideThePattern(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpatialSampling.JitterPixels(count, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpatialSampling.JitterPixels(count, count));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void JitterMovesProjectionByPixelsWithoutChangingDepthOrCameraPose(bool leftHanded, bool perspective)
    {
        var camera = Camera(leftHanded, perspective);
        var offset = new Vector2(.375f, -.25f);
        var jittered = camera with { ProjectionJitterPixels = offset };
        Assert.Equal(camera.Eye, jittered.Eye);
        Assert.Equal(camera.Target, jittered.Target);
        Assert.Equal(camera.Up, jittered.Up);

        foreach ((int width, int height) in new[] { (640, 480), (1920, 1080), (721, 1031) })
        foreach (var point in new[] { new Vector3(1, 2, 0), new Vector3(-3, 1, 4), new Vector3(0, 0, -3) })
        {
            Vector4 before = Vector4.Transform(new Vector4(point, 1), camera.ViewProjection(width, height));
            Vector4 after = Vector4.Transform(new Vector4(point, 1), jittered.ViewProjection(width, height));
            Assert.InRange((after.X / after.W - before.X / before.W) * width / 2, offset.X - .00015f, offset.X + .00015f);
            Assert.InRange((before.Y / before.W - after.Y / after.W) * height / 2, offset.Y - .00015f, offset.Y + .00015f);
            Assert.Equal(before.Z, after.Z);
            Assert.Equal(before.W, after.W);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ZeroJitterPreservesHistoricalViewProjectionBits(bool leftHanded, bool perspective)
    {
        var camera = Camera(leftHanded, perspective);
        Assert.Equal(Vector2.Zero, camera.ProjectionJitterPixels);
        Matrix4x4 expected = HistoricalViewProjection(camera, 721, 1031);
        Assert.Equal(MatrixBytes(expected), MatrixBytes(camera.ViewProjection(721, 1031)));
        Assert.Equal(MatrixBytes(expected), MatrixBytes((camera with { ProjectionJitterPixels = new(-0f, 0f) }).ViewProjection(721, 1031)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithCameraSharesImmutableGeometryAndPreservesSceneState(bool dynamic)
    {
        var source = Triangle();
        if (dynamic) source = source.WithDeformedVertices(source.Vertices.ToArray());
        var replacement = source.Camera with { ProjectionJitterPixels = new(.125f, -.375f) };
        var changed = source.WithCamera(replacement);

        Assert.NotSame(source, changed);
        Assert.Same(replacement, changed.Camera);
        Assert.Equal(Vector2.Zero, source.Camera.ProjectionJitterPixels);
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(source.Vertices), ref MemoryMarshal.GetReference(changed.Vertices)));
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(source.Indices), ref MemoryMarshal.GetReference(changed.Indices)));
        Assert.Same(source.Materials, changed.Materials);
        Assert.Same(source.Textures, changed.Textures);
        Assert.Equal(source.World, changed.World);
        Assert.Equal(source.LightDirection, changed.LightDirection);
        Assert.Equal(dynamic, changed.HasDynamicVertices);
        Assert.Equal(source.Vertices.ToArray(), changed.Vertices.ToArray());
    }

    [Fact]
    public void WithCameraRejectsNull()
    {
        var source = Triangle();
        Assert.Throws<ArgumentNullException>(() => source.WithCamera(null!));
    }

    [Fact]
    public void AllCameraEntryPointsRejectNonFiniteAndDegenerateCameraState()
    {
        var source = Triangle();
        foreach (SceneCamera camera in InvalidCameras())
        {
            Assert.ThrowsAny<ArgumentException>(() => source.WithCamera(camera));
            Assert.ThrowsAny<ArgumentException>(() => Triangle(camera));
            Assert.ThrowsAny<ArgumentException>(() => camera.ViewProjection(640, 480));
        }
        Assert.Equal(Vector2.Zero, source.Camera.ProjectionJitterPixels);
    }

    private static IEnumerable<SceneCamera> InvalidCameras()
    {
        var camera = SceneCamera.Default;
        yield return camera with { ProjectionJitterPixels = new(float.NaN, 0) };
        yield return camera with { ProjectionJitterPixels = new(0, float.PositiveInfinity) };
        yield return camera with { Eye = new(float.NaN, 0, 0) };
        yield return camera with { Target = new(0, float.NegativeInfinity, 0) };
        yield return camera with { Eye = camera.Target };
        yield return camera with { Up = Vector3.Zero };
        yield return camera with { Up = camera.Target - camera.Eye };
        yield return camera with { Eye = new(float.MaxValue, 0, 0), Target = new(-float.MaxValue, 0, 0) };
        yield return camera with { Up = new(0, float.MaxValue, 0) };
        yield return camera with { Eye = Vector3.Zero, Target = new(1e-30f, 0, 0), Up = new(0, 1e18f, 0) };
        yield return camera with { OrthographicHeight = float.NaN };
        yield return camera with { OrthographicHeight = 0 };
        yield return camera with { Near = 0 };
        yield return camera with { Near = float.NaN };
        yield return camera with { Far = camera.Near };
        yield return camera with { Far = float.PositiveInfinity };
        yield return camera with { VerticalFovDegrees = float.NaN };
        yield return camera with { VerticalFovDegrees = 180 };
    }

    private static SceneCamera Camera(bool leftHanded, bool perspective) =>
        new(new(3, 4, -20), new(0, 2, 0), Vector3.UnitY, 17, .1f, 200, perspective ? 37 : null, leftHanded);

    private static SceneSnapshot Triangle(SceneCamera? camera = null) => new(
        [new(new(-1, 0, 0), -Vector3.UnitZ, Vector2.Zero), new(new(1, 0, 0), -Vector3.UnitZ, Vector2.UnitX), new(new(0, 1, 0), -Vector3.UnitZ, Vector2.UnitY)],
        [0, 1, 2], [new("triangle", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)],
        [new("texture", 1, 1, [255, 255, 255, 255])], Matrix4x4.CreateRotationY(.2f), camera, new(.2f, .8f, -.5f));

    private static byte[] MatrixBytes(Matrix4x4 matrix) => MemoryMarshal.AsBytes(new[] { matrix }.AsSpan()).ToArray();

    private static Matrix4x4 HistoricalViewProjection(SceneCamera camera, int width, int height)
    {
        if (camera.LeftHanded) return Matrix4x4.CreateLookAtLeftHanded(camera.Eye, camera.Target, camera.Up)
            * (camera.VerticalFovDegrees is { } lhDegrees
                ? Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(lhDegrees * MathF.PI / 180, (float)width / height, camera.Near, camera.Far)
                : Matrix4x4.CreateOrthographicLeftHanded(camera.OrthographicHeight * width / height, camera.OrthographicHeight, camera.Near, camera.Far));
        return Matrix4x4.CreateLookAt(camera.Eye, camera.Target, camera.Up)
            * (camera.VerticalFovDegrees is { } degrees
                ? Matrix4x4.CreatePerspectiveFieldOfView(degrees * MathF.PI / 180, (float)width / height, camera.Near, camera.Far)
                : Matrix4x4.CreateOrthographic(camera.OrthographicHeight * width / height, camera.OrthographicHeight, camera.Near, camera.Far));
    }
}
