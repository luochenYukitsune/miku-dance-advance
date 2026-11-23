using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>One scene instance's contiguous material range and stable preset reference. ObjectId identifies the
/// instance, never a filename or material name. The host maps its explicit ModelRole to IsCharacter and supplies
/// a frame with its existing role policy already applied; false preserves that incoming policy.</summary>
public sealed record D3D12StylePresetMaterialRange(Guid ObjectId, int MaterialStart, int MaterialCount,
    string PresetId, bool IsCharacter);

/// <summary>An explicit preset resolution failure. The whole application is rejected; no fallback is selected.</summary>
public sealed class D3D12StylePresetApplicationException : InvalidOperationException
{
    public Guid ObjectId { get; }
    public D3D12StylePresetResult Result { get; }

    internal D3D12StylePresetApplicationException(Guid objectId, D3D12StylePresetResult result)
        : base($"Object '{objectId:D}' cannot use preset '{result.StableId}': {result.Diagnostic}")
    { ObjectId = objectId; Result = result; }
}

/// <summary>Pure renderer consumption of the implemented preset parameters. All material ranges and preset
/// references, including non-character selections, are validated before constructing a frame. Character ranges
/// consume the existing Core outline and main-light flags and retain neutral, palette-preserving GI. Null preset
/// parameters preserve incoming material flags. Other roles and materials outside the ranges remain unchanged.
/// Applying a non-null character main-light response requires explicit modern frame options; the simple legacy
/// path does not consume that response. Outline-only presets can also use the legacy surface entry.
/// This API neither resolves project selection rules nor opens files, devices or output sessions.</summary>
public static class D3D12StylePresetApplicator
{
    public static OriginalFrameScene Apply(OriginalFrameScene frame, D3D12StylePresetCatalog catalog,
        IEnumerable<D3D12StylePresetMaterialRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(ranges);
        int materialCount = frame.Materials.Count;
        if (materialCount != frame.Surface.Materials.Count || materialCount != frame.Surface.Snapshot.Materials.Count)
            throw new ArgumentException("Frame flags, surface bindings and scene materials must have matching counts.", nameof(frame));

        var selection = ranges.ToArray();
        var objectIds = new HashSet<Guid>();
        foreach (var range in selection)
        {
            if (range is null || range.ObjectId == Guid.Empty || !objectIds.Add(range.ObjectId))
                throw new ArgumentException("Preset ranges require unique, nonempty scene instance identities.", nameof(ranges));
            if (range.MaterialStart < 0 || range.MaterialStart >= materialCount || range.MaterialCount <= 0
                || range.MaterialCount > materialCount - range.MaterialStart)
                throw new ArgumentException("Preset material ranges must be nonempty and within the frame material count.", nameof(ranges));
            D3D12StylePresetDefinition.ValidateIdentifier(range.PresetId, nameof(ranges));
        }
        int previousEnd = 0;
        foreach (var range in selection.OrderBy(range => range.MaterialStart))
        {
            if (range.MaterialStart < previousEnd)
                throw new ArgumentException("Preset material ranges must not overlap.", nameof(ranges));
            previousEnd = range.MaterialStart + range.MaterialCount;
        }

        // Resolve the complete selection before producing any material or frame snapshot.
        var definitions = new D3D12StylePresetDefinition[selection.Length];
        for (int i = 0; i < selection.Length; i++)
        {
            var result = catalog.Find(selection[i].PresetId);
            if (!result.IsAvailable)
                throw new D3D12StylePresetApplicationException(selection[i].ObjectId, result);
            definitions[i] = result.Definition!;
        }
        if (frame.Options is null)
            for (int i = 0; i < selection.Length; i++)
                if (selection[i].IsCharacter && definitions[i].CharacterToonResponse is not null)
                    throw new NotSupportedException("Character main-light style presets require explicit modern frame options; the simple legacy path does not consume the character toon response.");

        var materials = frame.Materials.ToArray();
        bool changed = false;
        for (int i = 0; i < selection.Length; i++)
        {
            var range = selection[i];
            if (!range.IsCharacter) continue;
            var definition = definitions[i];
            // The reserved built-in MMD selection is the original incoming material path.
            // A custom preset using pipelineId=mmd is still an explicit parameter override.
            if (definition.StableId == "mmd") continue;
            bool respectMaterialEdges = catalog.FindPlugin(definition.PipelineId)?.RespectMaterialEdgeFlag == true;
            for (int index = range.MaterialStart; index < range.MaterialStart + range.MaterialCount; index++)
            {
                var original = materials[index];
                // A plugin's default line style only recolors authored edges. Drawing a new hull
                // for a single-sided lining or expression card can cover the visible surface.
                var outline = definition.CharacterOutlineStyle;
                if (respectMaterialEdges && frame.Outline?.Materials[index] is { DrawEnabled: false })
                    outline = null;
                var updated = original with
                {
                    CharacterOutlineStyle = outline ?? original.CharacterOutlineStyle,
                    CharacterToonResponse = definition.CharacterToonResponse ?? original.CharacterToonResponse,
                    GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands,
                };
                if (updated == original) continue;
                materials[index] = updated;
                changed = true;
            }
        }
        return changed ? new(frame.Surface, frame.Outline, frame.OutlineOptions, frame.Options, materials,
            frame.Lighting, frame.EditorOverlay) : frame;
    }
}
