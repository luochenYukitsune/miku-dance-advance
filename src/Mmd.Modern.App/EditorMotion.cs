using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace Mmd.Modern.App;

/// <summary>Quiet hover/selection feedback; geometry, focus borders and text stay immediate.</summary>
public static class EditorMotion
{
    public static readonly AttachedProperty<bool> FeedbackProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Feedback", typeof(EditorMotion));

    private static readonly ConditionalWeakTable<Control, FeedbackObserver> observers = new();

    static EditorMotion() => FeedbackProperty.Changed.AddClassHandler<Control>((control, args) =>
    {
        if (args.GetNewValue<bool>()) observers.GetValue(control, static value => new FeedbackObserver(value));
        else if (observers.TryGetValue(control, out var observer))
        {
            observer.Dispose();
            observers.Remove(control);
        }
    });

    public static bool GetFeedback(Control control) => control.GetValue(FeedbackProperty);
    public static void SetFeedback(Control control, bool value) => control.SetValue(FeedbackProperty, value);

    private sealed class FeedbackObserver : IDisposable
    {
        private readonly Control control;
        private readonly Transitions? previousTransitions;
        private readonly Transitions transitions;
        private readonly List<Visual> ancestors = [];
        private IDisposable? surfaceObserver;
        private bool attached, motionAllowed;

        internal FeedbackObserver(Control control)
        {
            this.control = control;
            previousTransitions = control.Transitions;
            transitions = new Transitions
            {
                new BrushTransition
                {
                    Property = control is Border ? Border.BackgroundProperty : TemplatedControl.BackgroundProperty,
                    Duration = TimeSpan.FromMilliseconds(110), Easing = new CubicEaseOut()
                }
            };
            control.AttachedToVisualTree += Attached;
            control.DetachedFromVisualTree += Detached;
            control.PropertyChanged += ControlChanged;
            if (control.GetVisualRoot() is not null) Attach();
        }

        private void Attached(object? sender, VisualTreeAttachmentEventArgs args) => Attach();
        private void Attach()
        {
            if (attached) return;
            attached = true;
            for (var parent = control.GetVisualParent(); parent is not null; parent = parent.GetVisualParent())
            {
                ancestors.Add(parent);
                parent.PropertyChanged += AncestorChanged;
            }
            if (TopLevel.GetTopLevel(control) is { } surface)
                surfaceObserver = EditorMotionPreferences.Observe(surface, allowed => { motionAllowed = allowed; Apply(); });
            Apply();
        }

        private void ControlChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == Visual.IsVisibleProperty || args.Property == Control.IsEffectivelyEnabledProperty ||
                args.Property == Button.IsPressedProperty)
                Apply();
        }

        private void AncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == Visual.IsVisibleProperty) Apply();
        }

        private void Apply()
        {
            var allowed = attached && motionAllowed && control.IsVisible && control.IsEffectivelyEnabled &&
                control is not Button { IsPressed: true } && !ancestors.Exists(value => !value.IsVisible);
            // Replacing the collection disposes an in-flight Avalonia transition immediately.
            // Only Background is interpolated: keyboard focus and pressed borders never wait.
            control.SetCurrentValue(Animatable.TransitionsProperty, allowed ? transitions : null);
        }

        private void Detached(object? sender, VisualTreeAttachmentEventArgs args) => Detach();
        private void Detach()
        {
            attached = false;
            surfaceObserver?.Dispose();
            surfaceObserver = null;
            foreach (var ancestor in ancestors) ancestor.PropertyChanged -= AncestorChanged;
            ancestors.Clear();
            Apply();
        }

        public void Dispose()
        {
            Detach();
            control.AttachedToVisualTree -= Attached;
            control.DetachedFromVisualTree -= Detached;
            control.PropertyChanged -= ControlChanged;
            control.SetCurrentValue(Animatable.TransitionsProperty, previousTransitions);
        }
    }
}
