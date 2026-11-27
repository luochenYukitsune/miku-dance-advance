using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    private EditorLightViewportController? lightController;
    private EditorBoneViewportController? boneController;

    private async void OpenRigSettings(object? sender, RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false } shell || shell.Browser.SelectedModel is not { } selected) return;
        if (!await CheckPoseDraftsAsync()) return;
        shell.StopPlayback();
        try
        {
            shell.CancelPendingScrub(); var session = shell.Workspace.Session!; session.Seek(shell.CurrentFrame);
            var dialog = new EditorRigSettingsWindow(shell.Workspace, selected.ObjectId, shell.Browser.SelectedBoneIndex)
            { ConfirmOverwriteAsync = plan => EditorDialogs.ConfirmAsync(this, Text("覆盖关键帧"),
                Format("目标位置已有 {0} 个关键帧。覆盖后可以撤销。", plan.Conflicts.Count), Text("覆盖")) };
            var draft = await dialog.ShowDialog<EditorRigSettingsDraft?>(this);
            if (draft is not null && ReferenceEquals(session, shell.Workspace.Session)) shell.Workspace.ApplyRigSettings(draft);
        }
        catch (Exception error) { await ShowErrorAsync(error); }
    }

    private async void ShowAbout(object? sender, RoutedEventArgs args) =>
        await new EditorAboutWindow().ShowDialog(this);
    private async void ShowShortcuts(object? sender, RoutedEventArgs args) =>
        await EditorDialogs.ChooseAsync(this, Text("快捷键"),
            "Ctrl+N — " + Text("新建工程") + "\nCtrl+O — " + Text("打开工程…") + "\nCtrl+S — " + Text("保存工程")
            + "\nCtrl+Shift+S — " + Text("另存为…") + "\nCtrl+Z / Ctrl+Y — " + Text("撤销") + " / " + Text("重做")
            + "\nEnter / K — " + Text("登记骨骼关键帧（视口焦点）")
            + "\n" + Text("Mda.Text.Shortcuts.Playback") + "\n" + Text("Mda.Text.Shortcuts.Timeline")
            + "\n" + Text("Mda.Text.Ux.Shortcuts.Gizmo") + "\n" + Text("Mda.Text.Shortcuts.Keyframes")
            + "\n" + Text("Mda.Text.Shortcuts.Slider")
            + "\nEsc — " + Text("取消"), [Text("关闭")]);

    private async void OpenPreviewSettings(object? sender, RoutedEventArgs args) => await OpenPreviewSettingsAsync(this);

    private EditorPreviewRuntimeInfo CapturePreviewRuntimeInfo()
    {
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        double scaling = TopLevel.GetTopLevel(viewport)?.RenderScaling ?? 1;
        int width = Math.Clamp((int)Math.Ceiling(viewport.Bounds.Width * scaling), 1, 4096);
        int height = Math.Clamp((int)Math.Ceiling(viewport.Bounds.Height * scaling), 1, 4096);
        return new(viewport.AdapterName, viewport.IsWarp, width, height, viewport.Statistics);
    }

    private async Task OpenPreviewSettingsAsync(Window owner)
    {
        if (_shell is not { IsBusy: false } shell) return;
        shell.StopPlayback();
        if (!owner.IsVisible) return;
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        var runtime = CapturePreviewRuntimeInfo();
        bool isClassic = !EditorRenderEngineOptions.UsesModernQualityPreferences(shell.Workspace.RenderEngine);
        EditorPreviewCapabilities.TryGetCached(EditorOptionalRuntimePaths.Resolve(), runtime.AdapterName, out var cached);
        ViewQualityOptions? GetExportQuality() =>
            shell.OutputSettings?.Preset.Export.Quality
            ?? (_persistPreferences ? EditorPreferences.LoadOutputSettings(out _)?.Preset.Export.Quality : null)
            ?? EditorRenderEngineOptions.DefaultExportQuality(shell.Workspace.RenderEngine);
        void CopyToExportQuality(ViewQualityOptions previewQuality)
        {
            var exportQuality = StripPreviewOnlyForExport(previewQuality);
            exportQuality.Validate();
            var baseDraft = shell.OutputSettings ?? (_persistPreferences ? EditorPreferences.LoadOutputSettings(out _) : null);
            if (baseDraft is null)
            {
                if (!shell.CreateOutputSettingsViewModel().TryCreate(out baseDraft) || baseDraft is null)
                    throw new InvalidOperationException(Text("Mda.Text.Preview.ExportCopyUnavailable"));
            }
            var nextExport = baseDraft.Preset.Export with { Quality = exportQuality };
            var nextPreset = new ViewOutputPresetDefinition(baseDraft.Preset.StableId, baseDraft.Preset.Name,
                baseDraft.Preset.Preview, nextExport, baseDraft.Preset.Encoding, baseDraft.Preset.SchemaVersion);
            var nextRequest = baseDraft.Request with { Quality = exportQuality };
            nextRequest.Validate();
            var updatedDraft = baseDraft with { Preset = nextPreset, Request = nextRequest };
            updatedDraft.Validate();
            shell.ApplyOutputSettings(updatedDraft);
            if (_persistPreferences && !isClassic)
                EditorPreferences.SaveOutputSettings(updatedDraft);
        }
        var viewModel = new EditorPreviewSettingsViewModel(
            viewport.Quality,
            cached,
            runtime,
            capabilitiesPending: cached is null,
            isClassicEngine: isClassic,
            customPresetsPath: null,
            getExportQuality: GetExportQuality,
            copyToExportQuality: CopyToExportQuality,
            persistCustomPresets: _persistPreferences);
        var dialog = new EditorPreviewSettingsWindow(viewModel,
            (forceRefresh, token) => Task.Run(() =>
                EditorPreviewCapabilities.QueryInstalledCached(runtime.Width, runtime.Height, runtime.AdapterName, forceRefresh, token), token));
        var quality = await dialog.ShowDialog<ViewQualityOptions?>(owner);
        if (quality is null) return;
        try
        {
            if (_persistPreferences && !isClassic)
                EditorPreferences.SavePreviewQuality(quality);
            ApplyPreviewQuality(viewport, quality);
        }
        catch (Exception error) { await ShowErrorAsync(error, owner); }
    }

    private static ViewQualityOptions StripPreviewOnlyForExport(ViewQualityOptions quality)
    {
        var gi = quality.Gi is { PreviewAcceleration: true } g ? g with { PreviewAcceleration = false } : quality.Gi;
        return gi == quality.Gi ? quality : quality with { Gi = gi };
    }

    internal void ApplyPreviewQuality(EditorViewportHost viewport, ViewQualityOptions quality)
    {
        _shell?.BeginPreviewAttempt();
        viewport.Quality = quality;
        if (viewport.HasRendererError) viewport.RestartRenderer();
    }

    private SceneProjectRenderSnapshot CaptureEditorSnapshot(IReadOnlyList<Mmd.Modern.Application.SceneObjectRenderRequest> requests)
    {
        var shell = _shell!;
        requests = boneController?.SnapshotRequests(requests) ?? requests;
        try
        {
            var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
            return shell.PosePreview.TryGetOrRequest(shell.Workspace.Session, shell.Animation, requests,
                viewport.RequestRender, error => shell.ReportActivityFailure(error, previewUnavailable: true),
                interactive: viewport.BoneController?.IsDragging == true,
                prewarm: !shell.IsPlaying && shell.Animation.IsBoneSelected)
                ?? shell.Workspace.Session!.CaptureRenderSnapshot(requests);
        }
        catch (Exception error)
        {
            shell.ReportActivityFailure(error, previewUnavailable: true);
            return shell.Workspace.Session!.CaptureRenderSnapshot(requests);
        }
    }

    private void InitializeLightController(EditorShellViewModel shell)
    {
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        lightController = new(shell.Workspace, CommitViewportLighting, viewport.RequestRender, shell.SupportsModernLightingWorkspace)
        { SelectionActivated = id => shell.ViewportPickedLightId = id };
        InitializeLightHelperTools(shell);
        if (boneController is not null) boneController.Changed -= BoneToolsChanged;
        boneController = new(shell, viewport.RequestRender)
        { ShowBones = this.FindControl<ToggleButton>("BoneGuidesToggle")!.IsChecked == true };
        boneController.Changed += BoneToolsChanged;
        viewport.BoneController = boneController;
        viewport.LightController = lightController;
        viewport.FrameDecorator = frame => boneController.DecorateFrame(lightController.DecorateFrame(frame), publishPicking: false,
            lightDragging: lightController.IsDragging || viewport.MainLightViewport?.IsDragging == true);
        SynchronizeBoneTools();
        InitializeLightingInspector(shell);
    }

    private void ToggleBoneGuides(object? sender, RoutedEventArgs args)
    { if (boneController is not null) boneController.ShowBones = (sender as ToggleButton)?.IsChecked == true; }
    private void ToggleSelectionBounds(object? sender, RoutedEventArgs args)
    { if (boneController is not null) boneController.ShowSelectionBounds = (sender as ToggleButton)?.IsChecked == true; }
    private void SelectBoneSelection(object? sender, RoutedEventArgs args)
    { if (!LeaveLightContext()) return; if (boneController is not null) boneController.IsManipulationEnabled = false; SynchronizeBoneTools(); }
    private void SelectBoneMove(object? sender, RoutedEventArgs args)
    { if (!LeaveLightContext()) return; if (boneController is not null) { boneController.IsManipulationEnabled = true; boneController.RotateHandle = false; } SynchronizeBoneTools(); }
    private void ToggleBoneRotation(object? sender, RoutedEventArgs args)
    { if (!LeaveLightContext()) return; if (boneController is not null) { boneController.IsManipulationEnabled = true; boneController.RotateHandle = true; } SynchronizeBoneTools(); }
    private void BoneToolsChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess()) SynchronizeBoneTools();
        else Dispatcher.UIThread.Post(SynchronizeBoneTools);
    }
    private void SynchronizeBoneTools()
    {
        if (boneController is not { } controller) return;
        this.FindControl<ToggleButton>("BoneGuidesToggle")!.IsChecked = controller.ShowBones;
        this.FindControl<ToggleButton>("BoneSelectMode")!.IsChecked = !controller.IsManipulationEnabled;
        this.FindControl<ToggleButton>("BoneMoveMode")!.IsChecked = controller.IsManipulationEnabled && !controller.RotateHandle;
        this.FindControl<ToggleButton>("BoneRotateMode")!.IsChecked = controller.IsManipulationEnabled && controller.RotateHandle;
    }

    private async void OpenInterpolation(object? sender, RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false, IsPlaying: false } shell) return;
        try
        {
            if (shell.SceneSelectionIndex == 1 && shell.Workspace.Session?.CaptureAuthoredContent().SceneMotion is { } scene)
            {
                uint frame = (uint)shell.CurrentFrame;
                if (!scene.Motion.CameraKeyframes.Any(key => key.Frame == frame)) return;
                var draft = await new EditorInterpolationWindow(EditorInterpolationViewModel.ForCamera(scene.Motion, [frame])).ShowDialog<EditorInterpolationDraft?>(this);
                if (draft is not null && ReferenceEquals(scene, shell.Workspace.Session?.CaptureAuthoredContent().SceneMotion))
                    shell.Workspace.CommitSceneMotion(draft.ApplyTo(scene.Motion));
                return;
            }
            if (!shell.IsAnimationInspector || shell.Browser.SelectedModel is not { } selected) return;
            var actor = shell.Workspace.Motions.Single(item => item.ObjectId == selected.ObjectId);
            var selection = shell.Animation.SelectedKeyframes.Where(key => key.Kind == KeyframeTrackKind.Bone).ToArray();
            if (selection.Length == 0) { shell.OperationStatus = Text("请选择一个骨骼关键帧。"); return; }
            // The dialog must never edit keys the current row projection hides without the user
            // explicitly seeing them: require the explicit All tracks view instead.
            if (shell.Animation.HasHiddenSelectedBoneKeyframes)
            { shell.OperationStatus = Text("Mda.Text.Context.Timeline.InterpolationBlocked"); return; }
            var result = await new EditorInterpolationWindow(new(actor.Motion, selection)).ShowDialog<EditorInterpolationDraft?>(this);
            if (result is not null && ReferenceEquals(actor.Motion, shell.Workspace.Motions.FirstOrDefault(item => item.ObjectId == actor.ObjectId)?.Motion))
                shell.Workspace.CommitMotion(actor.ObjectId, result.ApplyTo(actor.Motion));
        }
        catch (Exception error) { await ShowErrorAsync(error); }
    }

    private async void MoveTimelineKeys(object? sender, TimelineKeysMoveRequestedEventArgs args)
    {
        if (_shell is not { IsBusy: false } shell || args.FrameDelta == 0 || args.Selection.Count == 0) return;
        shell.Animation.SelectTimelineKeys(args.Selection);
        long target = args.Selection.Min(key => (long)key.Frame) + args.FrameDelta;
        if (target < 0 || target > int.MaxValue) return;
        shell.Animation.MoveTargetFrame = (int)target;
        await shell.Animation.MoveKeyframesAsync();
    }
}
