using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class OutlineEditorTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static readonly string Root = MdaWorkRoot.Path("settings/20261113/outline659");
    private readonly string directory = Path.Combine(Root, "editor-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ActualMaterialControlsApplyIndependentWidthsWithoutClassifyingMaterialsAndUndoTogether()
        => await session.Dispatch(async () =>
        {
            EditorLocalization.SetLanguage(EditorLocalization.CurrentLanguage);
            using var workspace = Workspace("controls-cache");
            Guid id = AddModel(workspace, out var sourceBytes);
            var owner = new Window();
            var dialog = new EditorModelPropertiesWindow(workspace, id, initialTabIndex: 1);
            owner.Show();
            var result = dialog.ShowDialog<EditorModelPropertiesDraft?>(owner);
            try
            {
                Dispatcher.UIThread.RunJobs();
                var model = dialog.ViewModel;
                var materials = Assert.Single(dialog.GetVisualDescendants().OfType<ListBox>(),
                    list => ReferenceEquals(list.ItemsSource, model.Materials));
                Assert.Equal(2, materials.ItemCount);
                Assert.All(model.Materials, row => Assert.Equal(0, row.RoleIndex));
                var numeric = Named<EditorNumericUpDown>(dialog, "MaterialOutlineScaleBox");
                Assert.True(numeric.IsEffectivelyVisible);
                Assert.True(numeric.IsEffectivelyEnabled); // No role mapping is required to edit width.
                Assert.Equal(100m, numeric.Value);
                Assert.Equal(0m, numeric.Minimum);
                Assert.Equal(400m, numeric.Maximum);

                Click(Named<Button>(dialog, "MaterialOutlineQuarterButton"));
                Assert.Equal(25m, model.Materials[0].OutlinePercent);
                Assert.Equal(25m, numeric.Value);
                materials.SelectedIndex = 1;
                Dispatcher.UIThread.RunJobs();
                numeric = Named<EditorNumericUpDown>(dialog, "MaterialOutlineScaleBox");
                Assert.Equal(100m, numeric.Value);
                Click(Named<Button>(dialog, "MaterialOutlineQuarterButton"));
                Assert.Equal(25m, model.Materials[1].OutlinePercent);
                Click(Named<Button>(dialog, "MaterialOutlineResetButton"));
                Assert.Equal(100m, model.Materials[1].OutlinePercent);
                numeric.Value = 175m;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(175m, model.Materials[1].OutlinePercent);
                materials.SelectedIndex = 0;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(25m, Named<EditorNumericUpDown>(dialog, "MaterialOutlineScaleBox").Value);
                materials.SelectedIndex = 1;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(175m, Named<EditorNumericUpDown>(dialog, "MaterialOutlineScaleBox").Value);
                Assert.Empty(workspace.MaterialBindings); // The visible dialog is still a detached draft.
                SaveScreenshot(dialog);

                Click(dialog.FindControl<Button>("ApplyModelPropertiesButton")!);
                var draft = Assert.IsType<EditorModelPropertiesDraft>(await result.WaitAsync(TimeSpan.FromSeconds(10), Token));
                AssertWidths(draft.MaterialProfile!, .25f, 1.75f);
                workspace.ApplyModelProperties(draft);
                AssertWidths(workspace.MaterialBindings[id], .25f, 1.75f);
                Assert.True(workspace.Undo());
                Assert.Empty(workspace.MaterialBindings);
                Assert.All(new EditorModelPropertiesViewModel(workspace, id).Materials, row => Assert.Equal(100m, row.OutlinePercent));
                Assert.True(workspace.Redo());
                AssertWidths(workspace.MaterialBindings[id], .25f, 1.75f);
                Assert.All(new EditorModelPropertiesViewModel(workspace, id).Materials, row => Assert.Equal(0, row.RoleIndex));
                Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(workspace.Session!.Objects.Single().SourcePath, Token));
            }
            finally
            {
                dialog.Close(); owner.Close(); Dispatcher.UIThread.RunJobs();
            }
            return true;
        }, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

    [Fact]
    public async Task MaterialWidthsSurvivePmaReopenAndIndependentRecoveryAfterTheSourceDocumentCloses()
        => await session.Dispatch(async () =>
        {
            await using var sourcePlugins = await BundledPluginsAsync("source-plugins");
            using var workspace = Workspace("source-cache", sourcePlugins);
            Guid id = AddModel(workspace, out var sourceBytes);
            string source = workspace.Session!.Objects.Single().SourcePath;
            var properties = new EditorModelPropertiesViewModel(workspace, id);
            properties.Materials[0].OutlinePercent = 25;
            properties.Materials[1].OutlinePercent = 0;
            Assert.True(properties.TryCreate(out var draft), properties.ValidationMessage);
            workspace.ApplyModelProperties(draft!);
            workspace.SetDefaultRenderingStyle(RenderingStyleId.WutheringWaves);
            string path = Path.Combine(directory, "outline.pma");
            workspace.Save(path, Token);
            byte[] expectedProfile = D3D12MaterialBindingProfileJson.Encode(workspace.MaterialBindings[id]);
            var animation = new EditorAnimationViewModel();
            var actor = Assert.Single(workspace.Motions);
            animation.SetActor(id, actor.Model, actor.Motion);
            var state = EditorRecoverySessionState.Capture(animation, new EditorSceneInspectorViewModel(), workspace.Motions);
            using var json = JsonDocument.Parse(state.ToJsonBytes());
            using var snapshot = workspace.CaptureRecoverySnapshot(Token);
            var store = new EditorRecoveryStore(Path.Combine(directory, "recovery"));
            using var lease = await store.CreateSessionAsync(null, "Outline recovery", Token);
            workspace.New();
            var checkpoint = await store.WriteCheckpointAsync(lease, new(null, "Outline recovery", 1, json.RootElement),
                (destination, token) => Task.Run(() => snapshot.Save(destination, token), token), Token);
            string movedSource = source + ".moved";
            File.Move(source, movedSource); // Opening either finished package must use its own frozen model bytes.
            lease.Dispose(); // Leave an abandoned session for the actual recovery discovery path.
            var available = Assert.Single(await new EditorRecoveryStore(store.RootDirectory).ListRecoverableAsync(Token));
            Assert.Equal(checkpoint.CheckpointId, Assert.Single(available.Checkpoints).CheckpointId);

            await using var reopenedPlugins = await BundledPluginsAsync("open-plugins");
            using var reopened = Workspace("open-cache", reopenedPlugins);
            reopened.Open(path, Token);
            AssertRestored(reopened, id, expectedProfile);
            Assert.False(reopened.IsDirty);
            await using var recoveryPlugins = await BundledPluginsAsync("recovery-plugins");
            using var recovered = Workspace("recovery-cache", recoveryPlugins);
            _ = recovered.OpenRecovery(checkpoint, Token);
            AssertRestored(recovered, id, expectedProfile);
            Assert.Null(recovered.ProjectPath);
            Assert.True(recovered.IsDirty);
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(movedSource, Token));
            return true;
        }, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

    [Fact]
    public async Task MaterialStyleTabShowsOnlyControlsUsedByTheSelectedStyleAndRole()
        => await session.Dispatch(async () =>
        {
            EditorLocalization.SetLanguage(EditorLocalization.CurrentLanguage);
            await using var plugins = await BundledPluginsAsync("role-plugins");
            using var workspace = Workspace("role-cache", plugins);
            Guid id = AddModel(workspace, out _);
            var dialog = new EditorModelPropertiesWindow(workspace, id, initialTabIndex: 1);
            try
            {
                dialog.Show();
                Dispatcher.UIThread.RunJobs();
                var model = dialog.ViewModel;
                var style = Named<ComboBox>(dialog, "ModelStyleSelector");
                var role = Named<ComboBox>(dialog, "MaterialRoleSelector");
                bool Visible(string name) => Named<StackPanel>(dialog, name).IsEffectivelyVisible;

                role.SelectedIndex = 2;
                Dispatcher.UIThread.RunJobs();
                Assert.False(Visible("FaceShadingSection")); // The model initially inherits MMD.
                var genshin = model.StyleChoices.Single(option => option.Reference?.StableId == "genshin");
                style.SelectedItem = genshin;
                Dispatcher.UIThread.RunJobs();
                Assert.True(Visible("FaceShadingSection"));
                Assert.False(Visible("EyeShadingSection"));
                Assert.False(Visible("PmxTreatmentSection"));
                role.SelectedIndex = 3;
                Dispatcher.UIThread.RunJobs();
                Assert.True(Visible("EyeShadingSection"));
                Assert.False(Visible("FaceShadingSection"));
                role.SelectedIndex = 4;
                Dispatcher.UIThread.RunJobs();
                Assert.True(Visible("PmxTreatmentSection"));
                Assert.False(Visible("EyeShadingSection"));

                var strength = Named<EditorValueSlider>(dialog, "HairHighlightStrengthSlider");
                var width = Named<EditorValueSlider>(dialog, "HairHighlightWidthSlider");
                Assert.Equal(.26m, strength.Number);
                Assert.Equal(.22m, width.Number);
                var material = model.SelectedMaterial!;
                material.PmxHairHighlightWidth = .31m;
                material.PmxHairHighlightStrength = .34m;
                strength.ResetToDefault();
                Assert.Equal(.26m, material.PmxHairHighlightStrength);
                Assert.Equal(.31m, material.PmxHairHighlightWidth);
                Click(Named<Button>(dialog, "ResetPmxTreatmentButton"));
                Assert.Equal(.22m, width.Number);
                Assert.True(model.TryCreate(out var unchangedTreatment));
                Assert.Null(Assert.Single(unchangedTreatment!.MaterialProfile!.Materials).PmxMaterialTreatment);

                var slider = Named<EditorValueSlider>(dialog, "MaterialOutlineScaleSlider");
                Assert.Equal(400d, slider.Maximum);
                Named<EditorNumericUpDown>(dialog, "MaterialOutlineScaleBox").Value = 325;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(325m, slider.Number);
                SaveScreenshot(dialog, "style-material-hair.png");
                Click(Named<Button>(dialog, "ClearMaterialMappingButton"));
                Assert.Equal(0, model.SelectedMaterial!.RoleIndex);
                Assert.Equal(100m, model.SelectedMaterial.OutlinePercent);
                Assert.Same(genshin, model.StyleChoices[model.StyleIndex]);
                Assert.Empty(workspace.MaterialBindings);
            }
            finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); }
            return true;
        }, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

    private static void AssertRestored(EditorProjectWorkspace workspace, Guid id, byte[] expectedProfile)
    {
        var profile = workspace.MaterialBindings[id];
        Assert.Equal(expectedProfile, D3D12MaterialBindingProfileJson.Encode(profile));
        AssertWidths(profile, .25f, 0);
        Assert.Equal(RenderingPresetReference.BuiltIn(RenderingStyleId.WutheringWaves), workspace.Session!.RenderingStyles.DefaultStyle);
        var properties = new EditorModelPropertiesViewModel(workspace, id);
        Assert.Equal(25m, properties.Materials[0].OutlinePercent);
        Assert.Equal(0m, properties.Materials[1].OutlinePercent);
        Assert.All(properties.Materials, row => Assert.Equal(0, row.RoleIndex));
    }

    private static void AssertWidths(D3D12MaterialBindingProfile profile, float first, float second)
    {
        Assert.Empty(profile.Materials);
        Assert.Equal(new[] { (0, first), (1, second) }, profile.OutlineWidths
            .OrderBy(row => row.LocalMaterialIndex).Select(row => (row.LocalMaterialIndex, row.Scale)).ToArray());
    }

    private EditorProjectWorkspace Workspace(string cache, EditorPluginService? plugins = null)
    {
        Directory.CreateDirectory(directory);
        var workspace = new EditorProjectWorkspace { PackageCacheDirectory = Path.Combine(directory, cache) };
        if (plugins is not null) workspace.AttachPluginService(plugins);
        return workspace;
    }

    private async Task<EditorPluginService> BundledPluginsAsync(string name)
    {
        string installDir = Path.Combine(directory, name);
        var service = new EditorPluginService(installDir, Path.Combine(installDir, "enabled.json"),
            new(new(0, 1, 0), 1, EditorLocalization.CurrentLanguage),
            [TestStylePlugins.BundledPluginsRoot], EditorPluginService.BundledDefaultPluginIds);
        await service.InitializeAsync(Token);
        return service;
    }

    private Guid AddModel(EditorProjectWorkspace workspace, out byte[] sourceBytes)
    {
        var original = EditorRecoveryPmaIntegrationTests.Model();
        var model = new PmxModel
        {
            Header = original.Header, Name = original.Name, EnglishName = original.EnglishName,
            Comment = original.Comment, EnglishComment = original.EnglishComment,
            Vertices = original.Vertices, Faces = [original.Faces[0], original.Faces[0]], Textures = [],
            Materials = [original.Materials[0], original.Materials[0]], Bones = original.Bones,
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
        string source = Path.Combine(directory, "two-materials.pmx");
        sourceBytes = PmxWriter.ToBytes(model);
        File.WriteAllBytes(source, sourceBytes);
        return workspace.AddModel(source, EditorProjectWorkspace.CreateModelAsset(model, source, Token), ModelRole.Character);
    }

    private static T Named<T>(Window window, string name) where T : Control
        => Assert.Single(window.GetVisualDescendants().OfType<T>(), control => control.Name == name);

    private static void Click(Button button)
    {
        Assert.True(button.IsEffectivelyVisible);
        Assert.True(button.IsEffectivelyEnabled);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void SaveScreenshot(Window dialog, string filename = "outline-material-dialog.png")
    {
        // Real CPU Skia rendering through Avalonia.Headless; no native HWND or GPU.
        var settled = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => settled.Continue = false;
        timer.Start();
        try { Dispatcher.UIThread.PushFrame(settled); } finally { timer.Stop(); }
        dialog.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height), new Vector(96, 96));
        bitmap.Render(dialog);
        bitmap.Save(Path.Combine(Root, filename));
    }

    public async ValueTask DisposeAsync()
    {
        try { await Task.Run(session.Dispose).ConfigureAwait(false); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
