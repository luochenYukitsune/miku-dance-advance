using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    private bool dockReady, dockRefreshPending;
    private bool animationDockExpanded = true;
    private VmdMotion? inlineSource;
    private Guid? inlineActor;
    private uint? inlineCameraFrame;
    private KeyframeAddress[] inlineBones = [];

    private void InitializeAnimationDock()
    {
        dockReady = true;
        InlineCurveEditor.DataContext = null;
        InlineCurveEditor.IsEnabled = false;
        InlineCurveGraph.EditCompleted += CommitInlineCurve;
        RefreshOutlineFilterLabels();
        EditorLocalization.Observe(this, static window => window.RefreshOutlineFilterLabels());
        RefreshAnimationDockLayout();
    }

    private void ToggleDockFromTransport(object? sender, RoutedEventArgs args)
        => SetAnimationDockVisible(AnimationDockToggle.IsChecked == true);

    private void SetAnimationDockVisible(bool visible)
    {
        AnimationDockToggle.IsChecked = visible;
        animationDockExpanded = visible;
        ShowLeftRailMenuItem.IsChecked = visible;
        RefreshDockContext();
    }

    private void ToggleOutline(object? sender, RoutedEventArgs args)
        => SetOutlineVisible(OutlineToggle.IsChecked == true);

    private void SetOutlineVisible(bool visible)
    {
        var rows = RightDockRows.RowDefinitions;
        if (!visible && OutlineScroll.Bounds.Height > 0) _outlineHeight = OutlineScroll.Bounds.Height;
        OutlineToggle.IsChecked = visible;
        OutlineFilter.IsVisible = visible;
        OutlineScroll.IsVisible = OutlineSplitter.IsVisible = visible;
        rows[0].Height = new GridLength(visible ? _outlineHeight : 0);
        rows[1].Height = new GridLength(visible ? DefaultSplitterSize : 0);
    }

    private void RefreshOutlineFilterLabels()
    {
        int index = Math.Max(0, OutlineFilter.SelectedIndex);
        OutlineFilter.ItemsSource = new[] { EditorLocalization.Text("Mda.Text.Layout.All"), EditorLocalization.Text("Mda.Text.Layout.Models"),
            EditorLocalization.Text("Mda.Text.3b579010a041") };
        OutlineFilter.SelectedIndex = index;
    }

    private void ChangeOutlineFilter(object? sender, SelectionChangedEventArgs? args)
    {
        if (!dockReady) return;
        int filter = OutlineFilter.SelectedIndex;
        ModelBrowserSurface.IsVisible = filter != 2;
        SceneObjects.IsVisible = filter != 1;
        OutlineWorld.IsVisible = OutlineCamera.IsVisible = filter == 0;
        OutlineAdditionalLights.IsVisible = filter != 1 && _shell?.SupportsModernLightingWorkspace == true;
    }

    private void ResizeAnimationDock(object? sender, SizeChangedEventArgs args) => RefreshAnimationDockLayout();
    private void SelectAnimationDockTab(object? sender, SelectionChangedEventArgs args) => RefreshAnimationDockLayout();

    private void RefreshAnimationDockLayout()
    {
        if (!dockReady) return;
        bool hasAnimationContext = _shell is not { IsLightInspector: true } || _shell.SceneInspector.ShowsPrimaryLightTrack;
        AnimationDockToggle.IsVisible = hasAnimationContext;
        AnimationDock.IsVisible = hasAnimationContext && animationDockExpanded;
        bool modelContext = (_shell?.SceneSelectionIndex ?? -1) < 0;
        double available = AnimationDock.Bounds.Width;
        bool compact = available > 0 && available < (modelContext ? 720 : 580);
        bool hasCurves = modelContext || _shell?.IsCameraInspector == true;
        AnimationDockTabs.IsVisible = hasCurves && compact;
        AnimationDock.Height = AnimationDockTabs.IsVisible ? 172 : 168;
        CurveDockDivider.IsVisible = hasCurves && !compact;
        MorphDockDivider.IsVisible = modelContext && !compact;
        MorphDockTab.IsVisible = modelContext;
        if (!modelContext && AnimationDockTabs.SelectedIndex == 1) AnimationDockTabs.SelectedIndex = 2;
        if (AnimationDockTabs.SelectedIndex < 0) AnimationDockTabs.SelectedIndex = 0;
        int selected = AnimationDockTabs.SelectedIndex;
        var columns = AnimationDockColumns.ColumnDefinitions;
        double curveWidth = modelContext ? (available >= 980 ? 160 : 144) : 154;
        double valuesWidth = available >= 980 ? 296 : 276;
        columns[0].Width = compact ? GridLength.Star : new GridLength(curveWidth);
        columns[1].Width = !compact && modelContext ? GridLength.Star : new GridLength(0);
        columns[2].Width = compact ? new GridLength(0) : modelContext ? new GridLength(valuesWidth) : GridLength.Star;
        Grid.SetColumn(MorphDockPanel, compact ? 0 : 1);
        Grid.SetColumn(StandardSelectionInspector, compact ? 0 : 2);
        CurveDockPanel.IsVisible = hasCurves && (!compact || selected == 0);
        MorphDockPanel.IsVisible = modelContext && (!compact || selected == 1);
        StandardSelectionInspector.IsVisible = !hasCurves || !compact || selected == 2;
        if (!hasCurves)
        {
            Grid.SetColumn(StandardSelectionInspector, 0);
            columns[0].Width = GridLength.Star; columns[2].Width = new GridLength(0);
        }
    }

    private void OnCurveSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or nameof(EditorAnimationViewModel.SelectedKeyframes) or nameof(EditorAnimationViewModel.HasHiddenSelectedBoneKeyframes))
            ScheduleDockRefresh();
    }

    private void OnDockSceneTrackChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(EditorSceneInspectorViewModel.ShowsPrimaryLightTrack)) ScheduleDockRefresh();
    }

    private void OnDockWorkspaceChanged(object? sender, EventArgs args) => ScheduleDockRefresh();

    private void ScheduleDockRefresh()
    {
        if (dockRefreshPending) return;
        dockRefreshPending = true;
        Dispatcher.UIThread.Post(() => { dockRefreshPending = false; RefreshDockContext(); });
    }

    private void RefreshDockContext()
    {
        if (!dockReady) return;
        ChangeOutlineFilter(this, null);
        RefreshAnimationDockLayout();
        VmdMotion? source = null;
        Guid? actorId = null;
        uint? cameraFrame = null;
        KeyframeAddress[] keys = [];
        if (AnimationDock.IsVisible && _shell is { IsBusy: false, IsPlaying: false } shell)
        {
            if (shell.SceneSelectionIndex == 1 && shell.Workspace.Session?.CaptureAuthoredContent().SceneMotion is { } scene)
            {
                uint frame = (uint)shell.CurrentFrame;
                if (scene.Motion.CameraKeyframes.Any(key => key.Frame == frame)) { source = scene.Motion; cameraFrame = frame; }
            }
            else if (shell.SceneSelectionIndex < 0 && !shell.Animation.HasHiddenSelectedBoneKeyframes && shell.Browser.SelectedModel is { } selected)
            {
                keys = shell.Animation.SelectedKeyframes.Where(key => key.Kind == KeyframeTrackKind.Bone).ToArray();
                if (keys.Length != 0 && shell.Workspace.Motions.FirstOrDefault(actor => actor.ObjectId == selected.ObjectId) is { } actor)
                { source = actor.Motion; actorId = actor.ObjectId; }
            }
        }
        if (ReferenceEquals(source, inlineSource) && actorId == inlineActor && cameraFrame == inlineCameraFrame && keys.SequenceEqual(inlineBones)) return;
        int channel = Math.Max(0, InlineCurveChannel.SelectedIndex);
        inlineSource = source; inlineActor = actorId; inlineCameraFrame = cameraFrame; inlineBones = keys;
        EditorInterpolationViewModel? editor = source is null ? null : cameraFrame.HasValue
            ? EditorInterpolationViewModel.ForCamera(source, [cameraFrame.Value]) : new(source, keys);
        if (editor is not null) editor.SelectedChannel = editor.Channels[Math.Min(channel, editor.Channels.Count - 1)];
        InlineCurveEditor.DataContext = editor;
        InlineCurveEditor.IsEnabled = editor is not null;
    }

    private void CommitInlineCurve()
    {
        if (_shell is not { IsBusy: false, IsPlaying: false } shell || InlineCurveEditor.DataContext is not EditorInterpolationViewModel editor) return;
        RefreshDockContext();
        if (!ReferenceEquals(editor, InlineCurveEditor.DataContext) || inlineSource is null ||
            !editor.HasChanges || !editor.TryCreateDraft(out var result) || result is null) return;
        if (inlineActor is { } id) shell.Workspace.CommitMotion(id, result.ApplyTo(inlineSource));
        else shell.Workspace.CommitSceneMotion(result.ApplyTo(inlineSource));
        RefreshDockContext();
    }
}
