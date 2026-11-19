using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12TransientResourcePoolTests
{
    [Fact]
    public void SteadyStateRenderingAndCaptureReachZeroFrameAllocationsAndZeroDescriptorWrites()
    {
        var plainFrame = D3D12OriginalFrameTests.Alpha(false, 0, 1);
        var shadowFrame = D3D12OriginalFrameTests.Shadow(true);
        var smaaDiffusionFrame = new OriginalFrameScene(plainFrame.Surface,
            options: plainFrame.Options! with { Antialiasing = FrameAntialiasing.Smaa1X, SmaaQuality = SmaaQuality.High });
        var ssaaFrame = new OriginalFrameScene(plainFrame.Surface,
            options: plainFrame.Options! with { SpatialSamples = 4 });
        var straightFrame = new OriginalFrameScene(plainFrame.Surface,
            options: plainFrame.Options! with { AlphaOutput = FrameAlphaOutput.Straight, BackgroundColor = new(0, 0, 0, .4f) });
        var diffusion = new D3D12DiffusionOptions(true, .6f, .01f);
        var fsr1 = new D3D12SpatialUpscalingOptions { Mode = D3D12SpatialUpscalingMode.Fsr1, EnableRcas = true };

        AssertSteadyState(plainFrame, 97, 73, null, null, expectedFrameOwned: 0);
        AssertSteadyState(shadowFrame, 97, 73, null, null, expectedFrameOwned: 1);
        AssertSteadyState(smaaDiffusionFrame, 97, 73, null, diffusion, expectedFrameOwned: 0);
        AssertSteadyState(ssaaFrame, 65, 49, fsr1, null, expectedFrameOwned: 0);
        AssertSteadyState(straightFrame, 73, 57, null, diffusion, expectedFrameOwned: 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelayedConsumerOfPreviousGpuOutputReadsIntactPixelsAfterSubsequentFrames(bool postProcessed)
    {
        var baseA = D3D12OriginalFrameTests.Shadow(true);
        var frameA = new OriginalFrameScene(baseA.Surface,
            options: baseA.Options! with
            {
                BackgroundColor = new(0.12f, 0.72f, 0.28f, 1f),
                Antialiasing = postProcessed ? FrameAntialiasing.Smaa1X : FrameAntialiasing.Off
            });
        var frameB = new OriginalFrameScene(baseA.Surface,
            options: baseA.Options! with
            {
                BackgroundColor = new(0.86f, 0.14f, 0.22f, 1f),
                Antialiasing = postProcessed ? FrameAntialiasing.Smaa1X : FrameAntialiasing.Off
            });
        var diffusion = postProcessed ? new D3D12DiffusionOptions(true, .6f, .01f) : null;
        const int width = 97, height = 73;

        byte[] expectedA;
        byte[] expectedB;
        using (var reference = new D3D12FrameSession())
        {
            expectedA = reference.Capture(frameA, width, height, diffusion: diffusion).Rgba;
            expectedB = reference.Capture(frameB, width, height, diffusion: diffusion).Rgba;
        }
        Assert.False(expectedA.AsSpan().SequenceEqual(expectedB));

        using var session = new D3D12FrameSession();
        // Warm up the three recording slots so the transient pool holds active entries.
        for (int i = 0; i < 3; i++)
        {
            using var warm = session.Render(frameB, width, height, diffusion: diffusion);
        }

        // Hold frame A's GPU output without disposing while 6 subsequent frames execute and overwrite pool targets.
        var heldA = session.Render(frameA, width, height, diffusion: diffusion);
        try
        {
            for (int i = 0; i < 6; i++)
            {
                using var next = session.Render(frameB, width, height, diffusion: diffusion);
                if (i == 5)
                    Assert.Equal(expectedB, session.Capture(frameB, width, height, diffusion: diffusion).Rgba);
            }

            // Delayed consumer reads frame A's GPU resource AFTER subsequent frames completed: must never be overwritten.
            byte[] actualA = ReadOutput(session.Runtime, heldA);
            Assert.Equal(expectedA, actualA);
        }
        finally
        {
            heldA.Dispose();
        }

        // After lease disposal, subsequent frames can reclaim/reuse the retired entry without unbounded growth.
        int afterReleaseLive = session.Statistics!.TransientLiveResources;
        for (int i = 0; i < 6; i++)
        {
            using var next = session.Render(frameB, width, height, diffusion: diffusion);
        }
        Assert.InRange(session.Statistics!.TransientLiveResources, 1, afterReleaseLive + 1);
    }

    [Fact]
    public void ResizeGenerationsRetireOldPooledResourcesAndPreserveExactPixels()
    {
        var baseFrame = D3D12OriginalFrameTests.Shadow(true);
        var frame = new OriginalFrameScene(baseFrame.Surface,
            options: baseFrame.Options! with { Antialiasing = FrameAntialiasing.Smaa1X });
        var diffusion = new D3D12DiffusionOptions(true, .5f, .01f);

        byte[] expectedSmall;
        byte[] expectedLarge;
        using (var reference = new D3D12FrameSession())
        {
            expectedLarge = reference.Capture(frame, 161, 121, diffusion: diffusion).Rgba;
            expectedSmall = reference.Capture(frame, 65, 49, diffusion: diffusion).Rgba;
        }

        using var session = new D3D12FrameSession();
        for (int i = 0; i < 9; i++)
        {
            using var output = session.Render(frame, 161, 121, diffusion: diffusion);
        }
        Assert.Contains((161UL, 121), session.Runtime.Transients.LiveTextureDimensions());

        // Resize to 65x49 and run past one full 3-slot recording cycle so 161x121 entries retire.
        for (int i = 0; i < 9; i++)
        {
            using var output = session.Render(frame, 65, 49, diffusion: diffusion);
        }
        var liveAfterResize = session.Runtime.Transients.LiveTextureDimensions();
        Assert.Contains((65UL, 49), liveAfterResize);
        Assert.DoesNotContain((161UL, 121), liveAfterResize);
        Assert.Equal(expectedSmall, session.Capture(frame, 65, 49, diffusion: diffusion).Rgba);
        Assert.Equal(expectedSmall, session.Capture(frame, 65, 49, diffusion: diffusion).Rgba);
        Assert.Equal(0, session.Statistics!.TransientResourceCreations);
        Assert.Equal(0, session.Statistics.TextureDescriptorWrites);

        // Resize back to 161x121 and verify exact pixels.
        Assert.Equal(expectedLarge, session.Capture(frame, 161, 121, diffusion: diffusion).Rgba);
        session.WaitForIdle();
        Assert.Equal(0, session.Runtime.Transients.LiveCount);
    }

    [Fact]
    public void AbortedRecordingRestoresPooledStateAndDoesNotCorruptNextFrame()
    {
        var baseFrame = D3D12OriginalFrameTests.Shadow(true);
        var plain = baseFrame;
        var smaaFrame = new OriginalFrameScene(baseFrame.Surface,
            options: baseFrame.Options! with { Antialiasing = FrameAntialiasing.Smaa1X });
        var diffusion = new D3D12DiffusionOptions(true, .7f, .01f);
        const int width = 97, height = 73;

        byte[] expectedPlain;
        byte[] expectedSmaa;
        using (var reference = new D3D12FrameSession())
        {
            expectedPlain = reference.Capture(plain, width, height).Rgba;
            expectedSmaa = reference.Capture(smaaFrame, width, height, diffusion: diffusion).Rgba;
        }

        using var session = new D3D12FrameSession();
        for (int i = 0; i < 4; i++)
        {
            using var warm = session.Render(plain, width, height);
        }
        long submissions = session.Runtime.Submissions;

        // Abort a recording that introduces new passes (SMAA + Diffusion), exercising abort of both reused and newly created entries.
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(smaaFrame, width, height, null,
            (_, _) => throw new OperationCanceledException("Abort mid-frame before submit."),
            diffusion: diffusion));
        Assert.False(session.Runtime.IsRecording);
        Assert.Equal(submissions, session.Runtime.Submissions);

        // Subsequent plain and SMAA+Diffusion frames must match reference byte-for-byte.
        Assert.Equal(expectedPlain, session.Capture(plain, width, height).Rgba);
        Assert.Equal(expectedSmaa, session.Capture(smaaFrame, width, height, diffusion: diffusion).Rgba);

        for (int i = 0; i < 6; i++)
        {
            using var next = session.Render(smaaFrame, width, height, diffusion: diffusion);
        }
        Assert.Equal(expectedSmaa, session.Capture(smaaFrame, width, height, diffusion: diffusion).Rgba);
        Assert.Equal(expectedSmaa, session.Capture(smaaFrame, width, height, diffusion: diffusion).Rgba);
        // Shadow(true) keeps 1 retained shadow-target lease; all transient GPU resources and descriptors stay 0.
        Assert.InRange(session.Statistics!.FrameOwnedAllocations, 0, 1);
        Assert.Equal(0, session.Statistics.TransientCreations);
        Assert.Equal(0, session.Statistics.TransientResourceCreations);
        Assert.Equal(0, session.Statistics.TextureDescriptorWrites);
    }

    [Fact]
    public void SrvCacheTracksTextureIdentityViewFormatHeapGenerationAndGiSampleSlots()
    {
        using (var runtime = new D3D12GpuRuntime())
        using (var heaps = new D3D12FrameDescriptorHeaps(runtime))
        {
            int stride = runtime.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            using var texA = runtime.Device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, 4, 4, mipLevels: 1), ResourceStates.PixelShaderResource);
            using var texB = runtime.Device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, 4, 4, mipLevels: 1), ResourceStates.PixelShaderResource);

            runtime.Begin();
            var set = heaps.Get(4);
            Assert.True(heaps.GetType() is not null);
            Assert.True(set.WriteSrv(runtime.Device, 0, texA, stride, Format.R8G8B8A8_UNorm));
            Assert.False(set.WriteSrv(runtime.Device, 0, texA, stride, Format.R8G8B8A8_UNorm));
            // Same resource with a different view format must rewrite.
            Assert.True(set.WriteSrv(runtime.Device, 0, texA, stride, Format.R8G8B8A8_UNorm_SRgb));
            Assert.False(set.WriteSrv(runtime.Device, 0, texA, stride, Format.R8G8B8A8_UNorm_SRgb));
            // Different resource on the same slot must rewrite.
            Assert.True(set.WriteSrv(runtime.Device, 0, texB, stride, Format.R8G8B8A8_UNorm));
            // Heap growth bumps generation and requires full refill.
            int genBefore = set.SrvGeneration;
            Assert.True(set.EnsureCapacity(runtime.Device, 16));
            Assert.Equal(genBefore + 1, set.SrvGeneration);
            Assert.True(set.WriteSrv(runtime.Device, 0, texB, stride, Format.R8G8B8A8_UNorm));
            Assert.False(set.WriteSrv(runtime.Device, 0, texB, stride, Format.R8G8B8A8_UNorm));
            // Layout change forces refill.
            set.UseLayout((1, 0, 0, 0, 0));
            set.UseLayout((2, 0, 0, 0, 0));
            Assert.True(set.WriteSrv(runtime.Device, 0, texB, stride, Format.R8G8B8A8_UNorm));
            runtime.Abort();
        }

        // End-to-end GI + SSAA + texture + sRGB stylized texture: GI slots remain independent across samples
        // and steady-state texture descriptor writes drop to zero.
        var bounce = D3D12RayGiTests.Bounce();
        var s = bounce.Surface.Snapshot;
        var diffuse = new SceneTexture("diffuse", 2, 2, [200, 120, 80, 255, 180, 140, 90, 255, 160, 110, 70, 255, 220, 180, 130, 255]);
        var matcap = new SceneTexture("matcap", 2, 2, [255, 200, 180, 255, 220, 190, 170, 255, 190, 160, 140, 255, 240, 210, 190, 255]);
        var materials = s.Materials.ToArray();
        materials[0] = materials[0] with { DiffuseTexture = 0 };
        var textured = new SceneSnapshot(s.Vertices, s.Indices, materials, [diffuse, matcap], s.World, s.Camera, s.LightDirection);
        var frame = new OriginalFrameScene(new OriginalStyleScene(textured, bounce.Surface.Materials, bounce.Surface.Profile),
            options: bounce.Options! with { SpatialSamples = 4 }, materials: bounce.Materials);
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: .5f, SamplesPerPixel: 2, Seed: 17,
            MaxRayDistance: 30, RayEpsilon: .001f, Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.Spatial);
        var styles = T8bTestHelpers.CreateDirectMap(["genshin", "mmd"],
            (D3D12CharacterMaterialRole.Body,
                [TestStylePlugins.GenshinMetallic(0, 1, matcapEncoding: "SrgbRgb",
                    darkColor: new(.3f, .4f, .5f), lightColor: new(.9f, .8f, .7f), shadowMultiplyColor: new(.6f, .5f, .4f))],
                .35f, 1f, .2f),
            (D3D12CharacterMaterialRole.Body, null, .35f, 1f, .2f));

        byte[] expected;
        using (var reference = new D3D12FrameSession())
        {
            expected = reference.Capture(frame, 65, 49, rayGiOptions: gi, surfaceStyles: styles).Rgba;
        }

        using var session = new D3D12FrameSession();
        for (int i = 0; i < 4; i++)
        {
            using var warm = session.Render(frame, 65, 49, rayGiOptions: gi, surfaceStyles: styles);
        }
        var stats = session.Statistics!;
        Assert.Equal(0, stats.TextureDescriptorWrites);
        Assert.True(stats.TextureDescriptorSkips >= 5);
        Assert.Equal(expected, session.Capture(frame, 65, 49, rayGiOptions: gi, surfaceStyles: styles).Rgba);
    }

    [Fact]
    public async Task D3D12ViewRendererReusesExactSizedPixelBuffersDiscardsMismatchesAndClearsOnDispose()
    {
        var token = TestContext.Current.CancellationToken;
        const int width = 81, height = 61;
        int requiredBytes = checked(width * height * 4);
        var source = D3D12OriginalFrameTests.Shadow(true);
        var scene = new OriginalFrameScene(source.Surface, source.Outline, source.OutlineOptions,
            source.Options! with { Antialiasing = FrameAntialiasing.Off, SpatialSamples = 1 },
            source.Materials, source.Lighting, source.EditorOverlay);
        var plan = new OfflineFramePlan(width, height, new(30), 0, 5);
        byte[] expected;
        using (var direct = new D3D12FrameSession())
        {
            expected = direct.Capture(scene, width, height).Rgba;
        }

        var renderer = new D3D12ViewRenderer(new ViewRenderRequest(width, height));
        var freeField = typeof(D3D12ViewRenderer).GetField("freePixels", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var freePixels = Assert.IsType<Stack<byte[]>>(freeField.GetValue(renderer));

        OfflineFrame Frame(int index) => new(plan.GetSample(index), width, height, scene, null, null, null);

        byte[]? firstArray;
        using (var lease1 = await renderer.CaptureAsync(Frame(0), token))
        {
            Assert.True(MemoryMarshal.TryGetArray(lease1.Pixels, out var seg1));
            firstArray = seg1.Array;
            Assert.NotNull(firstArray);
            Assert.Equal(requiredBytes, firstArray!.Length);
            Assert.Equal(expected, lease1.Pixels.ToArray());
        }
        Assert.Single(freePixels);

        // Next capture reuses the exact returned buffer.
        using (var lease2 = await renderer.CaptureAsync(Frame(1), token))
        {
            Assert.True(MemoryMarshal.TryGetArray(lease2.Pixels, out var seg2));
            Assert.Same(firstArray, seg2.Array);
            Assert.Equal(expected, lease2.Pixels.ToArray());
        }
        Assert.Single(freePixels);

        // Seed wrong-length buffers into freePixels: CaptureAsync must discard them and allocate/use exact size.
        lock (freePixels)
        {
            freePixels.Clear();
            freePixels.Push(new byte[16]);
            freePixels.Push(new byte[requiredBytes + 4]);
        }
        var lease3 = await renderer.CaptureAsync(Frame(2), token);
        Assert.Equal(requiredBytes, lease3.Pixels.Length);
        Assert.True(MemoryMarshal.TryGetArray(lease3.Pixels, out var seg3));
        Assert.Equal(requiredBytes, seg3.Array!.Length);
        Assert.Equal(expected, lease3.Pixels.ToArray());
        Assert.Empty(freePixels);

        // Hold lease3 while capturing lease4 so pool holds a separate returned buffer before DisposeAsync.
        using (var lease4 = await renderer.CaptureAsync(Frame(3), token))
        {
            Assert.True(MemoryMarshal.TryGetArray(lease4.Pixels, out var seg4));
            Assert.NotSame(seg3.Array, seg4.Array);
            Assert.Equal(expected, lease4.Pixels.ToArray());
        }
        Assert.Single(freePixels);

        // DisposeAsync clears freePixels immediately; late disposal of lease3 must not re-populate freePixels.
        await renderer.DisposeAsync();
        Assert.Empty(freePixels);
        Assert.Equal(expected, lease3.Pixels.ToArray());
        lease3.Dispose();
        Assert.Empty(freePixels);
    }

    [Fact]
    public void CaptureIntoWritesTightlyPackedRgbaAndRejectsUndersizedBuffers()
    {
        var frame = D3D12OriginalFrameTests.Shadow(true);
        const int width = 97, height = 73;
        int bytes = width * height * 4;
        using var session = new D3D12FrameSession();
        var expected = session.Capture(frame, width, height).Rgba;

        Assert.Throws<ArgumentException>(() => session.CaptureInto(new byte[bytes - 1], frame, width, height));

        byte[] destination = new byte[bytes + 64];
        destination.AsSpan(bytes).Fill(0xA5);
        var into = session.CaptureInto(destination, frame, width, height);
        Assert.Equal(width, into.Width);
        Assert.Equal(height, into.Height);
        Assert.Empty(into.Rgba);
        Assert.Equal(expected, destination.AsSpan(0, bytes).ToArray());
        for (int i = bytes; i < destination.Length; i++) Assert.Equal((byte)0xA5, destination[i]);
    }

    private static void AssertSteadyState(OriginalFrameScene frame, int width, int height,
        D3D12SpatialUpscalingOptions? upscaling, D3D12DiffusionOptions? diffusion, int expectedFrameOwned)
    {
        byte[] expected;
        using (var reference = new D3D12FrameSession())
        {
            expected = reference.Capture(frame, width, height, upscaling: upscaling, diffusion: diffusion).Rgba;
        }

        using var session = new D3D12FrameSession();
        int coldCreations = 0;
        for (int i = 0; i < 24; i++)
        {
            using var output = session.Render(frame, width, height, upscaling: upscaling, diffusion: diffusion);
            if (i == 0) coldCreations = output.Statistics.TransientCreations;
            if (i >= 1) Assert.Equal(expectedFrameOwned, output.Statistics.FrameOwnedAllocations);
            if (i >= 3)
            {
                Assert.Equal(0, output.Statistics.TransientCreations);
                Assert.Equal(0, output.Statistics.TransientResourceCreations);
                Assert.Equal(0, output.Statistics.TextureDescriptorWrites);
                Assert.True(output.Statistics.TransientReuses > 0);
                Assert.True(output.Statistics.TextureDescriptorSkips > 0);
            }
        }
        // Across 24 pipelined frames, total creations never exceed the three recording slots.
        Assert.InRange(session.Runtime.Transients.TotalCreations, coldCreations, checked(3 * coldCreations));

        var buffer = new byte[checked(width * height * 4)];
        for (int i = 0; i < 4; i++)
        {
            var captured = session.Capture(frame, width, height, upscaling: upscaling, diffusion: diffusion);
            Assert.Equal(expected, captured.Rgba);
            var into = session.CaptureInto(buffer, frame, width, height, upscaling: upscaling, diffusion: diffusion);
            Assert.Empty(into.Rgba);
            Assert.Equal(expected, buffer);
            if (i >= 1)
            {
                Assert.Equal(expectedFrameOwned, session.Statistics!.FrameOwnedAllocations);
                Assert.Equal(0, session.Statistics.TransientCreations);
                Assert.Equal(0, session.Statistics.TransientResourceCreations);
                Assert.Equal(0, session.Statistics.TextureDescriptorWrites);
            }
        }
    }

    private static byte[] ReadOutput(D3D12GpuRuntime runtime, D3D12FrameOutput output)
    {
        var context = runtime.Begin();
        var layouts = new PlacedSubresourceFootPrint[1];
        var rows = new int[1];
        var rowBytes = new ulong[1];
        context.Device.GetCopyableFootprints(output.Resource.Description, 0, 1, 0, layouts, rows, rowBytes, out ulong size);
        using var readback = context.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(size), ResourceStates.CopyDest);
        context.Commands.CopyTextureRegion(new TextureCopyLocation(readback, layouts[0]), 0, 0, 0, new TextureCopyLocation(output.Resource, 0));
        runtime.Wait(runtime.Submit());
        var pixels = new byte[checked(output.Width * output.Height * 4)];
        var data = readback.Map<byte>(0, checked((int)size));
        try
        {
            for (int y = 0; y < output.Height; y++)
                data.Slice(y * layouts[0].Footprint.RowPitch, output.Width * 4).CopyTo(pixels.AsSpan(y * output.Width * 4));
        }
        finally { readback.Unmap(0); }
        return pixels;
    }
}
