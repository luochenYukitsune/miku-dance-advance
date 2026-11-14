using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

internal sealed record PmmDrawOrderEntry(PmmDrawableKind Kind, int StoredIndex, byte SourceDrawOrderIndex, int DrawOrderIndex);

/// <summary>The existing neutral PMM mixed-order policy, shared by both project owners.</summary>
internal static class PmmDrawOrderPolicy
{
    internal static (IReadOnlyList<PmmDrawOrderEntry> Items, IReadOnlyList<string> Diagnostics) Build(PmmDocument document)
    {
        int accessoryCount = document.Accessories.Count;
        int cut = document.Settings.AccessoryIndexAfterModels;
        if (cut < -1 || cut >= accessoryCount && !(accessoryCount == 0 && cut == 0))
            throw new NotSupportedException("PMM mixed draw order requires an accessory cut of -1 or a zero-based index within the accessory count.");
        var items = new List<PmmDrawOrderEntry>(document.Models.Count + accessoryCount);
        foreach (var accessory in document.Accessories)
        {
            int order = accessory.DrawOrderIndex;
            if (cut >= 0 && order >= cut) order += document.Models.Count;
            items.Add(new(PmmDrawableKind.Accessory, accessory.Index, accessory.DrawOrderIndex, order));
        }
        foreach (var model in document.Models)
            items.Add(new(PmmDrawableKind.Model, model.Index, model.DrawOrderIndex, model.DrawOrderIndex + Math.Max(cut, 0)));
        var diagnostics = items.GroupBy(item => item.DrawOrderIndex).Where(group => group.Count() > 1)
            .Select(group => $"PMM objects have the same draw order {group.Key}; "
                + "the deterministic fallback preserves accessory document order followed by model document order. Original MMD tie behavior is unverified.").ToArray();
        return (Array.AsReadOnly(items.OrderBy(item => item.DrawOrderIndex).ToArray()), Array.AsReadOnly(diagnostics));
    }
}
