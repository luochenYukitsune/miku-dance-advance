using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Application;
using Mmd.Modern.Plugins;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;

namespace Mmd.Modern.App;

/// <summary>Application composition and local enablement preferences. The loader and its contracts
/// remain independent of the editor, scene clock, physics and rendering engines.</summary>
internal sealed class EditorPluginService : IAsyncDisposable
{
    internal const string CommandsCapability = PluginCapabilities.EditorCommands;
    internal const string StylesCapability = PluginCapabilities.RenderingStyles;
    internal static IReadOnlyList<string> BundledDefaultPluginIds { get; } =
    [
        "mda.style.genshin",
        "mda.style.star-rail",
        "mda.style.wuthering-waves",
        "mda.style.endfield"
    ];

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object notifySync = new();
    private readonly object catalogLock = new();
    private readonly string preferencesPath;
    private readonly string[] roots;
    private readonly HashSet<string> defaultEnabledIds;
    private readonly HashSet<string> disabledDefaults = new(StringComparer.Ordinal);
    private readonly string hostLanguage;
    private readonly bool tracksAppLanguage;
    private int gateDepth;
    private bool pendingPluginsChanged;
    private bool preferencesBlocked;
    private bool initialized, disposed;
    private D3D12ViewStyleCatalog cachedStyleCatalog = D3D12ViewStyleCatalog.Empty;
    private D3D12StylePresetCatalog cachedPresetCatalog = D3D12StylePresetCatalog.BuiltIn;
    private string? cachedCatalogLanguage;

    internal PluginManager Manager { get; }
    internal string InstallationDirectory { get; }
    internal string? Diagnostic { get; private set; }
    internal IReadOnlyList<PluginExtension<IPluginCommand>> Commands => Manager.Extensions.GetExtensions<IPluginCommand>(CommandsCapability);
    internal IReadOnlyList<PluginExtension<IRenderingStyleProvider>> Styles => Manager.Extensions.GetExtensions<IRenderingStyleProvider>(StylesCapability);

    public event EventHandler? PluginsChanged;
    public string CurrentLanguage => tracksAppLanguage ? EditorLocalization.CurrentLanguage : hostLanguage;

    public D3D12ViewStyleCatalog CurrentStyleCatalog
    {
        get
        {
            string language = CurrentLanguage;
            lock (catalogLock)
            {
                if (string.Equals(cachedCatalogLanguage, language, StringComparison.Ordinal))
                    return cachedStyleCatalog;
            }
            return BuildActiveStyleCatalog(language);
        }
    }

    public D3D12StylePresetCatalog CurrentPresetCatalog
    {
        get
        {
            string language = CurrentLanguage;
            lock (catalogLock)
            {
                if (string.Equals(cachedCatalogLanguage, language, StringComparison.Ordinal))
                    return cachedPresetCatalog;
            }
            BuildActiveStyleCatalog(language);
            lock (catalogLock)
                return cachedPresetCatalog;
        }
    }

    internal EditorPluginService(string installationDirectory, string preferencesPath, PluginHostInfo host,
        IEnumerable<string>? additionalRoots = null, IEnumerable<string>? defaultEnabledPluginIds = null,
        bool tracksAppLanguage = false)
    {
        ArgumentNullException.ThrowIfNull(host);
        InstallationDirectory = Path.GetFullPath(installationDirectory);
        this.preferencesPath = Path.GetFullPath(preferencesPath);
        roots = new[] { InstallationDirectory }.Concat(additionalRoots ?? []).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        this.defaultEnabledIds = new(defaultEnabledPluginIds ?? [], StringComparer.Ordinal);
        hostLanguage = string.IsNullOrWhiteSpace(host.Language) ? "zh-CN" : host.Language;
        this.tracksAppLanguage = tracksAppLanguage;
        cachedCatalogLanguage = CurrentLanguage;
        Manager = new(host, [
            new(CommandsCapability, typeof(IPluginCommand)),
            new(StylesCapability, typeof(IRenderingStyleProvider))
        ]);
        Manager.PluginsChanged += OnManagerPluginsChanged;
    }

    internal static EditorPluginService CreateDefault()
    {
        string directory = EditorUserDataPaths.Current.PluginDirectory;
        Version version = Version.TryParse(EditorAboutInfo.Current.Version.Split('-', 2)[0], out var parsed) ? parsed : new(0, 1, 0);
        return new(directory, Path.Combine(directory, "enabled.json"), new(version, 1, EditorLocalization.CurrentLanguage),
            [Path.Combine(AppContext.BaseDirectory, "plugins")], BundledDefaultPluginIds, tracksAppLanguage: true);
    }

    public bool IsBundledDefaultPlugin(string pluginId)
        => !string.IsNullOrWhiteSpace(pluginId) && defaultEnabledIds.Contains(pluginId);

    public D3D12ViewStyleCatalog BuildActiveStyleCatalog(string? language = null)
    {
        string effectiveLanguage = string.IsNullOrWhiteSpace(language) ? CurrentLanguage : language;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var definitions = new List<IRenderingStyleDefinition>();
        foreach (var provider in Styles)
        {
            IEnumerable<IRenderingStyleDefinition>? provided;
            try { provided = provider.Value.GetStyles(); }
            catch (Exception error)
            {
                Diagnostic ??= error.Message;
                continue;
            }
            if (provided is null) continue;
            foreach (var style in provided)
            {
                if (style is null) continue;
                try
                {
                    D3D12StylePluginAdapter.ToRegistration(style, effectiveLanguage);
                    if (seen.Add(style.StyleId))
                        definitions.Add(style);
                }
                catch (Exception error)
                {
                    Diagnostic ??= error.Message;
                }
            }
        }

        var catalog = new D3D12ViewStyleCatalog(definitions, effectiveLanguage);
        if (language is null || string.Equals(effectiveLanguage, CurrentLanguage, StringComparison.Ordinal))
        {
            var presetCatalog = catalog.ToD3D12PresetCatalog();
            lock (catalogLock)
            {
                cachedStyleCatalog = catalog;
                cachedPresetCatalog = presetCatalog;
                cachedCatalogLanguage = effectiveLanguage;
            }
        }
        return catalog;
    }

    public PluginInfo? FindPluginForStyle(string styleId)
    {
        if (string.IsNullOrWhiteSpace(styleId) || string.Equals(styleId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
            return null;

        var byId = Manager.Plugins.ToDictionary(plugin => plugin.Id, StringComparer.Ordinal);
        foreach (var provider in Styles)
        {
            IEnumerable<IRenderingStyleDefinition>? provided;
            try { provided = provider.Value.GetStyles(); }
            catch { continue; }
            if (provided is null) continue;
            if (provided.Any(style => style is not null && string.Equals(style.StyleId, styleId, StringComparison.Ordinal))
                && byId.TryGetValue(provider.PluginId, out var activeMatch))
            {
                return activeMatch;
            }
        }

        return Manager.Plugins
            .Where(plugin => plugin.ProvidedStyles?.Any(provided => string.Equals(provided, styleId, StringComparison.Ordinal)) == true)
            .OrderBy(plugin => plugin.State switch
            {
                PluginState.Active => 0,
                PluginState.Disabled => 1,
                PluginState.Failed => 2,
                PluginState.Incompatible => 3,
                _ => 4
            })
            .ThenBy(plugin => plugin.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public Task<PluginOperationResult> EnablePluginAsync(string id, CancellationToken cancellationToken = default)
        => SetEnabledAsync(id, true, cancellationToken);

    public Task<PluginOperationResult> DisablePluginAsync(string id, CancellationToken cancellationToken = default)
        => SetEnabledAsync(id, false, cancellationToken);

    public async Task<PluginOperationResult> EnablePluginForStyleAsync(string styleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(styleId) || string.Equals(styleId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
            return new(true);

        var plugin = FindPluginForStyle(styleId);
        if (plugin is null)
            return new(false, EditorLocalization.Format("Mda.Text.Plugins.StyleProviderNotFound", styleId));

        if (plugin.State == PluginState.Active)
        {
            return BuildActiveStyleCatalog().FindStyle(styleId) is not null
                ? new(true)
                : new(false, plugin.Diagnostic ?? EditorLocalization.Format("Mda.Text.Plugins.StyleNotActivated", plugin.GetDisplayName(CurrentLanguage), styleId));
        }

        if (plugin.State == PluginState.Incompatible)
            return new(false, plugin.Diagnostic);

        return await EnablePluginAsync(plugin.Id, cancellationToken);
    }

    internal async Task InitializeAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        EnterGateScope();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized) return;
            var discovery = await Task.Run(() => Manager.RefreshAsync(roots, token), token);
            if (!discovery.Succeeded) Diagnostic = discovery.Diagnostic;
            Enablement preferences;
            try { preferences = ReadPreferences(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                preferencesBlocked = true;
                Diagnostic = error.Message;
                initialized = true;
                return;
            }

            disabledDefaults.Clear();
            if (preferences.DisabledDefaults is not null)
            {
                foreach (string id in preferences.DisabledDefaults)
                    disabledDefaults.Add(id);
            }

            var enabledSet = new HashSet<string>(preferences.Enabled, StringComparer.Ordinal);
            foreach (string id in preferences.Enabled)
            {
                token.ThrowIfCancellationRequested();
                var result = await Task.Run(() => Manager.EnableAsync(id, token), token);
                if (!result.Succeeded) Diagnostic = result.Diagnostic;
            }

            if (defaultEnabledIds.Count > 0)
            {
                foreach (var plugin in Manager.Plugins)
                {
                    if (plugin.State != PluginState.Disabled
                        || enabledSet.Contains(plugin.Id)
                        || !defaultEnabledIds.Contains(plugin.Id)
                        || disabledDefaults.Contains(plugin.Id))
                        continue;

                    token.ThrowIfCancellationRequested();
                    var result = await Task.Run(() => Manager.EnableAsync(plugin.Id, token), token);
                    if (!result.Succeeded) Diagnostic = result.Diagnostic;
                }
            }

            BuildActiveStyleCatalog();
            initialized = true;
        }
        finally
        {
            gate.Release();
            ExitGateScopeAndDispatch();
        }
    }

    internal async Task<PluginOperationResult> RefreshAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        EnterGateScope();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var discovery = await Task.Run(() => Manager.RefreshAsync(roots, preserveActive: true, token), token);
            if (!preferencesBlocked && defaultEnabledIds.Count > 0)
            {
                foreach (var plugin in Manager.Plugins)
                {
                    if (plugin.State != PluginState.Disabled
                        || !defaultEnabledIds.Contains(plugin.Id)
                        || disabledDefaults.Contains(plugin.Id))
                        continue;

                    token.ThrowIfCancellationRequested();
                    var enabled = await Task.Run(() => Manager.EnableAsync(plugin.Id, token), token);
                    if (!enabled.Succeeded) Diagnostic = enabled.Diagnostic;
                }
            }
            BuildActiveStyleCatalog();
            return discovery;
        }
        finally
        {
            gate.Release();
            ExitGateScopeAndDispatch();
        }
    }

    internal async Task<PluginOperationResult> ImportAsync(string archivePath, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        EnterGateScope();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Include all roots: a bundled or manually installed plugin must not acquire
            // a second copy with the same identity through the import button.
            var reservedIds = Manager.Plugins.Select(plugin => plugin.Id).ToArray();
            var installed = await Task.Run(() => PluginPackageInstaller.InstallAsync(
                archivePath, InstallationDirectory, reservedIds, token), token);
            if (!installed.Succeeded) return new(false, installed.Diagnostic);
            var discovery = await Task.Run(() => Manager.RefreshAsync(roots, preserveActive: true, token), token);
            if (!discovery.Succeeded) return discovery;
            var plugin = Manager.Plugins.SingleOrDefault(value => value.Id == installed.PluginId);
            if (plugin?.State == PluginState.Incompatible)
                return new(false, plugin.Diagnostic);
            BuildActiveStyleCatalog();
            return new(true, EditorLocalization.Text("Mda.Text.Plugins.Imported"));
        }
        finally
        {
            gate.Release();
            ExitGateScopeAndDispatch();
        }
    }

    internal async Task<PluginOperationResult> SetEnabledAsync(string id, bool enabled, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        EnterGateScope();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = await Task.Run(() => enabled ? Manager.EnableAsync(id, token) : Manager.DisableAsync(id, token), token);
            if (IsBundledDefaultPlugin(id))
            {
                if (enabled)
                    disabledDefaults.Remove(id);
                else if (!Manager.Plugins.Any(plugin => plugin.Id == id && plugin.State == PluginState.Active))
                    disabledDefaults.Add(id);
            }
            BuildActiveStyleCatalog();
            // Even a failed Stop can withdraw registrations. Persist the actual remaining
            // active set, so a failed plugin is not silently reactivated at next startup.
            try { SaveEnabled(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Diagnostic = error.Message; return new(false, error.Message); }
            return result;
        }
        finally
        {
            gate.Release();
            ExitGateScopeAndDispatch();
        }
    }

    internal async Task<PluginCommandResult> RunCommandAsync(string pluginId, string registrationId, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        EnterGateScope();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var command = Commands.SingleOrDefault(value => value.PluginId == pluginId && value.RegistrationId == registrationId)
                ?? throw new InvalidOperationException(EditorLocalization.Text("Mda.Text.Plugins.CommandUnavailable"));
            return await Task.Run(async () => await command.Value.ExecuteAsync(token), token);
        }
        finally
        {
            gate.Release();
            ExitGateScopeAndDispatch();
        }
    }

    private void OnManagerPluginsChanged(object? sender, EventArgs e)
    {
        BuildActiveStyleCatalog();
        lock (notifySync)
        {
            if (gateDepth > 0)
            {
                pendingPluginsChanged = true;
                return;
            }
        }
        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnterGateScope()
    {
        lock (notifySync) gateDepth++;
    }

    private void ExitGateScopeAndDispatch()
    {
        bool raise;
        lock (notifySync)
        {
            gateDepth--;
            raise = gateDepth == 0 && pendingPluginsChanged;
            if (raise) pendingPluginsChanged = false;
        }
        if (raise) PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    private Enablement ReadPreferences()
    {
        if (!File.Exists(preferencesPath)) return new(1, [], null);
        using var input = File.OpenRead(preferencesPath);
        if (input.Length > 64 * 1024) throw new InvalidDataException("Plugin preferences exceed the supported size.");
        var data = JsonSerializer.Deserialize<Enablement>(input, JsonOptions) ?? throw new InvalidDataException("Plugin preferences are empty.");
        if (data.SchemaVersion != 1 || data.Enabled is null || data.Enabled.Length > 1024
            || data.Enabled.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128)
            || data.Enabled.Distinct(StringComparer.Ordinal).Count() != data.Enabled.Length)
            throw new InvalidDataException("Plugin enablement preferences are invalid.");
        if (data.DisabledDefaults is not null && (data.DisabledDefaults.Length > 1024
            || data.DisabledDefaults.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128)
            || data.DisabledDefaults.Distinct(StringComparer.Ordinal).Count() != data.DisabledDefaults.Length))
            throw new InvalidDataException("Plugin enablement preferences are invalid.");
        return data;
    }

    private void SaveEnabled()
    {
        string[]? disabled = disabledDefaults.Count == 0
            ? null
            : disabledDefaults.Order(StringComparer.Ordinal).ToArray();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Enablement(1,
            Manager.Plugins.Where(plugin => plugin.State == PluginState.Active).Select(plugin => plugin.Id).Order(StringComparer.Ordinal).ToArray(),
            disabled), JsonOptions);
        string directory = Path.GetDirectoryName(preferencesPath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".enabled-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            File.Move(temporary, preferencesPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record Enablement(
        int SchemaVersion,
        string[] Enabled,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? DisabledDefaults = null);

    private static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true };

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        EnterGateScope();
        try
        {
            if (disposed) return;
            disposed = true;
            Manager.PluginsChanged -= OnManagerPluginsChanged;
            await Task.Run(async () => await Manager.DisposeAsync());
        }
        finally
        {
            gate.Release();
            ExitGateScopeAndDispatch();
        }
    }
}
