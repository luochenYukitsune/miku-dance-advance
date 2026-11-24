using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    private async Task<bool> OpenPmmAsync(string path)
    {
        if (_shell is not { } shell) return false;
        PmmProjectImportPlan? plan = null;
        await ExecuteAsync(Text("正在检查模型资源…"), async token =>
            plan = await Task.Run(() => EditorProjectInterchange.PreparePmm(path, token: token), token));
        if (plan is null) return false;
        var classicEngine = shell.Workspace.RenderEngine as EditorClassicRenderEngine;
        var dialog = new EditorPmmImportWindow(plan, options: null, classic: classicEngine is not null);
        var confirmed = await dialog.ShowDialog<PmmProjectImportPlan?>(this);
        if (confirmed is null) return false;
        string? emmPath = null;
        if (classicEngine is not null)
        {
            int choice = await EditorDialogs.ChooseAsync(this, Text("Mda.Text.Classic.Import.EffectsTitle"),
                Text("Mda.Text.Classic.Import.EffectsPrompt"),
                [Text("Mda.Text.Classic.Import.ImplicitEffects"), Text("Mda.Text.Classic.Import.ChooseEmm"), Text("取消")]);
            if (choice == 1)
            {
                var files = await StorageProvider.OpenFilePickerAsync(new()
                {
                    Title = Text("Mda.Text.Classic.Import.ChooseEmm"), AllowMultiple = false,
                    FileTypeFilter = [new("EMM") { Patterns = ["*.emm"] }]
                });
                if (files.FirstOrDefault()?.TryGetLocalPath() is not { } selectedEmm) return false;
                emmPath = Path.GetFullPath(selectedEmm);
            }
            else if (choice != 0) return false;
        }
        bool committed = false;
        await ExecuteAsync(Text("正在打开工程…"), async token =>
        {
            var assets = new EditorPmmAssetLoader();
            using var candidate = await Task.Run(() =>
            {
                var physics = EditorProjectWorkspace.CreatePhysicsProvider();
                if (classicEngine is null)
                    return EditorProjectInterchange.CreatePmmCandidate(confirmed, assets.Load,
                        physics, dialog.ImportOptions, token, displayNameOverrides: dialog.DisplayNameOverrides);
                ReadOnlyMemory<byte>? emmBytes = emmPath is null ? null
                    : EditorProjectInterchange.ReadPmmImportBytes(emmPath, 16L * 1024 * 1024, token);
                return EditorProjectInterchange.CreateClassicPmmCandidate(confirmed, assets.Load,
                    physics, classicEngine, dialog.ImportOptions, token, displayNameOverrides: dialog.DisplayNameOverrides,
                    emmBytes: emmBytes, absoluteEmmPath: emmPath);
            }, token);
            shell.Workspace.Adopt(candidate);
            // Busy suppresses the usual viewport refresh. Adopt the new parent owner before
            // restoring its saved local camera draft, which can refer to a different parent
            // than the authored key at the current frame.
            var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
            viewport.Project = shell.Workspace.Session;
            shell.CurrentFrame = checked((int)confirmed.CurrentFrame);
            shell.Animation.RestoreSavedPoses(shell.Workspace.Motions, candidate.SavedPoses, shell.CurrentFrame);
            shell.SceneInspector.SetCameraDraft(candidate.SavedCamera);
            shell.SceneInspector.SetLightDraft(candidate.SavedLight);
            viewport.SetCameraPose(candidate.SavedCamera);
            shell.OperationStatus = string.Join(Environment.NewLine, candidate.Diagnostics);
            committed = true;
        });
        return committed;
    }

    private async Task<bool> EnsureMotionHeadersAsync()
    {
        if (_shell is not { } shell) return false;
        foreach (var actor in shell.Workspace.GetMotionHeaderRequirements())
        {
            while (true)
            {
                var name = await EditorDialogs.PromptAsync(this, "VMD 模型头",
                    Format("模型“{0}”的完整名称无法写入 VMD 的 20 字节模型头。请填写一个较短的名称；模型名称与原文件保持不变。", actor.Model.Name));
                if (name is null) return false;
                try { shell.Workspace.SetMotionHeader(actor.ObjectId, name); break; }
                catch (ArgumentException error) { await ShowErrorAsync(error); if (error.ParamName != "header.modelName") return false; }
            }
        }
        return true;
    }

    private async void ImportPose(object? sender, RoutedEventArgs args) => await ImportPoseAsync(null);

    private async Task<bool> ImportPoseAsync(string? requestedPath)
    {
        if (_shell is not { IsBusy: false } shell || shell.Browser.SelectedModel is not { } selected) return false;
        shell.StopPlayback();
        shell.SynchronizePendingScrub();
        if (!await CheckPoseDraftsAsync()) return false;
        string? path = requestedPath;
        if (path is null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = Text("导入姿态…"),
                FileTypeFilter = [new("VPD") { Patterns = ["*.vpd"] }] });
            path = files.FirstOrDefault()?.TryGetLocalPath();
        }
        if (path is null) return false;
        bool committed = false;
        try
        {
            var actor = shell.Workspace.Motions.Single(value => value.ObjectId == selected.ObjectId);
            var session = shell.Workspace.Session;
            uint frame = (uint)shell.CurrentFrame;
            await ExecuteAsync(Text("正在载入动作…"), async token =>
            {
                var records = await Task.Run(() => EditorProjectInterchange.PrepareVpd(path, selected.PmxModel, frame, token: token), token);
                var staged = new MotionEditor(actor.Motion); var plan = staged.PrepareRegister(records);
                if (plan.Conflicts.Count != 0 && !await EditorDialogs.ConfirmAsync(this, "覆盖关键帧",
                    Format("目标位置已有 {0} 个关键帧。覆盖后可以撤销。", plan.Conflicts.Count), Text("覆盖")))
                { shell.MarkOperationCancelled(); return; }
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(session, shell.Workspace.Session) || frame != (uint)shell.CurrentFrame
                    || !ReferenceEquals(actor.Motion, shell.Workspace.Motions.Single(item => item.ObjectId == selected.ObjectId).Motion))
                { shell.MarkOperationCancelled(); return; }
                staged.Apply(plan, confirmOverwrite: true); shell.Workspace.CommitMotion(actor.ObjectId, staged.ExportMotion());
                committed = true;
            });
        }
        catch (Exception error) { await ShowErrorAsync(error); }
        return committed;
    }

    private async void ExportMotion(object? sender, RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false } shell || shell.Browser.SelectedModel is not { } selected) return;
        if (!await CheckPoseDraftsAsync() || !await EnsureMotionHeadersAsync()) return;
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Text("导出动作…"), DefaultExtension = "vmd",
            SuggestedFileName = selected.Name + ".vmd", ShowOverwritePrompt = true, FileTypeChoices = [new("VMD") { Patterns = ["*.vmd"] }] });
        if (file?.TryGetLocalPath() is not { } path) return;
        await ExecuteAsync(Text("正在导出…"), token => Task.Run(() =>
        {
            var actor = shell.Workspace.Motions.Single(value => value.ObjectId == selected.ObjectId);
            EditorProjectInterchange.ExportVmd(path, actor.Motion, actor.Asset?.AnsiCodePage ?? 932, token: token);
        }, token));
    }

    private async void ExportPose(object? sender, RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false } shell || shell.Browser.SelectedModel is not { } selected) return;
        shell.StopPlayback();
        shell.SynchronizePendingScrub();
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Text("导出姿态…"), DefaultExtension = "vpd",
            SuggestedFileName = selected.Name + ".vpd", ShowOverwritePrompt = true, FileTypeChoices = [new("VPD") { Patterns = ["*.vpd"] }] });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            var actor = shell.Workspace.Motions.Single(value => value.ObjectId == selected.ObjectId);
            var pose = shell.Animation.CaptureLocalPose(actor, shell.CurrentFrame);
            await ExecuteAsync(Text("正在导出…"), token => Task.Run(() => EditorProjectInterchange.ExportVpd(path,
                actor.Model, pose.Bones, pose.MorphWeights, Path.GetFileName(selected.SourcePath), token: token), token));
        }
        catch (Exception error) { await ShowErrorAsync(error); }
    }

    private async Task ConvertProjectCopyAsync()
    {
        if (_shell is not { IsBusy: false, HasProject: true } shell) return;
        var workspace = shell.Workspace;
        string extension = workspace.ConversionFormat.Extension;
        string name = Path.GetFileName(workspace.ProjectPath) ?? Text("未命名工程.pma");
        string path;
        while (true)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new()
            {
                Title = shell.ConvertProjectLabel, DefaultExtension = extension[1..],
                SuggestedFileName = Path.ChangeExtension(name, extension), ShowOverwritePrompt = false,
                FileTypeChoices = [new(Text("MDA 便携工程")) { Patterns = ["*" + extension] }]
            });
            if (file?.TryGetLocalPath() is not { } selected) return;
            if (!File.Exists(selected)) { path = selected; break; }
            if (!await EditorDialogs.ConfirmAsync(this, shell.ConvertProjectLabel, Text("Mda.Text.Project.ConversionNewFile"),
                Text("Mda.Text.Project.ChooseAnotherName"))) return;
        }
        if (!CommitPendingInlineLighting() || !await CheckPoseDraftsAsync(preserveModelDrafts: true) || !await EnsureMotionHeadersAsync()) return;
        shell.SynchronizePendingScrub();
        await ExecuteAsync(Text("Mda.Text.Project.Converting"), async token =>
        {
            try
            {
                var report = await workspace.ExportConvertedCopyAsync(path, token, cancellation =>
                    Dispatcher.UIThread.InvokeAsync(() => SelectConversionToonsAsync(cancellation)).GetAwaiter().GetResult());
                string status = Format(workspace.ConversionFormat == EditorProjectFormat.Classic
                    ? "Mda.Text.Project.ClassicCopyCreated" : "Mda.Text.Project.ModernCopyCreated", Path.GetFileName(path));
                if (report.RestoredArchive) status += " " + Text("Mda.Text.Project.RestoredArchive");
                if (report.DeferredArchive) status += " " + Text("Mda.Text.Project.DeferredArchive");
                if (report.OmittedLights > 0 || report.OmittedGroups > 0 || report.OmittedLightingSettings
                    || report.OmittedModelPurposes > 0 || report.OmittedExtensions.Count > 0 || report.OmittedResources.Count > 0)
                    status += " " + Text("Mda.Text.Project.InactiveArchive");
                shell.OperationStatus = status;
            }
            catch (PmaSaveCommittedException error) when (StringComparer.OrdinalIgnoreCase.Equals(error.DestinationPath, Path.GetFullPath(path)))
            {
                shell.OperationStatus = Format("Mda.Text.Project.SavedCleanupFailed", error.InnerException?.Message ?? error.Message);
            }
        });
    }

    private async Task<IReadOnlyList<EditorClassicToonResources.Resource>> SelectConversionToonsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (await EditorDialogs.ConfirmAsync(this, Text("Mda.Text.Classic.Resources.Title"),
            Text("Mda.Text.Project.ConversionToonSource"), Text("Mda.Text.Classic.Resources.ChooseLocal")))
        {
            var selected = await EditorClassicToonResourcePicker.SelectAsync(this, token);
            if (selected is not null) return selected.Entries;
        }
        throw new OperationCanceledException(token);
    }

    private async void SavePmm(object? sender, RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false, HasProject: true } shell || !await CheckPoseDraftsAsync()) return;
        shell.StopPlayback(); shell.CancelPendingScrub(); shell.Workspace.Session!.Seek(shell.CurrentFrame);
        var options = new PmmSessionExportOptions();
        var report = EditorProjectInterchange.InspectPmmExport(shell.Workspace.Session, options);
        if (report.Issues.Any(issue => issue.Code == "pmm-name-encoding"))
        {
            int choice = await EditorDialogs.ChooseAsync(this, Text("工程文字编码"),
                Text("模型名称或路径无法按 CP932（日文）保存。可选择 CP936（中文）重试，名称与原文件保持不变。"),
                [Text("取消"), Text("使用 CP936 重试")], primary: 1);
            if (choice != 1) return;
            options = new() { AnsiCodePage = 936 };
            report = EditorProjectInterchange.InspectPmmExport(shell.Workspace.Session, options);
        }
        if (!report.CanExport) { await ShowErrorAsync(new PmaPmmExportException(report)); return; }
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Text("保存为 PMM…"), DefaultExtension = "pmm",
            SuggestedFileName = "project.pmm", ShowOverwritePrompt = true, FileTypeChoices = [new("PMM") { Patterns = ["*.pmm"] }] });
        if (file?.TryGetLocalPath() is not { } path) return;
        await ExecuteAsync(Text("正在保存工程…"), token => Task.Run(() => EditorProjectInterchange.ExportPmm(path, shell.Workspace.Session, options, token), token));
    }
}
