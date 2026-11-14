using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Application;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class OriginalCameraFramingTests
{
    [Fact]
    public void CameraMatchesWorldAndViewActuallyCapturedFromOriginalExecutable()
    {
        using var stream = typeof(OriginalCameraFramingTests).Assembly.GetManifestResourceStream(
            "Mmd.Modern.Application.Tests.Fixtures.original-camera-main.json")!;
        using var document = JsonDocument.Parse(stream);
        Assert.Equal("882c97dc2ee706acb3501277bf5facc66b82606a006d5337ecb50e2bd4387156",
            document.RootElement.GetProperty("originalExeSha256").GetString());
        foreach (var captured in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var f = captured.GetProperty("fixture");
            bool parented = f.TryGetProperty("parent", out var flag) && flag.GetBoolean();
            bool ignore = f.TryGetProperty("following", out var following) && following.GetInt32() != 0;
            var pose = new PmmCameraPose(Vector(f.GetProperty("at")), Vector(f.GetProperty("angles")),
                f.GetProperty("distance").GetSingle(), 30, true, parented ? 254 : -1, 0, ignore,
                f.TryGetProperty("pan", out var pan) ? new(pan[0].GetSingle(), pan[1].GetSingle()) : Vector2.Zero);
            Matrix4x4? parent = parented ? Matrix4x4.CreateRotationY(f.GetProperty("yaw").GetSingle())
                * Matrix4x4.CreateTranslation(7, 8, 9) : null;
            var camera = pose.CreateCamera(parent);
            var actual = Matrix4x4.CreateLookAtLeftHanded(camera.Eye, camera.Target, camera.Up);
            var expected = Matrix(captured.GetProperty("world")) * Matrix(captured.GetProperty("view"));
            foreach (var point in new[] { Vector3.Zero, new Vector3(2, 5, 1), new Vector3(-3, 17, -6), new Vector3(7, 8, 9) })
            {
                var a = Vector3.Transform(point, actual); var e = Vector3.Transform(point, expected);
                Assert.InRange(Vector3.Distance(e, a), 0, .00005f);
            }
            if (parented)
            {
                // Native parent branch publishes LookAt as its local eye, regardless of raw Distance.
                Assert.Equal(camera, (pose with { Distance = -103 }).CreateCamera(parent));
                Assert.Equal(pose.LookAt.Z, pose.CameraDepth);
            }
        }
    }

    [Theory]
    [InlineData((byte)0, false)]
    [InlineData((byte)1, true)]
    [InlineData((byte)255, true)]
    public void OriginalGlobalFollowRotationFlagSurvivesSampling(byte flag, bool ignored)
    {
        var source = new PmmDocument { Settings = new() { FollowingLookAtRaw = flag },
            Camera = new() { InitialKeyframe = new() { ParentModelIndex = 2, ParentBoneIndex = 3,
                Interpolation = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 6).SelectMany(x => x).ToArray() } } };
        var tracks = new PmmSceneTracks(source);
        Assert.Equal(ignored, tracks.SavedCamera.IgnoreParentRotation);
        Assert.Equal(ignored, tracks.CameraAt(0).IgnoreParentRotation);
    }

    [Fact]
    public void SavedEditorPanIsPreservedAndCanBeRegisteredWithoutMovingTheEye()
    {
        var source = new PmmDocument { Camera = new() { LookAt = new(3, 12, -4),
            Position = new(2, -3, -37), Angle = new(.31f, -.47f, .22f), Perspective = true },
            CameraFov = 30, Settings = new() { CameraLookAtModelIndex = -1 } };
        var pose = new PmmSceneTracks(source).SavedCamera;
        Assert.Equal(new Vector2(2, -3), pose.ViewOffset);
        var registered = pose.ToKeyframePose();
        Assert.Equal(Vector2.Zero, registered.ViewOffset);
        Assert.InRange(Vector3.Distance(pose.CreateCamera().Eye, registered.CreateCamera().Eye), 0, .00001f);
        Assert.Equal(source.Camera.LookAt, pose.LookAt);
    }

    private static Vector3 Vector(JsonElement values) => new(values[0].GetSingle(), values[1].GetSingle(), values[2].GetSingle());
    private static Matrix4x4 Matrix(JsonElement values)
    {
        var f = values.EnumerateArray().Select(v => v.GetSingle()).ToArray();
        return new(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
    }
}
