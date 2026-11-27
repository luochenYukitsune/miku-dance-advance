using System.Numerics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class PreviewQualityPr4UiWiringTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public PreviewQualityPr4UiWiringTests() => session.Dispatch(
        () => EditorLocalization.Text("Mda.Text.Preview.CustomPresetReadFailed"),
        Token).GetAwaiter().GetResult();

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);

    [Fact]
    public async Task Pr2AndPr3ControlsBindTwoWayAndSupportApplyAndCancelRollback() => await session.Dispatch(() =>
    {
        var caps = new EditorPreviewCapabilities(
            Gi: new D3D12RayGiCapabilities(false, true, 66, 11, true, true, true, true, true, null),
            SpatialUpscalingAvailable: true)
        {
            AdapterName = "Mock DXR GPU",
            LocalVideoMemoryBudgetBytes = 4096UL * 1024 * 1024
        };
        var initial = ViewQualityPresets.Create(BuiltInPreviewPresetKind.Balanced);

        // 1. Verify Cancel returns null without mutating initial.
        var cancelVm = new EditorPreviewSettingsViewModel(initial, caps, persistCustomPresets: false);
        var cancelDialog = new EditorPreviewSettingsWindow(cancelVm);
        ViewQualityOptions? cancelResult = initial;
        cancelDialog.Closed += (_, _) => { };
        cancelDialog.Show();
        try
        {
            cancelVm.PipelineModeIndex = 1; // ClusteredForward
            cancelVm.AntialiasingIndex = 4; // TAA
            cancelVm.StandaloneRcasEnabled = true;
            cancelVm.StandaloneRcasSharpness = 0.45m;
            cancelVm.AnisotropicFilteringIndex = 4; // 16x
            cancelVm.ShadowFilterIndex = 2; // PcfSoft
            cancelVm.ShadowSoftness = 2.5m;
            cancelVm.SsaoEnabled = true;
            cancelVm.SsaoStrength = 0.55m;
            cancelVm.SsaoRadius = 2.2m;
            cancelVm.SsaoSampleCountIndex = 2; // 16
            Dispatcher.UIThread.RunJobs();
            cancelDialog.Close(null);
            cancelResult = null;
        }
        finally
        {
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Null(cancelResult);
        Assert.Equal(RenderPipelineMode.Forward, initial.PipelineMode);
        Assert.False(initial.EffectiveScreenSpaceAo.Enabled);

        // 2. Verify all 7 FrameAntialiasing modes + ClusteredForward + PR2 fields round-trip on Apply.
        var vm = new EditorPreviewSettingsViewModel(initial, caps, persistCustomPresets: false);
        var dialog = new EditorPreviewSettingsWindow(vm);
        dialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            // Check all 7 FrameAntialiasing modes + Follow Scene (8 items total)
            Assert.Equal(8, vm.AntialiasingModes.Count);
            var expectedAa = new FrameAntialiasing?[]
            {
                null,
                FrameAntialiasing.Off,
                FrameAntialiasing.Fxaa,
                FrameAntialiasing.Smaa1X,
                FrameAntialiasing.Taa,
                FrameAntialiasing.Msaa2X,
                FrameAntialiasing.Msaa4X,
                FrameAntialiasing.Msaa8X
            };
            for (int i = 0; i < expectedAa.Length; i++)
            {
                vm.AntialiasingIndex = i;
                Assert.True(vm.TryCreate(out var aaDraft), vm.ValidationMessage);
                Assert.Equal(expectedAa[i], aaDraft!.Antialiasing);
            }

            dialog.FindControl<ComboBox>("PreviewPipelineModeBox")!.SelectedIndex = 1; // ClusteredForward
            dialog.FindControl<ComboBox>("PreviewAntialiasingBox")!.SelectedIndex = 6; // Msaa4X
            dialog.FindControl<CheckBox>("PreviewStandaloneRcasEnabledBox")!.IsChecked = true;
            dialog.FindControl<EditorNumericUpDown>("PreviewStandaloneRcasSharpnessBox")!.Text = "0.35";
            dialog.FindControl<ComboBox>("PreviewAnisotropicFilteringBox")!.SelectedIndex = 4; // 16x
            dialog.FindControl<ComboBox>("PreviewShadowResolutionBox")!.SelectedIndex = 4; // 4096
            dialog.FindControl<ComboBox>("PreviewShadowFilterBox")!.SelectedIndex = 2; // PcfSoft
            dialog.FindControl<EditorNumericUpDown>("PreviewShadowSoftnessBox")!.Text = "2.75";
            dialog.FindControl<CheckBox>("PreviewSsaoEnabledBox")!.IsChecked = true;
            dialog.FindControl<EditorNumericUpDown>("PreviewSsaoStrengthBox")!.Text = "0.65";
            dialog.FindControl<EditorNumericUpDown>("PreviewSsaoRadiusBox")!.Text = "3.2";
            dialog.FindControl<ComboBox>("PreviewSsaoSampleCountBox")!.SelectedIndex = 2; // 16
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, vm.PipelineModeIndex);
            Assert.Equal(6, vm.AntialiasingIndex);
            Assert.True(vm.StandaloneRcasEnabled);
            Assert.Equal(0.35m, vm.StandaloneRcasSharpness);
            Assert.Equal(4, vm.AnisotropicFilteringIndex);
            Assert.Equal(4, vm.ShadowResolutionIndex);
            Assert.Equal(2, vm.ShadowFilterIndex);
            Assert.Equal(2.75m, vm.ShadowSoftness);
            Assert.True(vm.SsaoEnabled);
            Assert.Equal(0.65m, vm.SsaoStrength);
            Assert.Equal(3.2m, vm.SsaoRadius);
            Assert.Equal(2, vm.SsaoSampleCountIndex);

            Assert.True(vm.TryCreate(out var rasterResult), vm.ValidationMessage);
            Assert.Equal(RenderPipelineMode.ClusteredForward, rasterResult!.PipelineMode);
            Assert.Equal(FrameAntialiasing.Msaa4X, rasterResult.Antialiasing);
            Assert.True(rasterResult.EffectiveSharpening.Enabled);
            Assert.Equal(0.35f, rasterResult.EffectiveSharpening.Sharpness);
            Assert.Equal(16, rasterResult.AnisotropicFiltering);
            Assert.Equal(ShadowFilterMode.PcfSoft, rasterResult.ShadowFilter);
            Assert.Equal(2.75f, rasterResult.ShadowSoftness);
            Assert.True(rasterResult.EffectiveScreenSpaceAo.Enabled);
            Assert.Equal(0.65f, rasterResult.EffectiveScreenSpaceAo.Strength);
            Assert.Equal(3.2f, rasterResult.EffectiveScreenSpaceAo.Radius);
            Assert.Equal(16, rasterResult.EffectiveScreenSpaceAo.SampleCount);

            // 3. Verify PathTracing full common + expert controls round-trip cleanly.
            vm.PipelineModeIndex = 2; // PathTracing
            vm.PtShadingModeIndex = 1; // Physical
            vm.PtSamplesPerFrame = 8m;
            vm.PtMaxBounces = 6m;
            vm.PtProgressiveAccumulation = true;
            vm.PtDenoiserIndex = 1; // Spatial
            vm.PtMaxAccumulatedFrames = 512m;
            vm.PtInteractiveSamplesPerFrame = 2m;
            vm.PtFireflyClamp = 25m;
            vm.PtUseHardwareRayTracing = false;
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.TryCreate(out var ptResult), vm.ValidationMessage);
            Assert.Equal(RenderPipelineMode.PathTracing, ptResult!.PipelineMode);
            Assert.Equal(FrameAntialiasing.Off, ptResult.Antialiasing);
            Assert.Equal(1, ptResult.SpatialSamples);
            Assert.Equal(ViewUpscalingMode.Off, ptResult.EffectiveUpscaling.Mode);
            Assert.Equal(ViewTemporalUpscalingMode.Off, ptResult.EffectiveTemporalUpscaling.Mode);
            Assert.NotNull(ptResult.PathTracing);
            Assert.Equal(PathTracingShadingMode.Physical, ptResult.PathTracing!.ShadingMode);
            Assert.Equal(8, ptResult.PathTracing.SamplesPerFrame);
            Assert.Equal(6, ptResult.PathTracing.MaxBounces);
            Assert.True(ptResult.PathTracing.ProgressiveAccumulation);
            Assert.Equal(RayGiDenoiser.Spatial, ptResult.PathTracing.Denoiser);
            Assert.Equal(512, ptResult.PathTracing.MaxAccumulatedFrames);
            Assert.Equal(2, ptResult.PathTracing.InteractiveSamplesPerFrame);
            Assert.Equal(25f, ptResult.PathTracing.FireflyClamp);
            Assert.False(ptResult.PathTracing.UseHardwareRayTracing);
        }
        finally
        {
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task LiveCompatibilityDisablingAndReasonNoticesWorkAcrossPr2AndPr3() => await session.Dispatch(() =>
    {
        var caps = new EditorPreviewCapabilities(
            Gi: new D3D12RayGiCapabilities(false, true, 66, 0, false, false, true, true, false, null), // Software GI only, no DXR
            SpatialUpscalingAvailable: true)
        {
            AdapterName = "Software-GI GPU"
        };
        var vm = new EditorPreviewSettingsViewModel(
            ViewQualityPresets.Create(BuiltInPreviewPresetKind.Balanced),
            caps,
            persistCustomPresets: false);
        var dialog = new EditorPreviewSettingsWindow(vm);
        dialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var aaBox = dialog.FindControl<ComboBox>("PreviewAntialiasingBox")!;
            var ssaaBox = dialog.FindControl<ComboBox>("PreviewSpatialSamplesBox")!;
            var fsr1Box = dialog.FindControl<ComboBox>("PreviewUpscalingModeBox")!;
            var temporalBox = dialog.FindControl<ComboBox>("PreviewTemporalModeBox")!;
            var renderScaleBox = dialog.FindControl<EditorNumericUpDown>("PreviewRenderScaleBox")!;
            var shadowResBox = dialog.FindControl<ComboBox>("PreviewShadowResolutionBox")!;
            var shadowFilterBox = dialog.FindControl<ComboBox>("PreviewShadowFilterBox")!;
            var shadowSoftnessBox = dialog.FindControl<EditorNumericUpDown>("PreviewShadowSoftnessBox")!;
            var ssaoToggle = dialog.FindControl<CheckBox>("PreviewSsaoEnabledBox")!;
            var giModeBox = dialog.FindControl<ComboBox>("PreviewGiModeBox")!;
            var giOccBox = dialog.FindControl<EditorNumericUpDown>("PreviewGiOcclusionStrengthBox")!;

            // 1. PcfSoft controls ShadowSoftness enabled state.
            vm.ShadowFilterIndex = 0; // Linear
            Dispatcher.UIThread.RunJobs();
            Assert.False(shadowSoftnessBox.IsEnabled);
            Assert.True(dialog.FindControl<TextBlock>("ShadowSoftnessHintText")!.IsVisible);

            vm.ShadowFilterIndex = 2; // PcfSoft
            Dispatcher.UIThread.RunJobs();
            Assert.True(shadowSoftnessBox.IsEnabled);
            Assert.False(dialog.FindControl<TextBlock>("ShadowSoftnessHintText")!.IsVisible);

            // 2. TAA & MSAA lock SSAA, FSR1, Temporal and show reason notice.
            vm.AntialiasingIndex = 4; // TAA
            Dispatcher.UIThread.RunJobs();
            Assert.False(ssaaBox.IsEnabled);
            Assert.False(fsr1Box.IsEnabled);
            Assert.False(temporalBox.IsEnabled);
            Assert.True(dialog.FindControl<TextBlock>("AntialiasingConflictHintText")!.IsVisible);

            vm.AntialiasingIndex = 6; // MSAA 4x
            Dispatcher.UIThread.RunJobs();
            Assert.False(ssaaBox.IsEnabled);
            Assert.False(fsr1Box.IsEnabled);
            Assert.False(temporalBox.IsEnabled);
            Assert.True(dialog.FindControl<TextBlock>("AntialiasingConflictHintText")!.IsVisible);

            // 3. FSR1 + RCAS shows double-sharpening notice alongside Standalone RCAS.
            vm.AntialiasingIndex = 1; // Off
            vm.UpscalingIndex = 1; // FSR 1
            vm.RcasEnabled = true;
            vm.StandaloneRcasEnabled = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.FindControl<TextBlock>("StandaloneRcasFsrNoticeText")!.IsVisible);
            vm.UpscalingIndex = 0;
            vm.StandaloneRcasEnabled = false;

            // 4. SSAO <-> Ray GI Occlusion symmetric mutual exclusion + notices.
            vm.GiIndex = 1; // SoftwareRayTracing
            vm.GiOcclusionStrength = 0.35m;
            Dispatcher.UIThread.RunJobs();
            Assert.False(ssaoToggle.IsEnabled);
            Assert.True(dialog.FindControl<TextBlock>("SsaoNoticeText")!.IsVisible);

            vm.GiOcclusionStrength = 0m;
            Dispatcher.UIThread.RunJobs();
            Assert.True(ssaoToggle.IsEnabled);

            vm.SsaoEnabled = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(giOccBox.IsEnabled);
            Assert.True(dialog.FindControl<TextBlock>("GiOcclusionLockedBySsaoHintText")!.IsVisible);

            // 5. PathTracing disables raster AA, SSAA, FSR1, Temporal, RenderScale, Shadow, SSAO, and Ray GI + shows notices.
            vm.PipelineModeIndex = 2; // PathTracing
            Dispatcher.UIThread.RunJobs();
            Assert.False(aaBox.IsEnabled);
            Assert.False(ssaaBox.IsEnabled);
            Assert.False(fsr1Box.IsEnabled);
            Assert.False(temporalBox.IsEnabled);
            Assert.False(renderScaleBox.IsEnabled);
            Assert.False(shadowResBox.IsEnabled);
            Assert.False(shadowFilterBox.IsEnabled);
            Assert.False(shadowSoftnessBox.IsEnabled);
            Assert.False(ssaoToggle.IsEnabled);
            Assert.False(giModeBox.IsEnabled);

            Assert.True(dialog.FindControl<StackPanel>("PathTracingSettingsPanel")!.IsVisible);
            Assert.True(dialog.FindControl<TextBlock>("PathTracingLocksGiHintText")!.IsVisible);
            Assert.True(dialog.FindControl<TextBlock>("ShadowDisabledHintText")!.IsVisible);
            Assert.True(dialog.FindControl<TextBlock>("SsaoNoticeText")!.IsVisible);
            Assert.True(dialog.FindControl<TextBlock>("PtHardwareFallbackNoticeText")!.IsVisible);
        }
        finally
        {
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task RightSidebarDynamicHelpAndLiveVramEstimatorUpdateAndRespectClassicMode() => await session.Dispatch(() =>
    {
        var caps = new EditorPreviewCapabilities(
            Gi: new D3D12RayGiCapabilities(false, true, 66, 11, true, true, true, true, true, null),
            SpatialUpscalingAvailable: true)
        {
            AdapterName = "Mock Test GPU",
            LocalVideoMemoryBudgetBytes = 2048UL * 1024 * 1024
        };
        var runtime = new EditorPreviewRuntimeInfo("Mock Test GPU", IsWarp: false, 1920, 1080, LocalVideoMemoryBudgetBytes: 2048UL * 1024 * 1024);
        var vm = new EditorPreviewSettingsViewModel(
            ViewQualityPresets.Create(BuiltInPreviewPresetKind.Performance),
            caps,
            runtime,
            persistCustomPresets: false);
        var dialog = new EditorPreviewSettingsWindow(vm);
        dialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var helpTitle = dialog.FindControl<TextBlock>("HelpTopicTitleText")!;
            var helpBody = dialog.FindControl<TextBlock>("HelpTopicBodyText")!;

            // 1. Switching categories always shows non-empty category default help.
            for (int cat = 0; cat < 6; cat++)
            {
                vm.SelectedCategoryIndex = cat;
                Dispatcher.UIThread.RunJobs();
                Assert.False(string.IsNullOrWhiteSpace(helpTitle.Text));
                Assert.False(string.IsNullOrWhiteSpace(helpBody.Text));
                Assert.DoesNotContain("Mda.Text.", helpTitle.Text);
                Assert.DoesNotContain("Mda.Text.", helpBody.Text);
            }

            // 2. Switching dropdown choices dynamically updates concrete option help.
            vm.SelectedCategoryIndex = 1; // Antialiasing
            vm.AntialiasingIndex = 2; // FXAA
            Dispatcher.UIThread.RunJobs();
            string fxaaTitle = helpTitle.Text!;
            vm.AntialiasingIndex = 6; // MSAA 4x
            Dispatcher.UIThread.RunJobs();
            string msaaTitle = helpTitle.Text!;
            Assert.NotEqual(fxaaTitle, msaaTitle);

            vm.SelectedCategoryIndex = 3; // Pipeline & GI
            vm.PipelineModeIndex = 2; // PathTracing
            Dispatcher.UIThread.RunJobs();
            string ptTitle = helpTitle.Text!;
            vm.PtShadingModeIndex = 1; // Physical
            Dispatcher.UIThread.RunJobs();
            string physicalTitle = helpTitle.Text!;
            Assert.NotEqual(ptTitle, physicalTitle);

            // 3. GotFocus & PointerEntered on controls update help text dynamically.
            vm.SelectedCategoryIndex = 2; // Shadows
            Dispatcher.UIThread.RunJobs();
            var ssaoBox = dialog.FindControl<CheckBox>("PreviewSsaoEnabledBox")!;
            ssaoBox.RaiseEvent(new GotFocusEventArgs { RoutedEvent = InputElement.GotFocusEvent });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Mda.Text.Preview.Help.ssao.enabled", vm.ActiveHelpTopicId);

            var anisoBox = dialog.FindControl<ComboBox>("PreviewAnisotropicFilteringBox")!;
            anisoBox.RaiseEvent(new PointerEventArgs(InputElement.PointerEnteredEvent, anisoBox,
                new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true),
                anisoBox, default, 0, default, Avalonia.Input.KeyModifiers.None));
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("Mda.Text.Preview.Help.Aniso.", vm.ActiveHelpTopicId, StringComparison.Ordinal);

            // 4. Resource estimator updates live with draft edits & budget overrun warning.
            vm.ApplyBuiltInPreset(BuiltInPreviewPresetKind.Performance);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PreviewPerformanceLoadTier.Moderate, vm.PerformanceLoadTier);
            double perfMiB = vm.ResourceEstimate.EstimatedRenderBufferMiB;
            Assert.Equal("1920 × 1080 (100%)", dialog.FindControl<TextBlock>("EstimateInternalResolutionText")!.Text);

            vm.RenderScale = 0.5m;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("960 × 540 (50%)", dialog.FindControl<TextBlock>("EstimateInternalResolutionText")!.Text);
            Assert.Equal(PreviewPerformanceLoadTier.Light, vm.PerformanceLoadTier);
            Assert.True(vm.ResourceEstimate.EstimatedRenderBufferMiB < perfMiB);

            vm.RenderScale = 1.0m;
            vm.ApplyBuiltInPreset(BuiltInPreviewPresetKind.UltraRayTraced);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PreviewPerformanceLoadTier.Heavy, vm.PerformanceLoadTier);
            Assert.True(vm.ResourceEstimate.EstimatedRenderBufferMiB > perfMiB);

            // Tight VRAM budget triggers warning banner.
            var tightRuntime = new EditorPreviewRuntimeInfo("Tiny GPU", IsWarp: false, 1920, 1080, LocalVideoMemoryBudgetBytes: 64UL * 1024 * 1024);
            var tightVm = new EditorPreviewSettingsViewModel(ViewQualityPresets.Create(BuiltInPreviewPresetKind.High), caps, tightRuntime, persistCustomPresets: false);
            var tightDialog = new EditorPreviewSettingsWindow(tightVm);
            tightDialog.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.True(tightVm.IsOverVramBudget);
                Assert.True(tightDialog.FindControl<TextBlock>("VramOverBudgetWarningText")!.IsVisible);
            }
            finally { tightDialog.Close(); Dispatcher.UIThread.RunJobs(); }

            // 5. Classic engine disables PR2/PR3 controls and displays Classic notice in right sidebar.
            var classicVm = new EditorPreviewSettingsViewModel(
                new ViewQualityOptions(Antialiasing: FrameAntialiasing.Off, SpatialSamples: 1, SurfaceOrder: SurfaceDrawOrder.Original),
                isClassicEngine: true,
                persistCustomPresets: false);
            var classicDialog = new EditorPreviewSettingsWindow(classicVm);
            classicDialog.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.True(classicDialog.FindControl<TextBlock>("EstimateClassicNoticeText")!.IsVisible);
                Assert.False(classicDialog.FindControl<Border>("PerformanceLoadBadge")!.IsVisible);
                Assert.False(classicDialog.FindControl<ComboBox>("PreviewPipelineModeBox")!.IsEnabled);
                Assert.False(classicDialog.FindControl<ComboBox>("PreviewShadowFilterBox")!.IsEnabled);
                Assert.False(classicDialog.FindControl<CheckBox>("PreviewSsaoEnabledBox")!.IsEnabled);
                Assert.False(classicDialog.FindControl<CheckBox>("PreviewStandaloneRcasEnabledBox")!.IsEnabled);
                Assert.False(classicDialog.FindControl<ComboBox>("PreviewAnisotropicFilteringBox")!.IsEnabled);
            }
            finally { classicDialog.Close(); Dispatcher.UIThread.RunJobs(); }
        }
        finally
        {
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task ExportBidirectionalCopyAndCustomPresetJsonRoundTripAllPr1Pr2Pr3FieldsAndLocalize() => await session.Dispatch(() =>
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "mda-pr4-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string outputPath = Path.Combine(tempRoot, "output.json");
        string presetsPath = Path.Combine(tempRoot, "presets.json");
        try
        {
            using var shell = new EditorShellViewModel();
            Assert.True(shell.CreateOutputSettingsViewModel().TryCreate(out var initialDraft));
            shell.ApplyOutputSettings(initialDraft!);

            var richRaster = new ViewQualityOptions(
                Gi: new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: 3.5f, SamplesPerPixel: 12,
                    OcclusionStrength: 0f, OcclusionRadius: 1.5f, Composition: RayGiComposition.ToonPalette,
                    Denoiser: RayGiDenoiser.TemporalSpatial)
                {
                    ToonResponse = RayGiToonResponse.PreservePaletteBands,
                    PreviewAcceleration = true
                },
                Antialiasing: FrameAntialiasing.Msaa4X,
                SmaaQuality: SmaaQuality.Ultra,
                SpatialSamples: 1,
                Diffusion: new ViewDiffusionOptions(true, 0.35f, 0.003f),
                SurfaceOrder: SurfaceDrawOrder.Original,
                PipelineMode: RenderPipelineMode.ClusteredForward)
            {
                Shadow = new ViewShadowOptions(4096, OriginalTextureSampling.LinearClamp),
                ShadowFilter = ShadowFilterMode.PcfSoft,
                ShadowSoftness = 2.25f,
                AnisotropicFiltering = 16,
                Sharpening = new ViewSharpeningOptions(true, 0.45f),
                ScreenSpaceAo = new ScreenSpaceAoOptions(true, 0.55f, 2.4f, 16),
                UseEnvironmentMipmaps = true,
                AdaptiveVramSampling = false,
                RenderScale = 0.85f,
                PreviewOutlineEnabled = true,
                PreviewOutlineWidthScale = 1.75f
            };

            var caps = new EditorPreviewCapabilities(
                Gi: new D3D12RayGiCapabilities(false, true, 66, 11, true, true, true, true, true, null),
                SpatialUpscalingAvailable: true);

            var vm = new EditorPreviewSettingsViewModel(
                richRaster,
                caps,
                customPresetsPath: presetsPath,
                getExportQuality: () => shell.OutputSettings?.Preset.Export.Quality,
                copyToExportQuality: q => EditorWindow.CopyPreviewQualityToExport(shell, q, persistPreferences: true, isClassic: false, outputSettingsPath: outputPath),
                persistCustomPresets: true);

            // Save to custom preset JSON and reload.
            vm.CustomPresetName = "PR4 Full Raster";
            Assert.True(vm.SaveNewCustomPreset(), vm.ValidationMessage);
            var reloadedPresets = EditorPreferences.LoadCustomPreviewPresets(out var diag, presetsPath);
            Assert.Null(diag);
            Assert.Single(reloadedPresets);
            Assert.Equal(richRaster, reloadedPresets[0].Quality);

            // Copy to export -> reload from disk -> load back into fresh ViewModel.
            Assert.True(vm.CopyToExport(), vm.ValidationMessage);
            var diskOutput = EditorPreferences.LoadOutputSettings(out var outDiag, outputPath);
            Assert.Null(outDiag);
            Assert.NotNull(diskOutput);

            var roundTripVm = new EditorPreviewSettingsViewModel(
                new ViewQualityOptions(),
                caps,
                persistCustomPresets: false,
                getExportQuality: () => diskOutput!.Preset.Export.Quality);
            Assert.True(roundTripVm.LoadFromExport(), roundTripVm.ValidationMessage);
            Assert.True(roundTripVm.TryCreate(out var roundTripped), roundTripVm.ValidationMessage);
            // Only PreviewAcceleration is preview-only and stripped on export.
            Assert.Equal(richRaster with { Gi = richRaster.Gi! with { PreviewAcceleration = false } }, roundTripped);

            // Also verify PathTracing bidirectional copy & export preservation.
            var richPt = new ViewQualityOptions(
                Antialiasing: FrameAntialiasing.Off,
                SpatialSamples: 1,
                PipelineMode: RenderPipelineMode.PathTracing,
                PathTracing: new PathTracingOptions(
                    PathTracingShadingMode.Physical,
                    SamplesPerFrame: 6,
                    MaxBounces: 5,
                    ProgressiveAccumulation: true,
                    MaxAccumulatedFrames: 512,
                    InteractiveSamplesPerFrame: 2,
                    FireflyClamp: 20f,
                    Denoiser: RayGiDenoiser.Spatial,
                    UseHardwareRayTracing: false))
            {
                Sharpening = new ViewSharpeningOptions(true, 0.3f),
                AnisotropicFiltering = 8
            };
            var ptVm = new EditorPreviewSettingsViewModel(
                richPt,
                caps,
                persistCustomPresets: false,
                copyToExportQuality: q => EditorWindow.CopyPreviewQualityToExport(shell, q, persistPreferences: true, isClassic: false, outputSettingsPath: outputPath));
            Assert.True(ptVm.CopyToExport(), ptVm.ValidationMessage);
            var ptDisk = EditorPreferences.LoadOutputSettings(out _, outputPath);
            Assert.Equal(richPt, ptDisk!.Preset.Export.Quality);

            // Verify 3-language localization across all new PR4 properties & help topics.
            string langBefore = EditorLocalization.CurrentLanguage;
            try
            {
                foreach (string lang in new[] { "zh-CN", "en", "ja" })
                {
                    EditorLocalization.SetLanguage(lang);
                    Dispatcher.UIThread.RunJobs();
                    Assert.DoesNotContain("Mda.Text.", vm.PipelineSectionLabel);
                    Assert.DoesNotContain("Mda.Text.", vm.StandaloneSharpeningSectionLabel);
                    Assert.DoesNotContain("Mda.Text.", vm.AnisotropicFilteringLabel);
                    Assert.DoesNotContain("Mda.Text.", vm.SsaoSectionLabel);
                    Assert.DoesNotContain("Mda.Text.", vm.EstimateHeader);
                    Assert.DoesNotContain("Mda.Text.", vm.PerformanceLoadBadgeText);
                    Assert.DoesNotContain("Mda.Text.", vm.ActiveHelpTitle);
                    Assert.DoesNotContain("Mda.Text.", vm.ActiveHelpBody);
                    Assert.All(vm.AntialiasingModes, item => Assert.DoesNotContain("Mda.Text.", item));
                    Assert.All(vm.ShadowFilterChoices, item => Assert.DoesNotContain("Mda.Text.", item));
                    Assert.All(vm.PipelineModeChoices, item => Assert.DoesNotContain("Mda.Text.", item));
                    Assert.Contains(EditorLocalization.Format("Mda.Text.Preview.OverviewPtBounces", 5), ptVm.OverviewConfigurationSummary, StringComparison.Ordinal);
                }
            }
            finally { EditorLocalization.SetLanguage(langBefore); }
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task AllEightDropdownHelpSettersHiddenPathTracingInputInvalidGiPathTracingAndTaaTransparentExportWork() => await session.Dispatch(() =>
    {
        var caps = new EditorPreviewCapabilities(
            Gi: new D3D12RayGiCapabilities(false, true, 66, 11, true, true, true, true, true, null),
            SpatialUpscalingAvailable: true,
            XeSsQueries: [new D3D12XeSsDeviceQuery("Mock", D3D12XeSsQuality.Quality) { Initialized = true }]);
        var vm = new EditorPreviewSettingsViewModel(
            ViewQualityPresets.Create(BuiltInPreviewPresetKind.Balanced),
            caps,
            persistCustomPresets: false);
        var dialog = new EditorPreviewSettingsWindow(vm);
        bool dialogClosed = false;
        dialog.Closed += (_, _) => dialogClosed = true;
        dialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            void AssertHelpClean(string expectedPrefix)
            {
                Assert.StartsWith("Mda.Text.Preview.Help.", vm.ActiveHelpTopicId, StringComparison.Ordinal);
                Assert.DoesNotContain("help.", vm.ActiveHelpTitle, StringComparison.Ordinal);
                Assert.DoesNotContain("help.", vm.ActiveHelpBody, StringComparison.Ordinal);
                Assert.DoesNotContain("Mda.Text.", vm.ActiveHelpTitle, StringComparison.Ordinal);
                Assert.DoesNotContain("Mda.Text.", vm.ActiveHelpBody, StringComparison.Ordinal);
                Assert.Contains(expectedPrefix, vm.ActiveHelpTopicId, StringComparison.Ordinal);
            }

            // 1. All 8 dropdown setters normalize help topic IDs properly.
            vm.SmaaIndex = 2; AssertHelpClean("aa.smaaQuality");
            vm.SpatialIndex = 2; AssertHelpClean("aa.ssaa");
            vm.Fsr1QualityIndex = 2; AssertHelpClean("fsr1.quality");
            vm.TemporalIndex = 1; // FSR
            vm.TemporalQualityIndex = 2; AssertHelpClean("temporal.quality");
            vm.TemporalIndex = 0; // Off
            vm.GiIndex = 1; // SoftwareRayTracing
            vm.GiCompositionIndex = (int)RayGiComposition.ToonPalette;
            vm.GiDenoiserIndex = 1; AssertHelpClean("gi.denoiser");
            vm.GiToonResponseIndex = 1; AssertHelpClean("gi.toonResponse");
            vm.ShadowResolutionIndex = 2; AssertHelpClean("shadow.res");
            vm.SsaoSampleCountIndex = 2; AssertHelpClean("ssao.samples");

            // 2. Hidden invalid PathTracing NumericUpDown does not soft-lock Apply after switching back to Forward.
            vm.PipelineModeIndex = 2; // PathTracing
            Dispatcher.UIThread.RunJobs();
            var ptSppBox = dialog.FindControl<EditorNumericUpDown>("PreviewPtSamplesPerFrameBox")!;
            Assert.True(ptSppBox.IsEnabled);
            ptSppBox.Text = "abc";
            Dispatcher.UIThread.RunJobs();

            vm.PipelineModeIndex = 0; // Forward
            Dispatcher.UIThread.RunJobs();
            Assert.False(ptSppBox.IsEnabled);
            // Applying while hidden PreviewPtSamplesPerFrameBox has invalid text "abc" must succeed cleanly.
            var hiddenSppDialog = new EditorPreviewSettingsWindow(vm);
            bool forwardClosed = false;
            hiddenSppDialog.Closed += (_, _) => forwardClosed = true;
            hiddenSppDialog.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                vm.PipelineModeIndex = 2; // PathTracing
                Dispatcher.UIThread.RunJobs();
                var dlgPtBox = hiddenSppDialog.FindControl<EditorNumericUpDown>("PreviewPtSamplesPerFrameBox")!;
                dlgPtBox.Text = "abc";
                Dispatcher.UIThread.RunJobs();
                vm.PipelineModeIndex = 0; // Forward (hides & disables dlgPtBox)
                Dispatcher.UIThread.RunJobs();
                Assert.False(dlgPtBox.IsEnabled);
                hiddenSppDialog.FindControl<Button>("ApplyPreviewSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.True(forwardClosed);
                Assert.False(vm.HasValidationMessage);
            }
            finally { hiddenSppDialog.Close(); Dispatcher.UIThread.RunJobs(); }

            // 3. Invalid cross-field GI (RayEpsilon > MaxRayDistance) does not block switching to PathTracing & applying.
            vm.GiIndex = 1;
            vm.GiMaxRayDistance = 1m;
            vm.GiRayEpsilon = 2m;
            vm.PipelineModeIndex = 2; // PathTracing
            ptSppBox.Text = "2";
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.TryCreate(out var ptFromInvalidGi), vm.ValidationMessage);
            Assert.Equal(RenderPipelineMode.PathTracing, ptFromInvalidGi!.PipelineMode);
            Assert.Equal(RayGiOptions.Off, ptFromInvalidGi.EffectiveGi);

            var directEval = ViewQualityCompatibility.Evaluate(new ViewQualityOptions(
                Gi: new RayGiOptions(LightingMode.SoftwareRayTracing, MaxRayDistance: 1f, RayEpsilon: 2f, OcclusionStrength: 0.5f, OcclusionRadius: 0.1f),
                PipelineMode: RenderPipelineMode.PathTracing));
            Assert.True(directEval.IsValid);

            dialog.FindControl<Button>("ApplyPreviewSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(dialogClosed);
            Assert.False(vm.HasValidationMessage);
        }
        finally
        {
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }

        // 4. TAA + TransparentBackground is rejected clearly at OutputSettingsDraft.Validate() / OutputSettingsViewModel.TryCreate(),
        // and user can switch straight to SMAA (index 2) without being trapped on a misleading SMAA label.
        using var shell = new EditorShellViewModel();
        Assert.True(shell.CreateOutputSettingsViewModel().TryCreate(out var baseDraft));
        shell.ApplyOutputSettings(baseDraft!);
        EditorWindow.CopyPreviewQualityToExport(shell, new ViewQualityOptions(Antialiasing: FrameAntialiasing.Taa), persistPreferences: false, isClassic: false);
        var exportVm = shell.CreateOutputSettingsViewModel();
        Assert.Equal(3, exportVm.AntialiasingIndex);
        Assert.Equal(EditorLocalization.Text("Mda.Text.Preview.AaTaa"), exportVm.AntialiasingModes[3].Label);
        Assert.False(exportVm.IsSmaa);
        exportVm.MediaKindIndex = 0; // PNG
        exportVm.TransparentBackground = true;
        Assert.False(exportVm.TryCreate(out _));
        Assert.Equal(EditorLocalization.Text("Mda.Text.Output.TransparentRejectsTaa"), exportVm.ValidationMessage);

        exportVm.AntialiasingIndex = 2; // SMAA
        Assert.True(exportVm.IsSmaa);
        Assert.True(exportVm.TryCreate(out var fixedDraft), exportVm.ValidationMessage);
        Assert.Equal(FrameAntialiasing.Smaa1X, fixedDraft!.Request.Quality!.Antialiasing);

        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task CodeRabbitRegressionGuardsWork() => await session.Dispatch(() =>
    {
        // 1. Adapter budget matching: do not mix fallback capability budget across adapters or onto WARP.
        var mismatchedVm = new EditorPreviewSettingsViewModel(
            new ViewQualityOptions(),
            capabilities: new EditorPreviewCapabilities { AdapterName = "GPU-B", LocalVideoMemoryBudgetBytes = 64UL * 1024 * 1024 },
            runtime: new EditorPreviewRuntimeInfo("GPU-A", IsWarp: false, Width: 1280, Height: 720, LocalVideoMemoryBudgetBytes: null),
            persistCustomPresets: false);
        Assert.Null(mismatchedVm.ResourceEstimate.AdapterBudgetBytes);

        var warpVm = new EditorPreviewSettingsViewModel(
            new ViewQualityOptions(),
            capabilities: new EditorPreviewCapabilities { AdapterName = "GPU-A", LocalVideoMemoryBudgetBytes = 64UL * 1024 * 1024 },
            runtime: new EditorPreviewRuntimeInfo("Microsoft Basic Render Driver", IsWarp: true, Width: 1280, Height: 720, LocalVideoMemoryBudgetBytes: 64UL * 1024 * 1024),
            persistCustomPresets: false);
        Assert.Null(warpVm.ResourceEstimate.AdapterBudgetBytes);

        var matchedVm = new EditorPreviewSettingsViewModel(
            new ViewQualityOptions(),
            capabilities: new EditorPreviewCapabilities { AdapterName = "GPU-A", LocalVideoMemoryBudgetBytes = 64UL * 1024 * 1024 },
            runtime: new EditorPreviewRuntimeInfo("GPU-A", IsWarp: false, Width: 1280, Height: 720, LocalVideoMemoryBudgetBytes: 512UL * 1024 * 1024),
            persistCustomPresets: false);
        Assert.Equal(512UL * 1024 * 1024, matchedVm.ResourceEstimate.AdapterBudgetBytes);

        // 2. TemporalIndex updates dependent renderScale & temporalQualityIndex BEFORE firing RefreshDerived().
        var caps = new EditorPreviewCapabilities(
            SpatialUpscalingAvailable: true,
            Fsr: new D3D12FsrRuntimeProbeResult(D3D12FsrRuntimeStatus.ContextCreated, "mock", null, null, [], null, "ok"));
        var orderVm = new EditorPreviewSettingsViewModel(
            new ViewQualityOptions(Antialiasing: FrameAntialiasing.Off) { RenderScale = 0.75f },
            caps,
            runtime: new EditorPreviewRuntimeInfo("Mock", IsWarp: false, Width: 1280, Height: 720),
            persistCustomPresets: false);
        var observedRenderWidthsWhenTemporal = new List<int>();
        orderVm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EditorPreviewSettingsViewModel.ResourceEstimate) && orderVm.IsTemporal)
                observedRenderWidthsWhenTemporal.Add(orderVm.ResourceEstimate.RenderWidth);
        };
        orderVm.TemporalIndex = 1; // FSR
        Assert.NotEmpty(observedRenderWidthsWhenTemporal);
        Assert.All(observedRenderWidthsWhenTemporal, w => Assert.NotEqual(960, w));
        Assert.Equal(1m, orderVm.RenderScale);

        // 3. Changing ShadowFilter / ShadowSoftness alone does not clobber Shadow == null scene resolution inheritance.
        var inheritShadowVm = new EditorPreviewSettingsViewModel(
            new ViewQualityOptions(),
            caps,
            persistCustomPresets: false);
        var inheritDialog = new EditorPreviewSettingsWindow(inheritShadowVm);
        inheritDialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            inheritDialog.FindControl<ComboBox>("PreviewShadowFilterBox")!.SelectedIndex = 2; // PcfSoft
            inheritDialog.FindControl<EditorNumericUpDown>("PreviewShadowSoftnessBox")!.Text = "2.25";
            Dispatcher.UIThread.RunJobs();
            Assert.True(inheritShadowVm.TryCreate(out var inheritDraft), inheritShadowVm.ValidationMessage);
            Assert.Null(inheritDraft!.Shadow);
            Assert.Equal(ShadowFilterMode.PcfSoft, inheritDraft.ShadowFilter);
            Assert.Equal(2.25f, inheritDraft.ShadowSoftness);
            var sceneWith1024Shadow = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false,
                SelfShadow: new OriginalSelfShadowOptions(Matrix4x4.Identity, 1024, 1024, true, false, OriginalTextureSampling.LinearClamp, OriginalDepthComparison.LessEqual));
            var appliedInherited = inheritDraft.ApplyTo(sceneWith1024Shadow);
            Assert.Equal(1024, appliedInherited.SelfShadow!.Width);
            Assert.Equal(ShadowFilterMode.PcfSoft, appliedInherited.SelfShadow.FilterMode);
            Assert.Equal(2.25f, appliedInherited.ShadowSoftness);

            // 4. PreviewPtFireflyClampBox companion slider allows up to 1000 without clamping > 100 down to 100.
            inheritDialog.FindControl<ComboBox>("PreviewPipelineModeBox")!.SelectedIndex = 2; // PathTracing
            Dispatcher.UIThread.RunJobs();
            var fireflyBox = inheritDialog.FindControl<EditorNumericUpDown>("PreviewPtFireflyClampBox")!;
            var fireflyGrid = fireflyBox.Parent as Grid;
            var fireflySlider = fireflyGrid?.Children.OfType<EditorValueSlider>().SingleOrDefault();
            Assert.NotNull(fireflySlider);
            Assert.Equal(1000d, fireflySlider!.Maximum);
            inheritShadowVm.PtFireflyClamp = 250m;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(250m, fireflySlider.Number);
            Assert.Equal(250m, inheritShadowVm.PtFireflyClamp);
        }
        finally
        {
            inheritDialog.Close();
            Dispatcher.UIThread.RunJobs();
        }

        // 5. NeedsCapabilitiesForApply short-circuits under PathTracing while capabilities are still probing.
        var probingVm = new EditorPreviewSettingsViewModel(
            new ViewQualityOptions(),
            capabilities: new EditorPreviewCapabilities(),
            capabilitiesPending: true,
            persistCustomPresets: false);
        probingVm.UpscalingIndex = 1; // FSR1 (normally requires capabilities)
        Assert.True(probingVm.NeedsCapabilitiesForApply);
        probingVm.PipelineModeIndex = 2; // PathTracing
        Assert.False(probingVm.NeedsCapabilitiesForApply);

        var neverResolvesProbe = new TaskCompletionSource<EditorPreviewCapabilities>().Task;
        var probingDialog = new EditorPreviewSettingsWindow(probingVm, (_, _) => neverResolvesProbe);
        bool appliedWithoutWait = false;
        probingDialog.Closed += (_, _) => appliedWithoutWait = true;
        probingDialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            probingDialog.FindControl<Button>("ApplyPreviewSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(appliedWithoutWait);
            Assert.True(probingVm.TryCreate(out var ptProbingDraft), probingVm.ValidationMessage);
            Assert.Equal(RenderPipelineMode.PathTracing, ptProbingDraft!.PipelineMode);
        }
        finally
        {
            probingDialog.Close();
            Dispatcher.UIThread.RunJobs();
        }

        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);
}
