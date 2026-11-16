// Texture resolution reference: Saba PMXModel.cpp:494-549 / PMDModel.cpp:349-429,
// @29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT (c) 2016-2017 benikabocha.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Copies source models and dependencies by default. Set CopyModelAssets to false for external references.</summary>
public sealed record SceneProjectSaveOptions
{
    public bool CopyModelAssets { get; init; } = true;
    internal PortableModelAssets.Limits? PortableLimits { get; init; }
    /// <summary>An already prepared capture to publish instead of preparing one here; the caller owns it.</summary>
    internal PortableModelAssets.Capture? PreparedPortable { get; init; }
}

/// <summary>Content-addressed portable model bundles. Inputs are only read, copies are rewritten.
/// Rejects observed directory/file redirects and rechecks before publishing. This is a local project
/// packager, not an isolation boundary against hostile concurrent filesystem replacement.</summary>
internal static class PortableModelAssets
{
    internal const int MaxFileBytes = 256 * 1024 * 1024;
    internal const long MaxTotalBytes = 512L * 1024 * 1024;
    private const int MaxFiles = 100_000;
    private const int MaxManifestBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    internal sealed record Limits(long FileBytes, long TotalBytes, string? TemporaryDirectory = null)
    {
        internal static Limits Default { get; } = new(MaxFileBytes, MaxTotalBytes);
        internal static Limits FromPma(PmaLoadOptions options) => new(options.MaxEntryBytes, options.MaxExpandedBytes);
    }

    internal sealed class Capture(string temporaryParent) : IDisposable
    {
        private string? temporary;
        internal readonly Dictionary<Guid, string> Paths = [];
        internal readonly Dictionary<string, Bundle> Bundles = new(StringComparer.Ordinal);
        /// <summary>Dependency snapshots (length + SHA-256) by full path. Shared between the original and edited
        /// captures of one save so each texture is read and hashed once; publishing still re-hashes what it copies.</summary>
        internal Dictionary<string, Resource> Snapshots { get; init; } =
            new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        internal string ModelTemporary()
        {
            temporary ??= Path.Combine(temporaryParent, "portable-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            return Path.Combine(temporary, Guid.NewGuid().ToString("N"));
        }
        public void Dispose() { if (temporary is not null && Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
        internal string[] Hashes => Bundles.Keys.Order(StringComparer.Ordinal).ToArray();
        internal void Publish(string parent, CancellationToken token, IReadOnlySet<string>? alreadyPublished = null)
        {
            foreach (var (hash, bundle) in Bundles)
            {
                token.ThrowIfCancellationRequested();
                // Content-addressed: a bundle this save already published is identical; verifying it again re-read every file.
                if (alreadyPublished?.Contains(hash) == true) continue;
                string directory = Path.Combine(parent, "assets", "models", hash);
                RejectRedirects(directory);
                Directory.CreateDirectory(directory);
                RejectRedirects(directory);
                // Check every existing entry before filling any absent file in a previously published bundle.
                foreach (var file in bundle.Files) VerifyExisting(directory, file.Key, file.Value, token);
                VerifyExisting(directory, "manifest.json", Resource.FromBytes(bundle.Manifest), token);
                foreach (var file in bundle.Files) Store(directory, file.Key, file.Value, token);
                Store(directory, "manifest.json", Resource.FromBytes(bundle.Manifest), token);
            }
        }
    }

    internal sealed record Resource(string? Path, byte[]? Bytes, long Length, string Sha256)
    {
        internal static Resource FromBytes(byte[] bytes) => new(null, bytes, bytes.LongLength, Hash(bytes));
        internal Stream Open() => Path is null ? new MemoryStream(Bytes!, writable: false) : OpenInput(Path, Length);
    }
    internal sealed record Bundle(byte[] Manifest, Dictionary<string, Resource> Files);

    internal static Capture Prepare(IReadOnlyList<EditableSceneObject> objects, CancellationToken token,
        IReadOnlyDictionary<Guid, PmxModel>? modelOverrides = null, Limits? limits = null, Capture? shareSnapshotsWith = null)
    {
        limits ??= Limits.Default;
        var capture = shareSnapshotsWith is null ? new Capture(limits.TemporaryDirectory ?? Path.GetTempPath())
            : new Capture(limits.TemporaryDirectory ?? Path.GetTempPath()) { Snapshots = shareSnapshotsWith.Snapshots };
        try
        {
            var loaded = new Dictionary<string, (string Hash, string Name)>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            long total = 0;
            foreach (var item in objects)
            {
                token.ThrowIfCancellationRequested();
                string path = Path.GetFullPath(item.SourcePath);
                var replacement = modelOverrides?.GetValueOrDefault(item.Id);
                // Two instances of one source may carry distinct edited PMX versions.
                string cacheKey = replacement is null ? path : path + "|" + item.Id;
                if (!loaded.TryGetValue(cacheKey, out var result))
                {
                    Bundle bundle = PrepareBundle(path, token, capture, limits, replacement);
                    string hash = Hash(bundle.Manifest), name = replacement is null ? "model" + Path.GetExtension(path).ToLowerInvariant() : "model.pmx";
                    if (!capture.Bundles.ContainsKey(hash))
                    {
                        foreach (var resource in bundle.Files.Values) AddBudget(ref total, resource.Length, limits.TotalBytes);
                        capture.Bundles.Add(hash, bundle);
                    }
                    result = (hash, name); loaded.Add(cacheKey, result);
                }
                capture.Paths.Add(item.Id, Relative(result.Hash, result.Name));
            }
            return capture;
        }
        catch { capture.Dispose(); throw; }
    }

    private static Bundle PrepareBundle(string path, CancellationToken token, Capture capture, Limits limits, PmxModel? replacement = null)
    {
        string directory = Path.GetDirectoryName(path)!;
        using var source = OpenInput(path, limits.FileBytes);
        var files = new Dictionary<string, Resource>(StringComparer.Ordinal);
        var shared = new Dictionary<(string Hash, string Extension), string>();
        var dependencies = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        long total = 0;
        string Copy(string reference)
        {
            if (reference.Length == 0) return reference;
            string dependency = ContainedDependency(directory, reference);
            if (dependencies.TryGetValue(dependency, out string? samePath)) return samePath;
            if (!capture.Snapshots.TryGetValue(dependency, out var resource))
            {
                try { resource = SnapshotFile(dependency, limits.FileBytes, token); }
                catch (IOException exception) { throw new IOException($"Cannot copy dependency '{reference}' for model '{path}'.", exception); }
                capture.Snapshots.Add(dependency, resource);
            }
            string extension = Path.GetExtension(dependency).ToLowerInvariant();
            if (extension.Length > 8 || extension.Any(c => c != '.' && !char.IsAsciiLetterOrDigit(c)))
                throw new InvalidDataException($"Dependency '{reference}' has an unsupported extension for a portable alias.");
            var key = (resource.Sha256, extension);
            if (shared.TryGetValue(key, out string? existing)) { dependencies.Add(dependency, existing); return existing; }
            string alias = files.Count.ToString("x", CultureInfo.InvariantCulture) + extension;
            if (files.Count >= MaxFiles) throw new InvalidDataException("Portable model dependency count exceeds the supported budget.");
            AddBudget(ref total, resource.Length, limits.TotalBytes);
            files.Add(alias, resource); shared.Add(key, alias); dependencies.Add(dependency, alias); return alias;
        }
        string modelName;
        Resource modelResource;
        if (replacement is not null || Path.GetExtension(path).Equals(".pmx", StringComparison.OrdinalIgnoreCase))
        {
            var model = replacement ?? PmxReader.Read(source, new() { MaxInputBytes = limits.FileBytes });
            // Only textures a material binds are dependencies. An unreferenced table entry is
            // never loaded by the renderer, so it must not be copied into a shareable package
            // either: blank it to keep every material's texture index unchanged.
            var referenced = new HashSet<int>();
            foreach (var material in model.Materials)
            {
                referenced.Add(material.TextureIndex); referenced.Add(material.SphereTextureIndex);
                if (material.ToonMode == PmxToonMode.Separate) referenced.Add(material.ToonTextureIndex);
            }
            string[] textures = model.Textures.Select((name, index) => referenced.Contains(index) ? Copy(name) : "").ToArray();
            var copy = new PmxModel
            {
                Header = model.Header, Name = model.Name, EnglishName = model.EnglishName,
                Comment = model.Comment, EnglishComment = model.EnglishComment,
                Vertices = model.Vertices, Faces = model.Faces, Textures = textures,
                Materials = model.Materials, Bones = model.Bones, Morphs = model.Morphs,
                DisplayFrames = model.DisplayFrames, RigidBodies = model.RigidBodies,
                Joints = model.Joints, SoftBodies = model.SoftBodies,
            };
            modelName = "model.pmx";
            string temporary = capture.ModelTemporary();
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                PmxWriter.Write(output, copy, new PmxWriteOptions { MaxOutputBytes = limits.FileBytes });
            modelResource = SnapshotFile(temporary, limits.FileBytes, token);
        }
        else if (Path.GetExtension(path).Equals(".pmd", StringComparison.OrdinalIgnoreCase))
        {
            var model = PmdReader.Read(source);
            if (model.UnknownTailData.Length != 0 || model.UnparsedTailBytes != 0)
                throw new NotSupportedException("Portable PMD copying cannot discover dependencies in an unknown trailer.");
            var materials = model.Materials.Select(m => m with { Texture = string.Join('*', m.Texture.Split('*').Select(Copy)) }).ToArray();
            var usedToons = model.Materials.Where(m => m.ToonIndex != 255).Select(m => (int)m.ToonIndex).ToHashSet();
            var toons = model.ToonTextures.Select((name, index) =>
            {
                if (name.Length == 0) return name;
                bool common = Enumerable.Range(1, 10).Any(i => name.Equals($"toon{i:00}.bmp", StringComparison.OrdinalIgnoreCase));
                // Saba checks a model-local toon before the shared data directory. Preserve an
                // authored local override, even if its name happens to equal a built-in toon.
                string local = ContainedDependency(directory, name);
                RejectRedirects(local);
                if (common && !usedToons.Contains(index)) return name;
                return common && !File.Exists(local) ? name : Copy(name);
            }).ToArray();
            bool changedToons = !toons.SequenceEqual(model.ToonTextures);
            modelName = "model.pmd"; modelResource = Resource.FromBytes(PmdWriter.ToBytes(model with { Materials = materials, ToonTextures = toons,
                HasAnimationTail = model.HasAnimationTail || changedToons,
                HasToonExtension = model.HasToonExtension || changedToons,
                BoneDisplayFrames = !model.HasAnimationTail && changedToons ? [new("センター", [])] : model.BoneDisplayFrames }));
        }
        else throw new NotSupportedException("Portable copying supports PMX and PMD source models.");
        if (modelResource.Length > limits.FileBytes)
            throw new InvalidDataException("Portable model resources exceed the supported size.");
        AddBudget(ref total, modelResource.Length, limits.TotalBytes);
        files.Add(modelName, modelResource);
        var manifest = new Manifest
        {
            Version = 1, Model = modelName,
            Files = files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new Entry
            { Name = pair.Key, Length = pair.Value.Length, Sha256 = pair.Value.Sha256 }).ToArray(),
        };
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (manifestBytes.Length > MaxManifestBytes) throw new InvalidDataException("Portable model manifest exceeds its supported size.");
        token.ThrowIfCancellationRequested();
        return new(manifestBytes, files);
    }

    internal static void ValidateMetadata(string[]? hashes, IEnumerable<string> sources)
    {
        if (hashes is null) return;
        if (hashes.Length is < 1 or > SceneProjectStore.MaxObjects || hashes.Any(hash => !IsHash(hash))
            || hashes.Distinct(StringComparer.Ordinal).Count() != hashes.Length)
            throw new JsonException("Portable model bundles require distinct SHA256 identities.");
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (string source in sources)
        {
            string[] parts = source.Split('/');
            if (parts.Length != 4 || parts[0] != "assets" || parts[1] != "models" || !hashes.Contains(parts[2], StringComparer.Ordinal)
                || parts[3] is not ("model.pmx" or "model.pmd"))
                throw new JsonException("Portable objects must use a canonical bundled PMX/PMD path.");
            used.Add(parts[2]);
        }
        if (used.Count != hashes.Length) throw new JsonException("Portable model metadata contains an unused bundle.");
    }

    internal static void Verify(string parent, string[]? hashes, IEnumerable<string> sources, CancellationToken token, Limits? limits = null)
    {
        limits ??= Limits.Default;
        if (hashes is null) return;
        long total = 0;
        foreach (string hash in hashes)
        {
            string directory = Path.Combine(parent, "assets", "models", hash);
            byte[] bytes = Read(Path.Combine(directory, "manifest.json"), MaxManifestBytes, token);
            if (Hash(bytes) != hash) throw new InvalidDataException("Portable manifest does not match its bundle SHA256.");
            var manifest = JsonSerializer.Deserialize<Manifest>(bytes, JsonOptions) ?? throw new JsonException("Missing portable model manifest.");
            if (manifest.Version != 1 || manifest.Model is not ("model.pmx" or "model.pmd")
                || manifest.Files is null || manifest.Files.Length is < 1 or > MaxFiles + 1)
                throw new JsonException("Unsupported portable model manifest.");
            if (sources.Where(source => source.StartsWith($"assets/models/{hash}/", StringComparison.Ordinal))
                .Any(source => source != Relative(hash, manifest.Model)))
                throw new JsonException("Portable object model name does not match its manifest.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                if (file is null || string.IsNullOrEmpty(file.Name) || file.Name != Path.GetFileName(file.Name)
                    || file.Name.Any(c => c != '.' && !char.IsAsciiLetterOrDigit(c)) || file.Name is "." or ".." or "manifest.json"
                    || !names.Add(file.Name) || !IsHash(file.Sha256) || file.Length < 0 || file.Length > limits.FileBytes)
                    throw new JsonException("Invalid portable file metadata.");
                AddBudget(ref total, file.Length, limits.TotalBytes);
                var content = SnapshotFile(Path.Combine(directory, file.Name), limits.FileBytes, token);
                if (content.Length != file.Length || content.Sha256 != file.Sha256)
                    throw new InvalidDataException($"Portable resource '{file.Name}' does not match its saved length/SHA256.");
            }
            if (!names.Contains(manifest.Model)) throw new JsonException("Portable manifest does not contain its model.");
        }
    }

    private static string Relative(string hash, string name) => $"assets/models/{hash}/{name}";
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Store(string directory, string name, Resource resource, CancellationToken token)
    {
        string destination = Path.Combine(directory, name);
        RejectRedirects(destination);
        if (File.Exists(destination)) { VerifyExisting(directory, name, resource, token); return; }
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var input = resource.Open();
                if (Transfer(input, stream, resource.Length, token) != resource.Sha256)
                    throw new InvalidDataException("Portable source changed while copying; the resource will not be published.");
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested(); RejectRedirects(destination);
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination)) { VerifyExisting(directory, name, resource, token); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void VerifyExisting(string directory, string name, Resource resource, CancellationToken token)
    {
        string path = Path.Combine(directory, name); RejectRedirects(path);
        if (!File.Exists(path)) return;
        var current = SnapshotFile(path, resource.Length, token);
        if (current.Length != resource.Length || current.Sha256 != resource.Sha256)
            throw new InvalidDataException("Existing portable hash resource differs; it will not be overwritten.");
    }

    private static void AddBudget(ref long total, long length, long maximum)
    {
        if (length < 0 || length > maximum || total > maximum - length)
            throw new InvalidDataException("Portable model resources exceed the configured byte budget.");
        total += length;
    }

    private static FileStream OpenInput(string path, long maximum)
    {
        RejectRedirects(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum)
        { stream.Dispose(); throw new InvalidDataException($"Portable input exceeds {maximum} bytes: {path}"); }
        return stream;
    }

    private static Resource SnapshotFile(string path, long maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var input = OpenInput(path, maximum);
        long length = input.Length;
        string hash = Transfer(input, null, length, token);
        RejectRedirects(path);
        return new(path, null, length, hash);
    }

    // Reuses the framework incremental SHA256 implementation (dotnet/runtime, MIT); no file-sized byte array.
    private static string Transfer(Stream input, Stream? output, long length, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024]; long copied = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int count = input.Read(buffer);
            if (count == 0) break;
            if (copied > length - count) throw new InvalidDataException("Portable input grew beyond its declared length.");
            copied += count; hash.AppendData(buffer, 0, count); output?.Write(buffer, 0, count);
        }
        if (copied != length) throw new InvalidDataException("Portable input length changed.");
        token.ThrowIfCancellationRequested();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static byte[] Read(string path, int maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); RejectRedirects(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new InvalidDataException($"Portable input exceeds {maximum} bytes: {path}");
        using var output = new MemoryStream(); byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int count = stream.Read(buffer, 0, Math.Min(buffer.Length, maximum - (int)output.Length + 1));
            if (count == 0) break;
            if (output.Length + count > maximum) throw new InvalidDataException("Portable input grew beyond its supported size.");
            output.Write(buffer, 0, count);
        }
        RejectRedirects(path); token.ThrowIfCancellationRequested(); return output.ToArray();
    }

    /// <summary>
    /// Resolves a model-authored dependency the way the texture loader does: it must be a
    /// relative path that stays inside the model directory. Rooted, drive-qualified and escaping
    /// references are rejected so a crafted model cannot pull arbitrary local files into a package.
    /// </summary>
    internal static string ContainedDependency(string modelDirectory, string reference)
    {
        string normalized = reference.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw new InvalidDataException($"Model dependency '{reference}' must be a relative path inside the model directory.");
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelDirectory)) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(folder, normalized));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(folder, comparison))
            throw new InvalidDataException($"Model dependency '{reference}' points outside the model directory.");
        return path;
    }

    private static void RejectRedirects(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Portable model paths must not redirect through reparse points: {current}");
        }
    }

    private sealed class Manifest
    {
        public required int Version { get; init; }
        public required string Model { get; init; }
        public required Entry[] Files { get; init; }
    }
    private sealed class Entry
    {
        public required string Name { get; init; }
        public required long Length { get; init; }
        public required string Sha256 { get; init; }
    }
}
