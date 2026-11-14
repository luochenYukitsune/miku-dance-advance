using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

/// <summary>Independent PMX real-time frame viewer. The legacy PMD preview stays on the default app route.</summary>
public sealed partial class FrameViewportWindow : Window
{
    public D3D12ViewportHost Viewport { get; }
    public FrameViewportViewModel ViewModel { get; }
    public bool DisposeViewModelOnClose { get; set; } = true;
    public FrameViewportWindow(FrameViewportViewModel viewModel)
    {
        ViewModel = viewModel; DataContext = viewModel;
        Viewport = new() { ViewModel = viewModel };
        if(viewModel.IsEngineDemo){BuildDemoView();AttachViewportLifecycle();return;}
        Title = "MMD Modern · D3D12 完整帧实时视口";
        Width = 1180; Height = 860; MinWidth = 800; MinHeight = 580;
        var layout = new Grid { RowDefinitions = new("Auto,*,Auto"), ColumnDefinitions = new("260,*"), Margin = new(16) };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new(0, 0, 0, 14) };
        heading.Children.Add(new TextBlock { Text = "MMD Modern  /  D3D12 实时角色视口", FontSize = 23 });
        var diagnostics = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        diagnostics.Bind(TextBlock.TextProperty, new Binding(nameof(viewModel.RenderDiagnostics))); heading.Children.Add(diagnostics);
        Grid.SetColumnSpan(heading, 2); layout.Children.Add(heading);
        var tools = new StackPanel { Spacing = 10, Margin = new(0, 0, 16, 0) };
        var toolScroll = new ScrollViewer { Content = tools, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(toolScroll, 1); layout.Children.Add(toolScroll);
        var open = new Button { Content = "打开 PMX 模型…", HorizontalAlignment = HorizontalAlignment.Stretch };
        open.Click += async (_, _) => await OpenModel(); tools.Children.Add(open);
        var openProject=new Button{Name="OpenPmmProject",Content="打开 PMM 工程…",HorizontalAlignment=HorizontalAlignment.Stretch};
        openProject.Click+=async(_,_)=>await OpenProject();tools.Children.Add(openProject);
        tools.Children.Add(new Expander{Name="SceneObjectEditor",Header="角色与场景 · 追加／选择／移动",IsExpanded=true,
            Content=SceneObjectControls.Create(this,viewModel),HorizontalAlignment=HorizontalAlignment.Stretch});
        var projectCamera=new CheckBox{Name="PmmProjectCamera",Content="使用工程镜头"};
        projectCamera.Bind(ToggleButton.IsCheckedProperty,new Binding(nameof(viewModel.ProjectCameraEnabled)){Mode=BindingMode.TwoWay});
        projectCamera.Bind(IsEnabledProperty,new Binding(nameof(viewModel.HasProject)));tools.Children.Add(projectCamera);
        var summary = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 };
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(viewModel.ModelSummary))); tools.Children.Add(summary);
        tools.Children.Add(Animation.FrameViewportAnimationControls.Create(this,viewModel));
        tools.Children.Add(new Expander{Header="场景用途与原生光源",Content=NativeLightControls.Create(this,viewModel)});
        tools.Children.Add(new TextBlock { Text = "场景", FontSize = 12 });
        var scenes = new ComboBox { ItemsSource = new[] { "空背景", "光追演示房间" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        scenes.Bind(IsEnabledProperty,new Binding(nameof(viewModel.AnimationSceneSelectionAvailable)));
        scenes.Bind(SelectingItemsControl.SelectedIndexProperty, new Binding(nameof(viewModel.ScenePresetIndex)) { Mode = BindingMode.TwoWay });
        scenes.SelectionChanged += (_, _) => Viewport.Restart(); tools.Children.Add(scenes);
        tools.Children.Add(new TextBlock { Text = "房间发光色块（实验，0–1）", FontSize = 12 });
        var emission = new NumericUpDown { Minimum = 0, Maximum = 1, Increment = .05m,
            FormatString = "0.00", HorizontalAlignment = HorizontalAlignment.Stretch };
        emission.Bind(NumericUpDown.ValueProperty, new Binding(nameof(viewModel.RoomEmissionStrength)) { Mode = BindingMode.TwoWay });
        emission.Bind(IsEnabledProperty, new Binding(nameof(viewModel.RoomEnabled)));
        ToolTip.SetTip(emission, "0 为关闭，建议 0.15 起。增加少量暖／冷发光面板；开启 GI 后可照亮附近表面。请关闭实验近场遮蔽以保持面板亮度。");
        emission.ValueChanged += (_, _) => Viewport.Restart();
        tools.Children.Add(emission);
        AddSlider(tools, "相机水平角度", viewModel, nameof(viewModel.Yaw), viewModel.Yaw, -180, 180);
        AddSlider(tools, "相机俯仰角度", viewModel, nameof(viewModel.Pitch), viewModel.Pitch, -85, 85);
        AddSlider(tools, "相机缩放", viewModel, nameof(viewModel.Zoom), viewModel.Zoom, .15, 4);
        var resetCamera = new Button { Content = "重置相机", HorizontalAlignment = HorizontalAlignment.Stretch };
        resetCamera.Click += (_, _) => { viewModel.ResetCamera(); Viewport.Restart(); }; tools.Children.Add(resetCamera);
        AddToggle(tools, "描边", nameof(viewModel.OutlineEnabled));
        AddToggle(tools, "自阴影（所选光矩阵）", nameof(viewModel.SelfShadowEnabled));
        var shadowFit = new CheckBox { Content = "完整自阴影覆盖（测试）" };
        ToolTip.SetTip(shadowFit, "现代自动覆盖角色及房间全部引用几何；并非原版宿主矩阵复刻。仅自阴影开启时生效；不代表 GI 凹缝泛白已修复。");
        shadowFit.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(viewModel.ShadowFitEnabled)) { Mode = BindingMode.TwoWay });
        tools.Children.Add(shadowFit);
        AddToggle(tools, "地面投影", nameof(viewModel.GroundShadowEnabled));
        AddToggle(tools, "透明诊断（alpha=0.5）", nameof(viewModel.TransparencyDiagnostic));
        tools.Children.Add(new TextBlock { Text = "抗锯齿（默认 Off）", FontSize = 14 });
        var antialiasing = new ComboBox { Name = "FrameAntialiasing", ItemsSource = new[] { "Off", "SMAA1x" },
            HorizontalAlignment = HorizontalAlignment.Stretch };
        ToolTip.SetTip(antialiasing, "平滑当前帧的色块与描边边缘；独立于 GI，可在 GI 关闭时使用。");
        antialiasing.Bind(SelectingItemsControl.SelectedIndexProperty, new Binding(nameof(viewModel.AntialiasingIndex)) { Mode = BindingMode.TwoWay });
        antialiasing.SelectionChanged += (_, _) => Viewport.Restart();
        tools.Children.Add(antialiasing);
        tools.Children.Add(new TextBlock { Text = "间接光 GI（启动默认 Off）", FontSize = 14 });
        var softwareItem = new ComboBoxItem { Content = "GPU 软件 BVH", IsEnabled = viewModel.SoftwareGiAvailable };
        var hardwareItem = new ComboBoxItem { Content = "DXR 硬件 GI", IsEnabled = viewModel.HardwareGiAvailable };
        var giModes = new ComboBox { ItemsSource = new[] { new ComboBoxItem { Content = "Off · 原版光照" }, softwareItem, hardwareItem }, SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        bool updatingGiSelection = false;
        static int ModeIndex(LightingMode mode) => mode switch { LightingMode.SoftwareRayTracing => 1, LightingMode.HardwareRayTracing => 2, _ => 0 };
        giModes.SelectedIndex = ModeIndex(viewModel.GiMode);
        giModes.SelectionChanged += (_, _) =>
        {
            if (updatingGiSelection) return;
            var mode = giModes.SelectedIndex switch { 1 => LightingMode.SoftwareRayTracing, 2 => LightingMode.HardwareRayTracing, _ => LightingMode.Legacy };
            if (!viewModel.TrySelectGiMode(mode))
            { updatingGiSelection = true; giModes.SelectedIndex = ModeIndex(viewModel.GiMode); updatingGiSelection = false; }
        };
        System.ComponentModel.PropertyChangedEventHandler updateGi = (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.GiMode))
            { updatingGiSelection = true; giModes.SelectedIndex = ModeIndex(viewModel.GiMode); updatingGiSelection = false; Viewport.Restart(); }
            if (args.PropertyName is nameof(viewModel.SoftwareGiAvailable) or nameof(viewModel.HardwareGiAvailable))
            { softwareItem.IsEnabled = viewModel.SoftwareGiAvailable; hardwareItem.IsEnabled = viewModel.HardwareGiAvailable; }
        };
        viewModel.PropertyChanged += updateGi;
        Closed += (_, _) => viewModel.PropertyChanged -= updateGi;
        tools.Children.Add(giModes);
        tools.Children.Add(new TextBlock { Text = "间接光合成", FontSize = 12 });
        var composition = new ComboBox { ItemsSource = new[] { "旧版叠加", "三渲二受限反弹（测试）" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        ToolTip.SetTip(composition, "仅 GI 开启时生效；手动切换对照。三渲二受限反弹仍在测试中，不会自动开启近场遮蔽或完整自阴影覆盖。");
        composition.Bind(SelectingItemsControl.SelectedIndexProperty, new Binding(nameof(viewModel.GiCompositionIndex)) { Mode = BindingMode.TwoWay });
        composition.SelectionChanged += (_, _) => Viewport.Restart();
        tools.Children.Add(composition);
        var giDenoise = new CheckBox { Content = "补光降噪（保边空间）" };
        ToolTip.SetTip(giDenoise, "仅在三渲二受限反弹中平滑 GI 补光，保留原有色块和描边；只使用当前帧，不含时域累积，也不消除锯齿。GI 开启且强度大于 0 时生效。");
        giDenoise.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(viewModel.GiDenoiseEnabled)) { Mode = BindingMode.TwoWay });
        giDenoise.Bind(IsEnabledProperty, new Binding(nameof(viewModel.GiDenoiseAvailable)));
        giDenoise.PropertyChanged += (_, args) => { if (args.Property == ToggleButton.IsCheckedProperty) Viewport.Restart(); };
        tools.Children.Add(giDenoise);
        var giStrength = AddSlider(tools, "GI 强度（0–12；手动开启默认 0.5）", viewModel, nameof(viewModel.GiStrength), viewModel.GiStrength, 0, 12);
        giStrength.SmallChange = .1; giStrength.LargeChange = .5;
        giStrength.TickFrequency = .1; giStrength.IsSnapToTickEnabled = true;
        var quality = new ComboBox { ItemsSource = new[] { "低 · 4 SPP（交互）", "中 · 16 SPP", "高 · 64 SPP（高开销）" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        quality.Bind(SelectingItemsControl.SelectedIndexProperty, new Binding(nameof(viewModel.GiQualityIndex)) { Mode = BindingMode.TwoWay });
        tools.Children.Add(quality);
        var nearOcclusion = new CheckBox { Content = "近场遮蔽（实验，默认关闭）" };
        ToolTip.SetTip(nearOcclusion, "连续压暗可能改变三渲二色块；仅 GI 开启时生效。");
        nearOcclusion.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(viewModel.NearOcclusionEnabled)) { Mode = BindingMode.TwoWay });
        tools.Children.Add(nearOcclusion);
        var giAvailability = new TextBlock { FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        giAvailability.Bind(TextBlock.TextProperty, new Binding(nameof(viewModel.GiAvailabilityDetails))); tools.Children.Add(giAvailability);
        var giExecution = new TextBlock { FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        giExecution.Bind(TextBlock.TextProperty, new Binding(nameof(viewModel.GiExecutionDiagnostics))); tools.Children.Add(giExecution);
        var capture = new Button { Content = "保存 PNG 截图…", HorizontalAlignment = HorizontalAlignment.Stretch };
        capture.Click += async (_, _) => await SaveCapture(); tools.Children.Add(capture);
        tools.Children.Add(new TextBlock { Text = "默认 GI / 光追关闭。\n现代渲染不支持 MME；旧兼容引擎另行提供。\n通道是独立实现，尚无原 EXE 整帧像素对照。", FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = .7 });
        Grid.SetRow(Viewport, 1); Grid.SetColumn(Viewport, 1); layout.Children.Add(Viewport);
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new(0, 12, 0, 0), FontSize = 12 };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(viewModel.Status)));
        Grid.SetRow(status, 2); Grid.SetColumnSpan(status, 2); layout.Children.Add(status);
        Content = layout;
        AttachViewportLifecycle();
    }
    private void AttachViewportLifecycle()
    {
        var viewModel=ViewModel;
        Viewport.Failed += error => viewModel.Status = $"D3D12 视口失败：{error.Message}";
        var presentIntervals = new Queue<double>(); double presentIntervalSum = 0;
        Viewport.FramePresented += (statistics, elapsed) =>
        {
            if (statistics.Timing?.InterPresentMilliseconds is > 0 and var interval)
            {
                presentIntervals.Enqueue(interval); presentIntervalSum += interval;
                if (presentIntervals.Count > 60) presentIntervalSum -= presentIntervals.Dequeue();
            }
            if (statistics.PresentCount == 1 || statistics.PresentCount % 15 == 0)
                viewModel.RenderDiagnostics = $"D3D12 · {Viewport.AdapterName} · 实际 Present {(presentIntervalSum > 0 ? presentIntervals.Count * 1000 / presentIntervalSum : 0):F1} FPS · 间隔 {statistics.Timing?.InterPresentMilliseconds:F1} ms · CPU 录制 {statistics.Frame?.RecordCpuMilliseconds:F1} ms · 读回/帧 {statistics.Frame?.ReadbackCount} · 实际 AA {statistics.Frame?.Antialiasing} / {statistics.Frame?.AntialiasingPassDrawCalls} 次绘制";
            if (statistics.PresentCount % 60 == 0)
                viewModel.Status = $"{Viewport.AdapterName} · Present {statistics.PresentCount:N0} · {statistics.Width}×{statistics.Height} · 单帧读回 {statistics.Frame?.ReadbackCount} · 实际 AA {statistics.Frame?.Antialiasing} / {statistics.Frame?.AntialiasingPassDrawCalls} 次绘制 · GI {viewModel.GiMode} · 补光降噪 {statistics.RayGi?.Denoiser ?? RayGiDenoiser.Off}";
        };
        PropertyChanged += (_, args) => { if (args.Property == WindowStateProperty) Viewport.Suspend(WindowState == WindowState.Minimized); };
        Closed += (_, _) => { Viewport.Dispose(); if (DisposeViewModelOnClose) viewModel.Dispose(); };
    }
    private static Slider AddSlider(StackPanel panel, string title, FrameViewportViewModel source, string property, double initialValue, double min, double max)
    {
        panel.Children.Add(new TextBlock { Text = title, FontSize = 12 });
        var slider = new Slider { Minimum = min, Maximum = max, Value = initialValue };
        slider.Bind(RangeBase.ValueProperty, new Binding(property) { Source = source, Mode = BindingMode.TwoWay }); panel.Children.Add(slider);
        return slider;
    }
    private static void AddToggle(StackPanel panel, string title, string property)
    {
        var checkbox = new CheckBox { Content = title };
        checkbox.Bind(ToggleButton.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay }); panel.Children.Add(checkbox);
    }
    private async Task OpenModel()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "选择静态 PMX 模型", AllowMultiple = false,
            FileTypeFilter = [new("PMX 模型") { Patterns = ["*.pmx"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        Viewport.Suspend(true);
        try
        {
            ViewModel.Status="正在读取 PMX 与纹理…";
            using var candidate=await Task.Run(()=>ViewModel.PrepareModel(path));
            ViewModel.CommitModel(candidate);
            Viewport.Restart();
        }
        catch(Exception error){ViewModel.Status=$"模型加载失败，保留当前场景：{error.Message}";}
        finally{Viewport.Suspend(false);}
    }
    private async Task OpenProject()
    {
        var files=await StorageProvider.OpenFilePickerAsync(new(){Title="选择MMD完整工程",AllowMultiple=false,FileTypeFilter=[new("PMM工程"){Patterns=["*.pmm"]}]});
        if(files.FirstOrDefault()?.TryGetLocalPath() is not {} path)return;
        try{ViewModel.Status="正在读取工程、模型和纹理…";using var candidate=await Task.Run(()=>ViewModel.PrepareProject(path));ViewModel.CommitProject(candidate);Viewport.Restart();}
        catch(Exception error){ViewModel.Status=$"工程导入失败，保留当前场景：{error.Message}";}
    }
    private async Task SaveCapture()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "保存显式 GPU 截图", SuggestedFileName = "frame.png",
            FileTypeChoices = [new("PNG 图像") { Patterns = ["*.png"] }] });
        if (file?.TryGetLocalPath() is not { } path) return;
        try { FrameViewportCapture.SavePng(Viewport.Capture(), path); ViewModel.Status = "已保存截图；仅本次请求执行一次 GPU 读回。"; }
        catch (Exception error) { ViewModel.Status = $"截图失败：{error.Message}"; }
    }
}
