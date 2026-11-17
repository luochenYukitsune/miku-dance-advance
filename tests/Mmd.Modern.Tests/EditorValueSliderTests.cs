using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Mmd.Modern.App.Controls;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorValueSliderTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));

    private sealed class Holder { public decimal? Number { get; set; } }

    private static (Window Window, EditorValueSlider Slider) Host(decimal? number)
    {
        var slider = new EditorValueSlider { Minimum = 0, Maximum = 1, SmallChange = .1, Step = .01, DefaultValue = .35, Number = number, Width = 200 };
        var window = new Window { Width = 300, Height = 100, Content = slider };
        window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        return (window, slider);
    }

    [Fact]
    public async Task OutOfRangeValuesAreShownClampedButNeverRewritten() => await session.Dispatch(() =>
    {
        var (window, slider) = Host(3.5m);
        try
        {
            Assert.Equal(1, slider.Value);
            Assert.Equal(3.5m, slider.Number);
            slider.Number = -2m; Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, slider.Value); Assert.Equal(-2m, slider.Number);
            slider.Number = null; Dispatcher.UIThread.RunJobs();
            Assert.Null(slider.Number);
            return true;
        }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task KeyboardGestureWritesRoundedValueAndEscapeRestoresTheStart() => await session.Dispatch(() =>
    {
        var (window, slider) = Host(.5m);
        try
        {
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            slider.Value = .7234;
            Assert.Equal(.72m, slider.Number);
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.Equal(.5m, slider.Number);
            // Outside a gesture, programmatic Value changes do not write back.
            slider.Value = .1;
            Assert.Equal(.5m, slider.Number);
            return true;
        }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task DoubleClickResetsAndWheelOnlyAdjustsWhenFocused() => await session.Dispatch(() =>
    {
        var (window, slider) = Host(.8m);
        try
        {
            slider.ResetToDefault();
            Assert.Equal(.35m, slider.Number);
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            PointerWheelEventArgs Wheel(KeyModifiers modifiers) => new(slider, pointer, window, new Point(10, 10), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), modifiers, new Vector(0, 1))
                { RoutedEvent = InputElement.PointerWheelChangedEvent };
            slider.RaiseEvent(Wheel(KeyModifiers.None));
            Assert.Equal(.35m, slider.Number); // unfocused: the wheel scrolls the panel instead
            slider.Focus(); Dispatcher.UIThread.RunJobs();
            slider.RaiseEvent(Wheel(KeyModifiers.None));
            Assert.Equal(.45m, slider.Number);
            slider.RaiseEvent(Wheel(KeyModifiers.Shift));
            Assert.Equal(.46m, slider.Number);
            return true;
        }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
