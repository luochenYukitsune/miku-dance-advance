using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Mmd.Modern.Application.MotionEditing;

namespace Mmd.Modern.App.Controls;

/// <summary>
/// Frame ruler and visible authored tracks. Evaluation, edits and playback belong to the host.
/// </summary>
public sealed class EditorTimeline : TemplatedControl
{
    public const double RulerHeight = 28;
    public const double MinimumPixelsPerFrame = .5;
    public const double MaximumPixelsPerFrame = 64;
    public const double DefaultPixelsPerFrame = 8;
    public const int MaximumVisibleTickCount = 512;
    public const double TrackRowHeight = 26;
    public const int MaximumVisibleKeyCount = 8192;

    public static readonly StyledProperty<int> CurrentFrameProperty =
        AvaloniaProperty.Register<EditorTimeline, int>(nameof(CurrentFrame),
            defaultBindingMode: BindingMode.TwoWay, coerce: (_, value) => Math.Max(0, value));

    public static readonly StyledProperty<double> PixelsPerFrameProperty =
        AvaloniaProperty.Register<EditorTimeline, double>(nameof(PixelsPerFrame), DefaultPixelsPerFrame,
            defaultBindingMode: BindingMode.TwoWay, coerce: (_, value) => CoerceZoom(value));

    public static readonly StyledProperty<double> FirstVisibleFrameProperty =
        AvaloniaProperty.Register<EditorTimeline, double>(nameof(FirstVisibleFrame),
            defaultBindingMode: BindingMode.TwoWay, coerce: (_, value) => CoerceFirstFrame(value));

    public static readonly StyledProperty<bool> HasTracksProperty =
        AvaloniaProperty.Register<EditorTimeline, bool>(nameof(HasTracks));

    public static readonly StyledProperty<IReadOnlyList<EditorTimelineTrack>> TracksProperty =
        AvaloniaProperty.Register<EditorTimeline, IReadOnlyList<EditorTimelineTrack>>(nameof(Tracks), Array.Empty<EditorTimelineTrack>());

    public static readonly StyledProperty<IReadOnlyList<EditorTimelineGroup>> GroupsProperty =
        AvaloniaProperty.Register<EditorTimeline, IReadOnlyList<EditorTimelineGroup>>(nameof(Groups), Array.Empty<EditorTimelineGroup>());

    /// <summary>Complete authored rows. Only selection validity uses this; geometry uses <see cref="Tracks"/>.</summary>
    public static readonly StyledProperty<IReadOnlyList<EditorTimelineTrack>> FullTracksProperty =
        AvaloniaProperty.Register<EditorTimeline, IReadOnlyList<EditorTimelineTrack>>(nameof(FullTracks), Array.Empty<EditorTimelineTrack>());

    /// <summary>Focused authored row inside the visible list; -1 scrolls nothing.</summary>
    public static readonly StyledProperty<int> FocusRowProperty =
        AvaloniaProperty.Register<EditorTimeline, int>(nameof(FocusRow), -1, coerce: (_, value) => Math.Max(-1, value));

    public static readonly StyledProperty<IReadOnlyList<KeyframeAddress>> SelectedKeyframesProperty =
        AvaloniaProperty.Register<EditorTimeline, IReadOnlyList<KeyframeAddress>>(nameof(SelectedKeyframes), Array.Empty<KeyframeAddress>(), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> FirstVisibleTrackProperty =
        AvaloniaProperty.Register<EditorTimeline, int>(nameof(FirstVisibleTrack), coerce: (_, value) => Math.Max(0, value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> TrackHeaderWidthProperty =
        AvaloniaProperty.Register<EditorTimeline, double>(nameof(TrackHeaderWidth), coerce: (_, value) => double.IsFinite(value) ? Math.Clamp(value, 0, 400) : 0);

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<EditorTimeline, IBrush?>(nameof(GridBrush),
            new SolidColorBrush(Color.Parse("#46545F")));

    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
        AvaloniaProperty.Register<EditorTimeline, IBrush?>(nameof(PlayheadBrush),
            new SolidColorBrush(Color.Parse("#A8BBC9")));

    private IPointer? _dragPointer;
    private KeyframeAddress? _selectionAnchor;
    private TimelineGesture _gesture;
    private Point _gestureStart, _gesturePosition;
    private IReadOnlyList<KeyframeAddress> _selectionBeforeGesture = Array.Empty<KeyframeAddress>();
    private IReadOnlyList<KeyframeAddress> _movingKeys = Array.Empty<KeyframeAddress>();
    private readonly Dictionary<EditorTimelineRow, EditorTimelineTrack> _movingRows = [];
    private readonly HashSet<string> _expandedGroups = new(StringComparer.Ordinal);
    private IReadOnlyList<EditorTimelineRow> _displayRows = Array.Empty<EditorTimelineRow>();
    /// <summary>Row geometry frozen for the current pointer gesture; authored changes still cancel it.</summary>
    private IReadOnlyList<EditorTimelineRow>? _gestureTracks;
    private bool _deferredTrackView;
    private uint _minimumMovingFrame, _maximumMovingFrame;
    private IReadOnlyList<KeyframeAddress>? _toggleOnClickAddresses;
    private bool _additiveBox, _gestureMoved;
    private long _keyDragDelta;
    private readonly List<(double X, double Center, StreamGeometry Geometry)> _diamondGeometries = [];
    private int _drawnDiamondCount;
    private readonly PlayheadLayer _playheadLayer;
    private TopLevel? _scalingSource;

    private enum TimelineGesture { None, Ruler, Keys, Box }

    public event EventHandler? KeyframeSelectionChanged;
    /// <summary>One request on release. The host preflights conflicts and owns the atomic edit and Undo.</summary>
    public event EventHandler<TimelineKeysMoveRequestedEventArgs>? KeysMoveRequested;
    public event EventHandler? ScrubStarted;
    public event EventHandler? ScrubEnded;
    /// <summary>Local gesture preview only; authored track data is never changed by pointer movement.</summary>
    public long PreviewFrameDelta => _gesture == TimelineGesture.Keys ? _keyDragDelta : 0;

    static EditorTimeline()
    {
        FocusableProperty.OverrideDefaultValue<EditorTimeline>(true);
        BackgroundProperty.OverrideDefaultValue<EditorTimeline>(Brushes.Black);
        ForegroundProperty.OverrideDefaultValue<EditorTimeline>(Brushes.White);
        FontSizeProperty.OverrideDefaultValue<EditorTimeline>(12);
        // The track layer ignores the playhead, the ruler and focus; those redraw only the playhead layer.
        AffectsRender<EditorTimeline>(PixelsPerFrameProperty,
            FirstVisibleFrameProperty, GridBrushProperty, PlayheadBrushProperty,
            ForegroundProperty, BackgroundProperty, FontFamilyProperty, FontSizeProperty,
            FontStyleProperty, FontWeightProperty, FontStretchProperty, BoundsProperty);
        AffectsRender<EditorTimeline>(TracksProperty, SelectedKeyframesProperty, FirstVisibleTrackProperty, TrackHeaderWidthProperty);
    }

    private static readonly HashSet<AvaloniaProperty> PlayheadLayerProperties =
    [
        CurrentFrameProperty, PixelsPerFrameProperty, FirstVisibleFrameProperty, HasTracksProperty, GridBrushProperty,
        PlayheadBrushProperty, ForegroundProperty, BackgroundProperty, FontFamilyProperty, FontSizeProperty, FontStyleProperty,
        FontWeightProperty, FontStretchProperty, BoundsProperty, IsFocusedProperty, TracksProperty, GroupsProperty,
        SelectedKeyframesProperty, FirstVisibleTrackProperty, TrackHeaderWidthProperty
    ];

    public EditorTimeline()
    {
        _playheadLayer = new PlayheadLayer(this);
        ((ISetLogicalParent)_playheadLayer).SetParent(this);
        VisualChildren.Add(_playheadLayer);
        EditorLocalization.Observe(this, static timeline => timeline.InvalidateLayers());
    }

    /// <summary>Track-layer passes; the playhead layer keeps this unchanged while only the frame moves.</summary>
    internal int TrackLayerRenderCount { get; private set; }
    internal int PlayheadLayerRenderCount { get; private set; }

    /// <summary>Nonnegative frontend frame position. Input keeps an existing binding intact.</summary>
    public int CurrentFrame
    {
        get => GetValue(CurrentFrameProperty);
        set => SetValue(CurrentFrameProperty, value);
    }

    /// <summary>Logical pixels per frame, clamped to .5–64.</summary>
    public double PixelsPerFrame
    {
        get => GetValue(PixelsPerFrameProperty);
        set => SetValue(PixelsPerFrameProperty, value);
    }

    /// <summary>Fractional frame at the left edge; preserves precise pointer-anchored zoom.</summary>
    public double FirstVisibleFrame
    {
        get => GetValue(FirstVisibleFrameProperty);
        set => SetValue(FirstVisibleFrameProperty, value);
    }

    /// <summary>
    /// Allows ruler grid lines to extend into a real track region. False draws no
    /// invented rows or keys; the host can place its genuine empty state below the ruler.
    /// </summary>
    public bool HasTracks
    {
        get => GetValue(HasTracksProperty);
        set => SetValue(HasTracksProperty, value);
    }

    /// <summary>Immutable, sorted visible rows. Render, hit testing, box selection and scrolling share this list.</summary>
    public IReadOnlyList<EditorTimelineTrack> Tracks { get => GetValue(TracksProperty); set => SetValue(TracksProperty, value); }
    /// <summary>Authored categories of the current model. Empty preserves the optional flat projection.</summary>
    public IReadOnlyList<EditorTimelineGroup> Groups { get => GetValue(GroupsProperty); set => SetValue(GroupsProperty, value); }
    /// <summary>Current lightweight row projection, shared by drawing and all pointer operations.</summary>
    public IReadOnlyList<EditorTimelineRow> DisplayRows => _displayRows;
    public bool IsGroupExpanded(string key) => _expandedGroups.Contains(key);
    public void SetGroupExpanded(string key, bool expanded)
    {
        if (_dragPointer is not null || !Groups.Any(group => group.Key == key)) return;
        bool changed = expanded ? _expandedGroups.Add(key) : _expandedGroups.Remove(key);
        if (!changed) return;
        RebuildDisplayRows(); TrackViewChanged(contentsChanged: false); InvalidateLayers();
    }
    /// <summary>
    /// Complete authored rows, including rows hidden by a UI-only projection. Selection validity is
    /// checked against this source so hiding a row never prunes a real selected keyframe. When unset,
    /// validity falls back to <see cref="Tracks"/> for standalone hosting.
    /// </summary>
    public IReadOnlyList<EditorTimelineTrack> FullTracks { get => GetValue(FullTracksProperty); set => SetValue(FullTracksProperty, value); }
    /// <summary>Focused authored row index in <see cref="Tracks"/>; kept on screen across projection changes.</summary>
    public int FocusRow { get => GetValue(FocusRowProperty); set => SetValue(FocusRowProperty, value); }
    public IReadOnlyList<KeyframeAddress> SelectedKeyframes { get => GetValue(SelectedKeyframesProperty); set => SetValue(SelectedKeyframesProperty, value); }
    public int FirstVisibleTrack { get => GetValue(FirstVisibleTrackProperty); set => SetValue(FirstVisibleTrackProperty, value); }
    /// <summary>Use 220 for an integrated label column. Zero preserves the standalone ruler geometry.</summary>
    public double TrackHeaderWidth { get => GetValue(TrackHeaderWidthProperty); set => SetValue(TrackHeaderWidthProperty, value); }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? PlayheadBrush
    {
        get => GetValue(PlayheadBrushProperty);
        set => SetValue(PlayheadBrushProperty, value);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        _playheadLayer.Arrange(new Rect(size));
        return size;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scalingSource = TopLevel.GetTopLevel(this);
        if (_scalingSource is not null) _scalingSource.ScalingChanged += OnScalingChanged;
    }

    private void OnScalingChanged(object? sender, EventArgs e) => InvalidateLayers();

    /// <summary>Redraws both layers; geometry, selection and gesture changes affect each of them.</summary>
    private void InvalidateLayers()
    {
        InvalidateVisual();
        _playheadLayer.InvalidateVisual();
    }

    /// <summary>Track layer: background, rows, keyframes and the box-selection rectangle.</summary>
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return;
        TrackLayerRenderCount++;

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var hairline = 1 / scale;
        var gridPen = new Pen(GridBrush, hairline);
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var fontSize = Math.Clamp(FontSize, 8, 24);
        var header = Math.Min(TrackHeaderWidth, width);

        using (context.PushClip(new Rect(Bounds.Size)))
        {
            context.FillRectangle(Background ?? Brushes.Transparent, new Rect(Bounds.Size));
            RenderTracks(context, width, height, header, gridPen, typeface, fontSize);
            if (_gesture == TimelineGesture.Box)
            {
                var box = SelectionRectangle();
                using (context.PushClip(new Rect(header, RulerHeight, Math.Max(0, width - header), Math.Max(0, height - RulerHeight))))
                {
                    using (context.PushOpacity(.12)) context.FillRectangle(PlayheadBrush ?? Foreground ?? Brushes.White, box);
                    context.DrawRectangle(new Pen(PlayheadBrush ?? Foreground, hairline), box);
                }
            }
        }
    }

    /// <summary>
    /// Playhead layer, drawn after the track layer in the same order as one pass: ticks, frame labels
    /// (which avoid the playhead marker), the ruler line, the playhead and the focus line.
    /// </summary>
    private void RenderPlayheadLayer(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return;
        PlayheadLayerRenderCount++;

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var hairline = 1 / scale;
        var rulerBottom = Math.Min(RulerHeight, height);
        var gridPen = new Pen(GridBrush, hairline);
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var fontSize = Math.Clamp(FontSize, 8, 24);
        var lastLabelRight = double.NegativeInfinity;
        var header = Math.Min(TrackHeaderWidth, width);
        var playheadX = header + (CurrentFrame - FirstVisibleFrame) * PixelsPerFrame;

        using (context.PushClip(new Rect(Bounds.Size)))
        {
            foreach (var tick in BuildVisibleTicks(width - header, FirstVisibleFrame, PixelsPerFrame))
            {
                var x = SnapStroke(header + tick.X, scale);
                if ((HasTracks || ActiveTracks.Count != 0) && tick.IsMajor && height > RulerHeight)
                {
                    using (context.PushOpacity(.3))
                        context.DrawLine(gridPen, new Point(x, rulerBottom), new Point(x, height));
                }

                context.DrawLine(gridPen, new Point(x, Math.Max(0, rulerBottom - (tick.IsMajor ? 12 : 5))),
                    new Point(x, rulerBottom));
                if (!tick.IsMajor || Foreground is null) continue;

                var label = new FormattedText(tick.Frame.ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, Foreground);
                var labelLeft = header + tick.X + 4;
                // Do not overlap adjacent labels or the playhead's small pointer.
                if (labelLeft < lastLabelRight + 8 || labelLeft + label.Width > width ||
                    (playheadX >= labelLeft - 7 && playheadX <= labelLeft + label.Width + 7))
                    continue;
                context.DrawText(label, new Point(Math.Round(labelLeft * scale) / scale, 3));
                lastLabelRight = labelLeft + label.Width;
            }

            context.DrawLine(gridPen, new Point(0, SnapStroke(rulerBottom - hairline, scale)),
                new Point(width, SnapStroke(rulerBottom - hairline, scale)));

            if (playheadX >= header && playheadX <= width && PlayheadBrush is { } playhead)
            {
                var x = SnapStroke(playheadX, scale);
                context.DrawLine(new Pen(playhead, hairline), new Point(x, 0), new Point(x, height));
                var marker = new StreamGeometry();
                using (var geometry = marker.Open())
                {
                    geometry.BeginFigure(new Point(playheadX - 5, 0), true);
                    geometry.LineTo(new Point(playheadX + 5, 0));
                    geometry.LineTo(new Point(playheadX + 5, 7));
                    geometry.LineTo(new Point(playheadX, 12));
                    geometry.LineTo(new Point(playheadX - 5, 7));
                    geometry.EndFigure(true);
                }
                context.DrawGeometry(playhead, null, marker);
            }

            if (IsFocused && Foreground is { } focusBrush)
            {
                // Focus belongs to the ruler, so keyboard ownership is visible.
                context.DrawLine(new Pen(focusBrush, hairline),
                    new Point(0, SnapStroke(rulerBottom - hairline, scale)),
                    new Point(width, SnapStroke(rulerBottom - hairline, scale)));
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!e.Handled && IsEffectivelyEnabled && point.Properties.IsLeftButtonPressed && _dragPointer is null &&
            point.Position.Y >= RulerHeight && point.Position.Y < Bounds.Height && ActiveTracks.Count != 0)
        {
            Focus(NavigationMethod.Pointer, e.KeyModifiers);
            BeginTrackGesture(point.Position, e.KeyModifiers, e.Pointer);
            e.PreventGestureRecognition();
            e.Handled = true;
            return;
        }
        if (e.Handled || !IsEffectivelyEnabled || _dragPointer is not null ||
            !point.Properties.IsLeftButtonPressed || point.Position.Y < 0 ||
            point.Position.Y >= Math.Min(RulerHeight, Bounds.Height) || point.Position.X < TrackHeaderWidth)
            return;

        Focus(NavigationMethod.Pointer, e.KeyModifiers);
        _dragPointer = e.Pointer;
        _gesture = TimelineGesture.Ruler;
        e.Pointer.Capture(this);
        e.PreventGestureRecognition();
        ScrubStarted?.Invoke(this, EventArgs.Empty);
        SetCurrentValue(CurrentFrameProperty, FrameAtX(point.Position.X));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragPointer != e.Pointer) return;
        if (!IsEffectivelyEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            CancelGesture();
            return;
        }
        UpdateGesture(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragPointer != e.Pointer || e.InitialPressMouseButton != MouseButton.Left) return;
        UpdateGesture(e.GetPosition(this));
        var movedKeys = _gesture == TimelineGesture.Keys && _gestureMoved && _keyDragDelta != 0
            ? new TimelineKeysMoveRequestedEventArgs(_movingKeys, _keyDragDelta) : null;
        if (_gesture == TimelineGesture.Keys && !_gestureMoved && _toggleOnClickAddresses is { } toggles)
        {
            var remove = toggles.ToHashSet();
            SetSelection(SelectedKeyframes.Where(key => !remove.Contains(key)));
        }
        if (_gesture == TimelineGesture.Box && !_gestureMoved)
            SetCurrentValue(CurrentFrameProperty, FrameAtX(_gestureStart.X));
        ReleaseDrag();
        if (movedKeys is not null) KeysMoveRequested?.Invoke(this, movedKeys);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (_dragPointer == e.Pointer) CancelGesture();
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Handled || !IsEffectivelyEnabled || _dragPointer is not null ||
            (e.KeyModifiers & (KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
            return;

        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
            if (!double.IsFinite(delta) || delta == 0) return;
            var viewport = ZoomAtX(e.GetPosition(this).X - TrackHeaderWidth, FirstVisibleFrame, PixelsPerFrame, delta);
            SetCurrentValue(PixelsPerFrameProperty, viewport.PixelsPerFrame);
            SetCurrentValue(FirstVisibleFrameProperty, viewport.FirstVisibleFrame);
        }
        else
        {
            if (ActiveTracks.Count != 0 && ((e.KeyModifiers & KeyModifiers.Shift) != 0 || e.GetPosition(this).X < TrackHeaderWidth))
            {
                var visible = Math.Max(1, (int)Math.Ceiling(Math.Max(0, Bounds.Height - RulerHeight) / TrackRowHeight));
                var deltaY = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
                if (!double.IsFinite(deltaY) || deltaY == 0) return;
                SetCurrentValue(FirstVisibleTrackProperty, Math.Clamp(FirstVisibleTrack - (int)Math.Clamp(Math.Round(deltaY * 3), -100, 100), 0, Math.Max(0, ActiveTracks.Count - visible)));
                e.Handled = true;
                return;
            }
            // Native horizontal wheels use X; ordinary vertical wheels pan the same ruler.
            var delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
            if (!double.IsFinite(delta) || delta == 0) return;
            SetCurrentValue(FirstVisibleFrameProperty,
                CoerceFirstFrame(FirstVisibleFrame - Math.Clamp(delta, -100, 100) * 48 / PixelsPerFrame));
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && _dragPointer is not null)
        {
            CancelGesture(); e.Handled = true; return;
        }
        if (e.Handled || !IsFocused || !IsEffectivelyEnabled ||
            (e.KeyModifiers & ~KeyModifiers.Shift) != 0 || (e.Key != Key.Left && e.Key != Key.Right))
            return;

        var step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 10L : 1L;
        var frame = (int)Math.Clamp(CurrentFrame + (e.Key == Key.Right ? step : -step), 0, int.MaxValue);
        SetCurrentValue(CurrentFrameProperty, frame);
        e.Handled = true;
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        CancelGesture();
        base.OnLostFocus(e);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelGesture();
        if (_scalingSource is not null) _scalingSource.ScalingChanged -= OnScalingChanged;
        _scalingSource = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (PlayheadLayerProperties.Contains(change.Property)) _playheadLayer?.InvalidateVisual();
        if (change.Property == SelectedKeyframesProperty) KeyframeSelectionChanged?.Invoke(this, EventArgs.Empty);
        if (change.Property == FullTracksProperty)
        {
            // Only authored source changes may cancel a live gesture; a projection refresh may not.
            // The selected unkeyed channel contributes a temporary empty row that the host adds or
            // removes on selection changes, so authored comparison ignores zero-frame rows.
            if (_gesture is TimelineGesture.Keys or TimelineGesture.Box &&
                !SameAuthoredContents(change.OldValue as IReadOnlyList<EditorTimelineTrack>, FullTracks)) CancelGesture();
            PruneSelectionAgainstAuthored();
        }
        if (change.Property == TracksProperty)
        {
            RebuildDisplayRows();
            var previous = change.OldValue as IReadOnlyList<EditorTimelineTrack>;
            bool contentsChanged = !SameTrackContents(previous, Tracks);
            if (_gesture is TimelineGesture.Keys or TimelineGesture.Box)
            {
                if (FullTracks.Count != 0)
                {
                    // The host's synchronous key-selection callback can replace the visible projection
                    // during BeginTrackGesture or box selection. Keep this gesture's row geometry until
                    // release, then adopt the new projection.
                    if (contentsChanged) _deferredTrackView = true;
                    PruneSelectionAgainstAuthored();
                    return;
                }
                // Standalone hosting has no separate authored source; the visible list is the source.
                if (!SameAuthoredContents(previous, Tracks)) { CancelGesture(); return; }
            }
            TrackViewChanged(contentsChanged);
            PruneSelectionAgainstAuthored();
        }
        if (change.Property == GroupsProperty)
        {
            RebuildDisplayRows();
            if (_gesture is TimelineGesture.Keys or TimelineGesture.Box) _deferredTrackView = true;
            else TrackViewChanged(contentsChanged: true);
            InvalidateLayers();
        }
        if (change.Property == FocusRowProperty)
        {
            if (_dragPointer is null) BringTrackIntoView(FocusRow);
            else _deferredTrackView = true;
        }
        if ((change.Property == IsEffectivelyEnabledProperty && !IsEffectivelyEnabled) ||
            (change.Property == IsVisibleProperty && !IsVisible))
            CancelGesture();
        else if ((change.Property == CurrentFrameProperty && _dragPointer is null) || change.Property == BoundsProperty)
            BringFrameIntoView(CurrentFrame);
    }

    /// <summary>Rows used by render, hit testing and box selection; frozen while a key/box gesture is active.</summary>
    private IReadOnlyList<EditorTimelineRow> ActiveTracks =>
        _gestureTracks is { } frozen && _gesture is TimelineGesture.Keys or TimelineGesture.Box ? frozen : _displayRows;

    private void RebuildDisplayRows()
    {
        if (Groups.Count == 0)
        {
            _displayRows = Array.AsReadOnly(Tracks.Select(track => new EditorTimelineRow(track)).ToArray());
            return;
        }
        var rows = new List<EditorTimelineRow>();
        foreach (var group in Groups)
        {
            rows.Add(group.Summary);
            if (_expandedGroups.Contains(group.Key)) rows.AddRange(group.Children);
        }
        _displayRows = rows.AsReadOnly();
    }

    /// <summary>Clamps row scroll to the visible projection and reveals the focused row after a projection change.</summary>
    private void TrackViewChanged(bool contentsChanged)
    {
        SetCurrentValue(FirstVisibleTrackProperty, Math.Min(FirstVisibleTrack, Math.Max(0, _displayRows.Count - 1)));
        if (contentsChanged) BringTrackIntoView(FocusRow);
    }

    private void BringTrackIntoView(int row)
    {
        var tracks = _displayRows;
        if (row < 0 || row >= Tracks.Count) return;
        var focused = Tracks[row];
        var address = new KeyframeAddress(focused.Kind, focused.Name, 0);
        row = -1;
        for (int index = 0; index < tracks.Count; index++)
            if (tracks[index].ContainsChannel(address)) { row = index; if (!tracks[index].IsGroup) break; }
        if (row < 0) return;
        int visible = Math.Max(1, (int)Math.Ceiling(Math.Max(0, Bounds.Height - RulerHeight) / TrackRowHeight));
        int first = Math.Min(FirstVisibleTrack, Math.Max(0, tracks.Count - 1));
        if (row < first) first = row;
        else if (row >= first + visible) first = row - visible + 1;
        first = Math.Clamp(first, 0, Math.Max(0, tracks.Count - 1));
        if (first != FirstVisibleTrack) SetCurrentValue(FirstVisibleTrackProperty, first);
    }

    /// <summary>
    /// A filtered projection must not invalidate selected keys it simply hides. Validity always uses
    /// the complete authored rows; only a real removal from the source prunes the selection.
    /// </summary>
    private void PruneSelectionAgainstAuthored()
    {
        if (SelectedKeyframes.Count == 0) return;
        var source = FullTracks.Count != 0 ? FullTracks : Tracks;
        var channels = source.DistinctBy(track => (track.Kind, track.Name)).ToDictionary(track => (track.Kind, track.Name));
        bool Valid(KeyframeAddress address)
        {
            if (!channels.TryGetValue((address.Kind, address.Name), out var track)) return false;
            int index = track.LowerBound(address.Frame);
            return index < track.Frames.Count && track.Frames[index] == address.Frame;
        }
        if (SelectedKeyframes.Any(address => !Valid(address)))
            SetCurrentValue(SelectedKeyframesProperty, Array.AsReadOnly(SelectedKeyframes.Where(Valid).ToArray()));
    }

    /// <summary>Maps a local logical X coordinate through this ruler's actual scroll and zoom.</summary>
    public int FrameAtX(double x) => FrameAtX(x - TrackHeaderWidth, FirstVisibleFrame, PixelsPerFrame);

    /// <summary>Shared render/input geometry; half frames round toward the following frame.</summary>
    public static int FrameAtX(double x, double firstVisibleFrame, double pixelsPerFrame)
    {
        var first = CoerceFirstFrame(firstVisibleFrame);
        if (double.IsNaN(x)) x = 0;
        var frame = first + x / CoerceZoom(pixelsPerFrame);
        return (int)Math.Round(Math.Clamp(frame, 0, int.MaxValue), MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Returns only visible 1/2/5-based ticks. Both iteration and allocation are capped,
    /// including very wide layouts and the final representable frame.
    /// </summary>
    public static IReadOnlyList<TimelineTick> BuildVisibleTicks(
        double width, double firstVisibleFrame, double pixelsPerFrame)
    {
        if (!double.IsFinite(width) || width <= 0) return Array.Empty<TimelineTick>();
        var first = CoerceFirstFrame(firstVisibleFrame);
        var zoom = CoerceZoom(pixelsPerFrame);
        var last = Math.Min(int.MaxValue, first + width / zoom);
        var span = last - first;
        var minorStep = NiceStep(Math.Max(8 / zoom, span / (MaximumVisibleTickCount - 2)));
        var majorStep = NiceStep(Math.Max(80 / zoom, minorStep * 5d));
        var firstTick = (long)Math.Ceiling(first / minorStep) * minorStep;
        var ticks = new List<TimelineTick>();
        for (var frame = firstTick; frame <= last && ticks.Count < MaximumVisibleTickCount; frame += minorStep)
            ticks.Add(new TimelineTick((int)frame, (frame - first) * zoom, frame % majorStep == 0));
        return ticks;
    }

    /// <summary>
    /// Zooms around the frame under X. At the start/end boundary the nonnegative
    /// representable frame range takes precedence over retaining the exact anchor.
    /// </summary>
    public static TimelineViewport ZoomAtX(double x, double firstVisibleFrame, double pixelsPerFrame, double wheelDelta)
    {
        var first = CoerceFirstFrame(firstVisibleFrame);
        var zoom = CoerceZoom(pixelsPerFrame);
        if (!double.IsFinite(x)) x = 0;
        if (!double.IsFinite(wheelDelta)) wheelDelta = 0;
        var nextZoom = CoerceZoom(zoom * Math.Pow(1.2, Math.Clamp(wheelDelta, -32, 32)));
        var nextFirst = first + x / zoom - x / nextZoom;
        return new TimelineViewport(CoerceFirstFrame(nextFirst), nextZoom);
    }

    private void BringFrameIntoView(int frame)
    {
        var width = Bounds.Width - TrackHeaderWidth;
        if (!double.IsFinite(width) || width <= 0) return;
        if (frame < FirstVisibleFrame)
            SetCurrentValue(FirstVisibleFrameProperty, (double)frame);
        else if ((frame - FirstVisibleFrame) * PixelsPerFrame > Math.Max(0, width - 8))
            SetCurrentValue(FirstVisibleFrameProperty,
                CoerceFirstFrame(frame - Math.Max(0, width - 8) / PixelsPerFrame));
    }

    private void ReleaseDrag()
    {
        var pointer = _dragPointer;
        var wasRuler = _gesture == TimelineGesture.Ruler;
        _dragPointer = null;
        _gesture = TimelineGesture.None;
        _gestureTracks = null;
        var applyDeferredTrackView = _deferredTrackView;
        _deferredTrackView = false;
        _keyDragDelta = 0;
        _movingKeys = Array.Empty<KeyframeAddress>();
        _movingRows.Clear();
        _selectionBeforeGesture = Array.Empty<KeyframeAddress>();
        _toggleOnClickAddresses = null;
        if (pointer?.Captured == this) pointer.Capture(null);
        // Adopt a projection that arrived synchronously during the gesture only once it has ended.
        if (applyDeferredTrackView) TrackViewChanged(contentsChanged: true);
        if (wasRuler && !applyDeferredTrackView) _playheadLayer.InvalidateVisual();
        else InvalidateLayers();
        if (wasRuler) ScrubEnded?.Invoke(this, EventArgs.Empty);
    }

    private void CancelGesture()
    {
        var restore = _selectionBeforeGesture;
        var hadTrackGesture = _gesture is TimelineGesture.Keys or TimelineGesture.Box;
        ReleaseDrag();
        if (hadTrackGesture) SetSelection(restore);
    }

    private void RenderTracks(DrawingContext context, double width, double height, double header,
        Pen gridPen, Typeface typeface, double fontSize)
    {
        _drawnDiamondCount = 0;
        var tracks = ActiveTracks;
        var selected = SelectedKeyframes.ToHashSet();
        var keyPen = new Pen(Foreground, 1);
        var activeBrush = PlayheadBrush ?? Foreground;
        var activeKeyPen = new Pen(activeBrush, 2);
        int remainingKeys = MaximumVisibleKeyCount;
        int visible = Math.Min(512, Math.Max(0, (int)Math.Ceiling((height - RulerHeight) / TrackRowHeight)));
        var rows = Math.Min(tracks.Count, (long)FirstVisibleTrack + visible);
        for (int index = FirstVisibleTrack; index < rows; index++)
        {
            var track = tracks[index];
            double top = RulerHeight + (index - FirstVisibleTrack) * TrackRowHeight;
            double center = top + TrackRowHeight / 2;
            using (context.PushOpacity(.5)) context.DrawLine(gridPen, new Point(0, top + TrackRowHeight), new Point(width, top + TrackRowHeight));
            if (header > 0 && Foreground is { } textBrush)
            {
                using (context.PushClip(new Rect(6, top, Math.Max(0, header - 12), TrackRowHeight)))
                {
                    var labelText = track.IsGroup ? (IsGroupExpanded(track.Group!.Key) ? "▾  " : "▸  ") + track.Label : track.Label;
                    var label = new FormattedText(labelText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, Math.Max(13, fontSize), textBrush);
                    context.DrawText(label, new Point(track.IsChild ? 25 : 9, center - label.Height / 2));
                }
            }
            using (context.PushClip(new Rect(header, RulerHeight, Math.Max(0, width - header), Math.Max(0, height - RulerHeight))))
            {
                var first = track.LowerBound(Math.Max(0, FirstVisibleFrame - 6 / PixelsPerFrame));
                var last = FirstVisibleFrame + (width - header + 6) / PixelsPerFrame;
                var selectedFrames = selected.Where(track.ContainsChannel).Select(key => key.Frame).ToHashSet();
                double lastDrawnX = double.NegativeInfinity;
                for (int k = first; k < track.Frames.Count && track.Frames[k] <= last && remainingKeys > 0; k++)
                {
                    var frame = track.Frames[k];
                    bool active = selectedFrames.Contains(frame);
                    double x = header + ((long)frame + (active ? PreviewFrameDelta : 0) - FirstVisibleFrame) * PixelsPerFrame;
                    // A category can contain selected and unselected channels at the same frame.
                    // Keep its unmoved marker while previewing only the selected child addresses.
                    if (active && PreviewFrameDelta != 0 && track.IsGroup && remainingKeys > 1 &&
                        track.AddressesAt(frame).Any(address => !selected.Contains(address)))
                    {
                        double sourceX = header + (frame - FirstVisibleFrame) * PixelsPerFrame;
                        DrawDiamond(context, sourceX, center, Background, keyPen);
                        remainingKeys--;
                    }
                    // Dense tracks collapse to one visible diamond per logical pixel; source data stays exact.
                    if (!active && x - lastDrawnX < 1) continue;
                    DrawDiamond(context, x, center, active ? activeBrush : Background, active ? activeKeyPen : keyPen);
                    remainingKeys--; if (!active) lastDrawnX = x;
                }
                // A selected key can be dragged into view from outside the original visible frame range.
                if (PreviewFrameDelta != 0 && remainingKeys > 0 && _movingRows.TryGetValue(track, out var moving))
                    for (int key = moving.LowerBound(FirstVisibleFrame - PreviewFrameDelta - 6 / PixelsPerFrame);
                         key < moving.Frames.Count && moving.Frames[key] <= last - PreviewFrameDelta && remainingKeys > 0; key++)
                    {
                        double originalX = header + (moving.Frames[key] - FirstVisibleFrame) * PixelsPerFrame;
                        double x = originalX + PreviewFrameDelta * PixelsPerFrame;
                        if ((originalX >= header - 6 && originalX <= width + 6) || x < header - 6 || x > width + 6 || remainingKeys <= 0) continue;
                        DrawDiamond(context, x, center, activeBrush, activeKeyPen);
                        remainingKeys--;
                    }
            }
        }
        if (header > 0) context.DrawLine(gridPen, new Point(header, 0), new Point(header, height));
        if (tracks.Count > visible && visible > 0 && GridBrush is { } scrollBrush)
        {
            double region = height - RulerHeight;
            double thumb = Math.Max(12, region * visible / tracks.Count);
            double start = RulerHeight + Math.Min(1, FirstVisibleTrack / (double)Math.Max(1, tracks.Count - visible)) * (region - thumb);
            context.FillRectangle(scrollBrush, new Rect(Math.Max(header, width - 3), start, 3, thumb));
        }
        if (_diamondGeometries.Count > _drawnDiamondCount)
            _diamondGeometries.RemoveRange(_drawnDiamondCount, _diamondGeometries.Count - _drawnDiamondCount);
    }

    private void DrawDiamond(DrawingContext context, double x, double center, IBrush? fill, Pen pen)
    {
        // Reuse only this render ordinal's exact absolute coordinates. This keeps original
        // brush coordinates and DPI transforms, without retaining a scroll/zoom history.
        int index = _drawnDiamondCount++;
        StreamGeometry geometry;
        if (index < _diamondGeometries.Count && _diamondGeometries[index] is var cached
            && cached.X == x && cached.Center == center)
            geometry = cached.Geometry;
        else
        {
            geometry = new StreamGeometry();
            using (var shape = geometry.Open())
            {
                shape.BeginFigure(new Point(x, center - 4), true);
                shape.LineTo(new Point(x + 4, center)); shape.LineTo(new Point(x, center + 4));
                shape.LineTo(new Point(x - 4, center)); shape.EndFigure(true);
            }
            if (index < _diamondGeometries.Count) _diamondGeometries[index] = (x, center, geometry);
            else if (index < MaximumVisibleKeyCount) _diamondGeometries.Add((x, center, geometry));
        }
        context.DrawGeometry(fill, pen, geometry);
    }

    private void BeginTrackGesture(Point position, KeyModifiers modifiers, IPointer pointer)
    {
        var tracks = ActiveTracks;
        int row = FirstVisibleTrack + (int)((position.Y - RulerHeight) / TrackRowHeight);
        var track = row >= 0 && row < tracks.Count ? tracks[row] : null;
        if (position.X < TrackHeaderWidth)
        {
            if (track?.Group is { } group) SetGroupExpanded(group.Key, !IsGroupExpanded(group.Key));
            return;
        }
        _gestureTracks = tracks;
        double frame = FirstVisibleFrame + (position.X - TrackHeaderWidth) / PixelsPerFrame;
        int candidate = track?.LowerBound(Math.Max(0, frame)) ?? -1;
        if (track is not null && (candidate >= track.Frames.Count || (candidate > 0 && Math.Abs(track.Frames[candidate - 1] - frame) < Math.Abs(track.Frames[candidate] - frame)))) candidate--;
        uint? hit = track is not null && candidate >= 0 && candidate < track.Frames.Count &&
            Math.Abs((track.Frames[candidate] - frame) * PixelsPerFrame) <= 7 &&
            Math.Abs(position.Y - (RulerHeight + (row - FirstVisibleTrack + .5) * TrackRowHeight)) <= 9
            ? track.Frames[candidate] : null;
        _selectionBeforeGesture = Array.AsReadOnly(SelectedKeyframes.ToArray());
        _dragPointer = pointer;
        _gestureStart = _gesturePosition = position;
        _gestureMoved = false;
        _keyDragDelta = 0;
        _toggleOnClickAddresses = null;
        bool control = (modifiers & KeyModifiers.Control) != 0;
        bool shift = (modifiers & KeyModifiers.Shift) != 0;
        if (hit is { } hitFrame)
        {
            var hitAddresses = track!.AddressesAt(hitFrame).ToArray();
            var address = hitAddresses[0];
            // Freeze the gesture before the synchronous host selection callback can replace the
            // visible projection; the row geometry below must stay valid for this pointer sequence.
            _gesture = TimelineGesture.Keys;
            var selection = control || (!shift && hitAddresses.All(SelectedKeyframes.Contains)) ? SelectedKeyframes.ToHashSet() : new HashSet<KeyframeAddress>();
            if (shift && _selectionAnchor is { } anchor && track.ContainsChannel(anchor))
            {
                uint low = Math.Min(anchor.Frame, address.Frame), high = Math.Max(anchor.Frame, address.Frame);
                foreach (var value in track.AddressesBetween(low, high)) selection.Add(value);
            }
            else
            {
                if (control && hitAddresses.All(selection.Contains)) _toggleOnClickAddresses = hitAddresses;
                selection.UnionWith(hitAddresses); _selectionAnchor = address;
            }
            SetSelection(selection);
            if (_gesture != TimelineGesture.Keys || _dragPointer != pointer) return;
            // Drag only rows this projection actually shows. Hidden selected keyframes stay authored
            // and selected; a release may replace the selection under the existing gesture rules.
            _movingKeys = Array.AsReadOnly(SelectedKeyframes.Where(key => tracks.Any(item => item.ContainsChannel(key))).ToArray());
            _movingRows.Clear();
            foreach (var display in tracks)
            {
                var frames = _movingKeys.Where(display.ContainsChannel).Select(key => key.Frame).ToArray();
                if (frames.Length != 0) _movingRows.Add(display, new EditorTimelineTrack(KeyframeTrackKind.Bone, string.Empty, frames));
            }
            _minimumMovingFrame = _movingKeys.Count == 0 ? 0 : _movingKeys.Min(k => k.Frame);
            _maximumMovingFrame = _movingKeys.Count == 0 ? 0 : _movingKeys.Max(k => k.Frame);
            if (address.Frame <= int.MaxValue) SetCurrentValue(CurrentFrameProperty, (int)address.Frame);
        }
        else
        {
            _gesture = TimelineGesture.Box;
            _additiveBox = control;
            if (!control) SetSelection([]);
        }
        pointer.Capture(this);
        InvalidateLayers();
    }

    private void UpdateGesture(Point position)
    {
        if (_gesture == TimelineGesture.Ruler) { SetCurrentValue(CurrentFrameProperty, FrameAtX(position.X)); return; }
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y)) return;
        _gesturePosition = new Point(Math.Clamp(position.X, TrackHeaderWidth, Math.Max(TrackHeaderWidth, Bounds.Width)),
            Math.Clamp(position.Y, RulerHeight, Math.Max(RulerHeight, Bounds.Height)));
        _gestureMoved |= Math.Abs(position.X - _gestureStart.X) >= 3 || Math.Abs(position.Y - _gestureStart.Y) >= 3;
        if (_gesture == TimelineGesture.Keys && _gestureMoved && _movingKeys.Count != 0)
        {
            long delta = (long)Math.Round((_gesturePosition.X - _gestureStart.X) / PixelsPerFrame, MidpointRounding.AwayFromZero);
            _keyDragDelta = Math.Clamp(delta, -(long)_minimumMovingFrame, (long)uint.MaxValue - _maximumMovingFrame);
        }
        else if (_gesture == TimelineGesture.Box)
        {
            var tracks = ActiveTracks;
            var rectangle = SelectionRectangle();
            var selection = _additiveBox ? _selectionBeforeGesture.ToHashSet() : new HashSet<KeyframeAddress>();
            int firstRow = Math.Max(FirstVisibleTrack, FirstVisibleTrack + (int)Math.Ceiling((rectangle.Top - RulerHeight) / TrackRowHeight - .5));
            int lastRow = Math.Min(tracks.Count - 1, FirstVisibleTrack + (int)Math.Floor((rectangle.Bottom - RulerHeight) / TrackRowHeight - .5));
            double firstFrame = FirstVisibleFrame + (rectangle.Left - TrackHeaderWidth) / PixelsPerFrame;
            double lastFrame = FirstVisibleFrame + (rectangle.Right - TrackHeaderWidth) / PixelsPerFrame;
            for (int row = firstRow; row <= lastRow; row++)
            {
                var track = tracks[row];
                selection.UnionWith(track.AddressesBetween(firstFrame, lastFrame));
            }
            SetSelection(selection);
        }
        InvalidateLayers();
    }

    private Rect SelectionRectangle() => new(Math.Min(_gestureStart.X, _gesturePosition.X), Math.Min(_gestureStart.Y, _gesturePosition.Y),
        Math.Abs(_gestureStart.X - _gesturePosition.X), Math.Abs(_gestureStart.Y - _gesturePosition.Y));
    private void SetSelection(IEnumerable<KeyframeAddress> selection) =>
        SetCurrentValue(SelectedKeyframesProperty, Array.AsReadOnly(selection.Distinct().OrderBy(k => k.Frame).ThenBy(k => k.Kind).ThenBy(k => k.Name, StringComparer.Ordinal).ToArray()));

    private static bool SameTrackContents(IReadOnlyList<EditorTimelineTrack>? before, IReadOnlyList<EditorTimelineTrack> after)
    {
        if (ReferenceEquals(before, after)) return true;
        if (before is null || before.Count != after.Count) return false;
        for (int i = 0; i < before.Count; i++)
            if (before[i].Kind != after[i].Kind || before[i].Name != after[i].Name || !before[i].Frames.SequenceEqual(after[i].Frames)) return false;
        return true;
    }

    /// <summary>
    /// Gesture-safety comparison of authored rows. The focused unkeyed channel contributes a
    /// temporary empty row that selection changes add or remove; it carries no authored frames and
    /// must not cancel a pointer gesture. Every non-empty row (Kind/Name/Frames) is compared in order,
    /// so real key additions, removals and frame moves still cancel the gesture.
    /// </summary>
    private static bool SameAuthoredContents(IReadOnlyList<EditorTimelineTrack>? before, IReadOnlyList<EditorTimelineTrack> after)
    {
        if (ReferenceEquals(before, after)) return true;
        if (before is null) return after.All(track => track.Frames.Count == 0);
        int left = 0, right = 0;
        while (true)
        {
            while (left < before.Count && before[left].Frames.Count == 0) left++;
            while (right < after.Count && after[right].Frames.Count == 0) right++;
            bool leftDone = left >= before.Count, rightDone = right >= after.Count;
            if (leftDone || rightDone) return leftDone && rightDone;
            var oldTrack = before[left++];
            var newTrack = after[right++];
            if (oldTrack.Kind != newTrack.Kind || oldTrack.Name != newTrack.Name ||
                !oldTrack.Frames.SequenceEqual(newTrack.Frames)) return false;
        }
    }

    private static double CoerceFirstFrame(double value) =>
        double.IsNaN(value) ? 0 : Math.Clamp(value, 0, int.MaxValue);

    private static double CoerceZoom(double value) => double.IsNaN(value)
        ? DefaultPixelsPerFrame : Math.Clamp(value, MinimumPixelsPerFrame, MaximumPixelsPerFrame);

    private static double SnapStroke(double value, double scale) => (Math.Floor(value * scale) + .5) / scale;

    private static long NiceStep(double minimum)
    {
        var decade = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, minimum))));
        var units = minimum / decade;
        return (long)(decade * (units <= 1 ? 1 : units <= 2 ? 2 : units <= 5 ? 5 : 10));
    }

    /// <summary>
    /// Non-input visual above the track layer. It is not a Control, so hit testing, focus and automation
    /// still see only the timeline itself; it draws in the timeline's own coordinates.
    /// </summary>
    private sealed class PlayheadLayer(EditorTimeline owner) : Visual
    {
        internal void Arrange(Rect bounds) { if (Bounds != bounds) { Bounds = bounds; InvalidateVisual(); } }
        public override void Render(DrawingContext context) => owner.RenderPlayheadLayer(context);
    }

    public readonly record struct TimelineTick(int Frame, double X, bool IsMajor);
    public readonly record struct TimelineViewport(double FirstVisibleFrame, double PixelsPerFrame);
}

public sealed class TimelineKeysMoveRequestedEventArgs(IReadOnlyList<KeyframeAddress> selection, long frameDelta) : EventArgs
{
    public IReadOnlyList<KeyframeAddress> Selection { get; } = Array.AsReadOnly(selection.ToArray());
    public long FrameDelta { get; } = frameDelta;
}

/// <summary>Real immutable authored row; sorted unique frame addresses support bounded viewport lookup.</summary>
public sealed class EditorTimelineTrack
{
    public KeyframeTrackKind Kind { get; }
    public string Name { get; }
    public string Label => Kind == KeyframeTrackKind.Ik ? EditorLocalization.Text("Mda.Text.Timeline.IkDisplay")
        : EditorLocalization.Text(Kind == KeyframeTrackKind.Morph ? "Mda.Text.Timeline.MorphPrefix" : "Mda.Text.Timeline.BonePrefix") + Name;
    public IReadOnlyList<uint> Frames { get; }
    public EditorTimelineTrack(KeyframeTrackKind kind, string name, IEnumerable<uint> frames)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(frames);
        Kind = kind; Name = name; Frames = Array.AsReadOnly(frames.Distinct().OrderBy(f => f).ToArray());
    }
    internal int LowerBound(double frame)
    {
        int low = 0, high = Frames.Count;
        while (low < high) { int middle = low + (high - low) / 2; if (Frames[middle] < frame) low = middle + 1; else high = middle; }
        return low;
    }
}
