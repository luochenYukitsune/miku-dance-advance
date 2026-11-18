using System.Collections.ObjectModel;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

public enum ModelRole { Unspecified, Character, Environment, Prop }
public sealed record ModelPurpose(ModelRole Role, bool? EnvironmentIndoor = null);

/// <summary>
/// Independently testable native-light editing session. Updates are immutable previews;
/// BeginEdit/CommitEdit coalesce one viewport gesture into one undo entry.
/// Model roles are explicit project-local choices keyed by the caller's stable asset identity.
/// </summary>
public sealed class SceneLightingEditor
{
    public const int SidecarVersion = 7;
    private sealed record State(SceneLightingSnapshot Lighting, Guid? SelectedId, ReadOnlyDictionary<string, ModelPurpose> Roles);
    private State state = new(SceneLightingSnapshot.Empty, null, new(new Dictionary<string, ModelPurpose>(StringComparer.Ordinal)));
    private State? editStart;
    private readonly Stack<State> undo = new();
    private readonly Stack<State> redo = new();

    public SceneLightingSnapshot Snapshot => state.Lighting;
    public IReadOnlyList<NativeSceneLight> Lights => state.Lighting.Lights;
    public IReadOnlyList<SceneLightGroup> Groups => state.Lighting.Groups;
    public SceneLightingSettings Settings => state.Lighting.Settings;
    public Guid? SelectedId => state.SelectedId;
    public IReadOnlyDictionary<string, ModelPurpose> ModelRoles => state.Roles;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool IsEditing => editStart is not null;

    public void Add(NativeSceneLight light)
    {
        ArgumentNullException.ThrowIfNull(light);
        var lighting = Snapshot.WithLights(Lights.Append(light));
        Apply(state with { Lighting = lighting, SelectedId = light.Id });
    }

    public void Update(NativeSceneLight light)
    {
        ArgumentNullException.ThrowIfNull(light);
        if (!Lights.Any(existing => existing.Id == light.Id))
            throw new ArgumentException("Cannot update a light outside the current session.", nameof(light));
        var lighting = Snapshot.WithLights(Lights.Select(existing => existing.Id == light.Id ? light : existing));
        Apply(state with { Lighting = lighting });
    }

    public bool Remove(Guid id)
    {
        if (!Lights.Any(light => light.Id == id)) return false;
        var lighting = Snapshot.WithLights(Lights.Where(light => light.Id != id));
        Apply(state with { Lighting = lighting, SelectedId = state.SelectedId == id ? null : state.SelectedId });
        return true;
    }

    public void AddGroup(SceneLightGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        Apply(state with { Lighting = Snapshot.WithGroups(Groups.Append(group)) });
    }

    public void UpdateGroup(SceneLightGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!Groups.Any(existing => existing.Id == group.Id))
            throw new ArgumentException("Cannot update a light group outside the current session.", nameof(group));
        Apply(state with { Lighting = Snapshot.WithGroups(Groups.Select(existing => existing.Id == group.Id ? group : existing)) });
    }

    /// <summary>Dissolves the group, retaining every member and its authored parameters.</summary>
    public bool RemoveGroup(Guid id)
    {
        if (!Groups.Any(group => group.Id == id)) return false;
        var lighting = new SceneLightingSnapshot(Lights.Select(light => light.GroupId == id ? light with { GroupId = null } : light),
            Groups.Where(group => group.Id != id), Settings);
        Apply(state with { Lighting = lighting });
        return true;
    }

    public void AssignToGroup(Guid lightId, Guid? groupId) => AssignToGroup([lightId], groupId);

    /// <summary>Assigns all requested members in one undoable update, or rejects the entire request.</summary>
    public void AssignToGroup(IEnumerable<Guid> lightIds, Guid? groupId)
    {
        ArgumentNullException.ThrowIfNull(lightIds);
        var ids = lightIds.ToHashSet();
        if (ids.Any(id => !Lights.Any(light => light.Id == id)))
            throw new ArgumentException("Every member must reference a light in the current session.", nameof(lightIds));
        if (groupId.HasValue && !Groups.Any(group => group.Id == groupId.Value))
            throw new ArgumentException("The target group must exist in the current session.", nameof(groupId));
        var lighting = Snapshot.WithLights(Lights.Select(light => ids.Contains(light.Id) ? light with { GroupId = groupId } : light));
        Apply(state with { Lighting = lighting });
    }

    /// <summary>Updates project-wide lighting as one normal content edit, or a preview within the current gesture.</summary>
    public void UpdateSettings(SceneLightingSettings settings) => Apply(state with { Lighting = Snapshot.WithSettings(settings) });

    /// <summary>Replaces authored lights and groups atomically, keeping the current light selected when it still exists.
    /// Model roles are retained; the replacement participates in normal edit gestures and history.</summary>
    public void ReplaceSnapshot(SceneLightingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ReplaceSnapshot(snapshot, snapshot.Lights.Any(light => light.Id == SelectedId) ? SelectedId : null);
    }

    public void ReplaceSnapshot(SceneLightingSnapshot snapshot, Guid? selectedId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (selectedId.HasValue && !snapshot.Lights.Any(light => light.Id == selectedId.Value))
            throw new ArgumentException("Selection must reference a light in the replacement snapshot.", nameof(selectedId));
        Apply(state with { Lighting = snapshot, SelectedId = selectedId });
    }

    /// <summary>Selection alone does not create a content edit or invalidate redo.</summary>
    public void Select(Guid? id)
    {
        if (id.HasValue && !Lights.Any(light => light.Id == id.Value))
            throw new ArgumentException("Selection must reference a light in the current session.", nameof(id));
        state = state with { SelectedId = id };
    }

    public ModelPurpose GetModelRole(string stableKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey);
        return state.Roles.TryGetValue(stableKey, out var purpose) ? purpose : new(ModelRole.Unspecified);
    }

    public void SetModelRole(string stableKey, ModelRole role, bool? environmentIndoor = null)
    {
        ValidatePurpose(stableKey, role, environmentIndoor);
        var roles = new Dictionary<string, ModelPurpose>(state.Roles, StringComparer.Ordinal)
        {
            [stableKey] = new(role, environmentIndoor),
        };
        Apply(state with { Roles = new(roles) });
    }

    public void BeginEdit()
    {
        if (IsEditing) throw new InvalidOperationException("An edit gesture is already active.");
        editStart = state;
    }

    public void CommitEdit()
    {
        var before = editStart ?? throw new InvalidOperationException("No edit gesture is active.");
        editStart = null;
        if (SameContent(before, state)) return;
        undo.Push(before);
        redo.Clear();
    }

    public void CancelEdit()
    {
        state = editStart ?? throw new InvalidOperationException("No edit gesture is active.");
        editStart = null;
    }

    public bool Undo()
    {
        RequireNoGesture();
        if (!undo.TryPop(out var before)) return false;
        redo.Push(state);
        state = before;
        return true;
    }

    public bool Redo()
    {
        RequireNoGesture();
        if (!redo.TryPop(out var after)) return false;
        undo.Push(state);
        state = after;
        return true;
    }

    private void Apply(State next)
    {
        if (SameContent(state, next))
        {
            // Selection is independent of content history, including a no-op snapshot replacement.
            state = state with { SelectedId = next.SelectedId };
            return;
        }
        if (!IsEditing)
        {
            undo.Push(state);
            redo.Clear();
        }
        state = next;
    }

    private void RequireNoGesture()
    {
        if (IsEditing) throw new InvalidOperationException("Commit or cancel the active edit gesture first.");
    }

    private static bool SameContent(State left, State right) =>
        left.Lighting.Lights.SequenceEqual(right.Lighting.Lights)
        && left.Lighting.Groups.SequenceEqual(right.Lighting.Groups)
        && left.Lighting.Settings == right.Lighting.Settings
        && left.Roles.Count == right.Roles.Count
        && left.Roles.All(pair => right.Roles.TryGetValue(pair.Key, out var purpose) && purpose == pair.Value);

    internal static void ValidatePurpose(string stableKey, ModelRole role, bool? environmentIndoor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey);
        if (!Enum.IsDefined(role) || (environmentIndoor.HasValue && role != ModelRole.Environment))
            throw new ArgumentException("Indoor/outdoor is an optional tag for Environment; a supported explicit role is required.", nameof(role));
    }

    public static string GetSidecarPath(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        return projectPath + ".lighting.json";
    }

    public string SerializeSidecar() => JsonSerializer.Serialize(new SidecarDocument
    {
        Version = SidecarVersion,
        Lights = Lights.Select(LightData.From).ToArray(),
        Groups = Groups.Select(GroupData.From).ToArray(),
        Settings = Settings == SceneLightingSettings.Default ? null : SettingsData.From(Settings),
        SelectedId = SelectedId,
        ModelRoles = ModelRoles.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new RoleData { Key = pair.Key, Role = pair.Value.Role, EnvironmentIndoor = pair.Value.EnvironmentIndoor }).ToArray(),
    }, JsonOptions);

    /// <summary>Parses and validates a complete candidate before replacing state. A successful load starts a fresh history.</summary>
    public bool TryLoadSidecar(string json, out string? error)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(json);
            var document = JsonSerializer.Deserialize<SidecarDocument>(json, JsonOptions)
                ?? throw new JsonException("The lighting sidecar must be a JSON object.");
            if (document.Version is not (1 or 2 or 3 or 4 or 5 or 6 or SidecarVersion))
                throw new JsonException($"Unsupported lighting sidecar version {document.Version}.");
            if (document.Version < 6 && document.HasSettingsField)
                throw new JsonException("Global lighting settings require lighting sidecar version 6.");
            if (document.HasSettingsField && document.Settings is null)
                throw new JsonException("Global lighting settings cannot be null.");
            if (document.Lights is null || document.ModelRoles is null)
                throw new JsonException("Lights and modelRoles arrays are required.");
            if (document.Version >= 2 && document.Groups is null)
                throw new JsonException("The groups array is required in lighting sidecar version 2 through 7.");
            if (document.Version < 5 && document.Lights.Any(data => data?.HasAreaSizeField == true || data?.Kind == SceneLightKind.Area))
                throw new JsonException("Area lights and dimensions require lighting sidecar version 5.");
            if (document.Version < 3 && document.Lights.Any(data => data?.HasParentField == true))
                throw new JsonException("Light parents require lighting sidecar version 3.");
            if (document.Version < 4 && document.Lights.Any(data => data?.HasReceiversField == true))
                throw new JsonException("Light receiver selections require lighting sidecar version 4.");
            if (document.Version == 1 && (document.Groups is not null || document.Lights.Any(data => data?.GroupId is not null)))
                throw new JsonException("Light groups and membership require lighting sidecar version 2.");
            // Version 7 introduced aim-normalized falloff; earlier lights are converted once here.
            var lighting = new SceneLightingSnapshot(document.Lights.Select(data =>
                data?.ToLight(legacyFalloff: document.Version < 7) ?? throw new JsonException("A light entry cannot be null.")),
                document.Groups?.Select(data => data?.ToGroup() ?? throw new JsonException("A light group entry cannot be null.")),
                document.Settings?.ToSettings());
            if (document.SelectedId.HasValue && !lighting.Lights.Any(light => light.Id == document.SelectedId.Value))
                throw new JsonException("The selected light does not exist in this sidecar.");
            var roles = new Dictionary<string, ModelPurpose>(StringComparer.Ordinal);
            foreach (var role in document.ModelRoles)
            {
                if (role is null) throw new JsonException("A model role entry cannot be null.");
                ValidatePurpose(role.Key, role.Role, role.EnvironmentIndoor);
                if (!roles.TryAdd(role.Key, new(role.Role, role.EnvironmentIndoor)))
                    throw new JsonException("Duplicate stable model key.");
            }
            var candidate = new State(lighting, document.SelectedId, new(roles));
            state = candidate;
            editStart = null;
            undo.Clear();
            redo.Clear();
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>Writes a JSON sidecar atomically in its own directory, leaving source PMM/PMX assets untouched.</summary>
    public void SaveSidecar(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Lighting sidecars must use the .json extension.", nameof(path));
        string fullPath = Path.GetFullPath(path);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string json = SerializeSidecar();
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public bool TryLoadSidecarFile(string path, out string? error)
    {
        try { return TryLoadSidecar(File.ReadAllText(path, Encoding.UTF8), out error); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private sealed class SidecarDocument
    {
        private SettingsData? settings;
        public required int Version { get; init; }
        public required LightData[] Lights { get; init; }
        public GroupData[]? Groups { get; init; }
        public required Guid? SelectedId { get; init; }
        public required RoleData[] ModelRoles { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SettingsData? Settings
        {
            get => settings;
            init { settings = value; HasSettingsField = true; }
        }
        [JsonIgnore]
        public bool HasSettingsField { get; private set; }
    }

    internal sealed class SettingsData
    {
        public required float EnvironmentStrength { get; init; }
        public required float ExposureEv { get; init; }
        public SceneLightingSettings ToSettings() => new(EnvironmentStrength, ExposureEv);
        public static SettingsData From(SceneLightingSettings settings) => new()
        { EnvironmentStrength = settings.EnvironmentStrength, ExposureEv = settings.ExposureEv };
    }

    private sealed class RoleData
    {
        public required string Key { get; init; }
        public required ModelRole Role { get; init; }
        public required bool? EnvironmentIndoor { get; init; }
    }

    internal sealed class GroupData
    {
        public required Guid Id { get; init; }
        public required string Name { get; init; }
        public required bool Enabled { get; init; }
        public required float Intensity { get; init; }
        public SceneLightGroup ToGroup() => new(Id, Name, Enabled, Intensity);
        public static GroupData From(SceneLightGroup group) => new()
        { Id = group.Id, Name = group.Name, Enabled = group.Enabled, Intensity = group.Intensity };
    }

    internal sealed class LightData
    {
        private ParentData? parent;
        private ReceiversData? receivers;
        private float[]? areaSize;
        private float? aimDistance;
        public required Guid Id { get; init; }
        public required string Name { get; init; }
        public required SceneLightKind Kind { get; init; }
        public required float[] Position { get; init; }
        public required float[] Rotation { get; init; }
        public required float[] Color { get; init; }
        public required float Intensity { get; init; }
        public required float Range { get; init; }
        public required float InnerConeRadians { get; init; }
        public required float OuterConeRadians { get; init; }
        public required bool Enabled { get; init; }
        public required bool CastShadows { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? GroupId { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ParentData? Parent
        {
            get => parent;
            init { parent = value; HasParentField = true; }
        }
        [JsonIgnore]
        public bool HasParentField { get; private set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ReceiversData? Receivers
        {
            get => receivers;
            init { receivers = value; HasReceiversField = true; }
        }
        [JsonIgnore]
        public bool HasReceiversField { get; private set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? AreaSize
        {
            get => areaSize;
            init { areaSize = value; HasAreaSizeField = true; }
        }
        [JsonIgnore]
        public bool HasAreaSizeField { get; private set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? AimDistance
        {
            get => aimDistance;
            init { aimDistance = value; HasAimDistanceField = true; }
        }
        [JsonIgnore]
        public bool HasAimDistanceField { get; private set; }

        /// <summary>Documents written before aim-normalized falloff stored I / d^2 intensities. They are
        /// converted with <see cref="SceneLightFalloff.FromLegacy"/>; such documents cannot carry an aim.</summary>
        public NativeSceneLight ToLight(bool legacyFalloff = false)
        {
            if (legacyFalloff && HasAimDistanceField)
                throw new JsonException("Light aim distances require the aim-normalized lighting format.");
            if (HasAimDistanceField && AimDistance is null)
                throw new JsonException("A light aim distance cannot be null.");
            if (Position is not { Length: 3 } || Rotation is not { Length: 4 } || Color is not { Length: 3 })
                throw new JsonException("Position/color require three values and rotation requires four XYZW values.");
            if (AreaSize is not null && AreaSize.Length != 2)
                throw new JsonException("Area dimensions require two width/height values.");
            var light = new NativeSceneLight(Id, Name, Kind, new(Position[0], Position[1], Position[2]),
                new(Rotation[0], Rotation[1], Rotation[2], Rotation[3]), new(Color[0], Color[1], Color[2]),
                Intensity, Range, InnerConeRadians, OuterConeRadians, Enabled, CastShadows, GroupId, Parent?.ToParent(), Receivers?.ToReceivers(),
                AreaSize is { } size ? new(size[0], size[1]) : null, AimDistance);
            return legacyFalloff ? SceneLightFalloff.FromLegacy(light) : light;
        }

        public static LightData From(NativeSceneLight light) => new()
        {
            Id = light.Id, Name = light.Name, Kind = light.Kind,
            Position = [light.Position.X, light.Position.Y, light.Position.Z],
            Rotation = [light.Rotation.X, light.Rotation.Y, light.Rotation.Z, light.Rotation.W],
            Color = [light.Color.X, light.Color.Y, light.Color.Z],
            Intensity = light.Intensity, Range = light.Range, InnerConeRadians = light.InnerConeRadians,
            OuterConeRadians = light.OuterConeRadians, Enabled = light.Enabled, CastShadows = light.CastShadows,
            GroupId = light.GroupId, Parent = light.Parent is { } parent ? ParentData.From(parent) : null,
            // This flag records input field presence, including JSON null. A fresh authoring
            // DTO omits a null parent and must remain valid in older unparented layouts.
            HasParentField = light.Parent is not null,
            Receivers = light.Receivers is { } receivers ? ReceiversData.From(receivers) : null,
            HasReceiversField = light.Receivers is not null,
            AreaSize = light.AreaSize is { } size ? [size.X, size.Y] : null,
            HasAreaSizeField = light.AreaSize is not null,
            AimDistance = light.AimDistance,
            HasAimDistanceField = light.AimDistance is not null,
        };
    }

    internal sealed class ParentData
    {
        public required Guid ObjectId { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? BoneIndex { get; init; }
        public SceneLightParent ToParent() => new(ObjectId, BoneIndex);
        public static ParentData From(SceneLightParent parent) => new() { ObjectId = parent.ObjectId, BoneIndex = parent.BoneIndex };
    }

    internal sealed class ReceiversData
    {
        public required SceneLightReceiverMode Mode { get; init; }
        public required Guid[] ObjectIds { get; init; }
        public SceneLightReceivers ToReceivers() => new(Mode, ObjectIds);
        public static ReceiversData From(SceneLightReceivers receivers) => new() { Mode = receivers.Mode, ObjectIds = receivers.ObjectIds.ToArray() };
    }
}
