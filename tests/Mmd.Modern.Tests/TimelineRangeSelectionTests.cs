using System.Globalization;
using System.Numerics;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class TimelineRangeSelectionTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Fact]
    public async Task NoActorHasZeroDefaultsAndCannotRunSelectionCommands() => await session.Dispatch(() =>
    {
        var viewModel = new EditorAnimationViewModel();
        Assert.Equal("0", viewModel.TimelineRangeStartText);
        Assert.Equal("0", viewModel.TimelineRangeEndText);
        Assert.False(viewModel.SelectTimelineRangeCommand.CanExecute(null));
        Assert.False(viewModel.ClearTimelineSelectionCommand.CanExecute(null));
        viewModel.SelectTimelineRangeCommand.Execute(null);
        viewModel.ClearTimelineSelectionCommand.Execute(null);
        Assert.Empty(viewModel.SelectedKeyframes);
        Assert.Empty(viewModel.TimelineRangeValidationMessage);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task InclusiveFrameRangeSelectsOnlyExistingCurrentModelBoneMorphAndIkKeysWithoutEditing() => await session.Dispatch(() =>
    {
        var viewModel = new EditorAnimationViewModel(); var original = Motion("A");
        viewModel.SetActor(Guid.NewGuid(), Model("A"), original);
        viewModel.SelectBone(0); viewModel.CurrentFrame = 7; viewModel.PositionX = 3m;
        var drafts = viewModel.SnapshotDrafts().ToArray(); var tracks = viewModel.Tracks;
        int commits = 0, poseChanges = 0;
        viewModel.CommitMotion = (_, _) => commits++;
        viewModel.PoseDraftChanged += (_, _) => poseChanges++;
        viewModel.TimelineRangeStartText = "10"; viewModel.TimelineRangeEndText = "30";
        viewModel.SelectTimelineRangeCommand.Execute(null);

        Assert.Equal(new[] { KeyframeAddress.Bone("A.Root", 10), KeyframeAddress.Bone("A.Hand", 15),
            KeyframeAddress.Morph("A.Smile", 20), KeyframeAddress.Ik(30) }, viewModel.SelectedKeyframes);
        Assert.Equal(7, viewModel.CurrentFrame); Assert.Equal(0, viewModel.SelectedBoneIndex);
        Assert.Equal(3m, viewModel.PositionX); Assert.Equal(drafts, viewModel.SnapshotDrafts());
        Assert.Equal(0, poseChanges); Assert.Equal(0, commits); Assert.Same(tracks, viewModel.Tracks);
        Assert.Equal(3, original.BoneKeyframes.Count); Assert.Single(original.MorphKeyframes); Assert.Single(original.IkKeyframes);
        Assert.Single(original.CameraKeyframes); Assert.Single(original.LightKeyframes);
        Assert.Equal(EditorLocalization.Format("Mda.Text.TimelineBatch.Selected", 4), viewModel.TimelineSelectionSummary);
        Assert.Empty(viewModel.TimelineRangeValidationMessage);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task RangeUsesFullAuthoredTracksAndShowsSelectionWhileClearOnlyChangesViewState() => await session.Dispatch(() =>
    {
        var viewModel = new EditorAnimationViewModel(); viewModel.SetActor(Guid.NewGuid(), Model("A"), Motion("A"));
        viewModel.SelectBone(0); viewModel.CurrentFrame = 6; viewModel.PositionX = 8m;
        viewModel.SetTimelineTrackMode(EditorTimelineTrackMode.FollowSelection);
        Assert.Single(viewModel.VisibleTracks);
        viewModel.TimelineRangeStartText = "10"; viewModel.TimelineRangeEndText = "30";
        viewModel.SelectTimelineRangeCommand.Execute(null);
        Assert.True(viewModel.IsTimelineAllTracks); Assert.Equal(4, viewModel.SelectedKeyframes.Count);
        Assert.Equal(0, viewModel.HiddenSelectedKeyframeCount); Assert.True(viewModel.CanModifySelection);
        viewModel.ClearTimelineSelectionCommand.Execute(null);
        Assert.Empty(viewModel.SelectedKeyframes); Assert.Equal(6, viewModel.CurrentFrame); Assert.Equal(8m, viewModel.PositionX);
        Assert.True(viewModel.HasDraft); Assert.True(viewModel.IsTimelineAllTracks);
        Assert.Equal(EditorLocalization.Format("Mda.Text.TimelineBatch.Selected", 0), viewModel.TimelineSelectionSummary);
        return true;
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("", "30", "InvalidRange")]
    [InlineData("0", "", "InvalidRange")]
    [InlineData("-1", "30", "InvalidRange")]
    [InlineData("0", "-1", "InvalidRange")]
    [InlineData("0.5", "30", "InvalidRange")]
    [InlineData("0", "1.5", "InvalidRange")]
    [InlineData("2147483648", "2147483648", "InvalidRange")]
    [InlineData("0", "999999999999999999999999", "InvalidRange")]
    [InlineData("0", "1e2", "InvalidRange")]
    [InlineData("0", "+20", "InvalidRange")]
    [InlineData("0", "二十", "InvalidRange")]
    [InlineData("31", "10", "StartAfterEnd")]
    public async Task InvalidInputPreservesSelectionFrameProjectionAndSource(string start, string end, string error) => await session.Dispatch(() =>
    {
        var viewModel = new EditorAnimationViewModel(); viewModel.SetActor(Guid.NewGuid(), Model("A"), Motion("A"));
        viewModel.SelectBone(0); viewModel.CurrentFrame = 7;
        viewModel.SelectedKeyframes = [KeyframeAddress.Bone("A.Root", 10)];
        viewModel.SetTimelineTrackMode(EditorTimelineTrackMode.FollowSelection);
        int commits = 0; viewModel.CommitMotion = (_, _) => commits++;
        viewModel.TimelineRangeStartText = start; viewModel.TimelineRangeEndText = end;
        viewModel.SelectTimelineRangeCommand.Execute(null);
        Assert.Equal(new[] { KeyframeAddress.Bone("A.Root", 10) }, viewModel.SelectedKeyframes);
        Assert.Equal(7, viewModel.CurrentFrame); Assert.True(viewModel.IsTimelineFollowSelection); Assert.Equal(0, commits);
        Assert.Equal(start, viewModel.TimelineRangeStartText); Assert.Equal(end, viewModel.TimelineRangeEndText);
        Assert.Equal(EditorLocalization.Text("Mda.Text.TimelineBatch." + error), viewModel.TimelineRangeValidationMessage);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task EmptyRangeSelectsNoKeysAndReportsItWithoutCreatingOrSeeking() => await session.Dispatch(() =>
    {
        var viewModel = new EditorAnimationViewModel(); viewModel.SetActor(Guid.NewGuid(), Model("A"), Motion("A"));
        viewModel.CurrentFrame = 7; viewModel.SelectedKeyframes = [KeyframeAddress.Bone("A.Root", 10)];
        viewModel.TimelineRangeStartText = "22"; viewModel.TimelineRangeEndText = "29";
        viewModel.SelectTimelineRangeCommand.Execute(null);
        Assert.Empty(viewModel.SelectedKeyframes); Assert.Equal(7, viewModel.CurrentFrame);
        Assert.Equal(5, viewModel.Tracks.Sum(track => track.Frames.Count));
        Assert.Equal(EditorLocalization.Text("Mda.Text.TimelineBatch.RangeEmpty"), viewModel.TimelineRangeValidationMessage);
        viewModel.TimelineRangeEndText = "30";
        Assert.Empty(viewModel.TimelineRangeValidationMessage);
        viewModel.SelectTimelineRangeCommand.Execute(null);
        Assert.Equal(new[] { KeyframeAddress.Ik(30) }, viewModel.SelectedKeyframes);
        Assert.Empty(viewModel.TimelineRangeValidationMessage);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SupportedBoundaryFramesAreNotClampedAndCameraLightAndUintOnlyKeysAreExcluded() => await session.Dispatch(() =>
    {
        var viewModel = new EditorAnimationViewModel();
        viewModel.SetActor(Guid.NewGuid(), Model("A"), new VmdMotion
        {
            BoneKeyframes = [new() { BoneName = "A.Root", Frame = 0 }, new() { BoneName = "A.Root", Frame = int.MaxValue },
                new() { BoneName = "A.Root", Frame = uint.MaxValue }],
            CameraKeyframes = [new() { Frame = int.MaxValue }], LightKeyframes = [new() { Frame = int.MaxValue }]
        });
        viewModel.TimelineRangeStartText = int.MaxValue.ToString(CultureInfo.InvariantCulture);
        viewModel.TimelineRangeEndText = int.MaxValue.ToString(CultureInfo.InvariantCulture);
        viewModel.SelectTimelineRangeCommand.Execute(null);
        Assert.Equal(new[] { KeyframeAddress.Bone("A.Root", int.MaxValue) }, viewModel.SelectedKeyframes);
        viewModel.TimelineRangeStartText = "0"; viewModel.TimelineRangeEndText = "0";
        viewModel.SelectTimelineRangeCommand.Execute(null);
        Assert.Equal(new[] { KeyframeAddress.Bone("A.Root", 0) }, viewModel.SelectedKeyframes);
        Assert.Equal(0, viewModel.CurrentFrame); Assert.Equal(3, Assert.Single(viewModel.Tracks).Frames.Count);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SwitchingModelsClearsOldSelectionAndValidationAndDefaultsToCurrentModelExtent() => await session.Dispatch(() =>
    {
        var viewModel = new EditorAnimationViewModel(); var sharedModel = Model("A");
        viewModel.SetActor(Guid.NewGuid(), sharedModel, Motion("A"));
        Assert.Equal("0", viewModel.TimelineRangeStartText); Assert.Equal("30", viewModel.TimelineRangeEndText);
        viewModel.SelectedKeyframes = [KeyframeAddress.Bone("A.Root", 10)];
        viewModel.TimelineRangeStartText = "bad"; viewModel.SelectTimelineRangeCommand.Execute(null);
        Assert.NotEmpty(viewModel.TimelineRangeValidationMessage);
        var next = Guid.NewGuid();
        viewModel.SetActor(next, sharedModel, new VmdMotion { BoneKeyframes = [new() { BoneName = "A.Root", Frame = 71 }] });
        Assert.Empty(viewModel.SelectedKeyframes); Assert.Empty(viewModel.TimelineRangeValidationMessage);
        Assert.Equal("0", viewModel.TimelineRangeStartText); Assert.Equal("71", viewModel.TimelineRangeEndText);
        viewModel.SelectTimelineRangeCommand.Execute(null);
        Assert.Equal(next, viewModel.ObjectId); Assert.Equal(new[] { KeyframeAddress.Bone("A.Root", 71) }, viewModel.SelectedKeyframes);
        viewModel.SetActor(null, null, null);
        Assert.Empty(viewModel.SelectedKeyframes); Assert.Equal("0", viewModel.TimelineRangeEndText);
        Assert.Equal(EditorLocalization.Format("Mda.Text.TimelineBatch.Selected", 0), viewModel.TimelineSelectionSummary);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task BusyOrHostBlockedCommandsDoNotChangeSelection() => await session.Dispatch(async () =>
    {
        var viewModel = new EditorAnimationViewModel(); viewModel.SetActor(Guid.NewGuid(), Model("A"), Motion("A"));
        viewModel.SelectedKeyframes = [KeyframeAddress.Bone("A.Root", 10)]; viewModel.CopyKeyframes(); viewModel.CurrentFrame = 10;
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.ConfirmOverwriteAsync = _ => confirmation.Task; viewModel.CommitMotion = (_, _) => { };
        var paste = viewModel.PasteKeyframesAsync();
        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.SelectTimelineRangeCommand.CanExecute(null)); Assert.False(viewModel.ClearTimelineSelectionCommand.CanExecute(null));
        viewModel.ClearTimelineSelectionCommand.Execute(null); viewModel.SelectTimelineRangeCommand.Execute(null);
        Assert.Equal(new[] { KeyframeAddress.Bone("A.Root", 10) }, viewModel.SelectedKeyframes);
        confirmation.SetResult(false); await paste;
        Assert.True(viewModel.SelectTimelineRangeCommand.CanExecute(null)); Assert.True(viewModel.ClearTimelineSelectionCommand.CanExecute(null));
        viewModel.CanMutateProject = () => false; viewModel.RefreshInputValidation();
        Assert.False(viewModel.SelectTimelineRangeCommand.CanExecute(null)); Assert.False(viewModel.ClearTimelineSelectionCommand.CanExecute(null));
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task RangeCopyPasteDeleteUseProductionWorkspaceUndoAndRedo() => await session.Dispatch(async () =>
    {
        using var workspace = new EditorProjectWorkspace();
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
        var asset = EditorProjectWorkspace.LoadModelAsset(path, TestContext.Current.CancellationToken);
        var id = workspace.AddModel(path, asset, ModelRole.Character); var name = asset.Model.Bones[0].Name;
        workspace.CommitMotion(id, new VmdMotion { ModelName = asset.Model.Name,
            BoneKeyframes = [new() { BoneName = name, Frame = 10, Translation = new(1, 2, 3) },
                new() { BoneName = name, Frame = 20, Translation = new(4, 5, 6) }] });
        var viewModel = new EditorAnimationViewModel { CommitMotion = workspace.CommitMotion };
        void Refresh() => viewModel.SetActor(id, asset.Model, workspace.Motions.Single(actor => actor.ObjectId == id).Motion);
        Refresh(); int changes = 0; workspace.Changed += (_, _) => changes++;
        bool dirty = workspace.IsDirty, undo = workspace.CanUndo, redo = workspace.CanRedo;
        viewModel.TimelineRangeStartText = "10"; viewModel.TimelineRangeEndText = "20";
        viewModel.SelectTimelineRangeCommand.Execute(null); viewModel.CopyKeyframes();
        Assert.Equal(0, changes); Assert.Equal((dirty, undo, redo), (workspace.IsDirty, workspace.CanUndo, workspace.CanRedo));
        viewModel.CurrentFrame = 40; await viewModel.PasteKeyframesAsync();
        Assert.Equal(new uint[] { 10, 20, 40, 50 }, workspace.Motions.Single().Motion.BoneKeyframes.Select(key => key.Frame));
        Assert.Equal(1, changes); Assert.True(workspace.Undo()); Refresh();
        Assert.Equal(new uint[] { 10, 20 }, workspace.Motions.Single().Motion.BoneKeyframes.Select(key => key.Frame));
        Assert.True(workspace.Redo()); Refresh();
        viewModel.TimelineRangeStartText = "40"; viewModel.TimelineRangeEndText = "50"; viewModel.SelectTimelineRangeCommand.Execute(null);
        await viewModel.DeleteKeyframesAsync();
        Assert.Equal(new uint[] { 10, 20 }, workspace.Motions.Single().Motion.BoneKeyframes.Select(key => key.Frame));
        Assert.True(workspace.Undo()); Refresh();
        Assert.Equal(new uint[] { 10, 20, 40, 50 }, workspace.Motions.Single().Motion.BoneKeyframes.Select(key => key.Frame));
        Assert.Equal(40, viewModel.CurrentFrame);
        return true;
    }, TestContext.Current.CancellationToken);

    private static VmdMotion Motion(string name) => new()
    {
        BoneKeyframes = [new() { BoneName = name + ".Root" }, new() { BoneName = name + ".Root", Frame = 10 },
            new() { BoneName = name + ".Hand", Frame = 15 }],
        MorphKeyframes = [new() { MorphName = name + ".Smile", Frame = 20, Weight = .5f }], IkKeyframes = [new() { Frame = 30 }],
        CameraKeyframes = [new() { Frame = 20 }], LightKeyframes = [new() { Frame = 20 }]
    };

    private static PmxModel Model(string name) => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = name, EnglishName = "", Comment = "", EnglishComment = "", Vertices = [], Faces = [], Textures = [], Materials = [],
        RigidBodies = [], Joints = [], SoftBodies = [],
        Bones = [Bone(name + ".Root"), Bone(name + ".Hand")],
        Morphs = [new() { Name = name + ".Smile", EnglishName = "", Type = PmxMorphType.Position, ControlPanel = 0 }],
        DisplayFrames = []
    };
    private static PmxBone Bone(string name) => new()
    { Name = name, EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0,
        Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate };

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
