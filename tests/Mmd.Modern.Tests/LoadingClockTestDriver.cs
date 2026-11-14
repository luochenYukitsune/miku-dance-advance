using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Mmd.Modern.App.Controls;
using Xunit;

namespace Mmd.Modern.Tests;

internal static class LoadingClockTestDriver
{
    internal static readonly StyledProperty<double> Progress = (StyledProperty<double>)typeof(LoadingDots)
        .GetField("AnimationProgressProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    internal static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        // RunJobs alone does not deliver pending platform timer events. This timeout
        // only exits the native dispatcher loop; it never activates or advances dots.
        using var timeout = new System.Threading.Timer(_ => Dispatcher.UIThread.Post(() => frame.Continue = false,
            DispatcherPriority.Send), null, milliseconds, Timeout.Infinite);
        using var cancellation = TestContext.Current.CancellationToken.Register(() => frame.Continue = false);
        Dispatcher.UIThread.PushFrame(frame);
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    internal static void AssertRunning(LoadingDots dots, string? diagnostic = null)
    {
        Assert.True(dots.Classes.Contains(":animating"), diagnostic);
        Assert.True(dots.IsAnimating(Progress), diagnostic);
    }

    internal static void AssertStopped(LoadingDots dots)
    {
        Assert.DoesNotContain(":animating", dots.Classes);
        Assert.False(dots.IsAnimating(Progress));
        double stopped = dots.GetValue(Progress);
        Pump(90);
        Assert.False(dots.IsAnimating(Progress));
        Assert.Equal(stopped, dots.GetValue(Progress));
    }
}
