using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>Scene pose drafts use the core VMD evaluator. Only registration calls the project
/// transaction; the host owns persistence and the shared character/scene Undo history.</summary>
public sealed partial class EditorSceneInspectorViewModel : INotifyPropertyChanged
{
    private static readonly PmmCameraPose DefaultCamera = new(new(0, 10, 0), Vector3.Zero, -45, 30, true);
    private static readonly PmmLightPose DefaultLight = new(new(.6f), new(-.5f, -1, .5f));
    private PmmCameraPose baseCamera, cameraPose;
    private PmmLightPose baseLight, lightPose;
    private VmdMotion motion = new();
    private SceneMotionAsset? source;
    private VmdSceneTracks tracks = new(new VmdMotion());
    private ScenePmmControlState? pmmControls;
    private IReadOnlyDictionary<Guid, (string Name, PmxModel Model)> cameraTargets = new Dictionary<Guid, (string, PmxModel)>();
    private int sceneSelectionIndex, currentFrame;
    private long contextRevision;
    private SceneTrackKind? committingTrack;
    private bool isBusy, cameraDraft, lightDraft, refreshingLocalization;
    private string status = "调整参数后，登记到当前帧";
    private object?[] statusArguments = [];
    private decimal? centerX, centerY, centerZ, rotationX, rotationY, rotationZ, distance, fov;
    private decimal? colorR, colorG, colorB, intensity, directionX, directionY, directionZ;
    private bool perspective;

    public EditorSceneInspectorViewModel() : this(DefaultCamera) { }
    public EditorSceneInspectorViewModel(PmmCameraPose camera, PmmLightPose? light = null)
    {
        baseCamera = camera; baseLight = light ?? DefaultLight;
        RegisterCameraKeyframeCommand = new SceneCommand(RegisterCameraKeyframeAsync, () => CanRegisterCamera);
        RegisterLightKeyframeCommand = new SceneCommand(RegisterLightKeyframeAsync, () => CanRegisterLight);
        RevertDraftCommand = new SceneCommand(() => { RevertDraft(); return Task.CompletedTask; }, () => CanRevertDraft);
        LoadPoses();
        Observe(this, static model => model.RefreshLocalization());
    }

    /// <summary>Must atomically commit, or throw without changing the project.</summary>
    public Action<VmdMotion>? CommitSceneMotion { get; set; }
    /// <summary>Project hosts commit the local camera and its retained PMM parent in one Undo step.</summary>
    public Action<VmdMotion, uint, PmmCameraPose?>? CommitSceneMotionWithCameraParent { get; set; }
    /// <summary>SceneTrackEditor owns scene conflicts; character MotionEditPlan is not applicable.</summary>
    public Func<SceneTrackEditPlan, Task<bool>>? ConfirmSceneOverwriteAsync { get; set; }
    /// <summary>Host validation includes raw native numeric text that has no parsed Value.</summary>
    public Func<bool>? ValidateInput { get; set; }
    public void RefreshInputValidation() => RefreshAvailability();
    public Action<string>? ReportStatus { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<PmmCameraPose>? CameraPoseChanged;
    public event Action<PmmLightPose>? LightPoseChanged;
    public ICommand RegisterCameraKeyframeCommand { get; }
    public ICommand RegisterLightKeyframeCommand { get; }
    public ICommand RevertDraftCommand { get; }
    public PmmCameraPose CameraPose => cameraPose;
    /// <summary>Color includes the UI intensity multiplier, as required by VMD's RGB-only light record.</summary>
    public PmmLightPose LightPose => lightPose;
    public bool IsWorldInspector => SceneSelectionIndex == 0;
    public bool IsCameraInspector => SceneSelectionIndex == 1;
    public bool IsLightInspector => SceneSelectionIndex == 2;
    public bool IsBusy => isBusy;
    public bool CanEdit => !IsBusy;
    public bool HasCameraDraft => cameraDraft;
    public bool HasLightDraft => lightDraft;
    public bool HasDraft => IsCameraInspector ? HasCameraDraft : IsLightInspector && HasLightDraft;
    public bool CanRevertDraft => !IsBusy && HasDraft;
    public bool CanRegisterCamera => !IsBusy && CameraIsValid && ValidateInput?.Invoke() != false;
    public bool CanRegisterLight => !IsBusy && LightIsValid && ValidateInput?.Invoke() != false;
    public string Status => Format(status, statusArguments);
    public string FrameSummary => Format("第 {0} 帧", CurrentFrame) + (HasDraft ? Text(" · 未登记") : "");
    public string CameraKeyframeSummary => Format("{0} 个相机关键帧", motion.CameraKeyframes.Count);
    public bool HasCameraParent => cameraPose.ParentModelIndex >= 0;
    public string CameraParentObject => CameraParentTarget() is { } parent && cameraTargets.TryGetValue(parent.ObjectId, out var target)
        ? target.Name : HasCameraParent ? Format("Mda.Text.CameraParentFallback", cameraPose.ParentModelIndex) : Text("Mda.Text.CameraParentNone");
    public string CameraParentBone => CameraParentTarget() is { } parent && cameraTargets.TryGetValue(parent.ObjectId, out var target)
        && (uint)parent.BoneIndex < (uint)target.Model.Bones.Count ? target.Model.Bones[parent.BoneIndex].Name : "";
    public string CameraParentHint => Text("Mda.Text.CameraParentHint");
    public string LightKeyframeSummary => Format("{0} 个灯光关键帧", motion.LightKeyframes.Count);
    public string CameraValidation => CameraIsValid ? "" : Text("请填完整构图参数；视角须在 1–179°，观察中心与距离须能形成有效视图。");
    public string LightValidation => LightIsValid ? "" : Text("请填完整灯光参数；RGB 和强度不能为负，方向不能全为 0。");
    public bool HasCameraValidation => !CameraIsValid;
    public bool HasLightValidation => !LightIsValid;
    public IReadOnlyList<string> Projections => [Text("透视"), Text("正交")];

    public int SceneSelectionIndex
    {
        get => sceneSelectionIndex;
        set
        {
            if (value is < 0 or > 2 || value == sceneSelectionIndex) return;
            sceneSelectionIndex = value; contextRevision++;
            Changed(); Changed(nameof(IsWorldInspector)); Changed(nameof(IsCameraInspector)); Changed(nameof(IsLightInspector));
            DraftChanged();
        }
    }
    public int CurrentFrame
    {
        get => currentFrame;
        set
        {
            value = Math.Max(0, value);
            if (currentFrame == value) return;
            currentFrame = value; contextRevision++; Changed(); LoadPoses();
        }
    }
    public decimal? CameraCenterX { get => centerX; set => SetCamera(ref centerX, value, p => p with { LookAt = p.LookAt with { X = Float(value) } }); }
    public decimal? CameraCenterY { get => centerY; set => SetCamera(ref centerY, value, p => p with { LookAt = p.LookAt with { Y = Float(value) } }); }
    public decimal? CameraCenterZ { get => centerZ; set => SetCamera(ref centerZ, value, p => p with { LookAt = p.LookAt with { Z = Float(value) } }); }
    public decimal? CameraRotationX { get => rotationX; set => SetCamera(ref rotationX, value, p => p with { Angle = p.Angle with { X = Radians(value) } }); }
    public decimal? CameraRotationY { get => rotationY; set => SetCamera(ref rotationY, value, p => p with { Angle = p.Angle with { Y = Radians(value) } }); }
    public decimal? CameraRotationZ { get => rotationZ; set => SetCamera(ref rotationZ, value, p => p with { Angle = p.Angle with { Z = Radians(value) } }); }
    public decimal? CameraDistance { get => distance; set => SetCamera(ref distance, value, p => p.ParentModelIndex >= 0
        ? p with { LookAt = p.LookAt with { Z = Float(value) } } : p with { Distance = Float(value) }); }
    public decimal? CameraFov { get => fov; set => SetCamera(ref fov, value, p => p with { Fov = Float(value) }); }
    public int CameraProjectionIndex
    {
        get => perspective ? 0 : 1;
        set
        {
            if (refreshingLocalization || IsBusy || value is < 0 or > 1 || value == CameraProjectionIndex) return;
            perspective = value == 0; cameraPose = cameraPose with { Perspective = perspective }; cameraDraft = true;
            contextRevision++; Changed(); DraftChanged(); PublishCamera();
        }
    }
    public decimal? LightColorR { get => colorR; set => SetLight(ref colorR, value, true); }
    public decimal? LightColorG { get => colorG; set => SetLight(ref colorG, value, true); }
    public decimal? LightColorB { get => colorB; set => SetLight(ref colorB, value, true); }
    public decimal? LightIntensity { get => intensity; set => SetLight(ref intensity, value, true); }
    public decimal? LightDirectionX { get => directionX; set => SetLight(ref directionX, value, false, 0); }
    public decimal? LightDirectionY { get => directionY; set => SetLight(ref directionY, value, false, 1); }
    public decimal? LightDirectionZ { get => directionZ; set => SetLight(ref directionZ, value, false, 2); }
    public Vector3 LightDirectionVector
    {
        get => lightPose.Direction;
        set
        {
            if (IsBusy || value == lightPose.Direction || !Finite(value) || value.LengthSquared() < 1e-12f) return;
            lightPose = lightPose with { Direction = value };
            directionX = Number(value.X); directionY = Number(value.Y); directionZ = Number(value.Z);
            lightDraft = true; contextRevision++;
            foreach (string name in new[] { nameof(LightDirectionX), nameof(LightDirectionY), nameof(LightDirectionZ) }) Changed(name);
            DraftChanged(); PublishLight();
        }
    }

    /// <summary>Set defaults from persisted host state; absent VMD tracks continue to use them.</summary>
    public void SetBasePoses(PmmCameraPose camera, PmmLightPose light)
    {
        baseCamera = camera; baseLight = light; contextRevision++; LoadPoses();
    }

    /// <summary>Keep PMM's stored parent IDs separate from the VMD local pose values. The target
    /// comes from the explicit native-name mapping, never the replacement model's ordinal.</summary>
    public void SetPmmCameraContext(SceneProjectSession? project)
    {
        var controls = project?.PmmControls;
        cameraTargets = project is null ? new Dictionary<Guid, (string, PmxModel)>()
            : project.CaptureAuthoredContent().Motions.ToDictionary(actor => actor.ObjectId,
                actor => (project.Objects.Single(item => item.Id == actor.ObjectId).Name, actor.Model));
        if (ReferenceEquals(pmmControls, controls))
        {
            // Names can change in an unrelated project transaction while this immutable
            // control state stays identical. Refresh labels without discarding local edits.
            Changed(nameof(CameraParentObject)); Changed(nameof(CameraParentBone));
            return;
        }
        pmmControls = controls;
        contextRevision++; LoadCamera(); DraftChanged();
    }

    /// <summary>Refresh after import, project transaction, Undo or Redo. Identical snapshots retain drafts.</summary>
    public void SetSceneMotion(SceneMotionAsset? asset)
    {
        if (ReferenceEquals(source, asset)) return;
        var candidate = new SceneTrackEditor(asset?.Motion ?? new VmdMotion()).ExportMotion();
        var evaluator = new VmdSceneTracks(candidate);
        source = asset; motion = candidate; tracks = evaluator; contextRevision++;
        LoadRegisteredOrAllPoses(); Changed(nameof(CameraKeyframeSummary)); Changed(nameof(LightKeyframeSummary));
    }

    public Task RegisterCameraKeyframeAsync()
    {
        if (!CanRegisterCamera) return Task.CompletedTask;
        uint frame = (uint)CurrentFrame;
        var keyPose = CameraPose.ToKeyframePose();
        var template = motion.CameraKeyframes.LastOrDefault(k => k.Frame == frame);
        // Preserve existing curve bytes. New records use the format's own default curve policy.
        var defaults = template ?? new VmdCameraKeyframe();
        return ApplyAsync(new VmdMotion { CameraKeyframes = [new VmdCameraKeyframe
        {
            Frame = frame, Interest = keyPose.LookAt, Rotation = keyPose.Angle, Distance = keyPose.Distance,
            ViewAngle = checked((uint)MathF.Round(keyPose.Fov)), Perspective = keyPose.Perspective,
            Interpolation = defaults.Interpolation
        }] }, SceneTrackKind.Camera, "已登记相机关键帧");
    }

    public Task RegisterLightKeyframeAsync()
    {
        if (!CanRegisterLight) return Task.CompletedTask;
        return ApplyAsync(new VmdMotion { LightKeyframes = [new VmdLightKeyframe
        { Frame = (uint)CurrentFrame, Color = LightPose.Color, Direction = LightPose.Direction }] }, SceneTrackKind.Light, "已登记主光关键帧");
    }

    public void RevertDraft()
    {
        if (!CanRevertDraft) return;
        contextRevision++;
        if (IsCameraInspector) LoadCamera();
        else if (IsLightInspector) LoadLight();
        DraftChanged(); SetStatus("已恢复当前帧的已登记参数");
    }

    /// <summary>Explicit host discard clears both scene channels even when their authored asset
    /// has not changed. A normal refresh deliberately retains unrelated unregistered drafts.</summary>
    public void DiscardAllDrafts()
    {
        if (IsBusy) return;
        contextRevision++; LoadPoses();
    }

    private async Task ApplyAsync(VmdMotion records, SceneTrackKind kind, string success)
    {
        if (IsBusy || ValidateInput?.Invoke() == false) return;
        isBusy = true; RefreshAvailability();
        long revision = contextRevision;
        try
        {
            if (CommitSceneMotion is null && CommitSceneMotionWithCameraParent is null) { SetStatus("请先载入模型或打开工程，再登记关键帧。"); return; }
            var staged = new SceneTrackEditor(motion);
            var plan = staged.PrepareRegister(records);
            if (plan.Conflicts.Count != 0 && (ConfirmSceneOverwriteAsync is null || !await ConfirmSceneOverwriteAsync(plan)))
            { SetStatus("已取消覆盖关键帧"); return; }
            if (revision != contextRevision) { SetStatus("帧或工程已变化，请重新登记。"); return; }
            if (ValidateInput?.Invoke() == false) return;
            staged.Apply(plan, confirmOverwrite: plan.Conflicts.Count != 0);
            var candidate = staged.ExportMotion();
            committingTrack = kind;
            try
            {
                if (CommitSceneMotionWithCameraParent is { } commit)
                    commit(candidate, checked((uint)CurrentFrame), kind == SceneTrackKind.Camera ? cameraPose : null);
                else CommitSceneMotion!(candidate);
                if (revision == contextRevision)
                {
                    motion = candidate; tracks = new(candidate); contextRevision++; LoadRegisteredOrAllPoses();
                    Changed(nameof(CameraKeyframeSummary)); Changed(nameof(LightKeyframeSummary));
                }
            }
            finally { committingTrack = null; }
            SetStatus(success);
        }
        catch (Exception ex) { SetStatus("登记关键帧失败：{0}", ex.Message); }
        finally { isBusy = false; RefreshAvailability(); }
    }

    private void LoadPoses() { LoadCamera(); LoadLight(); DraftChanged(); }
    private void LoadRegisteredOrAllPoses()
    {
        // The project's synchronous Changed callback can refresh during CommitSceneMotion.
        // Registering a camera must not discard an unrelated, unregistered light draft (or vice versa).
        if (committingTrack == SceneTrackKind.Camera) { LoadCamera(); DraftChanged(); }
        else if (committingTrack == SceneTrackKind.Light) { LoadLight(); DraftChanged(); }
        else LoadPoses();
    }
    private void LoadCamera()
    {
        cameraPose = tracks.CameraAt(CurrentFrame) ?? baseCamera;
        if (pmmControls is { } controls)
        {
            var parent = controls.CameraAt(CurrentFrame);
            cameraPose = cameraPose with { ParentModelIndex = parent.ParentModelIndex, ParentBoneIndex = parent.ParentBoneIndex,
                IgnoreParentRotation = parent.IgnoreParentRotation };
        }
        ReadCameraFields(); cameraDraft = false;
        PublishCamera();
    }
    public void SetCameraDraft(PmmCameraPose pose)
    {
        if (IsBusy || cameraPose == pose) return;
        _ = pose.CreateCamera().ViewProjection(1, 1);
        cameraPose = pose; ReadCameraFields(); cameraDraft = true;
        contextRevision++; DraftChanged(); PublishCamera();
    }
    /// <summary>Restores PMM's saved unregistered main light without changing its keyframes.</summary>
    public void SetLightDraft(PmmLightPose pose)
    {
        if (IsBusy || lightPose == pose) return;
        lightPose = pose;
        colorR = Number(pose.Color.X); colorG = Number(pose.Color.Y); colorB = Number(pose.Color.Z); intensity = 1;
        directionX = Number(pose.Direction.X); directionY = Number(pose.Direction.Y); directionZ = Number(pose.Direction.Z);
        lightDraft = true; contextRevision++;
        foreach (string name in new[] { nameof(LightColorR), nameof(LightColorG), nameof(LightColorB), nameof(LightIntensity), nameof(LightDirectionX), nameof(LightDirectionY), nameof(LightDirectionZ) }) Changed(name);
        DraftChanged(); PublishLight();
    }
    private void ReadCameraFields()
    {
        centerX = Number(cameraPose.LookAt.X); centerY = Number(cameraPose.LookAt.Y); centerZ = Number(cameraPose.LookAt.Z);
        rotationX = Number(cameraPose.Angle.X * (180 / MathF.PI)); rotationY = Number(cameraPose.Angle.Y * (180 / MathF.PI)); rotationZ = Number(cameraPose.Angle.Z * (180 / MathF.PI));
        distance = Number(cameraPose.CameraDepth); fov = Number(cameraPose.Fov); perspective = cameraPose.Perspective;
        foreach (string name in new[] { nameof(CameraCenterX), nameof(CameraCenterY), nameof(CameraCenterZ), nameof(CameraRotationX), nameof(CameraRotationY), nameof(CameraRotationZ), nameof(CameraDistance), nameof(CameraFov), nameof(CameraProjectionIndex) }) Changed(name);
    }
    private void LoadLight()
    {
        lightPose = tracks.LightAt(CurrentFrame) ?? baseLight;
        // VMD stores radiance RGB only. Intensity starts at one on load, retaining every original RGB bit.
        colorR = Number(lightPose.Color.X); colorG = Number(lightPose.Color.Y); colorB = Number(lightPose.Color.Z); intensity = 1;
        directionX = Number(lightPose.Direction.X); directionY = Number(lightPose.Direction.Y); directionZ = Number(lightPose.Direction.Z); lightDraft = false;
        foreach (string name in new[] { nameof(LightColorR), nameof(LightColorG), nameof(LightColorB), nameof(LightIntensity), nameof(LightDirectionX), nameof(LightDirectionY), nameof(LightDirectionZ) }) Changed(name);
        PublishLight();
    }
    private void SetCamera(ref decimal? field, decimal? value, Func<PmmCameraPose, PmmCameraPose> update, [CallerMemberName] string? name = null)
    {
        if (IsBusy || field == value) return;
        field = value;
        if (value.HasValue) cameraPose = update(cameraPose);
        if (cameraPose.ParentModelIndex >= 0 && name is nameof(CameraCenterZ) or nameof(CameraDistance))
        {
            centerZ = distance = value;
            Changed(name == nameof(CameraCenterZ) ? nameof(CameraDistance) : nameof(CameraCenterZ));
        }
        cameraDraft = true; contextRevision++; Changed(name); DraftChanged(); PublishCamera();
    }
    private void SetLight(ref decimal? field, decimal? value, bool color, int axis = 0, [CallerMemberName] string? name = null)
    {
        if (IsBusy || field == value) return;
        field = value;
        if (color && TryLightRadiance(out var radiance))
            lightPose = lightPose with { Color = radiance };
        else if (!color && value.HasValue)
        {
            var direction = lightPose.Direction;
            if (axis == 0) direction.X = Float(value); else if (axis == 1) direction.Y = Float(value); else direction.Z = Float(value);
            lightPose = lightPose with { Direction = direction };
        }
        lightDraft = true; contextRevision++; Changed(name); DraftChanged(); PublishLight();
    }

    private bool CameraIsValid => centerX.HasValue && centerY.HasValue && centerZ.HasValue
        && rotationX.HasValue && rotationY.HasValue && rotationZ.HasValue && distance.HasValue && fov.HasValue
        && cameraPose.Fov is >= 1 and <= 179 && CameraCanRender();
    private bool LightIsValid => colorR is >= 0 && colorG is >= 0 && colorB is >= 0 && intensity is >= 0
        && directionX.HasValue && directionY.HasValue && directionZ.HasValue
        && TryLightRadiance(out _)
        && Finite(lightPose.Color) && Finite(lightPose.Direction) && float.IsFinite(lightPose.Direction.LengthSquared()) && lightPose.Direction.LengthSquared() >= 1e-12f;
    private bool TryLightRadiance(out Vector3 radiance)
    {
        radiance = default;
        if (!colorR.HasValue || !colorG.HasValue || !colorB.HasValue || !intensity.HasValue) return false;
        radiance = new Vector3(Float(colorR), Float(colorG), Float(colorB)) * Float(intensity);
        return Finite(radiance);
    }
    private void PublishCamera()
    {
        Changed(nameof(CameraPose)); Changed(nameof(HasCameraParent)); Changed(nameof(CameraParentObject));
        Changed(nameof(CameraParentBone)); Changed(nameof(CameraParentHint));
        if (CameraIsValid) CameraPoseChanged?.Invoke(cameraPose);
    }
    private void PublishLight() { Changed(nameof(LightPose)); Changed(nameof(LightDirectionVector)); if (LightIsValid) LightPoseChanged?.Invoke(lightPose); }
    private void DraftChanged()
    {
        Changed(nameof(HasCameraDraft)); Changed(nameof(HasLightDraft)); Changed(nameof(HasDraft)); Changed(nameof(FrameSummary));
        Changed(nameof(CameraValidation)); Changed(nameof(HasCameraValidation)); Changed(nameof(LightValidation)); Changed(nameof(HasLightValidation));
        RefreshAvailability();
    }
    private void RefreshAvailability()
    {
        Changed(nameof(IsBusy)); Changed(nameof(CanEdit)); Changed(nameof(CanRegisterCamera)); Changed(nameof(CanRegisterLight)); Changed(nameof(CanRevertDraft));
        foreach (var command in new[] { RegisterCameraKeyframeCommand, RegisterLightKeyframeCommand, RevertDraftCommand }) ((SceneCommand)command).Refresh();
    }
    private void SetStatus(string value, params object?[] arguments)
    { status = value; statusArguments = arguments; Changed(nameof(Status)); ReportStatus?.Invoke(Status); }
    private void RefreshLocalization()
    {
        refreshingLocalization = true;
        try { Changed(null); }
        finally { refreshingLocalization = false; }
        Changed(nameof(CameraProjectionIndex));
    }
    private static float Float(decimal? value) => (float)(value ?? 0);
    private static float Radians(decimal? value) => Float(value) * (MathF.PI / 180);
    private bool CameraCanRender()
    {
        try { _ = cameraPose.CreateCamera().ViewProjection(1, 1); return true; }
        catch (ArgumentException) { return false; }
    }
    private ScenePmmCameraParent? CameraParentTarget()
    {
        if (!HasCameraParent || pmmControls is null) return null;
        return pmmControls.ResolveCameraParent(cameraPose);
    }
    private static decimal? Number(float value)
    {
        if (!float.IsFinite(value)) return null;
        try { return (decimal)value; }
        catch (OverflowException) { return null; }
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private sealed class SceneCommand(Func<Task> action, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter) { if (CanExecute(parameter)) await action(); }
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
