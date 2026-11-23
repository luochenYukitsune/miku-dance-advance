using System.Collections.Immutable;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    private readonly DispatcherTimer recoveryTimer = new();
    private readonly List<EditorAutoSaveCoordinator> recoveryOwners = [];
    private readonly Dictionary<string, (EditorRecoveryStore Store, Guid SessionId)> reviewedRecoverySessions
        = new(StringComparer.OrdinalIgnoreCase);
    private EditorAutoSaveCoordinator? recoveryOwner;
    private (string Directory, int Versions, long Bytes)? recoveryConfiguration;
    private Guid? recoveryDocument;
    private bool reviewingRecovery, closingRecovery, recoveryWindowClosed, capturingRecovery;

    private void InitializeRecovery()
    {
        recoveryTimer.Tick += OnRecoveryTimer;
        Opened += (_, _) => RefreshRecoveryTimer();
        Closed += (_, _) =>
        {
            recoveryWindowClosed = true; closingRecovery = true; recoveryTimer.Stop();
            // A managed Closed notification is a controlled exit, including a force-close or a
            // detached startup shell. Finish the detached writers before releasing their leases.
            // A process interruption never reaches this hook and leaves its own sessions unclean.
            // Coordinator shutdown and store I/O never await the dispatcher; captures already
            // belong to their Task.Run worker, so this final drain cannot wait on this UI thread.
            foreach (var owner in recoveryOwners)
            {
                try { owner.ShutdownCleanAsync().GetAwaiter().GetResult(); }
                catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
                finally { owner.Dispose(); }
            }
            foreach (var session in reviewedRecoverySessions.Values)
            {
                try { session.Store.CompleteInactiveSessionStartupOfferAsync(session.SessionId).GetAwaiter().GetResult(); }
                catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            }
            reviewedRecoverySessions.Clear();
        };
    }

    private void RefreshRecoveryTimer()
    {
        recoveryTimer.Stop();
        if (!_recoveryEnabled || !IsVisible || !_preferences.AutoSaveEnabled || closingRecovery || recoveryWindowClosed) return;
        recoveryTimer.Interval = TimeSpan.FromMinutes(_preferences.AutoSaveIntervalMinutes);
        recoveryTimer.Start();
    }

    private async void OnRecoveryTimer(object? sender, EventArgs args) => await SaveRecoveryCheckpointAsync();

    private void OnRecoveryWorkspaceChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnRecoveryWorkspaceChanged(sender, args));
            return;
        }
        if (sender is not EditorProjectWorkspace workspace || !ReferenceEquals(workspace, _shell?.Workspace)
            || recoveryDocument is not { } previous || previous == workspace.DocumentId) return;
        recoveryDocument = workspace.DocumentId;
        RetireAcknowledgedDocument(previous);
    }

    private async void RetireAcknowledgedDocument(Guid document)
    {
        try { foreach (var owner in recoveryOwners.ToArray()) await owner.RetireCleanAsync(document); }
        catch (Exception error)
        {
            if (!recoveryWindowClosed && _shell is { } shell)
                shell.OperationStatus = EditorLocalization.Format("Mda.Text.Recovery.RetireFailed", error.Message);
        }
    }

    internal async Task<EditorAutoSaveResult?> SaveRecoveryCheckpointAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_recoveryEnabled || !_preferences.AutoSaveEnabled || reviewingRecovery || closingRecovery || recoveryWindowClosed || capturingRecovery
            || _shell is not { IsBusy: false, IsPlaying: false, HasProject: true } shell) return null;
        capturingRecovery = true;
        Guid capturedDocument = shell.Workspace.DocumentId;
        try
        {
            var configuration = (_preferences.RecoveryDirectory, _preferences.RecoveryMaxVersions, _preferences.RecoveryMaxTotalBytes);
            if (recoveryOwner is null || recoveryConfiguration != configuration)
            {
                // The old owner keeps its leases until acknowledged document retirement or clean
                // shutdown. Changing a setting must not manufacture an abandoned editing session.
                if (recoveryOwner is not null) await recoveryOwner.WaitForIdleAsync();
                if (recoveryWindowClosed || closingRecovery || !ReferenceEquals(shell, _shell) || shell.IsBusy) return null;
                recoveryOwner = new(new EditorRecoveryStore(configuration.RecoveryDirectory,
                    new(configuration.RecoveryMaxVersions, configuration.RecoveryMaxTotalBytes)));
                recoveryOwners.Add(recoveryOwner); recoveryConfiguration = configuration;
            }
            Guid document = shell.Workspace.DocumentId;
            if (recoveryDocument is { } previous && previous != document)
                foreach (var owner in recoveryOwners) await owner.RetireCleanAsync(previous);
            recoveryDocument = document;
            if (recoveryWindowClosed || closingRecovery || !ReferenceEquals(shell, _shell) || shell.IsBusy
                || shell.IsPlaying || document != capturedDocument || shell.Workspace.DocumentId != capturedDocument) return null;
            // Both captures happen synchronously on the UI owner, without committing pose drafts.
            // The detached package writer never reads the live workspace or these view models.
            var state = EditorRecoverySessionState.Capture(shell.Animation, shell.SceneInspector, shell.Workspace.Motions) with
            {
                SceneSelectionIndex = shell.SceneSelectionIndex, HierarchyTab = shell.HierarchyTab,
                PixelsPerFrame = shell.PixelsPerFrame, FirstVisibleFrame = shell.FirstVisibleFrame,
                ProjectFrame = shell.Workspace.Session!.CurrentFrame,
                NumericInputText = EditorRecoveryNumericInputs.Capture(this),
                NativeLighting = this.FindControl<EditorLightInspector>("NativeLightInspector")!
                    .CaptureRecoveryState(shell.Workspace.Session.Lighting, shell.InspectorPage, capturedDocument) with
                    {
                        RotateHandle = lightController?.RotateHandle == true, ActiveLightId = lightController?.SelectedLightId,
                        ActiveLightIds = lightController?.SelectedLightIds.ToImmutableArray() ?? []
                    }
            };
            state = shell.Workspace.ProjectRecoveryState(state);
            using var json = JsonDocument.Parse(state.ToJsonBytes());
            var metadata = new EditorRecoveryCheckpointMetadata(shell.Workspace.ProjectPath,
                shell.Workspace.ProjectPath is { } path ? Path.GetFileName(path) : EditorLocalization.Text("Mda.Text.Recovery.Unnamed"),
                shell.ContentRevision, json.RootElement, projectFormat: shell.Workspace.ProjectFormat);
            var result = await recoveryOwner.CheckpointAsync(document, EditorAutoSaveSnapshot.GetFingerprint(metadata),
                () => new EditorAutoSaveSnapshot(metadata, shell.Workspace.CaptureRecoverySnapshot()));
            if (result.Error is { } error && !recoveryWindowClosed && ReferenceEquals(shell, _shell))
                shell.OperationStatus = EditorLocalization.Format(result.Checkpoint is null
                    ? "Mda.Text.Recovery.SaveFailed" : "Mda.Text.Recovery.SavedCleanupFailed", error.Message);
            return result;
        }
        catch (Exception error)
        {
            if (!recoveryWindowClosed && ReferenceEquals(shell, _shell))
                shell.OperationStatus = EditorLocalization.Format("Mda.Text.Recovery.SaveFailed", error.Message);
            return null;
        }
        finally { capturingRecovery = false; }
    }

    private async Task RetireRecoveryDocumentAsync(Guid document)
    {
        foreach (var owner in recoveryOwners) await owner.RetireCleanAsync(document);
        if (recoveryDocument == document) recoveryDocument = null;
    }

    internal async Task CompleteRecoveryShutdownAsync()
    {
        if (recoveryWindowClosed) return;
        closingRecovery = true; recoveryTimer.Stop();
        try
        {
            foreach (var owner in recoveryOwners) await owner.ShutdownCleanAsync();
            if (recoveryWindowClosed) return;
            // Finishing an automatic offer does not resolve an earlier interrupted run. Keep
            // its unclean history available, without repeating that offer after a controlled exit.
            foreach (var session in reviewedRecoverySessions.Values.ToArray())
                await session.Store.CompleteInactiveSessionStartupOfferAsync(session.SessionId);
            reviewedRecoverySessions.Clear();
        }
        catch (Exception error)
        {
            if (recoveryWindowClosed) { System.Diagnostics.Trace.WriteLine(error); return; }
            recoveryOwner = null; recoveryConfiguration = null; closingRecovery = false; RefreshRecoveryTimer(); throw;
        }
    }

    internal async Task ReviewRecoveryAsync()
    {
        if (!_recoveryEnabled || _shell is not { IsBusy: false } shell || reviewingRecovery || closingRecovery || recoveryWindowClosed) return;
        reviewingRecovery = true;
        try
        {
            while (!recoveryWindowClosed)
            {
                var found = await Task.Run(() => EditorRecoveryDiscovery.ScanAsync(_preferences));
                if (found.Errors.Count != 0)
                    shell.OperationStatus = EditorLocalization.Format("Mda.Text.Recovery.ScanFailed", found.Errors.Count);
                if (found.Sessions.Count == 0 || closingRecovery || recoveryWindowClosed) return;
                var choiceTask = new EditorRecoveryWindow(this, found.Sessions).ShowDialog<EditorRecoveryChoice?>(this);
                // Register after ShowDialog succeeds so a scan or a failed display does not
                // acknowledge unseen history. Other sessions and active editors stay untouched.
                foreach (var session in found.Sessions)
                {
                    var checkpoint = session.Checkpoints[0];
                    reviewedRecoverySessions[Path.GetDirectoryName(checkpoint.PackagePath)!]
                        = (found.Sources[checkpoint.PackagePath], session.SessionId);
                }
                var choice = await choiceTask;
                if (choice is null || recoveryWindowClosed) return;
                var source = found.Sources[choice.Checkpoint.PackagePath];
                if (choice.Dismiss)
                {
                    if (await EditorDialogs.ConfirmAsync(this, EditorLocalization.Text("Mda.Text.Recovery.Title"),
                        EditorLocalization.Text("Mda.Text.Recovery.DismissConfirm"), EditorLocalization.Text("Mda.Text.Recovery.Dismiss")))
                    {
                        await source.MarkInactiveSessionCleanAsync(choice.Checkpoint.SessionId);
                        reviewedRecoverySessions.Remove(Path.GetDirectoryName(choice.Checkpoint.PackagePath)!);
                    }
                    continue;
                }
                if (choice.Checkpoint.ProjectFormat != shell.Workspace.ProjectFormat)
                {
                    await ShowErrorAsync(new InvalidDataException(EditorLocalization.Format("Mda.Text.Recovery.ForeignFormat",
                        choice.Checkpoint.ProjectFormat.Extension)));
                    continue;
                }
                if (!await CheckUnsavedAsync()) continue;
                if (!await RestoreRecoveryCheckpointAsync(choice.Checkpoint)) continue;
                // Preserve the source recovery session until its replacement has actually committed.
                reviewingRecovery = false;
                var saved = await SaveRecoveryCheckpointAsync();
                reviewingRecovery = true;
                if (saved?.Checkpoint is not null)
                {
                    await source.MarkInactiveSessionCleanAsync(choice.Checkpoint.SessionId);
                    reviewedRecoverySessions.Remove(Path.GetDirectoryName(choice.Checkpoint.PackagePath)!);
                }
                return;
            }
        }
        catch (Exception error) { if (!recoveryWindowClosed) await ShowErrorAsync(error); }
        finally { reviewingRecovery = false; RefreshRecoveryTimer(); }
    }

    internal async Task<bool> RestoreRecoveryCheckpointAsync(EditorRecoveryCheckpoint checkpoint)
    {
        if (_shell is not { IsBusy: false } shell || recoveryWindowClosed) return false;
        if (checkpoint.ProjectFormat != shell.Workspace.ProjectFormat)
        {
            shell.OperationStatus = EditorLocalization.Format("Mda.Text.Recovery.ForeignFormat", checkpoint.ProjectFormat.Extension);
            return false;
        }
        shell.StopPlayback();
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        bool wasSuspended = viewport.IsSuspended;
        viewport.IsSuspended = true;
        var nativeInspector = this.FindControl<EditorLightInspector>("NativeLightInspector")!;
        using var lightEdits = nativeInspector.SuspendRecoveryEdits();
        bool previousAdjusting = _adjustingInspectorPage, previousLightContext = _restoringLightContext;
        bool previousRefresh = _refreshingLightingInspector;
        _adjustingInspectorPage = _restoringLightContext = _refreshingLightingInspector = true;
        try
        {
            var oldDocument = shell.Workspace.DocumentId;
            EditorRecoverySessionState? state = null;
            await ExecuteAsync(EditorLocalization.Text("Mda.Text.Recovery.Restoring"), async token =>
            {
                state = await Task.Run(() => shell.Workspace.OpenRecovery(checkpoint, token, notifyChanged: false), token);
                shell.RefreshWorkspace(state.Animation.ObjectId);
            });
            if (state is null) return false;
            await RetireRecoveryDocumentAsync(oldDocument);
            shell.RestoreRecoveryPlayhead(state.Animation.CurrentFrame);
            shell.Browser.HierarchyTab = state.HierarchyTab;
            shell.Browser.SelectedBoneIndex = state.Animation.SelectedBoneIndex;
            shell.Browser.SelectedMorphIndex = state.Animation.SelectedMorphIndex;
            shell.PixelsPerFrame = state.PixelsPerFrame; shell.FirstVisibleFrame = state.FirstVisibleFrame;
            shell.SceneSelectionIndex = state.SceneSelectionIndex;
            state.Restore(shell.Animation, shell.SceneInspector, shell.Workspace.Motions);
            EditorRecoveryNumericInputs.Restore(this, state.NumericInputText);
            if (state.NativeLighting is { } native)
            {
                shell.ShowInspectorPage(native.InspectorPage);
                _observedInspectorPage = shell.InspectorPage;
                nativeInspector.RestoreRecoveryState(native, shell.Workspace.Session!.Lighting, shell.Workspace.DocumentId);
                lightController?.CancelDrag();
                lightController?.SelectMany(native.ActiveLightIds.Length > 0 ? native.ActiveLightIds
                    : native.ActiveLightId is { } id ? [id] : [], native.ActiveLightId);
                if (lightController is { } controller) controller.RotateHandle = native.RotateHandle;
                this.FindControl<EditorViewportHost>("SceneViewport")!.RotateLightHandle = lightController?.RotateHandle ?? false;
                ApplyInspectorPagePresentation(shell);
            }
            RefreshViewport(this, EventArgs.Empty);
            shell.OperationStatus = EditorLocalization.Text("Mda.Text.Recovery.Restored");
            return true;
        }
        finally
        {
            _adjustingInspectorPage = previousAdjusting;
            _restoringLightContext = previousLightContext;
            _refreshingLightingInspector = previousRefresh;
            lightEdits.Dispose();
            try { if (!previousRefresh) RefreshLightingInspector(); }
            finally
            {
                viewport.RequireCurrentPoseInputForNextFrame(() => shell.Animation.SnapshotDrafts().Count != 0
                    && !shell.PosePreview.HasLatestInputSnapshot);
                viewport.IsSuspended = wasSuspended;
            }
        }
    }
}
