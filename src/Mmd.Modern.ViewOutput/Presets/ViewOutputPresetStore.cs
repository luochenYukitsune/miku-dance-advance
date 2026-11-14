using System.Collections.ObjectModel;

namespace Mmd.Modern.ViewOutput;

/// <summary>An explicit local library of create-only view/output presets. Each save creates a new identity;
/// no existing preset is updated, including when the caller copies a previously saved definition.</summary>
public sealed class ViewOutputPresetStore
{
    public const int MaxPresetBytes = ViewOutputPresetJson.MaxBytes;
    public const int MaxLocalPresets = 256;
    private readonly object gate = new();
    public string DirectoryPath { get; }

    public ViewOutputPresetStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = Path.GetFullPath(directoryPath);
    }

    /// <summary>Reads a detached, bounded snapshot. A missing directory is an empty library; failures of
    /// individual files remain local to their entries. Reading never creates a directory or lock file.</summary>
    public ViewOutputPresetCatalog List(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(DirectoryPath) & FileAttributes.Directory) == 0)
                    throw new IOException("Preset library path is not a directory.");
                RejectRedirect(DirectoryPath);
                string[] paths = LocalPaths(cancellationToken);
                if (paths.Length > MaxLocalPresets)
                    return new([new(null, ViewOutputPresetStatus.CapacityExceeded, null,
                        "Preset library exceeds the 256-file budget; presets were not loaded.")]);
                return new(paths.Order(StringComparer.Ordinal).Select(path => ReadFile(path, cancellationToken)));
            }
            catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException)
            { return new([]); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return new([new(null, ViewOutputPresetStatus.IoFailure, null, "Preset library could not be read: " + error.Message)]); }
        }
    }

    public ViewOutputPresetResult Find(string stableId, CancellationToken cancellationToken = default)
    {
        ViewOutputPresetDefinition.ValidateIdentifier(stableId, nameof(stableId));
        return List(cancellationToken).Find(stableId);
    }

    public ViewOutputPresetDefinition SaveAsNew(ViewOutputPresetDefinition source, string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SchemaVersion != ViewOutputPresetDefinition.CurrentSchemaVersion)
            throw new NotSupportedException("Cannot save parameters from an unsupported preset schema.");
        return SaveAsNew(name, source.Preview, source.Export, source.Encoding, cancellationToken);
    }

    public ViewOutputPresetDefinition SaveAsNew(string name, ViewRenderRequest preview, ViewExportPreset export,
        ViewOutputEncodingPreset? encoding = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Validate and bound the complete payload before touching storage.
        var definition = new ViewOutputPresetDefinition("custom-" + Guid.NewGuid().ToString("N"), name, preview, export, encoding);
        byte[] bytes = ViewOutputPresetJson.Serialize(definition);
        if (bytes.Length is < 1 or > MaxPresetBytes) throw new InvalidDataException("Preset exceeds the supported byte budget.");
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(DirectoryPath);
            RejectRedirect(DirectoryPath);
            // An exclusive open fails immediately on contention; no unbounded retry or wait occurs.
            string lockPath = Path.Combine(DirectoryPath, ".write.lock");
            if (File.Exists(lockPath)) RejectRedirect(lockPath);
            using var writeLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (LocalPaths(cancellationToken).Length >= MaxLocalPresets)
                throw new InvalidOperationException("Preset library has reached its 256-file budget.");
            string destination = Path.Combine(DirectoryPath, definition.StableId + ".json");
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool temporaryCreated = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    temporaryCreated = true;
                    output.Write(bytes);
                    output.Flush(flushToDisk: true);
                }
                RejectRedirect(DirectoryPath);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: false);
                return definition;
            }
            finally
            {
                if (temporaryCreated && File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    // Ignore nested directories, locks and unpublished temporary files. Stop at one entry over budget.
    private string[] LocalPaths(CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            paths.Add(path);
            if (paths.Count > MaxLocalPresets) break;
        }
        return paths.ToArray();
    }

    private static ViewOutputPresetResult ReadFile(string path, CancellationToken cancellationToken)
    {
        string id = Path.GetFileNameWithoutExtension(path);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ViewOutputPresetDefinition.ValidateIdentifier(id, "file name");
            RejectRedirect(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is < 1 or > MaxPresetBytes) throw new InvalidDataException("Preset exceeds the supported byte budget.");
            // Read through EOF, allowing a single extra byte to detect a file that grew after Length.
            byte[] bytes = new byte[MaxPresetBytes + 1];
            int used = 0;
            while (used < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = input.Read(bytes.AsSpan(used));
                if (read == 0) break;
                used += read;
            }
            if (used > MaxPresetBytes) throw new InvalidDataException("Preset exceeds the supported byte budget.");
            var definition = ViewOutputPresetJson.Deserialize(bytes.AsSpan(0, used));
            if (!StringComparer.Ordinal.Equals(id, definition.StableId))
                throw new InvalidDataException("Preset identity does not match its file name.");
            return new(id, ViewOutputPresetStatus.Available, definition, "");
        }
        catch (NotSupportedException error)
        { return new(id, ViewOutputPresetStatus.UnsupportedSchema, null, "Unsupported preset: " + error.Message); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        { return new(id, ViewOutputPresetStatus.InvalidData, null, "Invalid preset file: " + error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(id, ViewOutputPresetStatus.IoFailure, null, "Preset file could not be read: " + error.Message); }
    }

    private static void RejectRedirect(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Preset library directories and files must not be symbolic links or reparse points.");
    }
}

public enum ViewOutputPresetStatus { Available, Missing, UnsupportedSchema, InvalidData, IoFailure, CapacityExceeded }

public sealed class ViewOutputPresetResult
{
    public string? StableId { get; }
    public ViewOutputPresetStatus Status { get; }
    public ViewOutputPresetDefinition? Definition { get; }
    public string Diagnostic { get; }
    public bool IsAvailable => Status == ViewOutputPresetStatus.Available;

    internal ViewOutputPresetResult(string? stableId, ViewOutputPresetStatus status,
        ViewOutputPresetDefinition? definition, string diagnostic)
    { StableId = stableId; Status = status; Definition = definition; Diagnostic = diagnostic; }
}

/// <summary>A read-only snapshot with explicit per-file and directory failures. Availability means the
/// definition is valid; renderer and encoding-provider capabilities are resolved by their installed adapters.</summary>
public sealed class ViewOutputPresetCatalog
{
    private readonly ReadOnlyCollection<ViewOutputPresetResult> entries;
    private readonly IReadOnlyDictionary<string, ViewOutputPresetResult> byId;
    private readonly ViewOutputPresetResult? loadFailure;

    internal ViewOutputPresetCatalog(IEnumerable<ViewOutputPresetResult> results)
    {
        entries = Array.AsReadOnly(results.ToArray());
        loadFailure = entries.FirstOrDefault(entry => entry.StableId is null
            && entry.Status is ViewOutputPresetStatus.IoFailure or ViewOutputPresetStatus.CapacityExceeded);
        var index = new Dictionary<string, ViewOutputPresetResult>(StringComparer.Ordinal);
        foreach (var entry in entries)
            if (entry.StableId is { } id) index.TryAdd(id, entry);
        byId = new ReadOnlyDictionary<string, ViewOutputPresetResult>(index);
    }

    public IReadOnlyList<ViewOutputPresetResult> List() => entries;

    public ViewOutputPresetResult Find(string stableId)
    {
        ViewOutputPresetDefinition.ValidateIdentifier(stableId, nameof(stableId));
        if (byId.TryGetValue(stableId, out var result)) return result;
        return loadFailure is { } failure ? new(stableId, failure.Status, null, failure.Diagnostic)
            : new(stableId, ViewOutputPresetStatus.Missing, null, $"Preset '{stableId}' is not installed.");
    }
}
