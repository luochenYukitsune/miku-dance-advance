using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>An explicitly prepared instance and its generated-data provenance.</summary>
public sealed record D3D12GeneratedMaterialPmaEntry(Guid ObjectId,
    D3D12GeneratedEndfieldMaterialPreparationResult Preparation);

/// <summary>Saves the complete selected generated-mapping set using the existing PMA codec.
/// The source guards, generated pixels and generation/edit provenance travel together.
/// As with the underlying mapping codec, callers must supply every mapping they wish to retain.</summary>
public static class D3D12GeneratedMaterialPma
{
    public static string ManifestPath(Guid objectId)
    {
        if (objectId == Guid.Empty) throw new ArgumentException("A nonempty instance identity is required.", nameof(objectId));
        return "render-resources/generated-materials/" + objectId.ToString("N") + ".json";
    }

    public static PmaSaveOptions CreateSaveOptions(SceneProjectSession project,
        IEnumerable<D3D12GeneratedMaterialPmaEntry> entries, PmaSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(entries);
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.AdditionalResources);
        var selected = entries.Take(D3D12MaterialBindingProfile.MaxMaterials + 1).ToArray();
        if (selected.Length > D3D12MaterialBindingProfile.MaxMaterials)
            throw new ArgumentException("Too many generated material instances.", nameof(entries));
        var resources = options.AdditionalResources.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var paths = resources.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappings = new List<D3D12MaterialBindingPmaEntry>();
        foreach (var entry in selected)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(entry.Preparation);
            string path = ManifestPath(entry.ObjectId);
            if (!paths.Add(path))
                throw new ArgumentException("Duplicate generated provenance resource: " + path, nameof(entries));
            resources.Add(path, entry.Preparation.ManifestBytes);
            mappings.Add(new(entry.ObjectId, entry.Preparation.Profile, entry.Preparation.SourceRest));
        }
        return D3D12MaterialBindingPma.CreateSaveOptions(project, mappings,
            options with { AdditionalResources = resources });
    }
}
