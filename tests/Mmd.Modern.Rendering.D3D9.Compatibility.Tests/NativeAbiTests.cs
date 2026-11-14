using System.Runtime.InteropServices;
using System.Text;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

public sealed class NativeAbiTests
{
    [Fact]
    public void NativeStructuresHaveTheHeaderX64Layout()
    {
        Assert.Equal(40, Marshal.SizeOf<NativeError>());
        Assert.Equal(40, Marshal.SizeOf<NativeAbi>());
        Assert.Equal(24, Marshal.SizeOf<NativeCreate>());
        Assert.Equal(16, Marshal.SizeOf<NativeResize>());
        Assert.Equal(88, Marshal.SizeOf<NativeCapture>());
        Assert.Equal(80, Marshal.SizeOf<NativeCaptureResult>());
        Assert.Equal(48, Marshal.SizeOf<NativeCreatePreview>());
        Assert.Equal(72, Marshal.SizeOf<NativePresent>());
        Assert.Equal(80, Marshal.SizeOf<NativePresentResult>());
        Offset<NativeError>(nameof(NativeError.Utf8), 8);
        Offset<NativeError>(nameof(NativeError.RequiredBytes), 24);
        Offset<NativeAbi>(nameof(NativeAbi.SupportedFeatures), 24);
        Offset<NativeCapture>(nameof(NativeCapture.FrameBytes), 8);
        Offset<NativeCapture>(nameof(NativeCapture.RequiredFeatures), 40);
        Offset<NativeCapture>(nameof(NativeCapture.Flags), 48);
        Offset<NativeCapture>(nameof(NativeCapture.Rgba), 64);
        Offset<NativeCapture>(nameof(NativeCapture.RgbaStride), 80);
        Offset<NativeCaptureResult>(nameof(NativeCaptureResult.SourceFrame), 16);
        Offset<NativeCaptureResult>(nameof(NativeCaptureResult.EffectDispatches), 56);
        Offset<NativeCreatePreview>(nameof(NativeCreatePreview.TargetHwnd), 24);
        Offset<NativeCreatePreview>(nameof(NativeCreatePreview.Reserved0), 32);
        Offset<NativePresent>(nameof(NativePresent.FrameBytes), 8);
        Offset<NativePresent>(nameof(NativePresent.RequiredFeatures), 40);
        Offset<NativePresent>(nameof(NativePresent.Flags), 48);
        Offset<NativePresent>(nameof(NativePresent.Reserved0), 64);
        Offset<NativePresentResult>(nameof(NativePresentResult.SourceFrame), 16);
        Offset<NativePresentResult>(nameof(NativePresentResult.ReadbackBytes), 40);
        Offset<NativePresentResult>(nameof(NativePresentResult.EffectDispatches), 56);
        foreach (var type in typeof(NativeSurfaceExports).GetNestedTypes(System.Reflection.BindingFlags.NonPublic))
        {
            if (!typeof(Delegate).IsAssignableFrom(type)) continue;
            Assert.Equal(CallingConvention.Cdecl, type.GetCustomAttributes(typeof(UnmanagedFunctionPointerAttribute), false)
                .Cast<UnmanagedFunctionPointerAttribute>().Single().CallingConvention);
        }
    }

    [Fact]
    public void LargerCompatibleAbiMetadataIsAcceptedButBadVersionsAndFeaturesFail()
    {
        (NativeAbi.Valid() with { Size = 72 }).Validate();
        Assert.Throws<NotSupportedException>(() => (NativeAbi.Valid() with { Size = 39 }).Validate());
        Assert.Throws<NotSupportedException>(() => (NativeAbi.Valid() with { Version = 2 }).Validate());
        Assert.Throws<NotSupportedException>(() => (NativeAbi.Valid() with { AbiVersion = 2 }).Validate());
        Assert.Throws<NotSupportedException>(() => (NativeAbi.Valid() with { PointerBits = 32 }).Validate());
        Assert.Throws<NotSupportedException>(() => (NativeAbi.Valid() with { SupportedFeatures = 2 }).Validate());
        Assert.Throws<NotSupportedException>(() => (NativeAbi.Valid() with { SupportedFeatures = 128 }).Validate());
        Assert.Throws<NotSupportedException>(() => (NativeAbi.Valid() with { SupportedFeatures = 0x8001 }).Validate());
        Assert.Throws<NotSupportedException>(() => (NativeAbi.Valid() with { Reserved1 = 1 }).Validate());
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(131UL)]
    [InlineData(255UL)]
    [InlineData(511UL)]
    [InlineData(1923UL)] // The new scene-capable DLL must preserve isolated requests.
    public void CapturePinsCorrectHeaderBuffersAndReturnsIndependentTopDownRgba(ulong reportedFeatures)
    {
        var unloaded = 0;
        using var api = new NativeSurfaceApi(1, Exports(
            query: (ref NativeAbi abi, ref NativeError error) => { abi = NativeAbi.Valid() with { SupportedFeatures = reportedFeatures }; return 0; },
            capture: (ulong handle, in NativeCapture capture, ref NativeCaptureResult result, ref NativeError error) =>
        {
            Assert.Equal(0x1_0000_0002UL, handle);
            Assert.Equal(88U, capture.Size);
            Assert.Equal(1U, capture.Version);
            Assert.Equal(1UL, capture.RequiredFeatures);
            Assert.Equal(1U, capture.Flags);
            Assert.Equal(1U, capture.RenderOrder);
            Assert.Equal(208UL, capture.FrameLength);
            Assert.Equal(128UL, capture.VisualLength);
            Assert.Equal(32UL, capture.RgbaCapacity);
            Assert.Equal(16UL, capture.RgbaStride);
            Assert.Equal((byte)'M', Marshal.ReadByte(capture.FrameBytes));
            Assert.Equal((byte)'M', Marshal.ReadByte(capture.VisualBytes));
            for (var i = 0; i < 32; ++i) Marshal.WriteByte(capture.Rgba, i, (byte)i);
            result = Result();
            return 0;
        }), _ => unloaded++);
        Assert.Equal(reportedFeatures, api.QueryAbi().SupportedFeatures);
        var handle = api.Create(new(4, 2));
        var output = api.Capture(handle, SurfaceSessionTests.Snapshot(), D3D9SurfaceRenderOrder.OpaqueFirst);
        api.Destroy(handle);
        api.Dispose();
        Assert.Equal(Enumerable.Range(0, 32).Select(x => (byte)x), output.Rgba);
        Assert.Equal(4706, output.SourceFrame);
        Assert.Equal(1, unloaded);
        Assert.True(output.IsolatedHostMetadata);
        api.Dispose();
        Assert.Equal(1, unloaded);
    }

    [Theory]
    [InlineData(3UL)]
    [InlineData(131UL)]
    public void AdditionalUvCaptureDeclaresBothRequiredFeaturesToTheNativeEntryPoint(ulong reportedFeatures)
    {
        var calls = 0;
        using var api = new NativeSurfaceApi(1, Exports(
            query: (ref NativeAbi abi, ref NativeError error) => { abi = NativeAbi.Valid() with { SupportedFeatures = reportedFeatures }; return 0; },
            capture: (ulong handle, in NativeCapture capture, ref NativeCaptureResult result, ref NativeError error) =>
        {
            calls++;
            Assert.Equal(3UL, capture.RequiredFeatures);
            Assert.Equal(216UL, capture.FrameLength);
            result = Result();
            return 0;
        }), _ => { });
        Assert.Equal(reportedFeatures, api.QueryAbi().SupportedFeatures);
        var handle = api.Create(new(4, 2));
        var packets = SurfaceAdditionalUvSessionTests.UvPackets();
        _ = api.Capture(handle, SurfaceAdditionalUvSessionTests.Copy(packets.Frame, packets.Visual), D3D9SurfaceRenderOrder.Original);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void NativeStatusAndTruncatedUtf8ArePreservedWithoutRetryingOperation()
    {
        var calls = 0;
        using var api = new NativeSurfaceApi(1, Exports(capture: (ulong _, in NativeCapture capture, ref NativeCaptureResult result, ref NativeError error) =>
        {
            calls++;
            var message = Encoding.UTF8.GetBytes("设备丢失; Destroy required");
            Marshal.Copy(message, 0, error.Utf8, message.Length);
            error.Status = -9;
            error.RequiredBytes = 6000;
            return -9;
        }), _ => { });
        var handle = api.Create(new(4, 2));
        var failure = Assert.Throws<D3D9SurfaceNativeException>(() => api.Capture(handle, SurfaceSessionTests.Snapshot(), 0));
        Assert.Equal(-9, failure.Status);
        Assert.Equal("capture", failure.Operation);
        Assert.Equal("设备丢失; Destroy required", failure.Diagnostic);
        Assert.Equal(6000UL, failure.RequiredDiagnosticBytes);
        Assert.True(failure.DiagnosticTruncated);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void FailedDestroyDoesNotUnloadAndApiDisposeCanRetry()
    {
        var attempts = 0;
        var unloaded = 0;
        var api = new NativeSurfaceApi(1, Exports(destroy: (ulong _, ref NativeError error) =>
        {
            if (attempts++ != 0) return 0;
            error.Status = -10;
            return -10;
        }), _ => unloaded++);
        var handle = api.Create(new(4, 2));
        Assert.Throws<D3D9SurfaceNativeException>(() => api.Destroy(handle));
        Assert.Equal(0, unloaded);
        api.Dispose();
        Assert.Equal(1, unloaded);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void CreateFailureWithUnexpectedHandleStillHasAnOwnerForCleanup()
    {
        var destroyed = 0;
        var unloaded = 0;
        var api = new NativeSurfaceApi(1, Exports(create: (in NativeCreate _, out ulong handle, ref NativeError error) =>
        {
            handle = 0x1_0000_0002UL;
            error.Status = -10;
            return -10;
        }, destroy: (ulong handle, ref NativeError _) => { destroyed++; return 0; }), _ => unloaded++);
        Assert.Throws<D3D9SurfaceNativeException>(() => api.Create(new(4, 2)));
        api.Dispose();
        Assert.Equal(1, destroyed);
        Assert.Equal(1, unloaded);
    }

    [Fact]
    public void InvalidResultMetadataIsRejectedAfterNativeReturns()
    {
        using var api = new NativeSurfaceApi(1, Exports(capture: (ulong _, in NativeCapture capture, ref NativeCaptureResult result, ref NativeError error) =>
        {
            result = Result();
            result.Width = 99;
            return 0;
        }), _ => { });
        var handle = api.Create(new(4, 2));
        Assert.Throws<InvalidDataException>(() => api.Capture(handle, SurfaceSessionTests.Snapshot(), 0));
    }

    private static void Offset<T>(string field, int value) where T : struct => Assert.Equal(value, Marshal.OffsetOf<T>(field).ToInt32());

    private static NativeCaptureResult Result() => new()
    {
        Size = 80, Version = 1, Width = 4, Height = 2, SourceFrame = 4706,
        RgbaBytesRequired = 32, CompletedCaptures = 1, GeometryGeneration = 2, Flags = 1
    };

    private static NativeSurfaceExports Exports(NativeSurfaceExports.QueryAbiCall? query = null,
        NativeSurfaceExports.CreateCall? create = null, NativeSurfaceExports.CaptureCall? capture = null,
        NativeSurfaceExports.DestroyCall? destroy = null) => new()
    {
        QueryAbi = query ?? ((ref NativeAbi abi, ref NativeError error) => { abi = NativeAbi.Valid(); return 0; }),
        Create = create ?? ((in NativeCreate create, out ulong handle, ref NativeError error) =>
        {
            Assert.Equal(24U, create.Size);
            Assert.Equal(1U, create.Version);
            handle = 0x1_0000_0002UL;
            return 0;
        }),
        Capture = capture ?? ((ulong handle, in NativeCapture capture, ref NativeCaptureResult result, ref NativeError error) => { result = Result(); return 0; }),
        Resize = (ulong handle, in NativeResize resize, ref NativeError error) => 0,
        Destroy = destroy ?? ((ulong handle, ref NativeError error) => 0)
    };
}
