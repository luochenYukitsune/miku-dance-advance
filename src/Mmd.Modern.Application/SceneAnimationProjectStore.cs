using System.Security.Cryptography;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

public sealed record SceneAnimationModelAsset(PmxModel Model, OriginalFrameScene Source);

/// <summary>Owns only its freshly prepared animation session. Replace an active project only after Load succeeds.</summary>
public sealed class PreparedSceneAnimationProject : IDisposable
{
    public SceneCompositionEditor Composition { get; }
    public SceneLightingEditor Lighting { get; }
    public SceneAnimationSession Animation { get; }
    public ProjectSettings Settings { get; }
    public IReadOnlyList<SceneMediaTrack> Media { get; }
    /// <summary>Runtime-only prepared inputs supplied by an explicitly selected package adapter.
    /// The neutral animation document does not serialize accessory assets.</summary>
    public IReadOnlyList<SceneProjectAccessory> Accessories { get; }
    internal PreparedSceneAnimationProject(PreparedSceneProject layout, SceneAnimationSession animation,
        ProjectSettings? settings = null, IEnumerable<SceneMediaTrack>? media = null, IEnumerable<SceneProjectAccessory>? accessories = null)
    {
        Composition = layout.Composition; Lighting = layout.Lighting; Animation = animation;
        Settings = settings ?? new(); Media = Array.AsReadOnly(media?.ToArray() ?? []);
        Accessories = Array.AsReadOnly(accessories?.ToArray() ?? []);
    }
    public void Dispose() => Animation.Dispose();
}

public sealed class SceneProjectMotionException(Guid objectId, string sourcePath, Exception inner)
    : IOException($"Cannot prepare scene motion for {objectId}: {sourcePath}", inner)
{
    public Guid ObjectId { get; } = objectId;
    public string SourcePath { get; } = sourcePath;
}

public sealed class SceneProjectSceneMotionException(string sourcePath, Exception inner)
    : IOException($"Cannot prepare scene camera/light/shadow motion: {sourcePath}", inner)
{
    public string SourcePath { get; } = sourcePath;
}

/// <summary>Versioned animation project over the existing static scene schema. Validated VMD snapshots,
/// including edits, and model/texture dependencies are copied by default. Set CopyModelAssets to false for external
/// model references. Version 2 uses CP932;
/// version 3 retains explicit encoding; version 4 retains mixed-name decoding profiles;
/// version 5 stores outside parents and version 6 adds a camera/light/self-shadow VMD asset.
/// Version 7 adds effective project settings and immutable media resources.
/// Version 8 retains PMM controls; version 9 adds explicitly detached replacement data.
/// No GUI, decoder, renderer or solver serialization.</summary>
public static class SceneAnimationProjectStore
{
    public const string Format = "mmd-modern-scene";
    public const int Version = 2;
    public const int EncodedMotionVersion = 3;
    public const int MixedNameVersion = 4;
    public const int OutsideParentVersion = 5;
    public const int SceneMotionVersion = 6;
    public const int SettingsVersion = 7;
    public const int PmmControlsVersion = 8;
    public const int ModelReplacementVersion = 9;
    public const long MaxMediaBytes = 512L * 1024 * 1024;
    public const long MaxAssetBytes = 256L * 1024 * 1024;
    public const int MaxDocumentBytes = SceneProjectStore.MaxDocumentBytes;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    };

    public static void Save(string path, SceneCompositionEditor composition, SceneLightingEditor lighting,
        SceneAnimationSession animation, CancellationToken cancellationToken = default)
        => Save(path, composition, lighting, animation, new(), cancellationToken);

    public static void Save(string path, SceneCompositionEditor composition, SceneLightingEditor lighting,
        SceneAnimationSession animation, SceneProjectSaveOptions options, CancellationToken cancellationToken = default)
        => SaveCore(path, composition, lighting, animation, null, [], options, cancellationToken);

    public static void Save(string path, SceneCompositionEditor composition, SceneLightingEditor lighting,
        SceneAnimationSession animation, ProjectSettings settings, IEnumerable<SceneMediaTrack> media,
        SceneProjectSaveOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings); ArgumentNullException.ThrowIfNull(media);
        SaveCore(path, composition, lighting, animation, settings, media.ToArray(), options, cancellationToken);
    }

    private static void SaveCore(string path, SceneCompositionEditor composition, SceneLightingEditor lighting,
        SceneAnimationSession animation, ProjectSettings? settings, SceneMediaTrack[] media,
        SceneProjectSaveOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(animation);
        SaveSnapshot(path, composition, lighting, animation.CaptureSaveSnapshot(), settings, media, options, cancellationToken);
    }

    // Accepts only a detached authored capture. PMA background saves must never read an animation owner.
    internal static void SaveSnapshot(string path, SceneCompositionEditor composition, SceneLightingEditor lighting,
        SceneAnimationSession.SaveSnapshot state, ProjectSettings? settings, SceneMediaTrack[] media,
        SceneProjectSaveOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options); settings?.Validate(); ValidateMediaTracks(media);
        string destination = ProjectPath(path), parent = Path.GetDirectoryName(destination)!;
        ArgumentNullException.ThrowIfNull(composition); ArgumentNullException.ThrowIfNull(lighting);
        ArgumentNullException.ThrowIfNull(state); cancellationToken.ThrowIfCancellationRequested();
        if (state.CoupledSoftBodies && settings is null)
            settings = new() { Physics = new() { CoupledSoftBodies = true, SharedWorld = state.SharedPhysicsWorld } };
        if (state.CoupledSoftBodies != (settings?.Physics.CoupledSoftBodies ?? false))
            throw new InvalidOperationException("Saved coupled-physics selection must match the active animation session.");
        if (!composition.Objects.SequenceEqual(state.Objects) || !lighting.Lights.SequenceEqual(state.Lighting.Lights))
            throw new InvalidOperationException("The layout or lights changed after this animation session was created; rebuild the session before saving.");
        if (state.Motions.Any(binding => binding.Asset is null || !ReferenceEquals(binding.Motion, binding.Asset.Motion)))
            throw new NotSupportedException("Saving requires each bound motion's captured SceneMotionAsset; an unrelated file path cannot represent an in-memory motion.");
        IEnumerable<SceneMotionAsset> capturedAssets = state.Motions.Select(binding => binding.Asset!);
        if (state.SceneMotion is { } capturedScene) capturedAssets = capturedAssets.Append(capturedScene);
        var unique = capturedAssets.DistinctBy(asset => asset.Sha256).ToArray();
        if (unique.Sum(asset => (long)asset.Length) > MaxAssetBytes)
            throw new InvalidDataException("Copied motion assets exceed the supported total size.");
        string layoutJson = SceneProjectStore.Serialize(destination, composition, lighting);
        using var owned = options.PreparedPortable is null && options.CopyModelAssets
            ? PortableModelAssets.Prepare(composition.Objects, cancellationToken, limits: options.PortableLimits) : null;
        var portable = options.PreparedPortable ?? owned;
        if (portable is not null) layoutJson = SceneProjectStore.Serialize(destination, composition, lighting, portable);
        using var layout = JsonDocument.Parse(layoutJson);
        var document = new Document
        {
            Format = Format, Version = state.Motions.Any(m => m.Detachment is not null) || state.PmmControls?.HasReplacementMetadata == true ? ModelReplacementVersion : state.PmmControls is not null ? PmmControlsVersion : settings is not null ? SettingsVersion : state.SceneMotion is not null ? SceneMotionVersion : state.OutsideParents.Count != 0 ? OutsideParentVersion
                : state.Motions.Any(binding => binding.Asset!.NameOverrides.Count != 0) ? MixedNameVersion
                : state.Motions.Any(binding => binding.Asset!.AnsiCodePage != 932) ? EncodedMotionVersion : Version,
            Layout = layout.RootElement.Clone(), CurrentFrame = state.CurrentFrame,
            Settings = state.PmmControls is not null ? settings ?? new() : settings, Media = settings is null && state.PmmControls is null ? null : media.Select(track => new MediaData
            { Id = track.Id, Kind = track.Kind, AssetPath = MediaPath(track.Asset.Sha256), Sha256 = track.Asset.Sha256,
                Name = track.Asset.Name, OffsetTicks = track.OffsetTicks, Enabled = track.Enabled }).ToArray(),
            SceneMotion = state.SceneMotion is { } scene ? Resource(scene) : null,
            PmmControls = state.PmmControls?.Capture(),
            OutsideParents = state.OutsideParents.Count == 0 ? null : state.OutsideParents.Select(binding => new OutsideParentData
            { ObjectId = binding.ObjectId, BoneIndex = binding.BoneIndex, ParentObjectId = binding.ParentObjectId,
                ParentBoneIndex = binding.ParentBoneIndex, Offset = MatrixValues(binding.Offset) }).ToArray(),
            Motions = state.Motions.Select(binding => new MotionData
            {
                Detachment = binding.Detachment, ObjectId = binding.ObjectId, AssetPath = ResourcePath(binding.Asset!.Sha256), Sha256 = binding.Asset.Sha256,
                Name = binding.Asset.Name, IkEnabled = binding.IkEnabled, MorphsEnabled = binding.MorphsEnabled, PhysicsEnabled = binding.PhysicsEnabled,
                AnsiCodePage = binding.Asset.AnsiCodePage == 932 ? null : binding.Asset.AnsiCodePage,
                NameOverrides = binding.Asset.NameOverrides.Count == 0 ? null : binding.Asset.NameOverrides.Select(entry => new NameOverrideData
                { Kind = entry.Kind, Value = entry.Name.Value, RawHex = Convert.ToHexString(entry.Name.Bytes.ToArray()), AnsiCodePage = entry.Name.AnsiCodePage }).ToArray(),
            }).ToArray(),
        };
        Validate(document, destination);
        var sizes = unique.ToDictionary(asset => asset.Sha256, asset => asset.Length);
        if (Resources(document).DistinctBy(item => (item.Sha256, item.AnsiCodePage ?? 932, ProfileKey(item)))
            .Sum(item => (long)sizes[item.Sha256]) > MaxAssetBytes)
            throw new InvalidDataException("Decoded motion profiles exceed the supported total size.");
        string json = JsonSerializer.Serialize(document, JsonOptions);
        if (Utf8.GetByteCount(json) > MaxDocumentBytes) throw new InvalidDataException("Animation project exceeds the supported size.");
        portable?.Publish(parent, cancellationToken);
        // Published resource names are immutable. A failed document replacement cannot change old referenced assets.
        foreach (var asset in unique)
        { cancellationToken.ThrowIfCancellationRequested(); StoreAsset(parent, asset, cancellationToken); }
        foreach (var asset in media.Select(track => track.Asset).DistinctBy(asset => asset.Sha256)) StoreMedia(parent, asset, cancellationToken);
        WriteDocument(destination, Utf8.GetBytes(json), cancellationToken);
    }

    public static PreparedSceneAnimationProject Load(string path,
        Func<string, CancellationToken, SceneAnimationModelAsset> loadModel,
        Func<EditableSceneObject, ICharacterPhysicsBackend> physicsFactory, CancellationToken cancellationToken = default)
    {
        string fullPath = ProjectPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        return Prepare(Utf8.GetString(ReadBounded(fullPath, MaxDocumentBytes, cancellationToken)).TrimStart('\uFEFF'),
            fullPath, loadModel, physicsFactory, cancellationToken);
    }

    public static PreparedSceneAnimationProject Load(string path,
        Func<string, CancellationToken, SceneAnimationModelAsset> loadModel,
        IProjectPhysicsBackendProvider provider, CancellationToken cancellationToken = default)
    {
        string fullPath = ProjectPath(path); ArgumentNullException.ThrowIfNull(provider);
        cancellationToken.ThrowIfCancellationRequested();
        return Prepare(Utf8.GetString(ReadBounded(fullPath, MaxDocumentBytes, cancellationToken)).TrimStart('\uFEFF'),
            fullPath, loadModel, provider, cancellationToken);
    }

    public static PreparedSceneAnimationProject Prepare(string json, string path,
        Func<string, CancellationToken, SceneAnimationModelAsset> loadModel,
        Func<EditableSceneObject, ICharacterPhysicsBackend> physicsFactory, CancellationToken cancellationToken = default)
        => PrepareCore(json, path, loadModel, physicsFactory, null, cancellationToken);

    public static PreparedSceneAnimationProject Prepare(string json, string path,
        Func<string, CancellationToken, SceneAnimationModelAsset> loadModel,
        IProjectPhysicsBackendProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return PrepareCore(json, path, loadModel, null, provider, cancellationToken);
    }

    internal static PreparedSceneAnimationProject LoadPma(string path,
        Func<string, CancellationToken, SceneAnimationModelAsset> loadModel,
        IProjectPhysicsBackendProvider provider, PortableModelAssets.Limits portableLimits, CancellationToken token,
        PmaAccessoryPersistenceCapture? accessoryPersistence = null)
    {
        string fullPath = ProjectPath(path); ArgumentNullException.ThrowIfNull(provider);
        token.ThrowIfCancellationRequested();
        return PrepareCore(Utf8.GetString(ReadBounded(fullPath, MaxDocumentBytes, token)).TrimStart('\uFEFF'),
            fullPath, loadModel, null, provider, token, portableLimits, accessoryPersistence);
    }

    private static PreparedSceneAnimationProject PrepareCore(string json, string path,
        Func<string, CancellationToken, SceneAnimationModelAsset> loadModel,
        Func<EditableSceneObject, ICharacterPhysicsBackend>? physicsFactory, IProjectPhysicsBackendProvider? provider,
        CancellationToken cancellationToken, PortableModelAssets.Limits? portableLimits = null,
        PmaAccessoryPersistenceCapture? accessoryPersistence = null)
    {
        string fullPath = ProjectPath(path), parent = Path.GetDirectoryName(fullPath)!;
        ArgumentNullException.ThrowIfNull(json); ArgumentNullException.ThrowIfNull(loadModel);
        if (physicsFactory is null && provider is null) throw new ArgumentNullException(nameof(physicsFactory));
        cancellationToken.ThrowIfCancellationRequested();
        if (Utf8.GetByteCount(json) > MaxDocumentBytes) throw new InvalidDataException("Animation project exceeds the supported size.");
        using var header = JsonDocument.Parse(json);
        if (header.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("A project must be an object.");
        bool staticV1 = header.RootElement.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String
            && format.GetString() == SceneProjectStore.Format;
        string layoutJson; double currentFrame; MotionData[] motions; MotionResourceData? sceneResource = null;
        SceneOutsideParentBinding[] outsideParents = [];
        ScenePmmControlPayload? pmmPayload = null;
        ProjectSettings settings = new(); MediaData[] mediaResources = [];
        if (staticV1)
        { layoutJson = json; currentFrame = 0; motions = []; _ = SceneProjectStore.ValidateLayoutDocument(json, fullPath); }
        else
        {
            var document = JsonSerializer.Deserialize<Document>(json, JsonOptions) ?? throw new JsonException("A project must be an object.");
            Validate(document, fullPath);
            layoutJson = document.Layout.GetRawText(); currentFrame = document.CurrentFrame; motions = document.Motions;
            sceneResource = document.SceneMotion; settings = document.Settings ?? new(); mediaResources = document.Media ?? [];
            pmmPayload = document.PmmControls;
            outsideParents = (document.OutsideParents ?? []).Select(binding => new SceneOutsideParentBinding(
                binding.ObjectId, binding.BoneIndex, binding.ParentObjectId, binding.ParentBoneIndex, ReadMatrix(binding.Offset))).ToArray();
        }
        if (provider is null && settings.Physics != new ProjectPhysicsSettings())
            throw new NotSupportedException("Saved physics settings require IProjectPhysicsBackendProvider; the legacy factory cannot honor them.");
        if (accessoryPersistence is not null)
            ScenePmmControlState.ValidateAccessoryPersistencePayload(accessoryPersistence, pmmPayload,
                SceneProjectStore.ValidateLayoutDocument(layoutJson, fullPath));
        var media = new List<SceneMediaTrack>(); var mediaByHash = new Dictionary<string, SceneMediaAsset>(); long mediaBytes = 0;
        foreach (var resource in mediaResources)
        {
            cancellationToken.ThrowIfCancellationRequested(); RequireManagedDirectory(parent, create: false);
            string source = Path.GetFullPath(resource.AssetPath, parent);
            if (File.Exists(source) && (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Managed media resources must not redirect outside the project tree.");
            if (!mediaByHash.TryGetValue(resource.Sha256, out var mediaAsset))
            {
                var bytes = ReadBounded(source, SceneMediaAsset.MaxBytes, cancellationToken);
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != resource.Sha256)
                    throw new InvalidDataException("Copied media does not match its saved SHA256.");
                mediaBytes += bytes.Length; if (mediaBytes > MaxMediaBytes) throw new InvalidDataException("Media resources exceed the supported total size.");
                mediaAsset = SceneMediaAsset.FromBytes(bytes, resource.Name); mediaByHash.Add(resource.Sha256, mediaAsset);
            }
            media.Add(new(resource.Id, resource.Kind, mediaAsset.WithName(resource.Name), resource.OffsetTicks, resource.Enabled));
        }
        // Verify every motion before invoking any host model loader or allocating a physics world.
        var assets = new Dictionary<(string Hash, int CodePage, string Profile), SceneMotionAsset>();
        var countedHashes = new HashSet<string>(StringComparer.Ordinal);
        long total = 0, parsedTotal = 0;
        IEnumerable<MotionResourceData> resources = motions;
        if (sceneResource is not null) resources = resources.Append(sceneResource);
        foreach (var item in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (item.Sha256, item.AnsiCodePage ?? 932, ProfileKey(item));
            if (assets.ContainsKey(key)) continue;
            string sourcePath = Path.GetFullPath(item.AssetPath, parent);
            try
            {
                RequireManagedDirectory(parent, create: false);
                var asset = SceneMotionAsset.Load(sourcePath, cancellationToken, new() { AnsiCodePage = key.Item2, NameOverrides = ReadProfile(item) });
                if (asset.Sha256 != item.Sha256) throw new InvalidDataException("Copied VMD does not match its saved SHA256.");
                if (countedHashes.Add(item.Sha256)) total += asset.Length;
                parsedTotal += asset.Length;
                if (total > MaxAssetBytes || parsedTotal > MaxAssetBytes) throw new InvalidDataException("Motion resources or decoded profiles exceed the supported total size.");
                assets.Add(key, asset);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or VmdFormatException or ArgumentException)
            {
                if (item is MotionData actor) throw new SceneProjectMotionException(actor.ObjectId, sourcePath, exception);
                throw new SceneProjectSceneMotionException(sourcePath, exception);
            }
        }
        SceneMotionAsset? sceneMotion = null;
        if (sceneResource is { } sceneItem)
        {
            sceneMotion = assets[(sceneItem.Sha256, sceneItem.AnsiCodePage ?? 932, ProfileKey(sceneItem))].WithName(sceneItem.Name);
            try { _ = SceneAnimationSession.PrepareSceneMotion(sceneMotion, currentFrame); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            { throw new SceneProjectSceneMotionException(Path.GetFullPath(sceneItem.AssetPath, parent), exception); }
        }
        var models = new List<SceneAnimationModelAsset>();
        var prepared = SceneProjectStore.Prepare(layoutJson, fullPath, (sourcePath, token) =>
        {
            var model = loadModel(sourcePath, token);
            if (model?.Model is null || model.Source is null) throw new InvalidDataException("A model loader must supply PMX data and its static frame.");
            models.Add(model); return model.Source;
        }, cancellationToken, portableLimits);
        var modelsById = prepared.Composition.Objects.Select((item, index) => (item.Id, models[index].Model))
            .ToDictionary(item => item.Id, item => item.Model);
        var pmmControls = pmmPayload is null ? null : ScenePmmControlState.Restore(pmmPayload, modelsById);
        // Parent/native-name validation requires the actual loaded skeletons. Complete it before the
        // first animation or physics owner is created, then pass these same immutable inputs to Freeze.
        SceneProjectAccessory[] accessories = accessoryPersistence is null ? [] : SceneProjectSession.FreezeAccessories(
            accessoryPersistence.Accessories, prepared.Composition.Objects.Select(item => item.Id).ToHashSet(), pmmControls);
        var bindings = new List<SceneActorMotion>();
        foreach (var item in motions)
        {
            var asset = assets[(item.Sha256, item.AnsiCodePage ?? 932, ProfileKey(item))].WithName(item.Name);
            bindings.Add(new(item.ObjectId, modelsById[item.ObjectId], asset.Motion, item.IkEnabled, item.MorphsEnabled, item.PhysicsEnabled, asset) { Detachment = item.Detachment });
        }
        SceneAnimationSession? candidate = null;
        try
        {
            candidate = new(prepared.Composition.Objects, bindings,
                provider is null ? physicsFactory! : item => provider.CreateIndependent(settings.Physics, item), prepared.Lighting.Snapshot,
                cancellationToken, initialFrame: currentFrame, outsideParents: outsideParents, sceneMotion: sceneMotion,
                sharedPhysicsFactory: settings.Physics.SharedWorld ? () => provider!.CreateShared(settings.Physics) : null,
                softPhysicsFactory: provider is null || settings.Physics.CoupledSoftBodies ? null : item => provider.CreateSoftBody(settings.Physics, item),
                sharedWorldGravity: settings.Physics.SharedWorld ? settings.Physics.Gravity : null, pmmControls: pmmControls,
                coupledSoftBodies: settings.Physics.CoupledSoftBodies, modelRoles: prepared.Lighting.ModelRoles);
            cancellationToken.ThrowIfCancellationRequested();
            return new(prepared, candidate, settings, media, accessories);
        }
        catch { candidate?.Dispose(); throw; }
    }

    private static void Validate(Document document, string path)
    {
        if (document.Format != Format || document.Version is < Version or > ModelReplacementVersion) throw new JsonException("Unsupported animation project format or version.");
        if (!double.IsFinite(document.CurrentFrame) || document.CurrentFrame < 0 || document.CurrentFrame > uint.MaxValue)
            throw new JsonException("The saved frame must be finite and within the supported VMD range.");
        if (document.Layout.ValueKind != JsonValueKind.Object || document.Motions is null || document.Motions.Length > SceneProjectStore.MaxObjects)
            throw new JsonException("A static layout and a bounded motions array are required.");
        if (document.Version < SettingsVersion && (document.Settings is not null || document.Media is not null))
            throw new JsonException("Project settings and media require version 7.");
        if (document.Version >= SettingsVersion && (document.Settings is null || document.Media is null))
            throw new JsonException("Version 7 requires explicit settings and media array.");
        try { document.Settings?.Validate(); ValidateMediaDocument(document.Media ?? []); }
        catch (ArgumentException exception) { throw new JsonException("Invalid project settings or media.", exception); }
        var objectIds = SceneProjectStore.ValidateLayoutDocument(document.Layout.GetRawText(), path).ToHashSet();
        if (document.Version < ModelReplacementVersion && (document.Motions.Any(item => item?.Detachment is not null)
            || document.PmmControls is { } metadata && (metadata.RestoredBindings is not null
                || metadata.Actors?.Any(actor => actor?.NativeBindingNames is not null || actor?.DetachedBoneOrdinals is not null) == true)))
            throw new JsonException("Detached model data and explicit PMM replacement mappings require project version 9.");
        if (document.Version < PmmControlsVersion && document.PmmControls is not null || document.Version == PmmControlsVersion && document.PmmControls is null)
            throw new JsonException("PMM control provenance requires project version 8 and a non-null payload.");
        if (document.PmmControls is { } controls) ScenePmmControlState.ValidatePayload(controls, objectIds);
        var bound = new HashSet<Guid>();
        foreach (var item in document.Motions)
        {
            if (item is null || !objectIds.Contains(item.ObjectId) || !bound.Add(item.ObjectId))
                throw new JsonException("Each motion must bind one distinct existing scene object.");
            ValidateResource(item, document.Version);
        }
        foreach (var parent in document.Motions.SelectMany(item => item.Detachment?.OutsideParents ?? []))
            if (!bound.Contains(parent.ObjectId) || !bound.Contains(parent.ParentObjectId))
                throw new JsonException("Detached outside-parent endpoints must retain existing motion-bound object identities.");
        if (document.SceneMotion is { } scene)
        {
            if (document.Version < SceneMotionVersion) throw new JsonException("Scene motion requires project version 6 or later.");
            ValidateResource(scene, document.Version);
        }
        ValidateOutsideParentDocument(document, bound);
    }

    private static void ValidateResource(MotionResourceData item, int version)
    {
        if ((item.AnsiCodePage.HasValue && version == Version) || (item.AnsiCodePage ?? 932) is not (932 or 936))
            throw new JsonException("Explicit VMD code pages require project version 3 or later and must be 932 or 936.");
        if (item.NameOverrides is not null && version < MixedNameVersion)
            throw new JsonException("Mixed VMD name profiles require project version 4 or later.");
        _ = ReadProfile(item);
        if (item.Sha256 is not { Length: 64 } || item.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || item.AssetPath != ResourcePath(item.Sha256))
            throw new JsonException("Motion assets must use their canonical relative SHA256 path.");
        if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 255 || item.Name != Path.GetFileName(item.Name)
            || item.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(item.Name))
            || !string.Equals(Path.GetExtension(item.Name), ".vmd", StringComparison.OrdinalIgnoreCase))
            throw new JsonException("A motion name must be a VMD base file name.");
    }

    private static IEnumerable<MotionResourceData> Resources(Document document)
        => document.Motions.Cast<MotionResourceData>().Concat(document.SceneMotion is { } scene ? [scene] : []);

    private static MotionResourceData Resource(SceneMotionAsset asset) => new()
    {
        AssetPath = ResourcePath(asset.Sha256), Sha256 = asset.Sha256, Name = asset.Name,
        AnsiCodePage = asset.AnsiCodePage == 932 ? null : asset.AnsiCodePage,
        NameOverrides = asset.NameOverrides.Count == 0 ? null : asset.NameOverrides.Select(entry => new NameOverrideData
        { Kind = entry.Kind, Value = entry.Name.Value, RawHex = Convert.ToHexString(entry.Name.Bytes.ToArray()), AnsiCodePage = entry.Name.AnsiCodePage }).ToArray()
    };

    private static void ValidateOutsideParentDocument(Document document, HashSet<Guid> bound)
    {
        if (document.OutsideParents is null) return;
        if (document.Version < OutsideParentVersion || document.OutsideParents.Length > 100_000)
            throw new JsonException("Outside-parent bindings require version 5 and a bounded array.");
        var slots = new HashSet<(Guid, int)>(); var dependencies = new Dictionary<Guid, List<Guid>>();
        foreach (var binding in document.OutsideParents)
        {
            if (binding is null || binding.ObjectId == binding.ParentObjectId || !bound.Contains(binding.ObjectId)
                || !bound.Contains(binding.ParentObjectId) || binding.BoneIndex < -1 || binding.ParentBoneIndex < 0
                || !slots.Add((binding.ObjectId, binding.BoneIndex)))
                throw new JsonException("Outside-parent bindings need distinct motion-bound objects, valid slots and unique subjects.");
            _ = ReadMatrix(binding.Offset);
            if (!dependencies.TryGetValue(binding.ObjectId, out var parents)) dependencies[binding.ObjectId] = parents = [];
            parents.Add(binding.ParentObjectId);
        }
        var done = new HashSet<Guid>(); var visiting = new HashSet<Guid>();
        void Visit(Guid id)
        {
            if (done.Contains(id)) return;
            if (!visiting.Add(id)) throw new JsonException("Outside-parent dependency cycle.");
            if (dependencies.TryGetValue(id, out var parents)) foreach (var parent in parents) Visit(parent);
            visiting.Remove(id); done.Add(id);
        }
        foreach (var id in dependencies.Keys) Visit(id);
    }

    private static float[] MatrixValues(Matrix4x4 m) => [m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,
        m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
    private static Matrix4x4 ReadMatrix(float[]? values)
    {
        if (values is not { Length: 16 } || values.Any(value => !float.IsFinite(value)))
            throw new JsonException("Binding offset must contain sixteen finite matrix values.");
        var m = new Matrix4x4(values[0],values[1],values[2],values[3],values[4],values[5],values[6],values[7],
            values[8],values[9],values[10],values[11],values[12],values[13],values[14],values[15]);
        if (m.M14 != 0 || m.M24 != 0 || m.M34 != 0 || m.M44 != 1 || !Matrix4x4.Invert(m, out _))
            throw new JsonException("Binding offset must be invertible and affine.");
        return m;
    }

    private static string ProjectPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), ".mmdscene", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a fully qualified .mmdscene path; original assets cannot be overwritten.", nameof(path));
        return Path.GetFullPath(path);
    }
    private static string ResourcePath(string sha256) => "assets/motions/" + sha256 + ".vmd";

    private static IReadOnlyList<VmdNameOverride> ReadProfile(MotionResourceData item)
    {
        if (item.NameOverrides is null) return [];
        if (item.NameOverrides.Length > 100_000) throw new JsonException("VMD name profile exceeds safety limit.");
        var entries = new List<VmdNameOverride>(); var keys = new HashSet<(VmdNameKind, string)>();
        try
        {
            foreach (var entry in item.NameOverrides)
            {
                if (entry is null || !Enum.IsDefined(entry.Kind) || entry.RawHex is null || entry.RawHex.Length > 40)
                    throw new JsonException("Invalid VMD name profile entry.");
                byte[] bytes = Convert.FromHexString(entry.RawHex);
                int width = entry.Kind is VmdNameKind.Model or VmdNameKind.Ik ? 20 : 15;
                if (bytes.Length > width || !keys.Add((entry.Kind, Convert.ToHexString(bytes))))
                    throw new JsonException("VMD name profile has overlong or duplicate raw names.");
                entries.Add(new(entry.Kind, new(entry.Value, bytes, entry.AnsiCodePage)));
            }
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        { throw new JsonException("Invalid VMD name encoding profile.", error); }
        return entries.AsReadOnly();
    }

    private static string ProfileKey(MotionResourceData item)
    {
        var entries = ReadProfile(item).OrderBy(entry => entry.Kind).ThenBy(entry => Convert.ToHexString(entry.Name.Bytes.ToArray()), StringComparer.Ordinal)
            .Select(entry => new { entry.Kind, entry.Name.AnsiCodePage, RawHex = Convert.ToHexString(entry.Name.Bytes.ToArray()) });
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entries)));
    }

    private static void RequireManagedDirectory(string parent, bool create)
    {
        foreach (string segment in new[] { "assets", "assets/motions", "assets/media" })
        {
            string directory = Path.Combine(parent, segment);
            if (create) Directory.CreateDirectory(directory);
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Managed motion directories must not redirect outside the project tree.");
        }
    }

    private static void StoreAsset(string parent, SceneMotionAsset asset, CancellationToken token)
    {
        RequireManagedDirectory(parent, create: true);
        string destination = Path.GetFullPath(ResourcePath(asset.Sha256), parent);
        void VerifyExisting()
        {
            byte[] bytes = ReadBounded(destination, SceneMotionAsset.MaxBytes, token);
            if (bytes.Length != asset.Length || Convert.ToHexStringLower(SHA256.HashData(bytes)) != asset.Sha256)
                throw new InvalidDataException("An existing content-addressed resource differs; it will not be overwritten.");
        }
        if (File.Exists(destination)) { VerifyExisting(); return; }
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { WriteChunks(output, asset.Bytes, token); output.Flush(flushToDisk: true); }
            token.ThrowIfCancellationRequested();
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination)) { VerifyExisting(); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string MediaPath(string hash) => "assets/media/" + hash + ".media";
    private static void ValidateMediaTracks(SceneMediaTrack[] tracks)
    {
        if (tracks.Length > 128) throw new ArgumentException("Too many media tracks.");
        var ids = new HashSet<Guid>();
        foreach (var track in tracks)
        {
            ArgumentNullException.ThrowIfNull(track); track.Validate();
            if (!ids.Add(track.Id)) throw new ArgumentException("Media track IDs must be unique.");
        }
        if (tracks.Select(track => track.Asset).DistinctBy(asset => asset.Sha256).Sum(asset => (long)asset.Length) > MaxMediaBytes)
            throw new InvalidDataException("Copied media exceed the supported total size.");
    }
    private static void ValidateMediaDocument(MediaData[] tracks)
    {
        if (tracks.Length > 128) throw new JsonException("Too many media tracks.");
        var ids = new HashSet<Guid>();
        foreach (var track in tracks)
        {
            if (track is null || track.Id == Guid.Empty || !ids.Add(track.Id) || !Enum.IsDefined(track.Kind) || track.OffsetTicks == long.MinValue
                || track.Sha256 is not { Length: 64 } || track.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                || track.AssetPath != MediaPath(track.Sha256)) throw new JsonException("Media requires unique IDs and canonical relative SHA256 resources.");
            _ = SceneMediaAsset.FromBytes([0], track.Name);
        }
    }
    private static void StoreMedia(string parent, SceneMediaAsset asset, CancellationToken token)
    {
        RequireManagedDirectory(parent, create: true);
        string destination = Path.GetFullPath(MediaPath(asset.Sha256), parent);
        if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Managed media resources cannot be symbolic links.");
        void Verify()
        {
            var bytes = ReadBounded(destination, SceneMediaAsset.MaxBytes, token);
            if (bytes.Length != asset.Length || Convert.ToHexStringLower(SHA256.HashData(bytes)) != asset.Sha256)
                throw new InvalidDataException("Existing immutable media resource differs; it will not be overwritten.");
        }
        if (File.Exists(destination)) { Verify(); return; }
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { WriteChunks(output, asset.Bytes, token); output.Flush(flushToDisk: true); }
            token.ThrowIfCancellationRequested();
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination)) { Verify(); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteDocument(string destination, ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { WriteChunks(output, bytes, token); output.Flush(flushToDisk: true); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void WriteChunks(Stream output, ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        for (int offset = 0; offset < bytes.Length; offset += 64 * 1024)
        { token.ThrowIfCancellationRequested(); output.Write(bytes.Slice(offset, Math.Min(64 * 1024, bytes.Length - offset))); }
    }
    private static byte[] ReadBounded(string path, int maximum, CancellationToken token)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximum) throw new InvalidDataException("Project input exceeds the supported size.");
        using var output = new MemoryStream(); byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int count = input.Read(buffer, 0, Math.Min(buffer.Length, maximum - (int)output.Length + 1));
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new InvalidDataException("Project input exceeds the supported size.");
            output.Write(buffer, 0, count);
        }
    }
    private sealed class Document
    {
        public required string Format { get; init; }
        public required int Version { get; init; }
        public required JsonElement Layout { get; init; }
        public required double CurrentFrame { get; init; }
        public required MotionData[] Motions { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ProjectSettings? Settings { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public MediaData[]? Media { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OutsideParentData[]? OutsideParents { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public MotionResourceData? SceneMotion { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ScenePmmControlPayload? PmmControls { get; init; }
    }
    private sealed class MediaData
    {
        public required Guid Id { get; init; }
        public required SceneMediaKind Kind { get; init; }
        public required string AssetPath { get; init; }
        public required string Sha256 { get; init; }
        public required string Name { get; init; }
        public required long OffsetTicks { get; init; }
        public required bool Enabled { get; init; }
    }

    private sealed class OutsideParentData
    {
        public required Guid ObjectId { get; init; }
        public required int BoneIndex { get; init; }
        public required Guid ParentObjectId { get; init; }
        public required int ParentBoneIndex { get; init; }
        public required float[] Offset { get; init; }
    }
    private class MotionResourceData
    {
        public required string AssetPath { get; init; }
        public required string Sha256 { get; init; }
        public required string Name { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? AnsiCodePage { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public NameOverrideData[]? NameOverrides { get; init; }
    }
    private sealed class MotionData : MotionResourceData
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SceneMotionDetachment? Detachment { get; init; }
        public required Guid ObjectId { get; init; }
        public required bool IkEnabled { get; init; }
        public required bool MorphsEnabled { get; init; }
        public required bool PhysicsEnabled { get; init; }
    }
    private sealed class NameOverrideData
    {
        public required VmdNameKind Kind { get; init; }
        public required string Value { get; init; }
        public required string RawHex { get; init; }
        public required int AnsiCodePage { get; init; }
    }
}
