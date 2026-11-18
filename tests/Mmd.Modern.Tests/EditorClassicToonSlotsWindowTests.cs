using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Tests;

// 十槽并列窗口：三语、最小尺寸不裁切、状态显示，以及缺少原版资源时禁用“恢复默认”。
[Collection(QualityLoadingCollection.Name)]
public sealed class EditorClassicToonSlotsWindowTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Theory]
    [InlineData("zh-CN", false)] [InlineData("en", true)] [InlineData("ja", false)]
    public async Task TenSlotsShowSourceAndFitTheMinimumWindow(string language, bool light)
        => await session.Dispatch(() =>
    {
        var token = TestContext.Current.CancellationToken;
        string before = EditorLocalization.CurrentLanguage;
        string directory = Path.Combine(MdaWorkRoot.Path("classic/20261117/toon-slots"), "window", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        EditorLocalization.SetLanguage(language);
        Window? owner = null; EditorClassicToonSlotsWindow? dialog = null;
        try
        {
            // 材质 1、2 共用 Common 槽 5，材质 0 用默认 Toon；材质 1 已被替换。
            var model = EditorClassicToonPackageTests.SlotModel();
            string path = Path.Combine(directory, "model.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
            var asset = EditorProjectWorkspace.CreateModelAsset(model, path, token);
            var composition = new SceneCompositionEditor();
            Guid other = composition.Add("Other", path, ModelRole.Character, asset.Source).Id;
            Guid id = composition.Add("Actor", path, ModelRole.Character, asset.Source).Id;
            var seed = EditorClassicToonPackageTests.SlotPackage([other, id]);
            byte[] replacement = EditorClassicToonPackageTests.SlotBitmap(200);
            var toons = seed.Replace(id, model, [1], replacement, "chosen.bmp", token);
            composition.ReplaceMaterialSource(other, seed.Apply(other, asset, path, token).Source);
            composition.ReplaceMaterialSource(id, toons.Apply(id, asset, path, token).Source);
            using var project = new SceneProjectSession(composition, new(),
                [new(other, model, new VmdMotion { ModelName = model.Name }, PhysicsEnabled: false),
                    new(id, model, new VmdMotion { ModelName = model.Name }, PhysicsEnabled: false)], new NoPhysics());
            var constructor = typeof(EditorClassicDocumentState).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(value => value.GetParameters()[0].ParameterType == typeof(SceneProjectSession));
            var metadata = ((EditorClassicDocumentState)constructor.Invoke([project, null, null, null, null, null, false]))
                .WithToons(project, toons);
            string[] names = ["Mmd.Modern.D3D9.Compatibility.dll", "d3dx9_43.dll", "d3dcompiler_43.dll"];
            foreach (string name in names) File.WriteAllBytes(Path.Combine(directory, name), [1]);
            // 未提供本机原版 MMD 资源，相当于“仅打开 PMC”。
            var engine = new EditorClassicRenderEngine(new(Path.Combine(directory, names[0]),
                Path.Combine(directory, names[1]), Path.Combine(directory, names[2])), directory);
            using var workspace = new EditorProjectWorkspace(engine);
            typeof(EditorProjectWorkspace).GetProperty("Session")!.SetValue(workspace, project);
            typeof(EditorProjectWorkspace).GetProperty("ProjectMetadata", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace, metadata);
            Assert.Null(workspace.ClassicToonResources);

            owner = new Window { RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
            owner.Show();
            dialog = new EditorClassicToonSlotsWindow(owner, workspace, id);
            dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
            dialog.Show(owner); dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(owner.ActualThemeVariant, dialog.ActualThemeVariant);
            Assert.Equal(id, dialog.SelectedModelId);
            Assert.Equal(EditorLocalization.Text("Mda.Text.Mme.ToonSlots.Title"), dialog.Title);

            var texts = dialog.GetVisualDescendants().OfType<TextBlock>().ToArray();
            Assert.All(texts, text => Assert.DoesNotContain("Mda.Text.", text.Text ?? ""));
            string[] headers = Enumerable.Range(1, 10).Select(slot => $"Toon{slot:00}").ToArray();
            Assert.Equal(headers, texts.Select(text => text.Text).Where(text => text is not null && headers.Contains(text)));
            Assert.Single(texts, text => text.Text == EditorLocalization.Text("Mda.Text.Mme.ToonSlots.Replaced"));
            Assert.Equal(9, texts.Count(text => text.Text == EditorLocalization.Text("Mda.Text.Mme.ToonSlots.Unused")));
            Assert.Contains(texts, text => text.Text == EditorLocalization.Text("Mda.Text.Mme.ToonSlots.NoOriginals"));
            var previews = dialog.GetVisualDescendants().OfType<Image>().ToArray();
            Assert.Equal(10, previews.Length); Assert.Single(previews, image => image.Source is not null);

            // 默认选中本模型第一个用到的槽（Toon05），下方按钮作用于它。
            Assert.Equal(5, dialog.SelectedSlot);
            Assert.Contains(texts, text => text.Text == "Toon05 · " + EditorLocalization.Format("Mda.Text.Mme.ToonSlots.Materials", 2));
            var buttons = dialog.GetVisualDescendants().OfType<Button>().ToArray();
            var replace = buttons.Single(button => Equals(button.Content, EditorLocalization.Text("Mda.Text.Mme.ToonSlots.Replace")));
            var restore = buttons.Single(button => Equals(button.Content, EditorLocalization.Text("Mda.Text.Mme.ToonSlots.Restore")));
            Assert.True(replace.IsEnabled);
            // 原版资源缺失：已替换的槽也不能恢复，并给出提示。
            Assert.False(restore.IsEnabled);
            Assert.Equal(EditorLocalization.Text("Mda.Text.Mme.ToonSlots.NoOriginals"), ToolTip.GetTip(restore));
            dialog.SelectSlot(1); Dispatcher.UIThread.RunJobs();
            Assert.False(replace.IsEnabled); Assert.False(restore.IsEnabled);
            dialog.SelectSlot(5); Dispatcher.UIThread.RunJobs();
            Assert.Equal(5, dialog.SelectedSlot); Assert.True(replace.IsEnabled);

            // 最小窗口：十个槽一屏放下、不需要滚动，预览完整，按钮和换行文字不被裁切。
            var list = dialog.GetVisualDescendants().OfType<ListBox>().Single();
            var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(scroller.Extent.Height <= scroller.Viewport.Height + 0.5, $"{scroller.Extent.Height} > {scroller.Viewport.Height}");
            Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 0.5, $"{scroller.Extent.Width} > {scroller.Viewport.Width}");
            // 空槽也保留同样大小的预览框，十格对齐不跳动。
            Assert.All(previews, image => Assert.Equal(42, ((Border)image.Parent!).Bounds.Width));
            Assert.All(previews.Where(image => image.Source is not null), image => Assert.Equal(image.Width, image.Bounds.Width));
            foreach (var button in buttons.Where(button => button.Content is string))
            {
                var presenter = button.GetVisualDescendants().OfType<TextBlock>().Single();
                Assert.True(presenter.DesiredSize.Width <= presenter.Bounds.Width + 0.5, $"'{button.Content}' is clipped");
            }
            foreach (var text in texts.Where(text => text.TextWrapping == Avalonia.Media.TextWrapping.Wrap && !string.IsNullOrEmpty(text.Text)))
            {
                text.Measure(new Avalonia.Size(text.Bounds.Width, double.PositiveInfinity));
                Assert.True(text.DesiredSize.Height <= text.Bounds.Height + 0.5, $"'{text.Text}' is clipped");
            }
            Assert.Equal(5, dialog.SelectedSlot);
            using var frame = dialog.CaptureRenderedFrame(); Assert.NotNull(frame);
            if (Environment.GetEnvironmentVariable("MDA_TEST_SCREENSHOT_DIRECTORY") is { Length: > 0 } screenshots)
            {
                Directory.CreateDirectory(screenshots);
                frame.Save(Path.Combine(screenshots, $"toon-slots-minimum-{language}-{(light ? "light" : "dark")}.png"));
            }
            // 窗口本身不修改工程。
            Assert.Same(metadata, workspace.ProjectMetadata); Assert.False(workspace.IsDirty); Assert.False(workspace.CanUndo);
        }
        finally
        {
            dialog?.Close(); owner?.Close(); Dispatcher.UIThread.RunJobs();
            EditorLocalization.SetLanguage(before); Directory.Delete(directory, true);
        }
        return true;
    }, TestContext.Current.CancellationToken);

    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject actor) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
