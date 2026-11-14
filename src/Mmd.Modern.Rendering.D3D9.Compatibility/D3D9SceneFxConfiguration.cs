namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>
/// Explicit scene-effects opt-in using the existing copied assignment/resource contract. A nonempty
/// resource map is required. The caller must retain the complete immutable dependency tree through
/// successful session disposal; this declaration does not discover dependencies or import EMM mappings.
/// </summary>
public sealed class D3D9SceneFxConfiguration
{
    public D3D9SceneFxConfiguration(IEnumerable<D3D9ObjectFxAssignment> assignments, D3D9EffectResourceMap resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (resources.Bindings.Count == 0)
            throw new ArgumentException("Scene FX requires a nonempty effect resource map.", nameof(resources));
        CopiedConfiguration = new(assignments, resources);
    }

    public IReadOnlyList<D3D9ObjectFxAssignment> Assignments => CopiedConfiguration.Assignments;
    public D3D9EffectResourceMap Resources => CopiedConfiguration.Resources!;
    internal D3D9ObjectFxConfiguration CopiedConfiguration { get; }
}
