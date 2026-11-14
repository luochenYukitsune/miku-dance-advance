using System.Text.Json;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Local renderer library of create-only JSON definitions. Project files retain only stable references.
/// Every save gets a new identity, including edits to an existing custom preset. No update or overwrite API exists.</summary>
public sealed class D3D12StylePresetStore
{
    public const int MaxPresetBytes = 16 * 1024;
    public const int MaxLocalPresets = 256;
    private readonly object gate = new();
    public string DirectoryPath { get; }

    public D3D12StylePresetStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = Path.GetFullPath(directoryPath);
    }

    /// <summary>Reads a detached catalog, preserving per-file failures as explicit results. A missing directory is an empty local library.</summary>
    public D3D12StylePresetCatalog Load()
    {
        lock (gate)
        {
            try
            {
                if ((File.GetAttributes(DirectoryPath) & FileAttributes.Directory) == 0)
                    throw new IOException("Preset library path is not a directory.");
                RejectRedirect(DirectoryPath);
                string[] paths = LocalPaths();
                if (paths.Length > MaxLocalPresets)
                    return new([new(null, D3D12StylePresetStatus.CapacityExceeded, null, "Preset library exceeds the 256-file budget; local presets were not loaded.")]);
                return new(paths.Order(StringComparer.Ordinal).Select(ReadFile).ToArray());
            }
            catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException)
            { return D3D12StylePresetCatalog.BuiltIn; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return new([new(null, D3D12StylePresetStatus.IoFailure, null, "Preset library could not be read: " + error.Message)]); }
        }
    }

    public D3D12StylePresetDefinition SaveAsNew(D3D12StylePresetDefinition source, string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SchemaVersion != D3D12StylePresetDefinition.CurrentSchemaVersion)
            throw new NotSupportedException("Cannot save parameters from an unsupported preset schema.");
        return SaveAsNew(name, source.PipelineId, source.CharacterOutlineStyle, source.CharacterToonResponse);
    }

    /// <summary>Saves the caller's current parameter values as a new preset. Null retains source material behavior.</summary>
    public D3D12StylePresetDefinition SaveAsNew(string name, string pipelineId,
        CharacterOutlineStyle? characterOutlineStyle, CharacterToonResponse? characterToonResponse)
    {
        // Validate and serialize before creating directories or any temporary file.
        var definition = new D3D12StylePresetDefinition("custom-" + Guid.NewGuid().ToString("N"), name, pipelineId,
            characterOutlineStyle, characterToonResponse);
        byte[] bytes = D3D12StylePresetJson.Write(definition);
        lock (gate)
        {
            Directory.CreateDirectory(DirectoryPath); RejectRedirect(DirectoryPath);
            // Serialize writers in other store instances/processes as well. Contention is an explicit I/O failure.
            string lockPath = Path.Combine(DirectoryPath, ".write.lock");
            if (File.Exists(lockPath)) RejectRedirect(lockPath);
            using var writeLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (LocalPaths().Length >= MaxLocalPresets) throw new InvalidOperationException("Preset library has reached its 256-file budget.");
            string destination = Path.Combine(DirectoryPath, definition.StableId + ".json");
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool temporaryCreated = false;
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { temporaryCreated = true; output.Write(bytes); output.Flush(flushToDisk: true); }
                RejectRedirect(DirectoryPath);
                File.Move(temporary, destination, overwrite: false);
                return definition;
            }
            finally { if (temporaryCreated && File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    // Stop after one entry over budget rather than materializing an unbounded directory.
    private string[] LocalPaths() => Directory.EnumerateFiles(DirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
        .Take(MaxLocalPresets + 1).ToArray();

    private static D3D12StylePresetResult ReadFile(string path)
    {
        string id = Path.GetFileNameWithoutExtension(path);
        try
        {
            D3D12StylePresetDefinition.ValidateIdentifier(id, "file name");
            if (D3D12StylePresetCatalog.IsBuiltIn(id)) throw new InvalidDataException("Built-in preset identities are reserved and cannot be replaced by local files.");
            RejectRedirect(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is < 1 or > MaxPresetBytes) throw new InvalidDataException("Preset exceeds the supported byte budget.");
            byte[] bytes = new byte[MaxPresetBytes + 1];
            int used = 0;
            while (used < bytes.Length)
            {
                int read = input.Read(bytes.AsSpan(used));
                if (read == 0) break;
                used += read;
            }
            return D3D12StylePresetJson.Read(bytes.AsMemory(0, used), id);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        { return new(id, D3D12StylePresetStatus.InvalidData, null, "Invalid preset file: " + error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(id, D3D12StylePresetStatus.IoFailure, null, "Preset file could not be read: " + error.Message); }
    }

    private static void RejectRedirect(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Preset library directories and files must not be symbolic links or reparse points.");
    }
}
