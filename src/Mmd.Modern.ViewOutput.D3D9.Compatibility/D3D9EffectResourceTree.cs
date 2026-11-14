using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using Mmd.Modern.Application;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>Independent bounds on one explicitly selected common directory tree.</summary>
public sealed record D3D9EffectResourceTreeLimits
{
    public int MaxFiles { get; init; } = 65_536;
    public int MaxDirectories { get; init; } = 65_536;
    public int MaxDepth { get; init; } = 128;
    public long MaxFileBytes { get; init; } = 256L * 1024 * 1024;
    public long MaxTotalBytes { get; init; } = 512L * 1024 * 1024;
    /// <summary>Total strict UTF-8 bytes of relative file and directory names.</summary>
    public long MaxPathBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxMetadataBytes { get; init; } = 16 * 1024 * 1024;

    internal void Validate()
    {
        if (MaxFiles is < 1 or > 65_536 || MaxDirectories is < 0 or > 65_536 || MaxDepth is < 1 or > 256
            || MaxFileBytes < 1 || MaxFileBytes > Array.MaxLength || MaxTotalBytes < 1 || MaxPathBytes < 1
            || MaxMetadataBytes is < 1 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxFiles), "Invalid bounded effect-tree limits.");
    }
}

public sealed record D3D9EffectResourceTreeFile(string RelativePath, long ByteLength, string Sha256);
internal sealed record D3D9EffectResourceContent(D3D9EffectResourceTreeFile Description, byte[] Bytes);

/// <summary>
/// Detached raw bytes from an explicit common tree (schemas one/two), or a completed compiler dependency
/// graph with explicit per-file origins (schema three). Capturing a common tree does not prove closure of
/// absolute, macro-generated or external dependencies. Original paths are identity data only;
/// restoration never accesses them. Filesystem capture/materialization requires Windows.
/// </summary>
public sealed partial class D3D9EffectResourceTree
{
    public const string ExtensionName = "org.mda.traditional-effect-tree";
    private const string ResourceRoot = "render-resources/traditional/effect-tree/";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };
    private readonly D3D9EffectResourceContent[] contents;
    private readonly Dictionary<string, D3D9EffectResourceContent> byPath;
    public IReadOnlyList<D3D9EffectResourceTreeFile> Files { get; }
    public IReadOnlyList<string> Directories { get; }
    public IReadOnlyList<string> RequiredRelativeFiles { get; }
    public long TotalBytes { get; }
    /// <summary>Original canonical common root, used only to identify packaged resource references.
    /// Null for legacy schema one and schema three's per-file origins. Schema one cannot supply an
    /// absolute-origin resource map; schema three uses DependencyGraph.Nodes instead.</summary>
    public string? OriginAbsoluteRoot { get; }

    private D3D9EffectResourceTree(D3D9EffectResourceContent[] contents, string[] directories, string[] required,
        string? originAbsoluteRoot)
    {
        this.contents = contents;
        byPath = contents.ToDictionary(value => value.Description.RelativePath, StringComparer.Ordinal);
        Files = Array.AsReadOnly(contents.Select(value => value.Description).ToArray());
        Directories = Array.AsReadOnly(directories);
        RequiredRelativeFiles = Array.AsReadOnly(required);
        TotalBytes = contents.Sum(value => value.Description.ByteLength);
        OriginAbsoluteRoot = originAbsoluteRoot;
    }

    /// <summary>
    /// Discovers all files, not just required roots. Required paths must be nonempty, canonical relative names.
    /// Read-share file and ancestor handles reject reparse/physical aliases and observed name changes during
    /// capture. This is a bounded scan, not a filesystem transaction. Failure returns no partial snapshot;
    /// every source handle is closed before return, including cancellation and budget failures.
    /// </summary>
    public static D3D9EffectResourceTree Capture(string absoluteCommonRoot, IEnumerable<string> requiredRelativeFiles,
        D3D9EffectResourceTreeLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= new(); limits.Validate();
        string root = EffectTreeFiles.AbsoluteDirectory(absoluteCommonRoot);
        string[] required = Required(requiredRelativeFiles, limits);
        cancellationToken.ThrowIfCancellationRequested();
        var files = new List<D3D9EffectResourceContent>(); var directories = new List<string>();
        var observed = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0, pathBytes = 0;
        using var held = new EffectTreeFiles.DirectoryLeases();
        held.HoldAncestors(root);
        var identities = new HashSet<EffectTreeFiles.Identity>();
        Visit(root, 0);
        foreach (var pair in observed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!pair.Value.SequenceEqual(Enumerate(pair.Key), StringComparer.Ordinal))
                throw new InvalidDataException("Effect-tree directory entries changed during capture: " + pair.Key);
        }
        files.Sort((left, right) => StringComparer.Ordinal.Compare(left.Description.RelativePath, right.Description.RelativePath));
        directories.Sort(StringComparer.Ordinal);
        if (required.Any(path => !files.Any(value => value.Description.RelativePath == path)))
            throw new InvalidDataException("A required effect-tree file is missing or has different path casing.");
        EffectTreeFiles.ValidateOriginFilePaths(root, files.Select(value => value.Description.RelativePath), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(files.ToArray(), directories.ToArray(), required, root);

        string[] Enumerate(string directory)
        {
            var found = new List<string>();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (found.Count >= (long)limits.MaxFiles + limits.MaxDirectories)
                    throw new InvalidDataException("Effect-tree enumeration exceeds its entry budget.");
                found.Add(entry);
            }
            found.Sort(StringComparer.Ordinal); return found.ToArray();
        }
        void Visit(string directory, int depth)
        {
            string[] entries = Enumerate(directory); observed.Add(directory, entries);
            foreach (string entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (depth + 1 > limits.MaxDepth) throw new InvalidDataException("Effect-tree depth exceeds its budget.");
                string relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                int bytes = EffectTreeFiles.RelativePath(relative);
                pathBytes += bytes;
                if (pathBytes > limits.MaxPathBytes) throw new InvalidDataException("Effect-tree names exceed their byte budget.");
                if (!names.Add(relative)) throw new InvalidDataException("Effect-tree names collide under Windows path comparison.");
                using var handle = EffectTreeFiles.Open(entry, directory: true);
                var information = EffectTreeFiles.Information(handle, entry);
                EffectTreeFiles.RejectReparse(information, entry);
                if (information.IsDirectory)
                {
                    if (directories.Count == limits.MaxDirectories) throw new InvalidDataException("Effect-tree directory count exceeds its budget.");
                    // A separate retained handle is needed after the inspection handle leaves this scope.
                    held.HoldAncestors(entry); directories.Add(relative); Visit(entry, depth + 1);
                }
                else
                {
                    if (files.Count == limits.MaxFiles) throw new InvalidDataException("Effect-tree file count exceeds its budget.");
                    using var sourceHandle = EffectTreeFiles.Open(entry, directory: false);
                    information = EffectTreeFiles.Information(sourceHandle, entry);
                    EffectTreeFiles.RejectReparse(information, entry);
                    if (information.IsDirectory || !identities.Add(information.Identity))
                        throw new InvalidDataException("Effect-tree files must have distinct physical identities.");
                    long length = information.Length;
                    if (length > limits.MaxFileBytes || length > limits.MaxTotalBytes - totalBytes)
                        throw new InvalidDataException("Effect-tree file bytes exceed their budget: " + relative);
                    byte[] content = new byte[checked((int)length)];
                    using var stream = new FileStream(sourceHandle, FileAccess.Read);
                    for (int offset = 0; offset < content.Length;)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int read = stream.Read(content.AsSpan(offset, Math.Min(1024 * 1024, content.Length - offset)));
                        if (read == 0) throw new EndOfStreamException("An effect-tree file changed during capture.");
                        offset += read;
                    }
                    if (stream.ReadByte() != -1) throw new InvalidDataException("An effect-tree file changed during capture.");
                    // Retain a read-share handle through the final directory comparison as well.
                    held.HoldFile(entry, information.Identity);
                    totalBytes += length;
                    files.Add(new(new(relative, length, Convert.ToHexStringLower(SHA256.HashData(content))), content));
                }
            }
        }
    }

    public byte[] CopyFileBytes(string relativePath)
    {
        _ = EffectTreeFiles.RelativePath(relativePath);
        if (!byPath.TryGetValue(relativePath, out var file)) throw new FileNotFoundException("File is not part of the captured effect tree.", relativePath);
        return (byte[])file.Bytes.Clone();
    }

    /// <summary>Traditional schema over neutral opaque transport, with ordinal ASCII blob names.</summary>
    public static PmaSaveOptions CreateSaveOptions(SceneProjectSession project, D3D9EffectResourceTree tree, PmaSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(tree); options ??= new();
        ArgumentNullException.ThrowIfNull(options.OptionalExtensions); ArgumentNullException.ThrowIfNull(options.AdditionalResources);
        ArgumentNullException.ThrowIfNull(options.ValidationOptions);
        if (options.OptionalExtensions.Keys.Any(key => key.Equals(ExtensionName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Save options already contain the effect-tree extension.", nameof(options));
        if (options.ContentSelection?.ExcludesExtension(ExtensionName) == true)
            throw new InvalidDataException("Save content selection excludes the effect-tree extension.");
        var resources = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in options.AdditionalResources)
        {
            // Validate every logical key before filtering, matching neutral container admission.
            ValidateResourceKey(pair.Key);
            if (options.ContentSelection?.ExcludesResource(pair.Key) == true) continue;
            if (!resources.TryAdd(pair.Key, pair.Value)) throw new ArgumentException("Save resource paths collide.", nameof(options));
        }
        var metadata = new MetadataV1(1, tree.Directories.ToArray(), tree.RequiredRelativeFiles.ToArray(),
            tree.Files.Select((value, index) => new FileData(value.RelativePath, ResourcePath(index), value.ByteLength, value.Sha256)).ToArray());
        // Legacy data has no trustworthy original absolute identity. Preserve schema one instead of
        // inventing an origin from the package location, process directory or private materialization.
        byte[] json = tree.dependencyMetadata is not null
            ? JsonSerializer.SerializeToUtf8Bytes(tree.dependencyMetadata, Json)
            : tree.OriginAbsoluteRoot is null
            ? JsonSerializer.SerializeToUtf8Bytes(metadata, Json)
            : JsonSerializer.SerializeToUtf8Bytes(new MetadataV2(2, tree.OriginAbsoluteRoot,
                metadata.Directories, metadata.RequiredRelativeFiles, metadata.Files), Json);
        if (json.Length > 16 * 1024 * 1024) throw new InvalidDataException("Effect-tree metadata exceeds its byte budget.");
        var lengths = resources.ToDictionary(pair => pair.Key, pair => (long)pair.Value.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var file in metadata.Files)
        {
            if (options.ContentSelection?.ExcludesResource(file.ResourcePath) == true)
                throw new InvalidDataException("Save content selection excludes an effect-tree blob.");
            if (!lengths.TryAdd(file.ResourcePath, file.ByteLength))
                throw new ArgumentException("Save options already contain an owned effect-tree resource.", nameof(options));
        }
        PmaProjectStore.ValidateAdditionalResourceBudget(project, lengths,
            options.ValidationOptions with { ContentSelection = options.ContentSelection });
        // Only one defensive copy per owned file, after all metadata and neutral budgets pass.
        for (int index = 0; index < tree.contents.Length; index++)
            resources.Add(ResourcePath(index), (byte[])tree.contents[index].Bytes.Clone());
        var extensions = options.OptionalExtensions.Where(pair => options.ContentSelection?.ExcludesExtension(pair.Key) != true)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        using var parsed = JsonDocument.Parse(json); extensions.Add(ExtensionName, parsed.RootElement.Clone());
        return options with
        {
            AdditionalResources = new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(resources),
            OptionalExtensions = new ReadOnlyDictionary<string, JsonElement>(extensions)
        };
    }

    /// <summary>Validates this schema and SHA256 after neutral archive validation, without accessing originals.</summary>
    public static D3D9EffectResourceTree? Read(PmaLoadedProject project, D3D9EffectResourceTreeLimits? limits = null,
        CancellationToken cancellationToken = default)
        => ReadPackage(project, limits, cancellationToken);

    /// <summary>Validates the same schema and detached resource bytes through neutral read-only package
    /// access, including before host models or physics exist. Does not materialize files or execute effects.</summary>
    public static D3D9EffectResourceTree? ReadPackage(IPmaPackageResources project, D3D9EffectResourceTreeLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project); limits ??= new(); limits.Validate(); cancellationToken.ThrowIfCancellationRequested();
        if (!project.OptionalExtensions.TryGetValue(ExtensionName, out var element)) return null;
        try
        {
            string raw = element.GetRawText();
            if (EffectTreeFiles.StrictUtf8.GetByteCount(raw) > limits.MaxMetadataBytes) throw new InvalidDataException("Effect-tree metadata exceeds its byte budget.");
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version))
                throw new InvalidDataException("Missing effect-tree schema version.");
            MetadataV1 metadata; string? origin = null;
            if (version == 3) return ReadDependencyPackage(project, raw, limits, cancellationToken);
            if (version == 1)
                metadata = JsonSerializer.Deserialize<MetadataV1>(raw, Json) ?? throw new InvalidDataException("Missing effect-tree metadata.");
            else if (version == 2)
            {
                var current = JsonSerializer.Deserialize<MetadataV2>(raw, Json) ?? throw new InvalidDataException("Missing effect-tree metadata.");
                origin = EffectTreeFiles.OriginDirectory(current.OriginAbsoluteRoot);
                metadata = new(2, current.Directories, current.RequiredRelativeFiles, current.Files);
            }
            else throw new InvalidDataException("Unsupported effect-tree schema version.");
            if (metadata.SchemaVersion != version || metadata.Files is null || metadata.Directories is null || metadata.RequiredRelativeFiles is null
                || metadata.Files.Length is < 1 || metadata.Files.Length > limits.MaxFiles || metadata.Directories.Length > limits.MaxDirectories)
                throw new InvalidDataException("Unsupported or incomplete effect-tree metadata.");
            string[] required = Required(metadata.RequiredRelativeFiles, limits);
            if (!metadata.RequiredRelativeFiles.SequenceEqual(required, StringComparer.Ordinal)) throw new InvalidDataException("Effect-tree roots are not in canonical order.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = new HashSet<string>(StringComparer.Ordinal); long pathBytes = 0, totalBytes = 0;
            string? previous = null;
            foreach (string path in metadata.Directories)
            {
                ValidateName(path);
                if (previous is not null && StringComparer.Ordinal.Compare(previous, path) >= 0) throw new InvalidDataException("Effect-tree directories are not in canonical order.");
                previous = path; directories.Add(path);
            }
            previous = null;
            for (int index = 0; index < metadata.Files.Length; index++)
            {
                var file = metadata.Files[index] ?? throw new InvalidDataException("Missing effect-tree file descriptor.");
                ValidateName(file.RelativePath);
                if (previous is not null && StringComparer.Ordinal.Compare(previous, file.RelativePath) >= 0) throw new InvalidDataException("Effect-tree files are not in canonical order.");
                previous = file.RelativePath;
                if (file.ResourcePath != ResourcePath(index) || file.ByteLength < 0 || file.ByteLength > limits.MaxFileBytes
                    || file.ByteLength > limits.MaxTotalBytes - totalBytes || file.Sha256 is null || file.Sha256.Length != 64
                    || file.Sha256.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                    throw new InvalidDataException("Invalid effect-tree blob descriptor.");
                totalBytes += file.ByteLength;
            }
            foreach (string path in metadata.Directories.Concat(metadata.Files.Select(value => value.RelativePath)))
            {
                int slash = path.LastIndexOf('/');
                if (slash >= 0 && !directories.Contains(path[..slash])) throw new InvalidDataException("An effect-tree parent directory is absent or aliases another path.");
            }
            var fileNames = metadata.Files.Select(value => value.RelativePath).ToHashSet(StringComparer.Ordinal);
            if (required.Any(value => !fileNames.Contains(value))) throw new InvalidDataException("A required effect-tree root is not present.");
            if (origin is not null)
                EffectTreeFiles.ValidateOriginFilePaths(origin, metadata.Files.Select(value => value.RelativePath), cancellationToken);
            var contents = new D3D9EffectResourceContent[metadata.Files.Length];
            for (int index = 0; index < contents.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested(); var file = metadata.Files[index];
                byte[] bytes = project.ReadResource(file.ResourcePath, file.ByteLength);
                if (bytes.LongLength != file.ByteLength || Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256)
                    throw new InvalidDataException("An effect-tree blob disagrees with its byte length or SHA256.");
                contents[index] = new(new(file.RelativePath, file.ByteLength, file.Sha256), bytes);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(contents, metadata.Directories, required, origin);

            void ValidateName(string path)
            {
                pathBytes += EffectTreeFiles.RelativePath(path);
                if (pathBytes > limits.MaxPathBytes || path.Count(value => value == '/') + 1 > limits.MaxDepth)
                    throw new InvalidDataException("Effect-tree path/depth budget exceeded.");
                if (!names.Add(path)) throw new InvalidDataException("Effect-tree file/directory names collide.");
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException or FileNotFoundException)
        { throw new InvalidDataException("Invalid portable effect-tree data.", error); }
    }

    /// <summary>Creates a fresh owned child of an existing caller cache. Keep the returned owner alive until
    /// native sessions and their asset leases have been successfully released. No source path is consulted.</summary>
    public D3D9EffectResourceTreeMaterialization Materialize(string existingAbsoluteCacheDirectory, CancellationToken cancellationToken = default)
        => D3D9EffectResourceTreeMaterialization.Create(this, contents, existingAbsoluteCacheDirectory, cancellationToken);

    private static string[] Required(IEnumerable<string> paths, D3D9EffectResourceTreeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(paths); var required = new List<string>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (string path in paths)
        {
            bytes += EffectTreeFiles.RelativePath(path);
            if (required.Count == limits.MaxFiles || bytes > limits.MaxPathBytes) throw new InvalidDataException("Effect-tree required roots exceed their budget.");
            if (!names.Add(path)) throw new ArgumentException("Required effect-tree roots must be distinct.", nameof(paths));
            required.Add(path);
        }
        if (required.Count == 0) throw new ArgumentException("At least one required effect-tree file is needed.", nameof(paths));
        required.Sort(StringComparer.Ordinal); return required.ToArray();
    }
    private static string ResourcePath(int ordinal) => ResourceRoot + "file" + ordinal.ToString("D5", CultureInfo.InvariantCulture) + ".bin";
    private static void ValidateResourceKey(string path)
    {
        // Neutral opaque resource keys deliberately accept a narrower ASCII vocabulary than native tree names.
        if (string.IsNullOrEmpty(path) || path.Length > 512 || !path.StartsWith("render-resources/", StringComparison.Ordinal)
            || path.Contains('\\') || path.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.')
                || segment.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('.' or '_' or '-'))))
            throw new InvalidDataException("Invalid neutral effect-tree save resource key.");
        _ = EffectTreeFiles.RelativePath(path);
    }
    private sealed record MetadataV1(int SchemaVersion, string[] Directories, string[] RequiredRelativeFiles, FileData[] Files);
    private sealed record MetadataV2(int SchemaVersion, string OriginAbsoluteRoot, string[] Directories,
        string[] RequiredRelativeFiles, FileData[] Files);
    private sealed record FileData(string RelativePath, string ResourcePath, long ByteLength, string Sha256);
}

/// <summary>Owns only the generated cache child and immutable file/ancestor leases. Dispose is retryable:
/// external session leases that still pin a file cause IOException before any file is deleted.</summary>
public sealed class D3D9EffectResourceTreeMaterialization : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, string> paths;
    private readonly Dictionary<string, EffectTreeFiles.Identity> directories;
    private readonly Dictionary<string, EffectTreeFiles.Identity> files;
    private readonly D3D9EffectResourceMap? resourceMap;
    private D3D9ObjectFxAssets? lease;
    private bool disposed;
    public string RootDirectory { get; }
    public IReadOnlyList<string> CompleteDependencyPaths { get; }

    private D3D9EffectResourceTreeMaterialization(string root, Dictionary<string, string> paths,
        Dictionary<string, EffectTreeFiles.Identity> directories, Dictionary<string, EffectTreeFiles.Identity> files,
        D3D9ObjectFxAssets lease, D3D9EffectResourceMap? resourceMap)
    {
        RootDirectory = root; this.paths = paths; this.directories = directories; this.files = files; this.lease = lease;
        this.resourceMap = resourceMap;
        CompleteDependencyPaths = Array.AsReadOnly(paths.Values.ToArray());
    }

    public string GetAbsolutePath(string relativePath)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this); _ = EffectTreeFiles.RelativePath(relativePath);
            return paths.TryGetValue(relativePath, out string? path) ? path : throw new FileNotFoundException("File is not part of the materialized effect tree.", relativePath);
        }
    }

    /// <summary>Returns the immutable original-identity to private-file mapping. Origin paths are never
    /// accessed. Legacy schema-one trees cannot create this mapping because their origin is unknown.</summary>
    public D3D9EffectResourceMap CreateResourceMap()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return resourceMap ?? throw new InvalidOperationException("Legacy effect-tree data has no original absolute root for a resource map.");
        }
    }

    internal static D3D9EffectResourceTreeMaterialization Create(D3D9EffectResourceTree tree,
        D3D9EffectResourceContent[] contents, string cache, CancellationToken token)
    {
        cache = EffectTreeFiles.AbsoluteDirectory(cache); token.ThrowIfCancellationRequested();
        var directories = new Dictionary<string, EffectTreeFiles.Identity>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, EffectTreeFiles.Identity>(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        string root = Path.Combine(cache, "mda-fx-" + Guid.NewGuid().ToString("N"));
        // Cache prefixes count toward native lease limits. Reject before creating any directory or byte copy.
        EffectTreeFiles.ValidateMaterializationPaths(root, tree.Directories, tree.Files.Select(file => file.RelativePath));
        // The map also bounds the expanded original and private paths before creating anything. This
        // constructor validates values only; it must never open an original path or require it to exist.
        D3D9EffectResourceMap? resourceMap = null;
        if (tree.DependencyGraph is { } graph)
            resourceMap = new(graph.Nodes.Select(node => new D3D9EffectResourceBinding(
                node.OriginAbsolutePath, EffectTreeFiles.Descendant(root, node.RelativePath))));
        else if (tree.OriginAbsoluteRoot is { } originRoot)
            resourceMap = new(contents.Select(content => new D3D9EffectResourceBinding(
                EffectTreeFiles.OriginDescendant(originRoot, content.Description.RelativePath),
                EffectTreeFiles.Descendant(root, content.Description.RelativePath))));
        D3D9ObjectFxAssets? lease = null;
        using var held = new EffectTreeFiles.DirectoryLeases(); held.HoldAncestors(cache);
        try
        {
            EffectTreeFiles.CreateOwnedDirectory(root); directories.Add(root, held.HoldAncestors(root));
            foreach (string relative in tree.Directories)
            {
                token.ThrowIfCancellationRequested(); string path = EffectTreeFiles.Descendant(root, relative);
                EffectTreeFiles.CreateOwnedDirectory(path); directories.Add(path, held.HoldAncestors(path));
            }
            foreach (var content in contents)
            {
                token.ThrowIfCancellationRequested(); string path = EffectTreeFiles.Descendant(root, content.Description.RelativePath);
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var info = EffectTreeFiles.Information(stream.SafeFileHandle, path);
                    EffectTreeFiles.RejectReparse(info, path); files.Add(path, info.Identity);
                    paths.Add(content.Description.RelativePath, path);
                    for (int offset = 0; offset < content.Bytes.Length;)
                    {
                        token.ThrowIfCancellationRequested(); int count = Math.Min(1024 * 1024, content.Bytes.Length - offset);
                        stream.Write(content.Bytes.AsSpan(offset, count)); offset += count;
                    }
                }
            }
            token.ThrowIfCancellationRequested(); lease = D3D9ObjectFxAssets.Open(new([]), paths.Values);
            foreach (var content in contents)
            {
                token.ThrowIfCancellationRequested();
                using var stream = new FileStream(paths[content.Description.RelativePath], FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != content.Description.ByteLength || Convert.ToHexStringLower(SHA256.HashData(stream)) != content.Description.Sha256)
                    throw new InvalidDataException("A materialized effect-tree file changed before it could be leased.");
            }
            token.ThrowIfCancellationRequested();
            return new(root, paths, directories, files, lease, resourceMap);
        }
        catch (Exception error)
        {
            lease?.Dispose(); held.Dispose();
            try { if (directories.ContainsKey(root)) EffectTreeFiles.DeleteOwned(root, directories, files); }
            catch (Exception cleanupError) { error.Data["EffectTreeCleanupError"] = cleanupError; error.Data["EffectTreeCleanupRoot"] = root; }
            throw;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            lease?.Dispose(); lease = null;
            EffectTreeFiles.DeleteOwned(RootDirectory, directories, files);
            disposed = true;
        }
    }
}

/// <summary>Local Windows handle operations for the tree bridge; never traverses an unowned deletion tree.</summary>
internal static class EffectTreeFiles
{
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal readonly record struct Identity(uint Volume, uint High, uint Low);
    internal readonly record struct Info(uint Attributes, long Length, Identity Identity)
    { internal bool IsDirectory => (Attributes & 0x10) != 0; }

    internal static void ValidateMaterializationPaths(string root, IEnumerable<string> relativeDirectories, IEnumerable<string> relativeFiles)
    {
        var heldDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assetDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long heldBytes = 0, assetDirectoryBytes = 0, fileBytes = 0;
        AddAncestors(root, heldDirectories, ref heldBytes, 65_792, 64L * 1024 * 1024);
        foreach (string relative in relativeDirectories)
            AddAncestors(Descendant(root, relative), heldDirectories, ref heldBytes, 65_792, 64L * 1024 * 1024);
        int fileCount = 0;
        foreach (string relative in relativeFiles)
        {
            string path = Descendant(root, relative);
            fileBytes += AbsoluteBytes(path);
            if (++fileCount > 65_536 || fileBytes > 16L * 1024 * 1024)
                throw new InvalidDataException("Materialized effect-tree file paths exceed native lease budgets; use a shorter cache path.");
            AddAncestors(Path.GetDirectoryName(path)!, assetDirectories, ref assetDirectoryBytes, 65_536, 16L * 1024 * 1024);
        }
        static int AbsoluteBytes(string path)
        {
            int bytes = StrictUtf8.GetByteCount(path);
            if (bytes > 32767) throw new InvalidDataException("A materialized effect-tree path exceeds native lease limits; use a shorter cache path.");
            return bytes;
        }
        static void AddAncestors(string path, HashSet<string> paths, ref long bytes, int countLimit, long byteLimit)
        {
            for (string? value = path; value is not null && paths.Add(value); value = Path.GetDirectoryName(value))
            {
                bytes += AbsoluteBytes(value);
                if (paths.Count > countLimit || bytes > byteLimit)
                    throw new InvalidDataException("Materialized effect-tree ancestor paths exceed lease budgets; use a shorter cache path.");
            }
        }
    }

    internal static string AbsoluteDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Effect-tree capture/materialization requires Windows file-sharing semantics.");
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('/' or '\\'))
            throw new ArgumentException("A normal absolute Windows drive directory is required.", nameof(path));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Replace('/', '\\');
        full = char.ToUpperInvariant(full[0]) + full[1..];
        return OriginDirectory(full);
    }

    /// <summary>Pure lexical validation of stored provenance. Never resolves filesystem identity,
    /// checks existence, opens a drive or uses process/per-drive working directories.</summary>
    internal static string OriginDirectory(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 3 || path[0] is < 'A' or > 'Z' || path[1] != ':'
            || path[2] != '\\' || path.Contains('/'))
            throw new ArgumentException("Effect-tree origin must be a canonical absolute Windows drive path.", nameof(path));
        int bytes;
        try { bytes = StrictUtf8.GetByteCount(path); }
        catch (EncoderFallbackException error) { throw new ArgumentException("Effect-tree origin must contain valid Unicode.", nameof(path), error); }
        if (bytes > 32767) throw new ArgumentException("Effect-tree origin exceeds its UTF-8 byte limit.", nameof(path));
        if (path.Length > 3) _ = RelativePath(path[3..].Replace('\\', '/'));
        return path;
    }

    internal static string OriginDescendant(string root, string relative)
    {
        _ = RelativePath(relative);
        return root + (root.Length == 3 ? "" : "\\") + relative.Replace('/', '\\');
    }

    internal static void ValidateOriginFilePaths(string root, IEnumerable<string> relativeFiles, CancellationToken token)
    {
        long total = 0;
        foreach (string relative in relativeFiles)
        {
            token.ThrowIfCancellationRequested();
            int bytes = StrictUtf8.GetByteCount(OriginDescendant(root, relative));
            // These are necessary origin-only bounds. The combined origin/private-path map is
            // checked separately before materialization, once its cache prefix is known.
            if (bytes > 32767 || (total += bytes) > 16L * 1024 * 1024)
                throw new InvalidDataException("Effect-tree original file identities exceed resource-map path budgets.");
        }
    }

    internal static int RelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Any(value => value < 32 || value is ':' or '<' or '>' or '"' or '|' or '?' or '*'))
            throw new ArgumentException("Effect-tree paths must be canonical portable relative names.", nameof(path));
        int bytes;
        try { bytes = StrictUtf8.GetByteCount(path); }
        catch (EncoderFallbackException error) { throw new ArgumentException("Effect-tree names must contain valid Unicode.", nameof(path), error); }
        if (bytes > 32767) throw new ArgumentException("Effect-tree path exceeds its UTF-8 byte limit.", nameof(path));
        foreach (string segment in path.Split('/'))
        {
            string stem = segment.Split('.')[0].ToUpperInvariant();
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')
                throw new ArgumentException("Effect-tree path contains an unsafe Windows component.", nameof(path));
        }
        return bytes;
    }

    internal static string Descendant(string root, string relative)
    {
        _ = RelativePath(relative); string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', '\\')));
        if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Effect-tree path escapes its owned root.");
        return path;
    }

    internal static SafeFileHandle Open(string path, bool directory, bool delete = false)
    {
        const uint read = 0x80000000, attributes = 0x80, deleteAccess = 0x10000;
        var handle = CreateFile(path, delete ? deleteAccess | attributes : directory ? attributes : read,
            delete ? 1U | 4U : 1U, 0, 3, 0x00200000U | (directory ? 0x02000000U : 0), 0);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error(); handle.Dispose();
        if (error == 2) throw new FileNotFoundException("Effect-tree file does not exist.", path);
        if (error == 3) throw new DirectoryNotFoundException("Effect-tree directory does not exist: " + path);
        throw new IOException("Cannot lease effect-tree path: " + path, new Win32Exception(error));
    }

    internal static Info Information(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("Cannot inspect effect-tree path: " + path, new Win32Exception(Marshal.GetLastWin32Error()));
        ulong length = ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
        if (length > long.MaxValue) throw new InvalidDataException("Effect-tree file size exceeds its supported range.");
        return new(info.Attributes, (long)length, new(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow));
    }
    internal static void RejectReparse(Info info, string path)
    {
        if ((info.Attributes & 0x400) != 0) throw new InvalidDataException("Reparse effect-tree paths are not supported: " + path);
    }
    internal static void CreateOwnedDirectory(string path)
    {
        if (!CreateDirectory(path, 0)) throw new IOException("Cannot create a fresh owned effect-tree directory: " + path, new Win32Exception(Marshal.GetLastWin32Error()));
    }

    internal sealed class DirectoryLeases : IDisposable
    {
        private readonly Dictionary<string, (SafeFileHandle Handle, Identity Identity)> directories = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<SafeFileHandle> files = [];
        private long pathBytes;
        internal Identity HoldAncestors(string path)
        {
            var pending = new Stack<string>();
            for (string? value = path; value is not null && !directories.ContainsKey(value); value = Path.GetDirectoryName(value)) pending.Push(value);
            while (pending.TryPop(out string? value))
            {
                if (directories.Count >= 65_792 || (pathBytes += StrictUtf8.GetByteCount(value)) > 64L * 1024 * 1024)
                    throw new InvalidDataException("Effect-tree ancestor handles exceed their budget.");
                var handle = Open(value, directory: true);
                try
                {
                    var info = Information(handle, value); RejectReparse(info, value);
                    if (!info.IsDirectory) throw new InvalidDataException("An effect-tree ancestor is not a directory.");
                    directories.Add(value, (handle, info.Identity));
                }
                catch { handle.Dispose(); throw; }
            }
            return directories[path].Identity;
        }
        internal void HoldFile(string path, Identity expected)
        {
            var handle = Open(path, directory: false);
            try
            {
                var info = Information(handle, path); RejectReparse(info, path);
                if (info.IsDirectory || info.Identity != expected) throw new InvalidDataException("An effect-tree file identity changed during capture.");
                files.Add(handle);
            }
            catch { handle.Dispose(); throw; }
        }
        internal void ReleaseDirectory(string path)
        { if (directories.Remove(path, out var held)) held.Handle.Dispose(); }
        public void Dispose()
        {
            for (int index = files.Count - 1; index >= 0; index--) files[index].Dispose(); files.Clear();
            foreach (var value in directories.Values.Reverse()) value.Handle.Dispose(); directories.Clear();
        }
    }

    internal static void DeleteOwned(string root, IReadOnlyDictionary<string, Identity> expectedDirectories,
        IReadOnlyDictionary<string, Identity> expectedFiles)
    {
        // No enumeration and no recursive delete: unknown names cause the final empty-directory delete to
        // fail and leave them untouched. Every traversed ancestor remains pinned while file deletes run.
        if (!expectedDirectories.TryGetValue(root, out var expectedRoot)) throw new IOException("Missing owned effect-tree root identity.");
        string? cache = Path.GetDirectoryName(root);
        if (cache is null || Path.GetFileName(root).Length != 39 || !Path.GetFileName(root).StartsWith("mda-fx-", StringComparison.Ordinal)
            || !Guid.TryParseExact(Path.GetFileName(root)[7..], "N", out _)) throw new IOException("Invalid owned effect-tree root.");
        using var held = new DirectoryLeases(); held.HoldAncestors(cache);
        if (!Directory.Exists(root)) throw new IOException("Owned effect-tree root is missing; cleanup refused.");
        if (held.HoldAncestors(root) != expectedRoot) throw new IOException("Owned effect-tree root was replaced; cleanup refused.");
        foreach (var directory in expectedDirectories)
        {
            if (directory.Key == root) continue;
            if (!directory.Key.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Owned directory escapes its root.");
            if (!Directory.Exists(directory.Key)) continue;
            if (held.HoldAncestors(directory.Key) != directory.Value) throw new IOException("Owned effect-tree directory was replaced; cleanup refused.");
        }
        var deleteHandles = new List<SafeFileHandle>();
        try
        {
            foreach (var file in expectedFiles)
            {
                if (!file.Key.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Owned file escapes its root.");
                SafeFileHandle handle;
                try { handle = Open(file.Key, directory: false, delete: true); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                try
                {
                    var info = Information(handle, file.Key); RejectReparse(info, file.Key);
                    if (info.IsDirectory || info.Identity != file.Value) throw new IOException("Owned effect-tree file was replaced; cleanup refused.");
                    deleteHandles.Add(handle);
                }
                catch { handle.Dispose(); throw; }
            }
            // All DELETE rights have been acquired before the first modification. Delete by verified handle,
            // avoiding a path replacement window and rejecting still-active native read-share leases.
            foreach (var handle in deleteHandles)
            {
                byte delete = 1;
                if (!SetFileInformationByHandle(handle, 4, ref delete, 1))
                    throw new IOException("Cannot remove an owned effect-tree file.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
        finally { foreach (var handle in deleteHandles) handle.Dispose(); }
        foreach (string path in expectedDirectories.Keys.OrderByDescending(value => value.Length))
        {
            held.ReleaseDirectory(path);
            SafeFileHandle handle;
            try { handle = Open(path, directory: true, delete: true); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            using (handle)
            {
                var info = Information(handle, path); RejectReparse(info, path);
                if (!info.IsDirectory || info.Identity != expectedDirectories[path])
                    throw new IOException("Owned effect-tree directory was replaced; cleanup refused.");
                byte delete = 1;
                if (!SetFileInformationByHandle(handle, 4, ref delete, 1))
                    throw new IOException("Cannot remove an owned effect-tree directory.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        internal uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInformation information);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, nint security);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref byte information, uint bytes);
}
