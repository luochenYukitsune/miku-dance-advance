using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App;

/// <summary>Document-owned original common/default toon bytes. This narrow opaque extension uses the
/// existing neutral package transport; authored separate toon files remain model dependencies.</summary>
internal sealed partial class EditorClassicToonPackage
{
    internal const string ExtensionName = "org.mda.classic-toons";
    internal const string ResourcePrefix = "render-resources/classic-toons/";
    private const int MaximumImageBytes = 65 * 1024 * 1024;
    private const long MaximumTotalBytes = 128L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };
    private sealed record MaterialDto([property: JsonRequired] int MaterialIndex,
        [property: JsonRequired] int Slot, [property: JsonRequired] string ImageSha256, bool Override = false);
    private sealed record ModelDto([property: JsonRequired] Guid ObjectId, [property: JsonRequired] MaterialDto[] Materials);
    private sealed record ImageDto([property: JsonRequired] string Sha256,
        [property: JsonRequired] string AuthoredToken, [property: JsonRequired] string ResourcePath);
    private sealed record ManifestDto([property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] ModelDto[] Models, [property: JsonRequired] ImageDto[] Images);
    private sealed record Image(string Sha256, string AuthoredToken, byte[] Bytes, SceneTexture Texture)
    {
        internal string Path => ResourcePrefix + Sha256.ToLowerInvariant() + ".bin";
    }
    private readonly Dictionary<Guid, MaterialDto[]> models;
    private readonly Dictionary<string, Image> images;

    private EditorClassicToonPackage(Dictionary<Guid, MaterialDto[]> models, Dictionary<string, Image> images)
    { this.models = models; this.images = images; }

    internal static EditorClassicToonPackage Capture(SceneProjectSession session,
        EditorClassicToonResources? selected, EditorClassicToonPackage? previous = null, CancellationToken token = default)
        => Capture(session.Objects, session.CaptureAuthoredContent().Motions, selected, previous, token);

    internal static EditorClassicToonPackage Capture(SceneProjectEdit edit,
        EditorClassicToonResources? selected, EditorClassicToonPackage? previous = null, CancellationToken token = default)
        => Capture(edit.Composition.Objects, edit.Motions, selected, previous, token);

    private static EditorClassicToonPackage Capture(IReadOnlyList<EditableSceneObject> objects,
        IEnumerable<SceneActorMotion> motions, EditorClassicToonResources? selected,
        EditorClassicToonPackage? previous, CancellationToken token)
    {
        var authored = motions.ToDictionary(value => value.ObjectId, value => value.Model);
        var models = new Dictionary<Guid, MaterialDto[]>();
        var images = new Dictionary<string, Image>(StringComparer.Ordinal);
        foreach (var obj in objects)
        {
            token.ThrowIfCancellationRequested();
            var model = authored.GetValueOrDefault(obj.Id) ?? throw Invalid("Every Classic model requires its authored PMX material identities to capture original toons.");
            if (obj.Source.Surface.Snapshot.Materials.Count != model.Materials.Count)
                throw Invalid("Classic source material count disagrees with its authored PMX.");
            var required = Required(model);
            if (previous is not null && previous.models.TryGetValue(obj.Id, out var retained))
                foreach (var entry in retained.Where(value => value.Override))
                    if (entry.MaterialIndex < model.Materials.Count && Slot(model.Materials[entry.MaterialIndex]) == entry.Slot)
                        required[entry.MaterialIndex] = entry.Slot;
            var materials = new List<MaterialDto>();
            foreach (var (index, slot) in required)
            {
                SceneTexture actual = BoundImage(obj.Source, index);
                Image? image = null;
                // A retained document can contain several original MMD sources. Keep each instance's
                // exact frozen image; never replace every actor merely because a new source was selected.
                Image? prior = null;
                if (previous is not null && previous.models.TryGetValue(obj.Id, out var old))
                {
                    var binding = old.FirstOrDefault(value => value.MaterialIndex == index && value.Slot == slot);
                    if (binding is not null && previous.images.TryGetValue(binding.ImageSha256, out var candidate)
                        && SameImage(actual, candidate.Texture))
                    { prior = candidate; if (ReferenceEquals(actual, candidate.Texture)) image = candidate; }
                }
                var resource = slot <= 10 ? selected?.Entries[slot] : null;
                // A newly prepared source may deliberately use different encoded bytes with identical
                // pixels. Its exact provider identity wins over a previous actor's pixel-only match.
                if (resource is not null && ReferenceEquals(actual, resource.Texture))
                    image = images.GetValueOrDefault(resource.Sha256)
                        ?? new(resource.Sha256, resource.AuthoredToken, resource.EncodedBytes.ToArray(), resource.Texture);
                image ??= prior;
                if (image is null && resource is not null)
                {
                    if (SameImage(actual, resource.Texture))
                        image = images.GetValueOrDefault(resource.Sha256)
                            ?? new(resource.Sha256, resource.AuthoredToken, resource.EncodedBytes.ToArray(), resource.Texture);
                }
                if (image is null)
                    throw Invalid($"Model {obj.Id}, material {index}: its actual original toon slot {slot} has no matching frozen encoded source.");
                AddImage(images, image);
                bool overridden = previous is not null && previous.models.TryGetValue(obj.Id, out var previousBindings)
                    && previousBindings.Any(value => value.MaterialIndex == index && value.Slot == slot && value.Override);
                materials.Add(new(index, slot, image.Sha256, overridden));
            }
            models.Add(obj.Id, materials.ToArray());
        }
        ValidateBudget(images.Values);
        var result = new EditorClassicToonPackage(models, images);
        result.Validate(objects, authored);
        return result;
    }

    /// <summary>Freeze, hash and decode all declared bytes during package preflight, before caller model
    /// loading or physics. A declared package never consults an original MMD directory.</summary>
    internal static EditorClassicToonPackage? ReadPackage(IPmaPackageResources package, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(package); token.ThrowIfCancellationRequested();
        string[] owned = package.ResourcePaths.Where(value => value.StartsWith(ResourcePrefix, StringComparison.Ordinal)).ToArray();
        if (!package.OptionalExtensions.TryGetValue(ExtensionName, out var json))
        {
            if (owned.Length != 0) throw Invalid("Classic toon resources have no owning metadata.");
            return null;
        }
        if (package.Format != PmcProjectStore.Format) throw Invalid("Original Classic toon metadata requires a PMC package.");
        ManifestDto manifest;
        try { manifest = json.Deserialize<ManifestDto>(Json) ?? throw Invalid("Classic toon metadata is null."); }
        catch (JsonException error) { throw new InvalidDataException("Classic toon metadata is malformed or contains unknown fields.", error); }
        if (manifest.SchemaVersion is not (1 or 2) || manifest.Models is null || manifest.Images is null)
            throw Invalid("Unsupported or incomplete Classic toon metadata.");
        var images = new Dictionary<string, Image>(StringComparer.Ordinal);
        long encoded = 0, decoded = 0;
        foreach (var entry in manifest.Images)
        {
            token.ThrowIfCancellationRequested();
            if (entry is null || !IsHash(entry.Sha256) || string.IsNullOrWhiteSpace(entry.AuthoredToken)
                || entry.AuthoredToken.Length > 512 || entry.ResourcePath != ResourcePrefix + entry.Sha256.ToLowerInvariant() + ".bin"
                || images.ContainsKey(entry.Sha256)) throw Invalid("A Classic toon image identity is invalid or duplicated.");
            byte[] bytes = package.ReadResource(entry.ResourcePath, MaximumImageBytes);
            encoded = checked(encoded + bytes.Length);
            if (bytes.Length == 0 || encoded > MaximumTotalBytes || Convert.ToHexString(SHA256.HashData(bytes)) != entry.Sha256)
                throw Invalid("A Classic toon image is missing, over budget or has a different digest.");
            var texture = EditorTextureLoader.Decode(bytes, entry.AuthoredToken, token);
            decoded = checked(decoded + (long)texture.Width * texture.Height * 4);
            if (decoded > MaximumTotalBytes) throw Invalid("Decoded Classic toon images exceed their memory budget.");
            images.Add(entry.Sha256, new(entry.Sha256, entry.AuthoredToken, bytes, texture));
        }
        // Neutral retained resources can include older, no-longer-referenced hashes after a model edit.
        // They remain opaque transport data; only the current manifest's exact image paths are used.
        if (images.Values.Any(value => !owned.Contains(value.Path, StringComparer.Ordinal)))
            throw Invalid("Classic toon metadata is missing a packaged image resource.");
        var models = new Dictionary<Guid, MaterialDto[]>(); var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Models)
        {
            token.ThrowIfCancellationRequested();
            if (entry is null || entry.ObjectId == Guid.Empty || entry.Materials is null || !models.TryAdd(entry.ObjectId, entry.Materials))
                throw Invalid("A Classic toon model identity is missing or duplicated.");
            var seen = new HashSet<int>(); var slotImages = new Dictionary<int, string>();
            foreach (var material in entry.Materials)
            {
                if (material is null || material.MaterialIndex < 0 || material.Slot < 0 || (material.Slot > 10 && !material.Override)
                    || (manifest.SchemaVersion == 1 && material.Override)
                    || !seen.Add(material.MaterialIndex) || !IsHash(material.ImageSha256) || !images.ContainsKey(material.ImageSha256))
                    throw Invalid("A Classic toon material binding is invalid, duplicated or missing its image.");
                if (material.Slot is >= 1 and <= 10 && slotImages.TryGetValue(material.Slot, out string? previous) && previous != material.ImageSha256)
                    throw Invalid("One Classic model declares conflicting images for the same original toon slot.");
                slotImages[material.Slot] = material.ImageSha256;
                used.Add(material.ImageSha256);
            }
        }
        if (!used.SetEquals(images.Keys)) throw Invalid("Classic toon metadata contains an unbound image.");
        return new(models, images);
    }

    /// <summary>Validates authored PMX material/index identities and supplies only package-owned common
    /// and default slots. Existing authored custom toon dependencies keep their ordinary model paths.</summary>
    internal SceneAnimationModelAsset Apply(Guid objectId, SceneAnimationModelAsset asset, string modelPath,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(asset); token.ThrowIfCancellationRequested();
        MaterialDto[] expected = RequireBindings(objectId, asset.Model);
        SceneTexture? Common(int index) => Resolve(index + 1);
        SceneTexture? Default() => Resolve(0);
        SceneTexture? Resolve(int slot)
        {
            var matching = expected.Where(value => value.Slot == slot).ToArray();
            if (matching.Length == 0) return null; // An authored zero-index material does not use an image.
            if (slot != 0 && matching.Any(value => value.ImageSha256 != matching[0].ImageSha256))
                throw Invalid("A Classic model has conflicting original toon slot images.");
            return images[matching[0].ImageSha256].Texture;
        }
        // Retain authored custom toon images through the existing adapter. The package providers above
        // are the sole common/default providers even if the caller's source was decorated elsewhere.
        asset = EditorClassicModelAssets.RetainToonTextures(asset, modelPath, token, Common, Default);
        var source = asset.Source; var baseline = source.Surface.Snapshot;
        var textures = baseline.Textures.ToList(); var materials = baseline.Materials.ToArray();
        var bindings = source.Surface.Materials.ToArray(); var appended = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in expected)
        {
            token.ThrowIfCancellationRequested(); var image = images[item.ImageSha256];
            if (!appended.TryGetValue(image.Sha256, out int textureIndex))
            { textureIndex = textures.Count; textures.Add(image.Texture); appended.Add(image.Sha256, textureIndex); }
            materials[item.MaterialIndex] = materials[item.MaterialIndex] with { ToonTexture = textureIndex };
            bindings[item.MaterialIndex] = bindings[item.MaterialIndex] with
            { ToonShadowColor = item.Slot == 0 && !item.Override ? Vector3.One : OriginalStyleMath.ReadToonShadowColor(image.Texture) };
        }
        var snapshot = new SceneSnapshot(baseline.Vertices, baseline.Indices, materials, textures, baseline.World,
            baseline.Camera, baseline.LightDirection, baseline.AdditionalUvs, baseline.RawRadialSeeds);
        if (baseline.HasDynamicVertices) snapshot = snapshot.WithDeformedVertices(baseline.Vertices.ToArray());
        var surface = new OriginalStyleScene(snapshot, bindings, source.Surface.Profile);
        return asset with { Source = new(surface, source.Outline, source.OutlineOptions, source.Options,
            source.Materials, source.Lighting, source.EditorOverlay) };
    }

    internal void Validate(SceneProjectSession session)
        => Validate(session.Objects, session.CaptureAuthoredContent().Motions.ToDictionary(value => value.ObjectId, value => value.Model));

    private void Validate(IReadOnlyList<EditableSceneObject> objects, IReadOnlyDictionary<Guid, PmxModel> authored)
    {
        if (!models.Keys.ToHashSet().SetEquals(objects.Select(value => value.Id)))
            throw Invalid("Classic toon metadata must cover the exact live model set.");
        foreach (var obj in objects)
        {
            if (!authored.TryGetValue(obj.Id, out var model)) throw Invalid("Classic toon metadata requires authored model identities.");
            foreach (var binding in RequireBindings(obj.Id, model))
                if (!SameImage(BoundImage(obj.Source, binding.MaterialIndex), images[binding.ImageSha256].Texture))
                    throw Invalid("A Classic model's actual toon image differs from its frozen portable binding.");
        }
    }

    /// <summary>Attach only the original images used by the converted PMX materials. The converter
    /// calls this after restoring any previous Classic archive, so authored overrides take priority.</summary>
    internal static PmaSaveOptions? PrepareConversion(IReadOnlyList<SceneActorMotion> actors,
        Func<CancellationToken, IReadOnlyList<EditorClassicToonResources.Resource>>? chooseImages, CancellationToken token)
    {
        var models = new Dictionary<Guid, MaterialDto[]>();
        var images = new Dictionary<string, Image>(StringComparer.Ordinal);
        IReadOnlyList<EditorClassicToonResources.Resource>? originals = null;
        foreach (var actor in actors)
        {
            token.ThrowIfCancellationRequested();
            var materials = new List<MaterialDto>();
            foreach (var (index, slot) in Required(actor.Model))
            {
                originals ??= chooseImages?.Invoke(token) ?? throw new InvalidOperationException(
                    EditorLocalization.Text("Mda.Text.Project.ConversionToonSource"));
                var original = originals[slot];
                if (original.Slot != slot) throw Invalid("The original Toon source does not match its requested slot.");
                if (!images.ContainsKey(original.Sha256))
                    images.Add(original.Sha256, new(original.Sha256, original.AuthoredToken,
                        original.EncodedBytes.ToArray(), original.Texture));
                materials.Add(new(index, slot, original.Sha256));
            }
            models.Add(actor.ObjectId, materials.ToArray());
        }
        if (images.Count == 0) return null;
        ValidateBudget(images.Values);
        return new EditorClassicToonPackage(models, images).WithPackagedImages(new());
    }

    internal PmaSaveOptions CreateSaveOptions(SceneProjectSession session, PmaSaveOptions options, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); Validate(session);
        var result = WithPackagedImages(options);
        PmaProjectStore.ValidateAdditionalResourceBudget(session, result.AdditionalResources.ToDictionary(pair => pair.Key,
            pair => (long)pair.Value.Length), options.ValidationOptions);
        return result;
    }

    private PmaSaveOptions WithPackagedImages(PmaSaveOptions options)
    {
        var extensions = options.OptionalExtensions.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        var resources = options.AdditionalResources.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var manifest = new ManifestDto(2, models.OrderBy(pair => pair.Key).Select(pair => new ModelDto(pair.Key, pair.Value)).ToArray(),
            images.Values.OrderBy(value => value.Sha256, StringComparer.Ordinal).Select(value => new ImageDto(value.Sha256, value.AuthoredToken, value.Path)).ToArray());
        JsonElement encoded = JsonSerializer.SerializeToElement(manifest, Json);
        if (extensions.TryGetValue(ExtensionName, out var extensionOverride) && !JsonElement.DeepEquals(extensionOverride, encoded))
            throw Invalid("A save-only Classic toon override conflicts with the prepared document.");
        extensions[ExtensionName] = encoded;
        foreach (var image in images.Values)
        {
            if (resources.TryGetValue(image.Path, out var resourceOverride) && !resourceOverride.Span.SequenceEqual(image.Bytes))
                throw Invalid("A save-only Classic toon image conflicts with the prepared document.");
            resources[image.Path] = image.Bytes;
        }
        if (resources.Keys.Any(value => value.StartsWith(ResourcePrefix, StringComparison.Ordinal)
            && !images.Values.Any(image => image.Path == value))) throw Invalid("A save supplied an unowned Classic toon image.");
        if (options.ContentSelection?.ExcludesExtension(ExtensionName) == true || images.Values.Any(value => options.ContentSelection?.ExcludesResource(value.Path) == true))
            throw Invalid("Required original Classic toon images cannot be excluded from a portable document.");
        return options with { OptionalExtensions = extensions, AdditionalResources = resources };
    }

    private MaterialDto[] RequireBindings(Guid objectId, PmxModel model)
    {
        if (!models.TryGetValue(objectId, out var expected)) throw Invalid("A model has no declared Classic toon binding.");
        var required = Required(model);
        foreach (var entry in expected.Where(value => value.Override))
        {
            if (entry.MaterialIndex < 0 || entry.MaterialIndex >= model.Materials.Count
                || Slot(model.Materials[entry.MaterialIndex]) != entry.Slot)
                throw Invalid("A Classic toon override disagrees with its authored material.");
            required[entry.MaterialIndex] = entry.Slot;
        }
        if (expected.Length != required.Count || expected.Any(value => !required.TryGetValue(value.MaterialIndex, out int slot) || value.Slot != slot))
            throw Invalid("Classic toon bindings disagree with authored PMX material modes or indices.");
        return expected;
    }
    private static Dictionary<int, int> Required(PmxModel model)
    {
        var result = new Dictionary<int, int>();
        for (int index = 0; index < model.Materials.Count; index++)
        {
            var material = model.Materials[index];
            if (material.FaceVertexCount <= 0) continue;
            if (material.ToonMode == PmxToonMode.Common)
            {
                if (material.ToonTextureIndex is < 0 or > 9) throw Invalid("Invalid authored PMX common toon index.");
                result.Add(index, material.ToonTextureIndex + 1);
            }
            else if (material.ToonMode == PmxToonMode.Separate)
            {
                if (material.ToonTextureIndex == -1) result.Add(index, 0);
                else if (material.ToonTextureIndex < 0 || material.ToonTextureIndex >= model.Textures.Count)
                    throw Invalid("Invalid authored PMX separate toon index.");
            }
            else throw Invalid("Invalid authored PMX toon mode.");
        }
        return result;
    }
    private static SceneTexture BoundImage(OriginalFrameScene source, int materialIndex)
    {
        var snapshot = source.Surface.Snapshot;
        if (materialIndex >= snapshot.Materials.Count) throw Invalid("Classic source material count disagrees with authored PMX.");
        int index = snapshot.Materials[materialIndex].ToonTexture;
        if (index < 0 || index >= snapshot.Textures.Count || snapshot.Textures[index].Kind != SceneTextureKind.Image)
            throw Invalid("A required original Classic toon image is absent from the actual source.");
        return snapshot.Textures[index];
    }
    private static bool SameImage(SceneTexture first, SceneTexture second)
        => ReferenceEquals(first, second) || first.Kind == SceneTextureKind.Image && first.Width == second.Width
            && first.Height == second.Height && first.Rgba.SequenceEqual(second.Rgba);
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F');
    private static InvalidDataException Invalid(string message) => new(message);
    private static void AddImage(Dictionary<string, Image> images, Image image)
    {
        if (images.TryGetValue(image.Sha256, out var existing) && !existing.Bytes.AsSpan().SequenceEqual(image.Bytes))
            throw Invalid("Conflicting encoded Classic toon images share one digest.");
        images[image.Sha256] = image;
    }
    private static void ValidateBudget(IEnumerable<Image> images)
    {
        long encoded = 0, decoded = 0;
        foreach (var image in images)
        {
            encoded = checked(encoded + image.Bytes.Length); decoded = checked(decoded + (long)image.Texture.Width * image.Texture.Height * 4);
            if (image.Bytes.Length is <= 0 or > MaximumImageBytes || encoded > MaximumTotalBytes || decoded > MaximumTotalBytes)
                throw Invalid("Classic toon images exceed their in-memory budget.");
        }
    }
}
