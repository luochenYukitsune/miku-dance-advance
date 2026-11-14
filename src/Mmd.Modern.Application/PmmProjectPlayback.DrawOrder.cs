namespace Mmd.Modern.Application;

public sealed partial class PmmProjectPlayback
{
    /// <summary>Samples all model and accessory entries at the current project clock, including hidden entries.
    /// Supply exactly one current accessory frame per stored PMM accessory ID, as returned by PmmAccessoryPlayback.Sample.
    /// The host applies this project's Camera and light tracks to all entries together.</summary>
    public PmmDrawPlan SampleDrawPlan(IReadOnlyList<PmmAccessoryFrame>? accessoryFrames = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var document = Plan.Document;
        int accessoryCount = document.Accessories.Count;
        // nanoem's MIT document setter accepts [0, accessoryCount) or stores -1. The raw reader
        // preserves other integers; do not silently reinterpret those as a different composition.
        // Pure-model documents also commonly retain the typed settings default of zero.
        var order = PmmDrawOrderPolicy.Build(document);

        accessoryFrames ??= [];
        if (accessoryFrames.Count != accessoryCount)
            throw new ArgumentException("Supply exactly one sampled frame for every stored PMM accessory ID.", nameof(accessoryFrames));
        var sources = document.Accessories.ToDictionary(accessory => (int)accessory.Index);
        var samples = new Dictionary<int, PmmAccessoryFrame>();
        foreach (var frame in accessoryFrames)
        {
            if (frame is null || frame.Frame is null || !sources.TryGetValue(frame.Index, out var source)
                || frame.AddBlendEnabled != source.AddBlendEnabled || !samples.TryAdd(frame.Index, frame))
                throw new ArgumentException("Accessory samples must have unique known stored IDs, non-null frames and the original additive blend requirement.", nameof(accessoryFrames));
        }

        // Interoperability mapping observed in nanoem's PMM host: a nonnegative cut inserts the
        // entire model block at that accessory draw index. A -1 value leaves both raw keys intact.
        // This independently expressed mapping does not assume that stored IDs are array positions.
        var shown = visible.ToHashSet();
        var items = order.Items.Select(item =>
        {
            var sampled = item.Kind == PmmDrawableKind.Accessory ? samples[item.StoredIndex] : null;
            return new PmmDrawItem(item.Kind, item.StoredIndex, item.SourceDrawOrderIndex, item.DrawOrderIndex,
                sampled?.Frame ?? entries[item.StoredIndex].Frame,
                sampled?.Visible ?? shown.Contains(item.StoredIndex), sampled?.AddBlendEnabled ?? false);
        });
        return new(items, order.Diagnostics);
    }
}
