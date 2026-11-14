using System.Collections.Immutable;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorRecoveryNativeLightingTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RecoveryPreservesHistoricalRotationBaselineAndEveryUnfinishedLight() => await session.Dispatch(() =>
    {
        var first = Light("first"); var second = Light("second");
        var snapshot = new SceneLightingSnapshot([first, second]);
        var inspector = new EditorLightInspector(); Guid document = Guid.NewGuid();
        inspector.SetLighting(snapshot, first.Id, document);
        var model = inspector.ViewModel!;
        // A successful earlier inline commit changes the committed project, while this draft's
        // original quaternion remains its exact historical baseline.
        model.Lights[0].Intensity = 2m;
        SceneLightingSnapshot committed = snapshot;
        inspector.CommitRequested = draft => { committed = draft.Snapshot; return true; };
        Assert.True(inspector.TryCommit());
        model.Lights[0].PositionX = 12.5m;
        model.Lights[1].Name = ""; model.Lights[1].Range = null; model.Lights[1].Red = decimal.MaxValue;
        model.SelectedLight = null;
        var captured = inspector.CaptureRecoveryState(committed, EditorInspectorPage.Lighting, document);
        captured.ValidateAgainst(committed);
        Assert.Null(captured.SelectedLightId); Assert.Equal(first, captured.Lights[0].Original);
        Assert.NotEqual(captured.CommittedLights[0].Intensity, captured.Lights[0].Original.Intensity);
        var restored = new EditorLightInspector(); restored.RestoreRecoveryState(captured, committed, Guid.NewGuid());
        Assert.Null(restored.ViewModel!.SelectedLight);
        Assert.Equal(12.5m, restored.ViewModel.Lights[0].PositionX);
        Assert.Equal(first.Rotation, restored.ViewModel.Lights[0].Build().Rotation);
        Assert.Equal("", restored.ViewModel.Lights[1].Name);
        Assert.Null(restored.ViewModel.Lights[1].Range);
        Assert.Equal(decimal.MaxValue, restored.ViewModel.Lights[1].Red);
        Assert.False(restored.ViewModel.TryCreate(out _));
        Assert.Equal(Vector3.Zero, committed.Lights[0].Position);
        return true;
    }, Token);

    [Fact]
    public async Task RawInputsSurviveSelectionLayoutAndLocalizationAndExtremeRgbDoesNotOverflow() => await session.Dispatch(() =>
    {
        var first = Light("first"); var second = Light("second");
        var snapshot = new SceneLightingSnapshot([first, second]); Guid document = Guid.NewGuid();
        var inspector = new EditorLightInspector(); inspector.SetLighting(snapshot, first.Id, document);
        var window = new Window { Width = 500, Height = 1000, Content = inspector };
        try
        {
            window.Show(); Pump(window);
            Find<EditorNumericUpDown>(inspector, first.Id, "InlineLightIntensity").Text = "invalid first";
            Find<TextBox>(inspector, first.Id, "LightHexColor").Text = "";
            Pump(window);
            inspector.ViewModel!.SelectedLight = inspector.ViewModel.Lights[1]; Pump(window);
            Find<EditorNumericUpDown>(inspector, second.Id, "InlineLightIntensity").Text = "invalid second";
            inspector.ViewModel.Lights[1].Red = decimal.MaxValue;
            inspector.ViewModel.Lights[1].Green = decimal.MinValue;
            var state = inspector.CaptureRecoveryState(snapshot, EditorInspectorPage.Lighting, document);
            using (inspector.SuspendRecoveryEdits()) inspector.RestoreRecoveryState(state, snapshot, document);
            Pump(window);
            Assert.Equal(decimal.MaxValue, inspector.ViewModel!.Lights[1].Red);
            Assert.Equal(decimal.MinValue, inspector.ViewModel.Lights[1].Green);
            Assert.Equal("invalid second", Find<EditorNumericUpDown>(inspector, second.Id, "InlineLightIntensity").Text);
            inspector.ViewModel.SelectedLight = inspector.ViewModel.Lights[0]; Pump(window);
            EditorLocalization.SetLanguage("ja"); Pump(window);
            Assert.Equal("", Find<TextBox>(inspector, first.Id, "LightHexColor").Text);
            Assert.Equal("invalid first", Find<EditorNumericUpDown>(inspector, first.Id, "InlineLightIntensity").Text);
            int commits = 0; inspector.CommitRequested = _ => { commits++; return true; };
            Assert.False(inspector.TryCommit()); Assert.Equal(0, commits);
            var again = inspector.CaptureRecoveryState(snapshot, EditorInspectorPage.Lighting, document);
            Assert.Equal("", again.Lights[0].HexText);
            Assert.Equal("invalid second", again.Lights[1].NumericText["InlineLightIntensity"]);
        }
        finally { EditorLocalization.SetLanguage("zh-CN"); window.Close(); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, Token);

    [Fact]
    public async Task DifferentDocumentCannotCaptureOldHiddenLightDraftsAndBadStateDoesNotReplaceInspector() => await session.Dispatch(() =>
    {
        var light = Light("first"); var snapshot = new SceneLightingSnapshot([light]);
        var inspector = new EditorLightInspector(); inspector.SetLighting(snapshot, light.Id, Guid.NewGuid());
        inspector.ViewModel!.Lights[0].PositionX = 77m;
        var current = inspector.CaptureRecoveryState(snapshot, EditorInspectorPage.Animation, Guid.NewGuid());
        Assert.Equal(0m, current.Lights[0].PositionX); Assert.Null(current.SelectedLightId);
        var model = inspector.ViewModel;
        var mismatch = new SceneLightingSnapshot([light with { Position = Vector3.One }]);
        Assert.Throws<InvalidDataException>(() => inspector.RestoreRecoveryState(current, mismatch));
        Assert.Same(model, inspector.ViewModel); Assert.Equal(77m, inspector.ViewModel!.Lights[0].PositionX);
        Assert.Throws<InvalidDataException>(() => (current with { SelectedLightId = Guid.NewGuid() }).Validate());
        Assert.Throws<InvalidDataException>(() => (current with
        {
            Lights = [current.Lights[0] with { NumericText = ImmutableSortedDictionary<string, string>.Empty.Add("arbitrary", "text") }]
        }).Validate());
        return true;
    }, Token);

    internal static NativeSceneLight Light(string name) => new(Guid.NewGuid(), name, SceneLightKind.Spot,
        Vector3.Zero, Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(.25f, MathF.PI / 2 - .00001f, -.15f)), Vector3.One);
    private static T Find<T>(EditorLightInspector inspector, Guid id, string name) where T : Control
        => inspector.GetVisualDescendants().OfType<T>().Single(control => control.Name == name && control.DataContext is EditorNativeLightItem item && item.Id == id);
    private static void Pump(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
