using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class ViewOutputPresetD3D12Tests : IDisposable
{
    private readonly string cacheRoot = Path.GetFullPath(
        Environment.GetEnvironmentVariable("MMD_VIEW_OUTPUT_PRESET_TEST_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "view-output-preset-gpu-tests"));
    private readonly string directory;

    public ViewOutputPresetD3D12Tests()
        => directory = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReopenedPreviewAndExportPresetsExecuteIndependentHardwareQualityAndKeepSourceSettings()
    {
        var token = TestContext.Current.CancellationToken;
        var box = D3D12OriginalOutlineTests.Box();
        var sourceOptions = D3D12OriginalFrameTests.Options() with
        {
            Antialiasing = FrameAntialiasing.Off, SmaaQuality = SmaaQuality.Low, SpatialSamples = 1
        };
        var scene = new OriginalFrameScene(box.Surface, box.Outline, box.OutlineOptions, sourceOptions,
            box.Materials, box.Lighting, box.EditorOverlay);
        var preview = new ViewRenderRequest(97, 73, new(Antialiasing: FrameAntialiasing.Smaa1X,
            SmaaQuality: SmaaQuality.High, SpatialSamples: 2));
        var export = new ViewExportPreset(193, 145, new(60000, 1001),
            new(Antialiasing: FrameAntialiasing.Smaa1X, SmaaQuality: SmaaQuality.Ultra, SpatialSamples: 4));
        var saved = new ViewOutputPresetStore(directory).SaveAsNew("Independent hardware quality", preview,
            export, cancellationToken: token);
        var reopened = new ViewOutputPresetStore(directory).Find(saved.StableId, token);
        Assert.Equal(ViewOutputPresetStatus.Available, reopened.Status);
        var definition = Assert.IsType<ViewOutputPresetDefinition>(reopened.Definition);
        Assert.Equal(preview, definition.Preview);
        Assert.Equal(export, definition.Export);
        var exportRequest = definition.Export.CreateRequest(12, 12, 4);
        Assert.Equal(new OfflineFrameRate(60000, 1001), exportRequest.Plan.FrameRate);
        Assert.Equal(4u, exportRequest.Plan.PhysicsPreRollFrames);
        var sample = exportRequest.Plan.GetSample(0);

        // Configure the direct renderer by hand so the reference does not reuse preset or quality mapping.
        OriginalFrameScene DirectScene(FrameAntialiasing antialiasing, SmaaQuality quality, int samples)
            => new(scene.Surface, scene.Outline, scene.OutlineOptions, sourceOptions with
            {
                RayGi = RayGiOptions.Off, Antialiasing = antialiasing,
                SmaaQuality = quality, SpatialSamples = samples
            }, scene.Materials, scene.Lighting, scene.EditorOverlay);

        async Task<byte[]> Capture(ViewRenderRequest request)
        {
            token.ThrowIfCancellationRequested();
            await using var renderer = new D3D12ViewRenderer(request);
            Assert.False(renderer.IsWarp);
            using var output = await renderer.CaptureAsync(
                new(sample, request.Width, request.Height, scene, null, null, null), token);
            Assert.Equal(request.Width, output.Width);
            Assert.Equal(request.Height, output.Height);
            Assert.Equal(request.Width * 4, output.Stride);
            Assert.Equal(request.Width * request.Height * 4, output.Pixels.Length);
            return output.Pixels.ToArray();
        }

        using var direct = new D3D12FrameSession();
        Assert.False(direct.IsWarp);
        var previewPixels = await Capture(definition.Preview);
        var expectedPreview = direct.Capture(DirectScene(FrameAntialiasing.Smaa1X, SmaaQuality.High, 2), 97, 73);
        Assert.Equal(expectedPreview.Rgba, previewPixels);
        var inheritedPreview = direct.Capture(DirectScene(FrameAntialiasing.Off, SmaaQuality.Low, 1), 97, 73);
        Assert.False(inheritedPreview.Rgba.AsSpan().SequenceEqual(previewPixels),
            "The fixture must visibly exercise the saved preview AA overrides.");
        var finalPixels = await Capture(exportRequest.Render);
        var expectedFinal = direct.Capture(DirectScene(FrameAntialiasing.Smaa1X, SmaaQuality.Ultra, 4), 193, 145);
        Assert.Equal(expectedFinal.Rgba, finalPixels);

        // Reset only preview quality, save and reopen again, then execute both resulting configurations.
        var defaultPreview = definition.Preview with { Quality = null };
        var reset = new ViewOutputPresetStore(directory).SaveAsNew("Inherited preview quality", defaultPreview,
            definition.Export, cancellationToken: token);
        var resetResult = new ViewOutputPresetStore(directory).Find(reset.StableId, token);
        Assert.Equal(ViewOutputPresetStatus.Available, resetResult.Status);
        var resetDefinition = Assert.IsType<ViewOutputPresetDefinition>(resetResult.Definition);
        Assert.Null(resetDefinition.Preview.Quality);
        Assert.Equal(export, resetDefinition.Export);
        Assert.Equal(inheritedPreview.Rgba, await Capture(resetDefinition.Preview));
        Assert.Equal(finalPixels, await Capture(resetDefinition.Export.CreateRequest(12, 12, 4).Render));

        Assert.Same(sourceOptions, scene.Options);
        Assert.Equal(FrameAntialiasing.Off, sourceOptions.Antialiasing);
        Assert.Equal(SmaaQuality.Low, sourceOptions.SmaaQuality);
        Assert.Equal(1, sourceOptions.SpatialSamples);
        Assert.Equal(preview, definition.Preview);
        Assert.Equal(export, definition.Export);
    }

    public void Dispose()
    {
        string target = Path.GetFullPath(directory);
        if (!target.StartsWith(Path.TrimEndingDirectorySeparator(cacheRoot) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GPU preset test cleanup must stay within its test cache.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
