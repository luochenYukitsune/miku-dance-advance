using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Formats;
using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

/// <summary>Single-file native project. ZipArchive supplies ZIP64; immutable logical files share SHA256 blobs.
/// A load validates every entry and digest before invoking a host model loader or creating a solver.</summary>
public static class PmaProjectStore
{
    /// <summary>Checks the union of supplied save-only logical resource lengths and retained package
    /// resources before a consuming module allocates image copies. Does not read bytes or edit the project.</summary>
    public static void ValidateAdditionalResourceBudget(SceneProjectSession project,
        IReadOnlyDictionary<string, long> resourceLengths, PmaLoadOptions? limits = null)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(resourceLengths);
        _ = project.Objects.Count; // Validate owner lifetime without sampling animation or physics.
        limits ??= new();
        PmaAdditionalResources.ValidateBudget(resourceLengths, project.PmaRetention, limits, limits.ContentSelection);
    }

    public const string Format = "mmd-modern-pma";
    public const int Version = 1;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    };

    public static void Save(string path, SceneProjectSession project, PmaSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        var snapshot = CaptureSnapshot(project, options, cancellationToken);
        snapshot.SaveAndDispose(path, cancellationToken);
    }

    /// <summary>Captures authored project state on its owner without seeking or advancing physics.
    /// The detached capture can be saved after editing or disposing the source project. Dispose it to
    /// release any extracted-package resources. External model/texture files remain ordinary save inputs.</summary>
    public static PmaSaveSnapshot CaptureSnapshot(SceneProjectSession project, PmaSaveOptions? options = null,
        CancellationToken cancellationToken = default)
        => CaptureSnapshot(project, options, cancellationToken, PmaPackageKind.Modern);

    internal static PmaSaveSnapshot CaptureSnapshot(SceneProjectSession project, PmaSaveOptions? options,
        CancellationToken cancellationToken, PmaPackageKind kind)
    {
        ArgumentNullException.ThrowIfNull(project); options ??= new();
        ArgumentNullException.ThrowIfNull(options.ModelOverrides); ArgumentNullException.ThrowIfNull(options.OptionalExtensions);
        ArgumentNullException.ThrowIfNull(options.ValidationOptions); options.ValidationOptions.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (kind != PmaPackageKind.Classic && options.AccessoryPersistence is not null)
            throw new NotSupportedException("Independent accessory persistence is supported only through an explicit Classic package adapter.");
        var captured = project.CapturePmaSaveInputs(options.AccessoryPersistence);
        IDisposable? lease = captured.Retained?.AcquireLease();
        try
        {
            var selection = options.ContentSelection?.Copy();
            if (selection is not null) captured = selection.Project(captured);
            var modelOverrides = options.ModelOverrides.ToDictionary(p => p.Key,
                p => p.Value ?? throw new ArgumentException("PMX override cannot be null."));
            var objectIds = captured.Composition.Objects.Select(o => o.Id).ToHashSet();
            if (modelOverrides.Keys.Any(id => !objectIds.Contains(id))) throw new ArgumentException("PMX override refers to an unknown actor.");
            var frozenOptions = options with
            {
                ModelOverrides = modelOverrides,
                ContentSelection = selection,
                ValidationOptions = options.ValidationOptions with { ContentSelection = options.ValidationOptions.ContentSelection?.Copy() },
                OptionalExtensions = new Dictionary<string, JsonElement>(),
                AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>>(),
                AccessoryPersistence = null,
            };
            var additionalResources = PmaAdditionalResources.Snapshot(options with { ContentSelection = selection }, captured.Retained, cancellationToken);
            var extensions = captured.Retained?.Extensions.Where(p => selection?.ExcludesExtension(p.Key) != true)
                .ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal) ?? [];
            extensions.Remove(SceneRenderingStyles.ExtensionName);
            if (selection?.ExcludesExtension(SceneRenderingStyles.ExtensionName) != true && captured.RenderingStyles.Serialize() is { } styles)
                extensions[SceneRenderingStyles.ExtensionName] = styles;
            foreach (var pair in options.OptionalExtensions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateExtensionName(pair.Key);
                if (selection?.ExcludesExtension(pair.Key) != true) extensions[pair.Key] = pair.Value.Clone();
            }
            if (extensions.TryGetValue(SceneRenderingStyles.ExtensionName, out var selectedStyles))
                _ = PmaRenderingStyleState.Parse(selectedStyles, objectIds);
            options.AccessoryPersistence?.ValidateSelected(extensions, additionalResources, captured.Retained, selection, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Guid projectId = captured.ProjectIdentity ?? captured.Retained?.ProjectId ?? Guid.NewGuid();
            var snapshot = new PmaSaveSnapshot(new(captured, frozenOptions, additionalResources, extensions, projectId, lease, kind));
            // Establish the session identity on its owner. Background saves never touch the live session,
            // and successive recovery captures of an unnamed project still share one stable identity.
            project.PmaProjectIdentity = projectId;
            return snapshot;
        }
        catch (Exception exception)
        {
            PmaSaveCleanup.Complete(exception, null, () => lease?.Dispose());
            throw;
        }
    }

    internal static void SaveSnapshot(string path, PmaSaveData captured, CancellationToken cancellationToken)
    {
        var project = captured.Project;
        var options = captured.Options;
        string destination = PackagePath(path, captured.Kind), parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        cancellationToken.ThrowIfCancellationRequested();
        string stage = FreshDirectory(options.CacheDirectory);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Exception? failure = null;
        bool committed = false;
        try
        {
            var models = SceneProjectSession.StagePma(stage, project, options, cancellationToken);
            PmaAdditionalResources.Stage(stage, project.Retained, captured.AdditionalResources, options.ContentSelection, cancellationToken);
            var files = new List<FileData>();
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            long logicalBytes = 0;
            foreach (string file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string logical = Path.GetRelativePath(stage, file).Replace('\\', '/'); ValidatePath(logical);
                using var input = File.OpenRead(file);
                long length = input.Length;
                if (length > options.ValidationOptions.MaxEntryBytes || files.Count >= options.ValidationOptions.MaxFiles
                    || length > options.ValidationOptions.MaxExpandedBytes - logicalBytes)
                    throw new InvalidDataException("PMA staged resources exceed the configured file or expanded-byte budget.");
                logicalBytes += length;
                string hash = Hash(input, cancellationToken, length);
                files.Add(new() { Path = logical, Length = input.Length, Sha256 = hash }); sources.TryAdd(hash, file);
            }
            var manifest = new Manifest
            {
                Format = PackageFormat(captured.Kind), Version = Version, MinimumReaderVersion = Version, Producer = "Mmd.Modern",
                ProjectId = captured.ProjectId, ProjectPath = "project.mmdscene",
                ProjectSchema = project.Animation.Motions.Any(m => m.Detachment is not null) || project.Animation.PmmControls?.HasReplacementMetadata == true
                    ? SceneAnimationProjectStore.ModelReplacementVersion : SceneAnimationProjectStore.PmmControlsVersion,
                Files = files.ToArray(), Models = models, RequiredExtensions = [], OptionalExtensions = captured.Extensions,
            };
            ValidateProjectReferences(stage, manifest, options.ValidationOptions);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
            if (bytes.Length > options.ValidationOptions.MaxManifestBytes) throw new InvalidDataException("PMA manifest exceeds its supported size.");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    using (var entry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal).Open()) entry.Write(bytes);
                    foreach (var pair in sources.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        using var input = File.OpenRead(pair.Value);
                        using var entry = zip.CreateEntry("blobs/" + pair.Key, CompressionLevel.Optimal).Open();
                        Copy(input, entry, cancellationToken);
                    }
                }
                output.Flush(flushToDisk: true);
            }
            // Validate the completed archive, without constructing a second animation/physics session.
            using (var input = File.OpenRead(temporary))
            {
                if (input.Length > options.ValidationOptions.MaxPackageBytes) throw new InvalidDataException("PMA package exceeds its input budget.");
                using var zip = new ZipArchive(input, ZipArchiveMode.Read);
                ValidateArchive(zip, options.ValidationOptions, cancellationToken, captured.Kind);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            committed = true;
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            PmaSaveCleanup.Complete(failure, committed ? destination : null,
                () => { if (File.Exists(temporary)) File.Delete(temporary); },
                () => { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); });
        }
    }

    public static PmaLoadedProject Load(string path, string cacheDirectory,
        Func<string, CancellationToken, SceneAnimationModelAsset> loadModel, IProjectPhysicsBackendProvider physics,
        PmaLoadOptions? options = null, CancellationToken cancellationToken = default)
        => Load(path, cacheDirectory, loadModel, physics, options, cancellationToken, PmaPackageKind.Modern);

    /// <summary>Reads the native package identity for startup routing, before selecting a renderer.
    /// Reuses the bounded archive/manifest checks without extracting or constructing models/physics.
    /// Blob digests are still verified by the normal Load path; this is not an integrity acceptance.</summary>
    public static string InspectPackageFormat(string path, PmaLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var kind = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pma" => PmaPackageKind.Modern,
            ".pmc" => PmaPackageKind.Classic,
            _ => throw new ArgumentException("Native projects require a .pma or .pmc extension.", nameof(path))
        };
        options ??= new(); options.Validate(); cancellationToken.ThrowIfCancellationRequested();
        using var input = File.OpenRead(PackagePath(path, kind));
        if (input.Length > options.MaxPackageBytes) throw new InvalidDataException("PMA package exceeds its input budget.");
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        return ValidateArchive(zip, options, cancellationToken, kind, verifyBlobDigests: false).Format;
    }

    internal static PmaLoadedProject Load(string path, string cacheDirectory,
        Func<string, CancellationToken, SceneAnimationModelAsset> loadModel, IProjectPhysicsBackendProvider physics,
        PmaLoadOptions? options, CancellationToken cancellationToken, PmaPackageKind kind)
    {
        ArgumentNullException.ThrowIfNull(loadModel); ArgumentNullException.ThrowIfNull(physics);
        options ??= new(); options.Validate(); cancellationToken.ThrowIfCancellationRequested();
        if (kind != PmaPackageKind.Classic && options.AccessoryPreparation is not null)
            throw new NotSupportedException("Independent accessory preparation is supported only through an explicit Classic package adapter.");
        using var input = File.OpenRead(PackagePath(path, kind));
        if (input.Length > options.MaxPackageBytes) throw new InvalidDataException("PMA package exceeds its input budget.");
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var manifest = ValidateArchive(zip, options, cancellationToken, kind);
        string stage = FreshDirectory(cacheDirectory);
        SceneProjectSession? session = null;
        PmaResourceReader? resourceReader = null;
        try
        {
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = Path.Combine(stage, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = zip.GetEntry("blobs/" + file.Sha256)!.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                Copy(source, output, cancellationToken, file.Length);
            }
            ValidateProjectReferences(stage, manifest, options);
            resourceReader = new(stage, manifest.Files.Where(f => f.Path.StartsWith(PmaAdditionalResources.Prefix, StringComparison.Ordinal))
                .Select(f => new PmaAdditionalResource(f.Path, f.Length, f.Sha256)).ToArray());
            var retained = new PmaRetainedState(manifest.ProjectId, stage, manifest.Models,
                manifest.OptionalExtensions.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal),
                resourceReader);
            PmaAccessoryPersistenceCapture? accessoryPersistence = null;
            if (options.PackagePreflight is not null || options.AccessoryPreparation is not null)
            {
                var context = new PmaPackageReadContext(manifest.Format, retained);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    options.PackagePreflight?.Invoke(context, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    accessoryPersistence = options.AccessoryPreparation?.Invoke(context, cancellationToken);
                    accessoryPersistence?.ValidatePackage(retained);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                finally { context.Invalidate(); }
            }
            Func<string, CancellationToken, SceneAnimationModelAsset> effectiveLoader = loadModel;
            int transformed = 0; (Guid Id, string Path)[] expectedModels = [];
            if (options.ModelAssetTransform is { } transform)
            {
                // SceneProjectStore.Prepare invokes its loader once for each layout object, in document order.
                // Preserve that explicit instance identity: different actors may share the same PMX path.
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, manifest.ProjectPath)));
                expectedModels = document.RootElement.GetProperty("layout").GetProperty("objects").EnumerateArray()
                    .Select(o => (o.GetProperty("id").GetGuid(), Path.GetFullPath(o.GetProperty("sourcePath").GetString()!, stage))).ToArray();
                var extensions = new System.Collections.ObjectModel.ReadOnlyDictionary<string, JsonElement>(retained.Extensions);
                effectiveLoader = (sourcePath, token) =>
                {
                    if (transformed >= expectedModels.Length || !Path.GetFullPath(sourcePath).Equals(expectedModels[transformed].Path, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("PMA model loader order disagrees with the validated layout.");
                    var expected = expectedModels[transformed++];
                    var asset = loadModel(sourcePath, token);
                    if (asset?.Model is null || asset.Source is null) throw new InvalidDataException("The host must supply a PMX model and its static source.");
                    token.ThrowIfCancellationRequested();
                    var decorated = transform(new(expected.Id, resourceReader, extensions), asset);
                    if (decorated?.Source is null || !ReferenceEquals(decorated.Model, asset.Model))
                        throw new InvalidDataException("A PMA model resource transform must retain the host PMX model instance and supply a source frame.");
                    token.ThrowIfCancellationRequested(); return decorated;
                };
            }
            session = SceneProjectSession.LoadPma(Path.Combine(stage, manifest.ProjectPath), effectiveLoader, physics,
                PortableModelAssets.Limits.FromPma(options), cancellationToken, accessoryPersistence);
            if (options.ModelAssetTransform is not null && transformed != expectedModels.Length)
                throw new InvalidDataException("PMA model loader did not visit every validated instance.");
            session.PmaRetention = retained;
            session.AdoptPmaRenderingStyles(manifest.OptionalExtensions.TryGetValue(SceneRenderingStyles.ExtensionName, out var styles)
                ? PmaRenderingStyleState.Parse(styles, manifest.Models.Select(m => m.ObjectId).ToHashSet()) : PmaRenderingStyleState.Default);
            return new(session, retained, manifest.Format);
        }
        catch (Exception exception)
        {
            PmaSaveCleanup.Complete(exception, null,
                () => resourceReader?.Dispose(),
                () => session?.Dispose(),
                () => { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); });
            throw;
        }
    }

    private static Manifest ValidateArchive(ZipArchive zip, PmaLoadOptions options, CancellationToken token, PmaPackageKind kind,
        bool verifyBlobDigests = true)
    {
        options.Validate();
        if (zip.Entries.Count > options.MaxFiles + 1L) throw new InvalidDataException("PMA contains too many archive entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!names.Add(entry.FullName) || (entry.FullName != "manifest.json" &&
                !(entry.FullName.StartsWith("blobs/", StringComparison.Ordinal) && IsHash(entry.FullName[6..]))))
                throw new InvalidDataException("PMA contains duplicate or noncanonical archive entries.");
            if (entry.Length < 0 || entry.Length > (entry.FullName == "manifest.json" ? options.MaxManifestBytes : options.MaxEntryBytes))
                throw new InvalidDataException("PMA entry exceeds its expanded budget.");
            expanded = checked(expanded + entry.Length);
            if (expanded > options.MaxExpandedBytes) throw new InvalidDataException("PMA exceeds its expanded budget.");
        }
        var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("PMA manifest is missing.");
        using var manifestInput = manifestEntry.Open(); using var manifestBuffer = new MemoryStream();
        Copy(manifestInput, manifestBuffer, token, manifestEntry.Length);
        var manifest = JsonSerializer.Deserialize<Manifest>(manifestBuffer.ToArray(), Json) ?? throw new InvalidDataException("PMA manifest is empty.");
        if (manifest.Format != PackageFormat(kind) || manifest.Version != Version || manifest.MinimumReaderVersion is < 1 or > Version
            || manifest.ProjectSchema is < SceneAnimationProjectStore.Version or > SceneAnimationProjectStore.ModelReplacementVersion
            || manifest.ProjectId == Guid.Empty || manifest.ProjectPath != "project.mmdscene"
            || string.IsNullOrWhiteSpace(manifest.Producer) || manifest.Files is null || manifest.Models is null
            || manifest.RequiredExtensions is null || manifest.OptionalExtensions is null)
            throw new NotSupportedException("Unsupported PMA format, reader version or project section schema.");
        // No plugin code is needed or executed by this reader. Unknown required capabilities are rejected.
        if (manifest.RequiredExtensions.Length != 0) throw new NotSupportedException("PMA requires unknown extensions: " + string.Join(", ", manifest.RequiredExtensions));
        foreach (var extension in manifest.OptionalExtensions.Keys) ValidateExtensionName(extension);
        if (manifest.Files.Length is < 1 || manifest.Files.Length > options.MaxFiles) throw new InvalidDataException("PMA file count exceeds its budget.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blobs = new Dictionary<string, long>(StringComparer.Ordinal);
        long logicalBytes = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null) throw new InvalidDataException("Null PMA file record.");
            ValidatePath(file.Path);
            if (file.Path.StartsWith(PmaAdditionalResources.Prefix, StringComparison.OrdinalIgnoreCase))
                PmaAdditionalResources.ValidatePath(file.Path);
            if (!paths.Add(file.Path) || !IsHash(file.Sha256) || file.Length < 0 || file.Length > options.MaxEntryBytes)
                throw new InvalidDataException("Invalid PMA file identity, length or digest.");
            // The project JSON has a stricter schema budget than opaque blobs. Reject
            // before extraction and File.ReadAllText/JsonDocument allocate its text.
            if (string.Equals(file.Path, manifest.ProjectPath, StringComparison.OrdinalIgnoreCase)
                && file.Length > SceneAnimationProjectStore.MaxDocumentBytes)
                throw new InvalidDataException("PMA project section exceeds the supported document size.");
            logicalBytes = checked(logicalBytes + file.Length);
            if (logicalBytes > options.MaxExpandedBytes) throw new InvalidDataException("PMA logical extraction exceeds its budget.");
            var entry = zip.GetEntry("blobs/" + file.Sha256);
            if (entry is null || entry.Length != file.Length) throw new InvalidDataException("PMA blob is missing or has the wrong length.");
            if (blobs.TryGetValue(file.Sha256, out long previous) && previous != file.Length) throw new InvalidDataException("Conflicting PMA blob lengths.");
            blobs[file.Sha256] = file.Length;
        }
        if (!paths.Contains(manifest.ProjectPath) || zip.Entries.Count != blobs.Count + 1)
            throw new InvalidDataException("PMA contains missing project data or unreferenced blobs.");
        var ids = new HashSet<Guid>();
        foreach (var model in manifest.Models)
        {
            if (model is null || model.ObjectId == Guid.Empty || !ids.Add(model.ObjectId)) throw new InvalidDataException("Invalid PMA model identity.");
            foreach (string path in new[] { model.OriginalSourcePath, model.OriginalModelPath, model.EditedModelPath })
            { ValidatePath(path); if (!paths.Contains(path)) throw new InvalidDataException("PMA model version refers to a missing file."); }
        }
        if (manifest.OptionalExtensions.TryGetValue(SceneRenderingStyles.ExtensionName, out var styles))
            _ = PmaRenderingStyleState.Parse(styles, ids);
        foreach (string hash in verifyBlobDigests ? blobs.Keys : Enumerable.Empty<string>())
        {
            using var source = zip.GetEntry("blobs/" + hash)!.Open();
            if (Hash(source, token, blobs[hash]) != hash) throw new InvalidDataException("PMA resource does not match its SHA256.");
        }
        return manifest;
    }

    private static void ValidateProjectReferences(string stage, Manifest manifest, PmaLoadOptions limits)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, manifest.ProjectPath)), new() { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != SceneAnimationProjectStore.Format || root.GetProperty("version").GetInt32() > manifest.ProjectSchema)
            throw new InvalidDataException("PMA project section disagrees with its declared schema.");
        var files = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var models = manifest.Models.ToDictionary(m => m.ObjectId);
        var objects = root.GetProperty("layout").GetProperty("objects");
        if (objects.GetArrayLength() != models.Count) throw new InvalidDataException("PMA model versions must cover every project actor.");
        foreach (var item in objects.EnumerateArray())
        {
            string path = item.GetProperty("sourcePath").GetString()!; ValidatePath(path);
            if (!files.Contains(path) || !models.TryGetValue(item.GetProperty("id").GetGuid(), out var model) || model.EditedModelPath != path)
                throw new InvalidDataException("PMA actor must reference its packaged edited model version.");
            ValidateModelDependencies(stage, path, files, limits);
        }
        void RequireResource(JsonElement resource)
        {
            string path = resource.GetProperty("assetPath").GetString()!; ValidatePath(path);
            if (!files.Contains(path)) throw new InvalidDataException("PMA motion or media refers to a missing packaged resource.");
        }
        foreach (var item in root.GetProperty("motions").EnumerateArray()) RequireResource(item);
        if (root.TryGetProperty("sceneMotion", out var motion) && motion.ValueKind != JsonValueKind.Null) RequireResource(motion);
        if (root.TryGetProperty("media", out var media) && media.ValueKind != JsonValueKind.Null)
            foreach (var item in media.EnumerateArray()) RequireResource(item);
        if (limits.ContentSelection is { } selection) ValidateContentSelection(root, manifest, selection);
    }

    private static void ValidateContentSelection(JsonElement project, Manifest manifest, PmaContentSelection selection)
    {
        if (manifest.OptionalExtensions.Keys.Any(selection.ExcludesExtension))
            throw new InvalidDataException("Package contains an extension excluded by its content selection.");
        if (manifest.Files.Any(file => selection.ExcludesResource(file.Path)))
            throw new InvalidDataException("Package contains a resource excluded by its content selection.");
        var layout = project.GetProperty("layout");
        if (!selection.RetainNativeLighting && (layout.GetProperty("lights").GetArrayLength() != 0
            || layout.GetProperty("selectedLightId").ValueKind != JsonValueKind.Null
            || (layout.TryGetProperty("groups", out var groups) && groups.ValueKind != JsonValueKind.Null && groups.GetArrayLength() != 0)
            || layout.TryGetProperty("settings", out _)))
            throw new InvalidDataException("Package contains native lighting excluded by its content selection.");
        if (!selection.RetainModelPurposes)
            foreach (var item in layout.GetProperty("objects").EnumerateArray())
            {
                var purpose = item.GetProperty("purpose");
                if (!string.Equals(purpose.GetProperty("role").GetString(), nameof(ModelRole.Unspecified), StringComparison.OrdinalIgnoreCase)
                    || purpose.GetProperty("environmentIndoor").ValueKind != JsonValueKind.Null)
                    throw new InvalidDataException("Package contains model purposes excluded by its content selection.");
            }
    }

    private static void ValidateModelDependencies(string stage, string path, HashSet<string> files, PmaLoadOptions limits)
    {
        string parent = path[..(path.LastIndexOf('/') + 1)];
        void RequireTexture(string reference, bool sharedToon = false)
        {
            if (reference.Length == 0) return;
            ValidatePath(reference);
            if (files.Contains(parent + reference)) return;
            if (sharedToon && Enumerable.Range(1, 10).Any(i => reference.Equals($"toon{i:00}.bmp", StringComparison.OrdinalIgnoreCase))) return;
            throw new InvalidDataException("PMA model texture refers outside its packaged resources.");
        }
        using var input = File.OpenRead(Path.Combine(stage, path));
        if (Path.GetExtension(path) == ".pmx")
        {
            var model = PmxReader.Read(input, new() { MaxInputBytes = limits.MaxEntryBytes });
            foreach (string reference in model.Textures) RequireTexture(reference);
        }
        else if (Path.GetExtension(path) == ".pmd")
        {
            var model = PmdReader.Read(input);
            if (model.UnknownTailData.Length != 0 || model.UnparsedTailBytes != 0) throw new NotSupportedException("Unknown PMD trailer dependencies cannot be validated.");
            foreach (var material in model.Materials)
            {
                foreach (string reference in material.Texture.Split('*')) RequireTexture(reference);
                if (material.ToonIndex < model.ToonTextures.Length) RequireTexture(model.ToonTextures[material.ToonIndex], sharedToon: true);
            }
        }
        else throw new NotSupportedException("PMA models must be packaged PMX or PMD assets.");
    }

    internal static void CopyRetainedModel(string source, string destination, PmaModelVersion model, CancellationToken token, long maximum = long.MaxValue)
    {
        CopyFile(Path.Combine(source, model.OriginalSourcePath), Path.Combine(destination, model.OriginalSourcePath), token, maximum);
        string directory = Path.GetDirectoryName(model.OriginalModelPath)!;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(source, directory)))
            CopyFile(file, Path.Combine(destination, directory, Path.GetFileName(file)), token, maximum);
    }

    internal static void CopyFile(string source, string destination, CancellationToken token, long maximum = long.MaxValue)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = File.OpenRead(source);
        if (input.Length > maximum) throw new InvalidDataException("PMA source resource exceeds the configured entry budget.");
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        Copy(input, output, token, input.Length);
    }

    private static string FreshDirectory(string parent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);
        // Unique children belong exclusively to this operation; never delete the caller's cache root.
        string path = Path.Combine(Path.GetFullPath(parent), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static string PackageFormat(PmaPackageKind kind) => kind switch
    {
        PmaPackageKind.Modern => Format,
        PmaPackageKind.Classic => PmcProjectStore.Format,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
    internal static string PackagePath(string path, PmaPackageKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string extension = kind switch
        {
            PmaPackageKind.Modern => ".pma",
            PmaPackageKind.Classic => ".pmc",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"Use a {extension} project path.", nameof(path));
        return Path.GetFullPath(path);
    }
    private static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 512 || path.Contains('\\') || path.Split('/').Any(p => p.Length == 0 || p is "." or ".."
            || p.EndsWith('.') || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')) || PmaAdditionalResources.IsDeviceName(p)))
            throw new InvalidDataException("PMA logical paths must be canonical relative ASCII paths.");
    }
    private static void ValidateExtensionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || !name.Contains('.') || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new InvalidDataException("PMA extension names must be namespaced ASCII identifiers.");
    }
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hash(Stream input, CancellationToken token, long maximum = long.MaxValue)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024]; long length = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested(); int count = input.Read(buffer);
            if (count == 0) break;
            length = checked(length + count); if (length > maximum) throw new InvalidDataException("PMA entry expanded beyond its declared length.");
            hash.AppendData(buffer, 0, count);
        }
        if (maximum != long.MaxValue && length != maximum) throw new InvalidDataException("Truncated PMA resource.");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static void Copy(Stream input, Stream output, CancellationToken token, long maximum = long.MaxValue)
    {
        byte[] buffer = new byte[64 * 1024]; long length = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested(); int count = input.Read(buffer);
            if (count == 0) break;
            length = checked(length + count); if (length > maximum) throw new InvalidDataException("PMA entry expanded beyond its declared length.");
            output.Write(buffer, 0, count);
        }
        if (maximum != long.MaxValue && length != maximum) throw new InvalidDataException("Truncated PMA resource.");
    }
    private sealed record Manifest
    {
        public required string Format { get; init; }
        public required int Version { get; init; }
        public required int MinimumReaderVersion { get; init; }
        public required string Producer { get; init; }
        public required Guid ProjectId { get; init; }
        public required string ProjectPath { get; init; }
        public required int ProjectSchema { get; init; }
        public required FileData[] Files { get; init; }
        public required PmaModelVersion[] Models { get; init; }
        public required string[] RequiredExtensions { get; init; }
        public required Dictionary<string, JsonElement> OptionalExtensions { get; init; }
    }
    private sealed record FileData
    {
        public required string Path { get; init; }
        public required long Length { get; init; }
        public required string Sha256 { get; init; }
    }
}
