using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Plugins;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput.D3D12;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorPluginStyleUiTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "mda-plugin-style-ui-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BundledDefaultsAutoEnableAndDisabledDefaultsStayDisabledAcrossRefreshAndRestart()
    {
        Directory.CreateDirectory(directory);
        string installDir = Path.Combine(directory, "plugins");
        string prefsPath = Path.Combine(installDir, "enabled.json");

        await using (var first = CreatePluginService(installDir, prefsPath, useBundledDefaults: true))
        {
            await first.InitializeAsync(Token);
            foreach (string id in EditorPluginService.BundledDefaultPluginIds)
                Assert.Equal(PluginState.Active, first.Manager.Plugins.Single(p => p.Id == id).State);
            Assert.Equal(4, first.CurrentStyleCatalog.Styles.Count);

            var disableResult = await first.DisablePluginAsync("mda.style.genshin", Token);
            Assert.True(disableResult.Succeeded, disableResult.Diagnostic);
            Assert.Equal(PluginState.Disabled, first.Manager.Plugins.Single(p => p.Id == "mda.style.genshin").State);
            Assert.Null(first.CurrentStyleCatalog.FindStyle("genshin"));
            Assert.Equal(3, first.CurrentStyleCatalog.Styles.Count);

            using (var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(prefsPath, Token)))
            {
                var disabledDefaults = doc.RootElement.GetProperty("disabledDefaults")
                    .EnumerateArray().Select(static x => x.GetString()).ToArray();
                Assert.Contains("mda.style.genshin", disabledDefaults);
                var enabled = doc.RootElement.GetProperty("enabled")
                    .EnumerateArray().Select(static x => x.GetString()).ToArray();
                Assert.DoesNotContain("mda.style.genshin", enabled);
                Assert.Contains("mda.style.star-rail", enabled);
            }

            // Refresh must respect DisabledDefaults and NOT auto-re-enable mda.style.genshin.
            var refreshed = await first.RefreshAsync(Token);
            Assert.True(refreshed.Succeeded, refreshed.Diagnostic);
            Assert.Equal(PluginState.Disabled, first.Manager.Plugins.Single(p => p.Id == "mda.style.genshin").State);
            Assert.Equal(3, first.CurrentStyleCatalog.Styles.Count);
        }

        // Restart: new EditorPluginService reads enabled.json and keeps mda.style.genshin disabled.
        await using (var second = CreatePluginService(installDir, prefsPath, useBundledDefaults: true))
        {
            await second.InitializeAsync(Token);
            Assert.Equal(PluginState.Disabled, second.Manager.Plugins.Single(p => p.Id == "mda.style.genshin").State);
            foreach (string id in EditorPluginService.BundledDefaultPluginIds.Where(id => id != "mda.style.genshin"))
                Assert.Equal(PluginState.Active, second.Manager.Plugins.Single(p => p.Id == id).State);
            Assert.Null(second.CurrentStyleCatalog.FindStyle("genshin"));

            var reEnabled = await second.EnablePluginAsync("mda.style.genshin", Token);
            Assert.True(reEnabled.Succeeded, reEnabled.Diagnostic);
            Assert.Equal(PluginState.Active, second.Manager.Plugins.Single(p => p.Id == "mda.style.genshin").State);
            Assert.NotNull(second.CurrentStyleCatalog.FindStyle("genshin"));

            using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(prefsPath, Token));
            Assert.False(doc.RootElement.TryGetProperty("disabledDefaults", out _));
        }
    }

    [Fact]
    public async Task StyleInspectorTracksLivePluginsLocalizationGuessToggleMissingRestoreAndClassicNotice()
        => await session.Dispatch(async () =>
        {
            Directory.CreateDirectory(directory);
            string priorLang = EditorLocalization.CurrentLanguage;
            try
            {
                EditorLocalization.SetLanguage("en");
                string installDir = Path.Combine(directory, "inspector-plugins");
                string prefsPath = Path.Combine(installDir, "enabled.json");
                await using var pluginService = CreatePluginService(installDir, prefsPath, useBundledDefaults: true, tracksAppLanguage: true);
                await pluginService.InitializeAsync(Token);

                using var workspace = CreateModernWorkspace("inspector-cache", pluginService);
                Guid modelId = AddModelWithTextures(workspace);

                var inspector = new EditorStyleInspectorViewModel();
                int pluginsChangedEvents = 0;
                pluginService.PluginsChanged += (_, _) =>
                {
                    pluginsChangedEvents++;
                    inspector.Refresh(workspace.Session, modelId, true, true);
                };
                inspector.AttachWorkspace(workspace);
                inspector.AttachPluginService(pluginService);
                inspector.Refresh(workspace.Session, modelId, true, true);

                Assert.True(inspector.IsModernEngine);
                Assert.False(inspector.IsClassicEngine);
                Assert.Equal(5, inspector.Options.Count);
                Assert.Equal(RenderingPresetReference.BuiltIn(RenderingStyleId.Mmd), inspector.SelectedReference);

                // Select Genshin -> localized metadata & source badge populate cleanly.
                int genshinIdx = inspector.Options.ToList().FindIndex(c => c.StableId == "genshin");
                Assert.True(genshinIdx > 0);
                inspector.SelectedIndex = genshinIdx;
                Assert.Equal(RenderingPresetReference.BuiltIn(RenderingStyleId.Genshin), inspector.SelectedReference);
                Assert.True(workspace.SetDefaultRenderingStyle(inspector.SelectedReference!.Value));
                inspector.Refresh(workspace.Session, modelId, true, true);
                Assert.Equal(RenderingPresetReference.BuiltIn(RenderingStyleId.Genshin), workspace.Session!.RenderingStyles.DefaultStyle);
                inspector.ReportStatus("Applied");
                var catalog = pluginService.CurrentStyleCatalog;
                var row = inspector.SelectedChoice;
                inspector.Refresh(workspace.Session, modelId, true, true);
                Assert.Same(catalog, pluginService.CurrentStyleCatalog);
                Assert.Same(row, inspector.SelectedChoice);
                Assert.Equal("Applied", inspector.Status);
                Assert.True(inspector.CanEditSelectedModelStyle);
                Assert.False(inspector.CanInheritSelectedModelStyle);
                Assert.Contains(workspace.Session.Objects.Single().Name, inspector.ModelStatus);
                Assert.True(inspector.HasSelectedStyleMetadata);
                Assert.Equal("MDA", inspector.SelectedStyleAuthor);
                Assert.Equal("1.0.0", inspector.SelectedStyleVersion);
                Assert.Equal(EditorLocalization.Text("Mda.Text.Context.Inspector.Style.Source.Bundled"), inspector.SelectedStyleSourceTag);
                Assert.Contains("Genshin Impact", inspector.SelectedStyleDescription);

                // Language switch live-updates localized choice label, source tag, and description without modifying project style.
                EditorLocalization.SetLanguage("ja");
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("原神", inspector.SelectedChoice!.Label);
                Assert.Equal(EditorLocalization.Text("Mda.Text.Context.Inspector.Style.Source.Bundled"), inspector.SelectedStyleSourceTag);
                Assert.Contains("原神スタイル描画", inspector.SelectedStyleDescription);
                Assert.Equal(RenderingPresetReference.BuiltIn(RenderingStyleId.Genshin), workspace.Session.RenderingStyles.DefaultStyle);

                // GuessMaterialRoles toggle syncs with workspace & Undo.
                Assert.True(inspector.GuessMaterialRoles);
                Assert.True(inspector.CanEditGuessMaterialRoles);
                Assert.True(workspace.SetGuessMaterialRoles(false));
                inspector.Refresh(workspace.Session, modelId, true, true);
                Assert.False(inspector.GuessMaterialRoles);
                Assert.True(workspace.Undo());
                inspector.Refresh(workspace.Session, modelId, true, true);
                Assert.True(inspector.GuessMaterialRoles);

                // Select Wuthering Waves, then disable its plugin -> live PluginsChanged shows missing warning + one-click restore.
                workspace.SetDefaultRenderingStyle(RenderingPresetReference.BuiltIn(RenderingStyleId.WutheringWaves));
                inspector.Refresh(workspace.Session, modelId, true, true);
                Assert.False(inspector.HasMissingStyle);

                var disabled = await pluginService.DisablePluginAsync("mda.style.wuthering-waves", Token);
                Assert.True(disabled.Succeeded, disabled.Diagnostic);
                Assert.True(pluginsChangedEvents >= 1);
                Assert.True(inspector.HasMissingStyle);
                Assert.Equal("wuthering-waves", inspector.MissingStyleId);
                Assert.Equal("mda.style.wuthering-waves", inspector.MissingStylePluginId);
                Assert.True(inspector.CanEnableMissingStylePlugin);
                Assert.True(inspector.CanOpenStyleReplacementDialog);
                Assert.True(inspector.HasNotice);
                Assert.DoesNotContain(inspector.Options, c => !c.IsRetained && c.StableId == "wuthering-waves");

                var restored = await inspector.EnablePluginForStyleAsync("wuthering-waves", Token);
                Assert.True(restored.Succeeded, restored.Diagnostic);
                Assert.False(inspector.HasMissingStyle);
                Assert.Equal(0, inspector.MissingStyleCount);
                Assert.Equal(RenderingPresetReference.BuiltIn(RenderingStyleId.WutheringWaves), inspector.SelectedReference);

                // Classic engine reports ClassicNotice and blocks Modern style edits.
                using var classicWorkspace = CreateClassicWorkspace("inspector-classic");
                inspector.AttachWorkspace(classicWorkspace);
                inspector.Refresh(classicWorkspace.Session, null, true, true);
                Assert.Empty(inspector.Options);
                Assert.Null(inspector.SelectedReference);
                Assert.False(inspector.CanEditSelectedModelStyle);
                Assert.False(inspector.CanInheritSelectedModelStyle);
                Assert.True(inspector.IsClassicEngine);
                Assert.False(inspector.IsModernEngine);
                Assert.False(inspector.CanEdit);
                Assert.False(inspector.CanEditGuessMaterialRoles);
                Assert.False(string.IsNullOrWhiteSpace(inspector.ClassicNotice));
            }
            finally
            {
                EditorLocalization.SetLanguage(priorLang);
            }
            return true;
        }, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

    [Fact]
    public async Task StyleReplacementDialogReEnablesPluginReplacesMissingDefaultAndSupportsUndo()
        => await session.Dispatch(async () =>
        {
            Directory.CreateDirectory(directory);
            string priorLang = EditorLocalization.CurrentLanguage;
            try
            {
                EditorLocalization.SetLanguage("zh-CN");
                string installDir = Path.Combine(directory, "replace-plugins");
                string prefsPath = Path.Combine(installDir, "enabled.json");
                await using var pluginService = CreatePluginService(installDir, prefsPath, useBundledDefaults: true);
                await pluginService.InitializeAsync(Token);

                using var workspace = CreateModernWorkspace("replace-cache", pluginService);
                Guid firstId = AddModelWithTextures(workspace);
                Guid secondId = AddModelWithTextures(workspace);

                // Set model 1 to WutheringWaves then disable plugin; set project default to an unmounted custom ID.
                workspace.Session!.SetModelRenderingStyle(firstId, RenderingPresetReference.BuiltIn(RenderingStyleId.WutheringWaves), Token);
                var disableResult = await pluginService.DisablePluginAsync("mda.style.wuthering-waves", Token);
                Assert.True(disableResult.Succeeded, disableResult.Diagnostic);
                workspace.Session!.EditRenderingStyles(edit =>
                {
                    edit.DefaultStyle = new RenderingPresetReference("missing-custom-style");
                }, Token);

                var owner = new Window();
                owner.Show();
                var showTask = EditorStyleReplacementDialog.ShowAsync(owner, workspace, pluginService);
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    var dialog = Assert.Single(owner.OwnedWindows);
                    Assert.Equal("StyleReplacementDialog", dialog.Name);

                    var confirm = FindNamed<Button>(dialog, "ConfirmStyleReplacementButton");
                    Assert.False(confirm.IsEnabled);

                    // Enable disabled plugin for wuthering-waves via the inline card button.
                    string enableLabel = EditorLocalization.Text("Mda.Text.Context.Inspector.Style.EnablePluginAndApply");
                    var enableButton = dialog.GetVisualDescendants().OfType<Button>()
                        .Single(b => Equals(b.Content, enableLabel));
                    // A target already chosen for the restored source must leave the remaining proposal with it.
                    var restoringCard = enableButton.FindAncestorOfType<Border>()!;
                    var discardChoice = Assert.Single(restoringCard.GetVisualDescendants().OfType<ComboBox>());
                    discardChoice.SelectedIndex = 0;
                    enableButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (int step = 0; step < 30 && pluginService.FindPluginForStyle("wuthering-waves")?.State != PluginState.Active; step++)
                    {
                        await Task.Delay(20, Token);
                        Dispatcher.UIThread.RunJobs();
                    }
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(PluginState.Active, pluginService.FindPluginForStyle("wuthering-waves")?.State);

                    // Pick "mmd" for the remaining missing style and confirm replacement.
                    var targetCombo = Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>());
                    Assert.True(targetCombo.IsEffectivelyEnabled);
                    targetCombo.SelectedIndex = 0;
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(confirm.IsEnabled);

                    // The inheriting model has to be renderable with the proposed target before any style is changed.
                    workspace.MaterialBindings[secondId] = new D3D12MaterialBindingProfile(
                        "wrong.layout", "Wrong layout", new string('0', 64), []);
                    confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(showTask.IsCompleted);
                    Assert.Equal("missing-custom-style", workspace.Session.RenderingStyles.DefaultStyle.StableId);
                    workspace.MaterialBindings.Remove(secondId);
                    confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Dispatcher.UIThread.RunJobs();

                    bool dialogResult = await showTask.WaitAsync(TimeSpan.FromSeconds(10), Token);
                    Assert.True(dialogResult);
                    Assert.Equal("mmd", workspace.Session.RenderingStyles.DefaultStyle.StableId);
                    Assert.Equal("wuthering-waves", workspace.Session.GetRenderingStyle(firstId).StableId);
                    Assert.Equal("mmd", workspace.Session.GetRenderingStyle(secondId).StableId);

                    // Undo restores prior missing default cleanly.
                    Assert.True(workspace.Undo());
                    Assert.Equal("missing-custom-style", workspace.Session.RenderingStyles.DefaultStyle.StableId);
                }
                finally
                {
                    foreach (var owned in owner.OwnedWindows.ToArray())
                        owned.Close();
                    owner.Close();
                    Dispatcher.UIThread.RunJobs();
                }
            }
            finally
            {
                EditorLocalization.SetLanguage(priorLang);
            }
            return true;
        }, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

    [Fact]
    public async Task ModelStyleInheritanceValidatesTheSelectedModelAndCreatesOneUndoStep()
        => await session.Dispatch(async () =>
        {
            string plugins = Path.Combine(directory, "inherit-plugins");
            await using var service = CreatePluginService(plugins, Path.Combine(plugins, "enabled.json"), useBundledDefaults: true);
            await service.InitializeAsync(Token);
            using var shell = new EditorShellViewModel();
            var workspace = shell.Workspace;
            workspace.PackageCacheDirectory = Path.Combine(directory, "inherit-cache");
            shell.AttachPluginService(service);
            Guid first = AddModelWithTextures(workspace), second = AddModelWithTextures(workspace);
            var project = workspace.Session!;
            project.SetModelRenderingStyle(first, new RenderingPresetReference("genshin"));
            project.SetModelRenderingStyle(second, new RenderingPresetReference("endfield"));
            var inspector = shell.StyleInspector;
            inspector.Refresh(project, first, false, true);
            Assert.False(inspector.CanInheritSelectedModelStyle);
            inspector.Refresh(project, first, true, true);
            Assert.True(inspector.CanInheritSelectedModelStyle);

            var incompatible = new D3D12MaterialBindingProfile("wrong.layout", "Wrong layout", new string('0', 64), []);
            workspace.MaterialBindings[first] = incompatible;
            Assert.Throws<InvalidOperationException>(() => workspace.InheritModelStyle(first));
            Assert.Same(incompatible, workspace.MaterialBindings[first]);
            Assert.Equal("genshin", project.GetRenderingStyle(first).StableId);
            workspace.MaterialBindings.Remove(first);

            shell.Browser.SelectedModel = shell.Browser.Models.Single(item => item.ObjectId == first);
            var window = new EditorWindow(false) { DataContext = shell };
            try
            {
                shell.ShowInspectorPage(EditorInspectorPage.Style);
                shell.Animation.SelectBone(0);
                shell.Animation.PositionX = 1.25m;
                Assert.True(shell.Animation.HasAnyDraft);
                var inherit = window.FindControl<Button>("InheritSelectedModelStyleButton")!;
                Assert.True(inherit.IsEffectivelyEnabled);
                inherit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(shell.Animation.HasAnyDraft);
                Assert.False(inherit.IsEnabled);
            }
            finally
            {
                window.DataContext = null;
                window.FindControl<EditorViewportHost>("SceneViewport")!.Dispose();
                window.Close();
            }
            Assert.False(workspace.InheritModelStyle(first));
            inspector.Refresh(project, first, true, true);
            Assert.True(inspector.IsModelInherited);
            Assert.False(inspector.CanInheritSelectedModelStyle);
            Assert.Equal("mmd", project.GetRenderingStyle(first).StableId);
            inspector.Refresh(project, second, true, true);
            Assert.True(inspector.CanInheritSelectedModelStyle);
            Assert.Equal("endfield", project.GetRenderingStyle(second).StableId);
            Assert.True(workspace.Undo());
            Assert.Equal("genshin", project.GetRenderingStyle(first).StableId);
            Assert.True(workspace.Redo());
            Assert.False(project.RenderingStyles.ModelOverrides.ContainsKey(first));
            inspector.Detach();
            Assert.Empty(inspector.Options);
            Assert.Null(inspector.SelectedChoice);
            Assert.False(inspector.HasSelectedModel);
            Assert.False(inspector.CanManagePlugins);
            return true;
        }, Token);

    [Fact]
    public async Task ModelPropertiesDynamicMaterialModesValidateResetAndRoundTripSchemaV14()
        => await session.Dispatch(async () =>
        {
            Directory.CreateDirectory(directory);
            string priorLang = EditorLocalization.CurrentLanguage;
            try
            {
                EditorLocalization.SetLanguage("en");
                string installDir = Path.Combine(directory, "modelprops-plugins");
                string prefsPath = Path.Combine(installDir, "enabled.json");
                await using var pluginService = CreatePluginService(installDir, prefsPath, useBundledDefaults: true);
                await pluginService.InitializeAsync(Token);

                using var workspace = CreateModernWorkspace("modelprops-save-cache", pluginService);
                Guid actorId = AddModelWithTextures(workspace);

                var vm = new EditorModelPropertiesViewModel(workspace, actorId);
                int endfieldIdx = vm.StyleChoices.ToList().FindIndex(c => c.Reference == new RenderingPresetReference("endfield"));
                Assert.True(endfieldIdx > 0);
                vm.StyleIndex = endfieldIdx;

                var mat0 = vm.Materials[0];
                var mat1 = vm.Materials[1];
                Assert.True(mat0.HasDynamicModes);
                Assert.Contains(mat0.DynamicModes, m => m.ModeId == "face-sdf");
                Assert.Contains(mat0.DynamicModes, m => m.ModeId == "rim");
                Assert.Contains(mat0.DynamicModes, m => m.ModeId == "matcap");

                // 1. Live validation failure propagation: face-sdf on Body role without Face role or textures fails immediately.
                mat0.RoleIndex = 1; // Body
                var faceMode = mat0.DynamicModes.Single(m => m.ModeId == "face-sdf");
                faceMode.IsEnabled = true;
                Assert.True(mat0.HasStyleValidationMessage);
                Assert.False(string.IsNullOrWhiteSpace(mat0.StyleValidationMessage));
                Assert.False(vm.TryCreate(out _));
                Assert.True(vm.HasValidationMessage);

                // 2. Fix role -> Face, fill slots + Float + Enum + BoneTracking -> validation clears.
                mat0.RoleIndex = 2; // Face
                faceMode.TextureSlots[0].SelectedPrimary = faceMode.TextureSlots[0].Textures[1]; // tex 0
                faceMode.TextureSlots[1].SelectedPrimary = faceMode.TextureSlots[1].Textures[2]; // tex 1
                var transitionParam = faceMode.Parameters.Single(p => p.Key == "transition");
                Assert.True(transitionParam.IsFloat);
                transitionParam.DecimalValue = 1.25m;
                var encodingParam = faceMode.Parameters.Single(p => p.Key == "encoding");
                Assert.True(encodingParam.IsEnum);
                encodingParam.SelectedEnumChoice = encodingParam.EnumChoices.Single(c => c.Value == "EndfieldRgbCmM");
                Assert.True(faceMode.SupportsBoneTracking);
                faceMode.BoneTrackingModeIndex = 1; // SelectedBone
                faceMode.SelectedBone = vm.Bones[0];
                Assert.False(mat0.HasStyleValidationMessage);

                // 3. Configure mat1 (Body): rim (Color3 + Float + ResetToDefaults) + coupled matcap (Enum + Bool).
                mat1.RoleIndex = 1; // Body
                var rimMode = mat1.DynamicModes.Single(m => m.ModeId == "rim");
                rimMode.IsEnabled = true;
                var edgeStrengthParam = rimMode.Parameters.Single(p => p.Key == "edgeStrength");
                var rimBaseColor = rimMode.Parameters.Single(p => p.Key == "baseColor");
                Assert.True(edgeStrengthParam.IsFloat);
                Assert.True(rimBaseColor.IsColor3);
                edgeStrengthParam.DecimalValue = 0.75m;
                rimBaseColor.VecX = 0.2m;
                rimBaseColor.VecY = 0.3m;
                rimBaseColor.VecZ = 0.4m;
                rimMode.ResetToDefaults();
                Assert.Equal(0m, edgeStrengthParam.DecimalValue);
                Assert.Equal(0.5m, rimBaseColor.VecX);
                Assert.Equal(0.5m, rimBaseColor.VecY);
                Assert.Equal(0.5m, rimBaseColor.VecZ);

                edgeStrengthParam.DecimalValue = 0.42m;
                rimBaseColor.VecX = 0.2m;
                rimBaseColor.VecY = 0.3m;
                rimBaseColor.VecZ = 0.4m;

                var matcapMode = mat1.DynamicModes.Single(m => m.ModeId == "matcap");
                matcapMode.IsEnabled = true;
                matcapMode.TextureSlots[0].SelectedPrimary = matcapMode.TextureSlots[0].Textures[1];
                matcapMode.TextureSlots[1].SelectedPrimary = matcapMode.TextureSlots[1].Textures[2];
                matcapMode.TextureSlots[2].SelectedPrimary = matcapMode.TextureSlots[2].Textures[3];
                var matcapBaseColor = matcapMode.Parameters.Single(p => p.Key == "baseColor");
                matcapBaseColor.VecX = 0.2m;
                matcapBaseColor.VecY = 0.3m;
                matcapBaseColor.VecZ = 0.4m;
                var matcapEncoding = matcapMode.Parameters.Single(p => p.Key == "matcapEncoding");
                Assert.True(matcapEncoding.IsEnum);
                matcapEncoding.SelectedEnumChoice = matcapEncoding.EnumChoices.Single(c => c.Value == "SrgbRgb");
                var coupleBool = matcapMode.Parameters.Single(p => p.Key == "coupleEndfieldPalette");
                Assert.True(coupleBool.IsBool);
                coupleBool.BoolValue = true;
                Assert.False(mat1.HasStyleValidationMessage);

                Assert.True(vm.TryCreate(out var draft), vm.ValidationMessage);
                workspace.ApplyModelProperties(draft!);
                Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, workspace.MaterialBindings[actorId].SchemaVersion);

                string pmaPath = Path.Combine(directory, "dynamic-modes-v14.pma");
                workspace.Save(pmaPath, Token);

                // Reopen in fresh workspace + fresh plugin service -> verify Schema v14 dynamic modes roundtrip identically.
                await using var reopenedPlugins = CreatePluginService(installDir, prefsPath, useBundledDefaults: true);
                await reopenedPlugins.InitializeAsync(Token);
                using var reopened = CreateModernWorkspace("modelprops-open-cache", reopenedPlugins);
                reopened.Open(pmaPath, Token);

                Assert.Equal(D3D12MaterialBindingProfile.CurrentSchemaVersion, reopened.MaterialBindings[actorId].SchemaVersion);
                var reloadedVm = new EditorModelPropertiesViewModel(reopened, actorId);
                Assert.Equal(new RenderingPresetReference("endfield"), reloadedVm.StyleChoices[reloadedVm.StyleIndex].Reference);

                var rMat0 = reloadedVm.Materials[0];
                var rFace = rMat0.DynamicModes.Single(m => m.ModeId == "face-sdf");
                Assert.True(rFace.IsEnabled);
                Assert.Equal(1.25m, rFace.Parameters.Single(p => p.Key == "transition").DecimalValue);
                Assert.Equal("EndfieldRgbCmM", rFace.Parameters.Single(p => p.Key == "encoding").SelectedEnumChoice!.Value);
                Assert.Equal(1, rFace.BoneTrackingModeIndex);
                Assert.Equal(vm.Bones[0].EffectiveName, rFace.SelectedBone!.EffectiveName);

                var rMat1 = reloadedVm.Materials[1];
                var rRim = rMat1.DynamicModes.Single(m => m.ModeId == "rim");
                Assert.True(rRim.IsEnabled);
                Assert.Equal(0.42m, rRim.Parameters.Single(p => p.Key == "edgeStrength").DecimalValue);
                Assert.Equal(0.2m, rRim.Parameters.Single(p => p.Key == "baseColor").VecX);
                Assert.Equal(0.3m, rRim.Parameters.Single(p => p.Key == "baseColor").VecY);
                Assert.Equal(0.4m, rRim.Parameters.Single(p => p.Key == "baseColor").VecZ);

                var rMatcap = rMat1.DynamicModes.Single(m => m.ModeId == "matcap");
                Assert.True(rMatcap.IsEnabled);
                Assert.Equal("SrgbRgb", rMatcap.Parameters.Single(p => p.Key == "matcapEncoding").SelectedEnumChoice!.Value);
                Assert.True(rMatcap.Parameters.Single(p => p.Key == "coupleEndfieldPalette").BoolValue);
            }
            finally
            {
                EditorLocalization.SetLanguage(priorLang);
            }
            return true;
        }, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

    [Theory]
    [InlineData("genshin", 0.26, 0.22)]
    [InlineData("star-rail", 0.38, 0.05)]
    [InlineData("wuthering-waves", 0.12, 0.30)]
    [InlineData("endfield", 0.30, 0.12)]
    public async Task OrdinaryPmxControlsFollowPluginDefaultsAndKeepExplicitOverrides(string style, double strength, double width)
        => await session.Dispatch(async () =>
        {
            string install = Path.Combine(directory, "treatment-plugins");
            await using var plugins = CreatePluginService(install, Path.Combine(install, "enabled.json"), useBundledDefaults: true);
            await plugins.InitializeAsync(Token);
            using var workspace = CreateModernWorkspace("treatment-cache", plugins);
            Guid actor = AddModelWithTextures(workspace);
            var vm = new EditorModelPropertiesViewModel(workspace, actor);
            int Index(string id) => vm.StyleChoices.ToList().FindIndex(choice => choice.Reference?.StableId == id);
            vm.StyleIndex = Index(style);
            var row = vm.Materials[1];
            row.RoleIndex = 4;
            var ordinary = D3D12StylePluginAdapter.ToD3D12Treatment(workspace.StyleCatalog.FindStyle(style)!.DefaultRoleTreatments[StyleMaterialRole.Hair]);
            Assert.True(row.CanEditOrdinaryPmxTreatment);
            Assert.Equal(ordinary, row.PmxDefaults);
            Assert.Equal((decimal)strength, row.PmxHairHighlightStrength);
            Assert.Equal((decimal)width, row.PmxHairHighlightWidth);
            Assert.Null(row.Build()!.PmxMaterialTreatment);

            row.PmxHairHighlightWidth = 0.33m;
            Assert.Equal(ordinary with { HairHighlightWidth = 0.33f }, row.Build()!.PmxMaterialTreatment);
            var hair = row.DynamicModes.Single(mode => mode.ModeId == "hair");
            hair.IsEnabled = true;
            Assert.False(row.CanEditOrdinaryPmxTreatment);
            row.RoleIndex = 1;
            Assert.True(row.CanEditOrdinaryPmxTreatment);
            Assert.Equal(0m, row.PmxHairHighlightStrength);
            row.RoleIndex = 4;
            string otherStyle = style == "star-rail" ? "genshin" : "star-rail";
            vm.StyleIndex = Index(otherStyle);
            Assert.True(row.CanEditOrdinaryPmxTreatment);
            Assert.Equal((decimal)workspace.StyleCatalog.FindStyle(otherStyle)!.DefaultRoleTreatments[StyleMaterialRole.Hair].HairHighlightStrength,
                row.PmxHairHighlightStrength);
            Assert.Equal(0.33m, row.PmxHairHighlightWidth);
            vm.StyleIndex = Index(style);
            Assert.False(row.CanEditOrdinaryPmxTreatment);
            hair.IsEnabled = false;
            Assert.Equal(ordinary with { HairHighlightWidth = 0.33f }, row.Build()!.PmxMaterialTreatment);
            row.ResetPmxTreatment();
            Assert.Equal((decimal)width, row.PmxHairHighlightWidth);
            Assert.Null(row.Build()!.PmxMaterialTreatment);

            var neutral = new D3D12PmxMaterialTreatment();
            var legacy = new EditorMaterialPropertiesItem(row.LocalMaterialIndex, row.Name, row.Build()! with { PmxMaterialTreatment = neutral },
                D3D12MaterialBindingLayout.Capture(workspace.Session!.Objects[0].Source), vm.Textures, vm.Bones,
                styleCatalog: workspace.StyleCatalog, effectiveStyleId: style);
            Assert.Equal(0m, legacy.PmxHairHighlightStrength);
            Assert.Equal(neutral, legacy.Build()!.PmxMaterialTreatment);
            legacy.ResetPmxTreatment();
            Assert.Equal((decimal)strength, legacy.PmxHairHighlightStrength);
            Assert.Null(legacy.Build()!.PmxMaterialTreatment);
            return true;
        }, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

    [Fact]
    public async Task MaterialEditsSurviveSwitchingModelStyleAndClearingOneRowRetainsItsNeighbours()
        => await session.Dispatch(async () =>
        {
            string installDir = Path.Combine(directory, "material-drafts");
            await using var service = CreatePluginService(installDir, Path.Combine(installDir, "enabled.json"), useBundledDefaults: true);
            await service.InitializeAsync(Token);
            using var workspace = CreateModernWorkspace("material-drafts-cache", service);
            Guid id = AddModelWithTextures(workspace);
            var vm = new EditorModelPropertiesViewModel(workspace, id);
            int endfield = vm.StyleChoices.ToList().FindIndex(choice => choice.Reference?.StableId == "endfield");
            Assert.True(endfield > 1);
            vm.StyleIndex = endfield;
            var face = vm.Materials[0];
            var body = vm.Materials[1];
            face.OutlinePercent = 175m;
            body.RoleIndex = 1;
            var rim = body.DynamicModes.Single(mode => mode.ModeId == "rim");
            rim.IsEnabled = true;
            rim.Parameters.Single(parameter => parameter.Key == "edgeStrength").DecimalValue = 0.42m;
            vm.StyleIndex = 1; // The model currently uses MMD, with its Endfield settings retained.
            Assert.False(body.HasActiveStylePlugin);
            Assert.True(vm.TryCreate(out var mapped), vm.ValidationMessage);
            var saved = Assert.Single(mapped!.MaterialProfile!.Materials);
            Assert.Equal(1, saved.LocalMaterialIndex);
            Assert.Equal(0.42, Assert.Single(saved.PluginBindings).NumberValues!["edgeStrength"], 5);
            workspace.ApplyModelProperties(mapped with { Role = ModelRole.Unspecified });

            var reopened = new EditorModelPropertiesViewModel(workspace, id);
            reopened.StyleIndex = endfield;
            body = reopened.Materials[1];
            var oldRim = body.DynamicModes.Single(mode => mode.ModeId == "rim");
            Assert.True(oldRim.IsEnabled);
            oldRim.IsEnabled = false;
            reopened.StyleIndex = 0;
            Assert.True(reopened.TryCreate(out var disabled), reopened.ValidationMessage);
            Assert.Empty(Assert.Single(disabled!.MaterialProfile!.Materials).PluginBindings);
            Assert.Equal(ModelRole.Unspecified, disabled.Role);
            Assert.Null(disabled.StyleOverride);

            reopened.SelectedMaterial = body;
            reopened.ClearSelectedMaterialMapping();
            Assert.Equal(1, reopened.SelectedMaterial!.LocalMaterialIndex);
            Assert.False(reopened.SelectedMaterial.HasCustomization);
            Assert.True(reopened.TryCreate(out var firstClear), reopened.ValidationMessage);
            Assert.Empty(firstClear!.MaterialProfile!.Materials);
            Assert.Equal(1.75f, Assert.Single(firstClear.MaterialProfile.OutlineWidths).Scale);
            reopened.SelectedMaterial = reopened.Materials[0];
            reopened.ClearSelectedMaterialMapping();
            Assert.True(reopened.TryCreate(out var finalClear), reopened.ValidationMessage);
            Assert.Null(finalClear!.MaterialProfile);
            return true;
        }, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

    [Fact]
    public async Task MaterialSelectionTracksFilteredRowsAndCurrentModelStyleAfterRefresh()
        => await session.Dispatch(async () =>
        {
            string installDir = Path.Combine(directory, "material-selection");
            await using var service = CreatePluginService(installDir, Path.Combine(installDir, "enabled.json"), useBundledDefaults: true);
            await service.InitializeAsync(Token);
            using var workspace = CreateModernWorkspace("material-selection-cache", service);
            Guid id = AddModelWithTextures(workspace);
            var vm = new EditorModelPropertiesViewModel(workspace, id);
            vm.MaterialSearchText = "#1";
            Assert.Same(vm.Materials[1], vm.SelectedMaterial);
            vm.MaterialSearchText = "no authored material with this name";
            Assert.True(vm.ShowNoMatchingMaterials);
            Assert.False(vm.HasSelectedMaterial);
            vm.MaterialSearchText = "";
            Assert.Same(vm.Materials[0], vm.SelectedMaterial);

            var inspector = new EditorMaterialInspectorViewModel();
            inspector.Refresh(workspace, id, true, true);
            inspector.SelectedMaterial = inspector.Materials[1];
            var stale = inspector.SelectedMaterial;
            workspace.SetDefaultRenderingStyle(new RenderingPresetReference("endfield"));
            inspector.Refresh(workspace, id, true, true);
            Assert.Equal(1, inspector.SelectedMaterialIndex);
            Assert.NotSame(stale, inspector.SelectedMaterial);
            Assert.True(inspector.SelectedMaterial!.HasActiveStylePlugin);
            inspector.SelectedMaterial = stale;
            Assert.NotSame(stale, inspector.SelectedMaterial);
            inspector.Refresh(workspace, id, true, false);
            Assert.False(inspector.CanEdit);
            inspector.Refresh(workspace, id, true, true);
            Assert.Equal(1, inspector.SelectedMaterialIndex);
            inspector.SelectedMaterial = null;
            Assert.False(inspector.HasSelectedMaterial);
            Assert.False(inspector.CanEdit);
            return true;
        }, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

    [Fact]
    public async Task EditorPluginsWindowRendersMetadataBadgesSceneWarningWithoutClippingAt680x560AcrossLocales()
        => await session.Dispatch(async () =>
        {
            Directory.CreateDirectory(directory);
            string priorLang = EditorLocalization.CurrentLanguage;
            try
            {
                string installDir = Path.Combine(directory, "ui-plugins");
                string prefsPath = Path.Combine(installDir, "enabled.json");
                await using var pluginService = CreatePluginService(installDir, prefsPath, useBundledDefaults: true, tracksAppLanguage: true);
                await pluginService.InitializeAsync(Token);

                using var workspace = CreateModernWorkspace("ui-cache", pluginService);
                AddModelWithTextures(workspace);
                workspace.SetDefaultRenderingStyle(RenderingPresetReference.BuiltIn(RenderingStyleId.Genshin));

                var owner = new Window();
                owner.Show();
                var pluginsWindow = new EditorPluginsWindow(owner, pluginService, workspace)
                {
                    Width = 680,
                    Height = 560
                };
                pluginsWindow.Show();
                try
                {
                    string[] alwaysVisibleNames =
                    [
                        "InstalledPlugins",
                        "EnablePluginButton",
                        "DisablePluginButton",
                        "RescanPluginsButton",
                        "ImportPluginButton",
                        "ClosePluginsButton",
                        "PluginImportHint",
                        "SelectedPluginName",
                        "SelectedPluginState",
                        "PluginDetails",
                        "PluginOperationResult",
                        "NoPluginCommands",
                        "PluginDirectoryExpander"
                    ];
                    string[] allExpectedNames =
                    [
                        ..alwaysVisibleNames,
                        "PluginCommands",
                        "RunPluginCommandButton",
                        "PluginInstallationDirectory"
                    ];

                    foreach (string lang in new[] { "zh-CN", "en", "ja" })
                    {
                        EditorLocalization.SetLanguage(lang);
                        pluginsWindow.Width = 680;
                        pluginsWindow.Height = 560;
                        pluginsWindow.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();
                        pluginsWindow.UpdateLayout();

                        var listBox = FindNamed<ListBox>(pluginsWindow, "InstalledPlugins");
                        var allPlugins = listBox.ItemsSource!.Cast<PluginInfo>().ToArray();
                        var genshinPlugin = allPlugins.Single(p => p.Id == "mda.style.genshin");
                        listBox.SelectedItem = genshinPlugin;
                        pluginsWindow.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();
                        pluginsWindow.UpdateLayout();

                        var selectedName = FindNamed<TextBlock>(pluginsWindow, "SelectedPluginName");
                        Assert.Equal(genshinPlugin.GetDisplayName(lang), selectedName.Text);

                        var wrapBadges = pluginsWindow.GetVisualDescendants().OfType<WrapPanel>()
                            .Where(panel => panel.IsEffectivelyVisible)
                            .SelectMany(panel => panel.GetVisualDescendants().OfType<TextBlock>())
                            .Select(tb => tb.Text)
                            .ToArray();
                        Assert.Contains(EditorLocalization.Text("Mda.Text.Plugins.Source.Bundled"), wrapBadges);
                        Assert.Contains(EditorLocalization.Text("Mda.Text.Plugins.Capability.RenderingStyles"), wrapBadges);

                        var visibleTexts = pluginsWindow.GetVisualDescendants().OfType<TextBlock>()
                            .Where(tb => tb.IsEffectivelyVisible && !string.IsNullOrEmpty(tb.Text))
                            .Select(tb => tb.Text!)
                            .ToArray();
                        string localizedGenshin = pluginService.CurrentStyleCatalog.FindStyle("genshin")!.DisplayName.Resolve(lang);
                        Assert.Contains(visibleTexts, t => t.Contains("genshin", StringComparison.Ordinal) && t.Contains(localizedGenshin, StringComparison.Ordinal));

                        string expectedWarning = EditorLocalization.Format("Mda.Text.Plugins.SceneInUseWarning", localizedGenshin);
                        var warningTextBlock = pluginsWindow.GetVisualDescendants().OfType<TextBlock>()
                            .Single(tb => string.Equals(tb.Text, expectedWarning, StringComparison.Ordinal));
                        Assert.True(warningTextBlock.IsEffectivelyVisible);

                        // Switch to unused plugin -> scene-in-use banner hides cleanly.
                        listBox.SelectedItem = allPlugins.Single(p => p.Id == "mda.style.star-rail");
                        Dispatcher.UIThread.RunJobs();
                        Assert.False(warningTextBlock.IsEffectivelyVisible);

                        // Switch back to genshin for bounds verification.
                        listBox.SelectedItem = genshinPlugin;
                        pluginsWindow.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();
                        pluginsWindow.UpdateLayout();

                        foreach (string name in alwaysVisibleNames)
                        {
                            var control = FindNamed<Control>(pluginsWindow, name);
                            Assert.True(control.IsEffectivelyVisible, $"Control '{name}' must be visible in '{lang}'.");
                            Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0,
                                $"Control '{name}' had zero size ({control.Bounds}) in '{lang}'.");
                            var topLeft = control.TranslatePoint(new Point(0, 0), pluginsWindow);
                            var bottomRight = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), pluginsWindow);
                            Assert.True(topLeft.HasValue && bottomRight.HasValue,
                                $"Control '{name}' failed coordinate mapping in '{lang}'.");
                            Assert.InRange(topLeft.Value.X, -0.5, 680.5);
                            Assert.InRange(topLeft.Value.Y, -0.5, 560.5);
                            Assert.InRange(bottomRight.Value.X, -0.5, 680.5);
                            Assert.InRange(bottomRight.Value.Y, -0.5, 560.5);
                        }

                        var directoryExpander = FindNamed<Expander>(pluginsWindow, "PluginDirectoryExpander");
                        directoryExpander.IsExpanded = true;
                        pluginsWindow.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();
                        pluginsWindow.UpdateLayout();

                        foreach (string name in allExpectedNames)
                            _ = FindNamed<Control>(pluginsWindow, name);

                        directoryExpander.IsExpanded = false;
                        pluginsWindow.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();
                    }
                }
                finally
                {
                    pluginsWindow.Close();
                    owner.Close();
                    Dispatcher.UIThread.RunJobs();
                }
            }
            finally
            {
                EditorLocalization.SetLanguage(priorLang);
            }
            return true;
        }, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

    private static EditorPluginService CreatePluginService(
        string installDir,
        string prefsPath,
        bool useBundledDefaults,
        bool tracksAppLanguage = false)
        => new(
            installDir,
            prefsPath,
            new PluginHostInfo(new Version(0, 1, 22), 1, "en"),
            [TestStylePlugins.BundledPluginsRoot],
            useBundledDefaults ? EditorPluginService.BundledDefaultPluginIds : null,
            tracksAppLanguage: tracksAppLanguage);

    private EditorProjectWorkspace CreateModernWorkspace(string cacheName, EditorPluginService plugins)
    {
        var workspace = new EditorProjectWorkspace { PackageCacheDirectory = Path.Combine(directory, cacheName) };
        workspace.AttachPluginService(plugins);
        return workspace;
    }

    private EditorProjectWorkspace CreateClassicWorkspace(string cacheName)
    {
        string compatDir = Path.Combine(directory, cacheName + "-compat");
        Directory.CreateDirectory(compatDir);
        string native = Path.Combine(compatDir, "Mmd.Modern.D3D9.Compatibility.dll");
        string d3dx = Path.Combine(compatDir, "d3dx9_43.dll");
        string compiler = Path.Combine(compatDir, "d3dcompiler_43.dll");
        File.WriteAllText(native, "fixture");
        File.WriteAllText(d3dx, "fixture");
        File.WriteAllText(compiler, "fixture");
        var engine = new EditorClassicRenderEngine(
            new EditorCompatibilityRuntimeSource(native, d3dx, compiler),
            compatDir);
        return new EditorProjectWorkspace(engine) { PackageCacheDirectory = Path.Combine(directory, cacheName) };
    }

    private Guid AddModelWithTextures(EditorProjectWorkspace workspace)
    {
        string modelDir = Path.Combine(directory, "model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(modelDir);
        File.WriteAllBytes(Path.Combine(modelDir, "tex0.png"), CreateSolidPng2x2(255, 64, 64, 255));
        File.WriteAllBytes(Path.Combine(modelDir, "tex1.png"), CreateSolidPng2x2(64, 255, 64, 255));
        File.WriteAllBytes(Path.Combine(modelDir, "tex2.png"), CreateSolidPng2x2(64, 64, 255, 255));

        var template = PmxReader.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
        PmxVertex MakeVertex(Vector3 pos, Vector2 uv) => new()
        {
            Position = pos,
            Normal = -Vector3.UnitZ,
            Uv = uv,
            AdditionalUvs = new Vector4[template.Header.AdditionalUvCount],
            WeightType = PmxVertexWeightType.Bdef1,
            BoneIndices = [0, -1, -1, -1],
            BoneWeights = [1f, 0f, 0f, 0f],
            EdgeScale = 1f
        };

        var model = new PmxModel
        {
            Header = template.Header,
            Name = "plugin-ui-fixture",
            EnglishName = "plugin-ui-fixture",
            Comment = "",
            EnglishComment = "",
            Vertices =
            [
                MakeVertex(new(-1f, -1f, 0f), new(0f, 0f)),
                MakeVertex(new(0f, 1f, 0f), new(0.5f, 1f)),
                MakeVertex(new(1f, -1f, 0f), new(1f, 0f))
            ],
            Faces = [new(0, 1, 2), new(0, 1, 2)],
            Textures = ["tex0.png", "tex1.png", "tex2.png"],
            Materials =
            [
                new()
                {
                    Name = "face",
                    EnglishName = "face",
                    Diffuse = new(0.8f, 0.7f, 0.65f, 1f),
                    Specular = new(0.2f),
                    SpecularPower = 8f,
                    Ambient = new(0.3f),
                    EdgeColor = new(0f, 0f, 0f, 1f),
                    EdgeSize = 1f,
                    DrawMode = PmxDrawModeFlags.BothFaces | PmxDrawModeFlags.DrawEdge,
                    TextureIndex = 0,
                    SphereTextureIndex = 1,
                    SphereMode = PmxSphereMode.Add,
                    ToonMode = PmxToonMode.Separate,
                    ToonTextureIndex = 2,
                    Memo = "",
                    FaceVertexCount = 3
                },
                new()
                {
                    Name = "body",
                    EnglishName = "body",
                    Diffuse = new(0.7f, 0.7f, 0.8f, 1f),
                    Specular = new(0.3f),
                    SpecularPower = 12f,
                    Ambient = new(0.3f),
                    EdgeColor = new(0f, 0f, 0f, 1f),
                    EdgeSize = 1f,
                    DrawMode = PmxDrawModeFlags.BothFaces | PmxDrawModeFlags.DrawEdge,
                    TextureIndex = 2,
                    SphereTextureIndex = -1,
                    SphereMode = PmxSphereMode.None,
                    ToonMode = PmxToonMode.Common,
                    ToonTextureIndex = 0,
                    Memo = "",
                    FaceVertexCount = 3
                }
            ],
            Bones = template.Bones,
            Morphs = [],
            DisplayFrames = [],
            RigidBodies = [],
            Joints = [],
            SoftBodies = []
        };

        string pmxPath = Path.Combine(modelDir, "actor.pmx");
        File.WriteAllBytes(pmxPath, PmxWriter.ToBytes(model));
        return workspace.AddModel(pmxPath, EditorProjectWorkspace.CreateModelAsset(model, pmxPath, Token), ModelRole.Character);
    }

    private static T FindNamed<T>(Visual root, string name) where T : Control
        => Assert.Single(root.GetVisualDescendants().OfType<T>(), c => string.Equals(c.Name, name, StringComparison.Ordinal));

    private static byte[] CreateSolidPng2x2(byte r, byte g, byte b, byte a)
    {
        using var ms = new MemoryStream();
        ms.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), 2);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), 2);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        WriteChunk(ms, "IHDR", ihdr);

        byte[] raw =
        [
            0, r, g, b, a, r, g, b, a,
            0, r, g, b, a, r, g, b, a
        ];
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);
        WriteChunk(ms, "IDAT", compressed.ToArray());
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();

        static void WriteChunk(Stream dst, string type, byte[] data)
        {
            Span<byte> buf4 = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buf4, data.Length);
            dst.Write(buf4);
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            dst.Write(typeBytes);
            dst.Write(data);
            uint crc = Crc32(typeBytes, data);
            BinaryPrimitives.WriteUInt32BigEndian(buf4, crc);
            dst.Write(buf4);
        }

        static uint Crc32(byte[] a, byte[] b)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte val in a) crc = Update(crc, val);
            foreach (byte val in b) crc = Update(crc, val);
            return ~crc;

            static uint Update(uint c, byte v)
            {
                c ^= v;
                for (int bit = 0; bit < 8; bit++)
                    c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
                return c;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Task.Run(session.Dispose).ConfigureAwait(false); }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
