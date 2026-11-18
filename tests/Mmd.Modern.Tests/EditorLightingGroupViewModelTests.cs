using System.Numerics;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorLightingGroupViewModelTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    [Fact]
    public async Task GroupSelectionIncludesChildrenAndGroupTogglePreservesAuthoredEnabled() => await session.Dispatch(() =>
    {
        var group = new SceneLightGroup(Guid.NewGuid(), "Face");
        var first = Light("key") with { GroupId = group.Id };
        var second = Light("fill") with { GroupId = group.Id, Enabled = false };
        var model = new EditorLightingViewModel(new([first, second], [group]));
        model.SelectRows([model.Groups[0]], model.Groups[0]);
        Assert.Equal(new[] { first.Id, second.Id }, model.SelectedLightIds);
        Assert.True(model.HasSelectedGroup);
        Assert.False(model.ShowSingleLight);
        model.Groups[0].Enabled = false;
        Assert.True(model.TryCreate(out var draft));
        Assert.True(draft!.Snapshot.Lights[0].Enabled);
        Assert.False(draft.Snapshot.Lights[1].Enabled);
        Assert.All(draft.Snapshot.EffectiveLights, light => Assert.False(light.Enabled));
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task GroupAndChildSearchKeepHierarchyAndExplicitSelectionCanReveal() => await session.Dispatch(() =>
    {
        var group = new SceneLightGroup(Guid.NewGuid(), "Face rig");
        var first = Light("key") with { GroupId = group.Id };
        var second = Light("fill") with { GroupId = group.Id };
        var model = new EditorLightingViewModel(new([first, second, Light("back")], [group]));
        model.SearchText = "FACE";
        Assert.Equal(3, model.BrowserRows.Count);
        model.SearchText = "key";
        Assert.Equal(2, model.BrowserRows.Count);
        Assert.Same(model.Groups[0], model.BrowserRows[0]);
        model.SelectMany([second.Id], second.Id, reveal: true);
        Assert.Equal("", model.SearchText);
        Assert.Equal(second.Id, model.SelectedLight!.Id);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task DuplicateGroupIsAtomicAndCopiesMembershipToANewGroup() => await session.Dispatch(() =>
    {
        var group = new SceneLightGroup(Guid.NewGuid(), "Rig", false, 2);
        var first = Light("key") with { GroupId = group.Id };
        var second = Light("fill") with { GroupId = group.Id };
        var model = new EditorLightingViewModel(new([first, second], [group]));
        model.SelectRows([model.Groups[0]], model.Groups[0]);
        Assert.True(model.DuplicateSelection());
        Assert.Equal(4, model.Lights.Count);
        Assert.Equal(2, model.Groups.Count);
        var copy = model.Groups[1];
        Assert.False(copy.Enabled); Assert.Equal(2m, copy.Intensity);
        Assert.Equal(2, model.Lights.Count(light => light.GroupId == copy.Id));
        Assert.DoesNotContain(model.SelectedLightIds, id => id == first.Id || id == second.Id);
        while (model.Lights.Count < SceneLightingSnapshot.MaxLights) model.Lights.Add(new(Light("stored") with { Enabled = false }));
        var before = model.Lights.ToArray();
        var beforeGroups = model.Groups.ToArray();
        Assert.False(model.DuplicateSelection());
        Assert.Equal(before, model.Lights);
        Assert.Equal(beforeGroups, model.Groups);
        Assert.True(model.HasValidationMessage);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task BatchBrightnessRejectsInvalidInputWithoutPartialMutation() => await session.Dispatch(() =>
    {
        var first = Light("key"); var second = Light("fill") with { Intensity = 3 };
        var model = new EditorLightingViewModel(new([first, second]));
        model.SelectMany([first.Id, second.Id], second.Id);
        Assert.False(model.SetSelectedIntensity(-1));
        Assert.Equal(new decimal?[] { 1, 3 }, model.Lights.Select(light => light.Intensity));
        Assert.True(model.SetSelectedIntensity(2));
        Assert.All(model.Lights, light => Assert.Equal(2m, light.Intensity));
        model.GroupSelection();
        Assert.Single(model.Groups);
        Assert.All(model.Lights, light => Assert.Equal(model.Groups[0].Id, light.GroupId));
        model.DissolveSelectedGroups();
        Assert.Empty(model.Groups);
        Assert.All(model.Lights, light => Assert.Null(light.GroupId));
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ReselectionIsNotAnEditAndDraftPreservesGroups() => await session.Dispatch(() =>
    {
        var group = new SceneLightGroup(Guid.NewGuid(), "Rig");
        var light = Light("key") with { GroupId = group.Id };
        var model = new EditorLightingViewModel(new([light], [group]));
        model.SelectRows([model.Groups[0]], model.Groups[0]);
        model.SelectMany([light.Id], light.Id);
        Assert.True(model.HasSelectedGroup);
        Assert.True(model.TryCreate(out var draft));
        Assert.Equal(group, Assert.Single(draft!.Snapshot.Groups));
        Assert.Equal(group.Id, Assert.Single(draft.SelectedGroupIds));
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ReadingGroupOutsideDecimalRangePreservesItsOriginalFloat() => await session.Dispatch(() =>
    {
        var group = new SceneLightGroup(Guid.NewGuid(), "HDR", Intensity: 1e30f);
        var item = new EditorLightGroupItem(group);
        Assert.Null(item.Intensity);
        Assert.Equal(group, item.Build());
        item.Enabled = false;
        Assert.Equal(group with { Enabled = false }, item.Build());
        item.Intensity = 2;
        Assert.Equal(2f, item.Build().Intensity);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task BatchBrightnessDoesNotPartiallyMutateWhenGroupMultiplicationOverflows() => await session.Dispatch(() =>
    {
        var group = new SceneLightGroup(Guid.NewGuid(), "HDR", Intensity: 1e30f);
        var first = Light("key") with { GroupId = group.Id };
        var second = Light("fill");
        var model = new EditorLightingViewModel(new([first, second], [group]));
        model.SelectMany([first.Id, second.Id]);
        Assert.False(model.SetSelectedIntensity(10000000000));
        Assert.All(model.Lights, light => Assert.Equal(1m, light.Intensity));
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task LegacySingleRemovalKeepsTheAdjacentLightSelected() => await session.Dispatch(() =>
    {
        var first = Light("key"); var second = Light("fill");
        var model = new EditorLightingViewModel(new([first, second]), first.Id);
        model.RemoveSelectedLight();
        Assert.Equal(second.Id, Assert.Single(model.Lights).Id);
        Assert.Equal(second.Id, model.SelectedLight?.Id);
        Assert.Equal(second.Id, Assert.Single(model.SelectedLightIds));
        return true;
    }, TestContext.Current.CancellationToken);

    private static NativeSceneLight Light(string name) => new(Guid.NewGuid(), name, SceneLightKind.Point,
        Vector3.One, Quaternion.Identity, Vector3.One);
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
