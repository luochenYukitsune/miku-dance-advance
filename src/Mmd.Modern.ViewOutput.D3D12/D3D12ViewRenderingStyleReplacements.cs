using System.Collections.ObjectModel;
using Mmd.Modern.Application;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>One referenced identity, including a default that every current instance overrides.
/// Affected instances include hidden objects; explicit overrides remain distinct from inheritance.</summary>
public sealed class D3D12ViewRenderingStyleReference
{
    public RenderingPresetReference Preset { get; }
    public D3D12StylePresetResult Resolution { get; }
    public bool IsDefault { get; }
    public IReadOnlyList<Guid> ExplicitObjectIds { get; }
    public IReadOnlyList<Guid> AffectedObjectIds { get; }

    internal D3D12ViewRenderingStyleReference(RenderingPresetReference preset, D3D12StylePresetResult resolution,
        bool isDefault, IEnumerable<Guid> explicitObjectIds, IEnumerable<Guid> affectedObjectIds)
    {
        Preset = preset; Resolution = resolution; IsDefault = isDefault;
        ExplicitObjectIds = Array.AsReadOnly(explicitObjectIds.ToArray());
        AffectedObjectIds = Array.AsReadOnly(affectedObjectIds.ToArray());
    }
}

/// <summary>Detached project-reference and catalog snapshot for a host's replacement selection UI.
/// Inspecting this report performs no file access, rendering, clock advancement or project edits.</summary>
public sealed class D3D12ViewRenderingStyleReplacementInspection
{
    public IReadOnlyList<D3D12ViewRenderingStyleReference> References { get; }
    public IReadOnlyList<D3D12ViewRenderingStyleReference> MissingStyles { get; }
    public IReadOnlyList<D3D12StylePresetResult> AvailableStyles { get; }

    internal D3D12ViewRenderingStyleReplacementInspection(IEnumerable<D3D12ViewRenderingStyleReference> references,
        D3D12StylePresetCatalog catalog)
    {
        References = Array.AsReadOnly(references.ToArray());
        MissingStyles = Array.AsReadOnly(References.Where(item => !item.Resolution.IsAvailable).ToArray());
        AvailableStyles = Array.AsReadOnly(catalog.List().Where(item => item.IsAvailable).ToArray());
    }
}

/// <summary>Immutable, explicit replacement proposal bound to one project owner and its selection/identity state.
/// Confirm changes only live metadata through the owner's existing Undo transaction. PMA is written only by Save.</summary>
public sealed class D3D12ViewRenderingStyleReplacementPlan
{
    private readonly SceneProjectSession owner;
    private readonly SceneRenderingStyles selection;
    private readonly D3D12ViewRenderingStyleReplacements.ObjectSelection[] objects;
    public D3D12ViewRenderingStyleReplacementInspection Inspection { get; }
    public IReadOnlyDictionary<string, string> Replacements { get; }
    public IReadOnlyList<D3D12ViewRenderingStyleReference> UnresolvedStyles { get; }
    public bool CanConfirm => UnresolvedStyles.Count == 0;

    internal D3D12ViewRenderingStyleReplacementPlan(SceneProjectSession owner, SceneRenderingStyles selection,
        D3D12ViewRenderingStyleReplacements.ObjectSelection[] objects,
        D3D12ViewRenderingStyleReplacementInspection inspection, Dictionary<string, string> replacements)
    {
        this.owner = owner; this.selection = selection; this.objects = objects.ToArray(); Inspection = inspection;
        Replacements = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(replacements, StringComparer.Ordinal));
        UnresolvedStyles = Array.AsReadOnly(inspection.MissingStyles
            .Where(item => !Replacements.ContainsKey(item.Preset.StableId)).ToArray());
    }

    /// <summary>Explicit host confirmation. Partial, stale, cancelled or different-owner plans never edit the project.
    /// Clock/visibility changes alone do not invalidate a selection. One Undo restores the complete old selection.</summary>
    public void Confirm(SceneProjectSession project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project); cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(project, owner))
            throw new InvalidOperationException("A rendering style replacement plan belongs to a different project owner.");
        if (!CanConfirm)
            throw new InvalidOperationException("Every unavailable project rendering style requires an explicit available replacement before confirmation.");
        var current = project.RenderingStyles;
        if (current.DefaultStyle != selection.DefaultStyle || current.ModelOverrides.Count != selection.ModelOverrides.Count
            || selection.ModelOverrides.Any(pair => !current.ModelOverrides.TryGetValue(pair.Key, out var value) || value != pair.Value)
            || !objects.SequenceEqual(D3D12ViewRenderingStyleReplacements.CaptureObjects(project)))
            throw new InvalidOperationException("The project's rendering style selection or object identities/roles changed; prepare a new replacement plan.");
        cancellationToken.ThrowIfCancellationRequested();
        project.EditRenderingStyles(edit =>
        {
            if (Replacements.TryGetValue(edit.DefaultStyle.StableId, out var replacement))
                edit.DefaultStyle = new(replacement);
            foreach (var pair in edit.ModelOverrides.ToArray())
                if (Replacements.TryGetValue(pair.Value.StableId, out replacement))
                    edit.ModelOverrides[pair.Key] = new(replacement);
        }, cancellationToken);
    }
}

/// <summary>Project-level resolution is separate from per-frame material preflight. A replacement proposal must
/// explicitly cover all unavailable authored references, including unused defaults and hidden overrides. Frame
/// preflight independently checks effective object selections; a fully overridden default does not shade that frame.</summary>
public static class D3D12ViewRenderingStyleReplacements
{
    internal readonly record struct ObjectSelection(Guid ObjectId, ModelRole ObjectRole, ModelRole EffectiveRole);

    public static D3D12ViewRenderingStyleReplacementInspection Inspect(SceneProjectSession project,
        D3D12StylePresetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(catalog);
        return Inspect(project.RenderingStyles, project.CaptureRenderSnapshot().RenderingStyles, catalog);
    }

    /// <summary>Accepts only directly available targets for referenced, currently unavailable source IDs.
    /// Partial mappings can be inspected, but cannot be confirmed. No fallback or chained mapping is inferred.</summary>
    public static D3D12ViewRenderingStyleReplacementPlan Prepare(SceneProjectSession project,
        D3D12StylePresetCatalog catalog, IEnumerable<KeyValuePair<string, string>> replacements)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(replacements);
        var selection = project.RenderingStyles;
        var snapshot = project.CaptureRenderSnapshot();
        var inspection = Inspect(selection, snapshot.RenderingStyles, catalog);
        var references = inspection.References.ToDictionary(item => item.Preset.StableId, StringComparer.Ordinal);
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in replacements)
        {
            // The valid source budget is bounded by the existing default plus explicit project references.
            if (copy.Count >= references.Count)
                throw new ArgumentException("Replacement mappings exceed the project's referenced style count.", nameof(replacements));
            _ = new RenderingPresetReference(pair.Key); _ = new RenderingPresetReference(pair.Value);
            if (!references.TryGetValue(pair.Key, out var source) || source.Resolution.IsAvailable)
                throw new ArgumentException($"Replacement source '{pair.Key}' must be an unavailable style referenced by this project.", nameof(replacements));
            if (!catalog.Find(pair.Value).IsAvailable)
                throw new ArgumentException($"Replacement target '{pair.Value}' must be directly available in the supplied catalog.", nameof(replacements));
            if (!copy.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException($"Replacement source '{pair.Key}' occurs more than once.", nameof(replacements));
        }
        return new(project, selection, CaptureObjects(project, snapshot.RenderingStyles), inspection, copy);
    }

    internal static ObjectSelection[] CaptureObjects(SceneProjectSession project)
        => CaptureObjects(project, project.CaptureRenderSnapshot().RenderingStyles);

    private static ObjectSelection[] CaptureObjects(SceneProjectSession project, IReadOnlyList<SceneRenderingStyleBinding> bindings)
        => project.Objects.Select((item, index) => new ObjectSelection(item.Id, item.Role, bindings[index].Role)).ToArray();

    private static D3D12ViewRenderingStyleReplacementInspection Inspect(SceneRenderingStyles selection,
        IReadOnlyList<SceneRenderingStyleBinding> bindings, D3D12StylePresetCatalog catalog)
    {
        var references = new[] { selection.DefaultStyle }.Concat(bindings
            .Where(item => selection.ModelOverrides.ContainsKey(item.ObjectId)).Select(item => selection.ModelOverrides[item.ObjectId]))
            .Distinct().Select(preset => new D3D12ViewRenderingStyleReference(preset, catalog.Find(preset.StableId),
                preset == selection.DefaultStyle,
                bindings.Where(item => selection.ModelOverrides.TryGetValue(item.ObjectId, out var explicitPreset) && explicitPreset == preset)
                    .Select(item => item.ObjectId),
                bindings.Where(item => item.Preset == preset).Select(item => item.ObjectId)));
        return new(references, catalog);
    }
}
