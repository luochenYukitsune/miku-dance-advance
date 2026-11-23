using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Mmd.Modern.Application.MotionEditing;

namespace Mmd.Modern.App;

/// <summary>A protected, detached multi-channel curve draft; its owner commits one returned edit.</summary>
public partial class EditorInterpolationWindow : Window
{
    public EditorInterpolationWindow() => InitializeComponent();
    public EditorInterpolationWindow(EditorInterpolationViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Opened += (_, _) =>
        {
            RequestedThemeVariant = Owner?.ActualThemeVariant ?? ThemeVariant.Dark;
            if (Owner is { } owner) { FontFamily = owner.FontFamily; Resources["OutputFont"] = owner.FontFamily; }
        };
        KeyDown += (_, args) => { if (!args.Handled && args.Key == Key.Escape) { Close(null); args.Handled = true; } };
    }
    public EditorInterpolationViewModel ViewModel => (EditorInterpolationViewModel)DataContext!;
    private void CancelInterpolation(object? sender, RoutedEventArgs args) => Close(null);
    private void ResetChannel(object? sender, RoutedEventArgs args) => ViewModel.SelectedChannel.Reset();
    private void SetLinear(object? sender, RoutedEventArgs args) => ViewModel.SelectedChannel.SetLinear();
    private void SetEaseInOut(object? sender, RoutedEventArgs args) => ViewModel.SelectedChannel.SetEaseInOut();
    private void SetEaseOut(object? sender, RoutedEventArgs args) => ViewModel.SelectedChannel.SetEaseOut();
    private void SetEaseIn(object? sender, RoutedEventArgs args) => ViewModel.SelectedChannel.SetEaseIn();
    private void ApplyAllChannels(object? sender, RoutedEventArgs args) => ViewModel.ApplySelectedChannelToAll();
    private void ApplyInterpolation(object? sender, RoutedEventArgs args)
    {
        if (this.GetVisualDescendants().OfType<Control>().Any(c => c.IsEffectivelyEnabled && DataValidationErrors.GetHasErrors(c)))
        { ViewModel.SetValidationMessage(EditorLocalization.Text("请先修正标出的控制点数值，再应用曲线。")); return; }
        if (ViewModel.TryCreateDraft(out var draft)) Close(draft);
    }
}

/// <summary>Native normalized VMD curve and two direct-manipulation handles. It does not sample motion.</summary>
public sealed class EditorInterpolationCurve : TemplatedControl
{
    public static readonly StyledProperty<EditorInterpolationChannelViewModel?> ChannelProperty =
        AvaloniaProperty.Register<EditorInterpolationCurve, EditorInterpolationChannelViewModel?>(nameof(Channel));
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<EditorInterpolationCurve, IBrush?>(nameof(GridBrush), new SolidColorBrush(Color.Parse("#46545F")));
    public static readonly StyledProperty<IBrush?> CurveBrushProperty =
        AvaloniaProperty.Register<EditorInterpolationCurve, IBrush?>(nameof(CurveBrush), new SolidColorBrush(Color.Parse("#A8BBC9")));
    public static readonly StyledProperty<IBrush?> Handle1BrushProperty =
        AvaloniaProperty.Register<EditorInterpolationCurve, IBrush?>(nameof(Handle1Brush), new SolidColorBrush(Color.Parse("#2BB673")));
    public static readonly StyledProperty<IBrush?> Handle2BrushProperty =
        AvaloniaProperty.Register<EditorInterpolationCurve, IBrush?>(nameof(Handle2Brush), new SolidColorBrush(Color.Parse("#38BDF8")));

    private IPointer? dragPointer;
    private EditorInterpolationChannelViewModel? observedChannel, dragChannel;
    private VmdBezierControl dragStart;
    internal event Action? EditCompleted;
    public int SelectedHandle { get; private set; } = 1;

    static EditorInterpolationCurve()
    {
        FocusableProperty.OverrideDefaultValue<EditorInterpolationCurve>(true);
        MinWidthProperty.OverrideDefaultValue<EditorInterpolationCurve>(200);
        MinHeightProperty.OverrideDefaultValue<EditorInterpolationCurve>(200);
        AffectsRender<EditorInterpolationCurve>(ChannelProperty, GridBrushProperty, CurveBrushProperty,
            Handle1BrushProperty, Handle2BrushProperty, ForegroundProperty, BackgroundProperty, BoundsProperty, IsFocusedProperty);
    }
    public EditorInterpolationChannelViewModel? Channel { get => GetValue(ChannelProperty); set => SetValue(ChannelProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? CurveBrush { get => GetValue(CurveBrushProperty); set => SetValue(CurveBrushProperty, value); }
    public IBrush? Handle1Brush { get => GetValue(Handle1BrushProperty); set => SetValue(Handle1BrushProperty, value); }
    public IBrush? Handle2Brush { get => GetValue(Handle2BrushProperty); set => SetValue(Handle2BrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var graph = GraphRectangle();
        if (graph.Width <= 0) return;
        using (context.PushClip(new Rect(Bounds.Size)))
        {
            context.FillRectangle(Background ?? Brushes.Transparent, new Rect(Bounds.Size));
            var gridPen = new Pen(GridBrush, 1);
            context.DrawRectangle(gridPen, graph);
            for (int tick = 1; tick < 4; tick++)
            {
                double part = tick / 4d;
                using (context.PushOpacity(.35))
                {
                    context.DrawLine(gridPen, new Point(graph.Left + graph.Width * part, graph.Top), new Point(graph.Left + graph.Width * part, graph.Bottom));
                    context.DrawLine(gridPen, new Point(graph.Left, graph.Top + graph.Height * part), new Point(graph.Right, graph.Top + graph.Height * part));
                }
            }
            DrawLabel(context, "0", new Point(graph.Left - 6, graph.Bottom + 6));
            DrawLabel(context, "127", new Point(graph.Right - 20, graph.Bottom + 6));
            DrawLabel(context, "127", new Point(graph.Left - 28, graph.Top - 6));
            if (Channel is not { IsCurveValid: true } channel) return;
            var curve = channel.GetCurve();
            Point start = PointOnGraph(0, 0), end = PointOnGraph(127, 127);
            Point one = PointOnGraph(curve.X1, curve.Y1), two = PointOnGraph(curve.X2, curve.Y2);
            var h1Brush = Handle1Brush ?? CurveBrush ?? Foreground;
            var h2Brush = Handle2Brush ?? CurveBrush ?? Foreground;
            using (context.PushOpacity(.75))
            {
                context.DrawLine(new Pen(h1Brush, 1.5), start, one);
                context.DrawLine(new Pen(h2Brush, 1.5), end, two);
            }
            var path = new StreamGeometry();
            using (var geometry = path.Open())
            {
                geometry.BeginFigure(start, false);
                geometry.CubicBezierTo(one, two, end);
                geometry.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(CurveBrush ?? Foreground, 2), path);
            bool overlap = DistanceSquared(one, two) <= 49;
            if (SelectedHandle == 1)
            {
                DrawHandle(context, two, h2Brush, selected: false, outerRing: overlap);
                DrawHandle(context, one, h1Brush, selected: true, outerRing: false);
            }
            else
            {
                DrawHandle(context, one, h1Brush, selected: false, outerRing: overlap);
                DrawHandle(context, two, h2Brush, selected: true, outerRing: false);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || dragPointer is not null || !IsEffectivelyEnabled || Channel is not { IsCurveValid: true } channel ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(this); var curve = channel.GetCurve();
        var one = PointOnGraph(curve.X1, curve.Y1); var two = PointOnGraph(curve.X2, curve.Y2);
        double d1 = DistanceSquared(point, one), d2 = DistanceSquared(point, two);
        if (Math.Min(d1, d2) > 144) return;
        SelectedHandle = d1 == d2 ? SelectedHandle : d1 < d2 ? 1 : 2;
        dragStart = curve; dragChannel = channel; dragPointer = e.Pointer;
        Focus(NavigationMethod.Pointer, e.KeyModifiers);
        e.Pointer.Capture(this); e.PreventGestureRecognition(); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer != dragPointer) return;
        if (!IsEffectivelyEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { EndDrag(cancel: true); return; }
        SetFromPointer(e.GetPosition(this)); e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer != dragPointer || e.InitialPressMouseButton != MouseButton.Left) return;
        SetFromPointer(e.GetPosition(this)); EndDrag(cancel: false); e.Handled = true; EditCompleted?.Invoke();
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (e.Pointer == dragPointer) EndDrag(cancel: true);
        base.OnPointerCaptureLost(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !IsFocused || !IsEffectivelyEnabled) return;
        if (e.Key == Key.Escape && dragPointer is not null) { EndDrag(cancel: true); e.Handled = true; return; }
        if ((e.KeyModifiers & ~KeyModifiers.Shift) != 0 || Channel is not { IsCurveValid: true } channel) return;
        if (e.Key == Key.Space) { SelectedHandle = SelectedHandle == 1 ? 2 : 1; InvalidateVisual(); e.Handled = true; return; }
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        int step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 10 : 1;
        var curve = channel.GetCurve();
        int x = SelectedHandle == 1 ? curve.X1 : curve.X2, y = SelectedHandle == 1 ? curve.Y1 : curve.Y2;
        if (e.Key == Key.Left) x -= step; else if (e.Key == Key.Right) x += step; else if (e.Key == Key.Up) y += step; else y -= step;
        channel.SetHandle(SelectedHandle, Math.Clamp(x, 0, 127), Math.Clamp(y, 0, 127)); e.Handled = true; EditCompleted?.Invoke();
    }
    protected override void OnLostFocus(RoutedEventArgs e) { EndDrag(cancel: true); base.OnLostFocus(e); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); ObserveChannel(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EndDrag(cancel: true);
        if (observedChannel is not null) observedChannel.PropertyChanged -= ChannelChanged;
        observedChannel = null; base.OnDetachedFromVisualTree(e);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChannelProperty) { EndDrag(cancel: true); ObserveChannel(); }
        if ((change.Property == IsEffectivelyEnabledProperty && !IsEffectivelyEnabled) || (change.Property == IsVisibleProperty && !IsVisible)) EndDrag(cancel: true);
    }
    private void ObserveChannel()
    {
        if (ReferenceEquals(observedChannel, Channel)) return;
        if (observedChannel is not null) observedChannel.PropertyChanged -= ChannelChanged;
        observedChannel = Channel;
        if (observedChannel is not null) observedChannel.PropertyChanged += ChannelChanged;
    }
    private void ChannelChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();
    public Rect GraphRectangle()
    {
        double size = Math.Max(0, Math.Min(Bounds.Width - 56, Bounds.Height - 44));
        return new Rect(32 + Math.Max(0, (Bounds.Width - 56 - size) / 2), 12 + Math.Max(0, (Bounds.Height - 44 - size) / 2), size, size);
    }
    public Point PointOnGraph(int x, int y)
    {
        var graph = GraphRectangle(); return new Point(graph.Left + graph.Width * x / 127, graph.Bottom - graph.Height * y / 127);
    }
    private void SetFromPointer(Point position)
    {
        var graph = GraphRectangle();
        if (graph.Width <= 0 || dragChannel is null || !double.IsFinite(position.X) || !double.IsFinite(position.Y)) return;
        int x = (int)Math.Round(Math.Clamp((position.X - graph.Left) / graph.Width * 127, 0, 127), MidpointRounding.AwayFromZero);
        int y = (int)Math.Round(Math.Clamp((graph.Bottom - position.Y) / graph.Height * 127, 0, 127), MidpointRounding.AwayFromZero);
        dragChannel.SetHandle(SelectedHandle, x, y);
    }
    private void EndDrag(bool cancel)
    {
        var pointer = dragPointer; var channel = dragChannel;
        dragPointer = null; dragChannel = null;
        if (cancel && channel is not null)
            channel.SetHandle(SelectedHandle, SelectedHandle == 1 ? dragStart.X1 : dragStart.X2, SelectedHandle == 1 ? dragStart.Y1 : dragStart.Y2);
        if (pointer?.Captured == this) pointer.Capture(null);
        InvalidateVisual();
    }
    private void DrawHandle(DrawingContext context, Point position, IBrush? brush, bool selected, bool outerRing)
    {
        var stroke = brush ?? CurveBrush ?? Foreground;
        if (outerRing)
        {
            context.DrawEllipse(Background, new Pen(stroke, 2.2), position, 6.5, 6.5);
        }
        else if (selected)
        {
            context.DrawEllipse(stroke, new Pen(Background ?? Brushes.Transparent, 1.5), position, 5.5, 5.5);
        }
        else
        {
            context.DrawEllipse(Background, new Pen(stroke, 2), position, 4.5, 4.5);
        }
        if (IsFocused && selected) context.DrawRectangle(new Pen(stroke, 1), new Rect(position.X - 8, position.Y - 8, 16, 16));
    }
    private void DrawLabel(DrawingContext context, string text, Point position) => context.DrawText(new FormattedText(text,
        CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily), 11, Foreground), position);
    private static double DistanceSquared(Point a, Point b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
}
