using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Mmd.Modern.Core;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

public sealed partial class EditorLightingViewModel
{
    private bool changingSelection;
    private IReadOnlyList<Guid> selectedLightIds = Array.Empty<Guid>();
    private IReadOnlyList<Guid> selectedGroupIds = Array.Empty<Guid>();
    private IReadOnlyList<object> selectedRows = Array.Empty<object>();
    private decimal? batchIntensity = 1;
    private decimal relativeBrightness = 1;
    private bool isModernWorkspace;
    public bool IsModernWorkspace { get => isModernWorkspace; internal set { if (isModernWorkspace == value) return; isModernWorkspace = value; Changed(); RefreshCapacity(); } }
    public ObservableCollection<EditorLightGroupItem> Groups { get; } = [];
    public IReadOnlyList<Guid> SelectedLightIds => selectedLightIds;
    public IReadOnlyList<Guid> SelectedGroupIds => selectedGroupIds;
    public IReadOnlyList<object> SelectedRows => selectedRows;
    public EditorLightGroupItem? SelectedGroup => SelectedGroupIds.Count == 1 ? Groups.FirstOrDefault(group => group.Id == SelectedGroupIds[0]) : null;
    public bool HasSelectedGroup => SelectedGroup is not null;
    public bool HasSelection => SelectedLightIds.Count != 0 || SelectedGroupIds.Count != 0;
    public bool ShowSingleLight => SelectedLightIds.Count == 1 && SelectedGroupIds.Count == 0;
    public bool ShowBatchControls => HasSelection && !ShowSingleLight;
    public bool CanGroupSelection => SelectedLightIds.Count > 0 && SelectedGroupIds.Count == 0;
    public bool CanDissolveSelection => SelectedGroupIds.Count != 0;
    public bool CanDuplicateSelection => HasSelection;
    public string SelectionSummary => SelectedGroupIds.Count == 0
        ? Format("Mda.Text.Context.Lighting.SelectedLights", SelectedLightIds.Count)
        : Format("Mda.Text.Context.Lighting.SelectedGroups", SelectedGroupIds.Count, SelectedLightIds.Count);
    public decimal? BatchIntensity { get => batchIntensity; set { batchIntensity = value; Changed(); } }
    public string GroupNameLabel => Text("Mda.Text.Context.Lighting.GroupName");
    public string GroupIntensityLabel => Text("Mda.Text.Context.Lighting.GroupBrightness");
    public string GroupSelectionLabel => Text("Mda.Text.Context.Lighting.GroupAction");
    public string DissolveSelectionLabel => Text("Mda.Text.Context.Lighting.DissolveGroup");
    public string DuplicateSelectionLabel => Text("复制");
    public string EnableSelectionLabel => Text("Mda.Text.Context.Lighting.EnableSelection");
    public string DisableSelectionLabel => Text("Mda.Text.Context.Lighting.DisableSelection");
    public string BatchIntensityLabel => Text("Mda.Text.Context.Lighting.BatchBrightness");
    public string ApplyIntensityLabel => Text("应用");
    public string SelectionHint => Text("Mda.Text.Context.Lighting.SelectionHint");
    public string RelativeBrightnessLabel => Text("Mda.Text.Context.Lighting.RelativeBrightness");
    public double RelativeBrightnessPosition => Math.Log10((double)relativeBrightness);
    public string RelativeBrightnessSummary => relativeBrightness.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) + "×";
    internal void ResetRelativeBrightness() { relativeBrightness = 1; Changed(nameof(RelativeBrightnessPosition)); Changed(nameof(RelativeBrightnessSummary)); }
    internal bool ScaleSelectedIntensity(SceneLightingSnapshot baseline, decimal multiplier)
    {
        try
        {
            var selected = SelectedLightIds.ToHashSet();
            var values = baseline.Lights.Where(light => selected.Contains(light.Id)).ToDictionary(light => light.Id,
                light => multiplier == 1 ? light.Intensity : light.Intensity * (float)multiplier);
            var current = Lights.Select(light => light.Build()).ToArray();
            _ = CreateSnapshot(current.Select(light => values.TryGetValue(light.Id, out float intensity)
                ? light with { Intensity = intensity } : light), Groups.Select(group => group.Build()));
            // Prepare every decimal before changing an item. HDR values outside the numeric editor's
            // representable range fail the gesture as a whole rather than corrupting another lamp.
            var edits = values.ToDictionary(pair => pair.Key, pair => (decimal)pair.Value);
            foreach (var light in Lights.Where(light => edits.ContainsKey(light.Id))) light.Intensity = edits[light.Id];
            relativeBrightness = multiplier; Changed(nameof(RelativeBrightnessSummary)); ValidationMessage = ""; return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { SetValidationError(error); return false; }
    }

    public IReadOnlyList<object> BrowserRows
    {
        get
        {
            string query = SearchText.Trim();
            bool Match(string value) => query.Length == 0 || value.Contains(query, StringComparison.OrdinalIgnoreCase);
            List<object> rows = [];
            foreach (var group in Groups)
            {
                var children = Lights.Where(light => light.GroupId == group.Id).ToArray();
                bool groupMatches = Match(group.Name);
                var visible = groupMatches ? children : children.Where(light => Match(light.Label)).ToArray();
                if (!groupMatches && visible.Length == 0) continue;
                rows.Add(group); rows.AddRange(visible);
            }
            rows.AddRange(Lights.Where(light => light.GroupId is null && Match(light.Label)));
            return rows;
        }
    }
    public bool HasBrowserRows => BrowserRows.Count != 0;
    public bool HasOutlineLights => Lights.Count != 0 || Groups.Count != 0;
    public bool IsOutlineSearchEmpty => HasOutlineLights && !string.IsNullOrWhiteSpace(SearchText) && !HasBrowserRows;

    private void InitializeGroups(SceneLightingSnapshot snapshot)
    {
        Groups.CollectionChanged += (_, args) =>
        {
            if (args.OldItems is not null) foreach (EditorLightGroupItem item in args.OldItems) item.PropertyChanged -= GroupChanged;
            if (args.NewItems is not null) foreach (EditorLightGroupItem item in args.NewItems) item.PropertyChanged += GroupChanged;
            RefreshFilter();
            RefreshCapacity();
        };
        foreach (var group in snapshot.Groups) Groups.Add(new(group));
    }
    private void GroupChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or nameof(EditorLightGroupItem.Name)) RefreshFilter();
        if (args.PropertyName is null or nameof(EditorLightGroupItem.Enabled)) RefreshCapacity();
    }

    /// <summary>One list selection can contain group rows and explicit lights. Child identities are always
    /// normalized to authored light order, so mixed group/child selections never transform a lamp twice.</summary>
    public void SelectRows(IEnumerable<object> rows, object? primary = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var chosen = rows.Distinct().Where(row => row is EditorLightGroupItem group && Groups.Contains(group)
            || row is EditorNativeLightItem light && Lights.Contains(light)).ToArray();
        var groups = chosen.OfType<EditorLightGroupItem>().Select(group => group.Id).ToHashSet();
        var requested = chosen.OfType<EditorNativeLightItem>().Select(light => light.Id).ToHashSet();
        foreach (var light in Lights.Where(light => light.GroupId is { } group && groups.Contains(group))) requested.Add(light.Id);
        Guid? primaryId = primary switch
        {
            EditorNativeLightItem light when requested.Contains(light.Id) => light.Id,
            EditorLightGroupItem group => Lights.FirstOrDefault(light => light.GroupId == group.Id)?.Id,
            _ => SelectedLight?.Id is { } previous && requested.Contains(previous) ? previous : null
        };
        SetSelection(chosen, requested, groups, primaryId);
    }

    public void SelectMany(IEnumerable<Guid> ids, Guid? primary = null, bool reveal = false, bool preserveGroups = true)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var requested = ids.ToHashSet();
        var ordered = Lights.Where(light => requested.Contains(light.Id)).Select(light => light.Id).ToArray();
        bool same = ordered.SequenceEqual(SelectedLightIds);
        HashSet<Guid> groups = preserveGroups && same ? SelectedGroupIds.ToHashSet() : [];
        var rows = Groups.Where(group => groups.Contains(group.Id)).Cast<object>()
            .Concat(Lights.Where(light => requested.Contains(light.Id) && !(light.GroupId is { } group && groups.Contains(group)))).ToArray();
        SetSelection(rows, ordered.ToHashSet(), groups, primary);
        if (reveal && ordered.Any(id => !BrowserRows.OfType<EditorNativeLightItem>().Any(light => light.Id == id))) SearchText = "";
    }

    public void SelectGroupIds(IEnumerable<Guid> ids, Guid? primaryGroupId = null)
    {
        var requested = ids.ToHashSet();
        var rows = Groups.Where(group => requested.Contains(group.Id)).ToArray();
        SelectRows(rows, rows.FirstOrDefault(group => group.Id == primaryGroupId) ?? rows.FirstOrDefault());
    }

    internal void RestoreSelection(IEnumerable<Guid> lightIds, Guid? primary, IEnumerable<Guid> groupIds)
    {
        var groups = groupIds.ToHashSet();
        var lights = lightIds.ToHashSet();
        var rows = Groups.Where(group => groups.Contains(group.Id)).Cast<object>()
            .Concat(Lights.Where(light => lights.Contains(light.Id) && !(light.GroupId is { } group && groups.Contains(group)))).ToArray();
        SelectRows(rows, Lights.FirstOrDefault(light => light.Id == primary));
    }

    private void SetSelection(IReadOnlyList<object> rows, HashSet<Guid> ids, HashSet<Guid> groups, Guid? primary)
    {
        var ordered = Lights.Where(light => ids.Contains(light.Id)).Select(light => light.Id).ToArray();
        var orderedGroups = Groups.Where(group => groups.Contains(group.Id)).Select(group => group.Id).ToArray();
        var current = Lights.FirstOrDefault(light => light.Id == primary && ids.Contains(light.Id))
            ?? Lights.FirstOrDefault(light => ids.Contains(light.Id));
        if (SelectedLight?.Id == current?.Id && ordered.SequenceEqual(SelectedLightIds)
            && orderedGroups.SequenceEqual(SelectedGroupIds) && rows.SequenceEqual(selectedRows)) return;
        changingSelection = true;
        try
        {
            selectedRows = rows; selectedLightIds = Array.AsReadOnly(ordered); selectedGroupIds = Array.AsReadOnly(orderedGroups);
            selectedLight = current;
            foreach (var light in Lights)
            {
                light.IsSelected = ids.Contains(light.Id);
                light.IsInspectorVisible = ShowSingleLight && light.IsSelected;
            }
            foreach (var group in Groups) group.IsSelected = groups.Contains(group.Id);
        }
        finally { changingSelection = false; }
        Changed(nameof(SelectedLight)); Changed(nameof(HasSelectedLight)); Changed(nameof(SelectedGroup)); Changed(nameof(HasSelectedGroup));
        Changed(nameof(HasSelection)); Changed(nameof(ShowSingleLight)); Changed(nameof(ShowBatchControls)); Changed(nameof(CanGroupSelection));
        Changed(nameof(CanDissolveSelection)); Changed(nameof(CanDuplicateSelection)); Changed(nameof(SelectionSummary));
        Changed(nameof(SelectedRows)); Changed(nameof(SelectedGroupIds)); Changed(nameof(SelectedLightIds));
        // After the selection notifications: the inspector captures unfinished text from the
        // previous panel before that panel is removed.
        SyncInspectorLights();
    }

    public bool SetSelectedEnabled(bool enabled)
    {
        // A group switch is a mask. It must never overwrite the authored switches of its members.
        var groups = SelectedGroupIds.ToHashSet();
        var direct = SelectedLightIds.Where(id => Lights.Any(light => light.Id == id
            && !(light.GroupId is { } group && groups.Contains(group)))).ToHashSet();
        try
        {
            _ = CreateSnapshot(Lights.Select(light => direct.Contains(light.Id) ? light.Build() with { Enabled = enabled } : light.Build()),
                Groups.Select(group => groups.Contains(group.Id) ? group.Build() with { Enabled = enabled } : group.Build()));
            foreach (var group in Groups.Where(group => groups.Contains(group.Id))) group.Enabled = enabled;
            foreach (var light in Lights.Where(light => direct.Contains(light.Id))) light.Enabled = enabled;
            ValidationMessage = ""; return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { SetValidationError(error); return false; }
    }

    public bool SetSelectedIntensity(decimal? intensity)
    {
        if (intensity is null or < 0 || !float.IsFinite((float)intensity.Value))
        { ValidationMessage = "Mda.Text.Context.Lighting.PositiveBrightness"; return false; }
        try
        {
            _ = CreateSnapshot(Lights.Select(light => light.Build() is { } value && SelectedLightIds.Contains(light.Id)
                ? value with { Intensity = (float)intensity.Value } : light.Build()), Groups.Select(group => group.Build()));
            foreach (var light in Lights.Where(light => SelectedLightIds.Contains(light.Id))) light.Intensity = intensity;
            BatchIntensity = intensity; ValidationMessage = ""; return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { SetValidationError(error); return false; }
    }

    public bool GroupSelection()
    {
        if (!CanGroupSelection) return false;
        try
        {
            var id = Guid.NewGuid();
            var group = new SceneLightGroup(id, UniqueName(Text("Mda.Text.Context.Lighting.GroupType"), Groups.Select(group => group.Name)));
            var chosen = SelectedLightIds.ToHashSet();
            _ = CreateSnapshot(Lights.Select(light => light.Build() with { GroupId = chosen.Contains(light.Id) ? id : light.GroupId }),
                Groups.Select(item => item.Build()).Append(group));
            Groups.Add(new(group));
            foreach (var light in Lights.Where(light => chosen.Contains(light.Id))) light.GroupId = id;
            SelectGroupIds([id], id); ValidationMessage = ""; return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { SetValidationError(error); return false; }
    }

    public bool DissolveSelectedGroups()
    {
        var chosen = SelectedGroupIds.ToHashSet();
        var lights = SelectedLightIds.ToArray();
        try
        {
            _ = CreateSnapshot(Lights.Select(light => light.Build() is { } value && value.GroupId is { } group && chosen.Contains(group)
                ? value with { GroupId = null } : light.Build()), Groups.Where(group => !chosen.Contains(group.Id)).Select(group => group.Build()));
            foreach (var light in Lights.Where(light => light.GroupId is { } group && chosen.Contains(group))) light.GroupId = null;
            foreach (var group in Groups.Where(group => chosen.Contains(group.Id)).ToArray()) Groups.Remove(group);
            SelectMany(lights, SelectedLight?.Id, preserveGroups: false); ValidationMessage = ""; return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { SetValidationError(error); return false; }
    }

    public bool DuplicateSelection()
    {
        if (!HasSelection) return false;
        if (Lights.Count + SelectedLightIds.Count > SceneLightingSnapshot.MaxLights)
        {
            SetValidationError(Error("Mda.Text.Context.Lighting.CopyCapacity", SceneLightingSnapshot.MaxLights));
            return false;
        }
        try
        {
            var selectedGroups = Groups.Where(group => SelectedGroupIds.Contains(group.Id)).ToArray();
            List<SceneLightGroup> copies = [];
            Dictionary<Guid, Guid> mapping = [];
            foreach (var item in selectedGroups)
            {
                var group = item.Build(); var id = Guid.NewGuid(); mapping.Add(group.Id, id);
                copies.Add(group with { Id = id, Name = UniqueName(group.Name, Groups.Select(group => group.Name).Concat(copies.Select(group => group.Name))) });
            }
            List<NativeSceneLight> newLights = [];
            foreach (var item in Lights.Where(light => SelectedLightIds.Contains(light.Id)))
            {
                var light = item.Build();
                newLights.Add(light with { Id = Guid.NewGuid(),
                    Name = UniqueName(light.Name, Lights.Select(light => light.Name).Concat(newLights.Select(light => light.Name))),
                    GroupId = light.GroupId is { } group && mapping.TryGetValue(group, out var copy) ? copy : light.GroupId });
            }
            // Validate the complete candidate before mutating either collection. Capacity/invalid drafts cannot leave half a copy.
            _ = CreateSnapshot(Lights.Select(light => light.Build()).Concat(newLights), Groups.Select(group => group.Build()).Concat(copies));
            foreach (var group in copies) Groups.Add(new(group));
            foreach (var light in newLights) Lights.Add(new(light) { UseModernControls = Lights.FirstOrDefault()?.UseModernControls == true });
            RestoreSelection(newLights.Select(light => light.Id), newLights.FirstOrDefault()?.Id, copies.Select(group => group.Id));
            CollectionChanged(); ValidationMessage = ""; return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { SetValidationError(error); return false; }
    }

    public void RemoveSelection()
    {
        var ids = SelectedLightIds.ToHashSet(); var groups = SelectedGroupIds.ToHashSet();
        foreach (var light in Lights.Where(light => ids.Contains(light.Id)).ToArray()) Lights.Remove(light);
        foreach (var group in Groups.Where(group => groups.Contains(group.Id)).ToArray()) Groups.Remove(group);
        SelectMany([], preserveGroups: false); CollectionChanged(); ValidationMessage = "";
    }

    private static string UniqueName(string basis, IEnumerable<string> existing)
    {
        var names = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int ordinal = 1;
        while (names.Contains($"{basis} {ordinal}")) ordinal++;
        return $"{basis} {ordinal}";
    }
}

public sealed class EditorLightGroupItem : INotifyPropertyChanged
{
    private readonly SceneLightGroup original;
    private string name;
    private bool enabled, isSelected;
    private bool refreshingLocalization;
    private decimal? intensity;
    public EditorLightGroupItem(SceneLightGroup group)
    {
        original = group; name = group.Name; enabled = group.Enabled;
        try { intensity = (decimal)group.Intensity; } catch (OverflowException) { intensity = null; }
        Observe(this, static model => model.RefreshLocalization());
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public Guid Id => original.Id;
    public string Name { get => name; set => Set(ref name, value); }
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public decimal? Intensity { get => intensity; set { Set(ref intensity, value); PropertyChanged?.Invoke(this, new(nameof(BrightnessSliderPosition))); } }
    public double BrightnessSliderPosition => EditorLightParameterScale.BrightnessPosition(Intensity);
    public bool IsSelected { get => isSelected; internal set => Set(ref isSelected, value); }
    public string TypeLabel => Text("Mda.Text.Context.Lighting.GroupType");
    public string NameLabel => Text("Mda.Text.Context.Lighting.GroupName");
    public string IntensityLabel => Text("Mda.Text.Context.Lighting.GroupBrightness");
    public SceneLightGroup Build()
    {
        decimal? originalValue;
        try { originalValue = (decimal)original.Intensity; } catch (OverflowException) { originalValue = null; }
        if (Intensity is null && originalValue is not null) throw Error("Mda.Text.Context.Lighting.RequiredGroupBrightness");
        float value;
        value = Intensity == originalValue ? original.Intensity : (float)Intensity!.Value;
        var result = original with { Name = Name == original.Name ? original.Name : Name.Trim(), Enabled = Enabled, Intensity = value };
        _ = new SceneLightingSnapshot([], [result]);
        return result;
    }
    internal EditorRecoveryLightGroupDraft CaptureDraft() => new() { Original = original, Name = Name, Enabled = Enabled, Intensity = Intensity };
    internal void RestoreInput(EditorRecoveryLightGroupDraft draft)
    { Name = draft.Name; Enabled = draft.Enabled; Intensity = draft.Intensity; }
    internal void RestorePending(EditorRecoveryLightGroupDraft pending, SceneLightGroup committed)
    {
        var before = new EditorLightGroupItem(committed).CaptureDraft();
        if (pending.Name != before.Name) Name = pending.Name;
        if (pending.Enabled != before.Enabled) Enabled = pending.Enabled;
        if (pending.Intensity != before.Intensity) Intensity = pending.Intensity;
    }
    internal static EditorLightGroupItem RestoreDraft(EditorRecoveryLightGroupDraft draft)
    {
        draft.Validate();
        return new(draft.Original) { Name = draft.Name, Enabled = draft.Enabled, Intensity = draft.Intensity };
    }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (refreshingLocalization || EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; PropertyChanged?.Invoke(this, new(name));
    }
    private void RefreshLocalization()
    {
        refreshingLocalization = true;
        try { PropertyChanged?.Invoke(this, new(null)); }
        finally { refreshingLocalization = false; }
    }
}

public sealed record EditorRecoveryLightGroupDraft
{
    public required SceneLightGroup Original { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; }
    public decimal? Intensity { get; init; }
    public string? NumericText { get; init; }
    internal void Validate()
    {
        _ = new SceneLightingSnapshot([], [Original]);
        EditorRecoverySessionState.ValidateText(Original.Name);
        EditorRecoverySessionState.ValidateText(Name);
        if (NumericText is not null) EditorRecoverySessionState.ValidateText(NumericText);
    }
}
