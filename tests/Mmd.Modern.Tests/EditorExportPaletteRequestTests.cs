using System.Reflection;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorExportPaletteRequestTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public void CharacterReceiverUsesPaletteInDetachedOutputRequestWithoutChangingSavedOrPreviewQuality()
    {
        var preview = new ViewQualityOptions(Gi: new RayGiOptions(LightingMode.Legacy,
            Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial));
        var quality = new ViewQualityOptions(
            Gi: new RayGiOptions(LightingMode.HardwareRayTracing, Strength: 4, SamplesPerPixel: 16,
                Composition: RayGiComposition.Additive, Denoiser: RayGiDenoiser.Off),
            Antialiasing: FrameAntialiasing.Smaa1X, SmaaQuality: SmaaQuality.Ultra, SpatialSamples: 32);
        var request = new ExportRequest(new OfflineFramePlan(1920, 1080, new(60), 0, 4706, includeEndFrame: false), quality);
        var preset = new ViewOutputPresetDefinition("test.palette-output", "Palette output",
            new ViewRenderRequest(1280, 720, preview), new(1920, 1080, new(60), quality));
        var draft = new OutputSettingsDraft(preset, request, OutputMediaKind.PngSequence, "output-frames",
            false, Vector4.One, null);

        var output = BuildRenderRequest(draft.Request, hasPaletteReceiver: true);

        Assert.Equal(RayGiComposition.ToonPalette, output.EffectiveQuality.EffectiveGi.Composition);
        Assert.Equal(quality.EffectiveGi with { Composition = RayGiComposition.ToonPalette }, output.EffectiveQuality.EffectiveGi);
        Assert.Equal(32, output.EffectiveQuality.SpatialSamples);
        Assert.Equal(SmaaQuality.Ultra, output.EffectiveQuality.SmaaQuality);
        Assert.Equal(RayGiComposition.Additive, request.Quality!.EffectiveGi.Composition);
        Assert.Same(request, draft.Request);
        Assert.Equal(RayGiComposition.Additive, draft.Preset.Export.Quality!.EffectiveGi.Composition);
        Assert.Equal(RayGiComposition.ToonPalette, draft.Preset.Preview.EffectiveQuality.EffectiveGi.Composition);
        Assert.Equal(RayGiDenoiser.TemporalSpatial, draft.Preset.Preview.EffectiveQuality.EffectiveGi.Denoiser);

        var effective = EditorExportService.BuildExportRequest(draft.Request, hasPaletteReceiver: true);
        Assert.NotSame(draft.Request, effective);
        Assert.Same(draft.Request.Plan, effective.Plan);
        Assert.Equal(output, effective.Render);
        Assert.Equal(RayGiComposition.ToonPalette, effective.Quality!.EffectiveGi.Composition);
        Assert.Equal(RayGiComposition.Additive, draft.Request.Quality!.EffectiveGi.Composition);
    }

    [Theory]
    [InlineData(false, LightingMode.HardwareRayTracing, 4)]
    [InlineData(true, LightingMode.Legacy, 4)]
    [InlineData(true, LightingMode.HardwareRayTracing, 0)]
    public void EnvironmentOnlyOrInactiveGiKeepsRequestedComposition(bool hasPaletteReceiver, LightingMode mode, int strength)
    {
        var request = new ExportRequest(new OfflineFramePlan(64, 64, new(30), 0, 0),
            new ViewQualityOptions(Gi: new RayGiOptions(mode, Strength: strength, Composition: RayGiComposition.Additive)));
        Assert.Equal(request.Render, BuildRenderRequest(request, hasPaletteReceiver));
        Assert.Same(request, EditorExportService.BuildExportRequest(request, hasPaletteReceiver));
    }

    [Fact]
    public void ReceiverDetectionUsesEffectivePurposeAndPreservesExplicitPropPolicy()
    {
        var raw = D3D12OriginalOutlineTests.Box();
        var flagged = new OriginalFrameScene(raw.Surface, raw.Outline, raw.OutlineOptions, raw.Options,
            raw.Materials.Select(flags => flags with { GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands }));
        using var overriddenEnvironment = Project(ModelRole.Character, flagged, ModelRole.Environment);
        using var overriddenCharacter = Project(ModelRole.Environment, raw, ModelRole.Character);
        using var explicitProp = Project(ModelRole.Prop, flagged, null);
        using var environment = Project(ModelRole.Environment, raw, null);
        Assert.False(HasPaletteReceiver(overriddenEnvironment));
        Assert.True(HasPaletteReceiver(overriddenCharacter));
        Assert.True(HasPaletteReceiver(explicitProp));
        Assert.False(HasPaletteReceiver(environment));
    }

    [Fact]
    public async Task EffectiveCompositionNoticeTracksTheRequestedSettings() => await session.Dispatch(() =>
    {
        var model = new OutputSettingsViewModel(hasPaletteReceiver: true)
        {
            GiIndex = 2, GiStrength = 4, GiCompositionIndex = (int)RayGiComposition.Additive
        };
        Assert.True(model.ShowGiPaletteNotice);
        Assert.False(string.IsNullOrWhiteSpace(model.GiPaletteNotice));
        Assert.False(string.IsNullOrWhiteSpace(model.GiCompositionScope));
        Assert.NotEqual("Mda.Text.OutputGi.PaletteNotice", model.GiPaletteNotice);
        Assert.NotEqual("Mda.Text.OutputGi.CompositionScope", model.GiCompositionScope);
        model.GiCompositionIndex = (int)RayGiComposition.ToonPalette;
        Assert.False(model.ShowGiPaletteNotice);
        model.GiCompositionIndex = (int)RayGiComposition.Additive;
        model.GiStrength = 0;
        Assert.False(model.ShowGiPaletteNotice);
        model.GiStrength = 4;
        model.GiIndex = 0;
        Assert.False(model.ShowGiPaletteNotice);
        var environment = new OutputSettingsViewModel(hasPaletteReceiver: false)
        { GiIndex = 2, GiStrength = 4, GiCompositionIndex = (int)RayGiComposition.Additive };
        Assert.False(environment.ShowGiPaletteNotice);
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    [Fact]
    public async Task ProjectPurposeReachesOutputWindowNotice() => await session.Dispatch(() =>
    {
        var raw = D3D12OriginalOutlineTests.Box();
        using var project = Project(ModelRole.Environment, raw, ModelRole.Character);
        var shell = new EditorShellViewModel(raw.Surface.Snapshot.Camera, project);
        var model = shell.CreateOutputSettingsViewModel();
        model.GiIndex = 2; model.GiStrength = 4; model.GiCompositionIndex = (int)RayGiComposition.Additive;
        var dialog = new OutputSettingsWindow(model);
        dialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var notice = dialog.FindControl<TextBlock>("OutputGiPaletteNotice");
            Assert.NotNull(notice);
            Assert.True(notice.IsVisible);
            Assert.Equal(model.GiPaletteNotice, notice.Text);
            model.GiCompositionIndex = (int)RayGiComposition.ToonPalette;
            Dispatcher.UIThread.RunJobs();
            Assert.False(notice.IsVisible);
        }
        finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    private static SceneProjectSession Project(ModelRole role, OriginalFrameScene source, ModelRole? overrideRole)
    {
        var layout = new SceneCompositionEditor();
        var item = layout.Add("fixture", "fixture.pmx", role, source);
        var lighting = new SceneLightingEditor();
        if (overrideRole is { } effectiveRole) lighting.SetModelRole(item.Key, effectiveRole);
        return new SceneProjectSession(layout, lighting, [], new ProjectSettingsBackendProvider(
            (_, _) => throw new InvalidOperationException("Static fixture does not create physics."),
            _ => throw new InvalidOperationException("Static fixture does not create physics.")));
    }

    private static bool HasPaletteReceiver(SceneProjectSession project)
    {
        var method = typeof(EditorExportService).GetMethod("HasPaletteReceiver", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method.Invoke(null, [project]));
    }

    private static ViewRenderRequest BuildRenderRequest(ExportRequest request, bool hasPaletteReceiver)
    {
        var method = typeof(EditorExportService).GetMethod("BuildRenderRequest", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<ViewRenderRequest>(method.Invoke(null, [request, hasPaletteReceiver]));
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
