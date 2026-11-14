using System.Reflection;
using System.Runtime.Loader;

namespace Mmd.Modern.Plugins;

/// <summary>Private managed dependencies stay beneath the plugin directory.</summary>
/// <remarks>
/// Uses the official .NET AssemblyDependencyResolver/AssemblyLoadContext pattern:
/// https://learn.microsoft.com/dotnet/core/tutorials/creating-app-with-plugin-support
/// This original implementation adds explicit shared contracts and path checks.
/// A collectible context requests unloading; it cannot stop arbitrary plugin code.
/// </remarks>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _directory;
    private readonly AssemblyDependencyResolver _resolver;
    private readonly IReadOnlyDictionary<string, Assembly> _sharedAssemblies;

    internal PluginLoadContext(string id, string directory, string entryAssembly,
        IReadOnlyDictionary<string, Assembly> sharedAssemblies)
        : base($"MDA plugin: {id}", isCollectible: true)
    {
        _directory = directory;
        _sharedAssemblies = sharedAssemblies;
        _resolver = new AssemblyDependencyResolver(PluginPaths.RequireContainedFile(directory, entryAssembly));
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var simpleName = assemblyName.Name ?? throw new FileLoadException("An unnamed plugin dependency was requested.");
        if (_sharedAssemblies.TryGetValue(simpleName, out var shared))
        {
            var definition = shared.GetName();
            if (assemblyName.Version != definition.Version ||
                !string.Equals(assemblyName.CultureName ?? "", definition.CultureName ?? "", StringComparison.OrdinalIgnoreCase) ||
                !(assemblyName.GetPublicKeyToken() ?? []).SequenceEqual(definition.GetPublicKeyToken() ?? []))
                throw new FileLoadException($"Plugin contract assembly '{simpleName}' has an incompatible identity.");
            return shared;
        }
        if (PluginFrameworkIdentity.IsFrameworkAssembly(assemblyName))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            // Library plugins without a deps.json may carry simple private DLLs beside the entry.
            if (simpleName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0)
                throw new FileLoadException("Invalid plugin dependency assembly name.");
            var local = PluginPaths.RequireContainedFile(_directory, Path.Combine(_directory, simpleName + ".dll"));
            if (File.Exists(local))
                path = local;
        }
        if (path is null)
            throw new FileNotFoundException($"Private plugin dependency '{simpleName}' was not found inside its plugin directory.");
        return LoadFromAssemblyPath(PluginPaths.RequireContainedFile(_directory, path));
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path is null)
            throw new DllNotFoundException($"Native plugin dependency '{unmanagedDllName}' was not resolved inside its plugin directory.");
        return LoadUnmanagedDllFromPath(PluginPaths.RequireContainedFile(_directory, path));
    }
}

/// <summary>Identifies shared framework assemblies even in a self-contained payload.</summary>
internal static class PluginFrameworkIdentity
{
    private static readonly Lazy<IReadOnlyDictionary<string, AssemblyName>> RuntimeInventory = new(CreateRuntimeInventory);
    private static readonly HashSet<string> FrameworkTokens = new(StringComparer.Ordinal)
    {
        "b03f5f7f11d50a3a", "b77a5c561934e089", "cc7b13ffcd2ddd51", "31bf3856ad364e35", "7cec85d7bea7798e"
    };

    internal static bool IsFrameworkAssembly(AssemblyName requested) => IsFrameworkAssembly(requested, RuntimeInventory.Value);

    internal static bool IsFrameworkAssembly(AssemblyName requested, IReadOnlyDictionary<string, AssemblyName> inventory)
    {
        if (requested.Name is not { } name || !inventory.TryGetValue(name, out var definition))
            return false;
        return requested.Version is not null && requested.Version <= definition.Version &&
            string.Equals(requested.CultureName ?? "", definition.CultureName ?? "", StringComparison.OrdinalIgnoreCase) &&
            (requested.GetPublicKeyToken() ?? []).SequenceEqual(definition.GetPublicKeyToken() ?? []);
    }

    internal static IReadOnlyDictionary<string, AssemblyName> CreateInventory(IEnumerable<AssemblyName> available)
    {
        var result = new Dictionary<string, AssemblyName>(StringComparer.OrdinalIgnoreCase);
        foreach (var identity in available)
        {
            if (identity.Name is not { } name || !HasFrameworkName(name) ||
                !string.IsNullOrEmpty(identity.CultureName) || identity.Version is null ||
                !FrameworkTokens.Contains(Convert.ToHexStringLower(identity.GetPublicKeyToken() ?? [])))
                continue;
            result.TryAdd(name, identity);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, AssemblyName> CreateRuntimeInventory()
    {
        var directory = Path.GetDirectoryName(typeof(object).Assembly.Location)
            ?? throw new NotSupportedException("Plugin loading requires framework assembly metadata on disk.");
        var identities = new List<AssemblyName>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            if (!HasFrameworkName(Path.GetFileNameWithoutExtension(file)))
                continue;
            try { identities.Add(AssemblyName.GetAssemblyName(file)); }
            catch (BadImageFormatException) { } // Native framework libraries are not managed assembly identities.
        }
        // A self-contained directory also contains GUI, renderer and application DLLs.
        // Directory membership alone must never grant access to the host's assemblies.
        // Strong-name metadata distinguishes contract identities, not trusted or sandboxed code.
        return CreateInventory(identities);
    }

    private static bool HasFrameworkName(string name) => name is "System" or "mscorlib" or "netstandard" or
        "WindowsBase" or "Microsoft.CSharp" or "Microsoft.VisualBasic" or "Microsoft.VisualBasic.Core" or
        "Microsoft.Win32.Primitives" or "Microsoft.Win32.Registry" || name.StartsWith("System.", StringComparison.Ordinal);
}
