using System.Buffers.Binary;
using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed partial class SurfaceFrameEncoderTests
{
    [Fact]
    public void ClassicPerspectiveUsesOriginalDepthRangeWithoutChangingTheSharedCamera()
    {
        using var project = ClassicMaterialProject(ModelRole.Character);
        var ordinary = new D3D9SurfaceFrameEncoder(MaterialBindings(project));
        var classic = new D3D9SurfaceFrameEncoder(MaterialBindings(project));
        classic.UseClassicPassInputs(ClassicSelections);
        var frame = MaterialCapture(project, classic);
        var camera = new SceneCamera(new(0, 0, -25), Vector3.Zero, Vector3.UnitY,
            10, .1f, 10000, 30, LeftHanded: true);
        frame = frame with { ResolvedCamera = camera };
        var before = project.CurrentFrame;

        var normalPacket = ordinary.Serialize(frame, Request, Vector4.One).Frame;
        var classicPacket = classic.Serialize(frame, Request, Vector4.One).Frame;
        var expected = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(MathF.PI / 6,
            (float)Request.Width / Request.Height, 1, 100000);
        var retained = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(MathF.PI / 6,
            (float)Request.Width / Request.Height, camera.Near, camera.Far);
        // FRM camera projection follows its 52-byte header and 64-byte view.
        static float Entry(byte[] packet, int index) =>
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(116 + index * 4));
        Assert.Equal(expected.M33, Entry(classicPacket, 10));
        Assert.Equal(expected.M43, Entry(classicPacket, 14));
        Assert.Equal(retained.M33, Entry(normalPacket, 10));
        Assert.Equal(retained.M43, Entry(normalPacket, 14));
        Assert.NotEqual(Entry(normalPacket, 14), Entry(classicPacket, 14));
        Assert.Same(camera, frame.ResolvedCamera);
        Assert.Equal(.1f, camera.Near); Assert.Equal(10000, camera.Far);
        Assert.Equal(before, project.CurrentFrame);
    }
}
