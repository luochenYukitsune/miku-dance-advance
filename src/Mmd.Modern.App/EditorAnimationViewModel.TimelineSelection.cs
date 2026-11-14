using System.Globalization;
using System.Windows.Input;
using Mmd.Modern.Application.MotionEditing;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

public sealed partial class EditorAnimationViewModel
{
    private string timelineRangeStartText = "0", timelineRangeEndText = "0";
    private string? timelineRangeValidationKey;

    public ICommand SelectTimelineRangeCommand { get; }
    public ICommand ClearTimelineSelectionCommand { get; }

    /// <summary>Raw frame input stays visible when invalid; it is never clamped or saved in the project.</summary>
    public string TimelineRangeStartText
    {
        get => timelineRangeStartText;
        set
        {
            value ??= string.Empty;
            if (timelineRangeStartText == value) return;
            timelineRangeStartText = value; Changed(); SetTimelineRangeValidation(null);
        }
    }

    public string TimelineRangeEndText
    {
        get => timelineRangeEndText;
        set
        {
            value ??= string.Empty;
            if (timelineRangeEndText == value) return;
            timelineRangeEndText = value; Changed(); SetTimelineRangeValidation(null);
        }
    }

    public string TimelineSelectionSummary => Format("Mda.Text.TimelineBatch.Selected", SelectedKeyframes.Count);
    public string TimelineRangeValidationMessage => timelineRangeValidationKey is { } key ? Text(key) : string.Empty;

    /// <summary>The timeline's two-way selection echo must not turn a view-only range operation into navigation.</summary>
    internal bool IsUpdatingTimelineSelectionWithoutNavigation { get; private set; }

    private bool CanSelectTimelineRange => HasActor && !IsBusy && CanMutateProject?.Invoke() != false;

    private void SelectTimelineRange()
    {
        if (!CanSelectTimelineRange) return;
        if (!int.TryParse(TimelineRangeStartText, NumberStyles.None, CultureInfo.InvariantCulture, out int start)
            || !int.TryParse(TimelineRangeEndText, NumberStyles.None, CultureInfo.InvariantCulture, out int end))
        {
            SetTimelineRangeValidation("Mda.Text.TimelineBatch.InvalidRange");
            return;
        }
        if (start > end)
        {
            SetTimelineRangeValidation("Mda.Text.TimelineBatch.StartAfterEnd");
            return;
        }

        // Tracks belongs to this actor and already applies its authored channel/detachment rules.
        // Never enumerate scene camera/light records or project-wide actor motion here.
        var selection = Tracks.SelectMany(track => track.Frames
                .Where(frame => frame >= (uint)start && frame <= (uint)end)
                .Select(frame => new KeyframeAddress(track.Kind, track.Name, frame)))
            .OrderBy(address => address.Frame).ThenBy(address => address.Kind)
            .ThenBy(address => address.Name, StringComparer.Ordinal).ToArray();
        IsUpdatingTimelineSelectionWithoutNavigation = true;
        try
        {
            SetTimelineTrackMode(EditorTimelineTrackMode.AllTracks);
            SelectedKeyframes = selection;
        }
        finally { IsUpdatingTimelineSelectionWithoutNavigation = false; }
        SetTimelineRangeValidation(selection.Length == 0 ? "Mda.Text.TimelineBatch.RangeEmpty" : null);
    }

    private void ClearTimelineSelection()
    {
        if (!CanSelectTimelineRange) return;
        IsUpdatingTimelineSelectionWithoutNavigation = true;
        try { SelectedKeyframes = Array.Empty<KeyframeAddress>(); }
        finally { IsUpdatingTimelineSelectionWithoutNavigation = false; }
        SetTimelineRangeValidation(null);
    }

    private void ResetTimelineRange()
    {
        TimelineRangeStartText = "0";
        // Authored VMD supports uint frames, while this editor's navigation supports int frames.
        // The default end covers only frames the range UI can represent; raw input is never clamped.
        TimelineRangeEndText = HasActor
            ? Tracks.SelectMany(track => track.Frames).Where(frame => frame <= int.MaxValue)
                .DefaultIfEmpty(0u).Max().ToString(CultureInfo.InvariantCulture)
            : "0";
        SetTimelineRangeValidation(null);
    }

    private void SetTimelineRangeValidation(string? key)
    {
        if (timelineRangeValidationKey == key) return;
        timelineRangeValidationKey = key; Changed(nameof(TimelineRangeValidationMessage));
    }
}
