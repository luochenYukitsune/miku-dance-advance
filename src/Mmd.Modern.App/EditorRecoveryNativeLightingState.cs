using System.Collections.Immutable;
using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

/// <summary>Editor-only light drafts. Committed identity and historical numeric baselines are distinct:
/// a successful inline commit does not replace an existing item's original float samples.</summary>
public sealed record EditorRecoveryNativeLightingState
{
    public required EditorInspectorPage InspectorPage { get; init; }
    public required Guid? SelectedLightId { get; init; }
    // The hidden inspector can remember a row after its viewport handle was deselected.
    public Guid? ActiveLightId { get; init; }
    public bool RotateHandle { get; init; }
    public required ImmutableArray<NativeSceneLight> CommittedLights { get; init; }
    public required ImmutableArray<EditorRecoveryNativeLightDraft> Lights { get; init; }

    internal void Validate()
    {
        if (!Enum.IsDefined(InspectorPage) || CommittedLights.IsDefault || Lights.IsDefault
            || CommittedLights.Length > SceneLightingSnapshot.MaxLights || Lights.Length != CommittedLights.Length)
            throw EditorRecoverySessionState.Invalid("Recovery native lighting state is invalid.");
        try { _ = new SceneLightingSnapshot(CommittedLights); }
        catch (ArgumentException error) { throw EditorRecoverySessionState.Invalid("Recovery committed lighting is invalid.", error); }
        var ids = new HashSet<Guid>();
        for (int index = 0; index < Lights.Length; index++)
        {
            var light = Lights[index];
            if (light is null || light.Original is null || !ids.Add(light.Original.Id) || light.Original.Id != CommittedLights[index].Id)
                throw EditorRecoverySessionState.Invalid("Recovery native light identities are inconsistent.");
            light.Validate();
        }
        if (SelectedLightId is { } selected && !ids.Contains(selected))
            throw EditorRecoverySessionState.Invalid("Recovery selected native light is absent.");
        if (ActiveLightId is { } active && !ids.Contains(active))
            throw EditorRecoverySessionState.Invalid("Recovery active native light is absent.");
    }

    internal void ValidateAgainst(SceneLightingSnapshot committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        Validate();
        if (!CommittedLights.SequenceEqual(committed.Lights))
            throw EditorRecoverySessionState.Invalid("Recovery native lighting does not match the project.");
    }
}

public sealed record EditorRecoveryNativeLightDraft
{
    public required NativeSceneLight Original { get; init; }
    public required Vector3 OriginalDegrees { get; init; }
    public required string Name { get; init; }
    public required int KindIndex { get; init; }
    public required bool Enabled { get; init; }
    public required bool CastShadows { get; init; }
    public required decimal? PositionX { get; init; }
    public required decimal? PositionY { get; init; }
    public required decimal? PositionZ { get; init; }
    public required decimal? RotationX { get; init; }
    public required decimal? RotationY { get; init; }
    public required decimal? RotationZ { get; init; }
    public required decimal? Red { get; init; }
    public required decimal? Green { get; init; }
    public required decimal? Blue { get; init; }
    public required decimal? Intensity { get; init; }
    public required decimal? Range { get; init; }
    public required decimal? InnerConeDegrees { get; init; }
    public required decimal? OuterConeDegrees { get; init; }
    // Null means that the hex editor has not acquired an independent raw input.
    public string? HexText { get; init; }
    public ImmutableSortedDictionary<string, string> NumericText { get; init; } = ImmutableSortedDictionary<string, string>.Empty;

    internal static readonly IReadOnlySet<string> NumericNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "InlineLightIntensity", "InlineLightRange", "InlineLightInnerCone", "InlineLightOuterCone",
        "InlineLightPositionX", "InlineLightPositionY", "InlineLightPositionZ",
        "InlineLightRotationX", "InlineLightRotationY", "InlineLightRotationZ",
        "InlineLightRed", "InlineLightGreen", "InlineLightBlue"
    };

    internal void Validate()
    {
        if (Original is null) throw EditorRecoverySessionState.Invalid("Recovery native light baseline is missing.");
        try { _ = new SceneLightingSnapshot([Original]); }
        catch (ArgumentException error) { throw EditorRecoverySessionState.Invalid("Recovery native light baseline is invalid.", error); }
        EditorRecoverySessionState.ValidateText(Original.Name);
        EditorRecoverySessionState.ValidateText(Name);
        if (!EditorRecoverySessionState.Finite(OriginalDegrees)
            || OriginalDegrees != EditorNativeLightItem.RotationDegrees(Original.Rotation)
            || NumericText is null || NumericText.Count > NumericNames.Count)
            throw EditorRecoverySessionState.Invalid("Recovery native light numeric baseline is invalid.");
        if (HexText is not null) EditorRecoverySessionState.ValidateText(HexText);
        foreach (var input in NumericText)
        {
            if (!NumericNames.Contains(input.Key)) throw EditorRecoverySessionState.Invalid("Recovery native light field is unsupported.");
            EditorRecoverySessionState.ValidateText(input.Value);
        }
        // Empty names, nullable/out-of-range numbers and unsupported kind indices are unfinished
        // editor input. Only ordinary Build validation may publish them later.
    }
}
