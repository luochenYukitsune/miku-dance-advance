using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class CharacterOutlineTemporalTests
{
    private const int Size = 64;
    private static readonly Vector4 Red = new(1, 0, 0, 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PerDrawMultiplierKeepsTemporalCoverageAndExpansionMotionConsistent(bool modern)
    {
        var template = Box();
        OriginalFrameScene Frame(float width, float scale) => new(template.Surface,
            new OriginalOutlineData(template.Outline!.Vertices, [new(width, Red)]), template.OutlineOptions,
            template.Options, [new(false, false, false, SurfaceEnabled: false)
            { CharacterOutlineStyle = modern ? new(width, Red) : null, OutlineWidthScale = scale }]);
        var previous = Frame(2, 1);
        var current = Frame(2, .25f);
        var manual = Frame(.5f, 1);
        using var runtime = new D3D12GpuRuntime();
        using var pass = new D3D12TemporalInputPass(runtime);
        var actual = Capture(runtime, pass, current, previous);
        var expected = Capture(runtime, pass, manual, previous);
        Assert.Equal(expected.Depth, actual.Depth);
        Assert.Equal(expected.Motion, actual.Motion);
        Assert.Equal(expected.Reactive, actual.Reactive);
        Assert.Contains(actual.Depth, value => value < 1);
        Assert.Contains(actual.Motion, value => value != Vector2.Zero);
    }

    [Theory]
    [InlineData(0, 1f, false, false, 0)]
    [InlineData(128, 1f, false, false, 128)]
    [InlineData(255, 1f, false, true, 0)]
    [InlineData(255, .5f, false, false, 128)]
    [InlineData(255, 1f, true, false, 128)]
    [InlineData(0, 1f, true, false, 128)]
    public void TexturedModernOutlineUsesSurfaceAlphaForCutoutOpaqueDepthAndReactive(
        byte textureAlpha, float outlineAlpha, bool transp, bool opaque, int reactive)
    {
        OriginalFrameScene Frame(byte alpha, float edgeAlpha, bool transparentDiagnostic)
        {
            var b = Box(); var snapshot = b.Surface.Snapshot;
            var material = snapshot.Materials[0] with { DiffuseTexture = 0, AlphaCutoff = .25f };
            var mesh = new SceneSnapshot(snapshot.Vertices, snapshot.Indices, [material],
                [new SceneTexture("uniform-alpha", 1, 1, [255, 255, 255, alpha])], camera: snapshot.Camera);
            var surface = new OriginalStyleScene(mesh, b.Surface.Materials, b.Surface.Profile);
            return new(surface, b.Outline, b.OutlineOptions, b.Options! with { Transp = transparentDiagnostic },
                [new(false, false, false, SurfaceEnabled: false) {
                    CharacterOutlineStyle = new(2, new(1, 0, 0, edgeAlpha)) }]);
        }
        using var runtime = new D3D12GpuRuntime();
        using var pass = new D3D12TemporalInputPass(runtime);
        using var renderer = new D3D12OriginalStyleRenderer();
        var coverage = renderer.Render(Frame(255, 1, false), Size, Size);
        var frame = Frame(textureAlpha, outlineAlpha, transp);
        var output = Capture(runtime, pass, frame, frame);
        int pixels = 0;
        for (int i = 0; i < output.Depth.Length; i++)
        {
            if (coverage.Rgba[i * 4] != 255 || coverage.Rgba[i * 4 + 1] != 0 || coverage.Rgba[i * 4 + 2] != 0) continue;
            pixels++;
            Assert.Equal(opaque, output.Depth[i] < 1);
            Assert.Equal(Vector2.Zero, output.Motion[i]);
            Assert.InRange(output.Reactive[i], Math.Max(0, reactive - 1), reactive + 1);
        }
        Assert.True(pixels > 100);
        // Null metadata still uses the authored solid outline, even when the surface texture is transparent.
        var legacy = EffectiveSource(Frame(0, 1, false));
        var old = Capture(runtime, pass, legacy, legacy);
        Assert.True(old.Depth.Count(value => value < 1) > 100);
        Assert.All(old.Reactive, value => Assert.Equal(0, value));
        Assert.Equal(0, runtime.Readbacks);
    }

    [Fact]
    public void ModernHullCoverageDepthAndExpansionMotionMatchAnIndependentEffectiveSourceControl()
    {
        using var runtime = new D3D12GpuRuntime();
        using var pass = new D3D12TemporalInputPass(runtime);
        var source = Box(); var thin = Styled(source, new(.5f, Red)); var wide = Styled(source, new(2, Red));
        var off = Capture(runtime, pass, source, source);
        var stationary = Capture(runtime, pass, wide, wide);
        var expected = Capture(runtime, pass, EffectiveSource(wide), EffectiveSource(wide));
        AssertBiasedOutlineDepth(wide, expected.Depth, stationary.Depth);
        Assert.Equal(expected.Motion, stationary.Motion);
        Assert.Equal(expected.Reactive, stationary.Reactive);
        Assert.True(stationary.Depth.Count(v => v < 1) > off.Depth.Count(v => v < 1) + 30);
        using var renderer = new D3D12OriginalStyleRenderer();
        var color = renderer.Render(wide, Size, Size);
        int border = 0;
        for (int i = 0; i < stationary.Depth.Length; i++)
        {
            int offset = i * 4;
            if (color.Rgba[offset] != 255 || color.Rgba[offset + 1] != 0 || color.Rgba[offset + 2] != 0) continue;
            border++;
            Assert.True(stationary.Depth[i] < 1, "A visible modern outline pixel must supply device depth.");
            Assert.Equal(Vector2.Zero, stationary.Motion[i]);
            Assert.Equal(0, stationary.Reactive[i]);
        }
        Assert.True(border > 30);
        var animated = Capture(runtime, pass, wide, thin);
        expected = Capture(runtime, pass, EffectiveSource(wide), EffectiveSource(thin));
        AssertBiasedOutlineDepth(wide, expected.Depth, animated.Depth);
        Assert.Equal(expected.Motion, animated.Motion);
        Assert.Equal(expected.Reactive, animated.Reactive);
        Assert.True(animated.Motion.Count(v => v.LengthSquared() > .001f) > 30,
            "Changing only modern width must use the previous expanded positions for geometric motion.");
        var disabled = Styled(source, new(2, Red, false));
        var disabledInputs = Capture(runtime, pass, disabled, disabled);
        Assert.Equal(off.Depth, disabledInputs.Depth);
        Assert.Equal(off.Motion, disabledInputs.Motion);
        Assert.Equal(off.Reactive, disabledInputs.Reactive);
        Assert.Equal(0, runtime.Readbacks); // The test's explicit resource copies are not ordinary Render readbacks.
    }

    [Fact]
    public void ModernWidthNeverChangesGroundProjectionDepthMotionOrReactiveInputs()
    {
        using var runtime = new D3D12GpuRuntime();
        using var pass = new D3D12TemporalInputPass(runtime);
        var box = Box();
        var ground = new OriginalFrameScene(box.Surface, box.Outline, box.OutlineOptions,
            box.Options! with { CameraOverride = box.Surface.Snapshot.Camera with { Eye = new(3, 3, -5) },
                GroundShadow = new(new(.3f, -1, .2f), new(0, 1, 0, 1), false, false) },
            [new(false, false, true, SurfaceEnabled: false)]);
        var baseline = Capture(runtime, pass, ground, ground);
        Assert.True(baseline.Depth.Count(v => v < 1) > 20);
        var changed = Styled(ground, new(12, Red, false));
        var modern = Capture(runtime, pass, changed, Styled(ground, new(.25f, Red, false)));
        Assert.Equal(baseline.Depth, modern.Depth);
        Assert.Equal(baseline.Motion, modern.Motion);
        Assert.Equal(baseline.Reactive, modern.Reactive);
    }

    [Theory]
    [InlineData(D3D12TemporalUpscalingMode.Fsr)]
    [InlineData(D3D12TemporalUpscalingMode.XeSs)]
    public void ActualNativeAaRetainsModernBorderAndStyleChangesResetSubmittedHistory(D3D12TemporalUpscalingMode mode)
    {
        using var session = new D3D12FrameSession();
        var box = Box();
        var surface = new OriginalStyleScene(box.Surface.Snapshot.WithCamera(
            box.Surface.Snapshot.Camera with { VerticalFovDegrees = 45 }), box.Surface.Materials, box.Surface.Profile);
        var source = new OriginalFrameScene(surface, box.Outline, box.OutlineOptions, box.Options, box.Materials);
        var selected = Styled(source, new(2, Red));
        string variable = mode == D3D12TemporalUpscalingMode.Fsr ? "MMD_FSR_LOADER_PATH" : "MMD_XESS_RUNTIME_PATH";
        var options = new D3D12TemporalUpscalingOptions { Mode = mode,
            QualityPreset = D3D12FsrQualityPreset.NativeAa, XeSsQuality = D3D12XeSsQuality.AntiAliasing,
            LoaderPath = Environment.GetEnvironmentVariable(variable) ?? throw new InvalidOperationException($"Set {variable} for actual SDK tests.") };
        const int width = 96, height = 72;
        for (int i = 0; i < 4; i++)
        {
            using var gpu = session.Render(Styled(source, new(2, Red)), width, height, temporalUpscaling: options);
            Assert.Equal(i == 0, gpu.Statistics.TemporalUpscaling!.ResetHistory);
            Assert.Equal(0, gpu.Statistics.ReadbackCount);
        }
        var reconstructed = session.Capture(selected, width, height, temporalUpscaling: options);
        Assert.True(Enumerable.Range(0, width * height).Count(i => reconstructed.Rgba[i * 4] > 150
            && reconstructed.Rgba[i * 4 + 1] < 70 && reconstructed.Rgba[i * 4 + 2] < 70) > 10,
            "Actual vendor reconstruction must retain visible red hull pixels.");
        long reads = session.Runtime.Readbacks;
        foreach (var style in new CharacterOutlineStyle?[] { new(1, Red), new(1, new(0, 0, 1, 1)), new(1, Red, false), null })
        {
            var changed = Styled(source, style);
            using (var gpu = session.Render(changed, width, height, temporalUpscaling: options))
            {
                Assert.Equal("SceneStructureChanged", gpu.Statistics.TemporalUpscaling!.ResetReason);
                Assert.Equal(0, gpu.Statistics.ReadbackCount);
            }
            using var stable = session.Render(Styled(source, style is null ? null : style with { }), width, height, temporalUpscaling: options);
            Assert.False(stable.Statistics.TemporalUpscaling!.ResetHistory);
            Assert.Equal(reads, session.Runtime.Readbacks);
        }
    }

    [Fact]
    public void StraightAlphaValidatesTheEffectiveOverrideRatherThanTheUnusedAuthorColor()
    {
        using var session = new D3D12FrameSession();
        var box = Box();
        var invalid = new OriginalOutlineData(box.Outline!.Vertices,
            [box.Outline.Materials[0] with { DrawEnabled = true, Color = new(1, 0, 0, 2) }]);
        var source = new OriginalFrameScene(box.Surface, invalid, box.OutlineOptions,
            box.Options! with { AlphaOutput = FrameAlphaOutput.Straight, BackgroundColor = Vector4.Zero }, box.Materials);
        Assert.Throws<NotSupportedException>(() => session.Capture(source, Size, Size));
        var valid = session.Capture(Styled(source, new(2, Red)), Size, Size);
        Assert.True(Enumerable.Range(0, Size * Size).Count(i => valid.Rgba[i * 4] == 255
            && valid.Rgba[i * 4 + 1] == 0 && valid.Rgba[i * 4 + 2] == 0) > 20);
        var disabled = session.Capture(Styled(source, new(2, Red, false)), Size, Size);
        Assert.Equal(session.Capture(new OriginalFrameScene(source.Surface, options: source.Options), Size, Size).Rgba, disabled.Rgba);
    }

    private static OriginalFrameScene Box()
    {
        var b = D3D12OriginalOutlineTests.Box();
        // Keep all outline vertices in one D32 exponent bin for the independent bias oracle.
        var surface = new OriginalStyleScene(b.Surface.Snapshot.WithCamera(
            b.Surface.Snapshot.Camera with { Far = 30 }), b.Surface.Materials, b.Surface.Profile);
        return new(surface, new(b.Outline!.Vertices, [b.Outline.Materials[0] with { DrawEnabled = false }]),
            b.OutlineOptions, D3D12OriginalFrameTests.Options());
    }

    private static OriginalFrameScene Styled(OriginalFrameScene source, CharacterOutlineStyle? style) =>
        new(source.Surface, source.Outline, source.OutlineOptions, source.Options,
            source.Materials.Select(flags => flags with { CharacterOutlineStyle = style }));

    // Independently resolve a source snapshot, then exercise the unchanged null-policy producer.
    private static OriginalFrameScene EffectiveSource(OriginalFrameScene frame) => new(frame.Surface,
        new OriginalOutlineData(frame.Outline!.Vertices, frame.Outline.Materials.Select((edge, i) =>
            CharacterOutlineMath.ResolveMaterial(edge, frame.Materials[i].CharacterOutlineStyle))), frame.OutlineOptions,
        frame.Options, frame.Materials.Select(flags => flags with { CharacterOutlineStyle = null }));

    private static void AssertBiasedOutlineDepth(OriginalFrameScene frame, float[] legacy, float[] modern)
    {
        // Microsoft depth-bias contract: D32 bias = 16 * 2^(exponent(max primitive z)-23),
        // applied to vertices before interpolation. This fixture lies within one exponent
        // bin, so every triangle has the same independently calculable bias. The legacy
        // color control identifies actual contour coverage without using the producer's PSO.
        // https://learn.microsoft.com/en-us/windows/win32/direct3d11/d3d10-graphics-programming-guide-output-merger-stage-depth-bias
        var control = EffectiveSource(frame);
        var transform = frame.Surface.Snapshot.World * (frame.Options?.CameraOverride
            ?? frame.Surface.Snapshot.Camera).ViewProjection(Size, Size);
        var clipDepth = OriginalOutlineMath.BuildPositions(control.Outline!, control.OutlineOptions!)
            .Select(p => Vector4.Transform(p, transform)).Select(p => p.Z / p.W).ToArray();
        int exponent = MathF.ILogB(clipDepth.Max());
        Assert.Equal(exponent, MathF.ILogB(clipDepth.Min()));
        float bias = 16 * MathF.ScaleB(1, exponent - 23);
        using var renderer = new D3D12OriginalStyleRenderer();
        var color = renderer.Render(control, Size, Size);
        int biased = 0;
        for (int i = 0; i < legacy.Length; i++)
        {
            int offset = i * 4;
            if (color.Rgba[offset] == 255 && color.Rgba[offset + 1] == 0 && color.Rgba[offset + 2] == 0)
            {
                float expected = legacy[i] + bias;
                // A constant offset added before rather than after interpolation can
                // round once differently. Bound this to exactly one binary32 depth ULP.
                Assert.InRange(modern[i], MathF.BitDecrement(expected), MathF.BitIncrement(expected));
                Assert.True(modern[i] > legacy[i]);
                biased++;
            }
            else Assert.Equal(legacy[i], modern[i]);
        }
        Assert.True(biased > 30);
    }

    private static (float[] Depth, Vector2[] Motion, byte[] Reactive) Capture(D3D12GpuRuntime runtime,
        D3D12TemporalInputPass pass, OriginalFrameScene current, OriginalFrameScene previous)
    {
        var context = runtime.Begin();
        try
        {
            var inputs = pass.Record(context, current, previous, Size, Size);
            var depth = Copy(inputs.Depth); var motion = Copy(inputs.MotionVectors); var reactive = Copy(inputs.ReactiveMask!);
            runtime.Wait(runtime.Submit());
            return (Read<float>(depth, 4), Read<Vector2>(motion, 8), Read<byte>(reactive, 1));

            (ID3D12Resource Resource, PlacedSubresourceFootPrint Footprint, int Bytes) Copy(ID3D12Resource source)
            {
                var layout = new PlacedSubresourceFootPrint[1];
                runtime.Device.GetCopyableFootprints(source.Description, 0, 1, 0, layout, new int[1], new ulong[1], out ulong size);
                var buffer = context.Own(runtime.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(size), ResourceStates.CopyDest));
                context.Commands.ResourceBarrierTransition(source, D3D12TemporalInputs.ShaderReadState, ResourceStates.CopySource);
                context.Commands.CopyTextureRegion(new TextureCopyLocation(buffer, layout[0]), 0, 0, 0, new TextureCopyLocation(source, 0));
                return (buffer, layout[0], checked((int)size));
            }

            T[] Read<T>((ID3D12Resource Resource, PlacedSubresourceFootPrint Footprint, int Bytes) source, int stride) where T : unmanaged
            {
                var output = new T[Size * Size]; var data = source.Resource.Map<byte>(0, source.Bytes);
                try
                {
                    for (int y = 0; y < Size; y++) MemoryMarshal.Cast<byte, T>(data.Slice(y * source.Footprint.Footprint.RowPitch,
                        Size * stride)).CopyTo(output.AsSpan(y * Size, Size));
                }
                finally { source.Resource.Unmap(0); }
                return output;
            }
        }
        finally { runtime.Abort(); }
    }
}
