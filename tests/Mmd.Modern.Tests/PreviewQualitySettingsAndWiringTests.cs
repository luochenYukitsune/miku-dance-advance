using System.Numerics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class PreviewQualityWiringAndPreferencesTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    public PreviewQualityWiringAndPreferencesTests() => session.Dispatch(
        () => EditorLocalization.Text("Mda.Text.Preview.CustomPresetReadFailed"),
        TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);

    [Fact]
    public void ViewSceneWiresShadowMipmapsOutlineAndRenderScaleCorrectly()
    {
        var box = D3D12OriginalOutlineTests.Box();
        var charFlags = new OriginalFrameMaterialFlags(true, true, false)
        {
            PreserveDirectLightPalette = true,
            UseMipmaps = true,
            OutlineWidthScale = 1f
        };
        var envFlags = new OriginalFrameMaterialFlags(true, true, false)
        {
            UseSceneLighting = true,
            UseMipmaps = true,
            OutlineWidthScale = 1f
        };
        var baseShadow = new OriginalSelfShadowOptions(Matrix4x4.Identity, 2048, 2048,
            true, false, OriginalTextureSampling.LinearClamp, OriginalDepthComparison.LessEqual);
        var options = D3D12OriginalFrameTests.Options() with { SelfShadow = baseShadow };
        var matCount = box.Surface.Materials.Count;
        var flags = Enumerable.Range(0, matCount).Select(i => i == 0 ? charFlags : envFlags).ToArray();
        var scene = new OriginalFrameScene(box.Surface, box.Outline, box.OutlineOptions, options, flags);

        // Defaults: UseEnvironmentMipmaps = false clears UseMipmaps; Outline enabled at 1x.
        var defaultResolved = D3D12ViewScene.Resolve(scene, null, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), new ViewQualityOptions());
        Assert.All(defaultResolved.Materials, m => Assert.False(m.UseMipmaps));
        Assert.True(defaultResolved.OutlineOptions!.Eligible);
        Assert.Equal(1f, defaultResolved.Options!.RenderScale);

        // Shadow 0 disables SelfShadow; UseEnvironmentMipmaps = true enables only non-character materials;
        // PreviewOutlineEnabled = false disables OutlineOptions.Eligible and zeroes OutlineWidthScale.
        var disabledQuality = new ViewQualityOptions
        {
            Shadow = new ViewShadowOptions(0, OriginalTextureSampling.PointClamp),
            UseEnvironmentMipmaps = true,
            PreviewOutlineEnabled = false,
            PreviewOutlineWidthScale = 2f,
            RenderScale = 0.75f
        };
        var resolvedDisabled = D3D12ViewScene.Resolve(scene, null, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), disabledQuality);
        Assert.Null(resolvedDisabled.Options!.SelfShadow);
        Assert.False(resolvedDisabled.Materials[0].UseMipmaps);
        for (int i = 1; i < resolvedDisabled.Materials.Count; i++)
            Assert.True(resolvedDisabled.Materials[i].UseMipmaps);
        Assert.False(resolvedDisabled.OutlineOptions!.Eligible);
        Assert.All(resolvedDisabled.Materials, m => Assert.Equal(0f, m.OutlineWidthScale));
        Assert.Equal(0.75f, resolvedDisabled.Options.RenderScale);

        // Shadow 512 PointClamp + PreviewOutlineWidthScale 2.5f.
        var scaledQuality = new ViewQualityOptions
        {
            Shadow = new ViewShadowOptions(512, OriginalTextureSampling.PointClamp),
            PreviewOutlineEnabled = true,
            PreviewOutlineWidthScale = 2.5f
        };
        var resolvedScaled = D3D12ViewScene.Resolve(scene, null, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), scaledQuality);
        Assert.NotNull(resolvedScaled.Options!.SelfShadow);
        Assert.Equal(512, resolvedScaled.Options.SelfShadow!.Width);
        Assert.Equal(512, resolvedScaled.Options.SelfShadow.Height);
        Assert.Equal(OriginalTextureSampling.PointClamp, resolvedScaled.Options.SelfShadow.Sampling);
        Assert.All(resolvedScaled.Materials, m => Assert.Equal(2.5f, m.OutlineWidthScale));
    }

    [Fact]
    public void AdaptiveVramSamplingDisablesReductionWhenFalse()
    {
        var snapshot = SceneSnapshot.CreateEmpty();
        var scene = new OriginalFrameScene(
            new(snapshot, Enumerable.Empty<OriginalStyleMaterialBinding>()),
            options: new(OriginalAlphaBlendMode.Normal, true, false, SpatialSamples: 8));

        var strictQuality = new ViewQualityOptions(SpatialSamples: 8) { AdaptiveVramSampling = false };
        var planner = new D3D12PreviewAdaptiveSampling();
        planner.Prepare(1280, 720, strictQuality, scene);
        Assert.Equal(8, planner.SpatialSamples);
        Assert.False(planner.Reduce("VRAM budget exceeded"));
        Assert.Equal(8, planner.SpatialSamples);

        var adaptiveQuality = new ViewQualityOptions(SpatialSamples: 8) { AdaptiveVramSampling = true };
        planner.Prepare(1280, 720, adaptiveQuality, scene);
        Assert.True(planner.Reduce("VRAM budget exceeded"));
        Assert.Equal(4, planner.SpatialSamples);
    }

    [Fact]
    public void RenderScaleUniformAspectRatioClampPreservesAspectAt4K()
    {
        int width = 3840, height = 2160;
        float renderScale = 1.5f;
        float clampedScale = Math.Min(renderScale, 4096f / Math.Max(width, height));
        int rw = Math.Clamp((int)MathF.Round(width * clampedScale), 1, 4096);
        int rh = Math.Clamp((int)MathF.Round(height * clampedScale), 1, 4096);
        Assert.Equal(4096, rw);
        Assert.Equal(2304, rh);
    }

    [Fact]
    public void CustomPreviewPresetsSaveOverwriteRenameAndDeleteInLocalPreferences()
    {
        string path = Path.Combine(Path.GetTempPath(), "mda-custom-presets-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Empty(EditorPreferences.LoadCustomPreviewPresets(out var diag, path));
            Assert.Null(diag);

            var qualityA = ViewQualityPresets.Create(BuiltInPreviewPresetKind.Performance);
            var created = EditorPreferences.SaveCustomPreviewPreset("快速草稿", qualityA, path: path);
            var list = EditorPreferences.LoadCustomPreviewPresets(out diag, path);
            Assert.Null(diag);
            Assert.Single(list);
            Assert.Equal("快速草稿", list[0].Name);
            Assert.Equal(qualityA, list[0].Quality);

            var qualityB = ViewQualityPresets.Create(BuiltInPreviewPresetKind.High);
            EditorPreferences.SaveCustomPreviewPreset("快速草稿", qualityB, created.Id, path);
            list = EditorPreferences.LoadCustomPreviewPresets(out diag, path);
            Assert.Single(list);
            Assert.Equal(qualityB, list[0].Quality);

            EditorPreferences.RenameCustomPreviewPreset(created.Id, "精细草稿", path);
            list = EditorPreferences.LoadCustomPreviewPresets(out diag, path);
            Assert.Single(list);
            Assert.Equal("精细草稿", list[0].Name);

            EditorPreferences.DeleteCustomPreviewPreset(created.Id, path);
            list = EditorPreferences.LoadCustomPreviewPresets(out diag, path);
            Assert.Empty(list);

            const string corruptJson = "{ invalid json";
            File.WriteAllText(path, corruptJson);
            list = EditorPreferences.LoadCustomPreviewPresets(out diag, path);
            Assert.Empty(list);
            Assert.NotNull(diag);

            // Corrupt file must NOT be overwritten by Save/Rename/Delete.
            Assert.Throws<InvalidDataException>(() => EditorPreferences.SaveCustomPreviewPreset("新预设", qualityA, path: path));
            Assert.Throws<InvalidDataException>(() => EditorPreferences.RenameCustomPreviewPreset(created.Id, "改名", path));
            Assert.Throws<InvalidDataException>(() => EditorPreferences.DeleteCustomPreviewPreset(created.Id, path));
            Assert.Equal(corruptJson, File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorPreviewSettingsUiRedesignTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SixCategoriesBuiltInPresetsCustomPresetsExportSyncGiFieldsAndLocalizationWork() => await session.Dispatch(() =>
    {
        string presetsPath = Path.Combine(Path.GetTempPath(), "mda-ui-presets-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ViewQualityOptions? exportedQuality = ViewQualityPresets.Create(BuiltInPreviewPresetKind.Performance);
            var caps = new EditorPreviewCapabilities(
                Gi: new(false, true, 65, 11, true, true, true, true, false, null), // Software GI only, no DXR
                SpatialUpscalingAvailable: true);

            var vm = new EditorPreviewSettingsViewModel(
                EditorPreferences.DefaultPreviewQuality,
                caps,
                runtime: new EditorPreviewRuntimeInfo("Mock GPU", false, 1280, 720),
                capabilitiesPending: false,
                isClassicEngine: false,
                customPresetsPath: presetsPath,
                getExportQuality: () => exportedQuality,
                copyToExportQuality: q => exportedQuality = q);

            var dialog = new EditorPreviewSettingsWindow(vm);
            dialog.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();

                // 1. Verify 6 categories switch visible panels cleanly.
                Assert.Equal(6, vm.Categories.Count);
                var panels = new[]
                {
                    dialog.FindControl<StackPanel>("CategoryOverviewPanel")!,
                    dialog.FindControl<StackPanel>("CategoryAntialiasingPanel")!,
                    dialog.FindControl<StackPanel>("CategoryShadowsPanel")!,
                    dialog.FindControl<StackPanel>("CategoryGiPanel")!,
                    dialog.FindControl<StackPanel>("CategoryPostPanel")!,
                    dialog.FindControl<StackPanel>("CategoryDiagnosticsPanel")!
                };
                for (int i = 0; i < 6; i++)
                {
                    vm.SelectedCategoryIndex = i;
                    Dispatcher.UIThread.RunJobs();
                    for (int j = 0; j < 6; j++)
                        Assert.Equal(i == j, panels[j].IsVisible);
                }

                // 2. Built-in presets (including UltraRayTraced DXR -> Software GI auto-fallback).
                vm.ApplyBuiltInPreset(BuiltInPreviewPresetKind.UltraRayTraced);
                Dispatcher.UIThread.RunJobs();
                Assert.True(vm.IsUltraRayTracedPresetActive);
                Assert.False(vm.DiffusionEnabled);
                Assert.True(vm.HasStatusMessage);
                Assert.True(vm.TryCreate(out var ultraResult), vm.ValidationMessage);
                Assert.Equal(LightingMode.SoftwareRayTracing, ultraResult!.EffectiveGi.Mode);
                Assert.Equal(RayGiToonResponse.PreservePaletteBands, ultraResult.EffectiveGi.ToonResponse);
                Assert.Equal(0.35f, ultraResult.EffectiveGi.OcclusionStrength);
                Assert.Equal(4096, ultraResult.EffectiveShadow.Resolution);
                Assert.True(ultraResult.UseEnvironmentMipmaps);

                // 3. Edit exposed GI expert fields + Shadow + Outline + RenderScale + AdaptiveVramSampling.
                vm.ApplyBuiltInPreset(BuiltInPreviewPresetKind.Balanced);
                vm.GiIndex = 1; // SoftwareRayTracing
                vm.GiCompositionIndex = (int)RayGiComposition.ToonPalette;
                vm.GiToonResponseIndex = (int)RayGiToonResponse.PreservePaletteBands;
                vm.GiOcclusionStrength = 0.6m;
                vm.GiOcclusionRadius = 2.5m;
                vm.GiMaxRayDistance = 500m;
                vm.GiDirectScale = 2.25m;
                vm.GiRayEpsilon = 0.01m;
                vm.GiSeed = 777m;
                vm.ShadowResolutionIndex = 1; // 512
                vm.ShadowSamplingIndex = 1; // PointClamp
                vm.UseEnvironmentMipmaps = true;
                vm.PreviewOutlineEnabled = true;
                vm.PreviewOutlineWidthScale = 2.5m;
                vm.RenderScale = 0.85m;
                vm.AdaptiveVramSampling = false;
                Dispatcher.UIThread.RunJobs();

                Assert.True(vm.TryCreate(out var edited), vm.ValidationMessage);
                Assert.Equal(512, edited!.EffectiveShadow.Resolution);
                Assert.Equal(OriginalTextureSampling.PointClamp, edited.EffectiveShadow.Sampling);
                Assert.True(edited.UseEnvironmentMipmaps);
                Assert.Equal(2.5f, edited.PreviewOutlineWidthScale);
                Assert.Equal(0.85f, edited.RenderScale);
                Assert.False(edited.AdaptiveVramSampling);
                Assert.Equal(RayGiToonResponse.PreservePaletteBands, edited.EffectiveGi.ToonResponse);
                Assert.Equal(0.6f, edited.EffectiveGi.OcclusionStrength);
                Assert.Equal(2.5f, edited.EffectiveGi.OcclusionRadius);
                Assert.Equal(500f, edited.EffectiveGi.MaxRayDistance);
                Assert.Equal(2.25f, edited.EffectiveGi.DirectScale);
                Assert.Equal(0.01f, edited.EffectiveGi.RayEpsilon);
                Assert.Equal(777u, edited.EffectiveGi.Seed);

                // 4. Inline conflict hints check.
                vm.GiCompositionIndex = (int)RayGiComposition.Additive;
                Dispatcher.UIThread.RunJobs();
                Assert.True(dialog.FindControl<TextBlock>("GiPaletteConflictHintText")!.IsVisible);

                vm.GiOcclusionRadius = 0.005m;
                Dispatcher.UIThread.RunJobs();
                Assert.True(dialog.FindControl<TextBlock>("GiOcclusionConflictHintText")!.IsVisible);
                vm.GiOcclusionRadius = 2.5m;
                vm.GiCompositionIndex = (int)RayGiComposition.ToonPalette;

                vm.ShadowResolutionIndex = 0;
                vm.PreviewOutlineEnabled = false;
                Dispatcher.UIThread.RunJobs();
                Assert.True(dialog.FindControl<TextBlock>("ShadowDisabledHintText")!.IsVisible);
                Assert.True(dialog.FindControl<TextBlock>("PreviewOutlineDisabledHintText")!.IsVisible);

                // 5. Custom preset Save/Overwrite/Rename/Delete + Copy/Load Export.
                vm.ShadowResolutionIndex = 3; // 2048
                vm.PreviewOutlineEnabled = true;
                vm.CustomPresetName = "我的预设";
                Assert.True(vm.SaveNewCustomPreset(), vm.ValidationMessage);
                Assert.Single(vm.CustomPresets);
                Assert.Equal(0, vm.CustomPresetIndex);
                vm.ShadowResolutionIndex = 4; // 4096
                Assert.True(vm.OverwriteSelectedCustomPreset(), vm.ValidationMessage);
                Assert.Equal(4096, vm.CustomPresets[0].Quality.EffectiveShadow.Resolution);
                vm.CustomPresetName = "重命名预设";
                Assert.True(vm.RenameSelectedCustomPreset(), vm.ValidationMessage);
                Assert.Equal("重命名预设", vm.CustomPresets[0].Name);
                Assert.Equal(0, vm.CustomPresetIndex);
                vm.ShadowResolutionIndex = 3; // 2048
                Assert.True(vm.CopyToExport(), vm.ValidationMessage);
                Assert.Equal(0.85f, exportedQuality!.RenderScale);

                exportedQuality = ViewQualityPresets.Create(BuiltInPreviewPresetKind.Performance);
                Assert.True(vm.LoadFromExport(), vm.ValidationMessage);
                Assert.Equal(1024, vm.TryCreate(out var loadedFromExp) ? loadedFromExp!.EffectiveShadow.Resolution : -1);
                vm.CustomPresetIndex = 0;
                Assert.Equal(4096, vm.TryCreate(out var reloadedCustom) ? reloadedCustom!.EffectiveShadow.Resolution : -1);
                Assert.True(vm.DeleteSelectedCustomPreset(), vm.ValidationMessage);
                Assert.Empty(vm.CustomPresets);

                // 6. 3-language localization check: zero leaked Mda.Text.* keys.
                string langBefore = EditorLocalization.CurrentLanguage;
                try
                {
                    foreach (string lang in new[] { "zh-CN", "en", "ja" })
                    {
                        EditorLocalization.SetLanguage(lang);
                        Dispatcher.UIThread.RunJobs();
                        Assert.All(vm.Categories, c => Assert.DoesNotContain("Mda.Text.", c));
                        Assert.DoesNotContain("Mda.Text.", vm.PresetsLabel);
                        Assert.DoesNotContain("Mda.Text.", vm.RenderScaleLabel);
                        Assert.DoesNotContain("Mda.Text.", vm.AdaptiveVramSamplingLabel);
                        Assert.DoesNotContain("Mda.Text.", vm.ShadowResolutionLabel);
                        Assert.DoesNotContain("Mda.Text.", vm.GiToonResponseLabel);
                    }
                }
                finally { EditorLocalization.SetLanguage(langBefore); }
            }
            finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); }
        }
        finally
        {
            if (File.Exists(presetsPath)) File.Delete(presetsPath);
        }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task ClassicEngineShowsNoticeAndDisablesModernOnlyControls() => await session.Dispatch(() =>
    {
        var vm = new EditorPreviewSettingsViewModel(
            new ViewQualityOptions(Antialiasing: FrameAntialiasing.Off, SpatialSamples: 1, SurfaceOrder: SurfaceDrawOrder.Original),
            isClassicEngine: true);
        var dialog = new EditorPreviewSettingsWindow(vm);
        dialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsClassicEngine);
            Assert.False(vm.IsModernEngine);
            Assert.True(dialog.FindControl<Border>("ClassicEngineNoticeBanner")!.IsVisible);
            Assert.False(dialog.FindControl<Button>("PresetBalancedButton")!.IsEnabled);
            Assert.False(dialog.FindControl<ComboBox>("PreviewAntialiasingBox")!.IsEnabled);
            Assert.False(dialog.FindControl<ComboBox>("PreviewShadowResolutionBox")!.IsEnabled);
            Assert.False(dialog.FindControl<ComboBox>("PreviewGiModeBox")!.IsEnabled);
            Assert.False(dialog.FindControl<CheckBox>("PreviewDiffusionEnabledBox")!.IsEnabled);
            Assert.False(dialog.FindControl<CheckBox>("PreviewAdaptiveVramSamplingBox")!.IsEnabled);
        }
        finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task ShadowNullnessUnsupportedLoadedTemporalQualityAndInvalidNumericGuardsWork() => await session.Dispatch(() =>
    {
        // 1. Untouched null Shadow stays null; explicit preset keeps explicit Shadow.
        var untouchedVm = new EditorPreviewSettingsViewModel(new ViewQualityOptions());
        Assert.True(untouchedVm.TryCreate(out var untouchedResult));
        Assert.Null(untouchedResult!.Shadow);

        untouchedVm.ApplyBuiltInPreset(BuiltInPreviewPresetKind.Balanced);
        Assert.True(untouchedVm.TryCreate(out var balancedResult));
        Assert.Equal(ViewShadowOptions.Default, balancedResult!.Shadow);

        // 2. Loading an unsupported temporal quality from export preserves it as unavailable instead of clamping to index 0.
        var exportWithUnsupportedXeSs = new ViewQualityOptions(
            TemporalUpscaling: new ViewTemporalUpscalingOptions
            {
                Mode = ViewTemporalUpscalingMode.XeSs,
                XeSsQuality = ViewXeSsQuality.UltraQualityPlus
            });
        var capsWithOnlyQualityXeSs = new EditorPreviewCapabilities(
            XeSsQueries: [new D3D12XeSsDeviceQuery("Mock", D3D12XeSsQuality.Quality) { Initialized = true }]);
        var temporalVm = new EditorPreviewSettingsViewModel(
            new ViewQualityOptions(),
            capsWithOnlyQualityXeSs,
            getExportQuality: () => exportWithUnsupportedXeSs,
            copyToExportQuality: _ => { });
        Assert.True(temporalVm.LoadFromExport());
        string expectedUnsupportedLabel = EditorLocalization.Format("{0}（不可用）", EditorLocalization.Text("超画质+"));
        Assert.Contains(temporalVm.TemporalQualities, q => q.Equals(expectedUnsupportedLabel, StringComparison.Ordinal));
        Assert.Equal(expectedUnsupportedLabel, temporalVm.TemporalQualities[temporalVm.TemporalQualityIndex]);
        Assert.False(temporalVm.TryCreate(out _));

        // 3. Invalid NumericUpDown input blocks Save/Overwrite/CopyToExport buttons.
        var guardVm = new EditorPreviewSettingsViewModel(
            EditorPreferences.DefaultPreviewQuality,
            capsWithOnlyQualityXeSs,
            copyToExportQuality: _ => throw new InvalidOperationException("Should not be called when input is invalid."));
        var dialog = new EditorPreviewSettingsWindow(guardVm);
        dialog.Show();
        try
        {
            guardVm.SelectedCategoryIndex = 2; // Shadows & Detail
            guardVm.CustomPresetName = "Test";
            Dispatcher.UIThread.RunJobs();
            var outlineBox = dialog.FindControl<Mmd.Modern.App.Controls.EditorNumericUpDown>("PreviewOutlineWidthScaleBox")!;
            outlineBox.Text = "abc";
            Dispatcher.UIThread.RunJobs();

            dialog.FindControl<Button>("SaveNewCustomPresetButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog.FindControl<Button>("CopyToExportButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(guardVm.CustomPresets);
            Assert.True(guardVm.HasValidationMessage);
        }
        finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); }

        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task InactiveCategoryAndCollapsedExpanderInvalidNumericIsRejectedAndRevealed() => await session.Dispatch(() =>
    {
        var capabilities = new EditorPreviewCapabilities(
            Gi: new D3D12RayGiCapabilities(false, true, 66, 0, false, false, true, true, false, null));
        var initial = new ViewQualityOptions(
            Gi: new RayGiOptions(LightingMode.SoftwareRayTracing));
        bool copyCalled = false;
        var vm = new EditorPreviewSettingsViewModel(
            initial,
            capabilities,
            persistCustomPresets: false,
            copyToExportQuality: _ => copyCalled = true);
        var dialog = new EditorPreviewSettingsWindow(vm);
        bool closed = false;
        dialog.Closed += (_, _) => closed = true;
        dialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, vm.SelectedCategoryIndex); // CategoryOverviewPanel active
            var lightingExpander = dialog.FindControl<Expander>("PreviewAdvancedLighting")!;
            Assert.False(lightingExpander.IsExpanded);

            var giSamplesBox = dialog.FindControl<Mmd.Modern.App.Controls.EditorNumericUpDown>("PreviewGiSamplesBox")!;
            giSamplesBox.Text = "abc";
            vm.CustomPresetName = "ShouldFail";
            Dispatcher.UIThread.RunJobs();

            void AssertRejected(string buttonName)
            {
                vm.SelectedCategoryIndex = 0;
                lightingExpander.IsExpanded = false;
                vm.SetValidationMessage("");
                Dispatcher.UIThread.RunJobs();

                dialog.FindControl<Button>(buttonName)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                Assert.False(closed);
                Assert.False(copyCalled);
                Assert.True(vm.HasValidationMessage);
                Assert.Equal(3, vm.SelectedCategoryIndex); // CategoryGiPanel revealed
                Assert.True(lightingExpander.IsExpanded);
            }

            AssertRejected("ApplyPreviewSettingsButton");
            AssertRejected("SaveNewCustomPresetButton");
            AssertRejected("CopyToExportButton");
        }
        finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); }

        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task LoadingFromExportOrPresetReplacesBaselineInsteadOfLeakingConstructorSource() => await session.Dispatch(() =>
    {
        var constructorSource = new ViewQualityOptions(
            Gi: new RayGiOptions(LightingMode.SoftwareRayTracing, Seed: 77, Strength: 2.5f),
            TemporalUpscaling: new ViewTemporalUpscalingOptions
            {
                Mode = ViewTemporalUpscalingMode.XeSs,
                XeSsQuality = ViewXeSsQuality.Performance
            },
            Diffusion: new ViewDiffusionOptions(false, 0.25f, 0.002f))
        {
            Shadow = new ViewShadowOptions(512, OriginalTextureSampling.PointClamp)
        };

        var exportOptions = new ViewQualityOptions { RenderScale = 0.8f };
        var vm = new EditorPreviewSettingsViewModel(
            constructorSource,
            new EditorPreviewCapabilities(
                Gi: new D3D12RayGiCapabilities(false, true, 66, 0, false, false, true, true, false, null),
                XeSsQueries: [new D3D12XeSsDeviceQuery("Mock", D3D12XeSsQuality.Quality) { Initialized = true }]),
            persistCustomPresets: false,
            getExportQuality: () => exportOptions);

        Assert.True(vm.LoadFromExport());
        Assert.True(vm.TryCreate(out var result));
        Assert.NotNull(result);
        Assert.Null(result!.Shadow);
        Assert.Equal(new RayGiOptions().Seed, result.EffectiveGi.Seed);
        Assert.Equal(new ViewTemporalUpscalingOptions().XeSsQuality, result.EffectiveTemporalUpscaling.XeSsQuality);
        Assert.Equal(0.8f, result.RenderScale);
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task CustomPresetCrudErrorsContainingBracesDoNotThrowFormatException() => await session.Dispatch(() =>
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "mda-brace-" + Guid.NewGuid().ToString("N"));
        string dirWithBraces = Path.Combine(tempRoot, "dir-{bad}");
        string presetsPath = Path.Combine(dirWithBraces, "presets.json");
        Directory.CreateDirectory(dirWithBraces);
        try
        {
            var vm = new EditorPreviewSettingsViewModel(
                EditorPreferences.DefaultPreviewQuality,
                customPresetsPath: presetsPath,
                persistCustomPresets: true);
            vm.CustomPresetName = "First";
            Assert.True(vm.SaveNewCustomPreset());
            vm.CustomPresetIndex = 0;

            // Replace the file with a directory so storage reads throw UnauthorizedAccessException with "{bad}" in the path.
            File.Delete(presetsPath);
            Directory.CreateDirectory(presetsPath);

            vm.CustomPresetName = "Renamed";
            Assert.False(vm.OverwriteSelectedCustomPreset());
            Assert.True(vm.HasValidationMessage);
            Assert.Contains("{bad}", vm.ValidationMessage, StringComparison.Ordinal);

            Assert.False(vm.RenameSelectedCustomPreset());
            Assert.True(vm.HasValidationMessage);
            Assert.Contains("{bad}", vm.ValidationMessage, StringComparison.Ordinal);

            Assert.False(vm.DeleteSelectedCustomPreset());
            Assert.True(vm.HasValidationMessage);
            Assert.Contains("{bad}", vm.ValidationMessage, StringComparison.Ordinal);

            vm.CustomPresetName = "Second";
            Assert.False(vm.SaveNewCustomPreset());
            Assert.True(vm.HasValidationMessage);
            Assert.Contains("{bad}", vm.ValidationMessage, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public async Task CustomPresetPreferenceErrorsRespectLocalization(string language) => await session.Dispatch(() =>
    {
        string languageBefore = EditorLocalization.CurrentLanguage;
        string tempRoot = Path.Combine(Path.GetTempPath(), "mda-loc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            EditorLocalization.SetLanguage(language);

            var duplicate = new EditorPreviewCustomPreset("dup", "One", EditorPreferences.DefaultPreviewQuality);
            var dupError = Assert.Throws<EditorLocalizedArgumentException>(() =>
                EditorPreferences.SaveCustomPreviewPresets([duplicate, duplicate with { Name = "Two" }], Path.Combine(tempRoot, "dup.json")));
            Assert.Equal(EditorLocalization.Text("Mda.Text.Preview.CustomPresetDuplicateId"), dupError.Message);

            var renameBlank = Assert.Throws<EditorLocalizedArgumentException>(() =>
                EditorPreferences.RenameCustomPreviewPreset(" ", "Name", Path.Combine(tempRoot, "none.json")));
            Assert.Equal(EditorLocalization.Text("Mda.Text.Preview.CustomPresetSelectRename"), renameBlank.Message);

            var deleteBlank = Assert.Throws<EditorLocalizedArgumentException>(() =>
                EditorPreferences.DeleteCustomPreviewPreset("", Path.Combine(tempRoot, "none.json")));
            Assert.Equal(EditorLocalization.Text("Mda.Text.Preview.CustomPresetSelectDelete"), deleteBlank.Message);

            var notFound = Assert.Throws<KeyNotFoundException>(() =>
                EditorPreferences.RenameCustomPreviewPreset("missing-id", "Name", Path.Combine(tempRoot, "missing.json")));
            Assert.Equal(EditorLocalization.Text("Mda.Text.Preview.CustomPresetNotFound"), notFound.Message);

            string badJsonPath = Path.Combine(tempRoot, "bad.json");
            File.WriteAllText(badJsonPath, "{not valid json");
            _ = EditorPreferences.LoadCustomPreviewPresets(out string? diagnostic, badJsonPath);
            Assert.NotNull(diagnostic);
            Assert.StartsWith(EditorLocalization.Text("Mda.Text.Preview.CustomPresetReadFailed"), diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            EditorLocalization.SetLanguage(languageBefore);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task LoadCustomPreviewPresetsRecoversCleanlyOnNullEmptyOrDuplicateIdsAndGuardsWrites() => await session.Dispatch(() =>
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "mda-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string seedPath = Path.Combine(tempRoot, "seed.json");
            var first = EditorPreferences.SaveCustomPreviewPreset("One", ViewQualityPresets.Create(BuiltInPreviewPresetKind.Performance), "id-one", seedPath);
            var second = EditorPreferences.SaveCustomPreviewPreset("Two", ViewQualityPresets.Create(BuiltInPreviewPresetKind.Balanced), "id-two", seedPath);
            string duplicateIdJson = File.ReadAllText(seedPath).Replace("\"id-two\"", "\"id-one\"", StringComparison.Ordinal);

            foreach (string payload in new[] { "null", "", duplicateIdJson })
            {
                string path = Path.Combine(tempRoot, Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(path, payload);

                var loaded = EditorPreferences.LoadCustomPreviewPresets(out string? diagnostic, path);
                Assert.Empty(loaded);
                Assert.NotNull(diagnostic);

                var vm = new EditorPreviewSettingsViewModel(
                    EditorPreferences.DefaultPreviewQuality,
                    customPresetsPath: path,
                    persistCustomPresets: true);
                Assert.Empty(vm.CustomPresets);

                Assert.Throws<InvalidDataException>(() =>
                    EditorPreferences.SaveCustomPreviewPreset("New", EditorPreferences.DefaultPreviewQuality, path: path));
                Assert.Throws<InvalidDataException>(() =>
                    EditorPreferences.RenameCustomPreviewPreset(first.Id, "Renamed", path));
                Assert.Throws<InvalidDataException>(() =>
                    EditorPreferences.DeleteCustomPreviewPreset(second.Id, path));
                Assert.Equal(payload, File.ReadAllText(path));
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task NeedsCapabilitiesForApplyRemainsTrueAcrossPresetAndExportLoadsWhileProbing() => await session.Dispatch(() =>
    {
        var exportHwGi = new ViewQualityOptions(Gi: new RayGiOptions(LightingMode.HardwareRayTracing));
        var vm = new EditorPreviewSettingsViewModel(
            new ViewQualityOptions(),
            capabilities: null,
            capabilitiesPending: true,
            persistCustomPresets: false,
            getExportQuality: () => exportHwGi);

        vm.ApplyBuiltInPreset(BuiltInPreviewPresetKind.UltraRayTraced);
        Assert.True(vm.NeedsCapabilitiesForApply);

        vm.ApplyBuiltInPreset(BuiltInPreviewPresetKind.Performance);
        Assert.False(vm.NeedsCapabilitiesForApply);

        Assert.True(vm.LoadFromExport());
        Assert.True(vm.NeedsCapabilitiesForApply);

        vm.ApplyBuiltInPreset(BuiltInPreviewPresetKind.UltraRayTraced);
        Assert.True(vm.NeedsCapabilitiesForApply);

        var softwareOnly = new EditorPreviewCapabilities(
            Gi: new D3D12RayGiCapabilities(false, true, 66, 0, false, false, true, true, false, null));
        vm.ApplyCapabilities(softwareOnly);
        Assert.False(vm.NeedsCapabilitiesForApply);
        Assert.True(vm.TryCreate(out var resolved));
        Assert.Equal(LightingMode.SoftwareRayTracing, resolved!.EffectiveGi.Mode);
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task CopyToExportCatchesUnauthorizedAccessAndKeepsShellOutputSettingsConsistent() => await session.Dispatch(() =>
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "mda-export-sync-" + Guid.NewGuid().ToString("N"));
        string blockedOutputPath = Path.Combine(tempRoot, "blocked-output.json");
        Directory.CreateDirectory(blockedOutputPath);
        try
        {
            using var shell = new EditorShellViewModel();
            Assert.True(shell.CreateOutputSettingsViewModel().TryCreate(out var initialDraft));
            Assert.NotNull(initialDraft);
            shell.ApplyOutputSettings(initialDraft!);

            var vm = new EditorPreviewSettingsViewModel(
                ViewQualityPresets.Create(BuiltInPreviewPresetKind.High),
                persistCustomPresets: false,
                copyToExportQuality: quality => EditorWindow.CopyPreviewQualityToExport(
                    shell, quality, persistPreferences: true, isClassic: false, outputSettingsPath: blockedOutputPath));

            Assert.False(vm.CopyToExport());
            Assert.True(vm.HasValidationMessage);
            Assert.Same(initialDraft, shell.OutputSettings);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}

[Trait("Category", "Gpu")]
public sealed class D3D12RenderScaleGpuTests
{
    [Theory]
    [InlineData(0.75f, 96, 54)]
    [InlineData(1.25f, 160, 90)]
    public void RenderScaleRendersAtScaledResolutionAndOutputsExactDisplayDimensions(
        float renderScale, int expectedInputWidth, int expectedInputHeight)
    {
        var box = D3D12OriginalOutlineTests.Box();
        var options = D3D12OriginalFrameTests.Options() with { RenderScale = renderScale };
        var scene = new OriginalFrameScene(box.Surface, box.Outline, box.OutlineOptions, options, box.Materials);
        using var session = new D3D12FrameSession();
        var captured = session.Capture(scene, 128, 72);
        Assert.Equal(128, captured.Width);
        Assert.Equal(72, captured.Height);
        Assert.NotNull(session.Statistics);
        Assert.Equal(expectedInputWidth, session.Statistics!.UpscalingInputWidth);
        Assert.Equal(expectedInputHeight, session.Statistics.UpscalingInputHeight);
        Assert.Equal(128, session.Statistics.UpscalingOutputWidth);
        Assert.Equal(72, session.Statistics.UpscalingOutputHeight);
    }

    [Fact]
    public void RenderScaleWithStraightAlphaRecordsStraightAlphaAtDisplayDimensions()
    {
        var box = D3D12OriginalOutlineTests.Box();
        var options = D3D12OriginalFrameTests.Options() with
        {
            RenderScale = 0.75f,
            AlphaOutput = FrameAlphaOutput.Straight
        };
        var scene = new OriginalFrameScene(box.Surface, box.Outline, box.OutlineOptions, options, box.Materials);
        using var session = new D3D12FrameSession();
        var captured = session.Capture(scene, 128, 72);
        Assert.Equal(128, captured.Width);
        Assert.Equal(72, captured.Height);
        var straightAlphaTarget = Assert.Single(session.LastTargets, target => target.Name == "$straight-alpha");
        Assert.Equal(128, straightAlphaTarget.Width);
        Assert.Equal(72, straightAlphaTarget.Height);
    }
}
