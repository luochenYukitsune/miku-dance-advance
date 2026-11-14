using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Plugins;
using Mmd.Modern.Plugins.Contracts;

namespace Mmd.Modern.App;

/// <summary>Application composition and local enablement preferences. The loader and its contracts
/// remain independent of the editor, scene clock, physics and rendering engines.</summary>
internal sealed class EditorPluginService : IAsyncDisposable
{
    internal const string CommandsCapability = "editor.commands";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string preferencesPath;
    private readonly string[] roots;
    private bool initialized, disposed;
    internal PluginManager Manager { get; }
    internal string InstallationDirectory { get; }
    internal string? Diagnostic { get; private set; }
    internal IReadOnlyList<PluginExtension<IPluginCommand>> Commands => Manager.Extensions.GetExtensions<IPluginCommand>(CommandsCapability);

    internal EditorPluginService(string installationDirectory, string preferencesPath, PluginHostInfo host,
        IEnumerable<string>? additionalRoots = null)
    {
        InstallationDirectory = Path.GetFullPath(installationDirectory);
        this.preferencesPath = Path.GetFullPath(preferencesPath);
        roots = new[] { InstallationDirectory }.Concat(additionalRoots ?? []).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Manager = new(host, [new(CommandsCapability, typeof(IPluginCommand))]);
    }

    internal static EditorPluginService CreateDefault()
    {
        string directory = EditorUserDataPaths.Current.PluginDirectory;
        Version version = Version.TryParse(EditorAboutInfo.Current.Version.Split('-', 2)[0], out var parsed) ? parsed : new(0, 1, 0);
        return new(directory, Path.Combine(directory, "enabled.json"), new(version, 1, EditorLocalization.CurrentLanguage),
            [Path.Combine(AppContext.BaseDirectory, "plugins")]);
    }

    internal async Task InitializeAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized) return;
            var discovery = await Task.Run(() => Manager.RefreshAsync(roots, token), token);
            if (!discovery.Succeeded) Diagnostic = discovery.Diagnostic;
            string[] enabled;
            try { enabled = ReadEnabled(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { Diagnostic = error.Message; initialized = true; return; }
            foreach (string id in enabled)
            {
                token.ThrowIfCancellationRequested();
                var result = await Task.Run(() => Manager.EnableAsync(id, token), token);
                if (!result.Succeeded) Diagnostic = result.Diagnostic;
            }
            initialized = true;
        }
        finally { gate.Release(); }
    }

    internal async Task<PluginOperationResult> RefreshAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try { ObjectDisposedException.ThrowIf(disposed, this); return await Task.Run(() => Manager.RefreshAsync(roots, preserveActive: true, token), token); }
        finally { gate.Release(); }
    }

    internal async Task<PluginOperationResult> ImportAsync(string archivePath, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
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
            return new(true, EditorLocalization.Text("Mda.Text.Plugins.Imported"));
        }
        finally { gate.Release(); }
    }

    internal async Task<PluginOperationResult> SetEnabledAsync(string id, bool enabled, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = await Task.Run(() => enabled ? Manager.EnableAsync(id, token) : Manager.DisableAsync(id, token), token);
            // Even a failed Stop can withdraw registrations. Persist the actual remaining
            // active set, so a failed plugin is not silently reactivated at next startup.
            try { SaveEnabled(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Diagnostic = error.Message; return new(false, error.Message); }
            return result;
        }
        finally { gate.Release(); }
    }

    internal async Task<PluginCommandResult> RunCommandAsync(string pluginId, string registrationId, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var command = Commands.SingleOrDefault(value => value.PluginId == pluginId && value.RegistrationId == registrationId)
                ?? throw new InvalidOperationException(EditorLocalization.Text("Mda.Text.Plugins.CommandUnavailable"));
            return await Task.Run(async () => await command.Value.ExecuteAsync(token), token);
        }
        finally { gate.Release(); }
    }

    private string[] ReadEnabled()
    {
        if (!File.Exists(preferencesPath)) return [];
        using var input = File.OpenRead(preferencesPath);
        if (input.Length > 64 * 1024) throw new InvalidDataException("Plugin preferences exceed the supported size.");
        var data = JsonSerializer.Deserialize<Enablement>(input, JsonOptions) ?? throw new InvalidDataException("Plugin preferences are empty.");
        if (data.SchemaVersion != 1 || data.Enabled is null || data.Enabled.Length > 1024
            || data.Enabled.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128)
            || data.Enabled.Distinct(StringComparer.Ordinal).Count() != data.Enabled.Length)
            throw new InvalidDataException("Plugin enablement preferences are invalid.");
        return data.Enabled;
    }

    private void SaveEnabled()
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Enablement(1,
            Manager.Plugins.Where(plugin => plugin.State == PluginState.Active).Select(plugin => plugin.Id).Order(StringComparer.Ordinal).ToArray()), JsonOptions);
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

    private sealed record Enablement(int SchemaVersion, string[] Enabled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true };

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            await Task.Run(async () => await Manager.DisposeAsync());
        }
        finally { gate.Release(); }
    }
}
