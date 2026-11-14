using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    private bool processingFileDrop;
    internal bool IsFileDropInProgress => processingFileDrop;

    private void InitializeFileDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnEditorFilesDragged, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnEditorFilesDragged, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnEditorFilesDropped, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private bool CanReceiveFileDrop => !processingFileDrop && !_closingConfirmed && !_closingPrompt
        && !_outputSettingsOpen && _shell is { IsBusy: false }
        && !OwnedWindows.Any(window => window.IsVisible);

    private void OnEditorFilesDragged(object? sender, DragEventArgs args)
    {
        // Leave text and other native control transfers alone. File drops are routed
        // through the editor even when the pointer is over an editable text field.
        if (!args.DataTransfer.Contains(DataFormat.File)) return;
        args.DragEffects = CanReceiveFileDrop && args.DragEffects.HasFlag(DragDropEffects.Copy)
            && GetDroppedFilePaths(args) is { } paths && EditorFileDropPlan.CanAcceptPaths(paths)
                ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
    }

    private async void OnEditorFilesDropped(object? sender, DragEventArgs args)
    {
        if (!args.DataTransfer.Contains(DataFormat.File)) return;
        var paths = GetDroppedFilePaths(args); // Platform transfer objects need not survive an await.
        args.Handled = true;
        if (!CanReceiveFileDrop || !args.DragEffects.HasFlag(DragDropEffects.Copy)
            || paths is null || !EditorFileDropPlan.CanAcceptPaths(paths))
        { args.DragEffects = DragDropEffects.None; return; }
        args.DragEffects = DragDropEffects.Copy;
        await ImportDroppedFilesAsync(paths);
    }

    private static string[]? GetDroppedFilePaths(DragEventArgs args)
    {
        var items = args.DataTransfer.TryGetFiles()?.Take(33).ToArray();
        if (items is null || items.Length is 0 or > 32 || items.Any(item => item is not IStorageFile)) return null;
        var paths = items.Select(item => item.TryGetLocalPath()).ToArray();
        return paths.Any(path => path is null) ? null : paths.Select(path => path!).ToArray();
    }

    internal async Task<bool> ImportDroppedFilesAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!CanReceiveFileDrop || _shell is not { } shell) return false;
        // This latch covers preflight and dialogs, where the existing operation's
        // IsBusy is deliberately false. Do not queue a second drop behind this one.
        processingFileDrop = true;
        shell.StopPlayback();
        try
        {
            var frozenPaths = paths.ToArray();
            var document = shell.Workspace.DocumentId;
            var preferences = _preferences;
            var format = shell.Workspace.ProjectFormat;
            EditorFileDropPlan? plan = null;
            await ExecuteAsync(EditorLocalization.Text("Mda.Text.FileDrop.Preparing"), async token =>
                plan = await Task.Run(() => EditorFileDropPlan.Prepare(frozenPaths, preferences, format, token), token));
            if (plan is null) return false;
            Guid? target = shell.Browser.SelectedModel?.ObjectId;
            foreach (var request in plan.Requests)
            {
                if (!ReferenceEquals(shell, _shell) || shell.IsBusy || document != shell.Workspace.DocumentId
                    || (request.Kind is EditorFileOpenKind.Motion or EditorFileOpenKind.Pose
                        && target.HasValue && target != shell.Browser.SelectedModel?.ObjectId))
                { shell.MarkOperationCancelled(); return false; }
                if (!await OpenAssociatedFileAsync(request))
                {
                    if (shell.ActivityState != EditorActivityState.Failed) shell.MarkOperationCancelled();
                    return false;
                }
                if (request.Kind is EditorFileOpenKind.Model or EditorFileOpenKind.Motion or EditorFileOpenKind.Pose)
                    target = shell.Browser.SelectedModel?.ObjectId;
            }
            return true;
        }
        catch (Exception error) { await ShowErrorAsync(error); return false; }
        finally { processingFileDrop = false; }
    }
}
