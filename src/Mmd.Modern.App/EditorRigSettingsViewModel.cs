using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>A detached subject-slot change; null removes the existing static attachment.</summary>
public sealed record EditorOutsideParentChange(int BoneIndex, SceneOutsideParentBinding? Binding, bool KeepWorld = false);

/// <summary>The host commits attachments and the optional edited motion in one workspace transaction.</summary>
public sealed record EditorRigSettingsDraft(Guid ObjectId, IReadOnlyList<EditorOutsideParentChange> OutsideParents,
    VmdMotion? Motion);

public sealed record EditorRigBoneChoice(int Index, string AuthoredName) : INotifyPropertyChanged
{
    public string Label => Index < 0 ? Text("整个模型") : $"{Index} · {AuthoredName}";
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLocalization() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    public override string ToString() => Label;
}

public sealed record EditorRigModelChoice(Guid? ObjectId, string AuthoredName) : INotifyPropertyChanged
{
    public string Label => ObjectId is null ? Text("无外亲") : AuthoredName;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLocalization() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    public override string ToString() => Label;
}

/// <summary>One real PMX IK controller. Authored names are never passed through localization.</summary>
public sealed class EditorRigIkItem(int boneIndex, string name, bool enabled) : INotifyPropertyChanged
{
    private bool isEnabled = enabled;
    public int BoneIndex { get; } = boneIndex;
    public string BoneName { get; } = name;
    public string Label => $"{BoneIndex} · {BoneName}";
    public bool OriginalEnabled { get; } = enabled;
    public bool Enabled
    {
        get => isEnabled;
        set { if (isEnabled == value) return; isEnabled = value; PropertyChanged?.Invoke(this, new(nameof(Enabled))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Opening, switching subjects and cancelling only alter private draft state. Keep-world
/// preparation delegates to the application's existing independently owned attachment candidate.</summary>
public sealed class EditorRigSettingsViewModel : INotifyPropertyChanged
{
    private sealed class ParentDraft(SceneOutsideParentBinding? original)
    {
        public SceneOutsideParentBinding? Original { get; } = original;
        public Guid? ParentObjectId { get; set; } = original?.ParentObjectId;
        public int? ParentBoneIndex { get; set; } = original?.ParentBoneIndex;
        public bool KeepWorld { get; set; } = true;
        public bool Changed => ParentObjectId != Original?.ParentObjectId
            || ParentObjectId is not null && ParentBoneIndex != Original?.ParentBoneIndex;
    }

    private readonly EditorProjectWorkspace workspace;
    private readonly SceneProjectSession session;
    private readonly SceneActorMotion actor;
    private readonly SceneActorMotion[] originalMotions;
    private readonly EditableSceneObject[] originalObjects;
    private readonly SceneOutsideParentBinding[] originalParents;
    private readonly ScenePmmControlState? originalPmmControls;
    private readonly Dictionary<int, ParentDraft> parents = [];
    private EditorRigBoneChoice? subject, targetBone;
    private EditorRigModelChoice? targetModel;
    private bool loading, isBusy;
    private string validationMessage = "";
    private Exception? validationError;

    public EditorRigSettingsViewModel(EditorProjectWorkspace workspace, Guid objectId, int? selectedBoneIndex = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        this.workspace = workspace;
        session = workspace.Session ?? throw new InvalidOperationException(Text("请先载入模型。"));
        originalMotions = workspace.Motions.ToArray();
        actor = originalMotions.SingleOrDefault(item => item.ObjectId == objectId)
            ?? throw new ArgumentException(Text("选中的模型已不存在。"), nameof(objectId));
        originalObjects = session.Objects.ToArray(); originalParents = session.OutsideParents.ToArray();
        originalPmmControls = session.PmmControls; Frame = session.CurrentFrame;
        if (Frame != Math.Floor(Frame) || Frame > uint.MaxValue)
            throw new InvalidOperationException(Text("请先定位到一个整数帧，再编辑 IK。"));
        ModelName = originalObjects.Single(item => item.Id == objectId).Name;
        Subjects = new[] { new EditorRigBoneChoice(-1, "") }
            .Concat(actor.Model.Bones.Select((bone, index) => new EditorRigBoneChoice(index, bone.Name))).ToArray();
        TargetModels = new[] { new EditorRigModelChoice(null, "") }
            .Concat(originalObjects.Where(item => item.Id != objectId && originalMotions.Any(motion => motion.ObjectId == item.Id))
                .Select(item => new EditorRigModelChoice(item.Id, item.Name))).ToArray();
        foreach (var choice in Subjects)
        {
            var binding = originalParents.SingleOrDefault(item => item.ObjectId == objectId && item.BoneIndex == choice.Index);
            // PMM-authored dynamic attachments are displayed truthfully and remain owned by their timeline.
            binding ??= originalPmmControls?.Sample(Frame).OutsideParents.SingleOrDefault(item => item.ObjectId == objectId && item.BoneIndex == choice.Index);
            parents.Add(choice.Index, new(binding));
        }
        var controllers = actor.Model.Bones.Select((bone, index) => (bone, index))
            .Where(item => item.bone.Flags.HasFlag(PmxBoneFlags.Ik)).ToArray();
        var duplicate = controllers.GroupBy(item => item.bone.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        HasAmbiguousIkNames = duplicate is not null;
        // The same adapter used by playback supplies the current discrete switch state.
        var names = controllers.GroupBy(item => item.bone.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        var evaluated = new MmdIkMotionAdapter(names, actor.Motion).Evaluate((uint)Frame);
        foreach (var item in controllers)
            IkControllers.Add(new(item.index, item.bone.Name, evaluated.GetValueOrDefault(item.index, true)));
        SelectedSubject = Subjects.FirstOrDefault(item => item.Index == selectedBoneIndex) ?? Subjects[0];
        Observe(this, static model => model.RefreshLocalization());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public Func<MotionEditPlan, Task<bool>>? ConfirmOverwriteAsync { get; set; }
    public Guid ObjectId => actor.ObjectId;
    public double Frame { get; }
    public string ModelName { get; }
    public bool HasIkControllers => IkControllers.Count > 0;
    public bool HasAmbiguousIkNames { get; }
    public bool CanEditIk => HasIkControllers && !HasAmbiguousIkNames;
    public bool CanEditOutsideParents => originalPmmControls is null;
    public bool HasTargetModel => SelectedTargetModel?.ObjectId is not null;
    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) Changed(nameof(CanApply)); } }
    public bool CanApply => !IsBusy;
    public IReadOnlyList<EditorRigBoneChoice> Subjects { get; }
    public IReadOnlyList<EditorRigModelChoice> TargetModels { get; }
    public ObservableCollection<EditorRigBoneChoice> TargetBones { get; } = [];
    public ObservableCollection<EditorRigIkItem> IkControllers { get; } = [];
    public string Title => Text("骨骼与 IK 设置");
    public string FrameSummary => Format("第 {0} 帧", Frame);
    public string OutsideParentTitle => Text("外亲绑定");
    public string SubjectLabel => Text("绑定对象");
    public string TargetModelLabel => Text("目标模型");
    public string TargetBoneLabel => Text("目标骨骼");
    public string TargetBonePlaceholder => Text(HasTargetModel ? "Mda.Text.Ux.Rig.SelectTargetBone" : "Mda.Text.Ux.Rig.SelectTargetModelFirst");
    public string KeepWorldLabel => Text("绑定时保持世界姿态");
    public string RemoveLabel => Text("移除外亲");
    public string OutsideParentHelp => CanEditOutsideParents
        ? Text("先选择模型或骨骼，再明确指定另一个模型的目标骨骼。默认保持当前世界姿态。")
        : Text("此工程的外亲由 PMM 时间轨道管理；此处显示当前绑定，不能覆盖原轨道。");
    public string RemoveHelp => Text("移除后恢复原有模型与骨骼变换；不会自动登记补偿关键帧。");
    public string IkTitle => Text("当前帧 IK 开关");
    public string EnableAllIkLabel => Text("Mda.Text.Ux.Rig.EnableAllIk");
    public string DisableAllIkLabel => Text("Mda.Text.Ux.Rig.DisableAllIk");
    public string IkHelp => Text("按模型实际 IK 控制骨逐项开关；应用时登记到当前帧，已有关键帧需确认覆盖。");
    public string NoIkMessage => Text("此模型没有 IK 控制骨。");
    public string AmbiguousIkMessage => Text("此模型包含同名 IK 控制骨；VMD 无法区分，不能逐项登记。");
    public string FooterHelp => Text("应用后写入工程并支持撤销；取消保留原有外亲和 IK 动作。");
    public string CancelLabel => Text("取消");
    public string ApplyLabel => Text("应用");
    public string ValidationMessage => validationError?.Message ?? Text(validationMessage);
    public bool HasValidationMessage => ValidationMessage.Length > 0;

    public EditorRigBoneChoice? SelectedSubject
    {
        get => subject;
        set
        {
            if (value is null || !Subjects.Contains(value) || !Set(ref subject, value)) return;
            LoadParent();
        }
    }
    public EditorRigModelChoice? SelectedTargetModel
    {
        get => targetModel;
        set
        {
            if (value is null || !TargetModels.Contains(value) || !Set(ref targetModel, value)) return;
            RebuildTargetBones();
            if (!loading && subject is not null)
            { parents[subject.Index].ParentObjectId = value.ObjectId; parents[subject.Index].ParentBoneIndex = null; }
            Changed(nameof(HasTargetModel)); Changed(nameof(TargetBonePlaceholder));
        }
    }
    public EditorRigBoneChoice? SelectedTargetBone
    {
        get => targetBone;
        set
        {
            if (value is not null && !TargetBones.Contains(value) || !Set(ref targetBone, value)) return;
            if (!loading && subject is not null) parents[subject.Index].ParentBoneIndex = value?.Index;
        }
    }
    public bool KeepWorld
    {
        get => subject is null || parents[subject.Index].KeepWorld;
        set { if (subject is null || KeepWorld == value) return; parents[subject.Index].KeepWorld = value; Changed(); }
    }

    public void RemoveSelectedOutsideParent()
    {
        if (!CanEditOutsideParents) return;
        SelectedTargetModel = TargetModels[0];
    }

    public void SetAllIkEnabled(bool enabled)
    {
        if (!CanEditIk) return;
        foreach (var controller in IkControllers)
            controller.Enabled = enabled;
    }

    public void SetValidationMessage(string message)
    {
        validationError = null; validationMessage = message;
        Changed(nameof(ValidationMessage)); Changed(nameof(HasValidationMessage));
    }

    public async Task<EditorRigSettingsDraft?> CreateDraftAsync()
    {
        if (IsBusy) return null;
        IsBusy = true;
        try
        {
            RequireCurrent();
            List<EditorOutsideParentChange> changes = [];
            foreach (var pair in parents.Where(pair => pair.Value.Changed))
            {
                if (!CanEditOutsideParents) throw Error("此工程的外亲由 PMM 时间轨道管理；此处显示当前绑定，不能覆盖原轨道。");
                var state = pair.Value;
                if (state.ParentObjectId is not { } parentId) { changes.Add(new(pair.Key, null)); continue; }
                if (state.ParentBoneIndex is not { } parentBone) throw Error("请选择目标模型的有效骨骼。");
                // Delegates source pose, identity, inverses, IK/physics checks and graph validation to the core.
                changes.Add(new(pair.Key, session.PrepareOutsideParent(ObjectId, pair.Key, parentId, parentBone, state.KeepWorld), state.KeepWorld));
            }
            VmdMotion? motion = null;
            if (IkControllers.Any(item => item.Enabled != item.OriginalEnabled))
            {
                if (HasAmbiguousIkNames) throw Error("此模型包含同名 IK 控制骨；VMD 无法区分，不能逐项登记。");
                var knownNames = IkControllers.Select(item => item.BoneName).ToHashSet(StringComparer.Ordinal);
                // Unknown authored switches are preserved when replacing an existing same-frame IK record.
                var unknown = actor.Motion.IkKeyframes.Where(key => key.Frame == (uint)Frame)
                    .SelectMany(key => key.Switches).Where(item => !knownNames.Contains(item.BoneName));
                var switches = unknown.Concat(IkControllers.Select(item => new VmdIkSwitch(item.BoneName, item.Enabled))).ToArray();
                var records = new VmdMotion { IkKeyframes = [new VmdIkKeyframe { Frame = (uint)Frame,
                    Visible = new MmdModelVisibilityTrack(actor.Motion).Evaluate(Frame), Switches = switches }] };
                var staged = new MotionEditor(actor.Motion);
                var plan = staged.PrepareRegister(records);
                if (plan.Conflicts.Count != 0 && (ConfirmOverwriteAsync is null || !await ConfirmOverwriteAsync(plan)))
                { SetValidationMessage("已取消覆盖关键帧"); return null; }
                RequireCurrent();
                staged.Apply(plan, confirmOverwrite: plan.Conflicts.Count != 0);
                motion = staged.ExportMotion();
            }
            RequireCurrent(); SetValidationMessage("");
            return new(ObjectId, Array.AsReadOnly(changes.ToArray()), motion);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException or OverflowException)
        {
            validationError = error; Changed(nameof(ValidationMessage)); Changed(nameof(HasValidationMessage)); return null;
        }
        finally { IsBusy = false; }
    }

    private void RequireCurrent()
    {
        if (!ReferenceEquals(workspace.Session, session) || session.CurrentFrame != Frame
            || !session.Objects.SequenceEqual(originalObjects) || !workspace.Motions.SequenceEqual(originalMotions)
            || !session.OutsideParents.SequenceEqual(originalParents) || !ReferenceEquals(session.PmmControls, originalPmmControls))
            throw new InvalidOperationException(Text("工程或当前帧已改变，请重新打开骨骼与 IK 设置。"));
    }

    private void LoadParent()
    {
        if (subject is null) return;
        loading = true;
        try
        {
            var state = parents[subject.Index];
            targetModel = TargetModels.FirstOrDefault(item => item.ObjectId == state.ParentObjectId) ?? TargetModels[0];
            Changed(nameof(SelectedTargetModel)); RebuildTargetBones();
            targetBone = TargetBones.FirstOrDefault(item => item.Index == state.ParentBoneIndex);
            Changed(nameof(SelectedTargetBone)); Changed(nameof(KeepWorld)); Changed(nameof(HasTargetModel)); Changed(nameof(TargetBonePlaceholder));
        }
        finally { loading = false; }
    }
    private void RebuildTargetBones()
    {
        targetBone = null; TargetBones.Clear();
        if (targetModel?.ObjectId is { } id)
        {
            var target = originalMotions.Single(item => item.ObjectId == id);
            for (int index = 0; index < target.Model.Bones.Count; index++)
                TargetBones.Add(new(index, target.Model.Bones[index].Name));
        }
        Changed(nameof(SelectedTargetBone));
    }
    private void RefreshLocalization()
    {
        foreach (var choice in Subjects) choice.RefreshLocalization();
        foreach (var choice in TargetModels) choice.RefreshLocalization();
        Changed(null);
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(property); return true; }
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
