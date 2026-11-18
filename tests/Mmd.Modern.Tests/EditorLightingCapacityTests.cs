using System.Numerics;
using Avalonia.Controls;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorLightingCapacityTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));

    [Fact]
    public async Task AuthoringCapacityIsSeparateFromEnabledCapacityAndNewLightsStartDisabledWhenFull() => await session.Dispatch(() =>
    {
        EditorLocalization.SetLanguage("zh-CN");
        var lights = Enumerable.Range(0, SceneLightingSnapshot.MaxActiveLights).Select(_ => Light()).ToArray();
        var model = new EditorLightingViewModel(new(lights)) { IsModernWorkspace = true };
        Assert.True(model.AddLight(SceneLightKind.Spot));
        Assert.Equal(33, model.Lights.Count); Assert.False(model.SelectedLight!.Enabled);
        Assert.Equal(32, model.EnabledLightCount);
        Assert.Contains("启用 32 / 32", model.LightCountSummary);
        Assert.True(model.TryCreate(out var draft)); Assert.Equal(33, draft!.Snapshot.Lights.Count);
        while (model.Lights.Count < SceneLightingSnapshot.MaxLights) Assert.True(model.AddLight(SceneLightKind.Point));
        var before = model.Lights.ToArray();
        Assert.False(model.AddLight(SceneLightKind.Point)); Assert.Equal(before, model.Lights);
        Assert.Equal(128, model.Lights.Count); Assert.Equal(32, model.EnabledLightCount);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task OverBudgetGroupEnableDissolveAndCopyAreAtomicAndFailedSwitchReturnsToAuthoredState() => await session.Dispatch(() =>
    {
        EditorLocalization.SetLanguage("zh-CN");
        var group = new SceneLightGroup(Guid.NewGuid(), "备用", Enabled: false);
        var lights = Enumerable.Range(0, 32).Select(_ => Light()).Concat(new[] { Light() with { GroupId = group.Id } }).ToArray();
        var committed = new SceneLightingSnapshot(lights, [group]);
        var model = new EditorLightingViewModel(committed); model.SelectGroupIds([group.Id]);
        Assert.False(model.SetSelectedEnabled(true)); Assert.False(model.Groups[0].Enabled);
        Assert.False(model.DissolveSelectedGroups()); Assert.Equal(group.Id, model.Lights[^1].GroupId);
        Assert.Equal(group, Assert.Single(model.Groups).Build()); Assert.Equal(32, model.EnabledLightCount);
        model.SelectMany([lights[0].Id], lights[0].Id);
        Assert.False(model.DuplicateSelection()); Assert.Equal(33, model.Lights.Count);
        Assert.Contains("32", model.ValidationMessage);
        var inspector = new EditorLightInspector(); inspector.UseWorkspaceBrowser(true); inspector.SetLighting(committed, lights[0].Id);
        var item = inspector.ViewModel!.Groups[0]; item.Enabled = true;
        var toggle = new CheckBox { Tag = "enabled", DataContext = item, IsChecked = true };
        Assert.False(inspector.CommitSwitch(toggle)); Assert.False(item.Enabled); Assert.False(toggle.IsChecked);
        Assert.True(inspector.TryCommit()); Assert.False(committed.Groups[0].Enabled);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public void FullAuthoringMarkersAndMultiSelectionRingsFitTheExistingOverlayBudget()
    {
        var lights = Enumerable.Range(0, SceneLightingSnapshot.MaxLights).Select(index => Light() with
        { Position = new((index % 16 - 8) * .3f, (index / 16 - 4) * .3f, 0), Enabled = index < 32 }).ToArray();
        var snapshot = new SceneLightingSnapshot(lights);
        var camera = new SceneCamera(new(0, 0, -12), Vector3.Zero, Vector3.UnitY, 10, .1f, 100);
        var overlay = LightViewportInteraction.CreateOverlay(snapshot, lights[0].Id, camera, 800, 600, 1,
            rotate: true, selectedIds: lights.Select(light => light.Id).ToArray(), selectionPivot: Vector3.Zero);
        // Two squares per light, plus the primary light's two-square aim target.
        Assert.Equal(258, overlay.Lines.Count(line => line.Shape == ViewportOverlayShape.Square));
        Assert.InRange(overlay.Lines.Count, 258, ViewportOverlaySnapshot.MaxLines);
    }

    private static NativeSceneLight Light() => new(Guid.NewGuid(), "fill", SceneLightKind.Spot,
        Vector3.Zero, Quaternion.Identity, Vector3.One);
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
