using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

internal sealed record ClassicAccessoryTexture(string Token, string OriginalPath, byte[] Bytes, SceneTexture Texture);
internal sealed record ClassicAccessoryContent(SceneProjectAccessory Accessory, byte[] SourceBytes,
    ClassicAccessoryTexture[] Textures);

/// <summary>Detached complete X assets and ordinary/sphere textures. Original paths are provenance only;
/// all resource bytes survive the package reader and can be saved again without reopening originals.</summary>
public sealed class D3D9ClassicAccessoryAssetsState
{
    private readonly IReadOnlyDictionary<string, ReadOnlyMemory<byte>> resources;
    private readonly JsonElement payload;
    private readonly string[] sourcePaths;
    public IReadOnlyList<SceneProjectAccessory> Accessories { get; }
    public IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, SceneTexture>> AccessoryTextures { get; }
    public IReadOnlyList<string> ResourcePaths { get; }

    internal D3D9ClassicAccessoryAssetsState(ClassicAccessoryContent[] contents)
    {
        Accessories = Array.AsReadOnly(contents.Select(value => value.Accessory with
            { Asset = value.Accessory.Asset with { Diagnostics = Array.AsReadOnly(value.Accessory.Asset.Diagnostics.ToArray()) } }).ToArray());
        AccessoryTextures = new ReadOnlyDictionary<Guid, IReadOnlyDictionary<string, SceneTexture>>(contents.ToDictionary(
            value => value.Accessory.ObjectId, value => (IReadOnlyDictionary<string, SceneTexture>)
                new ReadOnlyDictionary<string, SceneTexture>(value.Textures.ToDictionary(texture => texture.Token,
                    texture => texture.Texture, StringComparer.Ordinal))));
        var encoded = D3D9ClassicAccessoryAssets.Encode(contents);
        payload = encoded.Payload; resources = encoded.Resources; sourcePaths = encoded.SourcePaths;
        ResourcePaths = Array.AsReadOnly(resources.Keys.ToArray());
    }

    public byte[] CopyResourceBytes(string path)
        => resources.TryGetValue(path, out var bytes) ? bytes.ToArray()
            : throw new FileNotFoundException("Resource is not part of the frozen accessory assets.", path);

    /// <summary>Typed neutral opt-in, proving exact original X resources and the opaque payload/resources
    /// that the Classic codec has validated. The neutral bridge checks live/PMM identities separately.</summary>
    public PmaAccessoryPersistenceCapture ToPersistenceCapture()
        => new(D3D9ClassicAccessoryAssets.ExtensionName, payload, Accessories.Select((value, ordinal) =>
            new PmaAccessoryPersistenceItem(value, sourcePaths[ordinal])), resources);

    internal JsonElement Payload => payload;
    internal IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Resources => resources;
}

/// <summary>Traditional-engine-owned asset discovery and bounded opaque schema over the neutral PMC
/// transport. It uses the accepted X parser/adapter and token grammar; no FX or media discovery occurs.</summary>
public static class D3D9ClassicAccessoryAssets
{
    public const string ExtensionName = "org.mda.traditional-accessory-assets";
    public const int MaxMetadataBytes = 16 * 1024 * 1024;
    public const int MaxDescriptors = 100_000;
    private const string ResourceRoot = "render-resources/traditional/accessory-assets/";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };

    /// <summary>Automatically freezes every original material's ordinary/sphere image request. No manifest
    /// or directory scan is accepted. Missing files, unsupported media or decoder failures reject the entire
    /// candidate. Windows read/ancestor leases use the existing dependency-file source and close on return.</summary>
    public static D3D9ClassicAccessoryAssetsState Capture(IEnumerable<SceneProjectAccessory> accessories,
        Func<ReadOnlyMemory<byte>, string, SceneTexture> decodeTexture, PmaLoadOptions? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessories); ArgumentNullException.ThrowIfNull(decodeTexture);
        limits ??= new(); ValidateLimits(limits);
        cancellationToken.ThrowIfCancellationRequested();
        var selected = accessories.Take(257).ToArray();
        ValidateIdentities(selected);
        long total = 0; int count = 0, descriptorCount = selected.Length;
        var contents = new List<ClassicAccessoryContent>(selected.Length);
        // Source snapshots, grammar and all request identities are validated before any file or decoder.
        var planned = selected.Select(value =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = value.Asset.RequirePortableSource();
            _ = D3D9EmmPma.AbsoluteFileIdentity(source.OriginalPath);
            var tokens = References(value.Asset.Document!);
            descriptorCount = checked(descriptorCount + value.Asset.Document!.Materials.Count + tokens.Length);
            if (descriptorCount > MaxDescriptors) throw new InvalidDataException("Accessory descriptors exceed their count budget.");
            var paths = tokens.Select(token => ResolveTextureOrigin(source.OriginalPath, token)).ToArray();
            Claim(source.ByteLength);
            return (Accessory: value, Source: source, Tokens: tokens, Paths: paths);
        }).ToArray();
        long remainingTextureBytes = limits.MaxExpandedBytes - total;
        if (remainingTextureBytes == 0 && planned.Any(value => value.Tokens.Length != 0))
            throw new InvalidDataException("Portable accessory textures exceed the remaining byte budget.");
        using var files = new D3D9EffectDependencyFileSource(new(
            MaximumFiles: Math.Min(65_536, limits.MaxFiles), MaximumFileBytes: Math.Min(Array.MaxLength, limits.MaxEntryBytes),
            MaximumTotalBytes: Math.Max(1, remainingTextureBytes), MaximumReferenceBytes: MaxMetadataBytes));
        foreach (var plan in planned)
        {
            var textures = new List<ClassicAccessoryTexture>(plan.Tokens.Length);
            for (int i = 0; i < plan.Tokens.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = files.Capture(new(null,
                    new D3D9EffectDependencyReference(Utf8.GetBytes(plan.Paths[i]), 65001), D3D9EffectDependencyRequestKind.Root), cancellationToken);
                Claim(snapshot.ByteLength);
                byte[] bytes = snapshot.CopyBytes();
                var texture = Decode(decodeTexture, bytes, plan.Tokens[i], cancellationToken);
                // The dependency source caches Windows-equivalent paths, while this record preserves
                // each request's lexical origin (including authored case) independently of its digest.
                textures.Add(new(plan.Tokens[i], plan.Paths[i], bytes, texture));
            }
            contents.Add(new(plan.Accessory, plan.Source.CopyOriginalBytes(), textures.ToArray()));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(contents.ToArray());

        void Claim(long length)
        {
            if (++count > Math.Min(MaxDescriptors, limits.MaxFiles) || length > Math.Min(Array.MaxLength, limits.MaxEntryBytes)
                || length > limits.MaxExpandedBytes - total)
                throw new InvalidDataException("Portable accessory resources exceed their file/byte budget.");
            total += length;
        }
    }

    /// <summary>Composes save-only resource/extension overrides and the typed Classic capture, preserving
    /// neutral limits, selection constraints and unrelated opaque data. No original path is opened.</summary>
    public static PmaSaveOptions CreateSaveOptions(SceneProjectSession project, D3D9ClassicAccessoryAssetsState state,
        PmaSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(state);
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.OptionalExtensions); ArgumentNullException.ThrowIfNull(options.AdditionalResources);
        ArgumentNullException.ThrowIfNull(options.ValidationOptions);
        if (options.AccessoryPersistence is not null)
            throw new ArgumentException("Save options already contain an accessory persistence capture.", nameof(options));
        if (options.OptionalExtensions.Keys.Any(key => key.Equals(ExtensionName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Save options already contain the accessory extension.", nameof(options));
        var selection = MergeSelection(options.ContentSelection, options.ValidationOptions.ContentSelection);
        if (selection?.ExcludesExtension(ExtensionName) == true)
            throw new InvalidDataException("Save content selection excludes the accessory extension.");
        var supplied = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in options.AdditionalResources)
            if (!supplied.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException("Save options contain duplicate resource paths.", nameof(options));
        var lengths = supplied.ToDictionary(pair => pair.Key, pair => (long)pair.Value.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in state.Resources)
        {
            if (selection?.ExcludesResource(pair.Key) == true)
                throw new InvalidDataException("Save content selection excludes an accessory resource.");
            if (!lengths.TryAdd(pair.Key, pair.Value.Length))
                throw new ArgumentException("Save options already contain an accessory-owned resource: " + pair.Key, nameof(options));
        }
        PmaProjectStore.ValidateAdditionalResourceBudget(project, lengths, options.ValidationOptions with { ContentSelection = selection });
        var frozen = supplied.Where(pair => selection?.ExcludesResource(pair.Key) != true)
            .ToDictionary(pair => pair.Key, pair => (ReadOnlyMemory<byte>)pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in state.Resources) frozen.Add(pair.Key, pair.Value.ToArray());
        var extensions = options.OptionalExtensions.Where(pair => selection?.ExcludesExtension(pair.Key) != true)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        extensions.Add(ExtensionName, state.Payload.Clone());
        return options with
        {
            ContentSelection = selection,
            AdditionalResources = new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(frozen),
            OptionalExtensions = new ReadOnlyDictionary<string, JsonElement>(extensions),
            AccessoryPersistence = state.ToPersistenceCapture()
        };
    }

    /// <summary>Validates metadata, original request identities and every frozen resource before decoding.
    /// Restoration parses the packaged X bytes and never opens any original path or package extraction path.</summary>
    public static D3D9ClassicAccessoryAssetsState? ReadPackage(IPmaPackageResources project,
        Func<ReadOnlyMemory<byte>, string, SceneTexture> decodeTexture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(decodeTexture);
        cancellationToken.ThrowIfCancellationRequested();
        if (!project.OptionalExtensions.TryGetValue(ExtensionName, out var element)) return null;
        try
        {
            string raw = element.GetRawText();
            if (Utf8.GetByteCount(raw) > MaxMetadataBytes)
                throw new InvalidDataException("Portable accessory metadata exceeds its byte budget.");
            var metadata = JsonSerializer.Deserialize<Metadata>(raw, Json) ?? throw new InvalidDataException("Missing accessory metadata.");
            if (metadata.SchemaVersion != 1 || metadata.Assets is null || metadata.Assets.Length is < 1 or > 256)
                throw new InvalidDataException("Unsupported or incomplete portable accessory metadata.");
            var guids = new HashSet<Guid>(); var ids = new HashSet<byte>();
            int descriptors = metadata.Assets.Length;
            for (int ordinal = 0; ordinal < metadata.Assets.Length; ordinal++)
            {
                var asset = metadata.Assets[ordinal] ?? throw new InvalidDataException("Missing accessory descriptor.");
                if (asset.ObjectId == Guid.Empty || !guids.Add(asset.ObjectId) || !ids.Add(asset.StoredId)
                    || asset.MaterialTokens is null || asset.Textures is null || asset.ReadOptions is null
                    || asset.ParserIdentity != PmmAccessorySourceSnapshot.SupportedParserIdentity
                    || asset.AdapterIdentity != PmmAccessorySourceSnapshot.SupportedAdapterIdentity
                    || !float.IsFinite(asset.UnitScale) || asset.UnitScale <= 0)
                    throw new InvalidDataException("Invalid or unsupported accessory identity/reader/adapter descriptor.");
                _ = D3D9EmmPma.AbsoluteFileIdentity(asset.OriginalPath);
                ValidateResource(asset.Source, SourcePath(ordinal));
                if (asset.Source.ByteLength > asset.ReadOptions.MaxInputBytes)
                    throw new InvalidDataException("Accessory source exceeds its preserved reader budget.");
                descriptors = checked(descriptors + asset.MaterialTokens.Length + asset.Textures.Length);
                if (descriptors > MaxDescriptors) throw new InvalidDataException("Accessory descriptors exceed their count budget.");
                var expected = References(asset.MaterialTokens);
                if (expected.Length != asset.Textures.Length) throw new InvalidDataException("Accessory texture requests are incomplete.");
                for (int texture = 0; texture < expected.Length; texture++)
                {
                    var value = asset.Textures[texture] ?? throw new InvalidDataException("Missing accessory texture descriptor.");
                    if (value.Token != expected[texture] || value.OriginalPath != ResolveTextureOrigin(asset.OriginalPath, value.Token))
                        throw new InvalidDataException("Accessory texture provenance disagrees with its original material request.");
                    ValidateResource(value.Resource, TexturePath(ordinal, texture));
                }
            }
            // Read/validate the whole set before the first decoder callback; a failed candidate cannot
            // expose a partial prepared set or a silent untextured accessory.
            var frozen = metadata.Assets.Select(asset =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] source = ReadResource(asset.Source);
                var probe = PreparedPmmAccessory.FromOriginalBytes(asset.StoredId, asset.OriginalPath, source,
                    readOptions: asset.ReadOptions, unitScale: asset.UnitScale, cancellationToken: cancellationToken);
                if (!probe.Document!.Materials.Select(value => value.Material.Texture).SequenceEqual(asset.MaterialTokens, StringComparer.Ordinal))
                    throw new InvalidDataException("Accessory material requests disagree with the packaged original X.");
                byte[][] textures = asset.Textures.Select(value => ReadResource(value.Resource)).ToArray();
                return (Metadata: asset, Source: source, TextureBytes: textures);
            }).ToArray();
            var contents = new List<ClassicAccessoryContent>(frozen.Length);
            foreach (var item in frozen)
            {
                var asset = item.Metadata;
                var textures = asset.Textures.Select((value, index) => new ClassicAccessoryTexture(value.Token, value.OriginalPath,
                    item.TextureBytes[index], Decode(decodeTexture, item.TextureBytes[index], value.Token, cancellationToken))).ToArray();
                var byToken = textures.ToDictionary(value => value.Token, value => value.Texture, StringComparer.Ordinal);
                var prepared = PreparedPmmAccessory.FromOriginalBytes(asset.StoredId, asset.OriginalPath, item.Source,
                    token => D3D9AccessoryTextureToken.Parse(token).Primary is { } primary ? byToken[primary] : null, asset.ReadOptions, asset.UnitScale,
                    cancellationToken: cancellationToken);
                contents.Add(new(new(asset.ObjectId, prepared), item.Source, textures));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(contents.ToArray());
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException or IOException
            or KeyNotFoundException or OverflowException)
        {
            throw new InvalidDataException("Invalid portable accessory assets.", error);
        }

        byte[] ReadResource(ResourceData resource)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = project.ReadResource(resource.Path, resource.ByteLength);
            if (bytes.LongLength != resource.ByteLength || Convert.ToHexStringLower(SHA256.HashData(bytes)) != resource.Sha256)
                throw new InvalidDataException("Accessory resource does not match its byte length or SHA256.");
            return bytes;
        }
    }

    internal static (JsonElement Payload, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Resources, string[] SourcePaths)
        Encode(ClassicAccessoryContent[] contents)
    {
        var resources = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var assets = contents.Select((item, ordinal) =>
        {
            var source = item.Accessory.Asset.RequirePortableSource();
            string path = SourcePath(ordinal); resources.Add(path, item.SourceBytes);
            return new AssetData(item.Accessory.ObjectId, item.Accessory.Asset.Index, source.OriginalPath,
                source.ParserIdentity, source.AdapterIdentity, source.ReadOptions, source.UnitScale, Describe(path, item.SourceBytes),
                item.Accessory.Asset.Document!.Materials.Select(value => value.Material.Texture).ToArray(),
                item.Textures.Select((texture, index) =>
                {
                    string name = TexturePath(ordinal, index); resources.Add(name, texture.Bytes);
                    return new TextureData(texture.Token, texture.OriginalPath, Describe(name, texture.Bytes));
                }).ToArray());
        }).ToArray();
        if ((long)assets.Length + assets.Sum(value => (long)value.MaterialTokens.Length + value.Textures.Length) > MaxDescriptors)
            throw new InvalidDataException("Accessory descriptors exceed their count budget.");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Metadata(1, assets), Json);
        if (bytes.Length > MaxMetadataBytes) throw new InvalidDataException("Accessory metadata exceeds its byte budget.");
        using var document = JsonDocument.Parse(bytes);
        return (document.RootElement.Clone(), new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(resources),
            Enumerable.Range(0, contents.Length).Select(SourcePath).ToArray());
    }

    private static SceneTexture Decode(Func<ReadOnlyMemory<byte>, string, SceneTexture> decoder, byte[] bytes, string token,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A callback receives an independent buffer; even an unsafe decoder cannot mutate retained bytes.
        var result = decoder(bytes.ToArray(), token) ?? throw new InvalidDataException("Accessory texture decoder returned no image: " + token);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Kind != SceneTextureKind.Image) throw new InvalidDataException("Accessory textures require decoded ordinary image pixels.");
        return result;
    }
    private static string[] References(XTextDocument document) => References(document.Materials.Select(value => value.Material.Texture));
    private static string[] References(IEnumerable<string> materialTokens)
    {
        var result = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal); int count = 0;
        foreach (string token in materialTokens)
        {
            if (++count > MaxDescriptors) throw new InvalidDataException("Accessory material tokens exceed their count budget.");
            if (token is null) throw new InvalidDataException("Accessory material token cannot be null.");
            foreach (string reference in D3D9AccessoryTextureToken.Parse(token).References)
                if (seen.Add(reference))
                {
                    if (result.Count == MaxDescriptors) throw new InvalidDataException("Accessory texture requests exceed their count budget.");
                    result.Add(reference);
                }
        }
        return result.ToArray();
    }
    private static string ResolveTextureOrigin(string originalX, string token)
    {
        string origin = D3D9EffectDependencySnapshot.CanonicalizeOrigin(originalX);
        if (string.IsNullOrWhiteSpace(token) || token.Contains('\0') || token.StartsWith('/') || token.StartsWith('\\'))
            throw new InvalidDataException("Accessory texture requires an unambiguous relative or absolute drive file reference.");
        string candidate = token.Length >= 2 && token[1] == ':' ? token :
            origin[..(origin.LastIndexOf('\\') + 1)] + token;
        return D3D9EffectDependencySnapshot.CanonicalizeOrigin(candidate);
    }
    private static void ValidateIdentities(SceneProjectAccessory[] selected)
    {
        if (selected.Length is < 1 or > 256 || selected.Any(value => value is null || value.ObjectId == Guid.Empty || value.Asset is null)
            || selected.Select(value => value.ObjectId).Distinct().Count() != selected.Length
            || selected.Select(value => value.Asset.Index).Distinct().Count() != selected.Length)
            throw new InvalidDataException("Accessory assets require a nonempty set of unique independent Guid/stored-byte identities.");
    }
    private static void ValidateLimits(PmaLoadOptions limits)
    {
        if (limits.MaxFiles < 1 || limits.MaxEntryBytes < 1 || limits.MaxExpandedBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }
    private static PmaContentSelection? MergeSelection(PmaContentSelection? first, PmaContentSelection? second)
        => first is null ? second : second is null ? first : new(
            first.ExcludedExtensionNames.Concat(second.ExcludedExtensionNames),
            first.ExcludedResourcePrefixes.Concat(second.ExcludedResourcePrefixes),
            first.RetainNativeLighting && second.RetainNativeLighting, first.RetainModelPurposes && second.RetainModelPurposes,
            first.RetainRenderingStyles && second.RetainRenderingStyles);
    private static string SourcePath(int ordinal) => ResourceRoot + "asset" + ordinal.ToString("D3", CultureInfo.InvariantCulture) + ".bin";
    private static string TexturePath(int ordinal, int texture) => ResourceRoot + "asset" + ordinal.ToString("D3", CultureInfo.InvariantCulture)
        + "-texture" + texture.ToString("D5", CultureInfo.InvariantCulture) + ".bin";
    private static ResourceData Describe(string path, byte[] bytes) => new(path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    private static void ValidateResource(ResourceData? resource, string expectedPath)
    {
        if (resource is null || resource.Path != expectedPath || resource.ByteLength < 0 || resource.ByteLength > Array.MaxLength
            || resource.Sha256 is null || resource.Sha256.Length != 64
            || resource.Sha256.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("Invalid accessory resource descriptor.");
    }
    private sealed record Metadata(int SchemaVersion, AssetData[] Assets);
    private sealed record AssetData(Guid ObjectId, byte StoredId, string OriginalPath, string ParserIdentity,
        string AdapterIdentity, XTextReadOptions ReadOptions, float UnitScale, ResourceData Source, string[] MaterialTokens, TextureData[] Textures);
    private sealed record TextureData(string Token, string OriginalPath, ResourceData Resource);
    private sealed record ResourceData(string Path, long ByteLength, string Sha256);
}
