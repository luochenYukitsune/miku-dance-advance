using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorViewportContextMenuTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Fact]
    public async Task SecondaryPointerClickAndReturningDragUseDifferentActions() => await session.Dispatch(() =>
    {
        var surface = new Border { Background = Brushes.Transparent, Focusable = true };
        var window = new Window { Width = 240, Height = 140, Content = surface };
        List<Point> menus = [];
        List<Vector> pans = [];
        int starts = 0, reverts = 0;
        var gesture = new ViewportContextGesture(surface, () => starts++, pans.Add, menus.Add, () => reverts++);
        window.Show();
        try
        {
            var origin = new Point(50, 50);
            window.MouseDown(origin, MouseButton.Right);
            window.MouseMove(origin + new Vector(2, 2));
            window.MouseUp(origin + new Vector(2, 2), MouseButton.Right);
            Assert.Equal(origin + new Vector(2, 2), Assert.Single(menus));
            Assert.Empty(pans);
            window.MouseDown(origin, MouseButton.Right);
            window.MouseMove(origin + new Vector(3, 4));
            window.MouseMove(origin);
            window.MouseUp(origin, MouseButton.Right);
            Assert.Single(menus);
            Assert.Equal([new Vector(3, 4), default], pans);
            Assert.Equal(2, starts);

            gesture.Begin(origin, 10);
            gesture.Move(origin + new Vector(-20, 10));
            gesture.Revert();
            gesture.End(origin, 40);
            Assert.Equal(1, reverts);
            gesture.Begin(origin, 100);
            gesture.End(origin, 60_100);
            gesture.Begin(origin, 10);
            gesture.SuppressMenu();
            gesture.End(origin, 30);
            Assert.Single(menus);
            return true;
        }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ViewportMenuTracksWorldGuidesAndTheSelectedSceneTrack() => await session.Dispatch(() =>
    {
        var shell = new EditorShellViewModel();
        var window = new EditorWindow(false);
        window.FindControl<EditorViewportHost>("SceneViewport")!.IsVisible = false;
        window.DataContext = shell;
        window.Show();
        try
        {
            var guides = Find("Mda.Text.Ux.View.ShowWorldGuides");
            Assert.Equal(shell.ShowWorldGuides, guides.IsChecked);
            bool before = shell.ShowWorldGuides;
            guides.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.NotEqual(before, shell.ShowWorldGuides);
            Assert.Equal(shell.ShowWorldGuides, Find("Mda.Text.Ux.View.ShowWorldGuides").IsChecked);
            Assert.Equal(shell.ShowWorldGuides, window.FindControl<MenuItem>("ShowWorldGuidesMenuItem")!.IsChecked);
            Assert.False(Find("Mda.Text.Menu.FocusSelection").IsEnabled);
            Assert.DoesNotContain(window.ViewportMenuItems().OfType<MenuItem>(),
                item => Equals(item.Header, EditorLocalization.Text("Mda.Text.a619bd96a115")));
            var path = Path.Combine(Path.GetTempPath(), "viewport-menu.pmx");
            shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(EditorRecoveryPmaIntegrationTests.Model(),
                path, TestContext.Current.CancellationToken), Mmd.Modern.Application.ModelRole.Prop);
            shell.SceneSelectionIndex = 1;
            Assert.True(Find("Mda.Text.a619bd96a115").IsEnabled);
            Assert.Equal(7, Find("Mda.Text.0bb6877c08de").Items.OfType<MenuItem>().Count());
            return true;
        }
        finally { window.DataContext = null; window.Close(); shell.Dispose(); Dispatcher.UIThread.RunJobs(); }

        MenuItem Find(string key) => window.ViewportMenuItems().OfType<MenuItem>().Single(
            item => Equals(item.Header, EditorLocalization.Text(key)));
    }, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
