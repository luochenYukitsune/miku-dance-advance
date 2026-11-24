using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

public enum SceneLightingBuiltinPreset { PortraitSoft, IndoorWindow }
public enum SceneLightingPresetApplyMode { Add, ReplaceSelected }
public sealed record SceneLightingPresetApplication(SceneLightingSnapshot Snapshot,
    IReadOnlyList<Guid> AddedLightIds, IReadOnlyList<Guid> AddedGroupIds);

/// <summary>A portable static lighting selection. Project model/bone and receiver references are
/// deliberately absent; capture resolves selected parents at the caller's already sampled frame.</summary>
public sealed class SceneLightingPreset
{
    public const int MaxFileBytes = 1024 * 1024;
    /// <summary>Version 2 stores aim-normalized intensities; version 1 presets are converted on load.</summary>
    public const int Version = 2;
    public string Name { get; }
    public SceneLightingSnapshot Lighting { get; }

    private SceneLightingPreset(string name, SceneLightingSnapshot lighting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 200 || lighting.Lights.Count == 0 || lighting.Groups.Count > SceneLightingSnapshot.MaxLights)
            throw new ArgumentException("Lighting presets require a name of at most 200 characters and one to 128 lights/groups.");
        if (lighting.Lights.Any(light => light.Parent is not null || light.Receivers is not null))
            throw new ArgumentException("Portable presets cannot retain project-specific light parents or receiver references.");
        Name = name.Trim(); Lighting = lighting;
    }

    public static SceneLightingPreset Capture(string name, SceneLightingSnapshot source, IEnumerable<Guid> lightIds,
        IEnumerable<Guid>? groupIds = null, Func<SceneLightParent, Matrix4x4>? resolveParent = null)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(lightIds);
        var selected = lightIds.ToHashSet(); var groups = (groupIds ?? []).ToHashSet();
        ValidateSelection(source, selected, groups);
        var lights = source.Lights.Select((light, index) =>
        {
            if (!selected.Contains(light.Id)) return null;
            // A single member exported without its group retains the group's visible switch and
            // brightness. A selected group retains its independent authored member values instead.
            var value = light.GroupId is { } id && !groups.Contains(id)
                ? source.EffectiveLights[index] with { GroupId = null } : light;
            if (value.Parent is { } parent)
            {
                if (resolveParent is null) throw new ArgumentException("Capturing a parented light requires its current parent transform.");
                value = SceneLightTransform.ToWorld(value, resolveParent(parent));
            }
            return value with { Receivers = null };
        }).Where(light => light is not null).Select(light => light!).ToArray();
        return new(name, new(lights, source.Groups.Where(group => groups.Contains(group.Id))));
    }

    /// <summary>Builds and validates the entire replacement before the caller publishes one normal
    /// project transaction. Every application gets fresh identities; unselected content is retained.</summary>
    public SceneLightingPresetApplication Apply(SceneLightingSnapshot source, SceneLightingPresetApplyMode mode,
        IEnumerable<Guid>? selectedLightIds = null, IEnumerable<Guid>? selectedGroupIds = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var removed = (selectedLightIds ?? []).ToHashSet(); var removedGroups = (selectedGroupIds ?? []).ToHashSet();
        if (mode == SceneLightingPresetApplyMode.ReplaceSelected)
        {
            ValidateSelection(source, removed, removedGroups);
            if (removed.Count == 0 && removedGroups.Count == 0)
                throw new ArgumentException("Select lights or a group before replacing a lighting selection.");
        }
        else { removed.Clear(); removedGroups.Clear(); }
        var groupIds = Lighting.Groups.ToDictionary(group => group.Id, _ => Guid.NewGuid());
        var newGroups = Lighting.Groups.Select(group => group with { Id = groupIds[group.Id] }).ToArray();
        var newLights = Lighting.Lights.Select(light => light with
        {
            Id = Guid.NewGuid(), GroupId = light.GroupId is { } id ? groupIds[id] : null,
        }).ToArray();
        var snapshot = new SceneLightingSnapshot(source.Lights.Where(light => !removed.Contains(light.Id)).Concat(newLights),
            source.Groups.Where(group => !removedGroups.Contains(group.Id)).Concat(newGroups), source.Settings);
        return new(snapshot, Array.AsReadOnly(newLights.Select(light => light.Id).ToArray()),
            Array.AsReadOnly(newGroups.Select(group => group.Id).ToArray()));
    }

    /// <summary>Apply a portable lamp's photometric and shape settings to an existing scene object.
    /// Placement, aim, identity, links and group membership belong to the destination object.</summary>
    public SceneLightingSnapshot ApplyToLight(SceneLightingSnapshot scene, Guid id)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (Lighting.Lights.Count != 1) throw new ArgumentException("Choose a single-light preset.");
        var value = Lighting.EffectiveLights[0];
        var target = scene.Lights.Single(light => light.Id == id);
        var updated = value with { Id = id, Name = target.Name, Position = target.Position,
            Rotation = target.Rotation, AimDistance = target.AimDistance, Parent = target.Parent, Receivers = target.Receivers, GroupId = target.GroupId };
        return scene.WithLights(scene.Lights.Select(light => light.Id == id ? updated : light));
    }

    private static void ValidateSelection(SceneLightingSnapshot source, HashSet<Guid> lights, HashSet<Guid> groups)
    {
        if (lights.Any(id => !source.Lights.Any(light => light.Id == id)) || groups.Any(id => !source.Groups.Any(group => group.Id == id)))
            throw new ArgumentException("The selected lights and groups must exist in the current scene.");
        foreach (var light in source.Lights)
            if (light.GroupId is { } group && groups.Contains(group)) lights.Add(light.Id);
    }

    public byte[] Serialize()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document
        {
            Format = "MDA_LIGHTING_PRESET", Version = Version, Name = Name,
            Lights = Lighting.Lights.Select(SceneLightingEditor.LightData.From).ToArray(),
            Groups = Lighting.Groups.Select(SceneLightingEditor.GroupData.From).ToArray(),
        }, JsonOptions);
        if (bytes.Length > MaxFileBytes) throw new ArgumentException("Lighting preset files must not exceed 1 MiB.");
        return bytes;
    }

    public static SceneLightingPreset Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxFileBytes) throw new ArgumentException("Lighting preset files must not exceed 1 MiB.", nameof(bytes));
        try
        {
            // Accept a conventional UTF-8 BOM without relaxing the actual document schema.
            if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
            var document = JsonSerializer.Deserialize<Document>(bytes, JsonOptions) ?? throw new JsonException("A preset must be a JSON object.");
            if (document.Format != "MDA_LIGHTING_PRESET" || document.Version is not (1 or Version) || document.Lights is null || document.Groups is null)
                throw new JsonException("Unsupported or incomplete MDA lighting preset.");
            if (document.Lights.Length > SceneLightingSnapshot.MaxLights || document.Groups.Length > SceneLightingSnapshot.MaxLights)
                throw new JsonException("Lighting preset capacity exceeded.");
            if (document.Lights.Any(light => light is null || light.HasParentField || light.HasReceiversField))
                throw new JsonException("Portable presets cannot contain project-specific parent or receiver fields.");
            return new(document.Name, new(document.Lights.Select(light => light.ToLight(legacyFalloff: document.Version < 2)),
                document.Groups.Select(group => group?.ToGroup() ?? throw new JsonException("A group entry cannot be null."))));
        }
        catch (JsonException error) { throw new ArgumentException("Invalid MDA lighting preset JSON.", nameof(bytes), error); }
    }

    /// <summary>Atomically writes a user-selected JSON file. No global library or preference is changed.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Lighting preset files must use the .json extension.", nameof(path));
        var bytes = Serialize(); string fullPath = Path.GetFullPath(path);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, fullPath, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static SceneLightingPreset Load(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > MaxFileBytes) throw new ArgumentException("Lighting preset files must not exceed 1 MiB.", nameof(path));
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192]; int count;
        while ((count = input.Read(buffer, 0, Math.Min(buffer.Length, MaxFileBytes + 1 - (int)output.Length))) > 0)
        { output.Write(buffer, 0, count); if (output.Length > MaxFileBytes) throw new ArgumentException("Lighting preset files must not exceed 1 MiB.", nameof(path)); }
        return Deserialize(output.ToArray());
    }

    public static SceneLightingPreset CreateBuiltin(SceneLightingBuiltinPreset kind)
    {
        var group = new SceneLightGroup(Guid.NewGuid(), kind switch
        { SceneLightingBuiltinPreset.PortraitSoft => "Portrait soft light", SceneLightingBuiltinPreset.IndoorWindow => "Indoor window light", _ => throw new ArgumentOutOfRangeException(nameof(kind)) });
        // Authored once in the original I / d^2 units, then matched at the subject distance.
        var subject = new Vector3(0, 10, 0);
        NativeSceneLight Spot(string name, Vector3 position, Vector3 color, float intensity, bool shadow = true) =>
            new(Guid.NewGuid(), name, SceneLightKind.Spot, position, Aim(position, subject), color,
                intensity, 60, .35f, 1.2f, CastShadows: shadow, GroupId: group.Id, AimDistance: Vector3.Distance(position, subject));
        NativeSceneLight[] lights = kind == SceneLightingBuiltinPreset.PortraitSoft
            ? [Spot("Key", new(-12, 20, 16), new(1, .88f, .75f), 250) with { Kind = SceneLightKind.Area, AreaSize = new(8, 12) },
               Spot("Fill", new(12, 14, 10), new(.75f, .85f, 1), 65, false),
               Spot("Rim", new(4, 20, -14), Vector3.One, 180)]
            : [Spot("Window", new(-18, 18, 8), new(.8f, .9f, 1), 350) with { Kind = SceneLightKind.Area, AreaSize = new(12, 10) },
               new(Guid.NewGuid(), "Room fill", SceneLightKind.Point, new(5, 14, 3), Quaternion.Identity,
                   new(1, .84f, .65f), 15, 40, CastShadows: false, GroupId: group.Id, AimDistance: Vector3.Distance(new(5, 14, 3), subject))];
        return new(group.Name, new(lights.Select(SceneLightFalloff.FromLegacy), [group]));
    }

    private static Quaternion Aim(Vector3 position, Vector3 target)
    {
        var rotation = Matrix4x4.CreateWorld(Vector3.Zero, Vector3.Normalize(target - position), Vector3.UnitY);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
    private sealed class Document
    {
        public required string Format { get; init; }
        public required int Version { get; init; }
        public required string Name { get; init; }
        public required SceneLightingEditor.LightData[] Lights { get; init; }
        public required SceneLightingEditor.GroupData[] Groups { get; init; }
    }
}
