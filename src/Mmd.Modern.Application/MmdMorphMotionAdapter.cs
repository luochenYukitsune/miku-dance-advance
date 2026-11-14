// Linear weights and first/last-key endpoint behavior adapted from Saba VMDAnimation.cpp
// @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT, Copyright (c) 2016-2017 benikabocha.
// Full license: third_party/saba/LICENCE.txt. Duplicate frames use an explicit file-order overwrite policy.
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Immutable VMD morph tracks, matched to the first PMX morph with the same exact name.</summary>
public sealed class MmdMorphMotionAdapter
{
    private readonly record struct Key(uint Frame, float Weight);
    private sealed record Track(int MorphIndex, Key[] Keys);
    private readonly Track[] tracks;
    public uint DurationFrames { get; }
    public int MatchedTrackCount => tracks.Length;
    public bool HasMatchedTracks => tracks.Length != 0;
    public IReadOnlyList<string> Diagnostics { get; }

    public MmdMorphMotionAdapter(PmxMorphAdapter morphs, VmdMotion motion)
        : this((morphs ?? throw new ArgumentNullException(nameof(morphs))).SupportedMorphIndices, motion) { }

    public MmdMorphMotionAdapter(IReadOnlyDictionary<string, int> supportedMorphs, VmdMotion motion)
    {
        ArgumentNullException.ThrowIfNull(supportedMorphs); ArgumentNullException.ThrowIfNull(motion);
        var imported = new List<Track>(); var diagnostics = new List<string>();
        foreach (var group in motion.MorphKeyframes.GroupBy(k => k.MorphName, StringComparer.Ordinal))
        {
            var keys = new Dictionary<uint, Key>(); bool duplicate = false;
            foreach (var key in group)
            {
                if (!float.IsFinite(key.Weight)) throw new ArgumentException($"VMD morph '{group.Key}' has a non-finite weight.", nameof(motion));
                DurationFrames = Math.Max(DurationFrames, key.Frame);
                if (keys.ContainsKey(key.Frame)) duplicate = true;
                keys[key.Frame] = new(key.Frame, key.Weight);
            }
            if (duplicate) diagnostics.Add($"Duplicate VMD morph frame in '{group.Key}': the last record in file order wins; Saba does not define a stable duplicate order.");
            if (supportedMorphs.TryGetValue(group.Key, out int index))
                imported.Add(new(index, keys.Values.OrderBy(k => k.Frame).ToArray()));
            else diagnostics.Add($"VMD morph track '{group.Key}' has no matching supported morph; the track is not evaluated.");
        }
        tracks = imported.ToArray(); Diagnostics = diagnostics.ToArray();
    }

    public IReadOnlyDictionary<int, float> Evaluate(double frame)
    {
        if (!double.IsFinite(frame)) throw new ArgumentOutOfRangeException(nameof(frame));
        var values = new Dictionary<int, float>(tracks.Length);
        foreach (var track in tracks)
        {
            var keys = track.Keys; float weight;
            // Saba imports no artificial frame-zero seed for morph tracks.
            if (frame <= keys[0].Frame) weight = keys[0].Weight;
            else if (frame >= keys[^1].Frame) weight = keys[^1].Weight;
            else
            {
                int low = 0, high = keys.Length - 1;
                while (high - low > 1)
                {
                    int middle = low + (high - low) / 2;
                    if (keys[middle].Frame <= frame) low = middle; else high = middle;
                }
                double progress = (frame - keys[low].Frame) / (keys[high].Frame - (double)keys[low].Frame);
                weight = (float)((1 - progress) * keys[low].Weight + progress * keys[high].Weight);
            }
            values.Add(track.MorphIndex, weight);
        }
        return values;
    }
}
