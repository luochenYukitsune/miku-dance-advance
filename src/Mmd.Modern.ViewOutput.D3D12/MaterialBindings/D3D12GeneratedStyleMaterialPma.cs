using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput.D3D12;

public sealed record D3D12GeneratedStyleMaterialPmaEntry(Guid ObjectId, D3D12GeneratedStyleMaterialResult Preparation);

/// <summary>Complete desired generated-style mapping set, without modifying live project state.</summary>
public static class D3D12GeneratedStyleMaterialPma
{
    public static string ManifestPath(Guid objectId)
    {
        if (objectId == Guid.Empty) throw new ArgumentException("A nonempty instance identity is required.", nameof(objectId));
        return "render-resources/generated-styles/" + objectId.ToString("N") + ".json";
    }

    public static PmaSaveOptions CreateSaveOptions(SceneProjectSession project,
        IEnumerable<D3D12GeneratedStyleMaterialPmaEntry> entries, PmaSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(entries);
        options ??= new(); ArgumentNullException.ThrowIfNull(options.AdditionalResources);
        var selected = entries.Take(4097).ToArray();
        if (selected.Length > 4096) throw new ArgumentException("Too many generated style instances.", nameof(entries));
        var resources = options.AdditionalResources.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var claimed = resources.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappings = new List<D3D12MaterialBindingPmaEntry>();
        foreach (var entry in selected)
        {
            ArgumentNullException.ThrowIfNull(entry); ArgumentNullException.ThrowIfNull(entry.Preparation);
            string path = ManifestPath(entry.ObjectId);
            if (!claimed.Add(path)) throw new ArgumentException("Duplicate generated style resource: " + path, nameof(entries));
            resources.Add(path, entry.Preparation.ManifestBytes);
            mappings.Add(new(entry.ObjectId, entry.Preparation.Profile, entry.Preparation.SourceRest));
        }
        return D3D12MaterialBindingPma.CreateSaveOptions(project, mappings, options with { AdditionalResources = resources });
    }
}
