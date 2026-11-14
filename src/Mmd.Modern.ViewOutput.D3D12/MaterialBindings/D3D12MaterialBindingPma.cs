using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>A model-specific mapping and explicitly prepared source texture table. Common style
/// presets remain engine-owned references; these instance inputs travel with the project.</summary>
public sealed record D3D12MaterialBindingPmaEntry(Guid ObjectId, D3D12MaterialBindingProfile Profile,
    OriginalFrameScene SourceRest);

/// <summary>Renderer-owned portable mapping codec over PMA's existing validated logical resources.
/// It preserves exact source texture ordinals, including dedicated images not referenced by PMX
/// diffuse/sphere materials. RGBA8 resources are lossless decoded data, not re-encoded source images.</summary>
public static class D3D12MaterialBindingPma
{
    public const string ExtensionName = "org.mda.model-material-bindings";
    public const int MaxMetadataBytes = 512 * 1024;
    public const int MaxTexturesPerObject = 4096;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 16
    };

    /// <summary>Builds save-only options for the complete desired mapping set. Does not edit live
    /// project state; an empty set explicitly removes the mapping selection on the saved copy.</summary>
    public static PmaSaveOptions CreateSaveOptions(SceneProjectSession project,
        IEnumerable<D3D12MaterialBindingPmaEntry> entries, PmaSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(entries);
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.ValidationOptions);
        ArgumentNullException.ThrowIfNull(options.AdditionalResources);
        var selected = entries.Take(D3D12MaterialBindingProfile.MaxMaterials + 1).ToArray();
        if (selected.Length > D3D12MaterialBindingProfile.MaxMaterials)
            throw new InvalidDataException("Portable material mappings exceed their object budget.");
        var ids = new HashSet<Guid>(); var objects = project.Objects.ToDictionary(value => value.Id);
        var resources = options.AdditionalResources.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var pendingTextures = new List<(string Path, SceneTexture Texture)>();
        var pendingSeeds = new List<(string Path, SceneRawRadialSeeds Seeds, int Length)>();
        var paths = resources.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0; int fileCount = 0;
        foreach (var value in resources.Values) Reserve(value.Length);
        foreach (var entry in selected)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.ObjectId == Guid.Empty || !ids.Add(entry.ObjectId) || !objects.TryGetValue(entry.ObjectId, out var obj))
                throw new ArgumentException("Each portable mapping requires a unique known project object.", nameof(entries));
            var layout = D3D12MaterialBindingLayout.Capture(entry.SourceRest);
            layout.ValidateFingerprint(entry.Profile.LayoutFingerprint, entry.Profile.LayoutVersion);
            D3D12MaterialBindingLayout.Capture(obj.Source).ValidateFingerprint(entry.Profile.LayoutFingerprint, entry.Profile.LayoutVersion);
            // Validate all dedicated selectors without activating any style or creating a device.
            _ = D3D12MaterialBindingProfileResolver.Resolve(entry.Profile, entry.SourceRest, entry.ObjectId, "mmd");
            SeedData? seedData = null;
            if (entry.SourceRest.Surface.Snapshot.RawRadialSeeds is { } seeds)
            {
                int count = seeds.Vertices.Length;
                if (count != entry.SourceRest.Surface.Snapshot.Vertices.Length)
                    throw new InvalidDataException("Portable radial seeds must preserve source vertex count and order.");
                int length = D3D12MaterialBindingSeedResource.ByteLength(count);
                string seedPath = SeedPath(entry.ObjectId);
                Claim(seedPath); Reserve(length);
                pendingSeeds.Add((seedPath, seeds, length));
                seedData = new(D3D12MaterialBindingSeedResource.Encoding, count, length,
                    D3D12MaterialBindingSeedResource.Digest(seeds));
            }
            var textures = entry.SourceRest.Surface.Snapshot.Textures;
            if (textures.Count > MaxTexturesPerObject) throw new InvalidDataException("Portable source texture table exceeds its budget.");
            var descriptions = new TextureData[textures.Count];
            for (int ordinal = 0; ordinal < textures.Count; ordinal++)
            {
                var texture = textures[ordinal]; var guard = layout.CaptureTexture(ordinal); guard.Validate();
                descriptions[ordinal] = new(guard.Width, guard.Height, guard.Kind, guard.RgbaSha256);
                string texturePath = TexturePath(entry.ObjectId, ordinal);
                Claim(texturePath); Reserve(texture.Rgba.Length);
                pendingTextures.Add((texturePath, texture));
            }
            using var profile = JsonDocument.Parse(D3D12MaterialBindingProfileJson.Encode(entry.Profile));
            var metadata = new BindingData(seedData is null ? 1 : 2, entry.ObjectId, profile.RootElement.Clone(), descriptions, seedData);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, Json);
            if (bytes.Length > MaxMetadataBytes) throw new InvalidDataException("Portable material metadata exceeds its budget.");
            Add(MetadataPath(entry.ObjectId), bytes);
        }
        var lengths = resources.ToDictionary(pair => pair.Key, pair => (long)pair.Value.Length, StringComparer.Ordinal);
        foreach (var (texturePath, texture) in pendingTextures) lengths.Add(texturePath, texture.Rgba.Length);
        foreach (var (seedPath, _, length) in pendingSeeds) lengths.Add(seedPath, length);
        PmaProjectStore.ValidateAdditionalResourceBudget(project, lengths, options.ValidationOptions);
        // Every mapping, selector, metadata and resource reservation has passed before any image clone.
        foreach (var (texturePath, texture) in pendingTextures) resources.Add(texturePath, texture.Rgba.ToArray());
        foreach (var (seedPath, seeds, _) in pendingSeeds) resources.Add(seedPath, D3D12MaterialBindingSeedResource.Encode(seeds));
        var extensions = options.OptionalExtensions.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        extensions[ExtensionName] = JsonSerializer.SerializeToElement(new IndexData(1, ids.Order().ToArray()), Json);
        return options with { AdditionalResources = new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(resources), OptionalExtensions = extensions };

        void Add(string path, byte[] bytes)
        {
            Claim(path); Reserve(bytes.Length); resources.Add(path, bytes);
        }
        void Claim(string path)
        {
            if (!paths.Add(path))
                throw new ArgumentException("Save options already contain resources owned by this material mapping: " + path, nameof(options));
        }
        void Reserve(long length)
        {
            if (length < 0 || length > options.ValidationOptions.MaxEntryBytes
                || expandedBytes > options.ValidationOptions.MaxExpandedBytes - length || ++fileCount > options.ValidationOptions.MaxFiles)
                throw new InvalidDataException("Portable material resources exceed the configured byte or file budget.");
            expandedBytes += length;
        }
    }

    /// <summary>Restores mapping-owned source textures before project animation/physics is created.
    /// Unknown mappings can still be opened by the neutral PMA reader without this adapter; it retains
    /// their bytes and reports the uninterpreted extension. This adapter fails explicitly on corruption.</summary>
    public static PmaLoadedProject Load(string path, string cacheDirectory,
        Func<string, CancellationToken, SceneAnimationModelAsset> loadModel, IProjectPhysicsBackendProvider physics,
        out IReadOnlyDictionary<Guid, D3D12MaterialBindingProfile> profiles,
        PmaLoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loadModel);
        options ??= new();
        var mappings = new Dictionary<Guid, D3D12MaterialBindingProfile>();
        HashSet<Guid>? declared = null;
        var prior = options.ModelAssetTransform;
        var adapted = options with { ModelAssetTransform = Restore };
        var loaded = PmaProjectStore.Load(path, cacheDirectory, loadModel, physics, adapted, cancellationToken);
        try
        {
            if (declared is not null && !declared.SetEquals(mappings.Keys))
                throw new InvalidDataException("Portable material mapping index refers to an unknown or unrestored object.");
            profiles = new ReadOnlyDictionary<Guid, D3D12MaterialBindingProfile>(mappings);
            return loaded;
        }
        catch (Exception error)
        {
            try { loaded.Dispose(); }
            catch (Exception cleanupError) { error.Data["MaterialBindingProjectCleanupError"] = cleanupError; }
            throw;
        }

        SceneAnimationModelAsset Restore(PmaModelResourceContext context, SceneAnimationModelAsset asset)
        {
            cancellationToken.ThrowIfCancellationRequested();
            asset = prior?.Invoke(context, asset) ?? asset;
            if (declared is null)
                declared = ReadIndex(context.OptionalExtensions);
            if (!declared.Contains(context.ObjectId)) return asset;
            byte[] raw = context.ReadResource(MetadataPath(context.ObjectId), MaxMetadataBytes);
            if (raw.Length is < 1 or > MaxMetadataBytes) throw new InvalidDataException("Portable material metadata exceeds its byte budget.");
            BindingData metadata;
            try
            {
                metadata = JsonSerializer.Deserialize<BindingData>(raw, Json)
                    ?? throw new InvalidDataException("Missing portable material metadata.");
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("Invalid portable material metadata.", error);
            }
            if (metadata.SchemaVersion is not (1 or 2) || metadata.ObjectId != context.ObjectId || metadata.Textures is null
                || metadata.Textures.Length > MaxTexturesPerObject
                || (metadata.SchemaVersion == 2) != (metadata.RawRadialSeeds is not null))
                throw new InvalidDataException("Unsupported or mismatching portable material metadata.");
            var profile = D3D12MaterialBindingProfileJson.Decode(JsonSerializer.SerializeToUtf8Bytes(metadata.Profile));
            var original = asset.Source;
            long expanded = 0;
            if (metadata.RawRadialSeeds is { } seedData)
            {
                int count = original.Surface.Snapshot.Vertices.Length;
                int length = D3D12MaterialBindingSeedResource.ByteLength(count);
                D3D12MaterialBindingProfile.ValidateDigest(seedData.Sha256, nameof(seedData.Sha256));
                if (seedData.Encoding != D3D12MaterialBindingSeedResource.Encoding || seedData.VertexCount != count
                    || seedData.ByteLength != length || length > options.MaxEntryBytes || length > options.MaxExpandedBytes)
                    throw new InvalidDataException("Portable radial seed descriptor does not match its encoding, source count or byte budget.");
                byte[] bytes = context.ReadResource(SeedPath(context.ObjectId), length);
                if (bytes.Length != length || Convert.ToHexStringLower(SHA256.HashData(bytes)) != seedData.Sha256)
                    throw new InvalidDataException("Portable radial seeds do not match their byte length or digest.");
                var seeds = D3D12MaterialBindingSeedResource.Decode(bytes, count);
                if (original.Surface.Snapshot.RawRadialSeeds is { } authored
                    && !D3D12MaterialBindingSeedResource.SameBits(authored, seeds))
                    throw new InvalidDataException("Model loader authored radial seeds conflict with the portable source stream.");
                original = ReplaceSnapshot(original, original.Surface.Snapshot.WithRawRadialSeeds(seeds));
                expanded = length;
            }
            // Layout v1 includes the authored seed stream. Restore it first, then check the unchanged
            // complete geometry/material fingerprint before reading large dedicated image payloads.
            D3D12MaterialBindingLayout.Capture(original).ValidateFingerprint(profile.LayoutFingerprint, profile.LayoutVersion);
            var textures = new SceneTexture[metadata.Textures.Length];
            for (int ordinal = 0; ordinal < textures.Length; ordinal++)
            {
                var texture = metadata.Textures[ordinal] ?? throw new InvalidDataException("Missing source texture descriptor.");
                var guard = new D3D12MaterialBindingProfileTexture(ordinal, texture.Width, texture.Height, texture.Kind, texture.Sha256);
                guard.Validate();
                int expectedLength = checked(texture.Width * texture.Height * 4);
                expanded = checked(expanded + expectedLength);
                if (expectedLength > options.MaxEntryBytes || expanded > options.MaxExpandedBytes)
                    throw new InvalidDataException("Portable decoded textures exceed the configured resource budget.");
                byte[] pixels = context.ReadResource(TexturePath(context.ObjectId, ordinal), expectedLength);
                if (pixels.Length != expectedLength || Convert.ToHexStringLower(SHA256.HashData(pixels)) != texture.Sha256)
                    throw new InvalidDataException("Portable source texture does not match its dimensions or RGBA8 digest.");
                textures[ordinal] = new("portable-material-texture-" + ordinal, texture.Width, texture.Height, pixels, texture.Kind);
            }
            var s = original.Surface.Snapshot;
            var snapshot = new SceneSnapshot(s.Vertices, s.Indices, s.Materials, textures, s.World, s.Camera, s.LightDirection,
                additionalUvs: s.AdditionalUvs, rawRadialSeeds: s.RawRadialSeeds);
            var restored = ReplaceSnapshot(original, snapshot);
            _ = D3D12MaterialBindingProfileResolver.Resolve(profile, restored, context.ObjectId, "mmd");
            if (!mappings.TryAdd(context.ObjectId, profile)) throw new InvalidDataException("A portable object mapping was restored twice.");
            return new(asset.Model, restored);
        }
    }

    private static HashSet<Guid> ReadIndex(IReadOnlyDictionary<string, JsonElement> extensions)
    {
        if (!extensions.TryGetValue(ExtensionName, out var json)) return [];
        if (json.GetRawText().Length > MaxMetadataBytes) throw new InvalidDataException("Portable material index exceeds its budget.");
        var index = JsonSerializer.Deserialize<IndexData>(json, Json) ?? throw new InvalidDataException("Missing portable material index.");
        if (index.SchemaVersion != 1 || index.ObjectIds is null || index.ObjectIds.Length > D3D12MaterialBindingProfile.MaxMaterials
            || index.ObjectIds.Any(id => id == Guid.Empty) || index.ObjectIds.Distinct().Count() != index.ObjectIds.Length)
            throw new InvalidDataException("Unsupported or invalid portable material index.");
        return index.ObjectIds.ToHashSet();
    }

    private static string Root(Guid id) => "render-resources/material-bindings/" + id.ToString("N") + "/";
    private static string MetadataPath(Guid id) => Root(id) + "binding.json";
    private static string TexturePath(Guid id, int ordinal) => Root(id) + "textures/" + ordinal.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".rgba";
    private static string SeedPath(Guid id) => Root(id) + D3D12MaterialBindingSeedResource.RelativePath;
    private static OriginalFrameScene ReplaceSnapshot(OriginalFrameScene original, SceneSnapshot snapshot)
        => new(new(snapshot, original.Surface.Materials, original.Surface.Profile), original.Outline,
            original.OutlineOptions, original.Options, original.Materials, original.Lighting, original.EditorOverlay);
    private sealed record IndexData(int SchemaVersion, Guid[] ObjectIds);
    private sealed record BindingData(int SchemaVersion, Guid ObjectId, JsonElement Profile, TextureData[] Textures,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SeedData? RawRadialSeeds = null);
    private sealed record SeedData([property: JsonRequired] string Encoding, [property: JsonRequired] int VertexCount,
        [property: JsonRequired] long ByteLength, [property: JsonRequired] string Sha256);
    private sealed record TextureData(int Width, int Height, SceneTextureKind Kind, string Sha256);
}
