using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using System.Reflection;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class OutputGiSettingsTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OutputQualityProvidesEditableGiControls() => await session.Dispatch(() =>
    {
        var dialog = new OutputSettingsWindow(new OutputSettingsViewModel());
        dialog.Show();
        try
        {
            Assert.NotNull(dialog.FindControl<ComboBox>("OutputGiModeBox"));
            Assert.NotNull(dialog.FindControl<NumericUpDown>("OutputGiStrengthBox"));
            Assert.NotNull(dialog.FindControl<NumericUpDown>("OutputGiSamplesBox"));
            Assert.NotNull(dialog.FindControl<ComboBox>("OutputGiCompositionBox"));
            Assert.NotNull(dialog.FindControl<ComboBox>("OutputGiDenoiserBox"));
        }
        finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task HardwareGiReachesTheExportRequestAndPresetWithoutReducingSsaaOrChangingPreview() => await session.Dispatch(() =>
    {
        var source = Source(); var preview = source.Preset.Preview;
        var model = new OutputSettingsViewModel(source)
        {
            MediaKindIndex = (int)OutputMediaKind.PngImage,
            TransparentBackground = true,
            GiIndex = 2, GiStrength = 4.125m, GiSamples = 64,
            GiCompositionIndex = (int)RayGiComposition.ToonPalette,
            GiDenoiserIndex = (int)RayGiDenoiser.TemporalSpatial,
            AntialiasingIndex = 2, SmaaIndex = 4, SpatialIndex = 5
        };
        model.ApplyGiCapabilities(Hardware());
        Assert.True(model.TryCreate(out var result), model.ValidationMessage);
        var quality = result!.Request.Quality!;
        Assert.Equal(LightingMode.HardwareRayTracing, quality.EffectiveGi.Mode);
        Assert.Equal(64, quality.EffectiveGi.SamplesPerPixel); Assert.Equal(4.125f, quality.EffectiveGi.Strength);
        Assert.Equal(RayGiDenoiser.TemporalSpatial, quality.EffectiveGi.Denoiser);
        Assert.Equal(RayGiComposition.ToonPalette, quality.EffectiveGi.Composition);
        Assert.Equal(16, quality.SpatialSamples); Assert.Equal(SmaaQuality.Ultra, quality.SmaaQuality);
        Assert.Equal(FrameAntialiasing.Smaa1X, quality.Antialiasing);
        Assert.Equal(quality, result.Preset.Export.Quality); Assert.Equal(preview, result.Preset.Preview);
        Assert.Equal(source.Request.Quality!.EffectiveGi.Seed, quality.EffectiveGi.Seed);
        Assert.Equal(source.Request.Quality.EffectiveGi.MaxRayDistance, quality.EffectiveGi.MaxRayDistance);
        Assert.Equal(source.Request.Quality.EffectiveGi.RayEpsilon, quality.EffectiveGi.RayEpsilon);
        Assert.Equal(source.Request.Quality.EffectiveGi.ToonResponse, quality.EffectiveGi.ToonResponse);
        Assert.Equal(0, result.BackgroundColor.W);
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task UnavailableSavedHardwareGiRequiresAnExplicitSupportedChoiceAndKeepsInactiveValues() => await session.Dispatch(() =>
    {
        var source = Source(); var original = source.Preset.Export.Quality;
        var model = new OutputSettingsViewModel(source);
        model.ApplyGiCapabilities(new(new(false, true, 60, 0, false, false, true, true, false, "no DXR")));
        Assert.Equal(2, model.GiIndex); Assert.False(model.TryCreate(out _));
        Assert.True(model.HasValidationMessage); Assert.Equal(original, source.Preset.Export.Quality);
        model.GiIndex = 1;
        Assert.True(model.TryCreate(out var software), model.ValidationMessage);
        Assert.Equal(LightingMode.SoftwareRayTracing, software!.Request.Quality!.EffectiveGi.Mode);
        model.GiIndex = 0; model.GiSamples = null; model.GiStrength = null; model.GiCompositionIndex = -1;
        Assert.True(model.TryCreate(out var disabled), model.ValidationMessage);
        Assert.Equal(LightingMode.Legacy, disabled!.Request.Quality!.EffectiveGi.Mode);
        Assert.Equal(source.Request.Quality!.EffectiveGi.Strength, disabled.Request.Quality.EffectiveGi.Strength);
        Assert.Equal(source.Request.Quality.EffectiveGi.SamplesPerPixel, disabled.Request.Quality.EffectiveGi.SamplesPerPixel);
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task UntouchedOutputGiAndLocalPreferencesRoundTripEveryHiddenParameterExactly() => await session.Dispatch(() =>
    {
        var source = Source(); var model = new OutputSettingsViewModel(source);
        model.ApplyGiCapabilities(Hardware());
        Assert.True(model.TryCreate(out var result), model.ValidationMessage);
        Assert.Equal(source.Preset.Export.Quality!.Gi, result!.Request.Quality!.Gi);
        string path = Path.Combine(Path.GetTempPath(), "MDA-output-gi-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            EditorPreferences.SaveOutputSettings(result, path);
            var restored = EditorPreferences.LoadOutputSettings(out var diagnostic, path);
            Assert.Null(diagnostic); Assert.NotNull(restored);
            Assert.Equal(result.Request.Quality, restored.Request.Quality);
            Assert.Equal(result.Preset.Preview, restored.Preset.Preview);
        }
        finally { File.Delete(path); }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task TenSecondThirtyFpsIntervalAtSixtyFpsProducesSixHundredSamplesWithHalfFrameInterpolation() => await session.Dispatch(() =>
    {
        var model = new OutputSettingsViewModel
        {
            MediaKindIndex = (int)OutputMediaKind.PngSequence,
            FramesPerSecond = 60, StartFrame = 0, EndFrame = 300
        };
        Assert.True(model.TryCreate(out var result), model.ValidationMessage);
        var plan = result!.Request.Plan;
        Assert.Equal(600, plan.FrameCount);
        Assert.Equal(0, plan.GetSample(0).SampledSourceFrame.ToDouble());
        Assert.Equal(.5, plan.GetSample(1).SampledSourceFrame.ToDouble());
        Assert.Equal(299.5, plan.GetSample(plan.FrameCount - 1).SampledSourceFrame.ToDouble());
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task DeviceQueryIsLazySharedAndAClosedDialogRejectsItsLateResult() => await session.Dispatch(async () =>
    {
        int calls = 0; CancellationToken probeToken = default;
        var gate = new TaskCompletionSource<EditorPreviewCapabilities>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new OutputSettingsViewModel();
        var dialog = new OutputSettingsWindow(model, queryGiCapabilities: token =>
        { calls++; probeToken = token; return gate.Task; });
        dialog.Show();
        try
        {
            Assert.Equal(0, calls);
            var tabs = dialog.FindControl<TabControl>("OutputTabs")!;
            tabs.SelectedIndex = 1; Assert.Equal(1, calls);
            tabs.SelectedIndex = 0; tabs.SelectedIndex = 1; Assert.Equal(1, calls);
            var query = Assert.IsAssignableFrom<Task>(typeof(OutputSettingsWindow)
                .GetField("giCapabilityQuery", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog));
            dialog.Close(); Assert.True(probeToken.IsCancellationRequested);
            gate.SetResult(Hardware());
            await query.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.False(model.GiCapabilitiesReady);
        }
        finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    private static EditorPreviewCapabilities Hardware() => new(new(false, true, 65, 11, true, true, true, true, true, null));

    private static OutputSettingsDraft Source()
    {
        Assert.True(new OutputSettingsViewModel().TryCreate(out var baseline));
        var gi = new RayGiOptions(LightingMode.HardwareRayTracing, Strength: 1.2345678f, SamplesPerPixel: 37,
            Seed: 77, MaxRayDistance: 123, RayEpsilon: .0013f, DirectionalIrradiance: new(2, 3, 4),
            DirectScale: .37f, OcclusionStrength: .4f, OcclusionRadius: 2,
            Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial)
            { ToonResponse = RayGiToonResponse.PreservePaletteBands };
        var quality = baseline!.Request.Quality! with { Gi = gi };
        var preset = new ViewOutputPresetDefinition("test.output-gi", "Output GI test",
            new(640, 480, new(Gi: RayGiOptions.Off, Antialiasing: FrameAntialiasing.Smaa1X, SpatialSamples: 2)),
            baseline.Preset.Export with { Quality = quality }, baseline.Preset.Encoding);
        return baseline with { Preset = preset, Request = new(baseline.Request.Plan, quality) };
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
