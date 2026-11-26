using Avalonia;
using Avalonia.Input;

namespace Mmd.Modern.App.Controls;

internal enum TimelineContextArea { Ruler, SceneTrack, TrackHeader, GroupHeader, Key, Blank }

internal readonly record struct TimelineContextTarget(TimelineContextArea Area, uint Frame, EditorTimelineRow? Row = null);

public sealed partial class EditorTimeline
{
    internal TimelineContextTarget? PrepareContextTarget(Point? position)
    {
        if (_dragPointer is not null || !IsEffectivelyEnabled) return null;
        if (position is not { } point)
        {
            var selected = SelectedKeyframes.FirstOrDefault(key => key.Frame == CurrentFrame);
            var selectedRow = SelectedKeyframes.Count > 0 && selected.Name is not null
                ? DisplayRows.FirstOrDefault(row => row.ContainsChannel(selected)) : null;
            return new(selectedRow is null ? TimelineContextArea.Ruler : TimelineContextArea.Key,
                (uint)CurrentFrame, selectedRow);
        }
        if (!new Rect(Bounds.Size).Contains(point)) return null;

        bool header = point.X < TrackHeaderWidth;
        uint frame = (uint)(header ? CurrentFrame : FrameAtX(point.X));
        if (point.Y < RulerHeight) return new(TimelineContextArea.Ruler, frame);
        if (SceneTrack is { } scene && point.Y < RulerHeight + TrackRowHeight)
            return new(TimelineContextArea.SceneTrack, header ? frame : (uint)SceneFrameAt(scene, point.X));
        int row = FirstVisibleTrack + (int)((point.Y - RulerHeight) / TrackRowHeight);
        var track = row >= 0 && row < ActiveTracks.Count ? ActiveTracks[row] : null;
        if (header && track?.Group is not null) return new(TimelineContextArea.GroupHeader, frame, track);

        var draft = header ? null : HitDraft(point, track, row);
        var hit = header || draft.HasValue ? null : HitKey(point, track, row);
        if (hit is { } key)
        {
            var addresses = track!.AddressesAt(key).ToArray();
            if (!addresses.Any(SelectedKeyframes.Contains))
            {
                _selectionAnchor = addresses[0];
                SetSelection(addresses);
            }
        }
        // Selecting the key can synchronously reproject the rows. Keep the original channel.
        if (track?.Track is { } channel)
            TrackHeaderInvoked?.Invoke(this, new(channel, KeyModifiers.None));
        if (header && track is not null) return new(TimelineContextArea.TrackHeader, frame, track);
        return new(hit.HasValue ? TimelineContextArea.Key : TimelineContextArea.Blank, hit ?? draft ?? frame, track);
    }

    internal void SetAllGroupsExpanded(bool expanded)
    {
        if (_dragPointer is not null) return;
        foreach (var group in Groups)
        {
            if (expanded) _expandedGroups.Add(group.Key);
            else _expandedGroups.Remove(group.Key);
        }
        RebuildDisplayRows();
        TrackViewChanged(contentsChanged: false);
        InvalidateLayers();
    }
}
