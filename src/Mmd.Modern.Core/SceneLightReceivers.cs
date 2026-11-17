using System.Collections.ObjectModel;

namespace Mmd.Modern.Core;

public enum SceneLightReceiverMode { All, Include, Exclude }

/// <summary>Immutable model-instance selection. An empty Include intentionally illuminates nothing;
/// an empty Exclude illuminates everything. Shadow casters remain independent of this receiver filter.</summary>
public sealed class SceneLightReceivers : IEquatable<SceneLightReceivers>
{
    public SceneLightReceiverMode Mode { get; }
    public ReadOnlyCollection<Guid> ObjectIds { get; }
    public SceneLightReceivers(SceneLightReceiverMode mode, IEnumerable<Guid> objectIds)
    {
        ArgumentNullException.ThrowIfNull(objectIds);
        var ids = objectIds.Take(1025).ToArray();
        if (!Enum.IsDefined(mode) || ids.Length > 1024 || ids.Any(id => id == Guid.Empty)
            || ids.Distinct().Count() != ids.Length || mode == SceneLightReceiverMode.All && ids.Length != 0)
            throw new ArgumentException("Light receivers require a supported mode and at most 1024 distinct nonempty model IDs; All takes no IDs.");
        Mode = mode; ObjectIds = Array.AsReadOnly(ids.Order().ToArray());
    }
    public bool Affects(Guid? objectId) => Mode switch
    {
        SceneLightReceiverMode.All => true,
        SceneLightReceiverMode.Include => objectId is { } id && ObjectIds.Contains(id),
        SceneLightReceiverMode.Exclude => objectId is not { } id || !ObjectIds.Contains(id),
        _ => throw new InvalidOperationException("Unknown receiver mode."),
    };
    public bool Equals(SceneLightReceivers? other) => other is not null && Mode == other.Mode && ObjectIds.SequenceEqual(other.ObjectIds);
    public override bool Equals(object? obj) => obj is SceneLightReceivers other && Equals(other);
    public override int GetHashCode() { var hash = new HashCode(); hash.Add(Mode); foreach (var id in ObjectIds) hash.Add(id); return hash.ToHashCode(); }
}

public sealed partial class SceneLightingSnapshot
{
    /// <summary>Bit order is the renderer's contributing-light order, including bit 31.</summary>
    public uint ReceiverMask(Guid? objectId)
    {
        uint result = 0; int index = 0;
        foreach (var light in EffectiveLights)
        {
            if (!light.Enabled || light.Intensity <= 0 || light.Color == System.Numerics.Vector3.Zero) continue;
            if (light.Receivers?.Affects(objectId) != false) result |= 1u << index;
            index++;
        }
        return result;
    }
}
