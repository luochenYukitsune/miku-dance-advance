using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12DiffusionViewTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void MappingPreservesAllIndependentValuesAndValidatesEvenWhenDisabled()
    {
        var quality = new ViewQualityOptions(Diffusion: new(true, .37f, 0));
        var mapped = D3D12ViewQuality.Diffusion(quality);
        Assert.True(mapped.Enabled); Assert.Equal(.37f, mapped.Strength); Assert.Equal(0, mapped.Extent);
        Assert.False(D3D12ViewQuality.Diffusion(new()).Enabled);
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D12ViewQuality.Diffusion(new(Diffusion: new(false, float.NaN))));
    }

    [Theory]
    [InlineData(false, .7f, false, true)]
    [InlineData(true, 0f, false, true)]
    [InlineData(true, .7f, false, false)]
    [InlineData(true, .7f, true, true)]
    public void PreflightRejectsOnlyActiveTransparentInput(bool enabled, float strength, bool straight, bool accepts)
    {
        var source = OpaqueScene();
        var scene = new OriginalFrameScene(source.Surface, source.Outline, source.OutlineOptions,
            source.Options! with { BackgroundColor = Vector4.Zero,
                AlphaOutput = straight ? FrameAlphaOutput.Straight : source.Options.AlphaOutput },
            source.Materials, source.Lighting, source.EditorOverlay);
        var quality = new ViewQualityOptions(Diffusion: new(enabled, strength, 0));
        if (accepts) D3D12ViewQuality.ValidateDiffusion(scene, quality);
        else Assert.Throws<NotSupportedException>(() => D3D12ViewQuality.ValidateDiffusion(scene, quality));
        D3D12ViewQuality.ValidateDiffusion(source, quality);
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D12ViewQuality.ValidateDiffusion(source,
            new(Diffusion: new(false, float.NaN))));
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task CaptureMatchesDirectNativeFilterAndAllBypassModes(int mode)
    {
        ViewDiffusionOptions? options = mode switch
        {
            0 => null, 1 => new(false, .7f, .002f), 2 => new(true, 0, .002f), _ => new(true, .7f, .01f)
        };
        var request = new ViewRenderRequest(97, 73, new(Diffusion: options));
        var scene = OpaqueScene();
        var sample = new OfflineFramePlan(97, 73, new(30), 0, 0).GetSample(0);
        await using var view = new D3D12ViewRenderer(request);
        Assert.False(view.IsWarp);
        using var image = await view.CaptureAsync(new(sample, 97, 73, scene, null, null, null), Token);
        using var direct = new D3D12FrameSession();
        var resolved = D3D12ViewScene.Resolve(scene, null, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), request.EffectiveQuality);
        var expected = direct.Capture(resolved, 97, 73,
            diffusion: options is null ? null : new(options.Enabled, options.Strength, options.Extent));
        Assert.Equal(expected.Rgba, image.Pixels.ToArray());
        var baseline = direct.Capture(resolved, 97, 73);
        if (mode == 3) Assert.False(baseline.Rgba.AsSpan().SequenceEqual(image.Pixels.Span));
        else Assert.Equal(baseline.Rgba, image.Pixels.ToArray());
        Assert.Null(scene.Options!.RayGi);
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task ActiveTransparentCapturePreservesCoverageAndOpaqueRetryWorks()
    {
        var request = new ViewRenderRequest(97, 73, new(Diffusion: new(true)));
        var scene = OpaqueScene();
        var sample = new OfflineFramePlan(97, 73, new(30), 0, 0).GetSample(0);
        await using var view = new D3D12ViewRenderer(request);
        var transparent = new OriginalFrameScene(scene.Surface, scene.Outline, scene.OutlineOptions,
            scene.Options! with { BackgroundColor = Vector4.Zero }, scene.Materials, scene.Lighting, scene.EditorOverlay);
        var frame = new OfflineFrame(sample, 97, 73, transparent, null, null, null);
        using (var image = await view.CaptureAsync(frame, Token))
        {
            Assert.Equal(FrameAlphaMode.Straight, image.Alpha);
            Assert.Contains(Enumerable.Range(0, 97 * 73), p => image.Pixels.Span[p * 4 + 3] == 0);
        }
        using var actual = await view.CaptureAsync(frame with { Scene = scene }, Token);
        await using var fresh = new D3D12ViewRenderer(request);
        using var expected = await fresh.CaptureAsync(frame with { Scene = scene }, Token);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task RealtimePresentUsesFilteredGpuOutputWithoutCaptureAndSurvivesResize()
    {
        nint window = CreateWindowExW(0, "STATIC", "MDA native diffusion test", 0x80000000, 0, 0, 160, 128, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            await using var view = new D3D12RealtimeViewRenderer(window, new(97, 73, new(Diffusion: new(true, .7f, .01f))));
            var scene = OpaqueScene();
            await view.PresentAsync(new(97, 73, 0, scene, null, null, null), Token);
            Assert.Equal(1, view.Statistics.PresentCount);
            await view.ResizeAsync(113, 81, Token);
            await view.PresentAsync(new(113, 81, 1, scene, null, null, null), Token);
            Assert.Equal(2, view.Statistics.PresentCount);
            Assert.Equal(0, view.Statistics.ExplicitCaptureCount);
            Assert.Equal(0, view.Statistics.Frame!.TotalReadbacks);
        }
        finally { DestroyWindow(window); }
    }

    private static OriginalFrameScene OpaqueScene()
    {
        var box = D3D12OriginalOutlineTests.Box();
        return new(box.Surface, box.Outline, box.OutlineOptions,
            D3D12OriginalFrameTests.Options() with { BackgroundColor = new(.02f, .04f, .08f, 1) },
            box.Materials, box.Lighting, box.EditorOverlay);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extended, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
