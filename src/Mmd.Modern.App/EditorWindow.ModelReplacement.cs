using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Mmd.Modern.Application;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    private async Task ReplaceSelectedModelAsync()
    {
        if (_shell is not { IsBusy: false } shell || shell.Browser.SelectedModel is not { } selected) return;
        if (!CommitPendingInlineLighting() || !await CheckPoseDraftsAsync()) return;
        shell.StopPlayback();
        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Text("Mda.Text.Context.ReplaceModel.Title"), AllowMultiple = false,
            FileTypeFilter = [new("PMX") { Patterns = ["*.pmx"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        await ExecuteAsync(Text("Mda.Text.Context.ReplaceModel.Preparing"), async token =>
        {
            var session = shell.Workspace.Session ?? throw new InvalidOperationException(Text("请先载入模型。"));
            var asset = await Task.Run(() => EditorProjectWorkspace.LoadModelAssetWithLimits(path, _preferences.PmxMaxBytes, token), token);
            if (shell.Workspace.RenderEngine is EditorClassicRenderEngine classic)
                asset = await Task.Run(() => classic.PrepareModelAsset(asset, path, token), token);
            var plan = await Task.Run(() => session.PrepareModelReplacement(selected.ObjectId, path, asset, token), token);
            bool keepsMapping = shell.Workspace.ReplacementKeepsMaterialMapping(plan, asset);
            bool? answer = await EditorModelReplacementDialog.ConfirmAsync(this, selected.Name, plan, keepsMapping);
            token.ThrowIfCancellationRequested();
            if (answer is null) { shell.MarkOperationCancelled(); return; }
            if (!ReferenceEquals(session, shell.Workspace.Session)) throw new InvalidOperationException(Text("Mda.Text.Context.ReplaceModel.Stale"));
            shell.CanCancelOperation = false;
            await Task.Run(() => shell.Workspace.ApplyModelReplacement(plan, answer.Value, !keepsMapping, token), token);
            shell.RefreshWorkspace(selected.ObjectId);
            shell.OperationStatus = Text("Mda.Text.Context.ReplaceModel.Complete");
        });
    }

    private async void RestoreUnownedModelData(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false } shell || shell.Browser.SelectedModel is not { } selected) return;
        if (!CommitPendingInlineLighting() || !await CheckPoseDraftsAsync()) return;
        try { await ReviewOrphanRestorationAsync(selected.ObjectId, showEmpty: true); }
        catch (Exception error) { await ShowErrorAsync(error); }
    }

    private async Task ReviewOrphanRestorationAsync(Guid targetId, bool showEmpty = false)
    {
        if (_shell is not { IsBusy: false } shell || shell.Workspace.Session is not { } session) return;
        bool found = false;
        foreach (var sourceId in shell.Workspace.Motions.Select(m => m.ObjectId).ToArray())
        {
            if (!ReferenceEquals(session, shell.Workspace.Session)) return;
            var plan = session.PrepareOrphanRestoration(sourceId, targetId);
            if (plan.Notices.Count == 0) continue;
            found = true;
            var sourceName = session.Objects.Single(o => o.Id == sourceId).Name;
            var targetName = session.Objects.Single(o => o.Id == targetId).Name;
            if (!await EditorModelReplacementDialog.ConfirmRestorationAsync(this, sourceName, targetName, plan)) continue;
            await ExecuteAsync(Text("Mda.Text.Context.ReplaceModel.Restoring"), async token =>
            {
                shell.CanCancelOperation = false;
                await Task.Run(() => shell.Workspace.ApplyOrphanRestoration(plan, token), token);
                shell.RefreshWorkspace(targetId);
                shell.OperationStatus = Text("Mda.Text.Context.ReplaceModel.Restored");
            });
        }
        if (!found && showEmpty)
            await EditorDialogs.ChooseAsync(this, Text("Mda.Text.Context.ReplaceModel.Unowned"),
                Text("Mda.Text.Context.ReplaceModel.NoRestorable"), [Text("关闭")]);
    }
}
