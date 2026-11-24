using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>
/// Presentation state for the empty editor. Camera and light are real clear-only scene
/// state; editing and output remain unavailable until the application host connects them.
/// </summary>
public sealed partial class EditorShellViewModel : INotifyPropertyChanged
{
    private int _hierarchyTab;
    private int _sceneSelectionIndex = -1;
    private int _selectedThemeIndex;
    private bool _showWorldGuides = true;
    private int _currentFrame;
    private double _pixelsPerFrame = Controls.EditorTimeline.DefaultPixelsPerFrame;
    private double _firstVisibleFrame;
    private bool refreshingLocalization;
    private readonly SceneProjectSession? _projectSession;

    public EditorShellViewModel() : this(new PmmCameraPose(new Vector3(0, 10, 0), Vector3.Zero, -45, 30, true)) { }

    /// <summary>Keep the authored MMD pose; SceneCamera.Target is a view direction, not its observation center.</summary>
    public EditorShellViewModel(PmmCameraPose cameraPose) : this(cameraPose.CreateCamera()) => CameraPose = cameraPose;

    internal EditorShellViewModel(PmmCameraPose cameraPose, IEditorRenderEngine renderEngine)
        : this(cameraPose.CreateCamera(), null, renderEngine) => CameraPose = cameraPose;

    /// <summary>The inspector and world guides share the same immutable camera.</summary>
    public EditorShellViewModel(SceneCamera camera, SceneProjectSession? projectSession = null)
        : this(camera, projectSession, EditorModernRenderEngine.Instance) { }

    internal EditorShellViewModel(SceneCamera camera, SceneProjectSession? projectSession, IEditorRenderEngine renderEngine)
    {
        Workspace = new(renderEngine);
        _projectSession = projectSession;
        Scene = SceneSnapshot.CreateEmpty(camera);
        SelectAnimationContextCommand = new PresentationCommand(() => SceneSelectionIndex = -1);
        GoToStartCommand = new PresentationCommand(() => { FirstVisibleFrame = 0; CurrentFrame = 0; });
        PreviousFrameCommand = new PresentationCommand(() => MoveFrame(-1));
        NextFrameCommand = new PresentationCommand(() => MoveFrame(1));
        StepBackCommand = new PresentationCommand(() => MoveFrame(-10));
        StepForwardCommand = new PresentationCommand(() => MoveFrame(10));
        InitializeWorkspace();
        Observe(this, static model => model.RefreshLocalization());
    }

    public SceneSnapshot Scene { get; }
    public SceneCamera Camera => Scene.Camera;
    public OutputSettingsDraft? OutputSettings { get; private set; }
    /// <summary>The host owns the session; output reads its current authored range when opening a draft.</summary>
    public OutputSettingsViewModel CreateOutputSettingsViewModel()
    {
        var project = Workspace.Session ?? _projectSession;
        return new(OutputSettings, (uint)CurrentFrame, EditorExportRange.GetLastKeyframe(project),
            project is not null && EditorExportService.HasPaletteReceiver(project),
            defaultQuality: EditorRenderEngineOptions.DefaultExportQuality(Workspace.RenderEngine),
            supportsCaptureMemory: EditorRenderEngineOptions.UsesModernQualityPreferences(Workspace.RenderEngine));
    }
    public void ApplyOutputSettings(OutputSettingsDraft settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        OutputSettings = settings;
        OnPropertyChanged(nameof(OutputSettings));
        OnPropertyChanged(nameof(Status));
    }
    public PmmCameraPose? CameraPose { get; }
    public bool HasCameraPose => CameraPose.HasValue;
    public bool HasNoCameraPose => !HasCameraPose;
    public ICommand SelectAnimationContextCommand { get; }
    public ICommand GoToStartCommand { get; }
    public ICommand PreviousFrameCommand { get; }
    public ICommand NextFrameCommand { get; }
    public ICommand StepBackCommand { get; }
    public ICommand StepForwardCommand { get; }
    public int CurrentFrame
    {
        get => _currentFrame;
        set
        {
            value = Math.Clamp(value, 0, int.MaxValue);
            if (_currentFrame == value) return;
            _currentFrame = value;
            OnFrameChanged();
            OnPropertyChanged();
        }
    }
    public double PixelsPerFrame
    {
        get => _pixelsPerFrame;
        set
        {
            if (!double.IsFinite(value)) return;
            value = Math.Clamp(value, .5, 64);
            if (_pixelsPerFrame == value) return;
            _pixelsPerFrame = value;
            OnPropertyChanged();
        }
    }
    public double FirstVisibleFrame
    {
        get => _firstVisibleFrame;
        set
        {
            if (!double.IsFinite(value)) return;
            value = Math.Clamp(value, 0, int.MaxValue);
            if (_firstVisibleFrame == value) return;
            _firstVisibleFrame = value;
            OnPropertyChanged();
        }
    }
    public bool IsBoneInspector => HierarchyTab == 0;
    public bool IsMorphInspector => HierarchyTab == 1;
    public string CameraProjection => Text(Camera.VerticalFovDegrees.HasValue ? "透视" : "正交");
    public bool IsPerspective => Camera.VerticalFovDegrees.HasValue;
    public bool IsOrthographic => !IsPerspective;
    public string CameraCenterX => PoseNumber(CameraPose?.LookAt.X);
    public string CameraCenterY => PoseNumber(CameraPose?.LookAt.Y);
    public string CameraCenterZ => PoseNumber(CameraPose?.LookAt.Z);
    public string CameraRotationX => Degrees(CameraPose?.Angle.X);
    public string CameraRotationY => Degrees(CameraPose?.Angle.Y);
    public string CameraRotationZ => Degrees(CameraPose?.Angle.Z);
    public string CameraDistance => PoseNumber(CameraPose?.Distance);
    public string CameraOrthographicHeight => Number(Camera.OrthographicHeight);
    public string CameraFov => Number(Camera.VerticalFovDegrees ?? 0) + "°";
    public string LightDirectionX => Number(Scene.LightDirection.X);
    public string LightDirectionY => Number(Scene.LightDirection.Y);
    public string LightDirectionZ => Number(Scene.LightDirection.Z);

    public bool ShowWorldGuides
    {
        get => _showWorldGuides;
        set
        {
            if (_showWorldGuides == value) return;
            _showWorldGuides = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WorldGuidesStatus));
        }
    }

    public string WorldGuidesStatus => Text(ShowWorldGuides ? "网格与 XYZ 轴已显示" : "网格与 XYZ 轴已隐藏");
    public IReadOnlyList<string> Themes => [Text("黑"), Text("白")];
    public string ThemeLabel => Text("主题：") + Themes[SelectedThemeIndex];

    public int SelectedThemeIndex
    {
        get => _selectedThemeIndex;
        set
        {
            if (refreshingLocalization || value is < 0 or > 1 || _selectedThemeIndex == value) return;
            _selectedThemeIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThemeLabel));
        }
    }

    public string ProjectTitle => (Workspace.ProjectPath is { } path ? System.IO.Path.GetFileName(path) : Text("未命名工程")) + (Workspace.IsDirty ? " *" : "");
    public string Status => OperationStatus;
    public string UnavailableReason => Text("请先载入模型并选择要操作的对象。");
    public string AnimationSelectionLabel => Animation.SelectionLabel;
    public string AnimationInspectorPrompt => Text(HierarchyTab == 0 ? "未选择模型或骨骼" : "未选择模型或表情");
    public string AnimationWorkflowHint => Text(HierarchyTab == 0 ? "选择骨骼，调整姿态后在当前帧登记。" : "选择表情，调整权重后在当前帧登记。");
    public string InspectorTitle => Text(SceneSelectionIndex switch { 0 => "世界", 1 => "相机", 2 => "主光", _ => "动画属性" });
    public bool IsAnimationInspector => SceneSelectionIndex == -1;
    public bool IsSceneInspector => !IsAnimationInspector;
    public bool IsWorldInspector => SceneSelectionIndex == 0;
    public bool IsCameraInspector => SceneSelectionIndex == 1;
    public bool IsLightInspector => SceneSelectionIndex == 2;

    /// <summary>World, camera and main light are clear-only state, not imported models.</summary>
    public int SceneSelectionIndex
    {
        get => _sceneSelectionIndex;
        set
        {
            if (refreshingLocalization || value is < -1 or > 2 || _sceneSelectionIndex == value) return;
            _sceneSelectionIndex = value;
            SceneInspector.SceneSelectionIndex = value;
            // World/camera/main-light is not an authored animation channel: keep Follow selection quiet
            // instead of presenting retained model rows as scene tracks. Actor and drafts are untouched.
            Animation.SetAnimationContextActive(value == -1);
            OnPropertyChanged();
            OnPropertyChanged(nameof(InspectorTitle));
            OnPropertyChanged(nameof(IsAnimationInspector));
            OnPropertyChanged(nameof(IsSceneInspector));
            OnPropertyChanged(nameof(IsWorldInspector));
            OnPropertyChanged(nameof(IsCameraInspector));
            OnPropertyChanged(nameof(IsLightInspector));
        }
    }

    public int HierarchyTab
    {
        get => _hierarchyTab;
        set
        {
            if (refreshingLocalization || value is < 0 or > 1 || _hierarchyTab == value) return;
            _hierarchyTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AnimationSelectionLabel));
            OnPropertyChanged(nameof(AnimationInspectorPrompt));
            OnPropertyChanged(nameof(AnimationWorkflowHint));
            OnPropertyChanged(nameof(IsBoneInspector));
            OnPropertyChanged(nameof(IsMorphInspector));
            SceneSelectionIndex = -1;
        }
    }

    private void RefreshLocalization()
    {
        refreshingLocalization = true;
        try { OnPropertyChanged(null); }
        finally { refreshingLocalization = false; }
        OnPropertyChanged(nameof(SelectedThemeIndex));
        OnPropertyChanged(nameof(HierarchyTab));
        OnPropertyChanged(nameof(SceneSelectionIndex));
    }

    private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private void MoveFrame(int delta) => CurrentFrame = (int)Math.Clamp((long)CurrentFrame + delta, 0, int.MaxValue);
    private static string PoseNumber(float? value) => value.HasValue ? Number(value.Value) : "—";
    private static string Degrees(float? radians) => radians.HasValue ? Number(radians.Value * (180 / MathF.PI)) + "°" : "—";
    private sealed class PresentationCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
