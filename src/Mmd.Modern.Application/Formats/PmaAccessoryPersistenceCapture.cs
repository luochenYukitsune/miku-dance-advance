using System.Security.Cryptography;
using System.Text.Json;

namespace Mmd.Modern.Application;

/// <summary>A prepared independent accessory and the canonical package resource containing its original X bytes.</summary>
public sealed record PmaAccessoryPersistenceItem(SceneProjectAccessory Accessory, string SourceResourcePath);

/// <summary>Opt-in neutral proof supplied by a trusted traditional asset adapter after it has validated a
/// complete portable accessory set. The adapter owns its opaque schema and texture/FX dependency policy;
/// this bridge binds exact live identities and original X provenance to that schema and resource bytes.</summary>
public sealed class PmaAccessoryPersistenceCapture
{
    private readonly SceneProjectAccessory[] accessories;
    private readonly Dictionary<string, ResourceIdentity> resources;
    private sealed record ResourceIdentity(long Length, string Sha256);

    public string ExtensionName { get; }
    public JsonElement Payload { get; }
    public IReadOnlyList<SceneProjectAccessory> Accessories { get; }
    public IReadOnlyList<string> RequiredResourcePaths { get; }

    /// <summary>Freezes metadata and resource identities without retaining caller-owned byte buffers.
    /// Each item must use canonical frozen X provenance and name its exact original-byte resource.
    /// requiredResources must include every dependency required by the supplying adapter.</summary>
    public PmaAccessoryPersistenceCapture(string extensionName, JsonElement payload,
        IEnumerable<PmaAccessoryPersistenceItem> items, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> requiredResources)
    {
        ArgumentNullException.ThrowIfNull(items); ArgumentNullException.ThrowIfNull(requiredResources);
        if (string.IsNullOrWhiteSpace(extensionName) || extensionName.Length > 256 || !extensionName.Contains('.')
            || extensionName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new InvalidDataException("Accessory persistence requires a namespaced ASCII extension identity.");
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Accessory persistence requires a complete opaque extension object.");
        var captured = items.ToArray();
        if (captured.Length == 0 || captured.Any(item => item is null || item.Accessory is null
                || item.Accessory.ObjectId == Guid.Empty || item.Accessory.Asset is null || item.Accessory.Asset.Diagnostics is null)
            || captured.Select(item => item.Accessory.ObjectId).Distinct().Count() != captured.Length
            || captured.Select(item => item.Accessory.Asset.Index).Distinct().Count() != captured.Length
            || captured.Select(item => item.SourceResourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != captured.Length)
            throw new InvalidDataException("Accessory persistence requires complete unique object, stored-ID and original-resource identities.");
        resources = new(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in requiredResources)
        {
            PmaAdditionalResources.ValidatePath(pair.Key);
            if (!resources.TryAdd(pair.Key, new(pair.Value.Length, Convert.ToHexStringLower(SHA256.HashData(pair.Value.Span)))))
                throw new InvalidDataException("Accessory persistence contains colliding required resource identities.");
        }
        foreach (var item in captured)
        {
            PmaAdditionalResources.ValidatePath(item.SourceResourcePath);
            var source = item.Accessory.Asset.RequirePortableSource();
            if (!resources.TryGetValue(item.SourceResourcePath, out var original)
                || original.Length != source.ByteLength || original.Sha256 != source.Sha256)
                throw new InvalidDataException("Accessory persistence must carry the exact frozen original X bytes for every stored accessory.");
        }
        ExtensionName = extensionName; Payload = payload.Clone();
        accessories = captured.Select(item => item.Accessory with
            { Asset = item.Accessory.Asset with { Diagnostics = Array.AsReadOnly(item.Accessory.Asset.Diagnostics.ToArray()) } }).ToArray();
        Accessories = Array.AsReadOnly(accessories);
        RequiredResourcePaths = Array.AsReadOnly(resources.Keys.ToArray());
    }

    internal void ValidateLive(IReadOnlyList<SceneProjectAccessory> actual)
    {
        if (actual.Count != accessories.Length) throw new InvalidDataException("Accessory persistence capture does not cover the complete live accessory set.");
        var byId = accessories.ToDictionary(item => item.ObjectId);
        foreach (var item in actual)
        {
            _ = item.Asset.RequirePortableSource();
            if (!byId.TryGetValue(item.ObjectId, out var expected) || item.Asset.Index != expected.Asset.Index
                || !ReferenceEquals(item.Asset.SourceSnapshot, expected.Asset.SourceSnapshot)
                || !ReferenceEquals(item.Asset.Document, expected.Asset.Document) || !ReferenceEquals(item.Asset.Source, expected.Asset.Source)
                || item.Asset.Path != expected.Asset.Path || item.Asset.Sha256 != expected.Asset.Sha256)
                throw new InvalidDataException("Accessory persistence capture disagrees with a live Guid, stored ID or immutable source binding.");
        }
    }

    internal void ValidateDeclaredIds(IEnumerable<byte> storedIds, IReadOnlyCollection<Guid> modelIds)
    {
        if (!accessories.Select(item => item.Asset.Index).ToHashSet().SetEquals(storedIds)
            || accessories.Any(item => modelIds.Contains(item.ObjectId)))
            throw new InvalidDataException("Accessory persistence does not match the complete PMM accessory table and independent scene identities.");
    }

    internal void ValidateSelected(Dictionary<string, JsonElement> extensions, Dictionary<string, byte[]> supplied,
        PmaRetainedState? retained, PmaContentSelection? selection, CancellationToken token)
    {
        ValidatePayload(extensions);
        foreach (var pair in resources)
        {
            token.ThrowIfCancellationRequested();
            if (selection?.ExcludesResource(pair.Key) == true)
                throw new InvalidDataException("Package content selection removes a required accessory resource.");
            ResourceIdentity? actual = supplied.TryGetValue(pair.Key, out var bytes)
                ? new(bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)))
                : retained?.Resources.FirstOrDefault(item => item.Path == pair.Key) is { } resource
                    ? new(resource.Length, resource.Sha256) : null;
            if (actual != pair.Value) throw new InvalidDataException("Selected accessory resource is missing or differs from its validated frozen bytes.");
        }
    }

    internal void ValidatePackage(PmaRetainedState retained)
    {
        ValidatePayload(retained.Extensions);
        foreach (var pair in resources)
        {
            var actual = retained.Resources.FirstOrDefault(item => item.Path == pair.Key);
            if (actual is null || actual.Length != pair.Value.Length || actual.Sha256 != pair.Value.Sha256)
                throw new InvalidDataException("Prepared accessories disagree with validated package resource identities.");
        }
    }

    private void ValidatePayload(IReadOnlyDictionary<string, JsonElement> extensions)
    {
        if (!extensions.TryGetValue(ExtensionName, out var payload) || !JsonElement.DeepEquals(Payload, payload))
            throw new InvalidDataException("Accessory persistence requires its exact validated opaque extension payload.");
    }
}

public sealed partial class ScenePmmControlState
{
    // This only reuses the canonical PMM reader on the already validated payload. Native-name skeleton
    // matching remains in Restore; no second timeline, parent mapping or authored representation is created.
    internal static void ValidateAccessoryPersistencePayload(PmaAccessoryPersistenceCapture persistence,
        ScenePmmControlPayload? payload, IReadOnlyCollection<Guid> modelIds)
    {
        if (payload is null) throw new InvalidDataException("Prepared accessory persistence requires complete PMM control metadata.");
        persistence.ValidateDeclaredIds(Read(payload.DocumentBytes, payload.AnsiCodePage).Accessories.Select(item => item.Index), modelIds);
    }
}
