using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class TimelineModelGroupTests
{
    [Fact]
    public void AuthoredDisplayOrderAndMixedTargetKindsTakePrecedenceOverTrackOrder()
    {
        var tracks = Tracks();
        var groups = EditorTimelineGrouping.Build(Model(
            Display("First", new(PmxDisplayTargetType.Morph, 0), new(PmxDisplayTargetType.Bone, 1)),
            Display("Second", new PmxDisplayTarget(PmxDisplayTargetType.Bone, 0))), tracks, "actorA");
        Assert.Equal(new[] { "First", "Second", "IK / 显示", "未分组・已有帧" }, groups.Select(group => group.Label));
        Assert.Equal(new[] { KeyframeTrackKind.Morph, KeyframeTrackKind.Bone }, groups[0].Tracks.Select(track => track.Kind));
        Assert.Equal("actorA:display:0", groups[0].Key);
        Assert.Same(tracks[2], groups[0].Tracks[0]);
        Assert.Same(tracks[1], groups[0].Tracks[1]);
        Assert.Equal("Removed channel", Assert.Single(groups[3].Tracks).Name);
        Assert.Equal(tracks.Sum(track => track.Frames.Count), groups.SelectMany(group => group.Tracks).Sum(track => track.Frames.Count));
    }

    [Fact]
    public void RepeatedMembersAppearInEachAuthoredGroupAndInvalidIndicesAreIgnored()
    {
        var tracks = Tracks();
        var groups = EditorTimelineGrouping.Build(Model(
            Display("One", new(PmxDisplayTargetType.Bone, 0), new(PmxDisplayTargetType.Bone, 0), new(PmxDisplayTargetType.Bone, 99)),
            Display("Duplicate", new PmxDisplayTarget(PmxDisplayTargetType.Bone, 0)),
            Display("Empty", new PmxDisplayTarget(PmxDisplayTargetType.Morph, -1))), tracks);
        Assert.Equal(new[] { "One", "Duplicate", "IK / 显示", "未分组・已有帧" }, groups.Select(group => group.Label));
        Assert.Single(groups[0].Tracks);
        Assert.Same(Assert.Single(groups[0].Tracks), Assert.Single(groups[1].Tracks));
        Assert.Equal(tracks.Count, groups.SelectMany(group => group.Tracks).Select(track => (track.Kind, track.Name)).Distinct().Count());
    }

    [Fact]
    public void MissingDisplayMetadataRetainsAllChannelsWithoutNameHeuristics()
    {
        var groups = EditorTimelineGrouping.Build(null, Tracks(), "A", "Other", "Visibility");
        Assert.Equal(new[] { "Visibility", "Other" }, groups.Select(group => group.Label));
        Assert.Equal(4, groups[1].Tracks.Count);
        Assert.Equal("A:unclassified", groups[1].Key);
        Assert.NotEqual(groups[1].Key, EditorTimelineGrouping.Build(null, Tracks(), "B")[1].Key);
        var unkeyedHelper = new EditorTimelineTrack(KeyframeTrackKind.Bone, "Hidden helper", []);
        var withHelper = EditorTimelineGrouping.Build(null, Tracks().Append(unkeyedHelper).ToArray());
        Assert.DoesNotContain(withHelper.SelectMany(group => group.Tracks), track => track.Name == unkeyedHelper.Name);
    }

    [Fact]
    public void SummaryCombinesFramePositionsWhileResolvingEveryActualTypedAddress()
    {
        var groups = EditorTimelineGrouping.Build(Model(Display("Mixed", new(PmxDisplayTargetType.Bone, 0), new(PmxDisplayTargetType.Morph, 0))), Tracks());
        var timelineGroup = groups[0];
        Assert.Equal("Shared", timelineGroup.Tracks[0].Name);
        Assert.Equal("Shared", timelineGroup.Tracks[1].Name);
        // The UI test exercises this same summary through pointer selection: two keys at frame 10,
        // one diamond, and no synthetic category address committed to the editing engine.
        Assert.Equal(new uint[] { 0, 10, 20 }, timelineGroup.Tracks.SelectMany(track => track.Frames).Distinct().Order());
        Assert.Equal(4, timelineGroup.Tracks.Sum(track => track.Frames.Count));
    }

    internal static IReadOnlyList<EditorTimelineTrack> Tracks() => new EditorTimelineTrack[]
    {
        new(KeyframeTrackKind.Bone, "Shared", [0, 10]),
        new(KeyframeTrackKind.Bone, "Second", [10, 30]),
        new(KeyframeTrackKind.Morph, "Shared", [10, 20]),
        new(KeyframeTrackKind.Bone, "Removed channel", [40]),
        new(KeyframeTrackKind.Ik, string.Empty, [0, 50])
    };
    internal static PmxDisplayFrame Display(string label, params PmxDisplayTarget[] targets) =>
        new() { Name = label, EnglishName = string.Empty, Type = PmxDisplayFrameType.Default, Targets = targets };
    internal static PmxModel Model(params PmxDisplayFrame[] groups) => new()
    {
        Header = null!, Name = "Model", EnglishName = "", Comment = "", EnglishComment = "",
        Vertices = [], Faces = [], Textures = [], Materials = [], RigidBodies = [], Joints = [], SoftBodies = [],
        Bones = new[] { Bone("Shared"), Bone("Second") },
        Morphs = new[] { new PmxMorph { Name = "Shared", EnglishName = "", Type = PmxMorphType.Position, ControlPanel = 0 } },
        DisplayFrames = groups
    };
    private static PmxBone Bone(string name) => new()
    { Name = name, EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.AllowRotate };
}

[Collection(QualityLoadingCollection.Name)]
public sealed class TimelineModelGroupUiTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Fact]
    public async Task CategoriesStartCollapsedAndExpansionPreservesActualSelectionAndSource() => await session.Dispatch(() =>
    {
        var (window, timeline, source) = Editor();
        try
        {
            Assert.Equal(3, timeline.DisplayRows.Count); Assert.All(timeline.DisplayRows, row => Assert.True(row.IsGroup));
            var group = timeline.Groups[0];
            Assert.Equal(new uint[] { 0, 10, 20, 30 }, timeline.DisplayRows[0].Frames);
            var selected = new[] { KeyframeAddress.Bone("Shared", 10), KeyframeAddress.Morph("Shared", 10) };
            timeline.SelectedKeyframes = selected;
            timeline.SetGroupExpanded(group.Key, true);
            Assert.Equal(6, timeline.DisplayRows.Count);
            Assert.Equal(selected, timeline.SelectedKeyframes); Assert.Same(source, timeline.Tracks);
            timeline.SetGroupExpanded(group.Key, false);
            Assert.Equal(3, timeline.DisplayRows.Count); Assert.Equal(selected, timeline.SelectedKeyframes);
            Assert.Equal(2, timeline.DisplayRows[0].AddressesAt(10).Count(address => address.Name == "Shared"));
            return true;
        }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task CollapsedDiamondSelectsEveryRealMemberAndDragRequestsOneAtomicMove() => await session.Dispatch(() =>
    {
        var (window, timeline, source) = Editor();
        try
        {
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var location = At(10, 0);
            TimelineKeysMoveRequestedEventArgs? move = null; timeline.KeysMoveRequested += (_, value) => move = value;
            Press(window, timeline, pointer, location);
            Assert.Equal(3, timeline.SelectedKeyframes.Count);
            Assert.Contains(KeyframeAddress.Bone("Shared", 10), timeline.SelectedKeyframes);
            Assert.Contains(KeyframeAddress.Bone("Second", 10), timeline.SelectedKeyframes);
            Assert.Contains(KeyframeAddress.Morph("Shared", 10), timeline.SelectedKeyframes);
            Move(window, timeline, pointer, At(14, 0));
            Assert.Equal(4, timeline.PreviewFrameDelta);
            Release(window, timeline, pointer, At(14, 0));
            Assert.NotNull(move); Assert.Equal(4, move.FrameDelta); Assert.Equal(3, move.Selection.Count);
            Assert.Same(source, timeline.Tracks); Assert.Equal(new uint[] { 0, 10 }, source[0].Frames);
            Assert.DoesNotContain(move.Selection, key => key.Name == "Mixed");
            return true;
        }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task CollapsedBoxSelectionKeepsKeysFromEveryMemberAndControlToggleIsSymmetric() => await session.Dispatch(() =>
    {
        var (window, timeline, _) = Editor();
        try
        {
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            Press(window, timeline, pointer, new Point(At(8, 0).X, EditorTimeline.RulerHeight + 1));
            Move(window, timeline, pointer, new Point(At(22, 0).X, EditorTimeline.RulerHeight + EditorTimeline.TrackRowHeight - 1));
            Release(window, timeline, pointer, new Point(At(22, 0).X, EditorTimeline.RulerHeight + EditorTimeline.TrackRowHeight - 1));
            Assert.Equal(4, timeline.SelectedKeyframes.Count);
            Press(window, timeline, pointer, At(10, 0), KeyModifiers.Control);
            Release(window, timeline, pointer, At(10, 0), KeyModifiers.Control);
            Assert.Equal(new[] { KeyframeAddress.Morph("Shared", 20) }, timeline.SelectedKeyframes);
            return true;
        }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task PickingHighlightsCollapsedGroupWithoutScrollingAndHeaderClickSelectsItsChannel() => await session.Dispatch(() =>
    {
        var (window, timeline, source) = Editor();
        try
        {
            timeline.FirstVisibleTrack = 1;
            timeline.FocusedChannel = KeyframeAddress.Bone("Second", 0);
            timeline.FocusRow = 1;
            Assert.Equal(1, timeline.FirstVisibleTrack);
            Assert.True(timeline.IsRowFocused(timeline.DisplayRows[0]));
            Assert.False(timeline.IsGroupExpanded(timeline.Groups[0].Key));
            timeline.FirstVisibleTrack = 0;
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            TimelineTrackHeaderEventArgs? invoked = null;
            timeline.TrackHeaderInvoked += (_, args) => invoked = args;
            ToolTip.SetTip(timeline, "Help");
            Move(window, timeline, pointer, new Point(30, At(0, 0).Y));
            Assert.Equal(timeline.DisplayRows[0].Label, ToolTip.GetTip(timeline));
            Move(window, timeline, pointer, At(5, 0));
            Assert.Equal("Help", ToolTip.GetTip(timeline));
            Press(window, timeline, pointer, new Point(30, At(0, 0).Y));
            Press(window, timeline, pointer, new Point(30, At(0, 2).Y), KeyModifiers.Control);
            Assert.Same(source[1], invoked!.Track);
            Assert.Equal(KeyModifiers.Control, invoked.Modifiers);
            Assert.Empty(timeline.SelectedKeyframes);
            timeline.Drafts = [KeyframeAddress.Bone("Second", 21)];
            var marker = new Point(At(21, 2).X, At(21, 2).Y + 7);
            Press(window, timeline, pointer, marker);
            Assert.Equal(21, timeline.CurrentFrame);
            Assert.Empty(timeline.SelectedKeyframes);
            Assert.DoesNotContain(21u, timeline.DisplayRows[0].Frames);
            return true;
        }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ScrollingUnderAStationaryPointerShowsTheNewGroupName() => await session.Dispatch(() =>
    {
        var (window, timeline, _) = Editor();
        try
        {
            timeline.Height = EditorTimeline.RulerHeight + EditorTimeline.TrackRowHeight;
            window.UpdateLayout();
            ToolTip.SetTip(timeline, "Help");
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var point = new Point(30, At(0, 0).Y);
            Move(window, timeline, pointer, point);
            Assert.Equal(timeline.DisplayRows[0].Label, ToolTip.GetTip(timeline));

            timeline.RaiseEvent(new PointerWheelEventArgs(timeline, pointer, window, timeline.TranslatePoint(point, window)!.Value, 3,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None, new Avalonia.Vector(0, -1)));
            Assert.Equal(2, timeline.FirstVisibleTrack);
            Assert.Equal(timeline.DisplayRows[2].Label, ToolTip.GetTip(timeline));
            timeline.FirstVisibleTrack = 1;
            Assert.Equal(timeline.DisplayRows[1].Label, ToolTip.GetTip(timeline));

            Move(window, timeline, pointer, At(5, 0));
            timeline.FirstVisibleTrack = 0;
            Assert.Equal("Help", ToolTip.GetTip(timeline));
            return true;
        }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SynchronousSelectionProjectionRefreshKeepsGestureRowsAndActualAddressesStable() => await session.Dispatch(() =>
    {
        var (window, timeline, source) = Editor();
        try
        {
            timeline.KeyframeSelectionChanged += (_, _) => timeline.Groups = EditorTimelineGrouping.Build(
                TimelineModelGroupTests.Model(TimelineModelGroupTests.Display("Replacement", new PmxDisplayTarget(PmxDisplayTargetType.Bone, 1))), source, "A");
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            TimelineKeysMoveRequestedEventArgs? move = null; timeline.KeysMoveRequested += (_, value) => move = value;
            Press(window, timeline, pointer, At(10, 0));
            Move(window, timeline, pointer, At(12, 0)); Release(window, timeline, pointer, At(12, 0));
            Assert.NotNull(move); Assert.Equal(3, move.Selection.Count); Assert.Equal(2, move.FrameDelta);
            Assert.Equal("Replacement", timeline.DisplayRows[0].Label);
            return true;
        }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task AuthoredSourceChangeCancelsMoveInsteadOfApplyingStaleCategoryAddresses() => await session.Dispatch(() =>
    {
        var (window, timeline, source) = Editor();
        try
        {
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            bool moved = false; timeline.KeysMoveRequested += (_, _) => moved = true;
            Press(window, timeline, pointer, At(10, 0)); Move(window, timeline, pointer, At(14, 0));
            Assert.Equal(4, timeline.PreviewFrameDelta);
            var replacement = source.Skip(1).ToArray();
            timeline.FullTracks = replacement; timeline.Tracks = replacement;
            timeline.Groups = EditorTimelineGrouping.Build(null, replacement, "B");
            Release(window, timeline, pointer, At(14, 0));
            Assert.False(moved); Assert.Equal(0, timeline.PreviewFrameDelta);
            Assert.Empty(timeline.SelectedKeyframes); Assert.Null(pointer.Captured);
            return true;
        }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ExpansionStateBelongsToTheExactModelInstanceAndOptionalFlatModePreservesKeys() => await session.Dispatch(() =>
    {
        var (window, timeline, source) = Editor();
        try
        {
            var firstModelGroups = timeline.Groups;
            timeline.SetGroupExpanded(firstModelGroups[0].Key, true);
            Assert.Equal(6, timeline.DisplayRows.Count);
            var selected = new[] { KeyframeAddress.Morph("Shared", 20) }; timeline.SelectedKeyframes = selected;
            timeline.Groups = [];
            Assert.Equal(source.Count, timeline.DisplayRows.Count); Assert.All(timeline.DisplayRows, row => Assert.False(row.IsGroup));
            Assert.Equal(selected, timeline.SelectedKeyframes);
            timeline.Groups = EditorTimelineGrouping.Build(TimelineModelGroupTests.Model(TimelineModelGroupTests.Display("Mixed",
                new(PmxDisplayTargetType.Bone, 0), new(PmxDisplayTargetType.Bone, 1), new(PmxDisplayTargetType.Morph, 0))), source, "B");
            Assert.Equal(3, timeline.DisplayRows.Count);
            Assert.False(timeline.IsGroupExpanded(timeline.Groups[0].Key));
            timeline.Groups = firstModelGroups;
            Assert.Equal(6, timeline.DisplayRows.Count); Assert.Equal(selected, timeline.SelectedKeyframes);
            return true;
        }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);

    private static (Window Window, EditorTimeline Timeline, IReadOnlyList<EditorTimelineTrack> Source) Editor()
    {
        var source = TimelineModelGroupTests.Tracks();
        var model = TimelineModelGroupTests.Model(TimelineModelGroupTests.Display("Mixed", new(PmxDisplayTargetType.Bone, 0),
            new(PmxDisplayTargetType.Bone, 1), new(PmxDisplayTargetType.Morph, 0)));
        var timeline = new EditorTimeline { Tracks = source, FullTracks = source,
            Groups = EditorTimelineGrouping.Build(model, source, "A"), TrackHeaderWidth = 180, PixelsPerFrame = 8 };
        var window = new Window { Width = 900, Height = 300, Content = timeline };
        window.Show(); Dispatcher.UIThread.RunJobs(); return (window, timeline, source);
    }
    private static Point At(int frame, int row) => new(180 + frame * 8, EditorTimeline.RulerHeight + (row + .5) * EditorTimeline.TrackRowHeight);
    private static void Press(Window window, EditorTimeline timeline, IPointer pointer, Point position, KeyModifiers modifiers = KeyModifiers.None) =>
        timeline.RaiseEvent(new PointerPressedEventArgs(timeline, pointer, window, timeline.TranslatePoint(position, window)!.Value, 1,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), modifiers));
    private static void Move(Window window, EditorTimeline timeline, IPointer pointer, Point position) =>
        timeline.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, timeline, pointer, window, timeline.TranslatePoint(position, window)!.Value, 2,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
    private static void Release(Window window, EditorTimeline timeline, IPointer pointer, Point position, KeyModifiers modifiers = KeyModifiers.None) =>
        timeline.RaiseEvent(new PointerReleasedEventArgs(timeline, pointer, window, timeline.TranslatePoint(position, window)!.Value, 3,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), modifiers, MouseButton.Left));
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
