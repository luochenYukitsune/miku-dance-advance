using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application.MotionEditing;

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
}
