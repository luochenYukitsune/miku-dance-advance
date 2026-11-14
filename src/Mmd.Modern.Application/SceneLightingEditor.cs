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
    public const int SidecarVersion = 1;
    private sealed record State(SceneLightingSnapshot Lighting, Guid? SelectedId, ReadOnlyDictionary<string, ModelPurpose> Roles);
    private State state = new(SceneLightingSnapshot.Empty, null, new(new Dictionary<string, ModelPurpose>(StringComparer.Ordinal)));
    private State? editStart;
    private readonly Stack<State> undo = new();
    private readonly Stack<State> redo = new();

    public SceneLightingSnapshot Snapshot => state.Lighting;
    public IReadOnlyList<NativeSceneLight> Lights => state.Lighting.Lights;
    public Guid? SelectedId => state.SelectedId;
    public IReadOnlyDictionary<string, ModelPurpose> ModelRoles => state.Roles;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool IsEditing => editStart is not null;

    public void Add(NativeSceneLight light)
    {
        ArgumentNullException.ThrowIfNull(light);
        var lighting = new SceneLightingSnapshot(Lights.Append(light));
        Apply(state with { Lighting = lighting, SelectedId = light.Id });
    }

    public void Update(NativeSceneLight light)
    {
        ArgumentNullException.ThrowIfNull(light);
        if (!Lights.Any(existing => existing.Id == light.Id))
            throw new ArgumentException("Cannot update a light outside the current session.", nameof(light));
        var lighting = new SceneLightingSnapshot(Lights.Select(existing => existing.Id == light.Id ? light : existing));
        Apply(state with { Lighting = lighting });
    }

    public bool Remove(Guid id)
    {
        if (!Lights.Any(light => light.Id == id)) return false;
        var lighting = new SceneLightingSnapshot(Lights.Where(light => light.Id != id));
        Apply(state with { Lighting = lighting, SelectedId = state.SelectedId == id ? null : state.SelectedId });
        return true;
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
        if (SameContent(state, next)) return;
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
            if (document.Version != SidecarVersion)
                throw new JsonException($"Unsupported lighting sidecar version {document.Version}.");
            if (document.Lights is null || document.ModelRoles is null)
                throw new JsonException("Lights and modelRoles arrays are required.");
            var lighting = new SceneLightingSnapshot(document.Lights.Select(data =>
                data?.ToLight() ?? throw new JsonException("A light entry cannot be null.")));
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
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private sealed class SidecarDocument
    {
        public required int Version { get; init; }
        public required LightData[] Lights { get; init; }
        public required Guid? SelectedId { get; init; }
        public required RoleData[] ModelRoles { get; init; }
    }

    private sealed class RoleData
    {
        public required string Key { get; init; }
        public required ModelRole Role { get; init; }
        public required bool? EnvironmentIndoor { get; init; }
    }

    internal sealed class LightData
    {
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

        public NativeSceneLight ToLight()
        {
            if (Position is not { Length: 3 } || Rotation is not { Length: 4 } || Color is not { Length: 3 })
                throw new JsonException("Position/color require three values and rotation requires four XYZW values.");
            return new(Id, Name, Kind, new(Position[0], Position[1], Position[2]),
                new(Rotation[0], Rotation[1], Rotation[2], Rotation[3]), new(Color[0], Color[1], Color[2]),
                Intensity, Range, InnerConeRadians, OuterConeRadians, Enabled, CastShadows);
        }

        public static LightData From(NativeSceneLight light) => new()
        {
            Id = light.Id, Name = light.Name, Kind = light.Kind,
            Position = [light.Position.X, light.Position.Y, light.Position.Z],
            Rotation = [light.Rotation.X, light.Rotation.Y, light.Rotation.Z, light.Rotation.W],
            Color = [light.Color.X, light.Color.Y, light.Color.Z],
            Intensity = light.Intensity, Range = light.Range, InnerConeRadians = light.InnerConeRadians,
            OuterConeRadians = light.OuterConeRadians, Enabled = light.Enabled, CastShadows = light.CastShadows,
        };
    }
}
