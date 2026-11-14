namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>
/// Retains an explicitly declared complete scene FX dependency tree using the same file/ancestor leases
/// as object FX. No dependencies are collected automatically. Existing session references survive Dispose
/// until native Destroy and module unload succeed, including initialization-cleanup retries.
/// </summary>
public sealed class D3D9SceneFxAssets : IDisposable
{
    private readonly D3D9ObjectFxAssets retained;

    private D3D9SceneFxAssets(D3D9SceneFxConfiguration configuration, D3D9ObjectFxAssets retained)
    {
        Configuration = configuration;
        this.retained = retained;
    }

    public D3D9SceneFxConfiguration Configuration { get; }
    public IReadOnlyList<string> CompleteDependencyPaths => retained.CompleteDependencyPaths;

    public static D3D9SceneFxAssets Open(D3D9SceneFxConfiguration configuration, IEnumerable<string> completeDependencyPaths)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new(configuration, D3D9ObjectFxAssets.Open(configuration.CopiedConfiguration, completeDependencyPaths));
    }

    public void Dispose() => retained.Dispose();
    internal D3D9ObjectFxAssets.Lease Acquire() => retained.Acquire();
}
