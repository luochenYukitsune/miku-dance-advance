using Avalonia.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    private async Task RemoveSelectedModelAsync()
    {
        if (_shell is not { IsBusy: false } shell || shell.Browser.SelectedModel is not { } selected) return;
        if (!await CheckPoseDraftsAsync()) return;
        shell.StopPlayback();
        if (shell.Workspace.Motions.Any(actor => actor.ObjectId == selected.ObjectId)) shell.Workspace.RemoveModel(selected.ObjectId);
    }

    private async Task<bool> CheckPoseDraftsAsync(bool preserveModelDrafts = false)
    {
        if (_shell is not { } shell) return false;
        if (Mmd.Modern.App.Controls.EditorNumericUpDown.HasInvalidInputs(this))
        { await ShowErrorAsync(new InvalidOperationException(Text("请先修正标出的输入值，再应用设置。"))); return false; }
        if ((!shell.Animation.HasAnyDraft || preserveModelDrafts) && !shell.SceneInspector.HasCameraDraft && !shell.SceneInspector.HasLightDraft) return true;
        shell.StopPlayback();
        shell.SynchronizePendingScrub();
        int answer = await EditorDialogs.ChooseAsync(this, Text("未登记姿态"), Text("当前工程有尚未登记的姿态。"),
            [Text("取消"), Text("放弃草稿"), Text("登记后继续")], primary: 2);
        if (answer == 0) return false;
        if (answer == 1) { if (!preserveModelDrafts) shell.Animation.InvalidateDrafts(); shell.SceneInspector.DiscardAllDrafts(); shell.RefreshWorkspace(); this.FindControl<EditorViewportHost>("SceneViewport")!.SetCameraPose(null); return true; }
        try
        {
            var source = shell.Workspace.Session ?? throw new InvalidOperationException(Text("请先载入模型。"));
            var actors = shell.Workspace.Motions;
            IReadOnlyList<EditorAnimationDraftMotionPlan> drafts = preserveModelDrafts ? [] : shell.Animation.PrepareDraftMotions(actors);
            if (shell.SceneInspector.HasCameraDraft && !shell.SceneInspector.CanRegisterCamera
                || shell.SceneInspector.HasLightDraft && !shell.SceneInspector.CanRegisterLight)
                throw new InvalidOperationException(Text("请先修正标出的输入值，再应用设置。"));
            var originalScene = source.CaptureAuthoredContent().SceneMotion;
            var sceneMotion = originalScene?.Motion ?? new VmdMotion { ModelName = "カメラ・照明" };
            uint frame = (uint)shell.CurrentFrame;
            var camera = shell.SceneInspector.CameraPose.ToKeyframePose();
            var light = shell.SceneInspector.LightPose;
            var records = new VmdMotion
            {
                CameraKeyframes = !shell.SceneInspector.HasCameraDraft ? [] : [new VmdCameraKeyframe
                {
                    Frame = frame, Interest = camera.LookAt, Rotation = camera.Angle, Distance = camera.Distance,
                    ViewAngle = checked((uint)MathF.Round(camera.Fov)), Perspective = camera.Perspective,
                    Interpolation = sceneMotion.CameraKeyframes.LastOrDefault(key => key.Frame == frame)?.Interpolation ?? new VmdCameraKeyframe().Interpolation
                }],
                LightKeyframes = !shell.SceneInspector.HasLightDraft ? [] : [new VmdLightKeyframe
                { Frame = frame, Color = light.Color, Direction = light.Direction }]
            };
            var sceneEditor = new SceneTrackEditor(sceneMotion);
            var scenePlan = sceneEditor.PrepareRegister(records);
            int conflicts = drafts.Sum(draft => draft.Plan.Conflicts.Count) + scenePlan.Conflicts.Count;
            if (conflicts != 0 && !await EditorDialogs.ConfirmAsync(this, Text("覆盖关键帧"),
                Format("目标位置已有 {0} 个关键帧。覆盖后可以撤销。", conflicts), Text("覆盖"))) return false;
            if (!ReferenceEquals(source, shell.Workspace.Session)
                || !ReferenceEquals(originalScene, source.CaptureAuthoredContent().SceneMotion)
                || actors.Any(actor => !ReferenceEquals(actor.Motion, shell.Workspace.Motions.Single(item => item.ObjectId == actor.ObjectId).Motion))) return false;
            var motions = drafts.ToDictionary(draft => draft.ObjectId, draft => draft.CreateMotion(confirmOverwrite: true));
            bool hasScene = records.CameraKeyframes.Count != 0 || records.LightKeyframes.Count != 0;
            if (hasScene) sceneEditor.Apply(scenePlan, confirmOverwrite: true);
            shell.Workspace.CommitDraftMotions(motions, hasScene ? sceneEditor.ExportMotion() : null,
                frame, shell.SceneInspector.HasCameraDraft ? camera : null);
            if (!preserveModelDrafts) shell.Animation.InvalidateDrafts();
            this.FindControl<EditorViewportHost>("SceneViewport")!.SetCameraPose(null);
            return true;
        }
        catch (Exception error) { await ShowErrorAsync(error); return false; }
    }
}
