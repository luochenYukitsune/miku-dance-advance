using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application.MotionEditing;
using static Mmd.Modern.App.EditorContextMenu;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    private void SelectTimelineKeyedTracks(object? sender, RoutedEventArgs args)
    {
        ApplyTimelineTrackMode(EditorTimelineTrackMode.KeyedTracks);
        if (sender is ToggleButton button) button.IsChecked = _shell?.Animation.IsTimelineKeyedTracks == true;
    }

    private void SelectTimelineTrackHeader(object? sender, TimelineTrackHeaderEventArgs args)
    {
        if (_shell is not { IsBusy: false } shell || shell.Browser.SelectedModel is not { } selected) return;
        shell.ViewportPickedLightId = null;
        var model = selected.PmxModel;
        if (args.Track.Kind == KeyframeTrackKind.Bone)
        {
            int index = Enumerable.Range(0, model.Bones.Count).FirstOrDefault(i => model.Bones[i].Name == args.Track.Name, -1);
            if (index < 0) return;
            var selection = (args.Modifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0
                ? shell.Animation.SelectedBoneIndices.ToList() : [];
            if (!selection.Remove(index)) selection.Add(index);
            shell.StopPlayback();
            shell.SceneSelectionIndex = -1;
            shell.Browser.HierarchyTab = 0;
            shell.Browser.SelectedBoneIndex = selection.Count == 0 ? null : selection[^1];
            shell.Animation.SelectBones(selection);
        }
        else if (args.Track.Kind == KeyframeTrackKind.Morph)
        {
            int index = Enumerable.Range(0, model.Morphs.Count).FirstOrDefault(i => model.Morphs[i].Name == args.Track.Name, -1);
            if (index < 0) return;
            shell.StopPlayback();
            shell.SceneSelectionIndex = -1;
            shell.Browser.HierarchyTab = 1;
            shell.Browser.SelectedMorphIndex = index;
        }
    }

    private void ClearTimelineSelectionWithEscape(object? sender, KeyEventArgs args)
    {
        if (args.Handled || args.Key != Key.Escape || args.KeyModifiers != KeyModifiers.None ||
            _shell?.Animation.ClearTimelineSelectionCommand is not { } clear || !clear.CanExecute(null)) return;
        clear.Execute(null);
        args.Handled = true;
    }

    private void OpenTimelineContextMenu(object? sender, ContextRequestedEventArgs args)
    {
        if (sender is not EditorTimeline timeline || _shell is not { IsBusy: false } shell) return;
        Point? position = args.TryGetPosition(timeline, out var point) ? point : null;
        if (timeline.PrepareContextTarget(position) is not { } target) return;
        timeline.Focus();
        EditorContextMenu.Open(timeline, position, TimelineMenuItems(shell, timeline, target));
        args.Handled = true;
    }

    private Control[] TimelineMenuItems(EditorShellViewModel shell, EditorTimeline timeline, TimelineContextTarget target)
    {
        var animation = shell.Animation;
        var row = target.Row;
        if (target.Area == TimelineContextArea.GroupHeader && row?.Group is { } group)
        {
            bool expanded = timeline.IsGroupExpanded(group.Key);
            return [
                Item(expanded ? "Mda.Text.Menu.Collapse" : "Mda.Text.Menu.Expand", () => timeline.SetGroupExpanded(group.Key, !expanded)),
                new Separator(),
                Item("Mda.Text.Menu.ExpandAll", () => timeline.SetAllGroupsExpanded(true),
                    timeline.Groups.Any(candidate => !timeline.IsGroupExpanded(candidate.Key))),
                Item("Mda.Text.Menu.CollapseAll", () => timeline.SetAllGroupsExpanded(false),
                    timeline.Groups.Any(candidate => timeline.IsGroupExpanded(candidate.Key)))
            ];
        }

        bool canSeek = shell.HasProject && target.Frame <= int.MaxValue;
        void Seek()
        {
            shell.StopPlayback();
            shell.CurrentFrame = checked((int)target.Frame);
        }
        MenuItem AtFrame(string label, ICommand command, string gesture) =>
            Item(label, () =>
            {
                Seek();
                if (command.CanExecute(null)) command.Execute(null);
            }, canSeek && command.CanExecute(null), gesture);
        MenuItem Jump() => Item("Mda.Text.Menu.JumpToFrame", Seek, canSeek && target.Frame != shell.CurrentFrame);
        var play = Item(shell.IsPlaying ? "Mda.Text.8d12fc0d4eb2" : "Mda.Text.c3396195e91c", shell.PlayCommand, "Space");

        if (target.Area is TimelineContextArea.Ruler or TimelineContextArea.SceneTrack || !shell.IsAnimationInspector)
        {
            var navigation = new List<Control> { Jump() };
            if (timeline.SceneTrack is { } scene)
                navigation.Add(scene.Kind == SceneTrackKind.Camera
                    ? AtFrame("Mda.Text.a619bd96a115", shell.SceneInspector.RegisterCameraKeyframeCommand, "Enter")
                    : AtFrame("Mda.Text.e4b84a93325e", shell.SceneInspector.RegisterLightKeyframeCommand, "Enter"));
            navigation.Add(new Separator());
            navigation.Add(play);
            return navigation.ToArray();
        }

        if (!animation.HasActor) return [Jump(), play];
        var items = new List<Control>();
        var channel = animation.TimelineFocusedChannel;
        bool targetSelected = channel.HasValue && (row is null || row.ContainsChannel(channel.Value));
        void AddRegistration()
        {
            if (targetSelected)
                items.Add(AtFrame("Mda.Text.3cf1e4dc9adc", animation.IsMorphSelected
                    ? animation.RegisterKeyframeCommand : animation.RegisterPoseCommand, "Enter"));
        }
        if (target.Area == TimelineContextArea.TrackHeader && row?.Track is { } track)
        {
            AddRegistration();
            if (track.Kind == KeyframeTrackKind.Bone && targetSelected)
                items.Add(Item("Mda.Text.cb5d682bac3d", animation.ResetPoseCommand));
            items.Add(new Separator());
            items.Add(Item("Mda.Text.Menu.SelectTrackKeys",
                () => animation.SelectTrackKeys(track.Frames.Select(frame => new KeyframeAddress(track.Kind, track.Name, frame)).ToArray()),
                track.Frames.Count > 0 && animation.SelectTimelineRangeCommand.CanExecute(null)));
            return items.ToArray();
        }

        if (target.Area == TimelineContextArea.Key)
        {
            AddRegistration();
            items.Add(new Separator());
            items.Add(Item("Mda.Text.c61bade89d5a", animation.CopyKeyframesCommand, "Ctrl+C"));
            items.Add(AtFrame("Mda.Text.c2449cd74350", animation.PasteKeyframesCommand, "Ctrl+V"));
            items.Add(Item("Mda.Text.2f9daa828907", animation.DeleteKeyframesCommand, "Delete"));
            items.Add(new Separator());
            if (row?.AddressesAt(target.Frame).Any(address => address.Kind == KeyframeTrackKind.Bone &&
                animation.SelectedKeyframes.Contains(address)) == true)
                items.Add(Item("Mda.Text.d20383c88bb0", () => OpenInterpolation(this, new RoutedEventArgs()),
                    !animation.IsBusy && !animation.HasHiddenSelectedBoneKeyframes));
            items.Add(Jump());
            return items.ToArray();
        }

        items.Add(Jump());
        AddRegistration();
        items.Add(new Separator());
        items.Add(AtFrame("Mda.Text.c2449cd74350", animation.PasteKeyframesCommand, "Ctrl+V"));
        items.Add(Item("Mda.Text.TimelineBatch.SelectRange", animation.SelectTimelineRangeCommand));
        items.Add(Item("Mda.Text.TimelineBatch.ClearSelection", animation.ClearTimelineSelectionCommand, "Escape"));
        return items.ToArray();
    }
}
