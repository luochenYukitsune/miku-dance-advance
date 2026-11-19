using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class GiPreviewAccelerationContractTests
{
    [Fact]
    public void PreviewAccelerationDefaultsOffAndStrippedEverywhereExceptRealtime()
    {
        var defaults = new RayGiOptions();
        Assert.False(defaults.PreviewAcceleration);
        Assert.False(RayGiOptions.Off.PreviewAcceleration);

        foreach (var denoiser in new[] { RayGiDenoiser.Off, RayGiDenoiser.Spatial, RayGiDenoiser.TemporalSpatial })
        {
            var composition = denoiser == RayGiDenoiser.Off ? RayGiComposition.Additive : RayGiComposition.ToonPalette;
            var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: 4, SamplesPerPixel: 16,
                Composition: composition, Denoiser: denoiser) { PreviewAcceleration = true };
            var quality = new ViewQualityOptions(Gi: gi);
            var state = new ViewTemporalFrameState { HistoryGeneration = 7, CameraCutKey = 3, ResetHistory = true };

            // Default (export/capture) MUST strip PreviewAcceleration to false.
            var exportGi = D3D12ViewQuality.Gi(quality, state);
            Assert.False(exportGi.PreviewAcceleration);
            Assert.False(D3D12ViewQuality.Gi(quality, state, allowPreviewAcceleration: false).PreviewAcceleration);

            // Only explicit allowPreviewAcceleration: true preserves PreviewAcceleration.
            var realtimeGi = D3D12ViewQuality.Gi(quality, state, allowPreviewAcceleration: true);
            Assert.True(realtimeGi.PreviewAcceleration);

            // App export preparation also strips PreviewAcceleration.
            var exportRequest = new ExportRequest(new OfflineFramePlan(128, 72, new(30), 0, 0), quality);
            var built = EditorModernRenderEngine.BuildRenderRequest(exportRequest, hasPaletteReceiver: false);
            Assert.False(built.EffectiveQuality.EffectiveGi.PreviewAcceleration);
        }

        // Legacy mode with PreviewAcceleration=true is also stripped by default.
        var legacyQuality = new ViewQualityOptions(Gi: new RayGiOptions(LightingMode.Legacy) { PreviewAcceleration = true });
        Assert.False(D3D12ViewQuality.Gi(legacyQuality).PreviewAcceleration);
        Assert.True(D3D12ViewQuality.Gi(legacyQuality, allowPreviewAcceleration: true).PreviewAcceleration);
    }

    [Fact]
    public void HalfResDimensionsAndShadersCompileWithoutGpu()
    {
        Assert.Equal((64, 36), D3D12RayGiHalfResPass.HalfDimensions(128, 72));
        Assert.Equal((65, 49), D3D12RayGiHalfResPass.HalfDimensions(129, 97));
        Assert.Equal((1, 1), D3D12RayGiHalfResPass.HalfDimensions(2, 2));

        var compiler = new DxcShaderCompiler();
        foreach (var req in D3D12RayGiHalfResPass.GetPrewarmRequests())
        {
            var compiled = compiler.CompileCompute(req.Source, req.EntryPoint, req.Profile, req.Defines);
            Assert.NotEmpty(compiled.Dxil);
        }
    }
}

[Collection(QualityLoadingCollection.Name)]
public sealed class PreviewGiAccelerationSettingsTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PreviewSettingsDefaultsOffBindsCheckBoxSwitchesLanguageAndPersists() => await session.Dispatch(() =>
    {
        Assert.False(EditorPreferences.DefaultPreviewQuality.EffectiveGi.PreviewAcceleration);

        var defaultVm = new EditorPreviewSettingsViewModel();
        Assert.False(defaultVm.PreviewGiAcceleration);
        Assert.True(defaultVm.TryCreate(out var defaultQuality));
        Assert.False(defaultQuality!.EffectiveGi.PreviewAcceleration);

        var caps = new EditorPreviewCapabilities(
            Gi: new(false, true, 65, 11, true, true, true, true, true, null),
            SpatialUpscalingAvailable: true);
        var vm = new EditorPreviewSettingsViewModel(defaultQuality, caps);
        var dialog = new EditorPreviewSettingsWindow(vm);
        dialog.Show();
        try
        {
            var modeBox = Assert.IsType<ComboBox>(dialog.FindControl<ComboBox>("PreviewGiModeBox"));
            var accelBox = Assert.IsType<CheckBox>(dialog.FindControl<CheckBox>("PreviewGiAccelerationBox"));
            Assert.False(accelBox.IsChecked);
            Assert.False(accelBox.IsEnabled);

            modeBox.SelectedIndex = 1; // SoftwareRayTracing
            Dispatcher.UIThread.RunJobs();
            Assert.True(accelBox.IsEnabled);

            accelBox.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.PreviewGiAcceleration);

            string languageBefore = EditorLocalization.CurrentLanguage;
            try
            {
                foreach (string language in new[] { "en", "ja", "zh-CN" })
                {
                    EditorLocalization.SetLanguage(language);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(accelBox.IsChecked);
                    Assert.DoesNotContain("Mda.Text.", vm.PreviewGiAccelerationLabel);
                    Assert.DoesNotContain("Mda.Text.", vm.PreviewGiAccelerationHint);
                    Assert.False(string.IsNullOrWhiteSpace(vm.PreviewGiAccelerationLabel));
                    Assert.False(string.IsNullOrWhiteSpace(vm.PreviewGiAccelerationHint));
                }
            }
            finally { EditorLocalization.SetLanguage(languageBefore); }

            Assert.True(vm.TryCreate(out var applied), vm.ValidationMessage);
            Assert.True(applied!.EffectiveGi.PreviewAcceleration);
            Assert.Equal(LightingMode.SoftwareRayTracing, applied.EffectiveGi.Mode);
            Assert.True(new EditorPreviewSettingsViewModel(applied, caps).PreviewGiAcceleration);

            string path = Path.Combine(Path.GetTempPath(), "mda-gi-preview-accel-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                EditorPreferences.SavePreviewQuality(applied, path);
                var loaded = EditorPreferences.LoadPreviewQuality(path);
                Assert.True(loaded.EffectiveGi.PreviewAcceleration);
                Assert.Equal(LightingMode.SoftwareRayTracing, loaded.EffectiveGi.Mode);

                // Legacy preset without previewAcceleration loads with false.
                var json = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
                json["preview"]!["quality"]!["gi"]!.AsObject().Remove("previewAcceleration");
                File.WriteAllText(path, json.ToJsonString(), new UTF8Encoding(false));
                var legacyLoaded = EditorPreferences.LoadPreviewQuality(path);
                Assert.False(legacyLoaded.EffectiveGi.PreviewAcceleration);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}

[Trait("Category", "Gpu")]
public sealed class D3D12GiPreviewAccelerationGpuTests
{
    private const int Width = 129, Height = 97;

    [Fact]
    public void HalfResUpsamplePreservesConstantAndRejectsDepthAndCornerBleeding()
    {
        using var runtime = new D3D12GpuRuntime();
        Assert.False(runtime.IsWarp);
        using var pass = new D3D12RayGiHalfResPass(runtime);
        var compiler = runtime.GetOrCreateDxcCompiler();

        const int fw = 16, fh = 16;
        var (hw, hh) = D3D12RayGiHalfResPass.HalfDimensions(fw, fh);
        Assert.Equal((8, 8), (hw, hh));

        var fullPos = new Vector4[fw * fh];
        var fullNorm = new Vector4[fw * fh];
        var halfSignal = new Vector4[hw * hh];

        // Left half: foreground z=0, normal (0,0,-1), bright red signal (0.8, 0.1, 0.05, 1.0)
        // Right half: background z=0.5 (different depth) OR perpendicular normal (1,0,0), zero signal (0, 0, 0, 1.0)
        for (int y = 0; y < fh; y++)
        {
            for (int x = 0; x < fw; x++)
            {
                int i = y * fw + x;
                bool left = x < fw / 2;
                fullPos[i] = left ? new Vector4(x - 8, y - 8, 0f, 0.3f) : new Vector4(x - 8, y - 8, 5.0f, 0.8f);
                fullNorm[i] = left ? new Vector4(0, 0, -1, 1) : new Vector4(1, 0, 0, 2);
            }
        }
        for (int y = 0; y < hh; y++)
        {
            for (int x = 0; x < hw; x++)
            {
                bool left = x < hw / 2;
                halfSignal[y * hw + x] = left ? new Vector4(0.8f, 0.1f, 0.05f, 1.0f) : new Vector4(0f, 0f, 0f, 1.0f);
            }
        }

        var output = RunHalfResPass(runtime, pass, compiler, fw, fh, fullPos, fullNorm, halfSignal);
        for (int y = 0; y < fh; y++)
        {
            for (int x = 0; x < fw; x++)
            {
                var v = output[y * fw + x];
                Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W));
                if (x < fw / 2)
                {
                    Assert.InRange(v.X, 0.79f, 0.81f);
                    Assert.InRange(v.Y, 0.09f, 0.11f);
                    Assert.InRange(v.W, 0.99f, 1.0f);
                }
                else
                {
                    // Must not bleed red signal across the depth + normal discontinuity.
                    Assert.InRange(v.X, 0f, 0.001f);
                    Assert.InRange(v.Y, 0f, 0.001f);
                    Assert.InRange(v.W, 0.99f, 1.0f);
                }
            }
        }
    }

    [Fact]
    public void OffAndTinyDimensionsMatchBaselineByteForByte()
    {
        var frame = Scene();
        var defaultOptions = new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: 0.5f, SamplesPerPixel: 8,
            Seed: 17, MaxRayDistance: 50, RayEpsilon: 0.001f, Composition: RayGiComposition.ToonPalette,
            Denoiser: RayGiDenoiser.TemporalSpatial);
        Assert.False(defaultOptions.PreviewAcceleration);
        var explicitOff = defaultOptions with { PreviewAcceleration = false };

        using var sessionA = new D3D12FrameSession();
        using var sessionB = new D3D12FrameSession();
        for (int i = 0; i < 2; i++)
        {
            var baseline = RenderViaPresentWithReadback(sessionA, frame, Width, Height, defaultOptions);
            var offPixels = RenderViaPresentWithReadback(sessionB, frame, Width, Height, explicitOff);
            Assert.Equal(baseline, offPixels);
            Assert.Equal(8, sessionB.RayGiStatistics!.SamplesPerPixel);
            Assert.Equal((long)Width * Height * 64, sessionB.RayGiStatistics.TemporalHistoryBytes);
        }

        // width < 2 || height < 2 must keep original full-res path even when PreviewAcceleration=true.
        using var tinyA = new D3D12FrameSession();
        using var tinyB = new D3D12FrameSession();
        var tinyOff = RenderViaPresentWithReadback(tinyA, frame, 1, 16, explicitOff);
        var tinyOn = RenderViaPresentWithReadback(tinyB, frame, 1, 16, explicitOff with { PreviewAcceleration = true });
        Assert.Equal(tinyOff, tinyOn);
        Assert.Equal(8, tinyB.RayGiStatistics!.SamplesPerPixel);
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public async Task RealtimePreviewAccelerationRendersMultiFrameWithSppCapShortHistoryAndZeroErrors(LightingMode mode)
    {
        nint window = CreateWindowExW(0, "STATIC", "GI preview acceleration headless test", 0x80000000,
            0, 0, 128, 96, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            var gi = new RayGiOptions(mode, Strength: 2.0f, SamplesPerPixel: 16, Seed: 31,
                MaxRayDistance: 50, RayEpsilon: 0.001f, Composition: RayGiComposition.ToonPalette,
                Denoiser: RayGiDenoiser.TemporalSpatial) { PreviewAcceleration = true };
            var request = new ViewRenderRequest(Width, Height,
                new(Gi: gi, Antialiasing: FrameAntialiasing.Off, SpatialSamples: 1));
            var scene = Scene();
            var token = TestContext.Current.CancellationToken;
            var (halfW, halfH) = D3D12RayGiHalfResPass.HalfDimensions(Width, Height);

            await using var preview = new D3D12RealtimeViewRenderer(window, request);
            var frame0 = new PreviewFrame(Width, Height, 0, scene, null, null, null);
            await preview.PresentAsync(frame0, token);
            Assert.False(preview.IsWarp);
            Assert.NotNull(preview.Statistics.RayGi);
            Assert.True(preview.Statistics.RayGi!.TemporalResetHistory);
            Assert.Equal("FirstFrame", preview.Statistics.RayGi.TemporalResetReason);
            Assert.Equal(2, preview.Statistics.RayGi.SamplesPerPixel);
            Assert.Equal((long)halfW * halfH * 64, preview.Statistics.RayGi.TemporalHistoryBytes);

            // Normal accumulation
            await preview.PresentAsync(frame0, token);
            Assert.False(preview.Statistics.RayGi!.TemporalResetHistory);
            Assert.Equal(2UL, preview.Statistics.RayGi.TemporalHistoryFrames);

            // Camera rotation + FOV change must reproject without full-frame reset
            var baseCamera = scene.Surface.Snapshot.Camera;
            var orbitedCamera = baseCamera with
            {
                Eye = baseCamera.Eye + new Vector3(0.25f, 0.1f, 0.0f),
                VerticalFovDegrees = 60f
            };
            var zoomedScene = new OriginalFrameScene(scene.Surface, scene.Outline, scene.OutlineOptions,
                scene.Options! with { CameraOverride = orbitedCamera }, scene.Materials, scene.Lighting);
            await preview.PresentAsync(frame0 with { Scene = zoomedScene }, token);
            Assert.False(preview.Statistics.RayGi!.TemporalResetHistory);
            Assert.Equal("None", preview.Statistics.RayGi.TemporalResetReason);
            Assert.Equal(3UL, preview.Statistics.RayGi.TemporalHistoryFrames);

            // Light direction change & material color change must keep history (short-history reprojection)
            var changedMaterials = zoomedScene.Surface.Materials.ToArray();
            var changedSnapshot = zoomedScene.Surface.Snapshot.WithLighting(Vector3.Normalize(new Vector3(-0.3f, 0.8f, 0.4f)));
            var litChangedScene = new OriginalFrameScene(
                new OriginalStyleScene(changedSnapshot, changedMaterials, zoomedScene.Surface.Profile),
                zoomedScene.Outline, zoomedScene.OutlineOptions, zoomedScene.Options, zoomedScene.Materials, zoomedScene.Lighting);
            await preview.PresentAsync(frame0 with { Scene = litChangedScene }, token);
            Assert.False(preview.Statistics.RayGi!.TemporalResetHistory);
            Assert.Equal(4UL, preview.Statistics.RayGi.TemporalHistoryFrames);

            // True structural topology change must reset
            var extraVerts = litChangedScene.Surface.Snapshot.Vertices.ToArray();
            var mats = litChangedScene.Surface.Snapshot.Materials;
            var truncatedSnapshot = new SceneSnapshot(extraVerts, [0, 2, 1, 0, 3, 2, 4, 6, 5],
                [mats[0], mats[1] with { StartIndex = 6, IndexCount = 3 }],
                camera: litChangedScene.Surface.Snapshot.Camera, lightDirection: litChangedScene.Surface.Snapshot.LightDirection);
            var topoScene = new OriginalFrameScene(
                new OriginalStyleScene(truncatedSnapshot, changedMaterials, litChangedScene.Surface.Profile),
                litChangedScene.Outline, litChangedScene.OutlineOptions, litChangedScene.Options,
                litChangedScene.Materials, litChangedScene.Lighting);
            await preview.PresentAsync(frame0 with { Scene = topoScene }, token);
            Assert.True(preview.Statistics.RayGi!.TemporalResetHistory);
            Assert.Equal("TopologyChanged", preview.Statistics.RayGi.TemporalResetReason);
            Assert.Equal(1UL, preview.Statistics.RayGi.TemporalHistoryFrames);
        }
        finally { DestroyWindow(window); }
    }

    [Fact]
    public void RealtimePreviewReadbackIsValidNonBlackFiniteAndSpatialWithoutTemporalCapsSppToFour()
    {
        using var session = new D3D12FrameSession();
        var frame = Scene();
        var noGi = RenderViaPresentWithReadback(session, frame, Width, Height, RayGiOptions.Off);

        var spatialAccel = new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: 2.0f, SamplesPerPixel: 16,
            Seed: 19, MaxRayDistance: 50, RayEpsilon: 0.001f, Composition: RayGiComposition.ToonPalette,
            Denoiser: RayGiDenoiser.Spatial) { PreviewAcceleration = true };
        var spatialPixels = RenderViaPresentWithReadback(session, frame, Width, Height, spatialAccel);
        Assert.Equal(4, session.RayGiStatistics!.SamplesPerPixel);
        Assert.NotEqual(noGi, spatialPixels);
        AssertNonBlackLitPixels(spatialPixels, Width, Height);

        var temporalAccel = spatialAccel with { Denoiser = RayGiDenoiser.TemporalSpatial };
        byte[]? prev = null;
        for (int i = 0; i < 4; i++)
        {
            var pixels = RenderViaPresentWithReadback(session, frame, Width, Height, temporalAccel);
            Assert.Equal(2, session.RayGiStatistics!.SamplesPerPixel);
            AssertNonBlackLitPixels(pixels, Width, Height);
            prev = pixels;
        }
        Assert.NotNull(prev);
        Assert.False(session.RayGiStatistics!.TemporalResetHistory);
        Assert.Equal(4UL, session.RayGiStatistics.TemporalHistoryFrames);
        Assert.Empty(session.RendererDebugMessagesOrEmpty());
    }

    [Fact]
    public void ShortHistoryReprojectionCapsControlZOnLightingChangeAndConvergesFast()
    {
        using var runtime = new D3D12GpuRuntime();
        Assert.False(runtime.IsWarp);
        using var temporal = new D3D12RayGiTemporalPass(runtime);
        var compiler = runtime.GetOrCreateDxcCompiler();

        const int w = 32, h = 24;
        var frameA = D3D12RayGiTests.Bounce();
        var sceneA = RayGiScene.FromOriginal(frameA);
        var options = new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: 1f, SamplesPerPixel: 2,
            Seed: 11, MaxRayDistance: 50, RayEpsilon: 0.001f, Composition: RayGiComposition.ToonPalette,
            Denoiser: RayGiDenoiser.TemporalSpatial) { PreviewAcceleration = true };

        var pos = new Vector4[w * h];
        var norm = new Vector4[w * h];
        var sigA = new Vector4[w * h];
        var sigB = new Vector4[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                // Receiver quad lies at z=0 in [-2, 2]^2 (triangle 0)
                float wx = ((x + 0.5f) / w * 2f - 1f) * 1.5f;
                float wy = (1f - (y + 0.5f) / h * 2f) * 1.5f;
                pos[i] = new Vector4(wx, wy, 0f, 0.2f);
                norm[i] = new Vector4(0f, 0f, -1f, 1f);
                sigA[i] = new Vector4(0.8f, 0.1f, 0.1f, 1f);
                sigB[i] = new Vector4(0.1f, 0.2f, 0.9f, 1f);
            }
        }

        // Warm up 10 frames with lighting A -> normal cap 32, moments.z accumulates to 10.
        Vector4[] lastMoments = [];
        for (int f = 0; f < 10; f++)
        {
            (_, lastMoments) = RunTemporalStep(runtime, temporal, compiler, frameA, sceneA, options, w, h, sigA, pos, norm);
            Assert.Equal(32u, temporal.LastHistoryCap);
            if (f == 0) Assert.True(temporal.LastReset);
            else Assert.False(temporal.LastReset);
        }
        Assert.Equal(10f, lastMoments[w * (h / 2) + w / 2].Z);

        // FOV change: must keep history without reset and keep normal cap 32.
        var fovFrame = new OriginalFrameScene(frameA.Surface, frameA.Outline, frameA.OutlineOptions,
            frameA.Options! with { CameraOverride = frameA.Surface.Snapshot.Camera with { VerticalFovDegrees = 60f } },
            frameA.Materials, frameA.Lighting);
        var fovScene = RayGiScene.FromOriginal(fovFrame);
        (_, lastMoments) = RunTemporalStep(runtime, temporal, compiler, fovFrame, fovScene, options, w, h, sigA, pos, norm);
        Assert.False(temporal.LastReset);
        Assert.Equal(32u, temporal.LastHistoryCap);

        // Lighting change: must NOT reset whole frame, MUST lower history cap Control.z to 6!
        var litB = new OriginalFrameScene(
            new OriginalStyleScene(fovFrame.Surface.Snapshot.WithLighting(Vector3.UnitY),
                fovFrame.Surface.Materials, fovFrame.Surface.Profile),
            fovFrame.Outline, fovFrame.OutlineOptions, fovFrame.Options, fovFrame.Materials, fovFrame.Lighting);
        var sceneB = RayGiScene.FromOriginal(litB);
        var (outB0, momB0) = RunTemporalStep(runtime, temporal, compiler, litB, sceneB, options, w, h, sigB, pos, norm);
        Assert.False(temporal.LastReset);
        Assert.Equal("None", temporal.LastResetReason);
        Assert.Equal(6u, temporal.LastHistoryCap);
        var centerMom = momB0[w * (h / 2) + w / 2];
        Assert.InRange(centerMom.Z, 1f, 6f);

        // Step 6 more frames with lighting B -> converges quickly toward sigB.
        Vector4[] outB6 = outB0;
        for (int f = 0; f < 6; f++)
        {
            (outB6, _) = RunTemporalStep(runtime, temporal, compiler, litB, sceneB, options, w, h, sigB, pos, norm);
            Assert.False(temporal.LastReset);
        }
        var centerB6 = outB6[w * (h / 2) + w / 2];
        Assert.True(centerB6.Z > centerB6.X + 0.25f, $"Expected fast convergence to new lighting blue ({centerB6}), not ghosting old red.");
    }

    [Theory]
    [InlineData(RayGiDenoiser.Off)]
    [InlineData(RayGiDenoiser.Spatial)]
    [InlineData(RayGiDenoiser.TemporalSpatial)]
    public async Task OfflineExportWithPreviewAccelerationEnabledInPresetMatchesOffByteForByte(RayGiDenoiser denoiser)
    {
        var composition = denoiser == RayGiDenoiser.Off ? RayGiComposition.Additive : RayGiComposition.ToonPalette;
        var giOff = new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: 1.5f, SamplesPerPixel: 8,
            Seed: 41, MaxRayDistance: 50, RayEpsilon: 0.001f, Composition: composition, Denoiser: denoiser)
            { PreviewAcceleration = false };
        var giOn = giOff with { PreviewAcceleration = true };

        var presetWithOn = new ViewOutputPresetDefinition("export-isolation", "Isolation",
            new ViewRenderRequest(Width, Height, new(Gi: giOn, Antialiasing: FrameAntialiasing.Off, SpatialSamples: 1)),
            new ViewExportPreset(Width, Height, new(30), new(Gi: giOn, Antialiasing: FrameAntialiasing.Off, SpatialSamples: 1)));
        var roundTripped = ViewOutputPresetJson.Deserialize(ViewOutputPresetJson.Serialize(presetWithOn));
        Assert.True(roundTripped.Export.Quality!.EffectiveGi.PreviewAcceleration);
        Assert.Equal(giOff, roundTripped.Export.Quality.EffectiveGi with { PreviewAcceleration = false });

        var scene = Scene();
        var plan = new OfflineFramePlan(Width, Height, new(30), 0, 2);
        var token = TestContext.Current.CancellationToken;

        await using var offExporter = new D3D12ViewRenderer(new(Width, Height,
            new(Gi: giOff, Antialiasing: FrameAntialiasing.Off, SpatialSamples: 1)));
        await using var onExporter = new D3D12ViewRenderer(new(Width, Height, roundTripped.Export.Quality));

        for (int i = 0; i < 3; i++)
        {
            var frame = new OfflineFrame(plan.GetSample(i), Width, Height, scene, null, null, null);
            using var offLease = await offExporter.CaptureAsync(frame, token);
            using var onLease = await onExporter.CaptureAsync(frame, token);
            Assert.Equal(offLease.Pixels.ToArray(), onLease.Pixels.ToArray());
            Assert.Equal(8, onExporter.RayGiStatistics!.SamplesPerPixel);
            if (denoiser == RayGiDenoiser.TemporalSpatial)
                Assert.Equal((long)Width * Height * 64, onExporter.RayGiStatistics.TemporalHistoryBytes);
        }

        // Low-level D3D12FrameSession.Capture and CaptureInto also strip PreviewAcceleration across multi-frame sequences.
        using var sessionOff = new D3D12FrameSession();
        using var sessionOn = new D3D12FrameSession();
        var intoOff = new byte[checked(Width * Height * 4)];
        var intoOn = new byte[checked(Width * Height * 4)];
        for (int i = 0; i < 2; i++)
        {
            var captureOff = sessionOff.Capture(scene, Width, Height, rayGiOptions: giOff);
            var captureOn = sessionOn.Capture(scene, Width, Height, rayGiOptions: giOn);
            Assert.Equal(captureOff.Rgba, captureOn.Rgba);
            Assert.Equal(8, sessionOn.RayGiStatistics!.SamplesPerPixel);

            _ = sessionOff.CaptureInto(intoOff, scene, Width, Height, rayGiOptions: giOff);
            _ = sessionOn.CaptureInto(intoOn, scene, Width, Height, rayGiOptions: giOn);
            Assert.Equal(intoOff, intoOn);
        }
    }

    private static OriginalFrameScene Scene()
    {
        var source = D3D12RayGiTests.Bounce();
        return new(new(source.Surface.Snapshot,
            source.Surface.Materials.Select((material, index) => index == 0 ? material with { ToonShadowColor = new(0.2f) } : material),
            source.Surface.Profile),
            source.Outline, source.OutlineOptions,
            source.Options! with { CameraOverride = source.Surface.Snapshot.Camera with { VerticalFovDegrees = 45f } },
            source.Materials, source.Lighting);
    }

    private static void AssertNonBlackLitPixels(byte[] rgba, int width, int height)
    {
        int nonBlack = 0;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i] > 25 || rgba[i + 1] > 25 || rgba[i + 2] > 25) nonBlack++;
            Assert.Equal(255, rgba[i + 3]);
        }
        Assert.True(nonBlack > width * height / 10, $"Expected non-black lit pixels, got only {nonBlack}.");
    }

    private static byte[] RenderViaPresentWithReadback(D3D12FrameSession session, OriginalFrameScene frame,
        int width, int height, RayGiOptions gi)
    {
        ID3D12Resource? readback = null;
        PlacedSubresourceFootPrint footprint = default;
        ulong totalBytes = 0;
        try
        {
            using var output = session.RenderForPresent(frame, width, height, gi, (context, target) =>
            {
                var footprints = new PlacedSubresourceFootPrint[1];
                session.Runtime.Device.GetCopyableFootprints(target.Resource.Description, 0, 1, 0,
                    footprints, new int[1], new ulong[1], out totalBytes);
                footprint = footprints[0];
                readback = session.Runtime.Device.CreateCommittedResource(HeapType.Readback,
                    ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);
                context.Transition(target, ResourceStates.CopySource);
                context.Commands.CopyTextureRegion(new TextureCopyLocation(readback, footprint), 0, 0, 0,
                    new TextureCopyLocation(target.Resource, 0));
            });
            session.WaitForIdle();
            var pixels = new byte[checked(width * height * 4)];
            var mapped = readback!.Map<byte>(0, checked((int)totalBytes));
            try
            {
                for (int y = 0; y < height; y++)
                    mapped.Slice(y * footprint.Footprint.RowPitch, width * 4).CopyTo(pixels.AsSpan(y * width * 4, width * 4));
            }
            finally { readback.Unmap(0); }
            return pixels;
        }
        finally { readback?.Dispose(); }
    }

    private static Vector4[] RunHalfResPass(D3D12GpuRuntime runtime, D3D12RayGiHalfResPass pass, DxcShaderCompiler compiler,
        int fullW, int fullH, Vector4[] fullPos, Vector4[] fullNorm, Vector4[] halfSignalData)
    {
        var (halfW, halfH) = D3D12RayGiHalfResPass.HalfDimensions(fullW, fullH);
        var context = runtime.Begin();
        var device = runtime.Device;
        var commands = context.Commands;

        ID3D12Resource UploadTexture(Vector4[] data, int w, int h)
        {
            var tex = context.Own(device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Texture2D(Format.R32G32B32A32_Float, (uint)w, (uint)h, mipLevels: 1),
                ResourceStates.CopyDest));
            var footprints = new PlacedSubresourceFootPrint[1];
            device.GetCopyableFootprints(tex.Description, 0, 1, 0, footprints, new int[1], new ulong[1], out ulong size);
            var upload = context.Own(device.CreateCommittedResource(HeapType.Upload,
                ResourceDescription.Buffer(size), ResourceStates.GenericRead));
            var src = MemoryMarshal.AsBytes(data.AsSpan());
            var mapped = upload.Map<byte>(0, (int)size);
            for (int y = 0; y < h; y++)
                src.Slice(y * w * 16, w * 16).CopyTo(mapped.Slice(y * footprints[0].Footprint.RowPitch, w * 16));
            upload.Unmap(0);
            commands.CopyTextureRegion(new TextureCopyLocation(tex, 0), 0, 0, 0,
                new TextureCopyLocation(upload, footprints[0]));
            commands.ResourceBarrierTransition(tex, ResourceStates.CopyDest,
                ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
            return tex;
        }

        ID3D12Resource UploadBuffer(Vector4[] data)
        {
            var bytes = MemoryMarshal.AsBytes(data.AsSpan());
            var upload = context.Own(device.CreateCommittedResource(HeapType.Upload,
                ResourceDescription.Buffer((ulong)bytes.Length), ResourceStates.GenericRead));
            bytes.CopyTo(upload.Map<byte>(0, bytes.Length));
            upload.Unmap(0);
            var buf = context.Own(device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Buffer((ulong)bytes.Length), ResourceStates.CopyDest));
            commands.CopyBufferRegion(buf, 0, upload, 0, (ulong)bytes.Length);
            commands.ResourceBarrierTransition(buf, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
            return buf;
        }

        var fullPosRes = new D3D12GiWorkspaceResource(UploadTexture(fullPos, fullW, fullH),
            ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
        var fullNormRes = new D3D12GiWorkspaceResource(UploadTexture(fullNorm, fullW, fullH),
            ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
        var halfSigRes = new D3D12GiWorkspaceResource(UploadBuffer(halfSignalData), ResourceStates.NonPixelShaderResource);
        var fullOutBytes = checked((ulong)fullW * (ulong)fullH * 16);
        var fullOutBuf = context.Own(device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Buffer(fullOutBytes, ResourceFlags.AllowUnorderedAccess),
            ResourceStates.UnorderedAccess));
        var fullOutRes = new D3D12GiWorkspaceResource(fullOutBuf, ResourceStates.UnorderedAccess);

        var ws = new D3D12RayGiWorkspace(context, fullW, fullH);
        var halfWs = ws.AcquireHalfRes(context, halfW, halfH);
        var heap = pass.CreateDescriptorHeap(runtime, fullPosRes.Resource, fullNormRes.Resource, halfWs.Position.Resource, halfWs.Normal.Resource, out int stride);
        pass.Downsample(context, heap, stride, fullPosRes, fullNormRes, halfWs, fullW, fullH, compiler);
        var camera = new SceneCamera(new(0, 0, -10), Vector3.Zero, Vector3.UnitY, fullH, 0.1f, 100);
        pass.Upsample(context, heap, stride, halfSigRes, fullOutRes, camera, fullW, fullH, halfW, halfH, compiler);

        var readback = context.Own(device.CreateCommittedResource(HeapType.Readback,
            ResourceDescription.Buffer(fullOutBytes), ResourceStates.CopyDest));
        commands.ResourceBarrierTransition(fullOutBuf, ResourceStates.PixelShaderResource, ResourceStates.CopySource);
        commands.CopyBufferRegion(readback, 0, fullOutBuf, 0, fullOutBytes);
        runtime.Wait(runtime.Submit());
        try { return MemoryMarshal.Cast<byte, Vector4>(readback.Map<byte>(0, checked((int)fullOutBytes))).ToArray(); }
        finally { readback.Unmap(0); }
    }

    private static (Vector4[] Signal, Vector4[] Moments) RunTemporalStep(D3D12GpuRuntime runtime, D3D12RayGiTemporalPass temporal,
        DxcShaderCompiler compiler, OriginalFrameScene frame, RayGiScene scene, RayGiOptions options,
        int w, int h, Vector4[] inputSignal, Vector4[] posData, Vector4[] normData)
    {
        temporal.Prepare(frame, scene, options, w, h, 0, 1);
        var context = runtime.Begin();
        var device = runtime.Device;
        var commands = context.Commands;

        ID3D12Resource Buffer<T>(ReadOnlySpan<T> values) where T : unmanaged
        {
            var bytes = MemoryMarshal.AsBytes(values);
            var upload = context.Own(device.CreateCommittedResource(HeapType.Upload,
                ResourceDescription.Buffer((ulong)bytes.Length), ResourceStates.GenericRead));
            bytes.CopyTo(upload.Map<byte>(0, bytes.Length));
            upload.Unmap(0);
            var buf = context.Own(device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Buffer((ulong)bytes.Length), ResourceStates.CopyDest));
            commands.CopyBufferRegion(buf, 0, upload, 0, (ulong)bytes.Length);
            commands.ResourceBarrierTransition(buf, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
            return buf;
        }

        var heap = context.Own(device.CreateDescriptorHeap(new(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, DescriptorHeapFlags.ShaderVisible)));
        int stride = device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        void Texture(Vector4[] values, int slot)
        {
            var tex = context.Own(device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Texture2D(Format.R32G32B32A32_Float, (uint)w, (uint)h, mipLevels: 1), ResourceStates.CopyDest));
            var footprints = new PlacedSubresourceFootPrint[1];
            device.GetCopyableFootprints(tex.Description, 0, 1, 0, footprints, new int[1], new ulong[1], out ulong size);
            var upload = context.Own(device.CreateCommittedResource(HeapType.Upload,
                ResourceDescription.Buffer(size), ResourceStates.GenericRead));
            var src = MemoryMarshal.AsBytes(values.AsSpan());
            var mapped = upload.Map<byte>(0, (int)size);
            for (int y = 0; y < h; y++)
                src.Slice(y * w * 16, w * 16).CopyTo(mapped.Slice(y * footprints[0].Footprint.RowPitch, w * 16));
            upload.Unmap(0);
            commands.CopyTextureRegion(new TextureCopyLocation(tex, 0), 0, 0, 0,
                new TextureCopyLocation(upload, footprints[0]));
            commands.ResourceBarrierTransition(tex, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
            device.CreateShaderResourceView(tex, null, heap.GetCPUDescriptorHandleForHeapStart().Offset(slot, stride));
        }

        var inBuf = Buffer<Vector4>(inputSignal);
        var triBuf = Buffer<RayGiTriangleGpu>(scene.Triangles);
        Texture(posData, 0);
        Texture(normData, 1);
        commands.SetDescriptorHeaps(heap);

        var outSig = temporal.Record(context, inBuf, triBuf, heap.GetGPUDescriptorHandleForHeapStart(),
            frame, scene, options, w, h, compiler);
        var outMom = temporal.RecordedMoments!;
        int bytesCount = checked(w * h * 16);
        var rbSig = context.Own(device.CreateCommittedResource(HeapType.Readback,
            ResourceDescription.Buffer((ulong)bytesCount), ResourceStates.CopyDest));
        var rbMom = context.Own(device.CreateCommittedResource(HeapType.Readback,
            ResourceDescription.Buffer((ulong)bytesCount), ResourceStates.CopyDest));
        commands.ResourceBarrierTransition(outSig, ResourceStates.NonPixelShaderResource, ResourceStates.CopySource);
        commands.CopyBufferRegion(rbSig, 0, outSig, 0, (ulong)bytesCount);
        commands.ResourceBarrierTransition(outSig, ResourceStates.CopySource, ResourceStates.NonPixelShaderResource);
        commands.ResourceBarrierTransition(outMom, ResourceStates.NonPixelShaderResource, ResourceStates.CopySource);
        commands.CopyBufferRegion(rbMom, 0, outMom, 0, (ulong)bytesCount);
        commands.ResourceBarrierTransition(outMom, ResourceStates.CopySource, ResourceStates.NonPixelShaderResource);

        temporal.ValidatePendingFrame();
        runtime.Wait(runtime.Submit());
        temporal.CommitFrame();

        var sig = MemoryMarshal.Cast<byte, Vector4>(rbSig.Map<byte>(0, bytesCount)).ToArray();
        rbSig.Unmap(0);
        var mom = MemoryMarshal.Cast<byte, Vector4>(rbMom.Map<byte>(0, bytesCount)).ToArray();
        rbMom.Unmap(0);
        return (sig, mom);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extended, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}

internal static class GiTestSessionDebugExtensions
{
    internal static IReadOnlyList<string> RendererDebugMessagesOrEmpty(this D3D12FrameSession session)
    {
        var field = typeof(D3D12FrameSession).GetField("renderer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(session) is D3D12FrameRenderer r ? r.LastDebugMessages : [];
    }
}
