using System.Runtime.InteropServices;
using System.Numerics;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

// Managed delegates only; this fixture does not load a native library or graphics device.
public sealed class NativePresentationTests
{
    [Fact]
    public void EditorPrimitivesUseTheSeparatePreviewExportWithoutChangingScenePackets()
    {
        int calls = 0;
        using var api = new NativeSurfaceApi(1, Exports(
            present: (ulong handle, in NativePresent present, ref NativePresentResult result, ref NativeError error)
                => throw new InvalidOperationException("Editing helpers must use the explicit overlay export."),
            overlay: (ulong handle, in NativePresentWithOverlay present, ref NativePresentResult result, ref NativeError error) =>
            {
                calls++;
                Assert.Equal(88U, present.Present.Size); Assert.Equal(1U, present.Present.Version);
                Assert.Equal(16513UL, present.Present.RequiredFeatures);
                Assert.Equal(4UL, present.PrimitiveCount);
                Assert.Equal(208UL, present.Present.FrameLength); Assert.Equal(128UL, present.Present.VisualLength);
                Assert.Equal(56, Marshal.SizeOf<NativeEditorOverlayPrimitive>());
                for (int index = 0; index < 4; index++)
                {
                    var primitive = Marshal.PtrToStructure<NativeEditorOverlayPrimitive>(present.Primitives + index * 56);
                    Assert.Equal((uint)index, primitive.Shape);
                    Assert.Equal(index == 0 ? 1U : 0U, primitive.Flags);
                    Assert.Equal(new Vector4(1, .5f, 0, .75f), primitive.Color);
                }
                result = Result(); return 0;
            }), _ => { });
        var original = SurfaceSessionTests.Snapshot();
        var rows = Enumerable.Range(0, 4).Select(index => new D3D9EditorOverlayPrimitive(new(.1f, .2f), new(.8f, .7f),
            new(1, .5f, 0, .75f), Shape: (D3D9EditorOverlayShape)index,
            DeviceDepth: index == 0 ? new Vector2(.3f, .6f) : null)).ToList();
        var snapshot = original.WithPreviewOverlay(rows);
        rows.Clear(); // Submission owns a frozen copy, not the caller's mutable list.
        Assert.Equal(0, original.PreviewOverlayPrimitiveCount); Assert.Equal(4, snapshot.PreviewOverlayPrimitiveCount);
        Assert.Same(original.FrameBytes, snapshot.FrameBytes); Assert.Same(original.VisualBytes, snapshot.VisualBytes);
        var handle = api.CreatePreview((nint)0x1234, new(4, 2));
        api.Present(handle, snapshot, 0);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void MissingOverlayExportAndCaptureRejectHelpersExplicitly()
    {
        var snapshot = SurfaceSessionTests.Snapshot().WithPreviewOverlay([new(Vector2.Zero, Vector2.One, Vector4.One)]);
        using (var api = new NativeSurfaceApi(1, Exports(), _ => { }))
        {
            var handle = api.CreatePreview((nint)0x1234, new(4, 2));
            Assert.Throws<NotSupportedException>(() => api.Present(handle, snapshot, 0));
        }
        using (var api = new NativeSurfaceApi(1, Exports(), _ => { }))
        {
            var handle = api.Create(new(4, 2));
            Assert.Throws<NotSupportedException>(() => api.Capture(handle, snapshot, 0));
        }
    }

    [Fact]
    public void OverlayFreezingRejectsInvalidRowsAndRetainsOnlyTheDeclaredPrimitiveBudget()
    {
        var snapshot = SurfaceSessionTests.Snapshot();
        Assert.Throws<ArgumentException>(() => snapshot.WithPreviewOverlay([new(Vector2.Zero, Vector2.One, Vector4.One, Width: float.NaN)]));
        Assert.Throws<ArgumentException>(() => snapshot.WithPreviewOverlay([new(Vector2.Zero, Vector2.One, Vector4.One,
            Shape: D3D9EditorOverlayShape.Circle, DeviceDepth: new(.5f))]));
        Assert.Throws<ArgumentException>(() => snapshot.WithPreviewOverlay(Enumerable.Repeat(new D3D9EditorOverlayPrimitive(Vector2.Zero, Vector2.One, Vector4.One), 1025)));
        var abi = NativeAbi.Valid() with { SupportedFeatures = 16513 }; abi.Validate();
        Assert.Equal(88, Marshal.SizeOf<NativePresentWithOverlay>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PresentPinsTheExistingPacketsAndReturnsMetadataOnly(bool additionalUv)
    {
        var calls = 0;
        using var api = new NativeSurfaceApi(1, Exports(present:
            (ulong handle, in NativePresent present, ref NativePresentResult result, ref NativeError error) =>
        {
            calls++;
            Assert.Equal(7UL, handle);
            Assert.Equal(72U, present.Size);
            Assert.Equal(1U, present.Version);
            Assert.Equal(additionalUv ? 131UL : 129UL, present.RequiredFeatures);
            Assert.Equal(1U, present.Flags);
            Assert.Equal(1U, present.RenderOrder);
            Assert.Equal(4U, present.Width);
            Assert.Equal(2U, present.Height);
            Assert.Equal(additionalUv ? 216UL : 208UL, present.FrameLength);
            Assert.Equal(128UL, present.VisualLength);
            Assert.Equal((byte)'M', Marshal.ReadByte(present.FrameBytes));
            Assert.Equal((byte)'M', Marshal.ReadByte(present.VisualBytes));
            Assert.Equal(0U, present.Reserved0);
            Assert.Equal(0U, present.Reserved1);
            Assert.Equal(80U, result.Size);
            Assert.Equal(1U, result.Version);
            result = Result() with { Flags = 7, DrawCalls = 9, EffectPassDraws = 3 };
            return 0;
        }), _ => { });
        var snapshot = SurfaceSessionTests.Snapshot();
        if (additionalUv)
        {
            var packets = SurfaceAdditionalUvSessionTests.UvPackets();
            snapshot = SurfaceAdditionalUvSessionTests.Copy(packets.Frame, packets.Visual);
        }
        var handle = api.CreatePreview((nint)0x1234, new(4, 2, adapterOrdinal: 2));
        var output = api.Present(handle, snapshot, D3D9SurfaceRenderOrder.OpaqueFirst);
        Assert.Equal(4, output.Width);
        Assert.Equal(2, output.Height);
        Assert.Equal(4706, output.SourceFrame);
        Assert.Equal(2UL, output.CompletedPresents);
        Assert.Equal(3UL, output.GeometryGeneration);
        Assert.Equal(9U, output.DrawCalls);
        Assert.True(output.IsolatedHostMetadata);
        Assert.True(output.SoftwareVertexProcessing);
        Assert.Equal(3U, output.Diagnostics.EffectPassDraws);
        Assert.DoesNotContain(typeof(D3D9SurfacePresentation).GetProperties(), property => property.PropertyType == typeof(byte[]));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void EitherMissingOptionalExportRejectsPreviewWithoutCallingCreate(bool hasCreate, bool hasPresent)
    {
        var exports = Exports(hasCreate: hasCreate, hasPresent: hasPresent);
        using var api = new NativeSurfaceApi(1, exports, _ => { });
        Assert.False(api.SupportsPreview);
        Assert.Throws<NotSupportedException>(() => api.CreatePreview((nint)0x1234, new(4, 2)));
        // Legacy creation still works with the original five-export contract.
        var handle = api.Create(new(4, 2));
        Assert.Equal(7UL, handle);
        Assert.Throws<InvalidOperationException>(() => api.Present(handle, SurfaceSessionTests.Snapshot(), 0));
    }

    [Theory]
    [InlineData("size")]
    [InlineData("version")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("frame")]
    [InlineData("readback")]
    [InlineData("reserved")]
    [InlineData("isolation")]
    [InlineData("accepted")]
    [InlineData("unknownFlag")]
    public void IncompatiblePresentationMetadataIsRejected(string field)
    {
        using var api = new NativeSurfaceApi(1, Exports(present:
            (ulong handle, in NativePresent present, ref NativePresentResult result, ref NativeError error) =>
        {
            result = Result();
            switch (field)
            {
                case "size": result.Size = 79; break;
                case "version": result.Version = 2; break;
                case "width": result.Width++; break;
                case "height": result.Height++; break;
                case "frame": result.SourceFrame = double.NaN; break;
                case "readback": result.ReadbackBytes = 1; break;
                case "reserved": result.Reserved = 1; break;
                case "isolation": result.Flags = 4; break;
                case "accepted": result.Flags = 1; break;
                case "unknownFlag": result.Flags = 13; break;
            }
            return 0;
        }), _ => { });
        var handle = api.CreatePreview((nint)0x1234, new(4, 2));
        Assert.Throws<InvalidDataException>(() => api.Present(handle, SurfaceSessionTests.Snapshot(), 0));
    }

    [Fact]
    public void PreviewRejectsCaptureAndPreservesNativePresentFailureWithoutRetrying()
    {
        var calls = 0;
        using var api = new NativeSurfaceApi(1, Exports(present:
            (ulong handle, in NativePresent present, ref NativePresentResult result, ref NativeError error) =>
        {
            calls++;
            error.Status = -9;
            return -9;
        }), _ => { });
        var handle = api.CreatePreview((nint)0x1234, new(4, 2));
        Assert.Throws<InvalidOperationException>(() => api.Capture(handle, SurfaceSessionTests.Snapshot(), 0));
        var error = Assert.Throws<D3D9SurfaceNativeException>(() => api.Present(handle, SurfaceSessionTests.Snapshot(), 0));
        Assert.Equal("present", error.Operation);
        Assert.Equal(-9, error.Status);
        Assert.Equal(1, calls);
    }

    private static NativePresentResult Result() => new()
    {
        Size = 80, Version = 1, Width = 4, Height = 2, SourceFrame = 4706,
        CompletedPresents = 2, GeometryGeneration = 3, Flags = 5
    };

    private static NativeSurfaceExports Exports(NativeSurfaceExports.PresentCall? present = null,
        bool hasCreate = true, bool hasPresent = true, NativeSurfaceExports.PresentWithOverlayCall? overlay = null) => new()
    {
        QueryAbi = (ref NativeAbi abi, ref NativeError error) => { abi = NativeAbi.Valid() with { SupportedFeatures = 131 }; return 0; },
        Create = (in NativeCreate create, out ulong handle, ref NativeError error) => { handle = 7; return 0; },
        Capture = (ulong handle, in NativeCapture capture, ref NativeCaptureResult result, ref NativeError error) => throw new InvalidOperationException("Capture was not expected."),
        Resize = (ulong handle, in NativeResize resize, ref NativeError error) => 0,
        Destroy = (ulong handle, ref NativeError error) => 0,
        CreatePreview = !hasCreate ? null : (in NativeCreatePreview create, out ulong handle, ref NativeError error) =>
        {
            Assert.Equal(48U, create.Size);
            Assert.Equal(1U, create.Version);
            Assert.Equal(4U, create.Width);
            Assert.Equal(2U, create.Height);
            Assert.Equal(0x1234UL, create.TargetHwnd);
            Assert.Equal(0U, create.Flags);
            Assert.Equal(0U, create.Reserved0 | create.Reserved1 | create.Reserved2 | create.Reserved3);
            handle = 7;
            return 0;
        },
        Present = !hasPresent ? null : present ?? ((ulong handle, in NativePresent input, ref NativePresentResult result, ref NativeError error) => { result = Result(); return 0; }),
        PresentWithOverlay = overlay
    };
}
