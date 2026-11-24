using System.Numerics;
using System.Reflection;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class TimelineProjectionViewModelTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Fact]
    public async Task DefaultShowsAllCurrentModelAuthoredChannelsInNativeCategories() => await session.Dispatch(() =>
    {
        var viewModel = new EditorAnimationViewModel();
        viewModel.SetActor(Guid.NewGuid(), Model("A"), Motion("A"));
        Assert.Equal(EditorTimelineTrackMode.AllTracks, viewModel.TimelineTrackMode);
        Assert.True(viewModel.IsTimelineAllTracks); Assert.False(viewModel.IsTimelineFollowSelection);
        Assert.Equal(4, viewModel.Tracks.Count); Assert.Equal(5, viewModel.VisibleTracks.Count);
        Assert.Equal(5, viewModel.VisibleTracks.Sum(track => track.Frames.Count));
        Assert.Equal(new[] { "Body", "Expressions", EditorLocalization.Text("Mda.Text.Timeline.IkDisplay") }, viewModel.TimelineGroups.Select(group => group.Label));
        Assert.Equal(new[] { "A.Root", "A.Hand", "A.Empty" }, viewModel.TimelineGroups[0].Tracks.Select(track => track.Name));
        Assert.Equal(KeyframeTrackKind.Morph, Assert.Single(viewModel.TimelineGroups[1].Tracks).Kind);
        Assert.Equal(KeyframeTrackKind.Ik, Assert.Single(viewModel.TimelineGroups[2].Tracks).Kind);
        Assert.DoesNotContain(viewModel.Tracks, track => track.Name == "A.Empty");
        Assert.Equal(5, viewModel.TimelineGroups.SelectMany(group => group.Tracks).Sum(track => track.Frames.Count));
        Assert.False(viewModel.ShowTimelineEmptyHint);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ChannelSelectionRetainsExactAuthoredTracksAndCachedCategoryObjects() => await session.Dispatch(() =>
    {
        var viewModel = new EditorAnimationViewModel(); var id = Guid.NewGuid(); var model = Model("A"); var motion = Motion("A");
        viewModel.SetActor(id, model, motion);
        var tracks = viewModel.Tracks; var groups = viewModel.TimelineGroups;
        var children = groups.SelectMany(group => group.Tracks).ToArray();
        for (int index = 0; index < 3; index++)
        {
            viewModel.SelectBone(index);
            Assert.Same(tracks, viewModel.Tracks); Assert.Same(groups, viewModel.TimelineGroups);
            Assert.Equal(5, viewModel.VisibleTracks.Count);
        }
        viewModel.SelectMorph(0); viewModel.CurrentFrame = 10;
        viewModel.SelectTimelineKeys([KeyframeAddress.Bone("A.Root", 10)]);
        Assert.Equal(0, viewModel.SelectedBoneIndex);
        Assert.Same(tracks, viewModel.Tracks); Assert.Same(groups, viewModel.TimelineGroups);
        Assert.Equal(children.Length, groups.SelectMany(group => group.Tracks).Count());
        foreach (var child in children) Assert.Contains(groups.SelectMany(group => group.Tracks), item => ReferenceEquals(item, child));
        viewModel.SetActor(id, model, motion, "Display name only");
        Assert.Same(tracks, viewModel.Tracks); Assert.Same(groups, viewModel.TimelineGroups);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task OptionalFollowSelectionShowsOnlyItsChannelAndAnEmptyUnkeyedPlaceholder() => await session.Dispatch(() =>
    {
        var viewModel = new EditorAnimationViewModel(); viewModel.SetActor(Guid.NewGuid(), Model("A"), Motion("A"));
        var authored = viewModel.Tracks; var groups = viewModel.TimelineGroups;
        viewModel.SetTimelineTrackMode(EditorTimelineTrackMode.FollowSelection);
        Assert.Empty(viewModel.VisibleTracks); Assert.Empty(viewModel.TimelineGroups);
        viewModel.SelectBone(0);
        Assert.Equal("A.Root", Assert.Single(viewModel.VisibleTracks).Name);
        Assert.Equal(new uint[] { 0, 10 }, viewModel.VisibleTracks[0].Frames);
        viewModel.SelectMorph(0);
        Assert.Equal(KeyframeTrackKind.Morph, Assert.Single(viewModel.VisibleTracks).Kind);
        viewModel.SelectBone(2);
        Assert.Equal("A.Empty", Assert.Single(viewModel.VisibleTracks).Name); Assert.Empty(viewModel.VisibleTracks[0].Frames);
        Assert.Same(authored, viewModel.Tracks); Assert.DoesNotContain(viewModel.Tracks, track => track.Name == "A.Empty");
        viewModel.SetAnimationContextActive(false); Assert.Empty(viewModel.VisibleTracks);
        viewModel.SetTimelineTrackMode(EditorTimelineTrackMode.AllTracks);
        Assert.Same(groups, viewModel.TimelineGroups); Assert.Equal(5, viewModel.VisibleTracks.Count);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SwitchingModelInstancesNeverRetainsPreviousActorKeysOrGroupIdentity() => await session.Dispatch(() =>
    {
        var viewModel = new EditorAnimationViewModel(); var model = Model("A"); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        viewModel.SetActor(first, model, Motion("A")); var originalKeys = viewModel.TimelineGroups.Select(group => group.Key).ToArray();
        viewModel.SelectedKeyframes = [KeyframeAddress.Bone("A.Root", 10)];
        viewModel.SetActor(second, model, new VmdMotion { BoneKeyframes = [new() { BoneName = "A.Root", Frame = 71 }] });
        Assert.Empty(viewModel.SelectedKeyframes); Assert.Equal(second, viewModel.ObjectId);
        Assert.Equal(new uint[] { 71 }, Assert.Single(viewModel.Tracks).Frames);
        Assert.All(viewModel.TimelineGroups, group => Assert.StartsWith(second.ToString("D"), group.Key));
        Assert.DoesNotContain(viewModel.TimelineGroups, group => originalKeys.Contains(group.Key));
        viewModel.SetActor(Guid.NewGuid(), Model("B"), Motion("B"));
        Assert.DoesNotContain(viewModel.TimelineGroups.SelectMany(group => group.Tracks), track => track.Name.StartsWith("A.", StringComparison.Ordinal));
        Assert.Contains(viewModel.TimelineGroups.SelectMany(group => group.Tracks), track => track.Name == "B.Root");
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task NewMotionAndDetachmentInvalidateCachedTracksAndSamplingBeforeSelection() => await session.Dispatch(() =>
    {
        var viewModel = new EditorAnimationViewModel(); var model = Model("A"); var id = Guid.NewGuid();
        viewModel.SetActor(id, model, Motion("A")); viewModel.SelectBone(0); viewModel.CurrentFrame = 10;
        viewModel.SelectedKeyframes = [KeyframeAddress.Bone("A.Root", 10)];
        var originalTracks = viewModel.Tracks; var originalGroups = viewModel.TimelineGroups;
        var updated = new VmdMotion
        {
            BoneKeyframes = [new() { BoneName = "A.Root", Frame = 55, Translation = new(9, 0, 0) }, new() { BoneName = "A.Hand", Frame = 60 }],
            MorphKeyframes = [new() { MorphName = "A.Smile", Frame = 70, Weight = .75f }]
        };
        viewModel.SetActor(id, model, updated); viewModel.CurrentFrame = 55;
        Assert.NotSame(originalTracks, viewModel.Tracks); Assert.NotSame(originalGroups, viewModel.TimelineGroups);
        Assert.Empty(viewModel.SelectedKeyframes); Assert.Equal(9m, viewModel.PositionX);
        Assert.Contains(viewModel.TimelineGroups.SelectMany(group => group.Tracks), track => track.Name == "A.Root" && track.Frames.SequenceEqual(new uint[] { 55 }));
        var beforeDetachment = viewModel.TimelineGroups;
        viewModel.SetActor(id, model, updated, actorDetachment: new SceneMotionDetachment(["A.Hand"], [], [], []));
        Assert.NotSame(beforeDetachment, viewModel.TimelineGroups);
        Assert.DoesNotContain(viewModel.Tracks, track => track.Name == "A.Hand");
        viewModel.SetActor(id, model, updated); // An explicit restored source revokes the detached filter.
        Assert.Contains(viewModel.Tracks, track => track.Name == "A.Hand" && track.Frames.Contains(60u));
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SelectionValidationAndHiddenKeyCopyRetainCompleteActualAddresses() => await session.Dispatch(async () =>
    {
        var viewModel = new EditorAnimationViewModel(); var motion = Motion("A");
        viewModel.SetActor(Guid.NewGuid(), Model("A"), motion);
        var selected = new[] { KeyframeAddress.Bone("A.Root", 10), KeyframeAddress.Morph("A.Smile", 20), KeyframeAddress.Ik(30) };
        viewModel.SelectedKeyframes = selected.Concat(new[] { selected[0], KeyframeAddress.Bone("missing", 10), KeyframeAddress.Bone("A.Root", 999) }).ToArray();
        Assert.Equal(selected, viewModel.SelectedKeyframes); Assert.True(viewModel.CanModifySelection);
        viewModel.SelectBone(0); viewModel.SetTimelineTrackMode(EditorTimelineTrackMode.FollowSelection);
        Assert.Equal(2, viewModel.HiddenSelectedKeyframeCount); Assert.False(viewModel.HasHiddenSelectedBoneKeyframes);
        Assert.False(viewModel.CanModifySelection); Assert.True(viewModel.CanEditSelection);
        int commits = 0; viewModel.CommitMotion = (_, _) => commits++;
        await viewModel.DeleteKeyframesAsync(); Assert.Equal(0, commits); Assert.Equal(selected, viewModel.SelectedKeyframes);
        viewModel.CopyKeyframes(); Assert.True(viewModel.CanPaste);
        var copied = (MotionClipboard)typeof(EditorAnimationViewModel).GetField("clipboard", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewModel)!;
        Assert.Equal("A.Root", Assert.Single(copied.BoneKeyframes).BoneName);
        Assert.Equal("A.Smile", Assert.Single(copied.MorphKeyframes).MorphName); Assert.Equal(30u, Assert.Single(copied.IkKeyframes).Frame);
        viewModel.SelectBone(1); Assert.Equal(3, viewModel.HiddenSelectedKeyframeCount); Assert.True(viewModel.HasHiddenSelectedBoneKeyframes);
        viewModel.SetTimelineTrackMode(EditorTimelineTrackMode.AllTracks);
        Assert.Equal(0, viewModel.HiddenSelectedKeyframeCount); Assert.True(viewModel.CanModifySelection);
        Assert.Equal(selected, viewModel.SelectedKeyframes); Assert.Equal(0, commits);
        Assert.Equal(3, motion.BoneKeyframes.Count); Assert.Single(motion.MorphKeyframes); Assert.Single(motion.IkKeyframes);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task KeyedFilterKeepsAuthorOrderAndFocusMembershipOfAnUnkeyedBone() => await session.Dispatch(() =>
    {
        var animation = new EditorAnimationViewModel();
        animation.SetActor(Guid.NewGuid(), Model("A"), Motion("A"));
        animation.SelectBone(2);
        var groups = animation.TimelineGroups;
        Assert.Empty(groups[0].Tracks.Single(track => track.Name == "A.Empty").Frames);
        animation.SetTimelineTrackMode(EditorTimelineTrackMode.KeyedTracks);
        Assert.Equal(new[] { "A.Root", "A.Hand" }, animation.TimelineGroups[0].Tracks.Select(track => track.Name));
        Assert.Equal(groups[0].Key, animation.TimelineGroups[0].Key);
        Assert.True(animation.TimelineGroups[0].ContainsMember(animation.TimelineFocusedChannel!.Value));
        Assert.DoesNotContain(animation.VisibleTracks, track => track.Name == "A.Empty");
        animation.SetTimelineTrackMode(EditorTimelineTrackMode.AllTracks);
        Assert.Same(groups, animation.TimelineGroups);
        Assert.Equal(2, animation.SelectedBoneIndex);
        return true;
    }, TestContext.Current.CancellationToken);

    private static VmdMotion Motion(string name) => new()
    {
        BoneKeyframes = [new() { BoneName = name + ".Root" }, new() { BoneName = name + ".Root", Frame = 10 }, new() { BoneName = name + ".Hand", Frame = 15 }],
        MorphKeyframes = [new() { MorphName = name + ".Smile", Frame = 20, Weight = .5f }],
        IkKeyframes = [new() { Frame = 30 }],
        CameraKeyframes = [new() { Frame = 99 }], LightKeyframes = [new() { Frame = 99 }]
    };
    private static PmxModel Model(string name) => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = name, EnglishName = "", Comment = "", EnglishComment = "", Vertices = [], Faces = [], Textures = [], Materials = [],
        RigidBodies = [], Joints = [], SoftBodies = [],
        Bones = new[] { Bone(name + ".Root"), Bone(name + ".Hand"), Bone(name + ".Empty") },
        Morphs = new[] { new PmxMorph { Name = name + ".Smile", EnglishName = "", Type = PmxMorphType.Position, ControlPanel = 0 } },
        DisplayFrames = new[]
        {
            new PmxDisplayFrame { Name = "Body", EnglishName = "", Type = PmxDisplayFrameType.Default,
                Targets = [new(PmxDisplayTargetType.Bone, 0), new(PmxDisplayTargetType.Bone, 1), new(PmxDisplayTargetType.Bone, 2)] },
            new PmxDisplayFrame { Name = "Expressions", EnglishName = "", Type = PmxDisplayFrameType.Special, Targets = [new(PmxDisplayTargetType.Morph, 0)] }
        }
    };
    private static PmxBone Bone(string name) => new()
    { Name = name, EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate };
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
