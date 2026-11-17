using System.Collections.Immutable;
using System.Numerics;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorRecoveryLightGroupTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));

    [Fact]
    public async Task RecoveryKeepsGroupAuthoringAndUnfinishedInputAfterAnEarlierCommit() => await session.Dispatch(() =>
    {
        var group = new SceneLightGroup(Guid.NewGuid(), "人物柔光");
        var first = new NativeSceneLight(Guid.NewGuid(), "key", SceneLightKind.Point, Vector3.Zero,
            Quaternion.Identity, Vector3.One, GroupId: group.Id);
        var second = first with { Id = Guid.NewGuid(), Name = "fill", Enabled = false };
        var committed = new SceneLightingSnapshot([first, second], [group]);
        Guid document = Guid.NewGuid();
        var inspector = new EditorLightInspector(); inspector.SetLighting(committed, first.Id, document);
        inspector.ViewModel!.SelectGroupIds([group.Id]);
        inspector.CommitRequested = draft => { committed = draft.Snapshot; return true; };
        inspector.ViewModel.Groups[0].Intensity = 2;
        Assert.True(inspector.TryCommit());
        inspector.ViewModel.Groups[0].Name = ""; inspector.ViewModel.Groups[0].Intensity = null;
        var captured = inspector.CaptureRecoveryState(committed, EditorInspectorPage.Lighting, document) with
        { ActiveLightIds = [first.Id, second.Id], ActiveLightId = first.Id };
        captured.ValidateAgainst(committed);
        Assert.Equal(2, captured.CommittedGroups[0].Intensity);
        Assert.Equal(group, captured.GroupDrafts[0].Original); // Original samples remain distinct from committed data.
        Assert.Equal(new[] { first.Id, second.Id }, captured.SelectedLightIds);
        var restored = new EditorLightInspector(); restored.RestoreRecoveryState(captured, committed, Guid.NewGuid());
        Assert.Equal(new[] { group.Id }, restored.ViewModel!.SelectedGroupIds);
        Assert.Equal(new[] { first.Id, second.Id }, restored.ViewModel.SelectedLightIds);
        Assert.Equal("", restored.ViewModel.Groups[0].Name); Assert.Null(restored.ViewModel.Groups[0].Intensity);
        Assert.False(restored.TryCommit());
        Assert.True(committed.Lights[0].Enabled); Assert.False(committed.Lights[1].Enabled);
        Assert.Equal(2, committed.Groups[0].Intensity);
        Assert.Throws<InvalidDataException>(() => (captured with { SelectedGroupIds = [Guid.NewGuid()] }).Validate());
        Assert.Throws<InvalidDataException>(() => (captured with { ActiveLightIds = [first.Id, first.Id] }).Validate());
        Assert.Throws<InvalidDataException>(() => (captured with { GroupDrafts = [] }).Validate());
        Assert.Throws<InvalidDataException>(() => (captured with { CommittedGroups = [group with { Id = Guid.NewGuid() }] }).Validate());
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task OldUngroupedRecoveryKeepsItsSingleSelectionFallback() => await session.Dispatch(() =>
    {
        var light = new NativeSceneLight(Guid.NewGuid(), "legacy", SceneLightKind.Point, Vector3.Zero, Quaternion.Identity, Vector3.One);
        var committed = new SceneLightingSnapshot([light]);
        var inspector = new EditorLightInspector(); inspector.SetLighting(committed, light.Id);
        var legacy = inspector.CaptureRecoveryState(committed, EditorInspectorPage.Lighting) with
        { SelectedLightIds = ImmutableArray<Guid>.Empty };
        var restored = new EditorLightInspector(); restored.RestoreRecoveryState(legacy, committed);
        Assert.Equal(light.Id, restored.ViewModel!.SelectedLight!.Id);
        Assert.Equal(new[] { light.Id }, restored.ViewModel.SelectedLightIds);
        Assert.Empty(restored.ViewModel.Groups); Assert.True(restored.ViewModel.TryCreate(out _));
        return true;
    }, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
