using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>A manual confirmation row for one stored PMM model identity. A null Purpose means unanswered;
/// an explicit Unspecified purpose is a confirmed answer. Paths and names never determine purpose.</summary>
public sealed record PmmModelRoleSelection(int ModelIndex, string Name, PmmString Reference,
    string? ResolvedPath, ModelPurpose? Purpose)
{
    public bool IsConfirmed => Purpose is not null;
    public ModelRole Role => Purpose?.Role ?? ModelRole.Unspecified;
    /// <summary>Existing lighting/PMA metadata can retain this index-based identity without a new schema.</summary>
    public string StableKey => "pmm-model:" + ModelIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
