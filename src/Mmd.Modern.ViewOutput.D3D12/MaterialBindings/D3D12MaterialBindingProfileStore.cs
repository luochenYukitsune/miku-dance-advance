using System.Collections.ObjectModel;

namespace Mmd.Modern.ViewOutput.D3D12;

public enum D3D12MaterialBindingProfileStatus { Available, Missing, IncompatibleSchema, InvalidData, IoFailure, CapacityExceeded }

public sealed record D3D12MaterialBindingProfileResult(string? StableId, D3D12MaterialBindingProfileStatus Status,
    D3D12MaterialBindingProfile? Profile, string Diagnostic)
{
    public bool IsAvailable => Status == D3D12MaterialBindingProfileStatus.Available;
}

/// <summary>A detached local library snapshot, with explicit per-file failures and no implicit replacement.</summary>
public sealed class D3D12MaterialBindingProfileCatalog
{
    private readonly ReadOnlyCollection<D3D12MaterialBindingProfileResult> entries;
    internal D3D12MaterialBindingProfileCatalog(IEnumerable<D3D12MaterialBindingProfileResult> values)
        => entries = Array.AsReadOnly(values.ToArray());
    public IReadOnlyList<D3D12MaterialBindingProfileResult> List() => entries;
    public D3D12MaterialBindingProfileResult Find(string stableId)
    {
        D3D12MaterialBindingProfile.ValidateIdentifier(stableId);
        return entries.FirstOrDefault(value => string.Equals(value.StableId, stableId, StringComparison.Ordinal))
            ?? entries.FirstOrDefault(value => value.StableId is null)
            ?? new(stableId, D3D12MaterialBindingProfileStatus.Missing, null, "Material binding profile is not installed.");
    }
}

/// <summary>Optional create-only local library for renderer-owned mappings. Portable project payloads
/// use the same independent codec; this library is not the only persistence destination. Every save
/// creates a new stable identity and never overwrites an existing mapping.</summary>
public sealed class D3D12MaterialBindingProfileStore
{
    public const int MaxLocalProfiles = 256;
    private readonly object gate = new();
    public string DirectoryPath { get; }
    public D3D12MaterialBindingProfileStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = Path.GetFullPath(directoryPath);
    }

    public D3D12MaterialBindingProfileCatalog Load()
    {
        lock (gate)
        {
            try
            {
                if ((File.GetAttributes(DirectoryPath) & FileAttributes.Directory) == 0)
                    throw new IOException("Material profile library path is not a directory.");
                RejectRedirect(DirectoryPath);
                string[] paths = LocalPaths();
                if (paths.Length > MaxLocalProfiles)
                    return new([new(null, D3D12MaterialBindingProfileStatus.CapacityExceeded, null,
                        "Material profile library exceeds its 256-file budget; no partial library was loaded.")]);
                return new(paths.Order(StringComparer.Ordinal).Select(ReadFile));
            }
            catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException)
            { return new([]); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return new([new(null, D3D12MaterialBindingProfileStatus.IoFailure, null, "Material profile library could not be read: " + error.Message)]); }
        }
    }

    public D3D12MaterialBindingProfile SaveAsNew(D3D12MaterialBindingProfile source, string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        var profile = new D3D12MaterialBindingProfile("custom-" + Guid.NewGuid().ToString("N"), name,
            source.LayoutFingerprint, source.Materials, source.LayoutVersion, source.OutlineWidths);
        // Validate and encode before touching the filesystem.
        byte[] bytes = D3D12MaterialBindingProfileJson.Encode(profile);
        lock (gate)
        {
            Directory.CreateDirectory(DirectoryPath); RejectRedirect(DirectoryPath);
            string lockPath = Path.Combine(DirectoryPath, ".write.lock");
            if (File.Exists(lockPath)) RejectRedirect(lockPath);
            using var writeLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (LocalPaths().Length >= MaxLocalProfiles)
                throw new InvalidOperationException("Material profile library has reached its 256-file budget.");
            string destination = Path.Combine(DirectoryPath, profile.StableId + ".json");
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool temporaryCreated = false;
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { temporaryCreated = true; output.Write(bytes); output.Flush(flushToDisk: true); }
                RejectRedirect(DirectoryPath);
                File.Move(temporary, destination, overwrite: false);
                return profile;
            }
            finally { if (temporaryCreated && File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private string[] LocalPaths() => Directory.EnumerateFiles(DirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
        .Take(MaxLocalProfiles + 1).ToArray();

    private static D3D12MaterialBindingProfileResult ReadFile(string path)
    {
        string id = Path.GetFileNameWithoutExtension(path);
        try
        {
            D3D12MaterialBindingProfile.ValidateIdentifier(id); RejectRedirect(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is < 1 or > D3D12MaterialBindingProfileJson.MaxProfileBytes)
                throw new InvalidDataException("Material profile exceeds the supported byte budget.");
            byte[] bytes = new byte[D3D12MaterialBindingProfileJson.MaxProfileBytes + 1];
            int used = 0;
            while (used < bytes.Length)
            {
                int read = input.Read(bytes.AsSpan(used));
                if (read == 0) break;
                used += read;
            }
            var profile = D3D12MaterialBindingProfileJson.Decode(bytes.AsMemory(0, used), id);
            return new(id, D3D12MaterialBindingProfileStatus.Available, profile, "");
        }
        catch (NotSupportedException error)
        { return new(id, D3D12MaterialBindingProfileStatus.IncompatibleSchema, null, error.Message); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException)
        { return new(id, D3D12MaterialBindingProfileStatus.InvalidData, null, "Invalid material profile: " + error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(id, D3D12MaterialBindingProfileStatus.IoFailure, null, "Material profile could not be read: " + error.Message); }
    }

    private static void RejectRedirect(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Material profile library directories and files must not be symbolic links or reparse points.");
    }
}
