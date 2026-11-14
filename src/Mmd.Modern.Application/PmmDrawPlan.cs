using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

public enum PmmDrawableKind { Model, Accessory }

/// <summary>StoredIndex is the PMM object ID, not a position in the plan or source document.</summary>
public sealed record PmmDrawItem(PmmDrawableKind Kind, int StoredIndex, byte SourceDrawOrderIndex,
    int DrawOrderIndex, OriginalFrameScene Frame, bool Visible, bool AddBlendEnabled);

/// <summary>A sampled heterogeneous draw sequence. Hidden entries retain their positions and identity.
/// Hosts must honor AddBlendEnabled or explicitly reject the corresponding visible accessory.</summary>
public sealed class PmmDrawPlan
{
    public IReadOnlyList<PmmDrawItem> Items { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    internal PmmDrawPlan(IEnumerable<PmmDrawItem> items, IEnumerable<string> diagnostics)
    {
        Items = Array.AsReadOnly(items.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }
}
