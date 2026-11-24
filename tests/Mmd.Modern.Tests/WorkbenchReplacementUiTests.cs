using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class WorkbenchReplacementUiTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ReplacementRefreshesSelectorBrowserAndPropertiesWithTheNewSourceName() => await session.Dispatch(() =>
    {
        var (window, shell) = Editor();
        try
        {
            var (asset, path) = Model("Original");
            var id = shell.Workspace.AddModel(path, asset, ModelRole.Character);
            shell.RefreshWorkspace(id);
            var (replacement, newPath) = Model("Replacement");
            var plan = shell.Workspace.Session!.PrepareModelReplacement(id, newPath, replacement, TestContext.Current.CancellationToken);
            shell.Workspace.ApplyModelReplacement(plan, false, false, TestContext.Current.CancellationToken);
            shell.RefreshWorkspace(id);
            var selector = window.FindControl<ComboBox>("CurrentModelSelector")!;
            Assert.Equal("Replacement", shell.Browser.SelectedModel!.Name);
            Assert.Equal("1. Replacement", shell.Browser.SelectedModel.SelectionLabel);
            Assert.Same(shell.Browser.SelectedModel, selector.SelectedItem);
            var properties = new EditorModelPropertiesViewModel(shell.Workspace, id);
            Assert.Equal("Replacement", properties.Name); Assert.Equal(newPath, properties.SourcePath);
            Assert.True(shell.Workspace.Undo()); shell.RefreshWorkspace(id);
            Assert.Equal("Original", shell.Browser.SelectedModel!.Name);
            Assert.True(shell.Workspace.Redo()); shell.RefreshWorkspace(id);
            Assert.Equal("Replacement", shell.Browser.SelectedModel!.Name);
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("zh-CN")] [InlineData("en")] [InlineData("ja")]
    public async Task RightSelectorSwitchesExactInstancesWithoutViewportPickingAndKeepsPages(string language)
        => await session.Dispatch(() =>
        {
            var (window, shell) = Editor(); string previous = EditorLocalization.CurrentLanguage;
            try
            {
                EditorLocalization.SetLanguage(language);
                var (asset, path) = Model();
                shell.Workspace.AddModel(path, asset, ModelRole.Character);
                var second = shell.Workspace.AddModel(path, asset, ModelRole.Character);
                shell.RefreshWorkspace();
                var selector = window.FindControl<ComboBox>("CurrentModelSelector")!;
                var tabs = window.FindControl<TabControl>("InspectorPageTabs")!;
                int page = shell.RightInspectorPageIndex;
                Assert.Equal(2, selector.ItemCount); Assert.NotEqual(shell.Browser.Models[0].SelectionLabel, shell.Browser.Models[1].SelectionLabel);
                selector.SelectedItem = shell.Browser.Models.Single(m => m.ObjectId == second);
                Assert.Equal(second, shell.Browser.SelectedModel!.ObjectId);
                Assert.Equal(page, tabs.SelectedIndex);
                Assert.True(window.FindControl<Button>("InspectorReplaceModelButton")!.IsEnabled);
                Assert.True(window.FindControl<EditorModelBrowser>("ModelBrowserSurface")!.FindControl<Button>("ReplaceModelButton")!.IsEnabled);
                Assert.Equal(EditorLocalization.Text("Mda.Text.Context.ReplaceModel.Current"), AutomationProperties.GetName(selector));
                window.Width = 1100; window.Height = 720;
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                Assert.True(selector.Bounds.Width >= 140, $"The current model selector only has {selector.Bounds.Width} px in {language}.");
                Assert.Equal(EditorLocalization.Text("Mda.Text.814c34f6f2d1"), AutomationProperties.GetName(window.FindControl<Button>("InspectorModelPropertiesButton")!));
                var stylePage = window.FindControl<ScrollViewer>("StyleInspectorPage")!;
                var modelPreset = window.FindControl<ComboBox>("ModelStyleOverrideBox")!;
                Assert.Equal(EditorLocalization.Text("Mda.Text.Layout.FollowProjectStyle"), modelPreset.PlaceholderText);
                var presetBottom = modelPreset.TranslatePoint(new Avalonia.Point(0, modelPreset.Bounds.Height), stylePage);
                Assert.NotNull(presetBottom);
                Assert.InRange(presetBottom.Value.Y, modelPreset.Bounds.Height, stylePage.Bounds.Height);
                var materialSettings = window.FindControl<Expander>("StyleMaterialSettings")!;
                Assert.False(materialSettings.IsExpanded);
                materialSettings.IsExpanded = true; window.UpdateLayout();
                Assert.True(window.FindControl<Button>("StyleModelPropertiesButton")!.IsEffectivelyVisible);

                return true;
            }
            finally { EditorLocalization.SetLanguage(previous); Close(window); }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ReplacementDialogCancellationIsExplicitAndDoesNotModifyTheWorkspace() => await session.Dispatch(async () =>
    {
        var (window, shell) = Editor();
        try
        {
            var (asset, path) = Model(); var id = shell.Workspace.AddModel(path, asset, ModelRole.Character);
            shell.RefreshWorkspace(); var frame = shell.Workspace.Session!.Frame; bool dirty = shell.Workspace.IsDirty;
            var plan = shell.Workspace.Session.PrepareModelReplacement(id, path, asset, TestContext.Current.CancellationToken);
            var type = typeof(EditorWindow).Assembly.GetType("Mmd.Modern.App.EditorModelReplacementDialog")!;
            var task = (Task<bool?>)type.GetMethod("ConfirmAsync")!.Invoke(null, [window, "example", plan, true])!;
            var dialog = Assert.Single(window.OwnedWindows);
            Assert.Equal("ModelReplacementDialog", dialog.Name);
            Assert.True(dialog.IsVisible);
            // This dialog is constructed in C#, so its named controls are logical
            // descendants rather than registered entries in a XAML name scope.
            Assert.False(NamedDescendant<CheckBox>(dialog, "RestoreCompatibleUnownedCheck").IsChecked);
            Assert.True(NamedDescendant<Button>(dialog, "ConfirmModelReplacement").IsEnabled);
            var cancel = NamedDescendant<Button>(dialog, "CancelModelReplacement");
            Assert.True(cancel.IsEnabled);
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(await task.WaitAsync(DialogTimeout, TestContext.Current.CancellationToken));
            Assert.False(dialog.IsVisible); Assert.DoesNotContain(dialog, window.OwnedWindows);
            Assert.Same(frame, shell.Workspace.Session.Frame); Assert.Equal(dirty, shell.Workspace.IsDirty);
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task MotionClocksStopOnHideDisableAndDetachWhileBusySemanticsRemainImmediate() => await session.Dispatch(() =>
    {
        var window = new Window(); var parent = new StackPanel(); var button = new Button { Content = "Action" };
        EditorMotion.SetFeedback(button, true);
        var dots = new LoadingDots { IsActive = true, ActivationDelay = TimeSpan.FromMilliseconds(25) };
        parent.Children.Add(button); parent.Children.Add(dots); window.Content = parent; window.Show();
        try
        {
            LoadingClockTestDriver.Pump(150); LoadingClockTestDriver.AssertRunning(dots);
            Assert.NotNull(button.Transitions);
            parent.IsVisible = false;
            Assert.Null(button.Transitions); LoadingClockTestDriver.AssertStopped(dots);
            parent.IsVisible = true; button.IsEnabled = false; Assert.Null(button.Transitions);
            button.IsEnabled = true; Assert.NotNull(button.Transitions);
            LoadingClockTestDriver.Pump(100); LoadingClockTestDriver.AssertRunning(dots);
            parent.IsEnabled = false; LoadingClockTestDriver.AssertStopped(dots);
            parent.IsEnabled = true;
            LoadingClockTestDriver.Pump(100); LoadingClockTestDriver.AssertRunning(dots);
            window.WindowState = WindowState.Minimized; LoadingClockTestDriver.AssertStopped(dots);
            window.WindowState = WindowState.Normal;
            LoadingClockTestDriver.Pump(100); LoadingClockTestDriver.AssertRunning(dots);
            dots.IsActive = false;
            LoadingClockTestDriver.AssertStopped(dots);
            dots.AnimationsEnabled = false; dots.IsActive = true;
            LoadingClockTestDriver.AssertStopped(dots); // Real busy stays static with client motion disabled.
            dots.AnimationsEnabled = true;
            LoadingClockTestDriver.Pump(100); LoadingClockTestDriver.AssertRunning(dots);
            parent.Children.Clear(); Assert.Null(button.Transitions); LoadingClockTestDriver.AssertStopped(dots);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    private static (EditorWindow, EditorShellViewModel) Editor()
    {
        var window = new EditorWindow(false); window.FindControl<EditorViewportHost>("SceneViewport")!.IsVisible = false;
        var shell = new EditorShellViewModel(); window.DataContext = shell; window.Show(); return (window, shell);
    }
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mda-replacement-ui-" + Guid.NewGuid().ToString("N"));
    private (SceneAnimationModelAsset, string) Model(string? name = null)
    {
        Directory.CreateDirectory(directory);
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd"));
        var model = PmdPmxAdapter.Convert(PmdReader.Read(file));
        if (name is not null) model = new PmxModel
        {
            Header = model.Header, Name = name, EnglishName = model.EnglishName, Comment = model.Comment, EnglishComment = model.EnglishComment,
            Vertices = model.Vertices, Faces = model.Faces, Textures = model.Textures, Materials = model.Materials, Bones = model.Bones,
            Morphs = model.Morphs, DisplayFrames = model.DisplayFrames, RigidBodies = model.RigidBodies, Joints = model.Joints, SoftBodies = model.SoftBodies,
        };
        var path = Path.Combine(directory, (name ?? "model") + ".pmx");
        File.WriteAllBytes(path, PmxWriter.ToBytes(model)); return (EditorProjectWorkspace.CreateModelAsset(model, path), path);
    }
    private static void Close(EditorWindow window)
    {
        // Bypass the editor's unsaved-project prompt before any modal cleanup can
        // reactivate it, including when an earlier assertion failed.
        typeof(EditorWindow).GetField("_closingConfirmed", Hidden)!.SetValue(window, true);
        try { foreach (var child in window.OwnedWindows.ToArray()) CloseOwnedWindow(child); }
        finally
        {
            try { window.Close(); }
            finally { Dispatcher.UIThread.RunJobs(); }
        }
    }
    private static T NamedDescendant<T>(Window window, string name) where T : Control =>
        Assert.Single(window.GetLogicalDescendants().OfType<T>(), control => control.Name == name);
    private static void CloseOwnedWindow(Window window)
    {
        try { foreach (var child in window.OwnedWindows.ToArray()) CloseOwnedWindow(child); }
        finally { window.Close(); }
    }
    public async ValueTask DisposeAsync()
    {
        // Dispatch can complete a failed test inline on its own worker. Dispose
        // joins that worker, so move the join off it before xUnit awaits cleanup.
        try { await Task.Run(session.Dispose).ConfigureAwait(false); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
