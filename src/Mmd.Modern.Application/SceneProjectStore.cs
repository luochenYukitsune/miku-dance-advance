using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public sealed record PreparedSceneProject(SceneCompositionEditor Composition, SceneLightingEditor Lighting);
public sealed class SceneProjectAssetException(Guid objectId, string sourcePath, Exception inner)
    : IOException($"Cannot prepare scene object {objectId}: {sourcePath}", inner)
{
    public Guid ObjectId { get; } = objectId;
    public string SourcePath { get; } = sourcePath;
}

public static class SceneProjectStore
{
    public const string Format = "mmd-modern-static-scene";
    public const int Version = 1;
    public const int PortableVersion = 2;
    public const int LightingGroupsVersion = 3;
    public const int PortableLightingGroupsVersion = 4;
    public const int LightingParentsVersion = 5;
    public const int PortableLightingParentsVersion = 6;
    public const int LightingReceiversVersion = 7;
    public const int PortableLightingReceiversVersion = 8;
    public const int LightingAreasVersion = 9;
    public const int PortableLightingAreasVersion = 10;
    public const int LightingSettingsVersion = 11;
    public const int PortableLightingSettingsVersion = 12;
    /// <summary>Aim-normalized light falloff. Any document with lights is written in this version;
    /// lights from earlier versions are converted on load.</summary>
    public const int LightingFalloffVersion = 13;
    public const int PortableLightingFalloffVersion = 14;
    public const int MaxObjects = 1024;
    public const int MaxDocumentBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>Captures external asset references, layout and native lights without copying or publishing resources.
    /// Use Save for a portable project. This is not PMM or an animation project.</summary>
    public static string Serialize(string path, SceneCompositionEditor composition, SceneLightingEditor lighting)
        => Serialize(path, composition, lighting, null);

    internal static string Serialize(string path, SceneCompositionEditor composition, SceneLightingEditor lighting, PortableModelAssets.Capture? portable)
    {
        string parent = Path.GetDirectoryName(ValidateProjectPath(path))!;
        ArgumentNullException.ThrowIfNull(composition); ArgumentNullException.ThrowIfNull(lighting);
        if (lighting.IsEditing) throw new InvalidOperationException("Commit or cancel the light edit gesture before saving.");
        bool hasParents = lighting.Lights.Any(light => light.Parent is not null);
        bool hasReceivers = lighting.Lights.Any(light => light.Receivers is not null);
        bool hasAreas = lighting.Lights.Any(light => light.Kind == SceneLightKind.Area || light.AreaSize is not null);
        bool hasFalloff = lighting.Lights.Count > 0;
        bool hasSettings = hasFalloff || lighting.Settings != SceneLightingSettings.Default;
        var document = new Document
        {
            Format = Format, Version = hasFalloff ? (portable is null ? LightingFalloffVersion : PortableLightingFalloffVersion)
                : hasSettings ? (portable is null ? LightingSettingsVersion : PortableLightingSettingsVersion)
                : hasAreas ? (portable is null ? LightingAreasVersion : PortableLightingAreasVersion)
                : hasReceivers ? (portable is null ? LightingReceiversVersion : PortableLightingReceiversVersion)
                : hasParents ? (portable is null ? LightingParentsVersion : PortableLightingParentsVersion)
                : lighting.Groups.Count == 0
                ? (portable is null ? Version : PortableVersion)
                : (portable is null ? LightingGroupsVersion : PortableLightingGroupsVersion),
            PortableModels = portable?.Hashes,
            Objects = composition.Objects.Select(item =>
            {
                RequireStatic(item.Source);
                if (!Path.IsPathFullyQualified(item.SourcePath))
                    throw new ArgumentException("Loaded asset paths must be fully qualified; never resolve them from the working directory.");
                var purpose = lighting.ModelRoles.TryGetValue(item.Key, out var value) ? value : new(item.Role);
                return new ObjectData
                {
                    Id = item.Id, Name = item.Name, NameFollowsSource = item.NameFollowsSource,
                    SourcePath = portable is not null ? portable.Paths[item.Id]
                        : Path.GetRelativePath(parent, Path.GetFullPath(item.SourcePath)).Replace('\\', '/'),
                    Position = [item.Position.X, item.Position.Y, item.Position.Z],
                    Rotation = [item.Rotation.X, item.Rotation.Y, item.Rotation.Z, item.Rotation.W], Scale = item.Scale,
                    Purpose = new() { Role = purpose.Role, EnvironmentIndoor = purpose.EnvironmentIndoor },
                };
            }).ToArray(),
            SelectedObjectId = composition.SelectedId, SelectedLightId = lighting.SelectedId,
            Lights = lighting.Lights.Select(SceneLightingEditor.LightData.From).ToArray(),
            Groups = lighting.Groups.Count == 0 && !hasParents && !hasReceivers && !hasAreas && !hasSettings ? null : lighting.Groups.Select(SceneLightingEditor.GroupData.From).ToArray(),
            Settings = hasSettings ? SceneLightingEditor.SettingsData.From(lighting.Settings) : null,
            HasSettingsField = hasSettings,
        };
        Validate(document);
        _ = composition.Frame; // Do not overwrite a valid document with transforms that cannot form a valid scene.
        string json = JsonSerializer.Serialize(document, JsonOptions);
        if (Utf8.GetByteCount(json) > MaxDocumentBytes) throw new InvalidDataException("Scene document exceeds the supported size.");
        return json;
    }

    /// <summary>Copies models and dependencies into the project resource directory, then writes with same-directory
    /// temporary replacement. Never writes to a PMM/PMX/PMD source asset.</summary>
    public static void Save(string path, SceneCompositionEditor composition, SceneLightingEditor lighting)
        => Save(path, composition, lighting, new());

    public static void Save(string path, SceneCompositionEditor composition, SceneLightingEditor lighting,
        SceneProjectSaveOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); cancellationToken.ThrowIfCancellationRequested();
        string destination = ValidateProjectPath(path);
        string json = Serialize(destination, composition, lighting);
        using var portable = options.CopyModelAssets ? PortableModelAssets.Prepare(composition.Objects, cancellationToken, limits: options.PortableLimits) : null;
        if (portable is not null) json = Serialize(destination, composition, lighting, portable);
        byte[] bytes = Utf8.GetBytes(json);
        portable?.Publish(Path.GetDirectoryName(destination)!, cancellationToken);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { cancellationToken.ThrowIfCancellationRequested(); stream.Write(bytes); stream.Flush(flushToDisk: true); }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static PreparedSceneProject Load(string path, Func<string, CancellationToken, OriginalFrameScene> load, CancellationToken cancellationToken = default)
    {
        string fullPath = ValidateProjectPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        // Bound the actual read, including if another process grows the file after it was opened.
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxDocumentBytes) throw new InvalidDataException("Scene document exceeds the supported size.");
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[8192]; int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes.Length + count > MaxDocumentBytes) throw new InvalidDataException("Scene document exceeds the supported size.");
            bytes.Write(buffer, 0, count);
        }
        return Prepare(Utf8.GetString(bytes.ToArray()).TrimStart('\uFEFF'), fullPath, load, cancellationToken);
    }

    /// <summary>Validates all metadata, prepares all assets and composes a frame before returning a fresh candidate.
    /// The caller replaces its active session only after success; no active editor is accepted or mutated here.</summary>
    public static PreparedSceneProject Prepare(string json, string path, Func<string, CancellationToken, OriginalFrameScene> load, CancellationToken cancellationToken = default)
        => Prepare(json, path, load, cancellationToken, null);

    internal static PreparedSceneProject Prepare(string json, string path, Func<string, CancellationToken, OriginalFrameScene> load,
        CancellationToken cancellationToken, PortableModelAssets.Limits? portableLimits)
    {
        string parent = Path.GetDirectoryName(ValidateProjectPath(path))!;
        ArgumentNullException.ThrowIfNull(load);
        cancellationToken.ThrowIfCancellationRequested();
        var document = ReadDocument(json);
        var lights = Validate(document);
        PortableModelAssets.Verify(parent, document.PortableModels, document.Objects.Select(item => item.SourcePath), cancellationToken, portableLimits);
        // Resolve every path before invoking resource loaders. Source references are relative to this document, not cwd.
        var paths = document.Objects.Select(item => Path.GetFullPath(item.SourcePath, parent)).ToArray();
        var objects = new List<EditableSceneObject>();
        for (int i = 0; i < document.Objects.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = document.Objects[i]; string sourcePath = paths[i];
            try
            {
                if (!File.Exists(sourcePath)) throw new FileNotFoundException("Referenced PMX/PMD asset was not found.", sourcePath);
                var source = load(sourcePath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                RequireStatic(source);
                objects.Add(new(item.Id, item.Name, sourcePath, item.Purpose.Role, source,
                    item.GetPosition(), item.GetRotation(), item.Scale) { NameFollowsSource = item.NameFollowsSource });
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or PmxFormatException
                or ArgumentException or InvalidOperationException or NotSupportedException)
            { throw new SceneProjectAssetException(item.Id, sourcePath, exception); }
        }
        var composition = SceneCompositionEditor.FromObjects(objects, document.SelectedObjectId);
        _ = composition.Frame; // A successful candidate must actually be composable.
        cancellationToken.ThrowIfCancellationRequested();
        var lighting = new SceneLightingEditor();
        lighting.ReplaceSnapshot(lights, document.SelectedLightId);
        foreach (var item in document.Objects)
            lighting.SetModelRole("scene-object:" + item.Id.ToString("D"), item.Purpose.Role, item.Purpose.EnvironmentIndoor);
        // Reuse the lighting session's validated restore path to start with an empty history.
        string sidecar = lighting.SerializeSidecar();
        if (!lighting.TryLoadSidecar(sidecar, out var error)) throw new InvalidDataException(error);
        return new(composition, lighting);
    }

    /// <summary>Reuses the strict static schema before an animation loader opens any model or motion asset.</summary>
    internal static IReadOnlyList<Guid> ValidateLayoutDocument(string json, string path, PortableModelAssets.Limits? verifyPortableAssets = null,
        CancellationToken cancellationToken = default)
    {
        string parent = Path.GetDirectoryName(ValidateProjectPath(path))!;
        var document = ReadDocument(json);
        _ = Validate(document);
        if (verifyPortableAssets is not null)
            PortableModelAssets.Verify(parent, document.PortableModels, document.Objects.Select(item => item.SourcePath), cancellationToken, verifyPortableAssets);
        foreach (var item in document.Objects) _ = Path.GetFullPath(item.SourcePath, parent);
        return Array.AsReadOnly(document.Objects.Select(item => item.Id).ToArray());
    }

    private static Document ReadDocument(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Utf8.GetByteCount(json) > MaxDocumentBytes) throw new InvalidDataException("Scene document exceeds the supported size.");
        return JsonSerializer.Deserialize<Document>(json, JsonOptions) ?? throw new JsonException("A scene must be an object.");
    }

    private static string ValidateProjectPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), ".mmdscene", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a fully qualified .mmdscene path; original asset formats cannot be overwritten.", nameof(path));
        return Path.GetFullPath(path);
    }

    private static void RequireStatic(OriginalFrameScene frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Surface.Snapshot.HasDynamicVertices)
            throw new NotSupportedException("Version 1 stores static source assets and layout, not animated or physics-deformed frames.");
        if (frame.Outline is null) throw new ArgumentException("A scene asset needs matching outline geometry.");
    }

    private static SceneLightingSnapshot Validate(Document document)
    {
        if (document.Format != Format || document.Version is not (Version or PortableVersion or LightingGroupsVersion or PortableLightingGroupsVersion
            or LightingParentsVersion or PortableLightingParentsVersion or LightingReceiversVersion or PortableLightingReceiversVersion
            or LightingAreasVersion or PortableLightingAreasVersion or LightingSettingsVersion or PortableLightingSettingsVersion
            or LightingFalloffVersion or PortableLightingFalloffVersion))
            throw new JsonException("Unsupported scene format or version.");
        if (document.Version < LightingSettingsVersion && document.HasSettingsField)
            throw new JsonException("Global lighting settings require static layout version 11 through 14.");
        if (document.Version >= LightingSettingsVersion && document.Settings is null)
            throw new JsonException("A non-null settings object is required in static layout version 11 through 14.");
        if (document.Objects is null || document.Objects.Length is < 1 or > MaxObjects || document.Lights is null)
            throw new JsonException("A scene requires an objects array within the supported budget and a lights array.");
        bool portable = document.Version is PortableVersion or PortableLightingGroupsVersion or PortableLightingParentsVersion or PortableLightingReceiversVersion or PortableLightingAreasVersion or PortableLightingSettingsVersion or PortableLightingFalloffVersion;
        bool grouped = document.Version >= LightingGroupsVersion;
        if (document.Version < LightingAreasVersion && document.Lights.Any(item => item?.HasAreaSizeField == true || item?.Kind == SceneLightKind.Area))
            throw new JsonException("Area lights and dimensions require static layout version 9 or 10.");
        if (document.Version < LightingReceiversVersion && document.Lights.Any(item => item?.HasReceiversField == true))
            throw new JsonException("Light receivers require static layout version 7 or 8.");
        if (document.Version < LightingParentsVersion && document.Lights.Any(item => item?.HasParentField == true))
            throw new JsonException("Light parents require static layout version 5 or 6.");
        if (portable != (document.PortableModels is not null))
            throw new JsonException("Portable model metadata requires static layout version 2, 4, 6, 8, 10, 12 or 14.");
        if (grouped && document.Groups is null)
            throw new JsonException("A groups array is required in static layout version 3 through 14.");
        if (!grouped && (document.Groups is not null || document.Lights.Any(item => item?.GroupId is not null)))
            throw new JsonException("Light groups and membership require static layout version 3 through 14.");
        var ids = new HashSet<Guid>();
        foreach (var item in document.Objects)
        {
            if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id)) throw new JsonException("Object identities must be non-empty and unique.");
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Name); ArgumentException.ThrowIfNullOrWhiteSpace(item.SourcePath);
            if (item.Purpose is null) throw new JsonException("An object purpose is required.");
            SceneLightingEditor.ValidatePurpose(item.Id.ToString(), item.Purpose.Role, item.Purpose.EnvironmentIndoor);
            SceneCompositionEditor.ValidateTransform(item.GetPosition(), item.GetRotation(), item.Scale);
            if ((Path.IsPathRooted(item.SourcePath) && !Path.IsPathFullyQualified(item.SourcePath))
                || (!Path.IsPathFullyQualified(item.SourcePath) && item.SourcePath.Contains(':'))
                || item.SourcePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0
                || !(string.Equals(Path.GetExtension(item.SourcePath), ".pmx", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetExtension(item.SourcePath), ".pmd", StringComparison.OrdinalIgnoreCase)))
                throw new JsonException("Assets must reference unambiguous relative or absolute local PMX/PMD paths.");
        }
        PortableModelAssets.ValidateMetadata(document.PortableModels, document.Objects.Select(item => item.SourcePath));
        if (document.SelectedObjectId.HasValue && !ids.Contains(document.SelectedObjectId.Value)) throw new JsonException("Selected object does not exist.");
        bool legacyFalloff = document.Version < LightingFalloffVersion;
        var lights = new SceneLightingSnapshot(document.Lights.Select(item => item?.ToLight(legacyFalloff) ?? throw new JsonException("Null light.")),
            document.Groups?.Select(item => item?.ToGroup() ?? throw new JsonException("Null light group.")), document.Settings?.ToSettings());
        if (lights.Lights.Any(light => light.Parent is { } parent && !ids.Contains(parent.ObjectId)))
            throw new JsonException("A light parent must reference a model in the same layout.");
        if (lights.Lights.Any(light => light.Receivers?.ObjectIds.Any(id => !ids.Contains(id)) == true))
            throw new JsonException("Light receivers must reference models in the same layout.");
        if (document.SelectedLightId.HasValue && !lights.Lights.Any(item => item.Id == document.SelectedLightId.Value))
            throw new JsonException("Selected light does not exist.");
        return lights;
    }

    private sealed class Document
    {
        private SceneLightingEditor.SettingsData? settings;
        public required string Format { get; init; }
        public required int Version { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? PortableModels { get; init; }
        public required ObjectData[] Objects { get; init; }
        public required Guid? SelectedObjectId { get; init; }
        public required SceneLightingEditor.LightData[] Lights { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SceneLightingEditor.GroupData[]? Groups { get; init; }
        public required Guid? SelectedLightId { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SceneLightingEditor.SettingsData? Settings
        {
            get => settings;
            init { settings = value; HasSettingsField = true; }
        }
        [JsonIgnore]
        public bool HasSettingsField { get; init; }
    }
    private sealed class PurposeData
    {
        public required ModelRole Role { get; init; }
        public required bool? EnvironmentIndoor { get; init; }
    }
    private sealed class ObjectData
    {
        public required Guid Id { get; init; }
        public required string Name { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? NameFollowsSource { get; init; }
        public required string SourcePath { get; init; }
        public required PurposeData Purpose { get; init; }
        public required float[] Position { get; init; }
        public required float[] Rotation { get; init; }
        public required float Scale { get; init; }
        public Vector3 GetPosition() => Position is { Length: 3 } ? new(Position[0], Position[1], Position[2]) : throw new JsonException("Position requires XYZ.");
        public Quaternion GetRotation() => Rotation is { Length: 4 } ? new(Rotation[0], Rotation[1], Rotation[2], Rotation[3]) : throw new JsonException("Rotation requires XYZW.");
    }
}
