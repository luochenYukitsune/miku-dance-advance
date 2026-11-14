using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed partial class SurfaceFrameEncoderTests
{
    [Fact]
    public void ClassicBackgroundUsesOriginalWidthBasedPlacementAndFreezesVideoAsOpaque()
    {
        // Original main-view oracle: 1280 reference units, /1.2 and -1/+1 boundaries.
        // Changing only output height does not stretch the original picture independently.
        var placement = new D3D9ClassicMediaPlacement(120, 60, .5f);
        Assert.Equal(new D3D9ClassicMediaRectangle(119, 59, 440, 240), placement.Resolve(640, 360, 1280, 720));
        Assert.Equal(new D3D9ClassicMediaRectangle(59, 29, 220, 120), placement.Resolve(640, 360, 640, 960));
        byte[] decoded = [7, 11, 13, 0, 17, 19, 23, 127];
        var video = new D3D9ClassicMediaLayer(D3D9ClassicMediaKind.Video, 2, 1, new(0, 0, 2, 1), decoded);
        var picture = new D3D9ClassicMediaLayer(D3D9ClassicMediaKind.Picture, 2, 1, new(0, 0, 2, 1), decoded);
        decoded.AsSpan().Fill(99);
        Assert.Equal(new byte[] { 7, 11, 13, 255, 17, 19, 23, 255 }, video.Rgba.ToArray());
        Assert.Equal(new byte[] { 7, 11, 13, 0, 17, 19, 23, 127 }, picture.Rgba.ToArray());
    }

    [Fact]
    public void ClassicScreenMetadataCarriesHiddenGlobalAviAndZeroLayerCaptureControl()
    {
        using var project = ClassicMaterialProject(Mmd.Modern.Application.ModelRole.Character);
        var bindings = MaterialBindings(project);
        var encoder = new D3D9SurfaceFrameEncoder(bindings, SurfaceEmmViewTests.Configuration(bindings));
        encoder.UseClassicPassInputs(ClassicSelections);
        var frame = MaterialCapture(project, encoder);
        var hidden = new D3D9ClassicMediaLayer(D3D9ClassicMediaKind.Video, 1, 1, new(-1, -1, 64, 32), [17, 39, 91, 0], drawBackground: false);
        Assert.Throws<ArgumentException>(() => new D3D9ClassicMediaInputs(frame, [hidden]));
        var packet = encoder.Serialize(frame, Request, Vector4.One, new(frame, [hidden], D3D9ClassicScreenCaptureMode.BackgroundRefresh));
        int start = packet.Frame.AsSpan().LastIndexOf("MDA9MED1"u8);
        Assert.Equal(2U, U32(packet.Frame, start + 8)); Assert.Equal(3U, U32(packet.Frame, start + 24));
        Assert.Equal(1U, U32(packet.Frame, start + 28)); Assert.Equal(2U, U32(packet.Frame, start + 32));
        Assert.Equal(0U, U32(packet.Frame, start + 36));
        Assert.Equal(new byte[] { 17, 39, 91, 255 }, packet.Frame.AsSpan(start + 68, 4).ToArray());
        Assert.Equal(SHA256.HashData(packet.Frame), packet.Visual.AsSpan(20, 32).ToArray());
        var control = encoder.Serialize(frame, Request, Vector4.One, new(frame, [], D3D9ClassicScreenCaptureMode.CropFourByThree));
        int controlStart = control.Frame.AsSpan().LastIndexOf("MDA9MED1"u8);
        Assert.Equal(6U, U32(control.Frame, 8)); Assert.Equal(2U, U32(control.Frame, controlStart + 24));
        Assert.Equal(0U, U32(control.Frame, controlStart + 28)); Assert.Equal(controlStart + 32, control.Frame.Length);
    }

    [Fact]
    public void ClassicMediaRequiresTheExactAcceptedFrameAndBindsTheTimedMediaPacket()
    {
        using var project = ClassicMaterialProject(Mmd.Modern.Application.ModelRole.Character);
        var bindings = MaterialBindings(project);
        var encoder = new D3D9SurfaceFrameEncoder(bindings, SurfaceEmmViewTests.Configuration(bindings));
        encoder.UseClassicPassInputs(ClassicSelections);
        var frame = MaterialCapture(project, encoder);
        var ownerFrame = project.Frame; double ownerClock = project.CurrentFrame;
        var layers = new[] { new D3D9ClassicMediaLayer(D3D9ClassicMediaKind.Video, 1, 1, new(-1, -1, 64, 32), [0, 255, 0, 0]) };
        var foreign = new D3D9ClassicMediaInputs(frame with { }, layers);
        Assert.Throws<InvalidOperationException>(() => encoder.Serialize(frame, Request, Vector4.One, foreign));

        var media = new D3D9ClassicMediaInputs(frame, layers);
        var packet = encoder.Serialize(frame, Request, Vector4.One, media);
        Assert.Equal(6U, U32(packet.Frame, 8)); Assert.Equal(7U, U32(packet.Visual, 8));
        Assert.Equal(7.5, BinaryPrimitives.ReadDoubleLittleEndian(packet.Frame.AsSpan(40)));
        Assert.Equal(SHA256.HashData(packet.Frame), packet.Visual.AsSpan(20, 32).ToArray());
        int mediaStart = packet.Frame.AsSpan().LastIndexOf("MDA9MED1"u8);
        Assert.True(mediaStart > 0);
        Assert.Equal(1U, U32(packet.Frame, mediaStart + 8));
        Assert.Equal(7.5, BinaryPrimitives.ReadDoubleLittleEndian(packet.Frame.AsSpan(mediaStart + 12)));
        Assert.Equal(.25f, BinaryPrimitives.ReadSingleLittleEndian(packet.Frame.AsSpan(mediaStart + 20)));
        Assert.Equal(1U, U32(packet.Frame, mediaStart + 24));
        Assert.Equal(2U, U32(packet.Frame, mediaStart + 28));
        Assert.Equal(4U, U32(packet.Frame, mediaStart + 56));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, packet.Frame.AsSpan(mediaStart + 60, 4).ToArray());
        Assert.Equal(28U, U32(packet.Frame, 208) & 28U);
        var empty = encoder.Serialize(frame, Request, Vector4.One, new(frame, []));
        Assert.Equal(5U, U32(empty.Frame, 8)); Assert.Equal(6U, U32(empty.Visual, 8));
        Assert.Equal(-1, empty.Frame.AsSpan().LastIndexOf("MDA9MED1"u8));
        Assert.Same(ownerFrame, project.Frame); Assert.Equal(ownerClock, project.CurrentFrame);
    }
}
