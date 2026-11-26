using System.Numerics;
using Vector = Avalonia.Vector;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorParameterContextMenuTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Reset = "Mda.Text.Menu.ResetValue", Copy = "Mda.Text.Menu.CopyNumber", Paste = "Mda.Text.Menu.PasteNumber";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NumericTextOpensOneThemedMenuAndCommitsOnlyEditableValues(bool light) => await session.Dispatch(async () =>
    {
        var window = new EditorWindow(false);
        window.FindControl<EditorViewportHost>("SceneViewport")!.IsVisible = false;
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var number = new EditorNumericUpDown { Width = 120, Height = 32, VerticalAlignment = VerticalAlignment.Center,
            Value = 12.50m, Minimum = 3, Maximum = 90, MenuDefaultValue = 45 };
        var panel = (Grid)window.FindControl<Border>("ViewportPanel")!.Child!;
        Grid.SetRow(number, 1); panel.Children.Add(number);
        int commits = 0;
        number.MenuValueCommitted += (_, _) => commits++;
        window.Show();
        try
        {
            await window.Clipboard!.SetTextAsync("invalid");
            var text = number.GetVisualDescendants().OfType<TextBox>().Single();
            text.Focus();
            var menu = Open(window, text);
            Assert.True(Entry(menu, Reset).IsEffectivelyEnabled);
            Assert.False(Entry(menu, Paste).IsEffectivelyEnabled);
            Assert.Equal(window.ActualThemeVariant, menu.ActualThemeVariant);
            Assert.True(window.TryFindResource("EditorPanelSurface", window.ActualThemeVariant, out var background));
            Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(background).Color,
                Assert.IsAssignableFrom<ISolidColorBrush>(menu.Background).Color);
            SaveMenuEvidence(menu, light);
            Invoke(menu, Copy);
            Assert.Equal("12.5", await window.Clipboard.TryGetTextAsync());

            await window.Clipboard.SetTextAsync("1.2e2");
            Invoke(Open(window, text), Paste);
            Assert.Equal(90m, number.Value);
            Assert.Equal(1, commits);
            Invoke(Open(window, text), Reset);
            Assert.Equal(45m, number.Value);
            Assert.Equal(2, commits);

            number.Value = 70;
            number.IsReadOnly = true;
            menu = Open(window, text);
            Assert.True(Entry(menu, Copy).IsEffectivelyEnabled);
            Assert.False(Entry(menu, Paste).IsEffectivelyEnabled);
            Assert.False(Entry(menu, Reset).IsEffectivelyEnabled);
            menu.Close();
            Assert.Equal(2, commits);
            AssertMenuLayout(window, number);
        }
        finally { window.Close(); }
        return true;
    }, Token);

    [Fact]
    public async Task SliderWithoutDefaultKeepsAuthoredNumberWhenOpenedAndClampsPastedValue() => await session.Dispatch(async () =>
    {
        var slider = new EditorValueSlider { Minimum = 0, Maximum = 1, Number = 2.25m, Width = 240, Height = 40 };
        var window = new Window { Content = slider, Width = 320, Height = 100 };
        window.Show();
        try
        {
            await window.Clipboard!.SetTextAsync("-4");
            var menu = Open(window, slider);
            Assert.Equal(2.25m, slider.Number);
            Assert.Equal(2, menu.Items.Count);
            Assert.All(menu.Items, control => Assert.IsType<MenuItem>(control));
            Invoke(menu, Copy);
            Assert.Equal("2.25", await window.Clipboard.TryGetTextAsync());
            await window.Clipboard.SetTextAsync("-4");
            Invoke(Open(window, slider), Paste);
            Assert.Equal(0m, slider.Number);
        }
        finally { window.Close(); }
        return true;
    }, Token);

    [Fact]
    public async Task LogarithmicBrightnessCopiesAuthoredUnitsAndPasteCreatesOneUndo() => await session.Dispatch(async () =>
    {
        var window = new EditorWindow(false) { Height = 980, Width = 1480 };
        window.FindControl<EditorViewportHost>("SceneViewport")!.IsVisible = false;
        using var shell = new EditorShellViewModel();
        window.DataContext = shell;
        var path = Path.Combine(Path.GetTempPath(), "parameter-menu.pmx");
        shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(EditorRecoveryPmaIntegrationTests.Model(), path, Token), ModelRole.Prop);
        var light = new NativeSceneLight(Guid.NewGuid(), "fill", SceneLightKind.Spot, Vector3.One, Quaternion.Identity, Vector3.One) { Intensity = 2.5f };
        shell.Workspace.EditLighting(new(new SceneLightingSnapshot([light]), light.Id));
        shell.ShowInspectorPage(EditorInspectorPage.Lighting);
        window.Show();
        try
        {
            Flush(window);
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var slider = inspector.GetVisualDescendants().OfType<EditorLightParameterSlider>().Single(c => c.Name == "LightBrightnessSlider");
            slider.BringIntoView();
            int starts = 0, previews = 0, completions = 0;
            slider.EditStarted += (_, _) => starts++;
            slider.EditPreview += (_, _) => previews++;
            slider.EditCompleted += (_, _) => completions++;
            var menu = Open(window, slider);
            Invoke(menu, Copy);
            Assert.Equal("2.5", await window.Clipboard!.TryGetTextAsync());
            Assert.Equal(0, starts);
            await window.Clipboard!.SetTextAsync("4");
            Invoke(Open(window, slider), Paste);
            Assert.Equal(1, starts);
            Assert.Equal(1, previews);
            Assert.Equal(1, completions);
            Assert.Equal(light with { Intensity = 4 }, Assert.Single(shell.Workspace.Session!.Lighting.Lights));
            Assert.True(shell.Workspace.Undo());
            Assert.Equal(light, Assert.Single(shell.Workspace.Session.Lighting.Lights));
            Assert.True(shell.Workspace.Undo());
            Assert.Empty(shell.Workspace.Session.Lighting.Lights);
        }
        finally { window.DataContext = null; window.Close(); }
        return true;
    }, Token);

    [Fact]
    public async Task RightClickOnDirectionPadFlipsTheHemisphereWithoutOpeningAMenu() => await session.Dispatch(() =>
    {
        var initial = new Vector3(-.5f, -1, .5f);
        var pad = new EditorLightDirectionPad { Direction = initial };
        var window = new Window { Width = 200, Height = 160, Content = pad };
        pad.EditPreview += (_, value) => pad.Direction = value;
        int completions = 0;
        pad.EditCompleted += (_, _) => completions++;
        window.Show();
        try
        {
            Assert.Empty(RightClick(window, pad));
            Assert.Equal(initial with { Z = -initial.Z }, pad.Direction);
            Assert.Empty(RightClick(window, pad));
            Assert.Equal(initial, pad.Direction);
            Assert.Equal(2, completions);
        }
        finally { window.Close(); }
        return true;
    }, Token);

    [Fact]
    public async Task EmptyAndHiddenGroupsNeverProduceAnEmptyPopupOrDanglingSeparator() => await session.Dispatch(() =>
    {
        var owner = new Button();
        var window = new Window { Content = owner };
        window.Show();
        try
        {
            var first = new MenuItem { Header = "A" };
            var second = new MenuItem { Header = "B" };
            var hidden = new MenuItem { IsVisible = false };
            List<ContextMenu> opened = [];
            using var listener = ContextMenu.OpenedEvent.AddClassHandler<ContextMenu>((menu, _) => opened.Add(menu));
            EditorContextMenu.Open(owner, null, new Separator(), hidden, new Separator());
            Assert.Empty(opened);
            EditorContextMenu.Open(owner, null, new Separator(), first, new Separator(), hidden, new Separator(), second, new Separator());
            var menu = Assert.Single(opened);
            Assert.Collection(menu.Items, item => Assert.Same(first, item), item => Assert.IsType<Separator>(item), item => Assert.Same(second, item));
            menu.Close();
        }
        finally { window.Close(); }
        return true;
    }, Token);

    private static ContextMenu Open(Window window, Control owner) => Assert.Single(RightClick(window, owner));

    private static List<ContextMenu> RightClick(Window window, Control owner)
    {
        Flush(window);
        List<ContextMenu> opened = [];
        using var listener = ContextMenu.OpenedEvent.AddClassHandler<ContextMenu>((menu, _) => opened.Add(menu));
        Point position = owner.TranslatePoint(new Point(owner.Bounds.Width / 2, owner.Bounds.Height / 2), window)!.Value;
        window.MouseMove(position);
        window.MouseDown(position, MouseButton.Right);
        window.MouseUp(position, MouseButton.Right);
        Flush(window);
        return opened;
    }

    private static MenuItem Entry(ContextMenu menu, string key) =>
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, EditorLocalization.Text(key)));

    private static void Invoke(ContextMenu menu, string key)
    {
        var item = Entry(menu, key);
        Assert.True(item.IsEffectivelyEnabled);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Flush(Window window)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertMenuLayout(Window window, Control owner)
    {
        var toggle = EditorContextMenu.Item("Mda.Text.Menu.ResetValue", () => { }, gesture: "Ctrl+Z");
        toggle.ToggleType = MenuItemToggleType.CheckBox;
        toggle.IsChecked = true;
        var nested = new MenuItem { Header = "Options" };
        nested.Items.Add(EditorContextMenu.Item("Select all keyframes on the current animation track", () => { }));
        ContextMenu? opened = null;
        using var listener = ContextMenu.OpenedEvent.AddClassHandler<ContextMenu>((sender, _) => opened = sender);
        EditorContextMenu.Open(owner, null, toggle, new Separator(), nested);
        var menu = Assert.IsType<ContextMenu>(opened);
        Flush(window);
        var header = toggle.GetVisualDescendants().OfType<ContentPresenter>().Single(c => c.Name == "PART_HeaderPresenter");
        var label = header.TranslatePoint(default, menu)!.Value;
        var shortcut = toggle.GetVisualDescendants().OfType<TextBlock>().Single(c => c.Name == "PART_InputGestureText");
        Assert.True(label.X >= 6);
        Assert.True(shortcut.TranslatePoint(default, menu)!.Value.X >= label.X + header.Bounds.Width + 8);
        Assert.True(toggle.GetVisualDescendants().Single(c => c.Name == "EditorMenuCheck").IsEffectivelyVisible);
        Assert.True(nested.GetVisualDescendants().Single(c => c.Name == "EditorMenuChevron").IsEffectivelyVisible);
        nested.IsSubMenuOpen = true;
        Flush(window);
        var popup = nested.GetVisualDescendants().OfType<Popup>().Single();
        Assert.True(popup.IsOpen);
        var scroll = popup.Child!.GetVisualDescendants().OfType<ScrollViewer>().First();
        var childHeader = scroll.GetVisualDescendants().OfType<ContentPresenter>().First(c => c.Name == "PART_HeaderPresenter");
        var childLabel = childHeader.GetVisualDescendants().OfType<TextBlock>().First();
        double x = childLabel.TranslatePoint(default, scroll)!.Value.X;
        Assert.True(x >= 0 && x + childLabel.TextLayout.Width <= scroll.Bounds.Width + 1);
        menu.Close();
    }

    private static void SaveMenuEvidence(ContextMenu menu, bool light)
    {
        Assert.True(menu.Bounds.Width > 0 && menu.Bounds.Height > 0);
        using var image = new RenderTargetBitmap(PixelSize.FromSize(menu.Bounds.Size, 1), new Vector(96, 96));
        image.Render(menu);
        if (Environment.GetEnvironmentVariable("MDA_MENU_EVIDENCE") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        image.Save(Path.Combine(folder, light ? "numeric-menu-light.png" : "numeric-menu-dark.png"));
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
