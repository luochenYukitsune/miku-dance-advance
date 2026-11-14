using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NativeAnimation = Avalonia.Animation.Animation;

namespace Mmd.Modern.App.Controls;

/// <summary>
/// Five dots fill from left to right, then empty from left to right.
/// Bind IsActive to real business activity; this control does not simulate work.
/// </summary>
public sealed class LoadingDots : TemplatedControl
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<LoadingDots, bool>(nameof(IsActive));

    public static readonly StyledProperty<TimeSpan> CycleDurationProperty =
        AvaloniaProperty.Register<LoadingDots, TimeSpan>(nameof(CycleDuration),
            TimeSpan.FromSeconds(1.4), validate: value => value > TimeSpan.Zero);

    public static readonly StyledProperty<bool> AnimationsEnabledProperty =
        AvaloniaProperty.Register<LoadingDots, bool>(nameof(AnimationsEnabled), true);

    public static readonly StyledProperty<TimeSpan> ActivationDelayProperty =
        AvaloniaProperty.Register<LoadingDots, TimeSpan>(nameof(ActivationDelay), TimeSpan.Zero,
            validate: value => value >= TimeSpan.Zero && value.TotalMilliseconds <= int.MaxValue);

    private static readonly StyledProperty<double> AnimationProgressProperty =
        AvaloniaProperty.Register<LoadingDots, double>("AnimationProgress");

    private readonly List<Visual> _ancestors = [];
    private readonly Style _animationStyle;
    private readonly NativeAnimation _animation;
    private IDisposable? _activationTimer, _surfaceObserver;
    private bool _attached, _activationReady, _animationRunning, _surfaceMotionAllowed = true;

    static LoadingDots()
    {
        ForegroundProperty.OverrideDefaultValue<LoadingDots>(
            new SolidColorBrush(Color.Parse("#45C6B0")));
        AffectsRender<LoadingDots>(IsActiveProperty, AnimationsEnabledProperty,
            ForegroundProperty, AnimationProgressProperty);
    }

    public LoadingDots()
    {
        IsHitTestVisible = false;
        _animation = new NativeAnimation
        {
            Duration = CycleDuration,
            IterationCount = IterationCount.Infinite,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(AnimationProgressProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(AnimationProgressProperty, 1d) } }
            }
        };
        _animationStyle = new Style(selector => selector.OfType<LoadingDots>().Class(":animating"))
        {
            Animations = { _animation }
        };
        // Add before parent styles are applied, and retain this definition. Adding a
        // style while active invalidates every Style-priority setter on this control,
        // including a parent-supplied ActivationDelay, which used to restart itself.
        // The selector controls the actual clock subscription, not the style collection.
        Styles.Add(_animationStyle);
    }

    /// <summary>False by default. Inactive controls draw nothing and consume no animation ticks.</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Duration of a complete fill-and-empty cycle; must be positive.</summary>
    public TimeSpan CycleDuration
    {
        get => GetValue(CycleDurationProperty);
        set => SetValue(CycleDurationProperty, value);
    }

    /// <summary>When false, an active indicator shows five steady dots without an animation clock.</summary>
    public bool AnimationsEnabled
    {
        get => GetValue(AnimationsEnabledProperty);
        set => SetValue(AnimationsEnabledProperty, value);
    }

    /// <summary>Defers the looping motion only. Actual activity always has a steady visible cue.</summary>
    public TimeSpan ActivationDelay
    {
        get => GetValue(ActivationDelayProperty);
        set => SetValue(ActivationDelayProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(64, 8);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!IsActive || Foreground is not { } brush)
            return;

        var scale = Math.Min(1, Math.Min(Bounds.Width / 64, Bounds.Height / 8));
        if (scale <= 0)
            return;

        var radius = 4 * scale;
        var left = (Bounds.Width - 64 * scale) / 2 + radius;
        var position = Math.Clamp(GetValue(AnimationProgressProperty), 0, 1) * 10;
        for (var index = 0; index < 5; index++)
        {
            // A delayed clock or disabled input must not erase feedback for real work.
            // Until motion can run, show the same steady cue used for reduced motion.
            var brightness = _activationReady && AnimationsEnabled && _surfaceMotionAllowed && IsEffectivelyEnabled
                ? SmoothStep(position - index) - SmoothStep(position - 5 - index)
                : 1;
            using (context.PushOpacity(0.18 + 0.82 * brightness))
                context.DrawEllipse(brush, null,
                    new Point(left + index * 14 * scale, Bounds.Height / 2), radius, radius);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        // Effective visibility has no change event in Avalonia 11. Observe the actual
        // ancestor properties so hiding a parent also releases the animation clock.
        for (var parent = this.GetVisualParent(); parent is not null; parent = parent.GetVisualParent())
        {
            _ancestors.Add(parent);
            parent.PropertyChanged += AncestorPropertyChanged;
        }
        if (TopLevel.GetTopLevel(this) is { } surface)
            _surfaceObserver = EditorMotionPreferences.Observe(surface, allowed =>
            {
                _surfaceMotionAllowed = allowed;
                UpdateAnimation();
                InvalidateVisual();
            });
        UpdateAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        CancelActivation();
        StopAnimation();
        _surfaceObserver?.Dispose();
        _surfaceObserver = null;
        foreach (var ancestor in _ancestors)
            ancestor.PropertyChanged -= AncestorPropertyChanged;
        _ancestors.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty || change.Property == AnimationsEnabledProperty ||
            change.Property == IsVisibleProperty || change.Property == IsEffectivelyEnabledProperty)
            UpdateAnimation();
        else if (change.Property == CycleDurationProperty || change.Property == ActivationDelayProperty)
        {
            if (change.Property == ActivationDelayProperty) CancelActivation();
            StopAnimation();
            UpdateAnimation();
        }
    }

    private void AncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsVisibleProperty || change.Property == Window.WindowStateProperty)
            UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        if (!_attached || !IsActive || !IsVisible ||
            _ancestors.Exists(ancestor => !ancestor.IsVisible || ancestor is Window { WindowState: WindowState.Minimized }))
        {
            CancelActivation();
            StopAnimation();
            return;
        }

        if (!IsEffectivelyEnabled || !AnimationsEnabled || !_surfaceMotionAllowed)
        {
            CancelActivation();
            StopAnimation();
            return;
        }

        if (!_activationReady)
        {
            if (ActivationDelay > TimeSpan.Zero)
            {
                _activationTimer ??= DispatcherTimer.RunOnce(() =>
                {
                    _activationTimer = null;
                    SetActivationReady(true);
                    UpdateAnimation();
                }, ActivationDelay, DispatcherPriority.Render);
                return;
            }
            SetActivationReady(true);
        }

        if (_animationRunning)
            return;

        _animation.Duration = CycleDuration;
        _animationRunning = true;
        PseudoClasses.Set(":animating", true);
    }

    private void StopAnimation()
    {
        PseudoClasses.Set(":animating", false);
        _animationRunning = false;
        // Unmatching the selector disposes the animation and restores its default
        // progress; keep the style definition so parent setters never get invalidated.
    }

    private void CancelActivation()
    {
        _activationTimer?.Dispose();
        _activationTimer = null;
        SetActivationReady(false);
    }

    private void SetActivationReady(bool ready)
    {
        if (_activationReady == ready) return;
        _activationReady = ready;
        InvalidateVisual();
    }

    private static double SmoothStep(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }
}
