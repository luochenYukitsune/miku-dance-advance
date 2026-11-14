using System.Globalization;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App.Controls;

/// <summary>A model-authored display category. Its summary is cached for this immutable track revision.</summary>
public sealed class EditorTimelineGroup
{
    public string Key { get; }
    public string Label { get; }
    public IReadOnlyList<EditorTimelineTrack> Tracks { get; }
    internal EditorTimelineRow Summary { get; }
    internal IReadOnlyList<EditorTimelineRow> Children { get; }

    public EditorTimelineGroup(string key, string label, IEnumerable<EditorTimelineTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(key); ArgumentNullException.ThrowIfNull(label); ArgumentNullException.ThrowIfNull(tracks);
        Key = key; Label = label;
        Tracks = Array.AsReadOnly(tracks.DistinctBy(track => (track.Kind, track.Name)).ToArray());
        Summary = new EditorTimelineRow(this);
        Children = Array.AsReadOnly(Tracks.Select(track => new EditorTimelineRow(track, true)).ToArray());
    }
}

/// <summary>Display geometry only. Every selected address still belongs to a real authored channel.</summary>
public sealed class EditorTimelineRow
{
    private readonly IReadOnlyList<EditorTimelineTrack> tracks;
    private readonly HashSet<(KeyframeTrackKind Kind, string Name)> channels;
    public EditorTimelineGroup? Group { get; }
    public EditorTimelineTrack? Track { get; }
    public bool IsGroup => Group is not null;
    public bool IsChild { get; }
    public string Label => Group?.Label ?? Track!.Label;
    public IReadOnlyList<uint> Frames { get; }

    internal EditorTimelineRow(EditorTimelineTrack track, bool child = false)
    {
        Track = track; IsChild = child; tracks = [track]; Frames = track.Frames;
        channels = [(track.Kind, track.Name)];
    }
    internal EditorTimelineRow(EditorTimelineGroup group)
    {
        Group = group; tracks = group.Tracks;
        channels = tracks.Select(track => (track.Kind, track.Name)).ToHashSet();
        Frames = Array.AsReadOnly(tracks.SelectMany(track => track.Frames).Distinct().Order().ToArray());
    }
    public bool ContainsChannel(KeyframeAddress address) => channels.Contains((address.Kind, address.Name));
    public IEnumerable<KeyframeAddress> AddressesAt(uint frame) => AddressesBetween(frame, frame);
    public IEnumerable<KeyframeAddress> AddressesBetween(double first, double last)
    {
        foreach (var track in tracks)
            for (int index = track.LowerBound(first); index < track.Frames.Count && track.Frames[index] <= last; index++)
                yield return new KeyframeAddress(track.Kind, track.Name, track.Frames[index]);
    }
    internal int LowerBound(double frame)
    {
        int low = 0, high = Frames.Count;
        while (low < high) { int middle = low + (high - low) / 2; if (Frames[middle] < frame) low = middle + 1; else high = middle; }
        return low;
    }
}

/// <summary>Projects exact PMX display targets; missing and unmatched authored channels remain visible.</summary>
public static class EditorTimelineGrouping
{
    public static IReadOnlyList<EditorTimelineGroup> Build(PmxModel? model,
        IReadOnlyList<EditorTimelineTrack> tracks, string modelKey = "",
        string fallbackLabel = "未分类", string ikLabel = "IK / 显示")
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var byChannel = tracks.DistinctBy(track => (track.Kind, track.Name)).ToDictionary(track => (track.Kind, track.Name));
        var assigned = new HashSet<(KeyframeTrackKind Kind, string Name)>();
        var result = new List<EditorTimelineGroup>();
        if (model is not null)
            for (int index = 0; index < model.DisplayFrames.Count; index++)
            {
                var display = model.DisplayFrames[index];
                var members = new List<EditorTimelineTrack>();
                foreach (var target in display.Targets)
                {
                    (KeyframeTrackKind Kind, string Name)? channel = target.Type switch
                    {
                        PmxDisplayTargetType.Bone when target.Index >= 0 && target.Index < model.Bones.Count => (KeyframeTrackKind.Bone, model.Bones[target.Index].Name),
                        PmxDisplayTargetType.Morph when target.Index >= 0 && target.Index < model.Morphs.Count => (KeyframeTrackKind.Morph, model.Morphs[target.Index].Name),
                        _ => null
                    };
                    // A channel appears once, in its first authored category, even if the PMX repeats a target.
                    if (channel is { } key && byChannel.TryGetValue(key, out var track) && assigned.Add(key)) members.Add(track);
                }
                if (members.Count == 0) continue;
                string label = !string.IsNullOrWhiteSpace(display.Name) ? display.Name
                    : !string.IsNullOrWhiteSpace(display.EnglishName) ? display.EnglishName : fallbackLabel;
                result.Add(new EditorTimelineGroup(modelKey + ":display:" + index.ToString(CultureInfo.InvariantCulture), label, members));
            }
        var other = tracks.Where(track => track.Kind != KeyframeTrackKind.Ik && assigned.Add((track.Kind, track.Name))).ToArray();
        if (other.Length != 0) result.Add(new EditorTimelineGroup(modelKey + ":unclassified", fallbackLabel, other));
        var ik = tracks.Where(track => track.Kind == KeyframeTrackKind.Ik).ToArray();
        if (ik.Length != 0) result.Add(new EditorTimelineGroup(modelKey + ":ik", ikLabel, ik));
        return result.AsReadOnly();
    }
}
