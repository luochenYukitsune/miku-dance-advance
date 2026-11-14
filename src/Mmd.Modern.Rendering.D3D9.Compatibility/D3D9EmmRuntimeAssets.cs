using System.Collections.ObjectModel;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>
/// Retains every materialized file in the frozen EMM resource map using the existing read-only FX lease.
/// The map must already contain the complete dependency closure. No original path is opened and no file
/// discovery, parsing, copying or effect selection occurs here. Existing sessions survive this owner's
/// disposal and retain their lease until native destruction and module unloading both succeed.
/// </summary>
public sealed class D3D9EmmRuntimeAssets : IDisposable
{
    private readonly D3D9ObjectFxAssets resources;

    private D3D9EmmRuntimeAssets(D3D9EmmRuntimeConfiguration configuration, D3D9ObjectFxAssets resources)
    { Configuration = configuration; this.resources = resources; }

    public D3D9EmmRuntimeConfiguration Configuration { get; }
    public IReadOnlyList<string> CompleteDependencyPaths => resources.CompleteDependencyPaths;

    public static D3D9EmmRuntimeAssets Open(D3D9EmmRuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // An empty assignment table is intentional: only the resource lease is reused. The EMM tables
        // and their absent/none/hide/main_default distinctions are never projected onto object FX rows.
        var resourceOnly = new D3D9ObjectFxConfiguration(Array.Empty<D3D9ObjectFxAssignment>(), configuration.Resources);
        return new(configuration, D3D9ObjectFxAssets.Open(resourceOnly,
            configuration.Resources.Bindings.Select(row => row.MaterializedAbsolutePath)));
    }

    public void Dispose() => resources.Dispose();
    internal D3D9ObjectFxAssets.Lease Acquire() => resources.Acquire();
}

/// <summary>Session-local copied identity map; neither record keys nor draw-order slots become identities.</summary>
internal sealed class D3D9EmmSessionConfiguration
{
    internal D3D9EmmSessionConfiguration(D3D9EmmRuntimeConfiguration configuration,
        IReadOnlyDictionary<Guid, ulong> nativeObjectIds)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(nativeObjectIds);
        var copied = new Dictionary<Guid, ulong>();
        var unique = new HashSet<ulong>();
        foreach (var value in configuration.Objects)
        {
            if (!nativeObjectIds.TryGetValue(value.ObjectId, out var id) || id == 0 || !unique.Add(id))
                throw new ArgumentException("Every EMM live object requires one nonzero unique native identity across both object kinds.", nameof(nativeObjectIds));
            copied.Add(value.ObjectId, id);
        }
        if (copied.Count != nativeObjectIds.Count)
            throw new ArgumentException("The native identity map must contain exactly the EMM configuration's live objects.", nameof(nativeObjectIds));
        Configuration = configuration;
        NativeObjectIds = new ReadOnlyDictionary<Guid, ulong>(copied);
    }

    internal D3D9EmmRuntimeConfiguration Configuration { get; }
    internal IReadOnlyDictionary<Guid, ulong> NativeObjectIds { get; }
}
