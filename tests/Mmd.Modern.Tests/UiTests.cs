using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Mmd.Modern.Demo;
using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<global::Mmd.Modern.App.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

[Collection(QualityLoadingCollection.Name)]
public sealed class UiTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(TestAppBuilder.BuildAvaloniaApp);

    [Fact]
    public async Task FrameCameraValuesSurviveSliderAttachAndWindowReopen() => await session.Dispatch(() =>
    {
        using var vm = new FrameViewportViewModel(new(true)) { Yaw = 25, Pitch = 12, Zoom = 1.3 };
        for (int reopen = 0; reopen < 2; reopen++)
        {
            var window = new FrameViewportWindow(vm) { DisposeViewModelOnClose = false };
            window.Viewport.IsVisible = false; // Headless host cannot create a native child; exercise real Avalonia bindings.
            try
            {
                window.Show();
                Assert.Equal(25, vm.Yaw);
                Assert.Equal(12, vm.Pitch);
                Assert.Equal(1.3, vm.Zoom);
                var tools = (StackPanel)((ScrollViewer)((Grid)window.Content!).Children[1]).Content!;
                var sliders = tools.Children.OfType<Slider>().ToArray();
                Assert.Equal([25d, 12d, 1.3d, .5d], sliders.Select(s => s.Value));
                var strength = sliders[3];
                Assert.Equal(.1, strength.SmallChange);
                Assert.Equal(.5, strength.LargeChange);
                Assert.Equal(.1, strength.TickFrequency);
                Assert.True(strength.IsSnapToTickEnabled);
                Assert.Contains(tools.Children.OfType<TextBlock>(), t => t.Text == "GI 强度（0–12；手动开启默认 0.5）");
            }
            finally { window.Close(); }
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public async Task MissingModelReportsErrorAndReenablesCommand() => await session.Dispatch(async () =>
    {
        using var vm = new MainWindowViewModel("missing-self-fixture.pmd");
        var window = new MainWindow { DataContext = vm };
        await vm.RenderAsync();
        Assert.StartsWith("渲染失败：", vm.Status);
        Assert.True(vm.RenderCommand.CanExecute(null));
        Assert.Null(vm.Preview);
        Assert.NotNull(window.FindControl<Image>("Viewport"));
        window.Close();
        return true;
    }, CancellationToken.None);

    [Fact]
    [Trait("Category", "Gpu")]
    public async Task RealGpuFrameBindsToHeadlessAvaloniaImage() => await session.Dispatch(async () =>
    {
        using var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        await vm.RenderAsync();
        Assert.DoesNotContain("失败", vm.Status);
        Assert.NotNull(vm.Preview);
        Assert.Equal(960, vm.Preview.PixelSize.Width);
        Assert.Same(vm.Preview, window.FindControl<Image>("Viewport")!.Source);
        Assert.Contains("14 材质分组", vm.ModelSummary);
        vm.Dispose();
        Assert.False(vm.RenderCommand.CanExecute(null));
        window.Close();
        return true;
    }, CancellationToken.None);

    // A dispatched failure can resume xUnit on this worker; synchronous Dispose
    // joins that same worker. Match the other headless fixtures' off-thread join.
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
