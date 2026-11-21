using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>A detached, validated native-light edit. The caller publishes it inside one workspace Edit.</summary>
public sealed record EditorLightingDraft
{
    public EditorLightingDraft(SceneLightingSnapshot snapshot, Guid? selectedLightId = null,
        IEnumerable<Guid>? selectedLightIds = null, IEnumerable<Guid>? selectedGroupIds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (selectedLightId is { } id && !snapshot.Lights.Any(light => light.Id == id))
            throw new ArgumentException(Text("选中的光源已不存在。"), nameof(selectedLightId));
        var requested = (selectedLightIds ?? (selectedLightId is { } selected ? [selected] : Array.Empty<Guid>())).ToHashSet();
        if (selectedLightId is { } primary) requested.Add(primary);
        if (requested.Any(id => !snapshot.Lights.Any(light => light.Id == id)))
            throw new ArgumentException(Text("选中的光源已不存在。"), nameof(selectedLightIds));
        var groups = (selectedGroupIds ?? []).ToHashSet();
        if (groups.Any(id => !snapshot.Groups.Any(group => group.Id == id)))
            throw new ArgumentException(Text("Mda.Text.Context.Lighting.MissingGroup"), nameof(selectedGroupIds));
        Snapshot = snapshot; SelectedLightId = selectedLightId;
        SelectedLightIds = Array.AsReadOnly(snapshot.Lights.Where(light => requested.Contains(light.Id)).Select(light => light.Id).ToArray());
        SelectedGroupIds = Array.AsReadOnly(snapshot.Groups.Where(group => groups.Contains(group.Id)).Select(group => group.Id).ToArray());
    }
    public SceneLightingSnapshot Snapshot { get; }
    public Guid? SelectedLightId { get; }
    public IReadOnlyList<Guid> SelectedLightIds { get; }
    public IReadOnlyList<Guid> SelectedGroupIds { get; }

    /// <summary>Reconciles only lights, preserving the existing explicit model-purpose metadata.</summary>
    public void ApplyTo(SceneLightingEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        editor.ReplaceSnapshot(Snapshot, SelectedLightId);
    }
}

/// <summary>Protected draft: each actual light retains its unfinished numeric values when switching rows.</summary>
public sealed partial class EditorLightingViewModel : INotifyPropertyChanged
{
    private EditorNativeLightItem? selectedLight;
    private string validationMessage = "";
    private string searchText = "";
    private Exception? validationError;
    private PmmCameraPose creationCamera = EditorLightPlacement.DefaultCamera;
    public EditorLightingViewModel(SceneLightingSnapshot snapshot, Guid? selectedLightId = null,
        IEnumerable<Guid>? selectedLightIds = null, IEnumerable<Guid>? selectedGroupIds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        InitializeGlobalSettings(snapshot.Settings);
        Lights.CollectionChanged += (_, args) =>
        {
            if (args.OldItems is not null) foreach (EditorNativeLightItem item in args.OldItems) item.PropertyChanged -= LightLabelChanged;
            if (args.NewItems is not null) foreach (EditorNativeLightItem item in args.NewItems) item.PropertyChanged += LightLabelChanged;
            RefreshFilter();
            RefreshCapacity();
            SyncInspectorLights();
        };
        InitializeGroups(snapshot);
        foreach (var light in snapshot.Lights) Lights.Add(new(light));
        SelectedLight = Lights.FirstOrDefault(light => light.Id == selectedLightId) ?? Lights.FirstOrDefault();
        if (selectedLightIds is not null || selectedGroupIds is not null)
            RestoreSelection(selectedLightIds ?? [], selectedLightId, selectedGroupIds ?? []);
        Observe(this, static model => model.Changed(null));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<EditorNativeLightItem> Lights { get; } = [];
    /// <summary>Only the rows whose full property panel is shown. The inspector realizes its heavy
    /// per-light template from this list, so hidden lights no longer create controls on every commit.</summary>
    public ObservableCollection<EditorNativeLightItem> InspectorLights { get; } = [];
    internal void SyncInspectorLights()
    {
        var wanted = Lights.Where(light => light.IsInspectorVisible).ToArray();
        if (InspectorLights.SequenceEqual(wanted)) return;
        InspectorLights.Clear();
        foreach (var light in wanted) InspectorLights.Add(light);
    }
    public string SearchText
    {
        get => searchText;
        set
        {
            if (searchText == value) return;
            searchText = value ?? ""; Changed(); RefreshFilter();
        }
    }
    public IReadOnlyList<EditorNativeLightItem> FilteredLights => Lights.Where(light =>
        string.IsNullOrWhiteSpace(searchText) || light.Label.Contains(searchText.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
    public bool HasFilteredLights => FilteredLights.Count != 0;
    internal bool IsRefreshingBrowser { get; private set; }
    private void RefreshFilter()
    {
        IsRefreshingBrowser = true;
        try { Changed(nameof(FilteredLights)); Changed(nameof(HasFilteredLights)); Changed(nameof(BrowserRows)); Changed(nameof(HasBrowserRows)); }
        finally { IsRefreshingBrowser = false; }
    }
    private void LightLabelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or nameof(EditorNativeLightItem.Label) or nameof(EditorNativeLightItem.GroupId)) RefreshFilter();
        if (args.PropertyName is null or nameof(EditorNativeLightItem.Enabled) or nameof(EditorNativeLightItem.GroupId)) RefreshCapacity();
    }
    public EditorNativeLightItem? SelectedLight
    {
        get => selectedLight;
        set
        {
            if (changingSelection) { selectedLight = value; return; }
            SelectMany(value is { } light ? [light.Id] : [], value?.Id, preserveGroups: false);
        }
    }
    public bool HasSelectedLight => SelectedLight is not null;
    public bool CanAddLight => Lights.Count < SceneLightingSnapshot.MaxLights;
    public string LightCountSummary => IsModernWorkspace
        ? Format("Mda.Text.Context.Lighting.LightCounts", Lights.Count, SceneLightingSnapshot.MaxLights, EnabledLightCount, SceneLightingSnapshot.MaxActiveLights)
        : Format("附加光源 {0} / {1}", Lights.Count, SceneLightingSnapshot.MaxLights);
    public string ValidationMessage
    { get => validationError?.Message ?? Text(validationMessage); private set { validationError = null; validationMessage = value; Changed(); Changed(nameof(HasValidationMessage)); } }
    public bool HasValidationMessage => ValidationMessage.Length > 0;
    public void SetValidationMessage(string message) => ValidationMessage = message;

    internal PmmCameraPose CreationCamera => creationCamera;

    /// <summary>Only subsequent additions use this detached view; existing lights keep their authored transforms.</summary>
    public void SetCreationCamera(PmmCameraPose camera)
    {
        _ = EditorLightPlacement.ValidateCamera(camera);
        creationCamera = camera;
    }

    public bool AddLight(SceneLightKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!CanAddLight) { SetValidationError(Error("最多支持 {0} 个附加光源，请先移除一个。", SceneLightingSnapshot.MaxLights)); return false; }
        string label = EditorNativeLightItem.KindName(kind);
        int ordinal = 1;
        while (Lights.Any(light => light.Name == $"{label} {ordinal}")) ordinal++;
        var created = EditorLightPlacement.Create(Guid.NewGuid(), $"{label} {ordinal}", kind, creationCamera);
        var item = new EditorNativeLightItem(created with { Enabled = EnabledLightCount < SceneLightingSnapshot.MaxActiveLights });
        Lights.Add(item); SelectedLight = item; CollectionChanged(); ValidationMessage = "";
        return true;
    }

    public void RemoveSelectedLight()
    {
        if (SelectedLight is not { } current) return;
        int index = Lights.IndexOf(current); Lights.Remove(current);
        SelectedLight = Lights.Count == 0 ? null : Lights[Math.Min(index, Lights.Count - 1)];
        CollectionChanged(); ValidationMessage = "";
    }

    public bool TryCreate(out EditorLightingDraft? draft)
    {
        draft = null;
        try
        {
            var snapshot = CreateSnapshot(Lights.Select(light => light.Build()), Groups.Select(group => group.Build()));
            draft = new(snapshot, SelectedLight?.Id, SelectedLightIds, SelectedGroupIds); ValidationMessage = ""; return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { SetValidationError(error); return false; }
    }
    private void SetValidationError(Exception error)
    { validationError = error; Changed(nameof(ValidationMessage)); Changed(nameof(HasValidationMessage)); }
    private void CollectionChanged() { Changed(nameof(CanAddLight)); Changed(nameof(LightCountSummary)); }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class EditorNativeLightItem : INotifyPropertyChanged
{
    private readonly NativeSceneLight original;
    private readonly Vector3 originalDegrees;
    private string name;
    private int kindIndex;
    private bool enabled, castShadows, isSelected, useModernControls;
    private bool isInspectorVisible;
    private Guid? groupId;
    private bool refreshingLocalization;
    private decimal? positionX, positionY, positionZ, rotationX, rotationY, rotationZ, targetX, targetY, targetZ;
    private decimal? red, green, blue, intensity, range, innerConeDegrees, outerConeDegrees;
    private decimal? areaWidth, areaHeight;
    private bool areaSizeSpecified, aimDistanceModified, targetEdited, backlit;
    private float? aimDistance;
    private Quaternion? directRotation;
    public EditorNativeLightItem(NativeSceneLight light) : this(light, ToDegrees(light?.Rotation ?? Quaternion.Identity)) { }

    private EditorNativeLightItem(NativeSceneLight light, Vector3 sampledDegrees)
    {
        original = light ?? throw new ArgumentNullException(nameof(light));
        name = light.Name; kindIndex = (int)light.Kind; enabled = light.Enabled; castShadows = light.CastShadows; groupId = light.GroupId;
        positionX = Decimal(light.Position.X); positionY = Decimal(light.Position.Y); positionZ = Decimal(light.Position.Z);
        originalDegrees = sampledDegrees;
        rotationX = Decimal(originalDegrees.X); rotationY = Decimal(originalDegrees.Y); rotationZ = Decimal(originalDegrees.Z);
        aimDistance = light.AimDistance;
        var initialTarget = light.Target;
        targetX = Decimal(initialTarget.X); targetY = Decimal(initialTarget.Y); targetZ = Decimal(initialTarget.Z);
        var initialDir = light.Direction;
        if (Finite(initialDir) && initialDir.LengthSquared() >= 1e-12f)
        {
            initialDir = Vector3.Normalize(initialDir);
            if (MathF.Abs(initialDir.Z) > 1e-5f) backlit = initialDir.Z < 0;
        }
        red = Decimal(light.Color.X); green = Decimal(light.Color.Y); blue = Decimal(light.Color.Z);
        intensity = Decimal(light.Intensity); range = Decimal(light.Range);
        areaWidth = Decimal(light.AreaSize?.X ?? 5); areaHeight = Decimal(light.AreaSize?.Y ?? 5);
        areaSizeSpecified = light.AreaSize is not null;
        innerConeDegrees = Decimal(light.InnerConeRadians * 180 / MathF.PI); outerConeDegrees = Decimal(light.OuterConeRadians * 180 / MathF.PI);
        Observe(this, static model => model.RefreshLocalization());
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public Guid Id => original.Id;
    // Stable item identity keeps language changes from clearing ComboBox selection or
    // publishing a transient -1 through the detached dialog's two-way selection binding.
    public EditorLightKindChoice[] KindChoices { get; } =
        [new(SceneLightKind.Directional), new(SceneLightKind.Point), new(SceneLightKind.Spot), new(SceneLightKind.Area)];
    public bool IsSelected { get => isSelected; internal set => Set(ref isSelected, value); }
    public bool IsInspectorVisible { get => isInspectorVisible; internal set => Set(ref isInspectorVisible, value); }
    public Guid? GroupId { get => groupId; internal set { if (Set(ref groupId, value)) Changed(nameof(IsGrouped)); } }
    public bool IsGrouped => GroupId is not null;
    public string Label => Format("{0} · {1}", Name, KindName((SceneLightKind)KindIndex));
    public string TypeLabel => KindName((SceneLightKind)KindIndex);
    public string Name { get => name; set { if (Set(ref name, value)) Changed(nameof(Label)); } }
    public int KindIndex
    {
        get => kindIndex;
        set
        {
            if (!Set(ref kindIndex, value)) return;
            if (value == (int)SceneLightKind.Area && !areaSizeSpecified)
            {
                areaSizeSpecified = true;
                AreaWidth = 5; AreaHeight = 5;
            }
            Changed(nameof(Label)); Changed(nameof(TypeLabel)); Changed(nameof(HasDirection));
            Changed(nameof(IsDirectional)); Changed(nameof(IsPositional));
            Changed(nameof(HasRange)); Changed(nameof(HasCone)); Changed(nameof(HasAreaSize));
            Changed(nameof(LightDirectionVector)); Changed(nameof(IsBacklit)); Changed(nameof(HemisphereLabel));
            SyncDisplayedTarget();
        }
    }
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public bool CastShadows { get => castShadows; set => Set(ref castShadows, value); }
    public bool UseModernControls { get => useModernControls; internal set { if (Set(ref useModernControls, value)) { Changed(nameof(UseLegacyControls)); Changed(nameof(BrightnessLabel)); Changed(nameof(ColorLabel)); } } }
    public bool UseLegacyControls => !UseModernControls;
    public string BrightnessLabel => UseModernControls ? Text("Mda.Text.Context.Lighting.Brightness") : Text("强度");
    public string ColorLabel => Text(UseModernControls ? "Mda.Text.Context.Lighting.Color" : "Mda.Text.44ba8282f5e5");
    public bool HasDirection => KindIndex != (int)SceneLightKind.Point;
    public bool IsDirectional => KindIndex == (int)SceneLightKind.Directional;
    public bool IsPositional => KindIndex != (int)SceneLightKind.Directional;
    public bool HasRange => KindIndex != (int)SceneLightKind.Directional;
    public bool HasCone => KindIndex == (int)SceneLightKind.Spot;
    public bool HasAreaSize => KindIndex == (int)SceneLightKind.Area;
    public decimal? PositionX { get => positionX; set { if (Set(ref positionX, value)) SyncDisplayedTarget(); } }
    public decimal? PositionY { get => positionY; set { if (Set(ref positionY, value)) SyncDisplayedTarget(); } }
    public decimal? PositionZ { get => positionZ; set { if (Set(ref positionZ, value)) SyncDisplayedTarget(); } }
    public decimal? RotationX { get => rotationX; set { if (Set(ref rotationX, value)) { directRotation = null; NotifyAimFromOrientation(); } } }
    public decimal? RotationY { get => rotationY; set { if (Set(ref rotationY, value)) { directRotation = null; NotifyAimFromOrientation(); } } }
    public decimal? RotationZ { get => rotationZ; set { if (Set(ref rotationZ, value)) { directRotation = null; NotifyAimFromOrientation(); } } }
    public decimal? TargetX { get => targetX; set { if (Set(ref targetX, value)) { targetEdited = true; TryApplyTargetFromFields(); } } }
    public decimal? TargetY { get => targetY; set { if (Set(ref targetY, value)) { targetEdited = true; TryApplyTargetFromFields(); } } }
    public decimal? TargetZ { get => targetZ; set { if (Set(ref targetZ, value)) { targetEdited = true; TryApplyTargetFromFields(); } } }
    public Vector3 LightDirectionVector { get => CurrentDirection(); set => ApplyAimDirection(value); }
    public bool IsBacklit
    {
        get
        {
            var dir = CurrentDirection();
            if (Finite(dir) && dir.LengthSquared() >= 1e-12f)
            {
                var normalized = Vector3.Normalize(dir);
                if (MathF.Abs(normalized.Z) > 1e-5f) return normalized.Z < 0;
            }
            return backlit;
        }
    }
    public string HemisphereLabel => Text(IsBacklit ? "Mda.Text.LightAim.FlipToFront" : "Mda.Text.LightAim.FlipToBack");
    public decimal? Red { get => red; set => Set(ref red, value); }
    public decimal? Green { get => green; set => Set(ref green, value); }
    public decimal? Blue { get => blue; set => Set(ref blue, value); }
    public decimal? Intensity { get => intensity; set { if (Set(ref intensity, value)) Changed(nameof(BrightnessSliderPosition)); } }
    public decimal? Range { get => range; set { if (Set(ref range, value)) { Changed(nameof(DistanceSliderPosition)); if (!aimDistanceModified) SyncDisplayedTarget(); } } }
    public double BrightnessSliderPosition => EditorLightParameterScale.BrightnessPosition(Intensity);
    public double DistanceSliderPosition => EditorLightParameterScale.DistancePosition(Range);
    public decimal? AreaWidth { get => areaWidth; set { if (Set(ref areaWidth, value)) { areaSizeSpecified = true; Changed(nameof(AreaWidthSliderPosition)); } } }
    public decimal? AreaHeight { get => areaHeight; set { if (Set(ref areaHeight, value)) { areaSizeSpecified = true; Changed(nameof(AreaHeightSliderPosition)); } } }
    public double AreaWidthSliderPosition => EditorLightParameterScale.DistancePosition(AreaWidth);
    public double AreaHeightSliderPosition => EditorLightParameterScale.DistancePosition(AreaHeight);
    public decimal? InnerConeDegrees { get => innerConeDegrees; set { if (Set(ref innerConeDegrees, value)) Changed(nameof(EdgeSoftnessPercent)); } }
    public decimal? OuterConeDegrees { get => outerConeDegrees; set { if (Set(ref outerConeDegrees, value)) { Changed(nameof(BeamWidthDegrees)); Changed(nameof(EdgeSoftnessPercent)); } } }
    /// <summary>The visible beam is the full outer angle. Untouched half-angle floats keep their
    /// exact historical samples through Build; merely displaying a slider never invokes a setter.</summary>
    public decimal? BeamWidthDegrees
    {
        get
        {
            try { return OuterConeDegrees * 2; }
            catch (OverflowException) { return null; }
        }
        set
        {
            if (value == BeamWidthDegrees) return;
            decimal? softness = EdgeSoftnessPercent;
            OuterConeDegrees = value / 2;
            try { InnerConeDegrees = OuterConeDegrees * (1 - softness / 100); }
            catch (OverflowException) { InnerConeDegrees = null; }
        }
    }
    public decimal? EdgeSoftnessPercent
    {
        get
        {
            try { return OuterConeDegrees is > 0 ? (OuterConeDegrees - InnerConeDegrees) / OuterConeDegrees * 100 : null; }
            catch (OverflowException) { return null; }
        }
        set
        {
            if (value == EdgeSoftnessPercent) return;
            // The legacy contract requires distinct half angles. A modern 0% gesture authors
            // an imperceptibly thin transition, with enough relative separation to survive float
            // conversion; it never rewrites an untouched historical cone.
            decimal? effective = value == 0 ? .0001m : value;
            try { InnerConeDegrees = OuterConeDegrees * (1 - effective / 100); }
            catch (OverflowException) { InnerConeDegrees = null; }
        }
    }

    internal void RestoreInput(EditorRecoveryNativeLightDraft draft)
    {
        Name = draft.Name; KindIndex = draft.KindIndex; Enabled = draft.Enabled; CastShadows = draft.CastShadows;
        PositionX = draft.PositionX; PositionY = draft.PositionY; PositionZ = draft.PositionZ;
        RotationX = draft.RotationX; RotationY = draft.RotationY; RotationZ = draft.RotationZ;
        directRotation = draft.DirectRotation;
        aimDistance = draft.AimDistance ?? draft.Original.AimDistance;
        aimDistanceModified = draft.AimDistanceModified;
        targetEdited = draft.TargetEdited;
        Red = draft.Red; Green = draft.Green; Blue = draft.Blue; Intensity = draft.Intensity; Range = draft.Range;
        InnerConeDegrees = draft.InnerConeDegrees; OuterConeDegrees = draft.OuterConeDegrees;
        AreaWidth = draft.AreaWidth; AreaHeight = draft.AreaHeight;
        areaSizeSpecified = draft.AreaSizeSpecified || draft.Original.AreaSize is not null || draft.KindIndex == (int)SceneLightKind.Area;
        if (draft.TargetX.HasValue || draft.TargetY.HasValue || draft.TargetZ.HasValue || draft.TargetEdited)
        {
            targetX = draft.TargetX; targetY = draft.TargetY; targetZ = draft.TargetZ;
            Changed(nameof(TargetX)); Changed(nameof(TargetY)); Changed(nameof(TargetZ));
        }
        else
            SyncDisplayedTarget();
        NotifyDirectionState();
    }

    /// <summary>Retain only unfinished fields across an outside edit. Committed fields follow Undo;
    /// editor text keeps its own intent without reusing a previous document's source binding.</summary>
    internal void RestorePendingInput(EditorRecoveryNativeLightDraft pending, NativeSceneLight committed)
    {
        var before = new EditorNativeLightItem(committed).CaptureRecoveryDraft();
        if (pending.Name != before.Name) Name = pending.Name;
        if (pending.KindIndex != before.KindIndex) KindIndex = pending.KindIndex;
        if (pending.Enabled != before.Enabled) Enabled = pending.Enabled;
        if (pending.CastShadows != before.CastShadows) CastShadows = pending.CastShadows;
        if (pending.PositionX != before.PositionX) PositionX = pending.PositionX;
        if (pending.PositionY != before.PositionY) PositionY = pending.PositionY;
        if (pending.PositionZ != before.PositionZ) PositionZ = pending.PositionZ;
        if (pending.RotationX != before.RotationX) RotationX = pending.RotationX;
        if (pending.RotationY != before.RotationY) RotationY = pending.RotationY;
        if (pending.RotationZ != before.RotationZ) RotationZ = pending.RotationZ;
        if (pending.DirectRotation != before.DirectRotation) directRotation = pending.DirectRotation;
        if (pending.AimDistanceModified != before.AimDistanceModified || pending.AimDistance != before.AimDistance)
        {
            aimDistance = pending.AimDistance;
            aimDistanceModified = pending.AimDistanceModified;
        }
        if (pending.TargetEdited || pending.DirectRotation is not null || pending.AimDistanceModified)
        {
            targetEdited = pending.TargetEdited;
            targetX = pending.TargetX; targetY = pending.TargetY; targetZ = pending.TargetZ;
            Changed(nameof(TargetX)); Changed(nameof(TargetY)); Changed(nameof(TargetZ));
        }
        if (pending.Red != before.Red) Red = pending.Red;
        if (pending.Green != before.Green) Green = pending.Green;
        if (pending.Blue != before.Blue) Blue = pending.Blue;
        if (pending.Intensity != before.Intensity) Intensity = pending.Intensity;
        if (pending.Range != before.Range) Range = pending.Range;
        if (pending.InnerConeDegrees != before.InnerConeDegrees) InnerConeDegrees = pending.InnerConeDegrees;
        if (pending.OuterConeDegrees != before.OuterConeDegrees) OuterConeDegrees = pending.OuterConeDegrees;
        if (pending.AreaWidth != before.AreaWidth) AreaWidth = pending.AreaWidth;
        if (pending.AreaHeight != before.AreaHeight) AreaHeight = pending.AreaHeight;
        if (pending.AreaSizeSpecified != before.AreaSizeSpecified) areaSizeSpecified = pending.AreaSizeSpecified;
        NotifyDirectionState();
    }

    internal EditorRecoveryNativeLightDraft CaptureRecoveryDraft() => new()
    {
        Original = original, OriginalDegrees = originalDegrees, Name = Name, KindIndex = KindIndex,
        Enabled = Enabled, CastShadows = CastShadows,
        PositionX = PositionX, PositionY = PositionY, PositionZ = PositionZ,
        RotationX = RotationX, RotationY = RotationY, RotationZ = RotationZ,
        TargetX = TargetX, TargetY = TargetY, TargetZ = TargetZ,
        AimDistance = aimDistance, AimDistanceModified = aimDistanceModified,
        TargetEdited = targetEdited, DirectRotation = directRotation,
        Red = Red, Green = Green, Blue = Blue, Intensity = Intensity, Range = Range,
        InnerConeDegrees = InnerConeDegrees, OuterConeDegrees = OuterConeDegrees,
        AreaWidth = AreaWidth, AreaHeight = AreaHeight, AreaSizeSpecified = areaSizeSpecified,
    };

    internal static EditorNativeLightItem RestoreRecoveryDraft(EditorRecoveryNativeLightDraft draft)
    {
        draft.Validate();
        var result = new EditorNativeLightItem(draft.Original, draft.OriginalDegrees);
        result.RestoreInput(draft);
        return result;
    }

    internal static Vector3 RotationDegrees(Quaternion rotation) => ToDegrees(rotation);

    public NativeSceneLight Build()
    {
        string label = Name == original.Name ? original.Name : Name.Trim();
        if (string.IsNullOrWhiteSpace(label) || label != original.Name && label.Any(char.IsControl)) throw Error("请填写光源名称，且不要包含控制字符。");
        if (!Enum.IsDefined((SceneLightKind)KindIndex)) throw Error("{0}：请选择光源类型。", label);
        var position = new Vector3(Number(PositionX, original.Position.X, "位置 X"), Number(PositionY, original.Position.Y, "位置 Y"), Number(PositionZ, original.Position.Z, "位置 Z"));
        if (targetEdited && HasDirection)
        {
            _ = RequiredNumber(targetX, "Mda.Text.Context.Lighting.TargetX");
            _ = RequiredNumber(targetY, "Mda.Text.Context.Lighting.TargetY");
            _ = RequiredNumber(targetZ, "Mda.Text.Context.Lighting.TargetZ");
            if (!aimDistanceModified || aimDistance is not { } d || !float.IsFinite(d) || d < 1e-4f)
                throw Error("Mda.Text.Context.Lighting.TargetTooClose", label);
        }
        var rotation = directRotation
            ?? (RotationX == Decimal(originalDegrees.X) && RotationY == Decimal(originalDegrees.Y) && RotationZ == Decimal(originalDegrees.Z)
                ? original.Rotation : Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(Radians(RotationY, "旋转 Y"), Radians(RotationX, "旋转 X"), Radians(RotationZ, "旋转 Z"))));
        var color = new Vector3(Number(Red, original.Color.X, "红色"), Number(Green, original.Color.Y, "绿色"), Number(Blue, original.Color.Z, "蓝色"));
        float strength = Number(Intensity, original.Intensity, "强度"), distance = Number(Range, original.Range, "影响范围");
        if (color.X < 0 || color.Y < 0 || color.Z < 0 || strength < 0) throw Error("{0}：颜色和强度不能小于 0。", label);
        if (distance <= 0) throw Error("{0}：影响范围必须大于 0。", label);
        float inner = Angle(InnerConeDegrees, original.InnerConeRadians, "内锥半角"), outer = Angle(OuterConeDegrees, original.OuterConeRadians, "外锥半角");
        if (inner < 0 || inner >= outer || outer > MathF.PI / 2) throw Error("{0}：锥角需满足 0° ≤ 内锥半角 < 外锥半角 ≤ 90°。", label);
        Vector2? areaSize = areaSizeSpecified || HasAreaSize ? new(Number(AreaWidth, original.AreaSize?.X ?? 5, "Mda.Text.Context.Lighting.AreaWidth"),
            Number(AreaHeight, original.AreaSize?.Y ?? 5, "Mda.Text.Context.Lighting.AreaHeight")) : null;
        if (areaSize is { } size && (size.X <= 0 || size.Y <= 0)) throw Error("Mda.Text.Context.Lighting.AreaSizeInvalid", label);
        float? resolvedAim = aimDistanceModified ? aimDistance : original.AimDistance;
        var result = original with { Name = label, Kind = (SceneLightKind)KindIndex, Position = position, Rotation = rotation,
            Color = color, Intensity = strength, Range = distance, InnerConeRadians = inner, OuterConeRadians = outer, Enabled = Enabled, CastShadows = CastShadows, GroupId = GroupId, AreaSize = areaSize, AimDistance = resolvedAim };
        SceneLightingSnapshot.ValidateLight(result);
        return result;
    }
    internal static string KindName(SceneLightKind kind) => Text(kind switch { SceneLightKind.Point => "点光", SceneLightKind.Spot => "聚光", SceneLightKind.Directional => "方向光", SceneLightKind.Area => "Mda.Text.Context.Lighting.Area", _ => "未选类型" });
    private static decimal? Decimal(float value) { try { return (decimal)value; } catch (OverflowException) { return null; } }
    private static float Number(decimal? value, float original, string label) => value == Decimal(original) ? original : RequiredNumber(value, label);
    private static float RequiredNumber(decimal? value, string label)
    {
        if (value is null) throw Error("请填写{0}。", EditorLocalization.Label(label));
        float result = (float)value.Value;
        if (!float.IsFinite(result)) throw Error("{0}超出支持范围。", EditorLocalization.Label(label));
        return result;
    }
    private static float Radians(decimal? value, string label) => RequiredNumber(value, label) * (MathF.PI / 180);
    private static float Angle(decimal? value, float original, string label) => value == Decimal(original * 180 / MathF.PI) ? original : Radians(value, label);
    private static Vector3 ToDegrees(Quaternion q)
    {
        double pitch = Math.Asin(Math.Clamp(2d * (q.W * q.X - q.Y * q.Z), -1, 1));
        bool singular = Math.Abs(Math.Cos(pitch)) < 1e-6;
        double yaw = singular ? 2 * Math.Atan2(q.Y, q.W) : Math.Atan2(2d * (q.W * q.Y + q.X * q.Z), 1 - 2d * (q.X * q.X + q.Y * q.Y));
        double roll = singular ? 0 : Math.Atan2(2d * (q.W * q.Z + q.X * q.Y), 1 - 2d * (q.X * q.X + q.Z * q.Z));
        return new((float)(pitch * 180 / Math.PI), (float)(yaw * 180 / Math.PI), (float)(roll * 180 / Math.PI));
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    { if (refreshingLocalization || EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(property); return true; }
    private void RefreshLocalization()
    {
        refreshingLocalization = true;
        try
        {
            foreach (var choice in KindChoices) choice.RefreshLocalization();
            Changed(null);
        }
        finally { refreshingLocalization = false; }
        Changed(nameof(KindIndex));
    }
    internal void ApplyAimDirection(Vector3 travel)
    {
        if (!Finite(travel) || travel.LengthSquared() < 1e-12f) return;
        var unitTravel = Vector3.Normalize(travel);
        var currentRotation = CurrentRotation();
        var currentDir = Vector3.Transform(-Vector3.UnitZ, currentRotation);
        var nextRotation = Quaternion.Normalize(SceneLightAim.Arc(currentDir, unitTravel) * currentRotation);
        SetOrientation(nextRotation, updateTarget: true);
    }

    internal void ApplyAimTarget(Vector3 target, bool growRange = true)
    {
        var pos = CurrentPosition();
        if (!Finite(pos) || !Finite(target)) return;
        var offset = target - pos;
        float dist = offset.Length();
        if (!float.IsFinite(dist) || dist < 1e-4f) return;
        var currentRotation = CurrentRotation();
        var currentDir = Vector3.Transform(-Vector3.UnitZ, currentRotation);
        var nextRotation = KindIndex == (int)SceneLightKind.Point
            ? currentRotation
            : Quaternion.Normalize(SceneLightAim.Arc(currentDir, offset / dist) * currentRotation);
        aimDistance = dist;
        aimDistanceModified = true;
        targetEdited = true;
        if (growRange && dist > CurrentRange() * SceneLightFalloff.MaxReferenceFraction)
            Range = Decimal(dist / SceneLightFalloff.MaxReferenceFraction);
        SetOrientation(nextRotation, updateTarget: false);
        targetX = Decimal(target.X); targetY = Decimal(target.Y); targetZ = Decimal(target.Z);
        Changed(nameof(TargetX)); Changed(nameof(TargetY)); Changed(nameof(TargetZ));
    }

    internal void FlipHemisphere()
    {
        var next = Controls.EditorLightAngles.FlipHemisphereDirection(CurrentDirection(), IsBacklit);
        backlit = !IsBacklit;
        ApplyAimDirection(next);
    }

    internal bool AimAtSceneCenter(PmmCameraPose camera)
    {
        var lookAt = camera.LookAt;
        if (!Finite(lookAt)) return false;
        var pos = CurrentPosition();
        if (Vector3.Distance(pos, lookAt) >= 0.05f)
        {
            ApplyAimTarget(lookAt);
            return true;
        }
        return AimFromCamera(camera);
    }

    internal bool AimFromCamera(PmmCameraPose camera)
    {
        try
        {
            var cam = EditorLightPlacement.ValidateCamera(camera);
            var forward = cam.Target - cam.Eye;
            if (!Finite(forward) || forward.LengthSquared() < 1e-12f) return false;
            ApplyAimDirection(forward);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    private void SetOrientation(Quaternion nextRotation, bool updateTarget)
    {
        directRotation = nextRotation;
        var degrees = ToDegrees(nextRotation);
        rotationX = Decimal(degrees.X); rotationY = Decimal(degrees.Y); rotationZ = Decimal(degrees.Z);
        Changed(nameof(RotationX)); Changed(nameof(RotationY)); Changed(nameof(RotationZ));
        if (updateTarget) SyncDisplayedTarget();
        NotifyDirectionState();
    }

    private void NotifyAimFromOrientation()
    {
        SyncDisplayedTarget();
        NotifyDirectionState();
    }

    private void NotifyDirectionState()
    {
        var dir = CurrentDirection();
        if (Finite(dir) && dir.LengthSquared() >= 1e-12f)
        {
            var normalized = Vector3.Normalize(dir);
            if (MathF.Abs(normalized.Z) > 1e-5f) backlit = normalized.Z < 0;
        }
        Changed(nameof(LightDirectionVector));
        Changed(nameof(IsBacklit));
        Changed(nameof(HemisphereLabel));
    }

    private void TryApplyTargetFromFields()
    {
        if (!targetX.HasValue || !targetY.HasValue || !targetZ.HasValue)
        {
            aimDistanceModified = true;
            aimDistance = null;
            return;
        }
        var target = new Vector3((float)targetX.Value, (float)targetY.Value, (float)targetZ.Value);
        var pos = CurrentPosition();
        if (!Finite(target) || !Finite(pos)) return;
        var offset = target - pos;
        float dist = offset.Length();
        if (!float.IsFinite(dist) || dist < 1e-4f)
        {
            aimDistanceModified = true;
            aimDistance = 0f;
            return;
        }
        var currentRotation = CurrentRotation();
        var currentDir = Vector3.Transform(-Vector3.UnitZ, currentRotation);
        var nextRotation = KindIndex == (int)SceneLightKind.Point
            ? currentRotation
            : Quaternion.Normalize(SceneLightAim.Arc(currentDir, offset / dist) * currentRotation);
        aimDistance = dist;
        aimDistanceModified = true;
        if (dist > CurrentRange() * SceneLightFalloff.MaxReferenceFraction)
            Range = Decimal(dist / SceneLightFalloff.MaxReferenceFraction);
        SetOrientation(nextRotation, updateTarget: false);
    }

    private void SyncDisplayedTarget()
    {
        var target = CurrentPosition() + CurrentDirection() * CurrentEffectiveAimDistance();
        if (!Finite(target)) return;
        targetX = Decimal(target.X); targetY = Decimal(target.Y); targetZ = Decimal(target.Z);
        Changed(nameof(TargetX)); Changed(nameof(TargetY)); Changed(nameof(TargetZ));
    }

    private Vector3 CurrentPosition() => new(
        PositionX.HasValue ? (float)PositionX.Value : original.Position.X,
        PositionY.HasValue ? (float)PositionY.Value : original.Position.Y,
        PositionZ.HasValue ? (float)PositionZ.Value : original.Position.Z);

    private Quaternion CurrentRotation()
    {
        if (directRotation is { } q) return q;
        if (RotationX == Decimal(originalDegrees.X) && RotationY == Decimal(originalDegrees.Y) && RotationZ == Decimal(originalDegrees.Z))
            return original.Rotation;
        if (RotationX.HasValue && RotationY.HasValue && RotationZ.HasValue)
            return Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(
                (float)RotationY.Value * (MathF.PI / 180),
                (float)RotationX.Value * (MathF.PI / 180),
                (float)RotationZ.Value * (MathF.PI / 180)));
        return original.Rotation;
    }

    private Vector3 CurrentDirection() => Vector3.Transform(-Vector3.UnitZ, CurrentRotation());

    private float CurrentRange() => Range.HasValue && (float)Range.Value > 0 ? (float)Range.Value : original.Range;

    private float CurrentEffectiveAimDistance()
        => aimDistanceModified && aimDistance is { } d && float.IsFinite(d) && d > 0
            ? d
            : (aimDistance ?? SceneLightFalloff.DefaultAimDistance(CurrentRange()));

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    public override string ToString() => Label;
}

public sealed class EditorLightKindChoice(SceneLightKind kind) : INotifyPropertyChanged
{
    public SceneLightKind Kind { get; } = kind;
    public string Label => EditorNativeLightItem.KindName(Kind);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLocalization() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    public override string ToString() => Label;
}
