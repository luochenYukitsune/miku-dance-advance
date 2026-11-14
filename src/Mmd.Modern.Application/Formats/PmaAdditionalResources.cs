using System.Security.Cryptography;

namespace Mmd.Modern.Application;

internal sealed record PmaAdditionalResource(string Path, long Length, string Sha256);

internal sealed class PmaResourceReader(string directory, PmaAdditionalResource[] resources) : IDisposable
{
    internal PmaAdditionalResource[] Resources { get; } = resources;
    public IReadOnlyList<string> ResourcePaths { get; } = Array.AsReadOnly(resources.Select(r => r.Path).ToArray());
    private volatile bool disposed;
    public byte[] ReadResource(string path, long maxBytes = long.MaxValue)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        PmaAdditionalResources.ValidatePath(path);
        var resource = Resources.FirstOrDefault(r => r.Path == path)
            ?? throw new FileNotFoundException("PMA additional resource is not present.", path);
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (resource.Length > maxBytes) throw new InvalidDataException("PMA resource exceeds the requested read budget.");
        return PmaAdditionalResources.Read(directory, resource);
    }
    public void Dispose() => disposed = true;
}

/// <summary>Opaque renderer-neutral resource transport; all interpretation stays with the consuming module.</summary>
internal static class PmaAdditionalResources
{
    internal const string Prefix = "render-resources/";

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 512 || !path.StartsWith(Prefix, StringComparison.Ordinal)
            || path.Contains('\\') || path.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.')
                || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')) || IsDeviceName(p)))
            throw new InvalidDataException("PMA additional resources require canonical safe ASCII paths under render-resources/.");
    }

    internal static bool IsDeviceName(string part)
    {
        string stem = part.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && stem[3] is >= '1' and <= '9';
    }

    internal static Dictionary<string, byte[]> Snapshot(PmaSaveOptions options, PmaRetainedState? retained, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(options.AdditionalResources);
        var supplied = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in options.AdditionalResources)
        {
            token.ThrowIfCancellationRequested(); ValidatePath(pair.Key);
            if (options.ContentSelection?.ExcludesResource(pair.Key) == true) continue;
            if (!supplied.TryAdd(pair.Key, pair.Value)) throw new InvalidDataException("Duplicate PMA additional resource paths.");
        }
        ValidateBudget(supplied.ToDictionary(pair => pair.Key, pair => (long)pair.Value.Length, StringComparer.OrdinalIgnoreCase),
            retained, options.ValidationOptions, options.ContentSelection);
        var snapshot = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in supplied)
        { token.ThrowIfCancellationRequested(); snapshot.Add(pair.Key, pair.Value.ToArray()); }
        return snapshot;
    }

    internal static void ValidateBudget(IReadOnlyDictionary<string, long> lengths, PmaRetainedState? retained, PmaLoadOptions limits,
        PmaContentSelection? selection = null)
    {
        limits.Validate();
        var supplied = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in lengths)
        {
            ValidatePath(pair.Key);
            if (selection?.ExcludesResource(pair.Key) == true) continue;
            if (!supplied.TryAdd(pair.Key, pair.Value)) throw new InvalidDataException("Duplicate PMA additional resource paths.");
        }
        long total = 0; int count = 0;
        foreach (var resource in retained?.Resources ?? [])
            if (selection?.ExcludesResource(resource.Path) != true && !supplied.ContainsKey(resource.Path)) AddBudget(resource.Length);
        foreach (long length in supplied.Values) AddBudget(length);
        void AddBudget(long length)
        {
            if (length < 0 || length > limits.MaxEntryBytes || total > limits.MaxExpandedBytes - length
                || ++count > limits.MaxFiles)
                throw new InvalidDataException("PMA additional resources exceed the configured file or expanded-byte budget.");
            total += length;
        }
    }

    internal static void Stage(string destination, PmaRetainedState? retained, Dictionary<string, byte[]> supplied,
        PmaContentSelection? selection, CancellationToken token)
    {
        foreach (var resource in retained?.Resources ?? [])
        {
            if (selection?.ExcludesResource(resource.Path) == true || supplied.ContainsKey(resource.Path)) continue;
            token.ThrowIfCancellationRequested();
            string target = Target(resource.Path);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyValidated(retained!.Directory, resource, output, token);
        }
        foreach (var pair in supplied)
        {
            if (selection?.ExcludesResource(pair.Key) == true) continue;
            token.ThrowIfCancellationRequested(); Write(pair.Key, pair.Value);
        }

        void Write(string path, byte[] data)
        {
            string target = Target(path);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var input = new MemoryStream(data, writable: false);
            byte[] buffer = new byte[64 * 1024];
            while (true)
            {
                token.ThrowIfCancellationRequested(); int count = input.Read(buffer);
                if (count == 0) break;
                output.Write(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested();
        }

        string Target(string path)
        {
            string target = System.IO.Path.Combine(destination, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            return target;
        }
    }

    private static void CopyValidated(string directory, PmaAdditionalResource resource, Stream output, CancellationToken token)
    {
        ValidatePath(resource.Path);
        string path = System.IO.Path.Combine(directory, resource.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != resource.Length) throw new InvalidDataException("Retained PMA resource length changed.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024]; long copied = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested(); int count = input.Read(buffer);
            if (count == 0) break;
            if (copied > resource.Length - count) throw new InvalidDataException("Retained PMA resource length changed.");
            copied += count; hash.AppendData(buffer, 0, count); output.Write(buffer, 0, count);
        }
        if (copied != resource.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != resource.Sha256)
            throw new InvalidDataException("Retained PMA resource does not match its validated SHA256.");
        token.ThrowIfCancellationRequested();
    }

    internal static byte[] Read(string directory, PmaAdditionalResource resource)
    {
        ValidatePath(resource.Path);
        if (resource.Length > Array.MaxLength) throw new InvalidDataException("PMA resource is too large for an in-memory read.");
        string path = System.IO.Path.Combine(directory, resource.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != resource.Length) throw new InvalidDataException("Retained PMA resource length changed.");
        byte[] bytes = new byte[(int)resource.Length]; input.ReadExactly(bytes);
        if (input.ReadByte() != -1 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != resource.Sha256)
            throw new InvalidDataException("Retained PMA resource does not match its validated SHA256.");
        return bytes;
    }
}
