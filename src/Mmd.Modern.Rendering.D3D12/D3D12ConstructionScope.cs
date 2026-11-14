namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Owns completed native allocations until a resource bundle is successfully constructed.</summary>
internal sealed class D3D12ConstructionScope : IDisposable
{
    private readonly List<IDisposable> resources = [];
    internal T Own<T>(T resource) where T : IDisposable { resources.Add(resource); return resource; }
    internal void Complete() => resources.Clear();
    public void Dispose()
    {
        for (int i = resources.Count - 1; i >= 0; i--) resources[i].Dispose();
        resources.Clear();
    }
}
