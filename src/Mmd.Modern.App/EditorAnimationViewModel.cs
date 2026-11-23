using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Mmd.Modern.Animation;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>A complete, unregistered local channel pose at the editor's current global frame.</summary>
public sealed record EditorAnimationPoseDraft(Guid ObjectId, int Frame, int? BoneIndex, int? MorphIndex,
    Vector3 Translation, Quaternion Rotation, float? MorphWeight);

/// <summary>
/// UI-only timeline row projection. It is session view state and never part of authored motion,
/// PMA/VMD content, Undo history or save/export.
/// </summary>
public enum EditorTimelineTrackMode
{
    /// <summary>Optionally focus the active model's selected bone or morph row.</summary>
    FollowSelection,
    /// <summary>Show all authored channels of the active model, grouped by its PMX display categories (default).</summary>
    AllTracks,
    /// <summary>Keep the author's group order, showing channels with registered keys.</summary>
    KeyedTracks
}

/// <summary>
/// Authored bone/morph editing. The project host owns commit, Undo/Redo and asset persistence;
/// no solved IK, append, morph or physics transform is baked into a registered VMD key.
/// </summary>
public sealed partial class EditorAnimationViewModel : INotifyPropertyChanged
{
    private Guid? objectId;
    private PmxModel? model;
    private string actorDisplayName = string.Empty;
    private VmdMotion motion = new();
    private VmdMotion? sourceMotion;
    private SceneMotionDetachment? detachment;
    private VmdMotion? effectiveMotion;
    private VmdMotion EffectiveMotion => effectiveMotion ??= detachment?.Filter(motion) ?? motion;
    // Exact authored keys by bone and frame for one immutable effective motion; a commit, Undo or
    // detachment change produces a new motion instance and therefore a new index.
    private (VmdMotion Source, Dictionary<string, Dictionary<uint, VmdBoneKeyframe>> Keys)? boneKeyIndex;
    internal VmdBoneKeyframe? FindExactBoneKey(string bone, uint frame)
    {
        var source = EffectiveMotion;
        if (boneKeyIndex is not { } index || !ReferenceEquals(index.Source, source))
        {
            var keys = new Dictionary<string, Dictionary<uint, VmdBoneKeyframe>>(StringComparer.Ordinal);
            // Later records overwrite earlier ones, matching the previous LastOrDefault scan.
            foreach (var key in source.BoneKeyframes)
            {
                if (!keys.TryGetValue(key.BoneName, out var frames)) keys.Add(key.BoneName, frames = []);
                frames[key.Frame] = key;
            }
            boneKeyIndex = index = (source, keys);
        }
        return index.Keys.TryGetValue(bone, out var byFrame) && byFrame.TryGetValue(frame, out var exact) ? exact : null;
    }
    public bool IsSelectedUnowned => IsBoneSelected && detachment?.BoneNames.Contains(SelectedBone!.Name) == true
        || IsMorphSelected && detachment?.MorphNames.Contains(model!.Morphs[morphIndex!.Value].Name) == true;
    private CharacterAnimator? boneSampler;
    private MmdMorphMotionAdapter? morphSampler;
    private MotionClipboard? clipboard;
    private int? boneIndex, morphIndex;
    private int currentFrame, moveTargetFrame;
    private long contextRevision;
    private bool isBusy, hasDraft, loadingPose;
    private decimal? positionX, positionY, positionZ, rotationX, rotationY, rotationZ, morphWeight;
    private Quaternion sampledRotation = Quaternion.Identity;
    private bool rotationEdited;
    private readonly Dictionary<(Guid Actor, int Frame), ActorDrafts> actorDrafts = [];
    private bool preservingRegisterDrafts;
    private sealed class ActorDrafts(PmxModel model, VmdMotion? source)
    {
        public PmxModel Model { get; } = model;
        public VmdMotion? Source { get; set; } = source;
        public Dictionary<int, BoneDraft> Bones { get; } = [];
        public Dictionary<int, decimal?> Morphs { get; } = [];
        public bool IsEmpty => Bones.Count == 0 && Morphs.Count == 0;
    }
    private sealed record BoneDraft(decimal? X, decimal? Y, decimal? Z, decimal? Pitch, decimal? Yaw, decimal? Roll,
        Quaternion SampledRotation, bool RotationEdited)
    {
        public bool IsValid => X.HasValue && Y.HasValue && Z.HasValue && Pitch.HasValue && Yaw.HasValue && Roll.HasValue;
        public Vector3 Translation => new((float)(X ?? 0), (float)(Y ?? 0), (float)(Z ?? 0));
        public Quaternion Rotation => RotationEdited
            ? Quaternion.CreateFromYawPitchRoll(ToRadians(Yaw), ToRadians(Pitch), ToRadians(Roll)) : SampledRotation;
    }
    private IReadOnlyList<KeyframeAddress> selectedKeyframes = Array.Empty<KeyframeAddress>();
    private IReadOnlyList<EditorTimelineTrack> tracks = Array.Empty<EditorTimelineTrack>();
    private IReadOnlyList<EditorTimelineTrack> visibleTracks = Array.Empty<EditorTimelineTrack>();
    private EditorTimelineTrackMode timelineTrackMode = EditorTimelineTrackMode.AllTracks;
    private IReadOnlyList<EditorTimelineGroup> timelineGroups = Array.Empty<EditorTimelineGroup>();
    private IReadOnlyList<EditorTimelineGroup> keyedTimelineGroups = [];
    private IReadOnlyList<EditorTimelineTrack> allGroupTracks = [];
    private IReadOnlyList<EditorTimelineTrack> keyedGroupTracks = [];
    private Dictionary<(KeyframeTrackKind Kind, string Name), EditorTimelineTrack> authoredTrackMap = new();
    private Dictionary<(KeyframeTrackKind Kind, string Name), EditorTimelineTrack> visibleTrackMap = new();
    private bool animationContextActive = true;
    private int timelineFocusRow = -1;
    private string status = "选择模型，再选择骨骼或表情";
    private object?[] statusArguments = [];
    private Exception? statusError;

    public EditorAnimationViewModel()
    {
        RegisterPoseCommand = new AnimationCommand(() => RegisterAsync(bonesOnly: true), () => CanRegisterBones);
        RegisterKeyframeCommand = new AnimationCommand(RegisterKeyframeAsync, () => CanRegister);
        DeleteKeyframesCommand = new AnimationCommand(DeleteKeyframesAsync, () => CanModifySelection);
        CopyKeyframesCommand = new AnimationCommand(() => { CopyKeyframes(); return Task.CompletedTask; }, () => CanEditSelection);
        PasteKeyframesCommand = new AnimationCommand(PasteKeyframesAsync, () => CanPaste);
        MoveKeyframesCommand = new AnimationCommand(MoveKeyframesAsync, () => CanModifySelection);
        ResetPoseCommand = new AnimationCommand(() => { ResetPose(); return Task.CompletedTask; }, () => HasEditableSelection && !IsBusy);
        CopyPoseCommand = new AnimationCommand(() => { CopyPose(); return Task.CompletedTask; }, () => CanCopyPose);
        PasteMirroredPoseCommand = new AnimationCommand(() => { PasteMirroredPose(); return Task.CompletedTask; }, () => CanPastePose);
        SelectTimelineRangeCommand = new AnimationCommand(() => { SelectTimelineRange(); return Task.CompletedTask; }, () => CanSelectTimelineRange);
        ClearTimelineSelectionCommand = new AnimationCommand(() => { ClearTimelineSelection(); return Task.CompletedTask; }, () => CanSelectTimelineRange);
        Observe(this, static model => model.RefreshLocalization());
    }

    /// <summary>Must commit atomically or throw. A failed callback leaves this editor's motion unchanged.</summary>
    public Action<Guid, VmdMotion>? CommitMotion { get; set; }
    public Func<MotionEditPlan, Task<bool>>? ConfirmOverwriteAsync { get; set; }
    /// <summary>Host validation includes raw native numeric text that has no parsed Value.</summary>
    public Func<bool>? ValidateInput { get; set; }
    public Func<bool>? CanMutateProject { get; set; }
    public void RefreshInputValidation() => RefreshCommands();
    public Action<string>? ReportStatus { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? PoseDraftChanged;

    public ICommand RegisterKeyframeCommand { get; }
    public ICommand DeleteKeyframesCommand { get; }
    public ICommand CopyKeyframesCommand { get; }
    public ICommand PasteKeyframesCommand { get; }
    public ICommand MoveKeyframesCommand { get; }
    public ICommand ResetPoseCommand { get; }
    public Guid? ObjectId => objectId;
    public int? SelectedBoneIndex => boneIndex;
    public int? SelectedMorphIndex => morphIndex;
    public bool HasActor => objectId.HasValue && model is not null;
    public bool IsBoneSelected => HasActor && boneIndex.HasValue;
    public bool IsMorphSelected => HasActor && morphIndex.HasValue;
    public bool HasEditableSelection => IsBoneSelected || IsMorphSelected;
    public bool IsEmptySelection => !HasEditableSelection;
    public bool CanTranslate => !IsSelectedUnowned && IsBoneSelected && SelectedBone!.Flags.HasFlag(PmxBoneFlags.AllowTranslate) && !IsBusy;
    public bool CanRotate => !IsSelectedUnowned && IsBoneSelected && SelectedBone!.Flags.HasFlag(PmxBoneFlags.AllowRotate) && !IsBusy;
    public bool CanEditMorph => !IsSelectedUnowned && IsMorphSelected && !IsBusy;
    /// <summary>Bone/morph registration belongs to the authored animation context only.
    /// Camera, world and main light have their own registration entries and must not receive a
    /// retained model channel's key while their context is active.</summary>
    public bool CanRegister => !IsSelectedUnowned && animationContextActive && HasEditableSelection && !IsBusy && CanMutateProject?.Invoke() != false && DraftIsValid && (!IsBoneSelected || !HasInvalidBoneRegistration) && ValidateInput?.Invoke() != false;
    public bool CanEditSelection => HasActor && !IsBusy && CanMutateProject?.Invoke() != false && SelectedKeyframes.Count != 0;
    /// <summary>
    /// Delete/Move act on the complete selection, so they are withheld while any selected keyframe
    /// is outside the visible row projection. Switch to All tracks instead of silently dropping or
    /// mutating hidden addresses; the hidden count and its tooltip expose that state.
    /// </summary>
    public bool CanModifySelection => CanEditSelection && HiddenSelectedKeyframeCount == 0;
    public bool CanPaste => HasActor && !IsBusy && CanMutateProject?.Invoke() != false && clipboard is { IsEmpty: false };
    public bool HasTracks => Tracks.Count != 0;
    /// <summary>Whether the selected bone or morph has unregistered edits, including incomplete numeric input.</summary>
    public bool SelectedHasDraft => hasDraft;
    public bool HasDraft => SelectedHasDraft;
    /// <summary>Includes other bones, morphs and models at this same global frame.</summary>
    public bool HasAnyDraft => actorDrafts.Values.Any(draft => !draft.IsEmpty);
    public bool IsBusy => isBusy;
    public string Status => Format(status, statusArguments) + statusError?.Message;
    public string SelectionName => IsBoneSelected ? SelectedBone!.Name : IsMorphSelected ? model!.Morphs[morphIndex!.Value].Name : Text("未选择骨骼或表情");
    public string SelectionLabel => HasActor ? $"{ActorDisplayName} › {SelectionName}" : SelectionName;
    public string ModelContextLabel => HasActor ? $"{ActorDisplayName} · {FrameSummary}" : FrameSummary;
    public string FrameSummary => Format("第 {0} 帧", CurrentFrame)
        + (!playing && objectId is { } id && actorDrafts.ContainsKey((id, CurrentFrame)) ? Text(" · 未登记") : "");
    public string KeyframeSummary => Format("{0} 个关键帧", motion.BoneKeyframes.Count + motion.MorphKeyframes.Count + motion.IkKeyframes.Count);
    /// <summary>Complete authored rows. Source of truth for validity, edits, save and export.</summary>
    public IReadOnlyList<EditorTimelineTrack> Tracks => tracks;
    /// <summary>Session-only displayed projection of <see cref="Tracks"/>; never persisted or committed.</summary>
    public IReadOnlyList<EditorTimelineTrack> VisibleTracks => visibleTracks;
    public IReadOnlyList<EditorTimelineGroup> TimelineGroups => IsTimelineAllTracks ? timelineGroups
        : IsTimelineKeyedTracks ? keyedTimelineGroups : Array.Empty<EditorTimelineGroup>();
    public bool IsTimelineKeyedTracks => timelineTrackMode == EditorTimelineTrackMode.KeyedTracks;
    public KeyframeAddress? TimelineFocusedChannel => IsBoneSelected ? KeyframeAddress.Bone(SelectedBone!.Name, 0)
        : IsMorphSelected ? KeyframeAddress.Morph(model!.Morphs[morphIndex!.Value].Name, 0) : null;
    public bool HasVisibleTracks => visibleTracks.Count != 0;
    public EditorTimelineTrackMode TimelineTrackMode => timelineTrackMode;
    public bool IsTimelineFollowSelection => timelineTrackMode == EditorTimelineTrackMode.FollowSelection;
    public bool IsTimelineAllTracks => timelineTrackMode == EditorTimelineTrackMode.AllTracks;
    /// <summary>Focused bone or morph row inside the visible projection; -1 when it has no row.</summary>
    public int TimelineFocusRow => timelineFocusRow;
    /// <summary>Selected authored keyframes that the current row projection does not show. Retained, not deleted.</summary>
    public int HiddenSelectedKeyframeCount
    {
        get
        {
            if (selectedKeyframes.Count == 0) return 0;
            var visible = VisibleTrackMap();
            int hidden = 0;
            foreach (var address in selectedKeyframes) if (!IsVisibleAddress(visible, address)) hidden++;
            return hidden;
        }
    }
    /// <summary>Selected bone keys hidden by the projection; interpolation must not edit them unseen.</summary>
    public bool HasHiddenSelectedBoneKeyframes
    {
        get
        {
            var visible = VisibleTrackMap();
            foreach (var address in selectedKeyframes)
                if (address.Kind == KeyframeTrackKind.Bone && !IsVisibleAddress(visible, address)) return true;
            return false;
        }
    }
    public bool HasHiddenSelectedKeyframes => HiddenSelectedKeyframeCount != 0;
    public string HiddenSelectionSummary => Format("Mda.Text.Context.Timeline.HiddenSelection", HiddenSelectedKeyframeCount);
    /// <summary>True only when the projection has no real row; the host shows the quiet context prompt.</summary>
    public bool ShowTimelineEmptyHint => visibleTracks.Count == 0;
    public string TimelineEmptyHint
    {
        get
        {
            if (!HasActor) return Text("Mda.Text.Context.Timeline.EmptyNoModel");
            if (!IsTimelineFollowSelection) return Text("Mda.Text.Context.Timeline.EmptyNoKeys");
            if (!animationContextActive) return Text("Mda.Text.Context.Timeline.EmptySceneContext");
            return Text("Mda.Text.Context.Timeline.EmptyNoChannel");
        }
    }
    public IReadOnlyList<KeyframeAddress> SelectedKeyframes
    {
        get => selectedKeyframes;
        set
        {
            var next = (value ?? Array.Empty<KeyframeAddress>()).Where(address => IsVisibleAddress(authoredTrackMap, address)).Distinct().ToArray();
            if (selectedKeyframes.SequenceEqual(next)) return;
            selectedKeyframes = Array.AsReadOnly(next);
            Changed(); Changed(nameof(TimelineSelectionSummary)); RefreshCommands(); NotifyHiddenSelection();
        }
    }

    /// <summary>Switch the UI-only row projection. Never creates a project edit, Undo entry or dirty state.</summary>
    public void SetTimelineTrackMode(EditorTimelineTrackMode mode)
    {
        if (!Enum.IsDefined(mode) || timelineTrackMode == mode) return;
        timelineTrackMode = mode;
        Changed(nameof(TimelineTrackMode)); Changed(nameof(IsTimelineFollowSelection)); Changed(nameof(IsTimelineAllTracks)); Changed(nameof(IsTimelineKeyedTracks)); Changed(nameof(TimelineGroups));
        RebuildVisibleTracks();
    }

    /// <summary>
    /// World/camera/main-light selection is not an authored model animation channel. Follow selection
    /// therefore shows its quiet context prompt instead of presenting retained model rows as scene keys;
    /// the retained actor, drafts and selected keyframes stay unchanged.
    /// </summary>
    public void SetAnimationContextActive(bool active)
    {
        if (animationContextActive == active) return;
        animationContextActive = active;
        RebuildVisibleTracks();
    }

    public int CurrentFrame
    {
        get => currentFrame;
        set
        {
            value = Math.Max(0, value);
            if (currentFrame == value) return;
            currentFrame = value; contextRevision++;
            Changed(); Changed(nameof(HasAnyDraft)); Changed(nameof(HasInvalidDrafts)); Changed(nameof(FrameSummary)); Changed(nameof(ModelContextLabel)); LoadPose();
            PoseDraftChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public int MoveTargetFrame { get => moveTargetFrame; set { moveTargetFrame = Math.Max(0, value); Changed(); } }
    public decimal? PositionX { get => positionX; set => SetDraft(ref positionX, value); }
    public decimal? PositionY { get => positionY; set => SetDraft(ref positionY, value); }
    public decimal? PositionZ { get => positionZ; set => SetDraft(ref positionZ, value); }
    public decimal? RotationX { get => rotationX; set => SetDraft(ref rotationX, value, true); }
    public decimal? RotationY { get => rotationY; set => SetDraft(ref rotationY, value, true); }
    public decimal? RotationZ { get => rotationZ; set => SetDraft(ref rotationZ, value, true); }
    public decimal? MorphWeight { get => morphWeight; set => SetDraft(ref morphWeight, value); }
    public Vector3 DraftTranslation => new((float)(PositionX ?? 0), (float)(PositionY ?? 0), (float)(PositionZ ?? 0));
    public Quaternion DraftRotation => rotationEdited
        ? Quaternion.CreateFromYawPitchRoll(ToRadians(RotationY), ToRadians(RotationX), ToRadians(RotationZ)) : sampledRotation;
    private PmxBone? SelectedBone => IsBoneSelected ? model!.Bones[boneIndex!.Value] : null;
    private string ActorDisplayName => string.IsNullOrWhiteSpace(actorDisplayName) ? Text("未命名模型") : actorDisplayName;
    private bool DraftIsValid => IsBoneSelected ? PositionX.HasValue && PositionY.HasValue && PositionZ.HasValue
        && RotationX.HasValue && RotationY.HasValue && RotationZ.HasValue : IsMorphSelected && MorphWeight.HasValue;

    /// <summary>Refresh from the current project after import, commit, Undo or Redo. Null motion is an editable empty motion.</summary>
    public void SetActor(Guid? actorId, PmxModel? actorModel, VmdMotion? actorMotion, string? actorName = null, SceneMotionDetachment? actorDetachment = null)
    {
        var sameActor = objectId == actorId && ReferenceEquals(model, actorModel);
        var nextDisplayName = actorName ?? actorModel?.Name ?? string.Empty;
        if (sameActor && ReferenceEquals(sourceMotion, actorMotion) && ReferenceEquals(detachment, actorDetachment))
        {
            if (StringComparer.Ordinal.Equals(actorDisplayName, nextDisplayName)) return;
            actorDisplayName = nextDisplayName;
            Changed(nameof(SelectionLabel)); Changed(nameof(ModelContextLabel));
            return;
        }
        if (actorId is { } nextId)
            foreach (var (key, pending) in actorDrafts.Where(pair => pair.Key.Actor == nextId).ToArray())
            {
                if (!ReferenceEquals(pending.Model, actorModel)) actorDrafts.Remove(key);
                else pending.Source = actorMotion;
            }
        objectId = actorId; model = actorModel; actorDisplayName = nextDisplayName;
        sourceMotion = actorMotion; detachment = actorDetachment;
        motion = new MotionEditor(actorMotion ?? new VmdMotion { ModelName = actorModel?.Name ?? string.Empty }).ExportMotion();
        contextRevision++;
        if (!sameActor) { selectedBoneIndices = []; boneIndex = null; morphIndex = null; selectedKeyframes = Array.Empty<KeyframeAddress>(); }
        if (boneIndex is { } b && (model is null || b >= model.Bones.Count)) boneIndex = null;
        if (morphIndex is { } m && (model is null || m >= model.Morphs.Count)) morphIndex = null;
        var existing = ExistingAddresses().ToHashSet();
        selectedKeyframes = Array.AsReadOnly(selectedKeyframes.Where(existing.Contains).ToArray());
        RebuildTracks();
        if (!sameActor) ResetTimelineRange();
        RebuildSampler(); LoadPose(); NotifyContext();
        if (!HasActor) SetStatus("选择模型，再选择骨骼或表情");
    }

    /// <summary>Explicitly discard all unregistered channels and notify the document owner.</summary>
    public void InvalidateDrafts()
    {
        if (preservingRegisterDrafts || actorDrafts.Count == 0) return;
        actorDrafts.Clear(); contextRevision++;
        Changed(nameof(HasAnyDraft)); Changed(nameof(HasInvalidDrafts)); NotifyDraftContentEdited(); LoadPose(); PoseDraftChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A detached snapshot of complete drafts across every model. Incomplete inputs remain
    /// pending for Save/New guards but cannot be sent to an animation evaluator.</summary>
    public IReadOnlyList<EditorAnimationPoseDraft> SnapshotDrafts()
    {
        var result = new List<EditorAnimationPoseDraft>();
        if (playing) return result;
        foreach (var ((id, _), draft) in actorDrafts.Where(pair => pair.Key.Frame == CurrentFrame).OrderBy(pair => pair.Key.Actor))
        {
            foreach (var (index, bone) in draft.Bones.OrderBy(pair => pair.Key))
                if (bone.IsValid) result.Add(new(id, CurrentFrame, index, null, bone.Translation, bone.Rotation, null));
            foreach (var (index, weight) in draft.Morphs.OrderBy(pair => pair.Key))
                if (weight.HasValue) result.Add(new(id, CurrentFrame, null, index, Vector3.Zero, Quaternion.Identity, (float)weight.Value));
        }
        return result.AsReadOnly();
    }

    public void SelectBone(int? index)
    {
        var validIndex = index is { } value && HasActor && value >= 0 && value < model!.Bones.Count ? index : null;
        if (boneIndex == validIndex && morphIndex is null) return;
        selectedBoneIndices = validIndex is { } selected ? [selected] : [];
        boneIndex = validIndex;
        morphIndex = null; contextRevision++;
        RebuildSampler(); RebuildVisibleTracks(); LoadPose(); NotifyContext();
        if (IsBoneSelected && boneSampler is not null) SetStatus("调整位置或旋转，然后登记当前帧");
    }

    public void SelectMorph(int? index)
    {
        var validIndex = index is { } value && HasActor && value >= 0 && value < model!.Morphs.Count ? index : null;
        if (morphIndex == validIndex && boneIndex is null) return;
        selectedBoneIndices = [];
        morphIndex = validIndex;
        boneIndex = null; contextRevision++;
        RebuildSampler(); RebuildVisibleTracks(); LoadPose(); NotifyContext();
        if (IsMorphSelected && morphSampler is not null) SetStatus("调整表情权重，然后登记当前帧");
    }

    /// <summary>Call for a timeline key selection; exact PMX names select the first matching model entry.</summary>
    public void SelectTimelineKeys(IReadOnlyList<KeyframeAddress> selection)
    {
        SelectedKeyframes = selection;
        if (SelectedKeyframes.Count == 0 || model is null) return;
        var address = SelectedKeyframes[^1];
        if (address.Kind == KeyframeTrackKind.Bone)
        {
            int index = FindIndex(model.Bones.Select(b => b.Name), address.Name);
            if (index >= 0) SelectBone(index);
        }
        else if (address.Kind == KeyframeTrackKind.Morph)
        {
            int index = FindIndex(model.Morphs.Select(m => m.Name), address.Name);
            if (index >= 0) SelectMorph(index);
        }
        if (address.Frame <= int.MaxValue) CurrentFrame = (int)address.Frame;
    }

    public Task RegisterKeyframeAsync() => RegisterAsync(bonesOnly: false);

    private async Task RegisterAsync(bool bonesOnly)
    {
        if (!animationContextActive)
        { SetStatus("Mda.Text.Context.Timeline.RegisterBlocked"); return; }
        if (!(bonesOnly ? CanRegisterBones : CanRegister)) return;
        try
        {
            var frame = (uint)CurrentFrame;
            VmdMotion records;
            KeyframeAddress[] addresses;
            if (bonesOnly || IsBoneSelected)
            {
                var pending = actorDrafts.GetValueOrDefault((objectId!.Value, CurrentFrame));
                var indices = pending?.Bones.Count > 0 ? pending.Bones.Keys.Order().ToArray() : SelectedBoneIndices.ToArray();
                var keys = new List<VmdBoneKeyframe>(indices.Length);
                foreach (int index in indices)
                {
                    var bone = model!.Bones[index];
                    if (detachment?.BoneNames.Contains(bone.Name) == true) return;
                    var pose = LocalBonePose(index);
                    var template = FindExactBoneKey(bone.Name, frame);
                    keys.Add(CreateDraftKey(bone, frame, pose.Translation, pose.Rotation, template));
                }
                records = new() { BoneKeyframes = keys };
                addresses = keys.Select(key => KeyframeAddress.Bone(key.BoneName, frame)).ToArray();
            }
            else
            {
                var name = model!.Morphs[morphIndex!.Value].Name;
                records = new() { MorphKeyframes = [new() { MorphName = name, Frame = frame, Weight = (float)MorphWeight!.Value }] };
                addresses = [KeyframeAddress.Morph(name, frame)];
            }
            await ApplyAsync(editor => editor.PrepareRegister(records), "已登记关键帧", addresses, registerBones: bonesOnly || IsBoneSelected);
        }
        catch (Exception ex) { SetFailure("登记关键帧失败：", ex); }
    }

    public async Task DeleteKeyframesAsync()
    {
        if (!CanEditSelection) return;
        if (HiddenSelectedKeyframeCount != 0)
        { SetStatus("Mda.Text.Context.Timeline.DeleteBlocked", HiddenSelectedKeyframeCount); return; }
        var selection = SelectedKeyframes.ToArray();
        await ApplyAsync(editor => editor.PrepareDelete(selection), "已删除选中关键帧", []);
    }

    public void CopyKeyframes()
    {
        if (!CanEditSelection) return;
        try { clipboard = new MotionEditor(motion).CopySelection(SelectedKeyframes); SetStatus("已复制 {0} 个关键帧位置", SelectedKeyframes.Count); RefreshCommands(); }
        catch (Exception ex) { SetFailure("复制失败：", ex); }
    }

    public async Task PasteKeyframesAsync()
    {
        if (!CanPaste) return;
        try
        {
        var content = clipboard!; var target = (uint)CurrentFrame;
        long delta = (long)target - content.SourceStartFrame!.Value;
        var selection = content.BoneKeyframes.Select(k => KeyframeAddress.Bone(k.BoneName, checked((uint)(k.Frame + delta))))
            .Concat(content.MorphKeyframes.Select(k => KeyframeAddress.Morph(k.MorphName, checked((uint)(k.Frame + delta)))))
            .Concat(content.IkKeyframes.Select(k => KeyframeAddress.Ik(checked((uint)(k.Frame + delta))))).Distinct().ToArray();
        await ApplyAsync(editor => editor.PreparePaste(content, target), "已粘贴关键帧", selection);
        }
        catch (Exception ex) { SetFailure("粘贴关键帧失败：", ex); }
    }

    public async Task MoveKeyframesAsync()
    {
        if (!CanEditSelection) return;
        if (HiddenSelectedKeyframeCount != 0)
        { SetStatus("Mda.Text.Context.Timeline.MoveBlocked", HiddenSelectedKeyframeCount); return; }
        var selection = SelectedKeyframes.ToArray();
        long delta = MoveTargetFrame - (long)selection.Min(k => k.Frame);
        // The backend validates underflow/overflow before preparing any mutation.
        await ApplyAsync(editor => editor.PrepareMove(selection, delta), "已移动关键帧",
            selection.Select(k => k with { Frame = (uint)Math.Clamp((long)k.Frame + delta, 0, uint.MaxValue) }).ToArray());
    }

    public void ResetPose()
    {
        if (!HasEditableSelection || IsBusy) return;
        if (IsBoneSelected)
        {
            if (CanTranslate) { PositionX = 0; PositionY = 0; PositionZ = 0; }
            if (CanRotate) { RotationX = 0; RotationY = 0; RotationZ = 0; }
        }
        else MorphWeight = 0;
    }

    private async Task ApplyAsync(Func<MotionEditor, MotionEditPlan> prepare, string success, IReadOnlyList<KeyframeAddress> selection,
        bool? registerBones = null)
    {
        if (!HasActor || IsBusy || CanMutateProject?.Invoke() == false || ValidateInput?.Invoke() == false) return;
        isBusy = true; Changed(nameof(IsBusy)); RefreshCommands();
        var revision = contextRevision; var actor = objectId!.Value;
        try
        {
            if (CommitMotion is null) { SetStatus("关键帧尚未连接工程保存，请先完成工程接线"); return; }
            // Stage on an isolated editor: confirmation/commit failure cannot advance local history or motion.
            var staged = new MotionEditor(motion);
            var plan = prepare(staged);
            if (plan.Conflicts.Count != 0)
            {
                if (ConfirmOverwriteAsync is null || !await ConfirmOverwriteAsync(plan)) { SetStatus("已取消覆盖关键帧"); return; }
            }
            if (revision != contextRevision || actor != objectId) { SetStatus("编辑对象已变化，请重新操作"); return; }
            if (CanMutateProject?.Invoke() == false || ValidateInput?.Invoke() == false) return;
            staged.Apply(plan, confirmOverwrite: plan.Conflicts.Count != 0);
            if (staged.Revision == 0) return;
            var candidate = staged.ExportMotion();
            preservingRegisterDrafts = registerBones.HasValue;
            RegistrationHistory = registerBones is { } boneRegistration ? CreateRegistrationHistory(actor, boneRegistration) : null;
            try
            {
                CommitMotion(actor, candidate);
                if (revision == contextRevision) SetActor(actor, model, candidate, actorDisplayName);
                RegistrationHistory?.Invoke(false);
            }
            finally { preservingRegisterDrafts = false; RegistrationHistory = null; }
            if (objectId == actor) SelectedKeyframes = selection;
            SetStatus(success);
        }
        catch (Exception ex) { SetFailure("关键帧编辑失败：", ex); }
        finally { isBusy = false; Changed(nameof(IsBusy)); RefreshCommands(); }
    }

    private void RebuildSampler()
    {
        boneSampler = null; morphSampler = null;
        try
        {
            if (IsBoneSelected)
            {
                // A vertex-free one-bone rig samples only authored local VMD curves through the existing evaluator.
                // Parent/IK/morph/physics transforms belong to the runtime viewport, never to these editable values.
                var name = SelectedBone!.Name;
                var clip = MmdBoneMotionAdapter.Convert(new() { BoneKeyframes = EffectiveMotion.BoneKeyframes.Where(k => k.BoneName == name).ToArray() });
                boneSampler = new(new CharacterRig([new RigBone(name, Vector3.Zero, -1)], [], []), clip);
            }
            else if (IsMorphSelected)
            {
                var name = model!.Morphs[morphIndex!.Value].Name;
                morphSampler = new(new Dictionary<string, int>(StringComparer.Ordinal) { [name] = morphIndex.Value }, EffectiveMotion);
            }
        }
        catch (Exception ex) { SetFailure("动作采样失败：", ex); }
    }

    private void LoadPose()
    {
        using var measure = InteractionDiagnostics.Measure("ui.load-pose", EditorInteractionTrace.CurrentInput);
        loadingPose = true;
        try
        {
            if (IsBoneSelected && boneSampler is not null)
            {
                var local = boneSampler.Sample(CurrentFrame).BoneWorldMatrices[0];
                var exact = FindExactBoneKey(SelectedBone!.Name, (uint)CurrentFrame);
                sampledRotation = exact?.Rotation ?? Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(local));
                var degrees = ToDegrees(Quaternion.Normalize(sampledRotation));
                positionX = ToDecimal(local.Translation.X); positionY = ToDecimal(local.Translation.Y); positionZ = ToDecimal(local.Translation.Z);
                rotationX = ToDecimal(degrees.X); rotationY = ToDecimal(degrees.Y); rotationZ = ToDecimal(degrees.Z);
                morphWeight = null;
            }
            else if (IsMorphSelected && morphSampler is not null)
            {
                var weights = morphSampler.Evaluate(CurrentFrame);
                morphWeight = ToDecimal(weights.GetValueOrDefault(morphIndex!.Value)); ClearBoneValues();
            }
            else { ClearBoneValues(); morphWeight = null; }
            hasDraft = false; rotationEdited = false;
            if (!playing && objectId is { } id && actorDrafts.TryGetValue((id, CurrentFrame), out var pending))
            {
                if (boneIndex is { } bone && pending.Bones.TryGetValue(bone, out var draft))
                {
                    positionX = draft.X; positionY = draft.Y; positionZ = draft.Z;
                    rotationX = draft.Pitch; rotationY = draft.Yaw; rotationZ = draft.Roll;
                    sampledRotation = draft.SampledRotation; rotationEdited = draft.RotationEdited; hasDraft = true;
                }
                else if (morphIndex is { } morph && pending.Morphs.TryGetValue(morph, out var weight))
                { morphWeight = weight; hasDraft = true; }
            }
        }
        catch (Exception ex) { ClearBoneValues(); morphWeight = null; hasDraft = false; rotationEdited = false; SetFailure("动作采样失败：", ex); }
        finally { loadingPose = false; }
        foreach (var name in new[] { nameof(PositionX), nameof(PositionY), nameof(PositionZ), nameof(RotationX), nameof(RotationY), nameof(RotationZ), nameof(MorphWeight), nameof(HasDraft), nameof(SelectedHasDraft), nameof(HasAnyDraft), nameof(FrameSummary), nameof(ModelContextLabel) }) Changed(name);
        RefreshCommands();
    }

    private void RebuildTracks()
    {
        effectiveMotion = null;
        boneKeyIndex = null;
        var result = new List<EditorTimelineTrack>();
        result.AddRange(EffectiveMotion.BoneKeyframes.GroupBy(k => k.BoneName, StringComparer.Ordinal).Select(g => new EditorTimelineTrack(KeyframeTrackKind.Bone, g.Key, g.Select(k => k.Frame))));
        result.AddRange(EffectiveMotion.MorphKeyframes.GroupBy(k => k.MorphName, StringComparer.Ordinal).Select(g => new EditorTimelineTrack(KeyframeTrackKind.Morph, g.Key, g.Select(k => k.Frame))));
        if (motion.IkKeyframes.Count != 0) result.Add(new(KeyframeTrackKind.Ik, string.Empty, motion.IkKeyframes.Select(k => k.Frame)));
        if (IsBoneSelected && !result.Any(t => t.Kind == KeyframeTrackKind.Bone && t.Name == SelectedBone!.Name)) result.Add(new(KeyframeTrackKind.Bone, SelectedBone!.Name, []));
        if (IsMorphSelected && !result.Any(t => t.Kind == KeyframeTrackKind.Morph && t.Name == model!.Morphs[morphIndex!.Value].Name)) result.Add(new(KeyframeTrackKind.Morph, model!.Morphs[morphIndex!.Value].Name, []));
        tracks = result.AsReadOnly();
        authoredTrackMap = tracks.ToDictionary(track => (track.Kind, track.Name));
        RebuildTimelineGroups();
        // Publish the authored source before the projection so the timeline validates and cancels
        // gestures against real tracks, never against rows that are merely hidden.
        Changed(nameof(Tracks)); Changed(nameof(HasTracks)); Changed(nameof(KeyframeSummary));
        RebuildVisibleTracks();
    }

    private void RebuildTimelineGroups()
    {
        timelineGroups = EditorTimelineGrouping.Build(model, tracks, objectId?.ToString("D") ?? string.Empty,
            Text("Mda.Text.Timeline.Unclassified"), Text("Mda.Text.Timeline.IkDisplay"));
        keyedTimelineGroups = Array.AsReadOnly(timelineGroups.Select(group => group.WithKeys()).OfType<EditorTimelineGroup>().ToArray());
        allGroupTracks = Array.AsReadOnly(timelineGroups.SelectMany(group => group.Tracks)
            .DistinctBy(track => (track.Kind, track.Name)).ToArray());
        keyedGroupTracks = Array.AsReadOnly(allGroupTracks.Where(track => track.Frames.Count != 0).ToArray());
        Changed(nameof(TimelineGroups));
    }

    /// <summary>
    /// Recomputes only the displayed rows. The complete authored <see cref="Tracks"/> list, motion,
    /// selection, drafts and Undo history are untouched; hidden selected keys stay selected.
    /// </summary>
    private void RebuildVisibleTracks()
    {
        IReadOnlyList<EditorTimelineTrack> visible = [];
        if (HasActor)
        {
            if (IsTimelineAllTracks) visible = allGroupTracks;
            else if (IsTimelineKeyedTracks) visible = keyedGroupTracks;
            else if (animationContextActive && TimelineFocusedChannel is { } channel)
            {
                visible = [authoredTrackMap.TryGetValue((channel.Kind, channel.Name), out var track)
                    ? track : new EditorTimelineTrack(channel.Kind, channel.Name, [])];
            }
        }
        if (!ReferenceEquals(visibleTracks, visible))
        {
            visibleTracks = visible;
            visibleTrackMap = visible.ToDictionary(track => (track.Kind, track.Name));
        }
        timelineFocusRow = -1;
        for (int index = 0; index < visibleTracks.Count && timelineFocusRow < 0; index++)
        {
            var track = visibleTracks[index];
            if (IsBoneSelected && track.Kind == KeyframeTrackKind.Bone
                && StringComparer.Ordinal.Equals(track.Name, SelectedBone!.Name)) timelineFocusRow = index;
            else if (IsMorphSelected && track.Kind == KeyframeTrackKind.Morph
                && StringComparer.Ordinal.Equals(track.Name, model!.Morphs[morphIndex!.Value].Name)) timelineFocusRow = index;
        }
        foreach (var name in new[] { nameof(VisibleTracks), nameof(HasVisibleTracks), nameof(TimelineFocusRow), nameof(TimelineFocusedChannel),
            nameof(ShowTimelineEmptyHint), nameof(TimelineEmptyHint), nameof(HiddenSelectedKeyframeCount),
            nameof(HasHiddenSelectedKeyframes), nameof(HasHiddenSelectedBoneKeyframes), nameof(HiddenSelectionSummary),
            nameof(CanModifySelection), nameof(CanRegister) })
            Changed(name);
        RefreshCommands();
    }

    private Dictionary<(KeyframeTrackKind Kind, string Name), EditorTimelineTrack> VisibleTrackMap()
        => visibleTrackMap;

    private static bool IsVisibleAddress(
        Dictionary<(KeyframeTrackKind Kind, string Name), EditorTimelineTrack> visible, KeyframeAddress address)
    {
        if (!visible.TryGetValue((address.Kind, address.Name), out var track)) return false;
        int index = track.LowerBound(address.Frame);
        return index < track.Frames.Count && track.Frames[index] == address.Frame;
    }

    private void NotifyHiddenSelection()
    {
        foreach (var name in new[] { nameof(HiddenSelectedKeyframeCount), nameof(HasHiddenSelectedKeyframes),
            nameof(HasHiddenSelectedBoneKeyframes), nameof(HiddenSelectionSummary), nameof(CanModifySelection) })
            Changed(name);
    }

    private IEnumerable<KeyframeAddress> ExistingAddresses() => motion.BoneKeyframes.Select(k => KeyframeAddress.Bone(k.BoneName, k.Frame))
        .Concat(motion.MorphKeyframes.Select(k => KeyframeAddress.Morph(k.MorphName, k.Frame)))
        .Concat(motion.IkKeyframes.Select(k => KeyframeAddress.Ik(k.Frame)));
    private void ClearBoneValues() { positionX = positionY = positionZ = rotationX = rotationY = rotationZ = null; sampledRotation = Quaternion.Identity; }
    private void NotifyContext()
    {
        foreach (var name in new[] { nameof(ObjectId), nameof(HasActor), nameof(IsBoneSelected), nameof(IsMorphSelected), nameof(IsEmptySelection), nameof(HasEditableSelection), nameof(SelectionName), nameof(SelectionLabel), nameof(ModelContextLabel), nameof(SelectedBoneIndex), nameof(SelectedMorphIndex), nameof(SelectedKeyframes), nameof(TimelineSelectionSummary) }) Changed(name);
        Changed(nameof(SelectedBoneIndices)); NotifyDraftAddresses(); RefreshCommands();
    }
    private void SetDraft(ref decimal? field, decimal? value, bool rotation = false, [CallerMemberName] string? property = null)
    {
        if (field == value || IsBusy) return;
        field = value; Changed(property);
        if (!loadingPose && HasEditableSelection)
        {
            hasDraft = true; rotationEdited |= rotation;
            var id = objectId!.Value;
            if (!actorDrafts.TryGetValue((id, CurrentFrame), out var pending)) actorDrafts.Add((id, CurrentFrame), pending = new(model!, sourceMotion));
            if (boneIndex is { } bone) pending.Bones[bone] = new(positionX, positionY, positionZ, rotationX, rotationY, rotationZ, sampledRotation, rotationEdited);
            else if (morphIndex is { } morph) pending.Morphs[morph] = morphWeight;
            Changed(nameof(HasDraft)); Changed(nameof(SelectedHasDraft)); Changed(nameof(HasAnyDraft)); Changed(nameof(HasInvalidDrafts)); Changed(nameof(FrameSummary)); Changed(nameof(ModelContextLabel));
            NotifyDraftContentEdited(); RefreshCommands(); PoseDraftChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal static VmdBoneKeyframe CreateDraftKey(PmxBone bone, uint frame, Vector3 translation, Quaternion rotation, VmdBoneKeyframe? template)
    {
        var basis = BoneEditingBasis.Create(bone, Matrix4x4.Identity, Matrix4x4.Identity, BoneEditingSpace.Global);
        var validated = basis.CreateKey(frame, new VpdLocalBoneTransform(translation, rotation), template);
        // Keep an untouched authored quaternion byte-for-byte when only translation was edited.
        return new() { BoneName = validated.BoneName, OriginalName = validated.OriginalName, Frame = frame,
            Translation = translation, Rotation = rotation, Interpolation = validated.Interpolation };
    }
    private void RefreshCommands()
    {
        foreach (var name in new[] { nameof(CanTranslate), nameof(CanRotate), nameof(CanEditMorph), nameof(CanRegister), nameof(CanEditSelection), nameof(CanModifySelection), nameof(CanPaste) }) Changed(name);
        if (!presentation.TryDeferCommands()) RaiseCommandAvailabilityChanged();
    }
    private void RaiseCommandAvailabilityChanged()
    {
        foreach (var command in new[] { CopyPoseCommand, PasteMirroredPoseCommand, RegisterPoseCommand, RegisterKeyframeCommand, DeleteKeyframesCommand, CopyKeyframesCommand, PasteKeyframesCommand, MoveKeyframesCommand, ResetPoseCommand, SelectTimelineRangeCommand, ClearTimelineSelectionCommand }) ((AnimationCommand)command).Refresh();
    }
    private void SetStatus(string value, params object?[] arguments)
    { status = value; statusArguments = arguments; statusError = null; Changed(nameof(Status)); ReportStatus?.Invoke(Status); }
    private void SetFailure(string prefix, Exception error)
    { status = prefix; statusArguments = []; statusError = error; Changed(nameof(Status)); ReportStatus?.Invoke(Status); }
    private void RefreshLocalization()
    {
        RebuildTimelineGroups();
        foreach (string name in new[] { nameof(Status), nameof(SelectionName), nameof(SelectionLabel), nameof(ModelContextLabel), nameof(FrameSummary), nameof(KeyframeSummary), nameof(TimelineEmptyHint), nameof(HiddenSelectionSummary), nameof(TimelineSelectionSummary), nameof(TimelineRangeValidationMessage) }) Changed(name);
    }
    private readonly EditorDeferredPresentation presentation = new();
    /// <summary>Playback presentation: notifications raised while deferred wait for FlushPresentation.</summary>
    internal void DeferPresentation(bool defer) => presentation.IsDeferring = defer;
    internal bool HasDeferredPresentation => presentation.HasPending;
    internal void FlushPresentation() => presentation.Flush(name => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)), RaiseCommandAvailabilityChanged);
    private void Changed([CallerMemberName] string? name = null)
    { if (!presentation.TryDefer(name)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
    private static int FindIndex(IEnumerable<string> names, string name) { int i = 0; foreach (var candidate in names) { if (candidate == name) return i; i++; } return -1; }
    private static float ToRadians(decimal? value) => (float)((double)(value ?? 0) * Math.PI / 180);
    private static decimal? ToDecimal(float value)
    {
        if (!float.IsFinite(value)) return null;
        try { return (decimal)value; } catch (OverflowException) { return null; }
    }
    private static Vector3 ToDegrees(Quaternion q)
    {
        // Inverse of System.Numerics CreateFromYawPitchRoll(Y, X, Z), with a stable gimbal-lock branch.
        double pitch = Math.Asin(Math.Clamp(2d * (q.W * q.X - q.Y * q.Z), -1, 1));
        double yaw, roll;
        if (Math.Abs(Math.Cos(pitch)) < 1e-6) { yaw = 2 * Math.Atan2(q.Y, q.W); roll = 0; }
        else
        {
            yaw = Math.Atan2(2d * (q.W * q.Y + q.X * q.Z), 1 - 2d * (q.X * q.X + q.Y * q.Y));
            roll = Math.Atan2(2d * (q.W * q.Z + q.X * q.Y), 1 - 2d * (q.X * q.X + q.Z * q.Z));
        }
        const double factor = 180 / Math.PI;
        return new((float)(pitch * factor), (float)(yaw * factor), (float)(roll * factor));
    }

    private sealed class AnimationCommand(Func<Task> action, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter) { if (CanExecute(parameter)) await action(); }
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
