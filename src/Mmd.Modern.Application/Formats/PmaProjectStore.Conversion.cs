using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mmd.Modern.Application;

/// <summary>Engine-specific fields archived in the output and whether a previous archive was restored or deferred.</summary>
public sealed record PmaConversionReport(int OmittedLights, int OmittedGroups, bool OmittedLightingSettings,
    int OmittedModelPurposes, IReadOnlyList<string> OmittedExtensions, IReadOnlyList<string> OmittedResources,
    bool RestoredArchive, bool DeferredArchive);

public static partial class PmaProjectStore
{
    public const string ConversionExtensionName = "org.mda.project-conversion";
    private const string ClassicToonExtension = "org.mda.classic-toons";
    private const string ClassicToonResources = "render-resources/classic-toons/";
    private static readonly PmaContentSelection ModernFeatures = new(
        ["org.mda.model-material-bindings"], ["render-resources/material-bindings/", "render-resources/generated-materials/",
            "render-resources/generated-styles/"], retainNativeLighting: false, retainModelPurposes: false, retainRenderingStyles: false);
    private static readonly PmaContentSelection ClassicFeatures = new(
        ["org.mda.traditional-accessory-assets", "org.mda.traditional-effect-tree", "org.mda.traditional-effect-project",
            "org.mda.traditional-emm", ClassicToonExtension], ["render-resources/traditional/accessory-assets/",
            "render-resources/traditional/effect-tree/", "render-resources/traditional/emm/", ClassicToonResources]);

    private sealed record ArchiveIndex(int Version, Guid[] Entries);
    private sealed record EngineArchive(string Format, Guid ProjectId, string[] Models, JsonObject? Lighting,
        Dictionary<Guid, JsonObject> Purposes, Dictionary<string, JsonElement> Extensions, string[] Resources);

    /// <summary>The caller owns the detached snapshot. The temporary source and output have separate publication boundaries.</summary>
    public static PmaConversionReport ConvertSnapshot(PmaSaveSnapshot snapshot, string destinationPath, string cacheDirectory,
        CancellationToken cancellationToken = default, PmaLoadOptions? limits = null,
        Func<CancellationToken, PmaSaveOptions?>? prepareMissingClassicToons = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string destination = PackagePath(destinationPath, snapshot.Format == Format ? PmaPackageKind.Classic : PmaPackageKind.Modern);
        if (File.Exists(destination)) throw new IOException("The conversion destination already exists.");
        cancellationToken.ThrowIfCancellationRequested();
        string stage = FreshDirectory(cacheDirectory);
        string? published = null;
        Exception? failure = null;
        try
        {
            string source = Path.Combine(stage, "source" + snapshot.FileExtension);
            try { snapshot.Save(source, cancellationToken); }
            catch (PmaSaveCommittedException error)
            { throw new IOException("The temporary source snapshot could not be finalized.", error); }
            var result = ConvertPackage(source, destination, stage, cancellationToken, limits, prepareMissingClassicToons);
            published = destination;
            return result;
        }
        catch (Exception error) { failure = error; throw; }
        finally { PmaSaveCleanup.Complete(failure, published, () => Directory.Delete(stage, true)); }
    }

    /// <summary>Creates a new opposite-engine package. Common files retain their content hashes. Engine-specific data
    /// uses the existing opaque resource transport so that normal editor saves carry it across a round trip.</summary>
    public static PmaConversionReport ConvertPackage(string sourcePath, string destinationPath, string cacheDirectory,
        CancellationToken cancellationToken = default, PmaLoadOptions? limits = null,
        Func<CancellationToken, PmaSaveOptions?>? prepareMissingClassicToons = null)
    {
        var sourceKind = string.Equals(Path.GetExtension(sourcePath), ".pmc", StringComparison.OrdinalIgnoreCase)
            ? PmaPackageKind.Classic : PmaPackageKind.Modern;
        var targetKind = sourceKind == PmaPackageKind.Classic ? PmaPackageKind.Modern : PmaPackageKind.Classic;
        string source = PackagePath(sourcePath, sourceKind), destination = PackagePath(destinationPath, targetKind);
        if (File.Exists(destination)) throw new IOException("The conversion destination already exists.");
        limits ??= new();
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        using var input = File.OpenRead(source);
        if (input.Length > limits.MaxPackageBytes) throw new InvalidDataException("Project package exceeds its input budget.");
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var sourceManifest = ValidateArchive(zip, limits, cancellationToken, sourceKind);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string stage = FreshDirectory(cacheDirectory);
        bool published = false;
        Exception? failure = null;
        try
        {
            ExtractFiles(zip, sourceManifest, stage, cancellationToken);
            bool requiresOriginalToons = ValidateProjectReferences(stage, sourceManifest, limits);
            string projectPath = Path.Combine(stage, sourceManifest.ProjectPath);
            var project = JsonNode.Parse(File.ReadAllText(projectPath))!.AsObject();
            SceneAnimationProjectStore.ValidatePackageDocument(JsonSerializer.SerializeToElement(project), projectPath,
                PortableModelAssets.Limits.FromPma(limits), cancellationToken);
            var files = sourceManifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            var manifest = sourceManifest with { Format = PackageFormat(targetKind),
                OptionalExtensions = new(sourceManifest.OptionalExtensions, StringComparer.Ordinal) };
            var report = ProjectEngineData(manifest, files, project["layout"]!.AsObject(), stage, sourceKind, targetKind, limits, cancellationToken);
            var convertedDocument = JsonSerializer.SerializeToElement(project);
            int accessories = SceneAnimationProjectStore.ValidatePackageDocument(convertedDocument, projectPath);
            if (targetKind == PmaPackageKind.Classic && accessories > 0 &&
                !manifest.OptionalExtensions.ContainsKey("org.mda.traditional-accessory-assets"))
                throw new NotSupportedException("Classic conversion requires the original .x accessory assets and textures for the PMM accessory tracks.");
            if (targetKind == PmaPackageKind.Classic && requiresOriginalToons && !manifest.OptionalExtensions.ContainsKey(ClassicToonExtension))
                AddClassicToons(prepareMissingClassicToons?.Invoke(cancellationToken), manifest, files,
                    stage, limits, cancellationToken);
            files[manifest.ProjectPath] = WriteJsonFile(stage, manifest.ProjectPath, project, limits.MaxEntryBytes);
            manifest = manifest with { Files = files.Values.ToArray() };
            ValidateProjectReferences(stage, manifest, limits);
            if (targetKind == PmaPackageKind.Classic)
                ValidateContentSelection(convertedDocument, manifest, ModernFeatures);
            var sources = files.Values.DistinctBy(file => file.Sha256)
                .ToDictionary(file => file.Sha256, file => Path.Combine(stage, file.Path), StringComparer.Ordinal);
            WriteArchive(temporary, manifest, sources, limits, targetKind, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
            published = true;
            return report;
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            PmaSaveCleanup.Complete(failure, published ? destination : null, () => File.Delete(temporary), () => Directory.Delete(stage, true));
        }
    }

    // The Classic codec supplies its validated, frozen manifest and images. Never replace an existing
    // archive binding or let a supplemental provider alter the shared scene or another extension.
    private static void AddClassicToons(PmaSaveOptions? supplied, Manifest manifest,
        Dictionary<string, FileData> files, string stage, PmaLoadOptions limits, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (supplied is null) throw new NotSupportedException("The Classic project requires original MMD shared or default Toon images.");
        if (supplied.OptionalExtensions.Count != 1 || !supplied.OptionalExtensions.TryGetValue(ClassicToonExtension, out var extension)
            || extension.ValueKind != JsonValueKind.Object || supplied.AdditionalResources.Count == 0)
            throw new InvalidDataException("The Classic Toon provider must supply its binding manifest and required images.");
        manifest.OptionalExtensions.Add(ClassicToonExtension, extension.Clone());
        long total = files.Values.Sum(file => file.Length);
        foreach (var (path, bytes) in supplied.AdditionalResources)
        {
            token.ThrowIfCancellationRequested();
            PmaAdditionalResources.ValidatePath(path);
            if (!path.StartsWith(ClassicToonResources, StringComparison.Ordinal) || bytes.Length > limits.MaxEntryBytes)
                throw new InvalidDataException("A supplemental Toon resource is outside its namespace or exceeds the file budget.");
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
            if (files.TryGetValue(path, out var existing))
            {
                if (existing.Path != path || existing.Sha256 != hash)
                    throw new InvalidDataException("A supplemental Toon image conflicts with an existing resource.");
                continue;
            }
            if (files.Count >= limits.MaxFiles || bytes.Length > limits.MaxExpandedBytes - total)
                throw new InvalidDataException("Supplemental Toon images exceed the package budget.");
            string target = Path.Combine(stage, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(bytes.Span);
            files.Add(path, new() { Path = path, Length = bytes.Length, Sha256 = hash });
            total += bytes.Length;
        }
    }

    private static PmaConversionReport ProjectEngineData(Manifest manifest, Dictionary<string, FileData> files, JsonObject layout,
        string stage, PmaPackageKind sourceKind, PmaPackageKind targetKind, PmaLoadOptions limits, CancellationToken token)
    {
        bool modern = sourceKind == PmaPackageKind.Modern;
        var excluded = modern ? ModernFeatures : ClassicFeatures;
        var extensions = manifest.OptionalExtensions;
        var archiveIds = new List<Guid>();
        if (extensions.Remove(ConversionExtensionName, out var indexElement))
        {
            var index = indexElement.Deserialize<ArchiveIndex>(Json);
            if (index is not { Version: 1, Entries: { } ids } || ids.Length > files.Count || ids.Contains(Guid.Empty)
                || ids.Distinct().Count() != ids.Length) throw new InvalidDataException("Invalid project conversion archive index.");
            archiveIds.AddRange(ids);
        }
        var modelHashes = manifest.Models.ToDictionary(m => m.ObjectId, m => files[m.EditedModelPath].Sha256);
        var actors = layout["objects"]!.AsArray().Select(actor => actor!.AsObject()).ToArray();
        var signature = actors.Select(actor => actor["id"]!.GetValue<Guid>())
            .Select(id => id.ToString("N") + ":" + modelHashes[id]).ToArray();
        var archivedExtensions = extensions.Where(pair => excluded.ExcludesExtension(pair.Key)).ToDictionary();
        var resourceNames = files.Keys.Where(excluded.ExcludesResource).Order(StringComparer.Ordinal).ToArray();
        var purposes = new Dictionary<Guid, JsonObject>();
        bool hasLighting = layout["lights"]!.AsArray().Count > 0 || layout["groups"]?.AsArray().Count > 0 || layout.ContainsKey("settings");
        long archiveLimit = Math.Min(limits.MaxEntryBytes, (long)limits.MaxManifestBytes + SceneAnimationProjectStore.MaxDocumentBytes);
        JsonObject? lighting = null;
        int lights = 0, groups = 0;
        bool settings = false;
        if (modern)
        {
            lights = layout["lights"]!.AsArray().Count;
            groups = layout["groups"]?.AsArray().Count ?? 0;
            settings = layout["settings"] is JsonObject value && (value["environmentStrength"]!.GetValue<float>() != 0
                || value["exposureEv"]!.GetValue<float>() != 0);
            if (hasLighting)
            {
                lighting = new();
                foreach (string key in new[] { "version", "lights", "groups", "settings", "selectedLightId" })
                    if (layout.Remove(key, out var property)) lighting.Add(key, property);
                layout["version"] = layout["portableModels"] is null ? SceneProjectStore.Version : SceneProjectStore.PortableVersion;
                layout["lights"] = new JsonArray();
                layout["selectedLightId"] = null;
            }
            foreach (var actor in actors)
            {
                var purpose = actor["purpose"]!.AsObject();
                if (!HasAuthoredPurpose(purpose)) continue;
                purposes.Add(actor["id"]!.GetValue<Guid>(), (JsonObject)purpose.DeepClone());
                actor["purpose"] = new JsonObject { ["role"] = nameof(ModelRole.Unspecified), ["environmentIndoor"] = null };
            }
        }

        // Newest matching archive wins. An archive referring to other actors or model revisions remains dormant;
        // never rebind an old material index or a bone to a replacement model merely because its name matches.
        bool restored = false, deferred = false;
        foreach (var archiveId in archiveIds.ToArray().Reverse())
        {
            token.ThrowIfCancellationRequested();
            string root = ArchiveDirectory(archiveId), path = root + "state.json";
            if (!files.TryGetValue(path, out var entry) || entry.Length > archiveLimit)
                throw new InvalidDataException("Missing or oversized project conversion archive metadata.");
            var archive = JsonSerializer.Deserialize<EngineArchive>(File.ReadAllBytes(Path.Combine(stage, path)), Json);
            if (archive is null || archive.ProjectId != manifest.ProjectId || archive.Models is null || archive.Purposes is null ||
                archive.Extensions is null || archive.Resources is null || archive.Format != Format && archive.Format != PmcProjectStore.Format)
                throw new InvalidDataException("Invalid project conversion archive.");
            if (archive.Format != PackageFormat(targetKind)) continue;
            var feature = targetKind == PmaPackageKind.Modern ? ModernFeatures : ClassicFeatures;
            if (archive.Extensions.Keys.Any(key => !feature.ExcludesExtension(key)) || archive.Resources.Any(p => !feature.ExcludesResource(p))
                || (targetKind == PmaPackageKind.Classic && (archive.Lighting is not null || archive.Purposes.Count != 0)))
                throw new InvalidDataException("Project archive contains data outside its engine profile.");
            // Some older neutral PMC writers can carry active native lighting or model purposes.
            // Keep those authored edits and defer an overlapping archive, just like a resource collision.
            bool overlapsLayout = (archive.Lighting is not null && hasLighting) || actors.Any(actor =>
                archive.Purposes.ContainsKey(actor["id"]!.GetValue<Guid>()) && HasAuthoredPurpose(actor["purpose"]!.AsObject()));
            if (restored || overlapsLayout || !signature.SequenceEqual(archive.Models) || archive.Extensions.Keys.Any(extensions.ContainsKey)
                || archive.Resources.Any(files.ContainsKey))
            { deferred = true; continue; }
            if (archive.Lighting is { } previous)
            {
                foreach (var (key, property) in previous)
                {
                    if (key is not ("version" or "lights" or "groups" or "settings" or "selectedLightId"))
                        throw new InvalidDataException("Unknown archived lighting field.");
                    // Ordinary package saves can promote an older relative layout to a portable layout.
                    // Lighting schema versions form adjacent relative/portable pairs throughout versions 1–14.
                    if (key == "version")
                    {
                        int version = property!.GetValue<int>();
                        if (version < SceneProjectStore.Version || version > SceneProjectStore.PortableLightingFalloffVersion)
                            throw new InvalidDataException("Unsupported archived lighting schema.");
                        layout[key] = ((version - 1) / 2 * 2) + (layout["portableModels"] is null ? 1 : 2);
                    }
                    else layout[key] = property?.DeepClone();
                }
            }
            foreach (var actor in actors)
                if (archive.Purposes.TryGetValue(actor["id"]!.GetValue<Guid>(), out var purpose))
                    actor["purpose"] = purpose.DeepClone();
            foreach (var pair in archive.Extensions) extensions.Add(pair.Key, pair.Value);
            for (int i = 0; i < archive.Resources.Length; i++)
            {
                PmaAdditionalResources.ValidatePath(archive.Resources[i]);
                RenameResource(root + i + ".bin", archive.Resources[i]);
            }
            files.Remove(path);
            archiveIds.Remove(archiveId);
            restored = true;
        }

        if (lighting is not null || purposes.Count != 0 || archivedExtensions.Count != 0 || resourceNames.Length != 0)
        {
            var id = Guid.NewGuid();
            string root = ArchiveDirectory(id), path = root + "state.json";
            var archive = new EngineArchive(PackageFormat(sourceKind), manifest.ProjectId, signature, lighting, purposes, archivedExtensions, resourceNames);
            files.Add(path, WriteJsonFile(stage, path, archive, archiveLimit));
            foreach (var key in archivedExtensions.Keys) extensions.Remove(key);
            for (int i = 0; i < resourceNames.Length; i++) RenameResource(resourceNames[i], root + i + ".bin");
            archiveIds.Add(id);
        }
        extensions.Add(ConversionExtensionName, JsonSerializer.SerializeToElement(new ArchiveIndex(1, archiveIds.ToArray()), Json));
        return new(lights, groups, settings, purposes.Count, archivedExtensions.Keys.Order(StringComparer.Ordinal).ToArray(), resourceNames, restored, deferred);

        void RenameResource(string from, string to)
        {
            token.ThrowIfCancellationRequested();
            if (!files.Remove(from, out var file) || files.ContainsKey(to))
                throw new InvalidDataException("Missing or conflicting archived resource.");
            string target = Path.Combine(stage, to);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(Path.Combine(stage, file.Path), target);
            files.Add(to, file with { Path = to });
        }
    }

    private static bool HasAuthoredPurpose(JsonObject purpose) => purpose["environmentIndoor"] is not null
        || !string.Equals((string?)purpose["role"], nameof(ModelRole.Unspecified), StringComparison.OrdinalIgnoreCase);

    private static string ArchiveDirectory(Guid id) => "render-resources/project-conversion/" + id.ToString("N") + "/";
    private static FileData WriteJsonFile<T>(string stage, string name, T value, long maximum)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.LongLength > maximum) throw new InvalidDataException("Project conversion metadata exceeds its byte budget.");
        string path = Path.Combine(stage, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return new() { Path = name, Length = bytes.LongLength, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
    }
}
