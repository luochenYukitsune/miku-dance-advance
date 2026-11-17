using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using System.ComponentModel;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Mmd.Modern.Application;
using Mmd.Modern.App.Controls;
using Mmd.Modern.ViewOutput;

namespace Mmd.Modern.App;

public partial class EditorWindow : Window
{
    private EditorShellViewModel? _shell;
    private bool _outputSettingsOpen;
    private bool _closingConfirmed;
    private bool _closingPrompt;
    private CancellationTokenSource? _operationCancellation;
    private readonly bool _persistPreferences;
    private readonly bool _recoveryEnabled;
    private EditorPreferences _preferences = new();
    private bool _applyingPreferences;
    private long _mappedRevision = -1;
    private long _mappedBindingRevision = -1;
    private SceneProjectSession? _mappedSession;

    public EditorWindow() : this(true) { }
    public EditorWindow(bool persistPreferences) : this(persistPreferences, null) { }
    internal EditorWindow(bool persistPreferences, EditorPreferences? initialPreferences)
    {
        _persistPreferences = persistPreferences;
        _recoveryEnabled = persistPreferences || initialPreferences is not null;
        if (initialPreferences is not null) _preferences = initialPreferences;
        else if (persistPreferences) _preferences = EditorPreferences.Load();
        EditorLocalization.SetLanguage(_preferences.Language);
        InitializeComponent();
        InitializeFileDrop();
        InitializeClassicEffects();
        this.FindControl<MenuItem>("PluginsMenuItem")!.IsEnabled = persistPreferences;
        InitializeRecovery();
        DataContextChanged += (_, _) => AttachShell(DataContext as EditorShellViewModel);
        Closed += async (_, _) =>
        {
            var old = _shell;
            AttachShell(null);
            this.FindControl<EditorViewportHost>("SceneViewport")!.Dispose();
            try { await ReleasePluginsAsync(); }
            catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            finally { old?.Dispose(); }
        };
        Closing += CheckClosing;
        KeyDown += OnEditorKeyDown;
        AddHandler(EditorNumericUpDown.InputValidityChangedEvent, (_, _) =>
        { _shell?.Animation.RefreshInputValidation(); _shell?.SceneInspector.RefreshInputValidation(); });
        this.FindControl<EditorViewportHost>("SceneViewport")!.Error += error =>
        {
            if (_shell?.Workspace.RenderEngine is EditorClassicRenderEngine) LogClassicEffect(error.ToString());
            _shell?.ReportActivityFailure(error, previewUnavailable: true);
        };
        this.FindControl<EditorViewportHost>("SceneViewport")!.CameraPoseChanged += pose => _shell?.SceneInspector.SetCameraDraft(pose);
        this.FindControl<EditorViewportHost>("SceneViewport")!.EditorKeyPressed += OnViewportKeyPressed;
    }

    private void AttachShell(EditorShellViewModel? shell)
    {
        if (_shell is not null)
        {
            _shell.Workspace.Changed -= OnRecoveryWorkspaceChanged;
            _shell.Workspace.Changed -= OnClassicMappingChanged;
            _shell.PropertyChanged -= OnShellPropertyChanged;
            _shell.ViewStateChanged -= RefreshViewport;
            _shell.PlayheadDiscontinuity -= InvalidateViewportPlayhead;
            _shell.SceneInspector.CameraPoseChanged -= PreviewCamera;
            _shell.SceneInspector.LightPoseChanged -= PreviewLight;
            DetachLoadingState(_shell);
        }
        if (lightController is not null) lightController.Changed -= LightToolsChanged;
        _shell = shell;
        RefreshClassicEffectsMenu();
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.SetPlaybackActive(shell?.IsPlaying == true);
        viewport.SnapshotProvider = null; viewport.FrameDecorator = null; viewport.PoseInputPending = null;
        viewport.LightController = null; viewport.BoneController = null;
        if (shell is null || _shell is null) return;
        shell.Workspace.Changed += OnRecoveryWorkspaceChanged;
        shell.Workspace.Changed += OnClassicMappingChanged;
        viewport.SetRenderEngine(shell.Workspace.RenderEngine);
        _mappedRevision = -1; _mappedBindingRevision = -1; _mappedSession = null;
        shell.Animation.ValidateInput = () => !EditorNumericUpDown.HasInvalidInputs(this);
        shell.SceneInspector.ValidateInput = () => !EditorNumericUpDown.HasInvalidInputs(this);
        this.FindControl<EditorViewportHost>("SceneViewport")!.SnapshotProvider = CaptureEditorSnapshot;
        viewport.PoseInputPending = () => shell.PosePreview.IsPreparingPreview && !shell.PosePreview.HasLatestInputSnapshot;
        InitializeLightController(shell);
        AttachLoadingState(shell);
        _shell.PropertyChanged += OnShellPropertyChanged;
        _shell.ViewStateChanged += RefreshViewport;
        _shell.PlayheadDiscontinuity += InvalidateViewportPlayhead;
        _shell.SceneInspector.CameraPoseChanged += PreviewCamera;
        _shell.SceneInspector.LightPoseChanged += PreviewLight;
        _shell.SceneInspector.ConfirmSceneOverwriteAsync = plan => EditorDialogs.ConfirmAsync(this, EditorLocalization.Text("覆盖场景关键帧"),
            EditorLocalization.Format("目标位置已有 {0} 个关键帧。覆盖后可以撤销。", plan.Conflicts.Count), "覆盖");
        _shell.ImportModelRequested = ImportModelAsync;
        _shell.ImportMotionRequested = ImportMotionAsync;
        _shell.RemoveModelRequested = RemoveSelectedModelAsync;
        _shell.ReplaceModelRequested = ReplaceSelectedModelAsync;
        _shell.OpenProjectRequested = OpenProjectAsync;
        _shell.SaveProjectRequested = () => SaveProjectAsync(false);
        _shell.SaveAsRequested = () => SaveProjectAsync(true);
        _shell.NewProjectRequested = NewProjectAsync;
        _shell.Animation.ConfirmOverwriteAsync = plan => EditorDialogs.ConfirmAsync(this, EditorLocalization.Text("覆盖关键帧"),
            EditorLocalization.Format("目标位置已有 {0} 个关键帧。覆盖后可以撤销。", plan.Conflicts.Count), "覆盖");
        if (_persistPreferences)
        {
            ApplyPreferences(_preferences);
            if (EditorRenderEngineOptions.UsesModernQualityPreferences(shell.Workspace.RenderEngine))
            {
                viewport.Quality = EditorPreferences.LoadPreviewQuality();
                var output = EditorPreferences.LoadOutputSettings(out var diagnostic);
                if (output is not null) _shell.ApplyOutputSettings(output);
                if (diagnostic is not null) _shell.OperationStatus = diagnostic;
            }
        }
        else if (_recoveryEnabled) ApplyPreferences(_preferences);
        ApplyTheme(_shell);
        RefreshViewport(this, EventArgs.Empty);
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(EditorShellViewModel.SelectedThemeIndex)
            && sender is EditorShellViewModel shell)
            ApplyTheme(shell);
        if (args.PropertyName is null or nameof(EditorShellViewModel.ActivityState)) RefreshActivityPresentation();
        if (args.PropertyName == nameof(EditorShellViewModel.ShowWorldGuides)) RefreshViewport(this, EventArgs.Empty);
        if (args.PropertyName == nameof(EditorShellViewModel.RightInspectorPageIndex)
            && sender is EditorShellViewModel pageShell) OnInspectorPageChanged(pageShell);
        if (args.PropertyName == nameof(EditorShellViewModel.SceneSelectionIndex)) OnSceneSelectionIndexChanged();
        if (args.PropertyName == nameof(EditorShellViewModel.IsPlaying))
            this.FindControl<EditorViewportHost>("SceneViewport")!.SetPlaybackActive(_shell?.IsPlaying == true);
        if (args.PropertyName is nameof(EditorShellViewModel.IsPlaying) or nameof(EditorShellViewModel.IsBusy)
            or nameof(EditorShellViewModel.IsPreparingPreview)) RefreshPreviewFrameRate();
        if (!_applyingPreferences && _persistPreferences && _shell is { } active
            && args.PropertyName is nameof(EditorShellViewModel.SelectedThemeIndex) or nameof(EditorShellViewModel.ShowWorldGuides))
        {
            _preferences = _preferences with { ThemeIndex = active.SelectedThemeIndex, ShowWorldGuides = active.ShowWorldGuides };
            try { _preferences.Save(); } catch (IOException error) { active.ReportActivityFailure(error); }
        }
    }

    private void InvalidateViewportPlayhead(object? sender, EventArgs args)
        => this.FindControl<EditorViewportHost>("SceneViewport")!.InvalidateTemporalHistory();

    private void ApplyTheme(EditorShellViewModel shell)
    {
        RequestedThemeVariant = shell.SelectedThemeIndex == 1 ? ThemeVariant.Light : ThemeVariant.Dark;
        RefreshActivityPresentation();
    }

    private void ToggleTheme(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (_shell is not null) _shell.SelectedThemeIndex = 1 - _shell.SelectedThemeIndex;
    }

    private async void OpenOutputSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (_outputSettingsOpen || _shell is not { IsBusy: false } shell) return;
        shell.StopPlayback();
        _outputSettingsOpen = true;
        try
        {
            var dialog = new OutputSettingsWindow(shell.CreateOutputSettingsViewModel(), shell.HasProject);
            var result = await dialog.ShowDialog<OutputSettingsDraft?>(this);
            if (result is not null && ReferenceEquals(shell, _shell))
            {
                shell.ApplyOutputSettings(result);
                if (_persistPreferences && EditorRenderEngineOptions.UsesModernQualityPreferences(shell.Workspace.RenderEngine))
                    EditorPreferences.SaveOutputSettings(result);
                if (dialog.ExportRequested)
                {
                    if (!await CheckPoseDraftsAsync()) return;
                    if (File.Exists(result.Destination) && !await EditorDialogs.ConfirmAsync(this, EditorLocalization.Text("覆盖输出文件"),
                        EditorLocalization.Text("输出文件已存在，是否覆盖？"), EditorLocalization.Text("覆盖"), destructive: true)) return;
                    await RunExportWithStatusAsync(shell, result);
                }
            }
        }
        catch (Exception error) { await ShowErrorAsync(error); }
        finally { _outputSettingsOpen = false; }
    }

    private void RefreshViewport(object? sender, EventArgs args)
    {
        if (_shell is not { } shell || shell.IsBusy) return;
        RefreshClassicEffectsMenu();
        using var measure = InteractionDiagnostics.Measure("view.refresh-viewport");
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.Project = shell.Workspace.Session;
        viewport.EmptyCamera = shell.Camera;
        viewport.ShowWorldGuides = shell.ShowWorldGuides;
        viewport.SetLightPose(shell.SceneInspector.HasLightDraft ? shell.SceneInspector.LightPose : null);
        if (_mappedRevision != shell.ContentRevision || !ReferenceEquals(_mappedSession, shell.Workspace.Session))
        {
            if (_mappedBindingRevision != shell.RenderBindingRevision || !ReferenceEquals(_mappedSession, shell.Workspace.Session))
            {
                viewport.SetRenderBinding(shell.Workspace.CaptureRenderBinding());
                _mappedBindingRevision = shell.RenderBindingRevision;
            }
            _mappedRevision = shell.ContentRevision; _mappedSession = shell.Workspace.Session;
            viewport.InvalidateTemporalHistory();
        }
        RefreshLightingInspector();
        viewport.RequestRender();
    }

    private void PreviewCamera(PmmCameraPose pose) => this.FindControl<EditorViewportHost>("SceneViewport")!
        .SetCameraPose(_shell?.SceneInspector.HasCameraDraft == true ? pose : null);
    private void PreviewLight(PmmLightPose pose) => this.FindControl<EditorViewportHost>("SceneViewport")!.SetLightPose(pose);

    private void ApplyPreferences(EditorPreferences preferences)
    {
        if (_shell is null) return;
        _preferences = preferences; _shell.Workspace.Preferences = preferences;
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.InvertCameraMouseX = preferences.InvertCameraMouseX;
        viewport.InvertCameraMouseY = preferences.InvertCameraMouseY;
        EditorLocalization.SetLanguage(preferences.Language);
        _applyingPreferences = true;
        try { _shell.SelectedThemeIndex = preferences.ThemeIndex; _shell.ShowWorldGuides = preferences.ShowWorldGuides; }
        finally { _applyingPreferences = false; }
        ApplyTheme(_shell); RefreshViewport(this, EventArgs.Empty);
        RefreshRecoveryTimer();
    }

    private async void OpenPreferences(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false }) return;
        var dialog = new EditorPreferencesWindow(_preferences)
        {
            OpenPreviewQualityAsync = OpenPreviewSettingsAsync,
            GetPreviewRuntimeInfo = CapturePreviewRuntimeInfo,
            GetInteractionDiagnosticsMetadata = GetInteractionDiagnosticsMetadataAsync
        };
        var result = await dialog.ShowDialog<EditorPreferences?>(this);
        if (result is null) return;
        try
        {
            result = result.WithRecoveryDirectoryHistory(_preferences);
            if (_persistPreferences) result.Save();
            ApplyPreferences(result);
        }
        catch (Exception error) { await ShowErrorAsync(error); }
    }

    private void SelectTimelineKeys(object? sender, EventArgs args)
    {
        if (_shell is null || sender is not EditorTimeline timeline) return;
        // Range/clear commands update this same two-way selection binding. They only select
        // keys; the pointer-selection navigation below must not seek or change the channel.
        if (_shell.Animation.IsUpdatingTimelineSelectionWithoutNavigation) return;
        _shell.Animation.SelectTimelineKeys(timeline.SelectedKeyframes);
        _shell.CurrentFrame = _shell.Animation.CurrentFrame;
        if (_shell.Animation.SelectedBoneIndex is { } bone) { _shell.Browser.HierarchyTab = 0; _shell.Browser.SelectedBoneIndex = bone; }
        else if (_shell.Animation.SelectedMorphIndex is { } morph) { _shell.Browser.HierarchyTab = 1; _shell.Browser.SelectedMorphIndex = morph; }
    }

    /// <summary>Session UI-only row filter; it never edits, saves or dirties the authored motion.</summary>
    private void SelectTimelineFollowSelection(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        ApplyTimelineTrackMode(EditorTimelineTrackMode.FollowSelection);

    private void SelectTimelineAllTracks(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        ApplyTimelineTrackMode(EditorTimelineTrackMode.AllTracks);

    private void ApplyTimelineTrackMode(EditorTimelineTrackMode mode)
    {
        if (_shell is null) return;
        _shell.Animation.SetTimelineTrackMode(mode);
        // ToggleButton can toggle itself off; keep both entries bound to the actual projection mode.
        if (this.FindControl<ToggleButton>("TimelineFollowSelectionToggle") is { } follow)
            follow.IsChecked = mode == EditorTimelineTrackMode.FollowSelection;
        if (this.FindControl<ToggleButton>("TimelineAllTracksToggle") is { } all)
            all.IsChecked = mode == EditorTimelineTrackMode.AllTracks;
    }

    private async void OpenModelProperties(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false } shell || shell.Browser.SelectedModel is not { } selected) return;
        if (!await CheckPoseDraftsAsync()) return;
        try
        {
            var draft = await new EditorModelPropertiesWindow(shell.Workspace, selected.ObjectId).ShowDialog<EditorModelPropertiesDraft?>(this);
            if (draft is null) return;
            shell.Workspace.ApplyModelProperties(draft);
            shell.OperationStatus = EditorLocalization.Text("模型属性已应用。");
        }
        catch (Exception error) { await ShowErrorAsync(error); }
    }

    private Task ImportModelAsync() => ImportModelAsync(null);

    private async Task<bool> ImportModelAsync(string? requestedPath)
    {
        if (!CommitPendingInlineLighting() || !await CheckPoseDraftsAsync()) return false;
        string[] paths = requestedPath is not null ? [requestedPath] :
            (await StorageProvider.OpenFilePickerAsync(new() { Title = EditorLocalization.Text("载入模型"), AllowMultiple = true,
                FileTypeFilter = [new(EditorLocalization.Text("MMD 模型")) { Patterns = ["*.pmx", "*.pmd"] }] }))
            .Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
        bool committed = false;
        foreach (string path in paths)
        {
            Guid importedId = Guid.Empty;
            if (_shell is not { } shell) continue;
            var options = await EditorDialogs.ChooseModelImportAsync(this, path, shell.Workspace.Session?.Lighting.Lights.Count ?? 0);
            if (options is null) continue;
            var workingFocus = shell.SceneInspector.CameraPose.LookAt;
            await ExecuteAsync(EditorLocalization.Text("正在载入模型…"), async token =>
            {
                var model = await Task.Run(() => EditorProjectWorkspace.LoadModelAssetWithLimits(path, _preferences.PmxMaxBytes, token), token);
                token.ThrowIfCancellationRequested();
                // Workspace commit and its physics preparation cannot be interrupted safely.
                shell.CanCancelOperation = false;
                int oldLights = shell.Workspace.Session?.Lighting.Lights.Count ?? 0;
                var id = await Task.Run(() => shell.Workspace.AddModel(path, model, options, workingFocus));
                importedId = id;
                int addedLights = (shell.Workspace.Session?.Lighting.Lights.Count ?? 0) - oldLights;
                shell.RefreshWorkspace(id);
                shell.OperationStatus = options.Purpose.EnvironmentIndoor == true
                    ? addedLights > 0
                        ? EditorLocalization.Text("Mda.Text.Context.Room.ImportedWithBasicLight")
                        : EditorLocalization.Text("Mda.Text.Context.Room.ImportedUsingLights")
                    : EditorLocalization.Text("模型已载入。选择骨骼或表情后开始编辑。");
                committed = true;
            });
            if (importedId != Guid.Empty) await ReviewOrphanRestorationAsync(importedId);
        }
        return committed;
    }

    private Task ImportMotionAsync() => ImportMotionAsync(null);

    private async Task<bool> ImportMotionAsync(string? requestedPath)
    {
        if (_shell is not { } shell || shell.Browser.SelectedModel is not { } selected) return false;
        if (!await CheckPoseDraftsAsync()) return false;
        string? path = requestedPath;
        if (path is null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = EditorLocalization.Text("载入动作"), AllowMultiple = false,
                FileTypeFilter = [new(EditorLocalization.Text("VMD 动作")) { Patterns = ["*.vmd"] }] });
            path = files.FirstOrDefault()?.TryGetLocalPath();
        }
        if (path is null) return false;
        if (shell.Workspace.Motions.First(item => item.ObjectId == selected.ObjectId).Motion is { } old
            && (old.BoneKeyframes.Count != 0 || old.MorphKeyframes.Count != 0)
            && !await EditorDialogs.ConfirmAsync(this, EditorLocalization.Text("替换模型动作"), EditorLocalization.Text("将替换当前模型的动作，工程中的其他模型保持原样。此操作可以撤销。"), EditorLocalization.Text("替换"))) return false;
        bool committed = false;
        await ExecuteAsync(EditorLocalization.Text("正在载入动作…"), async token =>
        {
            SceneMotionAsset motion;
            try
            {
                motion = await Task.Run(() => EditorProjectInterchange.PrepareVmd(path, selected.PmxModel, token: token), token);
            }
            catch (Mmd.Modern.Formats.VmdFormatException error) when (error.InnerException is System.Text.DecoderFallbackException)
            {
                token.ThrowIfCancellationRequested();
                int choice = await EditorDialogs.ChooseAsync(this, EditorLocalization.Text("动作文字编码"),
                    EditorLocalization.Text("动作中的名称无法按 CP932（日文）读取。若此文件使用中文编码，可选择 CP936 重试。") + "\n\n" + error.Message,
                    [EditorLocalization.Text("取消"), EditorLocalization.Text("使用 CP936 重试")], primary: 1);
                token.ThrowIfCancellationRequested();
                if (choice != 1)
                {
                    shell.MarkOperationCancelled();
                    return;
                }
                motion = await Task.Run(() => EditorProjectInterchange.PrepareVmd(path, selected.PmxModel, ansiCodePage: 936, token: token), token);
            }
            token.ThrowIfCancellationRequested();
            // Workspace commit and its physics preparation cannot be interrupted safely.
            shell.CanCancelOperation = false;
            await Task.Run(() => shell.Workspace.BindMotion(selected.ObjectId, motion));
            shell.OperationStatus = EditorLocalization.Text("动作已载入：") + Path.GetFileName(path);
            committed = true;
        });
        return committed;
    }

    private async Task OpenProjectAsync()
    {
        if (!await CheckUnsavedAsync() || _shell is not { } shell) return;
        string extension = shell.Workspace.ProjectFormat.Extension;
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = EditorLocalization.Text("打开工程"), AllowMultiple = false,
            FileTypeFilter = [new(EditorLocalization.Text("MDA / MMD 工程")) { Patterns = ["*" + extension, "*.pmm"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        await OpenProjectPathAsync(path);
    }

    private async Task<bool> OpenProjectPathAsync(string path)
    {
        if (_shell is not { } shell) return false;
        if (Path.GetExtension(path).Equals(".pmm", StringComparison.OrdinalIgnoreCase)) return await OpenPmmAsync(path);
        bool committed = false;
        await ExecuteAsync(EditorLocalization.Text("正在打开工程…"), async token =>
        {
            await Task.Run(() => shell.Workspace.Open(path, token), token);
            shell.CurrentFrame = (int)shell.Workspace.Session!.CurrentFrame;
            shell.OperationStatus = EditorLocalization.Text("工程已打开。");
            committed = true;
        });
        return committed;
    }

    private async Task SaveProjectAsync(bool saveAs)
    {
        if (_shell is not { HasProject: true } shell || !CommitPendingInlineLighting() || !await CheckPoseDraftsAsync() || !await EnsureMotionHeadersAsync()) return;
        string? path = saveAs ? null : shell.Workspace.ProjectPath;
        if (path is null)
        {
            string extension = shell.Workspace.ProjectFormat.Extension;
            var file = await StorageProvider.SaveFilePickerAsync(new() { Title = EditorLocalization.Text("保存工程"), DefaultExtension = extension[1..],
                SuggestedFileName = Path.ChangeExtension(EditorLocalization.Text("未命名工程.pma"), extension), ShowOverwritePrompt = true,
                FileTypeChoices = [new(EditorLocalization.Text("MDA 便携工程")) { Patterns = ["*" + extension] }] });
            path = file?.TryGetLocalPath();
        }
        if (path is null) return;
        await ExecuteAsync(EditorLocalization.Text("正在保存工程…"), async token =>
        {
            try
            {
                await Task.Run(() => shell.Workspace.Save(path, token), token);
                shell.OperationStatus = EditorLocalization.Text("工程已保存。");
            }
            catch (PmaSaveCommittedException error)
            {
                shell.OperationStatus = EditorLocalization.Format("Mda.Text.Project.SavedCleanupFailed", error.InnerException?.Message ?? error.Message);
            }
        });
    }

    private async Task NewProjectAsync()
    {
        if (!await CheckUnsavedAsync() || _shell is null) return;
        _shell.Workspace.New(); _shell.CurrentFrame = 0; _shell.OperationStatus = EditorLocalization.Text("新建空工程。");
    }

    private async Task<bool> CheckUnsavedAsync()
    {
        if (!CommitPendingInlineLighting() || !await CheckPoseDraftsAsync()) return false;
        if (_shell?.Workspace.IsDirty != true) return true;
        int answer = await EditorDialogs.ChooseAsync(this, EditorLocalization.Text("保存更改"), EditorLocalization.Text("当前工程有尚未保存的更改。"), [EditorLocalization.Text("取消"), EditorLocalization.Text("不保存"), EditorLocalization.Text("保存")], primary: 2);
        if (answer == 0) return false;
        if (answer == 1) return true;
        await SaveProjectAsync(false);
        return !_shell.Workspace.IsDirty;
    }

    private async void CheckClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_closingConfirmed || _shell is null) return;
        if (_shell.IsBusy || IsFileDropInProgress) { args.Cancel = true; _shell.OperationStatus = EditorLocalization.Text("操作进行中，请先取消或等待完成。"); return; }
        if (!CommitPendingInlineLighting()) { args.Cancel = true; return; }
        if (!_shell.Workspace.IsDirty && !_shell.Animation.HasAnyDraft && !_shell.SceneInspector.HasCameraDraft && !_shell.SceneInspector.HasLightDraft && !EditorNumericUpDown.HasInvalidInputs(this)
            && recoveryOwners.Count == 0 && reviewedRecoverySessions.Count == 0 && !_shell.HasAudioPlaybackOwner && Plugins is null) return;
        args.Cancel = true;
        if (_closingPrompt) return;
        _closingPrompt = true;
        try
        {
            if (await CheckUnsavedAsync())
            {
                await _shell.ReleaseAudioPlaybackAsync();
                await ReleasePluginsAsync();
                await CompleteRecoveryShutdownAsync(); _closingConfirmed = true; Close();
            }
        }
        catch (Exception error) { await ShowErrorAsync(error); }
        finally { _closingPrompt = false; }
    }

    private Task ExecuteAsync(string status, Func<CancellationToken, Task> operation, Window? errorOwner = null)
        => ExecuteCoreAsync(status, operation, errorOwner, null);

    private async Task ExecuteCoreAsync(string status, Func<CancellationToken, Task> operation, Window? errorOwner,
        Action<Exception>? failureHandler)
    {
        if (_shell is not { } shell) return;
        _operationCancellation = new();
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        bool wasSuspended = viewport.IsSuspended;
        viewport.IsSuspended = true;
        _waitingForOperationFrame = true;
        RefreshLoadingState();
        lightController?.CancelDrag(); boneController?.CancelDrag();
        try { await shell.RunOperationAsync(status, () => operation(_operationCancellation.Token)); }
        catch (Exception error)
        {
            if (failureHandler is null) await ShowErrorAsync(error, errorOwner);
            else { shell.ReportActivityFailure(error); failureHandler(error); }
        }
        finally
        {
            _operationCancellation.Dispose(); _operationCancellation = null;
            viewport.IsSuspended = wasSuspended;
            _waitingForOperationFrame = true;
            viewport.PrepareNextFrame();
            RefreshViewport(this, EventArgs.Empty); RefreshLoadingState();
        }
    }

    private void CancelOperation(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (_operationCancellation is not null && _shell?.RequestOperationCancellation() == true)
            _operationCancellation.Cancel();
    }
    private void OnEditorKeyDown(object? sender, KeyEventArgs args)
    {
        if (ExecuteEditorKey(args.Key, args.KeyModifiers, false)) args.Handled = true;
    }
    private void OnViewportKeyPressed(object? sender, EditorViewportKeyEventArgs args)
    {
        if (ExecuteEditorKey(args.Key, args.Modifiers, true)) args.Handled = true;
    }
    private bool TextInputHasFocus() => FocusManager?.GetFocusedElement() is TextBox or NumericUpDown or ComboBox or ListBox
        or ListBoxItem or TreeView or TreeViewItem or Button or ToggleButton or Slider
        || (FocusManager?.GetFocusedElement() as Avalonia.Visual)?.FindAncestorOfType<TextBox>() is not null;
    private bool ExecuteEditorKey(Key key, KeyModifiers modifiers, bool fromViewport)
    {
        if (_shell is not { } shell || shell.IsBusy || modifiers.HasFlag(KeyModifiers.Alt)) return false;
        if (fromViewport && (boneController?.IsDragging == true || lightController?.IsDragging == true)) return false;
        if (fromViewport && key == Key.Delete && modifiers == KeyModifiers.None && lightController?.SelectedLightId is not null)
        { DeleteViewportLight(this, new Avalonia.Interactivity.RoutedEventArgs()); return true; }
        bool control = modifiers.HasFlag(KeyModifiers.Control);
        if (key == Key.E && control && modifiers.HasFlag(KeyModifiers.Shift)
            && shell.Workspace.RenderEngine is EditorClassicRenderEngine)
        { ToggleClassicEffects(this, new Avalonia.Interactivity.RoutedEventArgs()); return true; }
        // Saving needs a complete light draft. History navigation keeps unfinished modern
        // light input through the inspector's pending-input projection instead of first
        // committing a new transaction (or blocking Undo on a half-typed colour).
        bool commitLightInput = key == Key.S || (key is Key.Z or Key.Y && !shell.IsModernLightingPage);
        if (control && commitLightInput && !CommitPendingInlineLighting()) return true;
        System.Windows.Input.ICommand? command = (key, control, modifiers.HasFlag(KeyModifiers.Shift)) switch
        {
            (Key.O, true, _) => shell.OpenProjectCommand, (Key.N, true, _) => shell.NewProjectCommand,
            (Key.S, true, true) => shell.SaveAsCommand, (Key.S, true, false) => shell.SaveProjectCommand,
            (Key.Z, true, true) => shell.RedoCommand, (Key.Z, true, false) => shell.UndoCommand,
            (Key.Y, true, _) => shell.RedoCommand,
            (Key.Enter or Key.K, false, false) when fromViewport => shell.Animation.RegisterKeyframeCommand,
            // Editing keys never act while a text field or list owns the keyboard (they keep their own copy/paste/space).
            (Key.Space, false, false) when !TextInputHasFocus() => shell.PlayCommand,
            (Key.C, true, false) when !TextInputHasFocus() => shell.Animation.CopyKeyframesCommand,
            (Key.V, true, false) when !TextInputHasFocus() => shell.Animation.PasteKeyframesCommand,
            (Key.Delete, false, false) when !TextInputHasFocus() => shell.Animation.DeleteKeyframesCommand,
            _ => null
        };
        if (command?.CanExecute(null) != true) return false;
        command.Execute(null); return true;
    }
}
