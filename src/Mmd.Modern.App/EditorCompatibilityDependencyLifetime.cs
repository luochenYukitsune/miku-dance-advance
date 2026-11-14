namespace Mmd.Modern.App;

/// <summary>Releases assets, then their private materialization, then native module dependencies.
/// A failed step keeps that step and every earlier acquisition owned for a later retry.</summary>
internal sealed class EditorCompatibilityDependencyLifetime : IDisposable
{
    private readonly object gate = new();
    private readonly List<IDisposable> resources = [];
    internal void Add(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (gate) resources.Add(resource);
    }
    public void Dispose()
    {
        lock (gate)
            while (resources.Count != 0)
            {
                int index = resources.Count - 1;
                resources[index].Dispose();
                resources.RemoveAt(index);
            }
    }
}

/// <summary>Used when initialization failed before a native GPU owner was returned.</summary>
internal sealed class EditorCompatibilityCompletedOwner : IAsyncDisposable
{
    internal static EditorCompatibilityCompletedOwner Instance { get; } = new();
    private EditorCompatibilityCompletedOwner() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
