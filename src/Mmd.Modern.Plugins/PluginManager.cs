using System.Reflection;
using System.Runtime.Loader;
using Mmd.Modern.Plugins.Contracts;

namespace Mmd.Modern.Plugins;

/// <summary>Discovers disabled plugins and owns their serialized, explicit lifecycle.</summary>
/// <remarks>
/// Discovery reads manifests only from immediate child directories of the supplied roots.
/// Refresh normally requires disabled plugins. An explicit preserving refresh retains
/// unchanged active instances and rejects discovery changes that would replace them.
/// Plugins execute with the host process's permissions. This is not a security sandbox.
/// </remarks>
public sealed class PluginManager : IAsyncDisposable
{
    private const int MaximumPlugins = 1024;
    private readonly PluginHostInfo _host;
    private readonly IReadOnlyDictionary<string, PluginCapability> _capabilities;
    private readonly IReadOnlyDictionary<string, Assembly> _sharedAssemblies;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PluginExtensionRegistry _registry = new();
    private readonly List<Entry> _activationOrder = [];
    private List<Entry> _entries = [];
    private Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private IReadOnlyList<PluginInfo> _snapshot = Array.Empty<PluginInfo>();
    private bool _disposed;

    public PluginManager(PluginHostInfo host, IEnumerable<PluginCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(host.ApplicationVersion);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (host.ApiVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(host), "The host API version must be positive.");
        _host = host;
        var capabilityMap = new Dictionary<string, PluginCapability>(StringComparer.Ordinal);
        var sharedAssemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        AddSharedAssembly(typeof(IMdaPlugin).Assembly, sharedAssemblies);
        foreach (var capability in capabilities)
        {
            ArgumentNullException.ThrowIfNull(capability);
            ArgumentNullException.ThrowIfNull(capability.ContractType);
            PluginManifest.RequireIdentifier(capability.Id, "host capability id");
            if (capability.ContractType.IsValueType || capability.ContractType.ContainsGenericParameters)
                throw new ArgumentException("Capability contracts must be closed reference types.", nameof(capabilities));
            if (!capabilityMap.TryAdd(capability.Id, capability))
                throw new ArgumentException($"Duplicate host capability '{capability.Id}'.", nameof(capabilities));
            AddSharedAssembly(capability.ContractType.Assembly, sharedAssemblies);
        }
        _capabilities = capabilityMap;
        _sharedAssemblies = sharedAssemblies;
    }

    public IReadOnlyList<PluginInfo> Plugins => Volatile.Read(ref _snapshot);
    public IPluginExtensionRegistry Extensions => _registry;

    /// <summary>Raised after <see cref="Plugins"/> is updated outside the manager synchronization gate.</summary>
    public event EventHandler? PluginsChanged;

    public Task<PluginOperationResult> RefreshAsync(IEnumerable<string> roots, CancellationToken cancellationToken = default) =>
        RefreshAsync(roots, preserveActive: false, cancellationToken);

    /// <summary>Discovers plugins, optionally retaining unchanged active instances and registrations.</summary>
    /// <remarks>
    /// Preserving discovery never starts, stops or reloads a plugin. A missing, changed,
    /// invalid or ambiguous active plugin rejects the entire refresh without publishing it.
    /// </remarks>
    public async Task<PluginOperationResult> RefreshAsync(IEnumerable<string> roots, bool preserveActive,
        CancellationToken cancellationToken = default)
    {
        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
            return Failure("Plugin refresh was canceled.");
        (PluginOperationResult Result, bool Changed) outcome;
        try
        {
            outcome = await RefreshCoreAsync(roots, preserveActive, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        if (outcome.Changed)
            OnPluginsChanged();
        return outcome.Result;
    }

    public async Task<PluginOperationResult> EnableAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
            return Failure("Plugin activation was canceled.");
        (PluginOperationResult Result, bool Changed) outcome;
        try
        {
            outcome = await EnableCoreAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        if (outcome.Changed)
            OnPluginsChanged();
        return outcome.Result;
    }

    public async Task<PluginOperationResult> DisableAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
            return Failure("Plugin deactivation was canceled.");
        (PluginOperationResult Result, bool Changed) outcome;
        try
        {
            outcome = await DisableCoreAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        if (outcome.Changed)
            OnPluginsChanged();
        return outcome.Result;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            Volatile.Write(ref _disposed, true);
            PluginsChanged = null;
            foreach (var entry in _activationOrder.ToArray().Reverse())
                await StopAsync(entry).ConfigureAwait(false);
            Publish();
        }
        finally
        {
            // Keep the semaphore usable so already queued callers can observe disposal.
            _gate.Release();
        }
    }

    private async Task<(PluginOperationResult Result, bool Changed)> RefreshCoreAsync(
        IEnumerable<string> roots, bool preserveActive, CancellationToken cancellationToken)
    {
        try
        {
            if (_disposed)
                return (Failure("The plugin manager is disposed."), false);
            if (!preserveActive && _activationOrder.Count != 0)
                return (Failure("Disable all active plugins before refreshing the plugin list."), false);
            ArgumentNullException.ThrowIfNull(roots);
            var discovered = new List<Entry>();
            var seenDirectories = new HashSet<string>(PluginPaths.Comparer);
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] directories;
                string fullRoot;
                try
                {
                    fullRoot = Path.GetFullPath(root);
                    if (!Directory.Exists(fullRoot) && !File.Exists(fullRoot))
                        continue;
                    directories = Directory.GetDirectories(fullRoot, "*", SearchOption.TopDirectoryOnly);
                    Array.Sort(directories, PluginPaths.Comparer);
                }
                catch (Exception exception)
                {
                    discovered.Add(InvalidEntry(root ?? "", Describe(exception)));
                    continue;
                }
                foreach (var directory in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!seenDirectories.Add(directory))
                        continue;
                    if (discovered.Count >= MaximumPlugins)
                        return (Failure($"Plugin discovery exceeds the limit of {MaximumPlugins} directories."), false);
                    try
                    {
                        var manifest = await PluginManifest.ReadAsync(directory, cancellationToken).ConfigureAwait(false);
                        discovered.Add(new Entry(directory, manifest));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        discovered.Add(InvalidEntry(directory, Describe(exception)));
                    }
                }
            }
            if (preserveActive && _activationOrder.Count != 0)
            {
                var byDirectory = discovered.Where(e => e.Manifest is not null)
                    .ToDictionary(e => e.Directory, PluginPaths.Comparer);
                foreach (var active in _activationOrder)
                {
                    if (!byDirectory.TryGetValue(active.Directory, out var candidate) || candidate.Manifest is null ||
                        !active.Manifest!.Digest.AsSpan().SequenceEqual(candidate.Manifest.Digest))
                        return (Failure($"Active plugin '{active.Id}' is missing or its manifest changed. Disable it before refreshing."), false);
                }
                // Reuse the actual entries: their runtime, collectible context and registrations
                // remain owned by the original activation order throughout the refresh.
                var activeByDirectory = _activationOrder.ToDictionary(e => e.Directory, PluginPaths.Comparer);
                for (var index = 0; index < discovered.Count; index++)
                    if (activeByDirectory.TryGetValue(discovered[index].Directory, out var active))
                        discovered[index] = active;
            }
            var byId = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var group in discovered.Where(e => e.Manifest is not null).GroupBy(e => e.Manifest!.Id, StringComparer.Ordinal))
            {
                var matches = group.ToArray();
                if (matches.Length > 1)
                {
                    if (matches.Any(e => e.Runtime is not null))
                        return (Failure($"Duplicate plugin id '{group.Key}' conflicts with an active plugin. Disable it before refreshing."), false);
                    foreach (var duplicate in matches)
                        duplicate.Reject($"Duplicate plugin id '{group.Key}'.");
                    continue;
                }
                var entry = matches[0];
                byId.Add(group.Key, entry);
                if (entry.Runtime is null)
                    ValidateHostCompatibility(entry);
            }
            // Active entries are frozen and may not be marked incompatible during staging.
            // Validate their full dependency closure before mutating any new entry's state.
            foreach (var active in _activationOrder)
            {
                var diagnostic = ValidateGraph(active, byId, new HashSet<string>(StringComparer.Ordinal), null);
                if (diagnostic is not null)
                    return (Failure(diagnostic), false);
            }
            foreach (var entry in discovered.Where(e => e.Manifest is not null && e.Runtime is null && e.State != PluginState.Incompatible))
            {
                var diagnostic = ValidateGraph(entry, byId, new HashSet<string>(StringComparer.Ordinal), null);
                if (diagnostic is not null)
                    entry.Reject(diagnostic);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _entries = discovered;
            _byId = byId;
            Publish();
            return (Success(), true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (Failure("Plugin refresh was canceled."), false);
        }
        catch (Exception exception)
        {
            return (Failure(Describe(exception)), false);
        }
    }

    private async Task<(PluginOperationResult Result, bool Changed)> EnableCoreAsync(
        string id, CancellationToken cancellationToken)
    {
        var newlyStarted = new List<Entry>();
        Entry? target = null;
        try
        {
            if (_disposed)
                return (Failure("The plugin manager is disposed."), false);
            if (id is null || !_byId.TryGetValue(id, out target))
                return (Failure($"Plugin '{id}' was not found or its identifier is ambiguous."), false);
            var plan = new List<Entry>();
            var diagnostic = ValidateGraph(target, _byId, new HashSet<string>(StringComparer.Ordinal), plan);
            if (diagnostic is not null)
                return (Failure(diagnostic), false);
            // All graph, version and capability checks complete before any instance is created.
            foreach (var entry in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Runtime is not null)
                    continue;
                var result = await StartAsync(entry, cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    var rollback = await RollbackAsync(newlyStarted).ConfigureAwait(false);
                    if (target != entry)
                    {
                        target.State = PluginState.Failed;
                        target.Diagnostic = $"Dependency '{entry.Manifest!.Id}' failed: {result.Diagnostic}";
                    }
                    Publish();
                    return (Failure(Combine(result.Diagnostic, rollback)), true);
                }
                newlyStarted.Add(entry);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (newlyStarted.Count == 0)
                return (Success(), false);
            Publish();
            return (Success(), true);
        }
        catch (Exception exception)
        {
            var diagnostic = exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? "Plugin activation was canceled." : Describe(exception);
            var rollback = await RollbackAsync(newlyStarted).ConfigureAwait(false);
            var changed = newlyStarted.Count > 0;
            if (target is not null && target.Runtime is null)
            {
                target.State = PluginState.Failed;
                target.Diagnostic = diagnostic;
                changed = true;
            }
            if (changed)
                Publish();
            return (Failure(Combine(diagnostic, rollback)), changed);
        }
    }

    private async Task<(PluginOperationResult Result, bool Changed)> DisableCoreAsync(
        string id, CancellationToken cancellationToken)
    {
        try
        {
            if (_disposed)
                return (Failure("The plugin manager is disposed."), false);
            if (id is null || !_byId.TryGetValue(id, out var entry))
                return (Failure($"Plugin '{id}' was not found or its identifier is ambiguous."), false);
            if (entry.Runtime is null)
                return (Success(), false);
            var dependents = _activationOrder.Where(e => e.Manifest!.Dependencies.Any(d => d.Id == id)).ToArray();
            if (dependents.Length != 0)
                return (Failure($"Plugin '{id}' is required by active plugins: {string.Join(", ", dependents.Select(e => e.Manifest!.Id))}."), false);
            cancellationToken.ThrowIfCancellationRequested();
            // Once stopping starts, cleanup finishes even if the caller cancels.
            var diagnostic = await StopAsync(entry).ConfigureAwait(false);
            Publish();
            return (diagnostic is null ? Success() : Failure(diagnostic), true);
        }
        catch (Exception exception)
        {
            return (Failure(exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? "Plugin deactivation was canceled." : Describe(exception)), false);
        }
    }

    private void OnPluginsChanged()
    {
        if (!Volatile.Read(ref _disposed))
            PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ValidateHostCompatibility(Entry entry)
    {
        var manifest = entry.Manifest!;
        if (manifest.ApiVersion != _host.ApiVersion)
            entry.Reject($"Plugin API version {manifest.ApiVersion} is incompatible with host API version {_host.ApiVersion}.");
        else if (_host.ApplicationVersion < manifest.MinimumHostVersion)
            entry.Reject($"Plugin requires host version {manifest.MinimumHostVersion} or newer.");
        else
        {
            var unsupported = manifest.Capabilities.FirstOrDefault(id => !_capabilities.ContainsKey(id));
            if (unsupported is not null)
                entry.Reject($"Unsupported plugin capability '{unsupported}'.");
        }
    }

    private static string? ValidateGraph(Entry entry, IReadOnlyDictionary<string, Entry> byId,
        HashSet<string> visiting, List<Entry>? plan, HashSet<string>? completed = null)
    {
        completed ??= new HashSet<string>(StringComparer.Ordinal);
        if (entry.State == PluginState.Incompatible)
            return Limit($"Plugin '{entry.Id}' is incompatible: {entry.Diagnostic}");
        var manifest = entry.Manifest!;
        if (completed.Contains(manifest.Id))
            return null;
        if (!visiting.Add(manifest.Id))
            return $"Plugin dependency cycle detected at '{manifest.Id}'.";
        foreach (var dependency in manifest.Dependencies)
        {
            if (!byId.TryGetValue(dependency.Id, out var required))
                return $"Plugin '{manifest.Id}' requires missing or ambiguous dependency '{dependency.Id}'.";
            var version = required.Manifest!.Version;
            if (version < dependency.MinimumVersion || dependency.MaximumVersion is not null && version > dependency.MaximumVersion)
                return $"Dependency '{dependency.Id}' version {version} is outside the range required by '{manifest.Id}'.";
            var diagnostic = ValidateGraph(required, byId, visiting, plan, completed);
            if (diagnostic is not null)
                return diagnostic;
        }
        visiting.Remove(manifest.Id);
        completed.Add(manifest.Id);
        plan?.Add(entry);
        return null;
    }

    private async Task<PluginOperationResult> StartAsync(Entry entry, CancellationToken cancellationToken)
    {
        PluginLoadContext? loadContext = null;
        IMdaPlugin? plugin = null;
        PluginStartContext? context = null;
        try
        {
            var manifest = entry.Manifest!;
            await manifest.VerifyUnchangedAsync(entry.Directory, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            loadContext = new PluginLoadContext(manifest.Id, entry.Directory, manifest.EntryAssembly, _sharedAssemblies);
            var assembly = loadContext.LoadFromAssemblyPath(manifest.EntryAssembly);
            var type = assembly.GetType(manifest.EntryType, throwOnError: true, ignoreCase: false)!;
            if (!type.IsVisible || type.IsAbstract || !typeof(IMdaPlugin).IsAssignableFrom(type))
                throw new InvalidDataException("entryType must be a public, concrete IMdaPlugin implementation.");
            plugin = Activator.CreateInstance(type) as IMdaPlugin
                ?? throw new InvalidDataException("entryType requires a public parameterless constructor.");
            context = new PluginStartContext(manifest, _host, _capabilities);
            await plugin.StartAsync(context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _registry.Commit(context.Close(keepRegistrations: true));
            entry.Runtime = new Runtime(plugin, loadContext);
            entry.State = PluginState.Active;
            entry.Diagnostic = null;
            _activationOrder.Add(entry);
            return Success();
        }
        catch (Exception exception)
        {
            context?.Close(keepRegistrations: false);
            _registry.Remove(entry.Id);
            var diagnostic = exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? "Plugin activation was canceled." : Describe(exception);
            var cleanup = await CleanupAsync(plugin, loadContext).ConfigureAwait(false);
            entry.State = PluginState.Failed;
            entry.Diagnostic = Combine(diagnostic, cleanup);
            return Failure(entry.Diagnostic);
        }
    }

    private async Task<string?> RollbackAsync(List<Entry> newlyStarted)
    {
        string? diagnostic = null;
        for (var index = newlyStarted.Count - 1; index >= 0; index--)
            diagnostic = Combine(diagnostic, await StopAsync(newlyStarted[index]).ConfigureAwait(false));
        return diagnostic;
    }

    private async Task<string?> StopAsync(Entry entry)
    {
        var runtime = entry.Runtime;
        entry.Runtime = null;
        _activationOrder.Remove(entry);
        _registry.Remove(entry.Id);
        var diagnostic = runtime is null ? null : await CleanupAsync(runtime.Plugin, runtime.LoadContext).ConfigureAwait(false);
        entry.State = diagnostic is null ? PluginState.Disabled : PluginState.Failed;
        entry.Diagnostic = diagnostic;
        return diagnostic;
    }

    private static async Task<string?> CleanupAsync(IMdaPlugin? plugin, PluginLoadContext? loadContext)
    {
        string? diagnostic = null;
        if (plugin is not null)
        {
            try { await plugin.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) { diagnostic = $"StopAsync failed: {Describe(exception)}"; }
            try { await plugin.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { diagnostic = Combine(diagnostic, $"DisposeAsync failed: {Describe(exception)}"); }
        }
        try { loadContext?.Unload(); }
        catch (Exception exception) { diagnostic = Combine(diagnostic, $"Unload request failed: {Describe(exception)}"); }
        return diagnostic;
    }

    private async Task<bool> EnterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void Publish() => Volatile.Write(ref _snapshot,
        Array.AsReadOnly(_entries.Select(e => new PluginInfo(
            e.Id,
            e.Name,
            e.Version,
            e.Directory,
            e.State,
            e.Diagnostic,
            e.Capabilities,
            e.Manifest?.Description,
            e.Manifest?.Author,
            e.Manifest?.ProvidedStyles,
            e.Manifest?.LocalizedNames,
            e.Manifest?.LocalizedDescriptions)).ToArray()));

    private static Entry InvalidEntry(string directory, string diagnostic) => new(directory, null)
    {
        State = PluginState.Incompatible,
        Diagnostic = diagnostic
    };

    private static void AddSharedAssembly(Assembly assembly, Dictionary<string, Assembly> shared)
    {
        if (AssemblyLoadContext.GetLoadContext(assembly) != AssemblyLoadContext.Default)
            throw new ArgumentException("Shared plugin contracts must belong to the default assembly load context.");
        var name = assembly.GetName().Name ?? throw new ArgumentException("A shared contract assembly must be named.");
        if (shared.TryGetValue(name, out var existing) && existing != assembly)
            throw new ArgumentException($"Shared contract assembly '{name}' is ambiguous.");
        shared[name] = assembly;
    }

    private static string Describe(Exception exception)
    {
        // Retain text only: exceptions and reflection metadata can root a collectible context.
        var actual = exception.GetBaseException();
        var description = $"{actual.GetType().Name}: {actual.Message}";
        return Limit(description);
    }

    private static string? Combine(string? first, string? second) => string.IsNullOrEmpty(first)
        ? second : string.IsNullOrEmpty(second) ? first : Limit(first + " " + second);
    private static string Limit(string value) => value.Length <= 2048 ? value : value[..2048];
    private static PluginOperationResult Success() => new(true);
    private static PluginOperationResult Failure(string? diagnostic) => new(false, diagnostic);

    private sealed record Runtime(IMdaPlugin Plugin, PluginLoadContext LoadContext);

    private sealed class Entry(string directory, PluginManifest? manifest)
    {
        internal string Directory { get; } = directory;
        internal PluginManifest? Manifest { get; } = manifest;
        internal IReadOnlyList<string>? Capabilities { get; } = manifest is null
            ? null
            : Array.AsReadOnly(manifest.Capabilities.ToArray());
        internal string Id => Manifest?.Id ?? "invalid:" + Directory;
        internal string Name => Manifest?.Name ?? Path.GetFileName(Directory);
        internal Version Version => Manifest?.Version ?? new Version(0, 0);
        internal PluginState State { get; set; } = PluginState.Disabled;
        internal string? Diagnostic { get; set; }
        internal Runtime? Runtime { get; set; }

        internal void Reject(string diagnostic)
        {
            State = PluginState.Incompatible;
            Diagnostic = diagnostic;
        }
    }
}
