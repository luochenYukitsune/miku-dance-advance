using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>界面总览截图：把主窗口各页和所有对话框按深色／浅色各截一张，供美化对照。
/// 只在设置了 MDA_UI_GALLERY_DIRECTORY 时截图；没设置时立即返回，不影响常规测试。</summary>
[Collection(QualityLoadingCollection.Name)]
public sealed class UiGalleryCaptureTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CaptureEveryWindow() => await session.Dispatch(async () =>
    {
        if (Environment.GetEnvironmentVariable("MDA_UI_GALLERY_DIRECTORY") is not { Length: > 0 } output) return true;
        string language = Environment.GetEnvironmentVariable("MDA_UI_GALLERY_LANGUAGE") is { Length: > 0 } value ? value : "zh-CN";
        string before = EditorLocalization.CurrentLanguage;
        EditorLocalization.SetLanguage(language);
        Directory.CreateDirectory(output);
        string scratch = Path.Combine(MdaWorkRoot.Path("ui-gallery-scratch"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var failures = new List<string>();
        try
        {
            foreach (bool light in new[] { false, true })
            {
                string theme = light ? "light" : "dark";
                var variant = light ? ThemeVariant.Light : ThemeVariant.Dark;
                async Task Shot(string name, Func<Window, Task<Window>> open, bool editorOwner = false)
                {
                    // 代码构建的对话框从所有者取配色，生产中所有者是主窗口。
                    Window owner = editorOwner ? Editor(language, variant) : new Window { RequestedThemeVariant = variant, Width = 400, Height = 300 };
                    owner.Show(); Pump(owner);
                    Window? window = null;
                    try
                    {
                        window = await open(owner);
                        window.RequestedThemeVariant = variant;
                        if (!window.IsVisible) window.Show(owner);
                        Pump(window);
                        using var frame = window.CaptureRenderedFrame();
                        frame!.Save(Path.Combine(output, $"{name}-{theme}.png"));
                    }
                    catch (Exception error) { failures.Add($"{name}-{theme}: {error.GetType().Name}: {error.Message}"); }
                    finally
                    {
                        if (window is EditorWindow editor) CloseEditor(editor); else window?.Close();
                        if (owner is EditorWindow editorOwnerWindow) CloseEditor(editorOwnerWindow); else owner.Close();
                        Dispatcher.UIThread.RunJobs();
                    }
                }

                // 主窗口：空工程，以及加载了模型后的四个右侧页。
                foreach (var (page, shot) in new[] { (EditorInspectorPage.Animation, ""), (EditorInspectorPage.Style, ""), (EditorInspectorPage.Material, ""),
                             (EditorInspectorPage.Lighting, ""), (EditorInspectorPage.Lighting, "4-mainlight") })
                    await Shot(shot.Length > 0 ? "01-main-" + shot : $"01-main-{(int)page}-{page.ToString().ToLowerInvariant()}", _ =>
                    {
                        var window = new EditorWindow(false, new EditorPreferences { Language = language }) { RequestedThemeVariant = variant, Width = 1480, Height = 940 };
                        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
                        viewport.IsSuspended = true;
                        var shell = new EditorShellViewModel(); window.DataContext = shell; shell.SelectedThemeIndex = light ? 1 : 0; window.Show();
                        string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
                        using (var input = File.OpenRead(path))
                        {
                            var model = PmdPmxAdapter.Convert(PmdReader.Read(input));
                            shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(model, path), ModelRole.Character);
                        }
                        shell.ShowInspectorPage(page);
                        if (shot.Length > 0)
                        {
                            Pump(window);
                            // The collapsed main-light section has no visual content yet; find it by its content type.
                            foreach (var expander in window.GetVisualDescendants().OfType<Expander>())
                                if (expander.Content is Border { Child: EditorMainLightInspector }) expander.IsExpanded = true;
                        }
                        return Task.FromResult<Window>(window);
                    });
                await Shot("02-startup", _ => Task.FromResult<Window>(new EditorStartupWindow()));
                await Shot("03-render-engine", _ => Task.FromResult(EditorRenderEngineSelectionUiProbe.CreateWindow(language, !light, 520, 440)));
                await Shot("04-preferences", _ => Task.FromResult<Window>(new EditorPreferencesWindow(new EditorPreferences())));
                await Shot("05-preview-settings", _ => Task.FromResult<Window>(new EditorPreviewSettingsWindow()));
                for (int tab = 0; tab < 3; tab++)
                {
                    int index = tab;
                    await Shot($"06-output-settings-tab{tab}", _ =>
                    {
                        var window = new OutputSettingsWindow(new OutputSettingsViewModel(), queryGiCapabilities: _ => Task.FromResult(new EditorPreviewCapabilities()));
                        window.Show(); Pump(window);
                        if (FirstTabControl(window) is { } tabs) tabs.SelectedIndex = index;
                        return Task.FromResult<Window>(window);
                    });
                }
                await Shot("07-export-status", _ => Task.FromResult<Window>(new ExportRenderStatusWindow(new ExportRenderStatusViewModel("D:/output/scene.mp4", 1200))));
                await Shot("08-about", _ => Task.FromResult<Window>(new EditorAboutWindow(new("0.1.29", "abcdef123456", ".NET 10"), AppContext.BaseDirectory)));
                await Shot("09-lighting", _ => Task.FromResult<Window>(new EditorLightingWindow(new SceneLightingSnapshot([
                    new NativeSceneLight(Guid.NewGuid(), "主光", SceneLightKind.Directional, new(2, 3, 4), System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One),
                    new NativeSceneLight(Guid.NewGuid(), "柔光箱", SceneLightKind.Area, new(-2, 3, 4), System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One,
                        AreaSize: new(2, 3))]))));
                await Shot("10-interpolation", _ =>
                {
                    var motion = new VmdMotion { ModelName = "m", BoneKeyframes = [new VmdBoneKeyframe { BoneName = "センター", Frame = 0 }, new VmdBoneKeyframe { BoneName = "センター", Frame = 30 }] };
                    return Task.FromResult<Window>(new EditorInterpolationWindow(new EditorInterpolationViewModel(motion,
                        [new KeyframeAddress(KeyframeTrackKind.Bone, "センター", 30)])));
                });
                await Shot("11-pmm-import", _ =>
                {
                    var playback = ResolvedOutputCameraFixture.Playback();
                    return Task.FromResult<Window>(new EditorPmmImportWindow(playback.Plan));
                });
                var workspace = new EditorProjectWorkspace { PackageCacheDirectory = Path.Combine(scratch, theme + "-cache") };
                Guid modelId;
                {
                    string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
                    using var input = File.OpenRead(path);
                    var model = PmdPmxAdapter.Convert(PmdReader.Read(input));
                    modelId = workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(model, path, Token), ModelRole.Character);
                }
                for (int tab = 0; tab < 3; tab++)
                    await Shot($"12-model-properties-tab{tab}", _ => Task.FromResult<Window>(new EditorModelPropertiesWindow(workspace, modelId, tab)));
                await Shot("13-rig-settings", _ => Task.FromResult<Window>(new EditorRigSettingsWindow(workspace, modelId)));
                workspace.Dispose();
                await Shot("14-plugins", async owner =>
                {
                    var service = new EditorPluginService(Path.Combine(scratch, "plugins"), Path.Combine(scratch, "enabled.json"), new(new(0, 1, 29), 1, language));
                    await service.InitializeAsync(Token);
                    return new EditorPluginsWindow(owner, service);
                }, true);
                await Shot("15-recovery", owner =>
                {
                    using var state = System.Text.Json.JsonDocument.Parse("{}");
                    var checkpoint = new EditorRecoveryCheckpoint(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
                        "D:/test/recovery.pma", 86_064_938, new string('0', 64), "D:/作品/未命名工程.pma", "未命名工程.pma", 1, state.RootElement.Clone(), 1);
                    return Task.FromResult<Window>(new EditorRecoveryWindow(owner, [new EditorRecoverySessionInfo(checkpoint.SessionId,
                        checkpoint.CreatedUtc, checkpoint.OriginalProjectPath, checkpoint.DisplayName, [checkpoint])]) { Width = 560, Height = 420 });
                }, true);
                await Shot("16-confirm-dialog", owner =>
                {
                    _ = EditorDialogs.ConfirmAsync(owner, "删除分配页", "分配页 GlowRT 属于要替换的特效，但其中有分配内容。\n\n要连同内容一起删除这些页吗？删除后可以撤销。", "删除这些页", destructive: true);
                    Dispatcher.UIThread.RunJobs();
                    return Task.FromResult(owner.OwnedWindows.Last());
                }, true);
                await Shot("17-mme-assignment", owner => Task.FromResult<Window>(ClassicEffects(owner, scratch)), true);
                await Shot("18-toon-slots", owner => Task.FromResult<Window>(ToonSlots(owner, scratch)), true);
            }
        }
        finally { EditorLocalization.SetLanguage(before); }
        File.WriteAllLines(Path.Combine(output, "failures.txt"), failures);
        return true;
    }, Token);

    private static EditorWindow Editor(string language, ThemeVariant variant)
    {
        var window = new EditorWindow(false, new EditorPreferences { Language = language }) { RequestedThemeVariant = variant, Width = 900, Height = 700 };
        window.FindControl<EditorViewportHost>("SceneViewport")!.IsSuspended = true;
        var shell = new EditorShellViewModel(); window.DataContext = shell;
        // 主题只在属性变化时应用，先赋 DataContext 再切换。
        shell.SelectedThemeIndex = variant == ThemeVariant.Light ? 1 : 0;
        return window;
    }

    private static TabControl? FirstTabControl(Control root)
        => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<TabControl>().FirstOrDefault();

    private static Window ClassicEffects(Window owner, string scratch)
    {
        string path = Path.Combine(scratch, "mme-model.pmx");
        var model = EditorRecoveryPmaIntegrationTests.Model();
        var asset = EditorProjectWorkspace.CreateModelAsset(model, path, Token);
        var composition = new SceneCompositionEditor();
        Guid id = composition.Add("初音ミク", path, ModelRole.Character, asset.Source).Id;
        var project = new SceneProjectSession(composition, new(),
            [new(id, model, new VmdMotion { ModelName = model.Name }, PhysicsEnabled: false)], new NoPhysics());
        var document = EmmReader.Read(System.Text.Encoding.UTF8.GetBytes($"[Info]\nVersion=3\n[Object]\nPmd0={path}\n[Effect]\nDefault=none\n[Effect@GlowRT]\nOwner=Pmd0\n"));
        var plan = EmmImportPlan.Create(document, Path.Combine(scratch, "draft.emm"), [new(id, true, path, 0)]);
        var emm = D3D9EmmPma.Capture(plan, []);
        var constructor = typeof(EditorClassicDocumentState).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(value => value.GetParameters()[0].ParameterType == typeof(SceneProjectSession));
        var metadata = (EditorClassicDocumentState)constructor.Invoke([project, emm, null, null, null, null, false]);
        var workspace = new EditorProjectWorkspace();
        typeof(EditorProjectWorkspace).GetProperty("Session")!.SetValue(workspace, project);
        typeof(EditorProjectWorkspace).GetProperty("ProjectMetadata", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace, metadata);
        return new EditorClassicEffectsWindow(owner, workspace) { Width = 760, Height = 520 };
    }

    private static Window ToonSlots(Window owner, string scratch)
    {
        var model = EditorClassicToonPackageTests.SlotModel();
        string path = Path.Combine(scratch, "toon-model.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
        var asset = EditorProjectWorkspace.CreateModelAsset(model, path, Token);
        var composition = new SceneCompositionEditor();
        Guid id = composition.Add("Actor", path, ModelRole.Character, asset.Source).Id;
        var seed = EditorClassicToonPackageTests.SlotPackage([id]);
        var toons = seed.Replace(id, model, [1], EditorClassicToonPackageTests.SlotBitmap(200), "chosen.bmp", Token);
        composition.ReplaceMaterialSource(id, toons.Apply(id, asset, path, Token).Source);
        var project = new SceneProjectSession(composition, new(),
            [new(id, model, new VmdMotion { ModelName = model.Name }, PhysicsEnabled: false)], new NoPhysics());
        var constructor = typeof(EditorClassicDocumentState).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(value => value.GetParameters()[0].ParameterType == typeof(SceneProjectSession));
        var metadata = ((EditorClassicDocumentState)constructor.Invoke([project, null, null, null, null, null, false])).WithToons(project, toons);
        string[] names = ["Mmd.Modern.D3D9.Compatibility.dll", "d3dx9_43.dll", "d3dcompiler_43.dll"];
        foreach (string name in names) File.WriteAllBytes(Path.Combine(scratch, name), [1]);
        var engine = new EditorClassicRenderEngine(new(Path.Combine(scratch, names[0]), Path.Combine(scratch, names[1]), Path.Combine(scratch, names[2])), scratch);
        var workspace = new EditorProjectWorkspace(engine);
        typeof(EditorProjectWorkspace).GetProperty("Session")!.SetValue(workspace, project);
        typeof(EditorProjectWorkspace).GetProperty("ProjectMetadata", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace, metadata);
        return new EditorClassicToonSlotsWindow(owner, workspace, id);
    }

    private static void Pump(Window window)
    {
        // 选中／悬停的颜色过渡约 110 ms，按真实时钟推进；等过渡结束再截图。
        for (int i = 0; i < 3; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        LoadingClockTestDriver.Pump(220);
        window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
    }

    private static void CloseEditor(EditorWindow window)
    {
        typeof(EditorWindow).GetField("_closingConfirmed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        window.FindControl<EditorViewportHost>("SceneViewport")?.Dispose();
        window.Close(); Dispatcher.UIThread.RunJobs();
    }

    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject actor) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
