namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Resolves writable runtime locations without creating directories or changing process state.</summary>
internal static class D3D12RuntimePaths
{
    internal static string ResolveDxcCacheDirectory(string? cacheDirectory, string? environmentCacheDirectory,
        string? workspaceRoot, string? localApplicationData)
    {
        if (cacheDirectory is not null) return Path.GetFullPath(cacheDirectory);
        if (environmentCacheDirectory is not null) return Path.GetFullPath(environmentCacheDirectory);
        if (workspaceRoot is not null)
            return Path.GetFullPath(Path.Combine(workspaceRoot, ".dev-cache", "modern-dotnet", "dxc-compute"));
        return Path.Combine(LocalDataRoot(localApplicationData, "MMD_DXC_CACHE"), "Dxc");
    }

    internal static string ResolveDlssLogDirectory(string? environmentLogDirectory, string? localApplicationData)
    {
        if (environmentLogDirectory is not null) return Path.GetFullPath(environmentLogDirectory);
        return Path.Combine(LocalDataRoot(localApplicationData, "MMD_DLSS_LOG_DIRECTORY"), "Logs", "Dlss");
    }

    private static string LocalDataRoot(string? localApplicationData, string overrideVariable)
    {
        if (string.IsNullOrWhiteSpace(localApplicationData) || !Path.IsPathFullyQualified(localApplicationData))
            throw new InvalidOperationException(
                $"LocalApplicationData is unavailable or is not an absolute path. Set {overrideVariable} to a writable directory.");
        return Path.GetFullPath(Path.Combine(localApplicationData, "MDA"));
    }
}
