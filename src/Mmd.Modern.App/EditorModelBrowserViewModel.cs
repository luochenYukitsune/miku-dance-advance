using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>Selection over authored models; sampled render geometry never supplies animation names.</summary>
public sealed class EditorModelBrowserViewModel : INotifyPropertyChanged
{
    private EditorModelItem? _selectedModel;
    private int? _selectedBoneIndex;
    private int? _selectedMorphIndex;
    private string? _selectedBoneGroupKey;
    private string? _selectedMorphGroupKey;
    private int _hierarchyTab;
    private bool _isHierarchyExpanded;
    private string _boneSearchText = "";
    private string _morphSearchText = "";
    private bool _refreshing;
    private ICommand? _importModelCommand;
    private ICommand? _removeModelCommand;
    private ICommand? _replaceModelCommand;
    private IReadOnlyList<EditorElementNode> _allBoneGroups = [];
    private IReadOnlyList<EditorElementNode> _allMorphGroups = [];

    public EditorModelBrowserViewModel() => Observe(this, static model => model.RefreshLocalization());

    public ObservableCollection<EditorModelItem> Models { get; } = [];
    public IReadOnlyList<EditorElementNode> BoneGroups { get; private set; } = [];
    public IReadOnlyList<EditorElementNode> MorphGroups { get; private set; } = [];
    public bool HasModels => Models.Count != 0;
    public bool NoModels => !HasModels;
    public bool HasSelectedModel => SelectedModel is not null;
    public bool HasNoBoneResults => BoneGroups.Count == 0;
    public bool HasNoMorphResults => MorphGroups.Count == 0;
    public string ModelCountLabel => Models.Count.ToString(CultureInfo.CurrentCulture);
    public string HierarchyLabel => Text("查找骨骼与表情");
    public string BoneEmptyPrompt => Text(SelectedModel is null ? "载入并选择模型后，显示骨骼分组。"
        : string.IsNullOrWhiteSpace(BoneSearchText) ? "此模型没有骨骼。" : "没有匹配的骨骼。");
    public string MorphEmptyPrompt => Text(SelectedModel is null ? "选择模型后，显示表情。"
        : string.IsNullOrWhiteSpace(MorphSearchText) ? "此模型没有表情。" : "没有匹配的表情。");
    public bool CanImportModel => ImportModelCommand?.CanExecute(null) == true;
    public bool CanRemoveModel => SelectedModel is not null && RemoveModelCommand?.CanExecute(SelectedModel) == true;

    public bool CanReplaceModel => SelectedModel is not null && ReplaceModelCommand?.CanExecute(SelectedModel) == true;

    public ICommand? ReplaceModelCommand
    {
        get => _replaceModelCommand;
        set
        {
            if (ReferenceEquals(_replaceModelCommand, value)) return;
            if (_replaceModelCommand is not null) _replaceModelCommand.CanExecuteChanged -= ReplaceCanExecuteChanged;
            _replaceModelCommand = value;
            if (_replaceModelCommand is not null) _replaceModelCommand.CanExecuteChanged += ReplaceCanExecuteChanged;
            OnPropertyChanged(); OnPropertyChanged(nameof(CanReplaceModel));
        }
    }

    public ICommand? ImportModelCommand
    {
        get => _importModelCommand;
        set
        {
            if (ReferenceEquals(_importModelCommand, value)) return;
            if (_importModelCommand is not null) _importModelCommand.CanExecuteChanged -= ImportCanExecuteChanged;
            _importModelCommand = value;
            if (_importModelCommand is not null) _importModelCommand.CanExecuteChanged += ImportCanExecuteChanged;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanImportModel));
        }
    }

    public ICommand? RemoveModelCommand
    {
        get => _removeModelCommand;
        set
        {
            if (ReferenceEquals(_removeModelCommand, value)) return;
            if (_removeModelCommand is not null) _removeModelCommand.CanExecuteChanged -= RemoveCanExecuteChanged;
            _removeModelCommand = value;
            if (_removeModelCommand is not null) _removeModelCommand.CanExecuteChanged += RemoveCanExecuteChanged;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRemoveModel));
        }
    }

    public EditorModelItem? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (_refreshing || ReferenceEquals(_selectedModel, value)) return;
            // A stale item from a replaced collection cannot become the active actor.
            if (value is not null && !Models.Contains(value)) return;
            _selectedModel = value;
            _selectedBoneIndex = null;
            _selectedMorphIndex = null;
            _selectedBoneGroupKey = null;
            _selectedMorphGroupKey = null;
            BuildGroups();
            NotifySelectionProperties();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int? SelectedBoneIndex
    {
        get => _selectedBoneIndex;
        set
        {
            if (value is { } index && (SelectedModel is null || index < 0 || index >= SelectedModel.PmxModel.Bones.Count))
                value = null;
            if (_selectedBoneIndex == value) return;
            _selectedBoneIndex = value;
            if (value is not null) _selectedBoneGroupKey = null;
            ExpandSelectedGroup(BoneGroups, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedBoneNode));
            if (!_refreshing) SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int? SelectedMorphIndex
    {
        get => _selectedMorphIndex;
        set
        {
            if (value is { } index && (SelectedModel is null || index < 0 || index >= SelectedModel.PmxModel.Morphs.Count))
                value = null;
            if (_selectedMorphIndex == value) return;
            _selectedMorphIndex = value;
            if (value is not null) _selectedMorphGroupKey = null;
            ExpandSelectedGroup(MorphGroups, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedMorphNode));
            if (!_refreshing) SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public EditorElementNode? SelectedBoneNode => SelectedBoneIndex is not null ? FindNode(BoneGroups, SelectedBoneIndex)
        : BoneGroups.FirstOrDefault(item => item.Key == _selectedBoneGroupKey);
    public EditorElementNode? SelectedMorphNode => SelectedMorphIndex is not null ? FindNode(MorphGroups, SelectedMorphIndex)
        : MorphGroups.FirstOrDefault(item => item.Key == _selectedMorphGroupKey);

    public int HierarchyTab
    {
        get => _hierarchyTab;
        set
        {
            if (value is < 0 or > 1 || _hierarchyTab == value) return;
            _hierarchyTab = value;
            OnPropertyChanged();
            if (!_refreshing) SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The optional target browser opens only by an explicit user action.</summary>
    public bool IsHierarchyExpanded
    {
        get => _isHierarchyExpanded;
        set
        {
            if (_isHierarchyExpanded == value) return;
            _isHierarchyExpanded = value;
            OnPropertyChanged();
        }
    }

    public string BoneSearchText
    {
        get => _boneSearchText;
        set
        {
            value ??= "";
            if (_boneSearchText == value) return;
            _boneSearchText = value;
            OnPropertyChanged();
            ApplyBoneFilter();
        }
    }

    public string MorphSearchText
    {
        get => _morphSearchText;
        set
        {
            value ??= "";
            if (_morphSearchText == value) return;
            _morphSearchText = value;
            OnPropertyChanged();
            ApplyMorphFilter();
        }
    }

    /// <summary>Refresh on the UI thread after an authored session change; preserve selection by object identity.</summary>
    public void Refresh(IReadOnlyList<SceneActorMotion> actors, IReadOnlyList<EditableSceneObject> objects, Guid? preferredId = null)
    {
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(objects);
        var oldModel = SelectedModel?.PmxModel;
        var oldId = SelectedModel?.ObjectId;
        var oldBone = SelectedBoneIndex;
        var oldMorph = SelectedMorphIndex;
        var oldBoneGroups = _allBoneGroups;
        var oldMorphGroups = _allMorphGroups;
        _refreshing = true;
        try
        {
            var byId = new Dictionary<Guid, SceneActorMotion>();
            foreach (var actor in actors) byId.TryAdd(actor.ObjectId, actor);
            Models.Clear();
            foreach (var item in objects)
                if (byId.TryGetValue(item.Id, out var actor))
                    Models.Add(new EditorModelItem(item.Id, item.Name, item.SourcePath, item.Role, actor.Model) { SelectionLabel = $"{Models.Count + 1}. {item.Name}" });
            _selectedModel = Models.FirstOrDefault(item => item.ObjectId == preferredId)
                ?? Models.FirstOrDefault(item => item.ObjectId == oldId) ?? Models.FirstOrDefault();
            var sameModel = oldId is not null && oldId == _selectedModel?.ObjectId;
            if (!sameModel) { _selectedBoneGroupKey = null; _selectedMorphGroupKey = null; }
            int? KeepIndex(int? index, IReadOnlyList<string> previous, IReadOnlyList<string> current)
            {
                if (!sameModel || index is not { } i || i >= previous.Count) return null;
                string name = previous[i];
                return current.Count(n => n == name) == 1 ? current.ToList().IndexOf(name) : null;
            }
            _selectedBoneIndex = KeepIndex(oldBone, oldModel?.Bones.Select(b => b.Name).ToArray() ?? [], _selectedModel?.PmxModel.Bones.Select(b => b.Name).ToArray() ?? []);
            _selectedMorphIndex = KeepIndex(oldMorph, oldModel?.Morphs.Select(m => m.Name).ToArray() ?? [], _selectedModel?.PmxModel.Morphs.Select(m => m.Name).ToArray() ?? []);
            BuildGroups(sameModel ? oldBoneGroups : null, sameModel ? oldMorphGroups : null);
            NotifySelectionProperties();
            OnPropertyChanged(nameof(HasModels));
            OnPropertyChanged(nameof(NoModels));
            OnPropertyChanged(nameof(ModelCountLabel));
        }
        finally { _refreshing = false; }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ignore collection-reset deselections while filtering; group selection deliberately clears the target.</summary>
    public void SelectBoneNode(EditorElementNode node)
    {
        if (!ContainsNode(BoneGroups, node)) return;
        _selectedBoneGroupKey = node.IsGroup ? node.Key : null;
        SelectedBoneIndex = node.Index;
        OnPropertyChanged(nameof(SelectedBoneNode));
    }

    public void SelectMorphNode(EditorElementNode node)
    {
        if (!ContainsNode(MorphGroups, node)) return;
        _selectedMorphGroupKey = node.IsGroup ? node.Key : null;
        SelectedMorphIndex = node.Index;
        OnPropertyChanged(nameof(SelectedMorphNode));
    }

    private void BuildGroups(IReadOnlyList<EditorElementNode>? previousBones = null, IReadOnlyList<EditorElementNode>? previousMorphs = null)
    {
        _allBoneGroups = MakeGroups(PmxDisplayTargetType.Bone, previousBones);
        _allMorphGroups = MakeGroups(PmxDisplayTargetType.Morph, previousMorphs);
        ApplyBoneFilter();
        ApplyMorphFilter();
    }

    private IReadOnlyList<EditorElementNode> MakeGroups(PmxDisplayTargetType type, IReadOnlyList<EditorElementNode>? previous)
    {
        if (SelectedModel is not { PmxModel: var model }) return [];
        var isBone = type == PmxDisplayTargetType.Bone;
        var leaves = isBone
            ? model.Bones.Select((item, i) => EditorElementNode.Element(i, Name(item.Name, item.EnglishName, Format("骨骼 #{0}", i)), item.EnglishName)).ToArray()
            : model.Morphs.Select((item, i) => EditorElementNode.Element(i, Name(item.Name, item.EnglishName, Format("表情 #{0}", i)), item.EnglishName)).ToArray();
        var grouped = new HashSet<int>();
        var result = new List<EditorElementNode>();
        for (var frameIndex = 0; frameIndex < model.DisplayFrames.Count; frameIndex++)
        {
            var frame = model.DisplayFrames[frameIndex];
            var indices = frame.Targets.Where(target => target.Type == type && target.Index >= 0 && target.Index < leaves.Length)
                .Select(target => target.Index).Distinct().ToArray();
            if (indices.Length == 0) continue;
            foreach (var index in indices) grouped.Add(index);
            var key = "display:" + frameIndex.ToString(CultureInfo.InvariantCulture);
            var expanded = previous?.FirstOrDefault(item => item.Key == key)?.IsExpanded ?? result.Count == 0;
            result.Add(EditorElementNode.Group(key, Name(frame.Name, frame.EnglishName, Text("未命名分组")), frame.EnglishName,
                indices.Select(index => leaves[index]).ToArray(), expanded));
        }
        var ungrouped = leaves.Where(item => !grouped.Contains(item.Index!.Value)).ToArray();
        if (ungrouped.Length > 0)
        {
            var caption = Text(result.Count == 0 ? (isBone ? "全部骨骼" : "全部表情") : "未分组");
            var expanded = previous?.FirstOrDefault(item => item.Key == "ungrouped")?.IsExpanded ?? result.Count == 0;
            result.Add(EditorElementNode.Group("ungrouped", caption, "", ungrouped, expanded));
        }
        return result;
    }

    private void ApplyBoneFilter()
    {
        BoneGroups = Filter(_allBoneGroups, BoneSearchText);
        ExpandSelectedGroup(BoneGroups, SelectedBoneIndex);
        OnPropertyChanged(nameof(BoneGroups));
        OnPropertyChanged(nameof(SelectedBoneNode));
        OnPropertyChanged(nameof(HasNoBoneResults));
        OnPropertyChanged(nameof(BoneEmptyPrompt));
    }

    private void ApplyMorphFilter()
    {
        MorphGroups = Filter(_allMorphGroups, MorphSearchText);
        ExpandSelectedGroup(MorphGroups, SelectedMorphIndex);
        OnPropertyChanged(nameof(MorphGroups));
        OnPropertyChanged(nameof(SelectedMorphNode));
        OnPropertyChanged(nameof(HasNoMorphResults));
        OnPropertyChanged(nameof(MorphEmptyPrompt));
    }

    private static IReadOnlyList<EditorElementNode> Filter(IReadOnlyList<EditorElementNode> groups, string search)
    {
        search = search.Trim();
        if (search.Length == 0) return groups;
        var result = new List<EditorElementNode>();
        foreach (var group in groups)
        {
            var children = Matches(group, search) ? group.Children : group.Children.Where(item => Matches(item, search)).ToArray();
            if (children.Count > 0)
                result.Add(EditorElementNode.Group(group.Key, group.DisplayName, group.EnglishName, children, true));
        }
        return result;
    }

    private static bool Matches(EditorElementNode item, string search) =>
        item.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
        || item.EnglishName.Contains(search, StringComparison.OrdinalIgnoreCase)
        || item.Index?.ToString(CultureInfo.InvariantCulture) == search;

    private static EditorElementNode? FindNode(IReadOnlyList<EditorElementNode> groups, int? index) =>
        index is null ? null : groups.SelectMany(item => item.Children).FirstOrDefault(item => item.Index == index);

    private static bool ContainsNode(IReadOnlyList<EditorElementNode> groups, EditorElementNode node) =>
        groups.Contains(node) || groups.Any(group => group.Children.Contains(node));

    private static void ExpandSelectedGroup(IReadOnlyList<EditorElementNode> groups, int? index)
    {
        if (index is null) return;
        var group = groups.FirstOrDefault(item => item.Children.Any(child => child.Index == index));
        if (group is not null) group.IsExpanded = true;
    }

    private static string Name(string name, string englishName, string fallback) =>
        !string.IsNullOrWhiteSpace(name) ? name : !string.IsNullOrWhiteSpace(englishName) ? englishName : fallback;

    private void NotifySelectionProperties()
    {
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(HasSelectedModel));
        OnPropertyChanged(nameof(SelectedBoneIndex));
        OnPropertyChanged(nameof(SelectedMorphIndex));
        OnPropertyChanged(nameof(SelectedBoneNode));
        OnPropertyChanged(nameof(SelectedMorphNode));
        OnPropertyChanged(nameof(CanRemoveModel));
        OnPropertyChanged(nameof(CanReplaceModel));
    }

    private void ReplaceCanExecuteChanged(object? sender, EventArgs args) => OnPropertyChanged(nameof(CanReplaceModel));
    private void ImportCanExecuteChanged(object? sender, EventArgs args) => OnPropertyChanged(nameof(CanImportModel));
    private void RemoveCanExecuteChanged(object? sender, EventArgs args) => OnPropertyChanged(nameof(CanRemoveModel));
    private void RefreshLocalization()
    {
        // Rebuild only generated captions, retaining authored names, identity and group expansion.
        _refreshing = true;
        try
        {
            foreach (var item in Models) item.RefreshLocalization();
            BuildGroups(_allBoneGroups, _allMorphGroups);
            OnPropertyChanged(null);
        }
        finally { _refreshing = false; }
    }
    public event EventHandler? SelectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed record EditorModelItem(Guid ObjectId, string Name, string SourcePath, ModelRole Role, PmxModel PmxModel) : INotifyPropertyChanged
{
    public string SelectionLabel { get; init; } = Name;
    public string RoleLabel => Text(Role switch { ModelRole.Character => "角色", ModelRole.Environment => "场景", _ => "道具" });
    public string Detail => Format("{0} · {1} 骨骼 · {2} 表情", RoleLabel, PmxModel.Bones.Count, PmxModel.Morphs.Count);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLocalization() => PropertyChanged?.Invoke(this, new(null));
}

/// <summary>A model-authored display frame or an indexed animation target. Groups carry no editable index.</summary>
public sealed class EditorElementNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private EditorElementNode(string key, string name, string englishName, int? index, IReadOnlyList<EditorElementNode> children, bool expanded)
    { Key = key; DisplayName = name; EnglishName = englishName; Index = index; Children = children; _isExpanded = expanded; }
    public string Key { get; }
    public string DisplayName { get; }
    public string EnglishName { get; }
    public int? Index { get; }
    public IReadOnlyList<EditorElementNode> Children { get; }
    public bool IsGroup => Index is null;
    public bool IsLeaf => !IsGroup;
    public string GroupCount => IsGroup ? Children.Count.ToString(CultureInfo.CurrentCulture) : "";
    public string ToolTip => string.IsNullOrWhiteSpace(EnglishName) || EnglishName == DisplayName
        ? DisplayName : DisplayName + "\n" + EnglishName;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
        }
    }
    internal static EditorElementNode Element(int index, string name, string englishName) => new("element:" + index, name, englishName, index, [], false);
    internal static EditorElementNode Group(string key, string name, string englishName, IReadOnlyList<EditorElementNode> children, bool expanded) => new(key, name, englishName, null, children, expanded);
    public event PropertyChangedEventHandler? PropertyChanged;
}
