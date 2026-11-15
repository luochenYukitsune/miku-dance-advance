using System.Diagnostics;
using System.Windows.Input;
using Avalonia.Threading;
using Mmd.Modern.Application;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App;

public sealed partial class EditorShellViewModel : IDisposable
{
    private readonly DispatcherTimer playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private bool refreshingFrame;
    private long lastPlaybackTimestamp;
    private bool advancing, refreshing, playing, busy, preparingPreview, canCancelOperation;
    private string operationStatus = "就绪";
    private Func<string>? operationStatusSource;
    public EditorProjectWorkspace Workspace { get; }
    public EditorModelBrowserViewModel Browser { get; } = new();
    public EditorAnimationViewModel Animation { get; } = new();
    public EditorSceneInspectorViewModel SceneInspector { get; } = new();
    public EditorPosePreview PosePreview { get; } = new();
    public long ContentRevision { get; private set; }
    public Func<Task>? ImportModelRequested { get; set; }
    public Func<Task>? ImportMotionRequested { get; set; }
    public Func<Task>? RemoveModelRequested { get; set; }
    public Func<Task>? ReplaceModelRequested { get; set; }
    public Func<Task>? OpenProjectRequested { get; set; }
    public Func<Task>? SaveProjectRequested { get; set; }
    public Func<Task>? SaveAsRequested { get; set; }
    public Func<Task>? NewProjectRequested { get; set; }
    public event EventHandler? ViewStateChanged;
    internal event EventHandler? PlayheadDiscontinuity;
    public ICommand ImportModelCommand { get; private set; } = null!;
    public ICommand ImportMotionCommand { get; private set; } = null!;
    public ICommand OpenProjectCommand { get; private set; } = null!;
    public ICommand SaveProjectCommand { get; private set; } = null!;
    public ICommand SaveAsCommand { get; private set; } = null!;
    public ICommand NewProjectCommand { get; private set; } = null!;
    public ICommand UndoCommand { get; private set; } = null!;
    public ICommand RedoCommand { get; private set; } = null!;
    public ICommand PlayCommand { get; private set; } = null!;
    public ICommand GoToEndCommand { get; private set; } = null!;
    public bool HasProject => Workspace.Session is not null;
    public bool IsBusy { get => busy; private set { if (busy == value) return; busy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLoading)); OnPropertyChanged(nameof(LoadingStatus)); OnPropertyChanged(nameof(CanEditWorkspace)); NotifyActivityChanged(); Animation.RefreshInputValidation(); SceneInspector.RefreshInputValidation(); RefreshCommands(); RefreshInspectorPages(); } }
    public bool CanCancelOperation { get => canCancelOperation; internal set { if (canCancelOperation == value) return; canCancelOperation = value; OnPropertyChanged(); NotifyActivityChanged(); } }
    public bool IsPreparingPreview => preparingPreview;
    public bool IsLoading => IsBusy || IsPreparingPreview;
    public string LoadingStatus => IsBusy ? activityState == EditorActivityState.Cancelling ? EditorLocalization.Text("正在取消…") : OperationStatus : IsPreparingPreview ? EditorLocalization.Text("正在准备预览…") : "";
    internal void SetPreviewLoading(bool value)
    {
        if (preparingPreview == value) return;
        preparingPreview = value; OnPropertyChanged(nameof(IsPreparingPreview));
        OnPropertyChanged(nameof(IsLoading)); OnPropertyChanged(nameof(LoadingStatus));
        NotifyActivityChanged();
    }
    public bool CanEditWorkspace => !IsBusy;
    public bool IsPlaying { get => playing; private set { playing = value; OnPropertyChanged(); OnPropertyChanged(nameof(PlayLabel)); } }
    public string PlayLabel => EditorLocalization.Text(IsPlaying ? "暂停" : "播放");
    public string OperationStatus
    {
        get => operationStatusSource?.Invoke() ?? EditorLocalization.Text(operationStatus);
        set
        {
            operationStatusSource = null; operationStatus = value; operationMessageRevision++;
            if (!IsBusy) { activityState = EditorActivityState.Idle; activityError = null; }
            OnPropertyChanged(); OnPropertyChanged(nameof(LoadingStatus)); OnPropertyChanged(nameof(Status));
            NotifyActivityChanged();
        }
    }
    public string MotionLabel => HasProject ? EditorLocalization.Format("工程动画 · {0} 帧", Workspace.Session!.DurationFrames) : EditorLocalization.Text("未载入动作");

    private void InitializeWorkspace()
    {
        ImportModelCommand = new WorkspaceCommand(() => Request(ImportModelRequested), () => !IsBusy);
        ImportMotionCommand = new WorkspaceCommand(() => Request(ImportMotionRequested), () => !IsBusy && Browser.SelectedModel is not null);
        OpenProjectCommand = new WorkspaceCommand(() => Request(OpenProjectRequested), () => !IsBusy);
        NewProjectCommand = new WorkspaceCommand(() => Request(NewProjectRequested), () => !IsBusy);
        SaveProjectCommand = new WorkspaceCommand(() => Request(SaveProjectRequested), () => !IsBusy && HasProject);
        SaveAsCommand = new WorkspaceCommand(() => Request(SaveAsRequested), () => !IsBusy && HasProject);
        UndoCommand = new WorkspaceCommand(() => { StopPlayback(); Workspace.Undo(); }, () => !IsBusy && Workspace.CanUndo);
        RedoCommand = new WorkspaceCommand(() => { StopPlayback(); Workspace.Redo(); }, () => !IsBusy && Workspace.CanRedo);
        Browser.ReplaceModelCommand = new WorkspaceCommand(() => Request(ReplaceModelRequested), () => !IsBusy && Browser.SelectedModel is not null);
        Browser.RemoveModelCommand = new WorkspaceCommand(() => Request(RemoveModelRequested), () => !IsBusy && Browser.SelectedModel is not null);
        PlayCommand = new WorkspaceCommand(TogglePlayback, () => !IsBusy && HasProject);
        GoToEndCommand = new WorkspaceCommand(() => CurrentFrame = (int)Math.Min(int.MaxValue, Workspace.Session?.DurationFrames ?? 0), () => !IsBusy && HasProject);
        Browser.ImportModelCommand = ImportModelCommand;
        Browser.SelectionChanged += (_, _) => RefreshSelection();
        Animation.CommitMotion = Workspace.CommitMotion;
        Animation.UnpersistableNames = Workspace.GetUnpersistableMotionNames;
        Animation.CanMutateProject = () => !IsBusy;
        Animation.ReportStatus = message => { OperationStatus = message; operationStatusSource = () => Animation.Status; };
        Animation.PoseDraftChanged += (_, _) =>
        {
            if (Animation.HasAnyDraft)
            {
                StopPlayback();
                if (Workspace.Session is { } session && session.CurrentFrame != CurrentFrame) session.Seek(CurrentFrame);
            }
            if (!refreshingFrame) ViewStateChanged?.Invoke(this, EventArgs.Empty);
        };
        Animation.PropertyChanged += (_, args) =>
        { if (args.PropertyName is null or nameof(EditorAnimationViewModel.SelectionLabel)) OnPropertyChanged(nameof(AnimationSelectionLabel)); };
        SceneInspector.ReportStatus = message => { OperationStatus = message; operationStatusSource = () => SceneInspector.Status; };
        SceneInspector.CommitSceneMotion = Workspace.CommitSceneMotion;
        SceneInspector.CommitSceneMotionWithCameraParent = Workspace.CommitSceneMotion;
        Workspace.Changed += (_, args) =>
        {
            bool preservePoseDrafts = args is EditorWorkspaceChangedEventArgs { PreservePoseDrafts: true };
            if (Dispatcher.UIThread.CheckAccess()) RefreshWorkspace(preservePoseDrafts: preservePoseDrafts);
            else Dispatcher.UIThread.Post(() => RefreshWorkspace(preservePoseDrafts: preservePoseDrafts));
        };
        playbackTimer.Tick += PlaybackTick;
        RefreshInspectorPages();
    }

    private async void Request(Func<Task>? action)
    {
        if (action is null || IsBusy) return;
        StopPlayback();
        try { await action(); }
        catch (Exception error) { ReportActivityFailure(error); }
    }

    public async Task RunOperationAsync(string status, Func<Task> operation)
    {
        if (IsBusy) return;
        StopPlayback(); activityError = null; activityState = EditorActivityState.Busy;
        CanCancelOperation = true; IsBusy = true; OperationStatus = status;
        long initialMessageRevision = operationMessageRevision;
        try
        {
            // Publish the actual busy state before beginning work. This is a dispatcher yield,
            // not a fake progress delay; expensive preparation must still run off the UI thread.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await operation();
            // Cancellation is a request. An operation that returns normally may already
            // have committed its atomic result; only an actual cancellation reports Cancelled.
            if (activityState is EditorActivityState.Busy or EditorActivityState.Cancelling)
            {
                if (operationMessageRevision == initialMessageRevision) OperationStatus = "操作已完成。";
                activityState = EditorActivityState.Completed;
            }
        }
        catch (OperationCanceledException) { MarkOperationCancelled(); }
        catch (Exception error) { ReportActivityFailure(error); throw; }
        finally { CanCancelOperation = false; IsBusy = false; }
    }

    public void RefreshWorkspace(Guid? preferredId = null, bool preservePoseDrafts = false)
    {
        RefreshAudioWorkspace();
        if (!preservePoseDrafts) Animation.InvalidateDrafts();
        ContentRevision++;
        refreshing = true;
        try { Browser.Refresh(Workspace.Motions, Workspace.Session?.Objects ?? [], preferredId ?? Browser.SelectedModel?.ObjectId); }
        finally { refreshing = false; }
        RefreshSelection();
        SceneInspector.SetPmmCameraContext(Workspace.Session);
        SceneInspector.SetSceneMotion(Workspace.Session?.CaptureAuthoredContent().SceneMotion);
        SceneInspector.CurrentFrame = CurrentFrame;
        OnPropertyChanged(nameof(ProjectTitle)); OnPropertyChanged(nameof(HasProject)); OnPropertyChanged(nameof(MotionLabel));
        RefreshCommands(); ViewStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshSelection()
    {
        if (refreshing) return;
        var actor = Workspace.Motions.FirstOrDefault(value => value.ObjectId == Browser.SelectedModel?.ObjectId);
        Animation.SetActor(actor?.ObjectId, actor?.Model, actor?.Motion, Browser.SelectedModel?.Name, actor?.Detachment);
        refreshingFrame = true;
        try
        {
            Animation.CurrentFrame = CurrentFrame;
            SceneInspector.CurrentFrame = CurrentFrame;
        }
        finally { refreshingFrame = false; }
        if (Browser.HierarchyTab == 0) Animation.SelectBone(Browser.SelectedBoneIndex);
        else Animation.SelectMorph(Browser.SelectedMorphIndex);
        HierarchyTab = Browser.HierarchyTab;
        // The lighting page keeps its main-light context while ordinary object selection updates page content.
        // When a project appears while that page is already open, establish the light context then.
        if (InspectorPage == EditorInspectorPage.Lighting && HasProject)
        {
            if (SceneSelectionIndex != 2) SceneSelectionIndex = 2;
        }
        else if (Browser.SelectedModel is not null) SceneSelectionIndex = -1;
        RefreshInspectorPages();
        RefreshCommands(); ViewStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnFrameChanged()
    {
        if (!advancing)
        {
            PlayheadDiscontinuity?.Invoke(this, EventArgs.Empty);
            Workspace.Session?.Seek(CurrentFrame);
            if (IsPlaying) lastPlaybackTimestamp = Stopwatch.GetTimestamp();
            SynchronizeAudioPlayback(explicitSeek: true);
        }
        refreshingFrame = true;
        try
        {
            Animation.CurrentFrame = CurrentFrame;
            SceneInspector.CurrentFrame = CurrentFrame;
        }
        finally { refreshingFrame = false; }
        ViewStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The loaded recovery PMA already owns the exact paused clock, including fractions.
    /// Updating its integer presentation must not seek and restart that restored solver.</summary>
    internal void RestoreRecoveryPlayhead(int frame)
    {
        if (frame < 0 || Workspace.Session is not { } session
            || frame != (int)Math.Min(int.MaxValue, session.CurrentFrame))
            throw new InvalidDataException("Recovered playhead does not match the project clock.");
        PlayheadDiscontinuity?.Invoke(this, EventArgs.Empty);
        _currentFrame = frame;
        SynchronizeAudioPlayback(explicitSeek: true);
        refreshingFrame = true;
        try { Animation.CurrentFrame = frame; SceneInspector.CurrentFrame = frame; }
        finally { refreshingFrame = false; }
        OnPropertyChanged(nameof(CurrentFrame)); ViewStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TogglePlayback()
    {
        if (IsPlaying) { StopPlayback(); return; }
        if (Workspace.Session is not { } session) return;
        if (CurrentFrame >= session.DurationFrames) CurrentFrame = 0;
        audioBlocked = false; reportedAudioFailure = null;
        lastPlaybackTimestamp = Stopwatch.GetTimestamp(); IsPlaying = true; playbackTimer.Start();
        SynchronizeAudioPlayback();
    }
    public void StopPlayback()
    {
        playbackTimer.Stop(); if (IsPlaying) IsPlaying = false;
        SynchronizeAudioPlayback();
    }
    private void PlaybackTick(object? sender, EventArgs e)
    {
        using var tickMeasure = InteractionDiagnostics.Measure("playback.ui-tick");
        if (!IsPlaying || Workspace.Session is not { } session || IsBusy) { StopPlayback(); return; }
        long now = Stopwatch.GetTimestamp();
        // A delayed UI tick still owns its full elapsed time. Keep the scene's
        // bounded physics catch-up and carry very long gaps across ticks so that
        // no call exceeds the soft-body advance limit.
        long elapsedTicks = Math.Min(now - lastPlaybackTimestamp, 10L * Stopwatch.Frequency);
        lastPlaybackTimestamp += elapsedTicks;
        double elapsed = elapsedTicks / (double)Stopwatch.Frequency;
        try
        {
            using (InteractionDiagnostics.Measure("playback.advance")) session.AdvancePreview(elapsed);
            advancing = true;
            int nextFrame = (int)Math.Min(int.MaxValue, session.CurrentFrame);
            bool changed = nextFrame != CurrentFrame;
            CurrentFrame = nextFrame;
            SynchronizeAudioPlayback();
            if (!changed) ViewStateChanged?.Invoke(this, EventArgs.Empty);
            if (session.CurrentFrame >= session.DurationFrames) StopPlayback();
        }
        catch (Exception error) { StopPlayback(); ReportActivityFailure(error, previewUnavailable: true); }
        finally { advancing = false; }
    }
    private void RefreshCommands()
    {
        foreach (var command in new[] { ImportModelCommand, ImportMotionCommand, OpenProjectCommand, SaveProjectCommand,
            SaveAsCommand, NewProjectCommand, UndoCommand, RedoCommand, PlayCommand, GoToEndCommand, Browser.RemoveModelCommand, Browser.ReplaceModelCommand })
            (command as WorkspaceCommand)?.RaiseCanExecuteChanged();
    }
    private sealed class WorkspaceCommand(Action execute, Func<bool>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
        public void Execute(object? parameter) { if (CanExecute(parameter)) execute(); }
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose()
    {
        StopPlayback(); playbackTimer.Tick -= PlaybackTick;
        DisposeAudioPlayback(); PosePreview.Dispose(); Workspace.Dispose();
    }
}
