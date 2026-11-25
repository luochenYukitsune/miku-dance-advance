using System.Collections.Immutable;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorProjectPersistenceRoutingTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession dispatcher = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void SelectedEngineDeclaresAnImmutableNativePackageIdentity()
    {
        using var files = new Fixture();
        using var modern = new EditorProjectWorkspace();
        using var classic = files.Workspace();
        Assert.Same(EditorProjectFormat.Modern, modern.ProjectFormat);
        Assert.Same(EditorProjectFormat.Classic, classic.ProjectFormat);
        Assert.Equal(PmaProjectStore.Format, modern.ProjectFormat.PackageFormat);
        Assert.Equal(PmcProjectStore.Format, classic.ProjectFormat.PackageFormat);
        Assert.Equal(".pma", modern.ProjectFormat.Extension);
        Assert.Equal(".pmc", classic.ProjectFormat.Extension);
        Assert.Throws<InvalidDataException>(() => EditorProjectFormat.FromPackageFormat("unknown"));
    }

    [Fact]
    public async Task ConversionMenuTargetsTheOtherFormatAndRequiresAnOpenDocument()
        => await dispatcher.Dispatch(() =>
        {
            using var files = new Fixture();
            using var modern = new EditorShellViewModel();
            using var classic = new EditorShellViewModel(new PmmCameraPose(new(0, 10, 0), default, -45, 30, true), files.ProductionEngine());
            var window = new EditorWindow(false);
            try
            {
                var item = window.FindControl<MenuItem>("ConvertProjectMenuItem")!;
                window.DataContext = modern;
                Assert.Contains(".pmc", Assert.IsType<string>(item.Header));
                Assert.Same(modern.ConvertProjectCommand, item.Command);
                Assert.False(item.Command!.CanExecute(null));
                files.AddModel(modern.Workspace);
                Assert.True(item.Command.CanExecute(null));
                window.DataContext = classic;
                Assert.Contains(".pma", Assert.IsType<string>(item.Header));
                Assert.Same(classic.ConvertProjectCommand, item.Command);
                Assert.False(item.Command!.CanExecute(null));
            }
            finally
            {
                window.DataContext = null;
                window.FindControl<EditorViewportHost>("SceneViewport")!.Dispose();
                window.Close();
            }
            return true;
        }, Token);

    [Fact]
    public async Task ConvertedCopiesBindInProductionClassicAndRestoreModernAuthoringAfterOrdinarySave()
        => await dispatcher.Dispatch(async () =>
        {
            using var files = new Fixture();
            using var modern = new EditorProjectWorkspace { PackageCacheDirectory = files.Path("modern-cache") };
            byte[] bitmap = EditorClassicToonPackageTests.Bitmap();
            File.WriteAllBytes(files.Path("authored.bmp"), bitmap);
            var id = files.AddModel(modern, EditorClassicToonPackageTests.Model());
            var mapping = new D3D12MaterialBindingProfile("conversion.mapping", "Authored",
                D3D12MaterialBindingLayout.Capture(modern.Session!.Objects.Single().Source).Fingerprint, []);
            modern.MaterialBindings.Add(id, mapping);
            string original = files.Path("original.pma"), copy = files.Path("copy.pmc"), restored = files.Path("back.pma");
            modern.Save(original, Token);
            var originalBytes = File.ReadAllBytes(original);
            var light = EditorRecoveryNativeLightingTests.Light("unsaved light");
            modern.EditLighting(new(new([light]), light.Id));
            Guid document = modern.DocumentId;
            await Assert.ThrowsAsync<InvalidOperationException>(() => modern.ExportConvertedCopyAsync(copy, Token));
            Assert.False(File.Exists(copy));
            int requests = 0;
            var report = await modern.ExportConvertedCopyAsync(copy, Token, cancellation =>
            {
                requests++;
                return Enumerable.Range(0, 11).Select(slot =>
                {
                    byte[] encoded = bitmap.ToArray();
                    encoded[54] = (byte)(slot + 10);
                    string name = $"toon{slot:00}.bmp";
                    return new EditorClassicToonResources.Resource(slot, name, name, encoded,
                        EditorTextureLoader.Decode(encoded, name, cancellation));
                }).ToArray();
            });
            Assert.Equal(1, requests);
            File.Delete(files.Path("authored.bmp"));
            Assert.Equal(1, report.OmittedLights);
            Assert.Equal(original, modern.ProjectPath);
            Assert.True(modern.IsDirty); Assert.Equal(document, modern.DocumentId);
            Assert.Equal(originalBytes, File.ReadAllBytes(original));
            Assert.True(modern.Undo()); Assert.Empty(modern.Session.Lighting.Lights);
            var copyBytes = File.ReadAllBytes(copy);
            await Assert.ThrowsAsync<IOException>(() => modern.ExportConvertedCopyAsync(copy, Token));
            Assert.Equal(copyBytes, File.ReadAllBytes(copy));

            using var classic = new EditorProjectWorkspace(files.ProductionEngine()) { PackageCacheDirectory = files.Path("classic-cache") };
            classic.Open(copy, Token);
            Assert.Empty(classic.Session!.Lighting.Lights);
            Assert.Empty(classic.MaterialBindings);
            Assert.Same(classic.RenderEngine, classic.CaptureRenderBinding().Engine);
            var state = Assert.IsType<EditorClassicDocumentState>(classic.ProjectMetadata);
            Assert.NotNull(state.FrozenEffects);
            Assert.Null(Assert.Single(state.FrozenEffects.RootSeeds).OriginAbsoluteFxPath);
            var surface = classic.Session.Objects.Single().Source.Surface.Snapshot;
            Assert.Equal(new byte[] { 10, 15, 51 }, surface.Materials
                .Select(material => surface.Textures[material.ToonTexture].Rgba[2]).ToArray());
            var motion = new VmdMotion { ModelName = classic.Motions.Single().Model.Name,
                BoneKeyframes = [new VmdBoneKeyframe { BoneName = "root", Frame = 12 }] };
            classic.CommitMotion(id, motion);
            string working = files.Path("edited.pmc");
            classic.Save(working, Token);
            classic.Open(working, Token);
            var back = await classic.ExportConvertedCopyAsync(restored, Token);
            Assert.True(back.RestoredArchive);
            Assert.Equal(working, classic.ProjectPath); Assert.False(classic.IsDirty);
            using var result = new EditorProjectWorkspace { PackageCacheDirectory = files.Path("roundtrip-cache") };
            result.Open(restored, Token);
            Assert.Equal(id, Assert.Single(result.Session!.Objects).Id);
            Assert.Equal(light, Assert.Single(result.Session.Lighting.Lights));
            Assert.Equal(D3D12MaterialBindingProfileJson.Encode(mapping), D3D12MaterialBindingProfileJson.Encode(result.MaterialBindings[id]));
            Assert.Equal(12u, Assert.Single(result.Motions.Single().Motion.BoneKeyframes).Frame);
            return true;
        }, Token);

    [Theory]
    [InlineData("toon01.bmp")]
    [InlineData("TOON01.BMP")]
    [InlineData(null)]
    public async Task ClassicConversionUsesCanonicalPmdToonFromThePackageBeforeOriginalMmd(string? packagedName)
        => await dispatcher.Dispatch(() =>
        {
            using var files = new Fixture();
            string source = files.Path("source.pma"), copy = files.Path("copy.pmc");
            byte[] bitmap = EditorClassicToonPackageTests.Bitmap();
            bool modelCanReadToon = WriteCanonicalPmdPackage(files, source, packagedName, bitmap);
            if (!modelCanReadToon)
            {
                var error = Assert.Throws<NotSupportedException>(() => PmaProjectStore.ConvertPackage(source, copy, files.DirectoryPath, Token));
                Assert.Contains("requires original MMD", error.Message);
                Assert.False(File.Exists(copy));
                return true;
            }

            PmaProjectStore.ConvertPackage(source, copy, files.DirectoryPath, Token);
            using var classic = new EditorProjectWorkspace(files.ProductionEngine()) { PackageCacheDirectory = files.Path("classic-cache") };
            classic.Open(copy, Token);
            var model = Assert.Single(classic.Motions).Model;
            var material = Assert.Single(model.Materials);
            Assert.Equal(PmxToonMode.Separate, material.ToonMode);
            Assert.Equal("toon01.bmp", model.Textures[material.ToonTextureIndex]);
            var surface = Assert.Single(classic.Session!.Objects).Source.Surface.Snapshot;
            var bound = surface.Textures[Assert.Single(surface.Materials).ToonTexture];
            Assert.Equal(SceneTextureKind.Image, bound.Kind);
            Assert.Equal(EditorTextureLoader.Decode(bitmap, "toon01.bmp", Token).Rgba.ToArray(), bound.Rgba.ToArray());
            return true;
        }, Token);

    // Author a valid package with the original PMD basename. A regular PMA save rewrites
    // portable texture names and stores the animated actor as PMX, so it cannot exercise this case.
    private static bool WriteCanonicalPmdPackage(Fixture files, string destination, string? packagedName, byte[] toon)
    {
        string directory = files.Path("loose"), actor = Path.Combine(directory, "actor");
        Directory.CreateDirectory(actor);
        const string modelPath = "actor/model.pmd", projectPath = "project.mmdscene";
        string modelFile = Path.Combine(directory, modelPath);
        var pmd = new PmdModel("model", [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero, 0, 0, 1),
            new(Vector3.UnitX, Vector3.UnitZ, Vector2.Zero, 0, 0, 1), new(Vector3.UnitY, Vector3.UnitZ, Vector2.Zero, 0, 0, 1)],
            [0, 1, 2], [new ModelMaterial(Vector4.One, Vector3.Zero, new(.3f), 4, "", 0, 3) { ToonIndex = 0 }],
            [new("root", -1, Vector3.Zero)], 0);
        File.WriteAllBytes(modelFile, PmdWriter.ToBytes(pmd));
        if (packagedName is not null) File.WriteAllBytes(Path.Combine(actor, packagedName), toon);
        // The conversion stage is on this same volume. Probe its actual behavior, including
        // case-sensitive directories on Windows or case-insensitive volumes on other platforms.
        bool modelCanReadToon = File.Exists(Path.Combine(actor, "toon01.bmp"));
        using var modern = new EditorProjectWorkspace();
        Guid id = modern.AddModel(modelFile, EditorProjectWorkspace.LoadModelAsset(modelFile, Token), ModelRole.Prop);
        modern.Session!.Save(Path.Combine(directory, projectPath), new() { CopyModelAssets = false }, Token);
        var resources = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(file =>
        {
            byte[] bytes = File.ReadAllBytes(file);
            return new { path = Path.GetRelativePath(directory, file).Replace('\\', '/'), bytes, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        }).ToArray();
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
        using (var stream = zip.CreateEntry("manifest.json").Open())
            JsonSerializer.Serialize(stream, new
            {
                format = PmaProjectStore.Format, version = 1, minimumReaderVersion = 1, producer = "fixture",
                projectId = Guid.NewGuid(), projectPath, projectSchema = SceneAnimationProjectStore.SettingsVersion,
                files = resources.Select(item => new { item.path, length = item.bytes.Length, item.sha256 }),
                models = new[] { new { objectId = id, originalSourcePath = modelPath, originalModelPath = modelPath, editedModelPath = modelPath } },
                requiredExtensions = Array.Empty<string>(), optionalExtensions = new { }
            });
        foreach (var item in resources.DistinctBy(item => item.sha256))
        {
            using var blob = zip.CreateEntry("blobs/" + item.sha256).Open();
            blob.Write(item.bytes);
        }
        Directory.Delete(directory, recursive: true);
        return modelCanReadToon;
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public async Task ConvertedModernProjectExportsVisibleGeometryThroughNativeClassic()
        => await dispatcher.Dispatch(async () =>
        {
            string? runtimeDirectory = Environment.GetEnvironmentVariable("MMD_CLASSIC_RUNTIME_DIRECTORY");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(runtimeDirectory), "Set MMD_CLASSIC_RUNTIME_DIRECTORY to a portable Classic runtime.");
            using var files = new Fixture();
            using var modern = new EditorProjectWorkspace { PackageCacheDirectory = files.Path("modern-cache") };
            files.AddModel(modern, EditorExportPaletteGpuIntegrationTests.Model());
            string copy = files.Path("converted.pmc");
            var bitmap = EditorClassicToonPackageTests.Bitmap();
            await modern.ExportConvertedCopyAsync(copy, Token, cancellation =>
                [new(0, "fixture", "default.bmp", bitmap, EditorTextureLoader.Decode(bitmap, "default.bmp", cancellation))]);
            File.Delete(files.Path("model.pmx"));

            var runtime = new EditorCompatibilityRuntimeSource(Path.Combine(runtimeDirectory, "Mmd.Modern.D3D9.Compatibility.dll"),
                Path.Combine(runtimeDirectory, "d3dx9_43.dll"), Path.Combine(runtimeDirectory, "d3dcompiler_43.dll"));
            string cache = files.Path("classic-cache");
            Directory.CreateDirectory(cache);
            var engine = new EditorClassicRenderEngine(runtime, cache);
            using var classic = new EditorProjectWorkspace(engine) { PackageCacheDirectory = cache };
            classic.Open(copy, Token);
            const int size = 96;
            var quality = EditorRenderEngineOptions.DefaultExportQuality(engine);
            var plan = new OfflineFramePlan(size, size, new(30), 0, 0);
            var preset = new ViewOutputPresetDefinition("test.classic.converted", "Converted PMC",
                new(size, size, quality), new(size, size, new(30), quality));
            string output = files.Path("frame.png");
            var draft = new OutputSettingsDraft(preset, new(plan, quality), OutputMediaKind.PngImage, output, false, Vector4.One, null);
            using var diagnostics = ExportDiagnosticSession.Start(classic, draft, files.Path("export-log"));
            var result = await EditorExportService.ExportWithResourcesAsync(classic, draft, null, null, null, null, Token, diagnostics);

            Assert.Equal(1, result.CompletedFrames);
            var image = EditorTextureLoader.Decode(File.ReadAllBytes(output), "frame.png", Token);
            Assert.Equal(size, image.Width); Assert.Equal(size, image.Height);
            byte[] pixels = image.Rgba.ToArray();
            Assert.Contains(Enumerable.Range(0, size * size), i => pixels[i * 4] == 255 && pixels[i * 4 + 2] == 255);
            Assert.Contains(Enumerable.Range(0, size * size), i => pixels[i * 4 + 2] > pixels[i * 4]);
            return true;
        }, Token);

    [Fact]
    public async Task ClassicCaptureProjectsModernLightsWithoutChangingLiveContentAndLoadsOnAWorker()
        => await dispatcher.Dispatch(async () =>
        {
            using var files = new Fixture();
            using var classic = files.Workspace();
            string source = files.Path("model.pmx");
            var model = EditorRecoveryPmaIntegrationTests.Model();
            File.WriteAllBytes(source, PmxWriter.ToBytes(model));
            classic.AddModel(source, EditorProjectWorkspace.CreateModelAsset(model, source, Token), ModelRole.Prop);
            var light = EditorRecoveryNativeLightingTests.Light("modern light");
            classic.EditLighting(new(new SceneLightingSnapshot([light])));
            var live = classic.Session;
            string destination = files.Path("classic.pmc");
            using var snapshot = classic.CaptureRecoverySnapshot(Token);
            Assert.Equal(PmcProjectStore.Format, snapshot.Format);
            Assert.Equal(".pmc", snapshot.FileExtension);
            Assert.Equal(light, Assert.Single(classic.Session!.Lighting.Lights));
            Assert.Same(live, classic.Session);
            classic.New();
            await Task.Run(() => snapshot.Save(destination, Token), Token);
            Assert.Null(classic.Session);
            await Task.Run(() => classic.Open(destination, Token), Token);
            Assert.Empty(classic.Session!.Lighting.Lights);
            Assert.Equal(destination, classic.ProjectPath);
            Assert.False(classic.IsDirty);
            Assert.NotSame(live, classic.Session);
            return true;
        }, Token);

    [Fact]
    public async Task ModernMaterialMappingRoundTripKeepsItsObjectIdentityAndFingerprint()
        => await dispatcher.Dispatch(async () =>
        {
            using var files = new Fixture();
            using var workspace = new EditorProjectWorkspace { PackageCacheDirectory = files.Path("modern-cache") };
            Guid id = files.AddModel(workspace);
            var profile = new D3D12MaterialBindingProfile("route.mapping", "Route mapping",
                D3D12MaterialBindingLayout.Capture(workspace.Session!.Objects.Single().Source).Fingerprint, []);
            workspace.MaterialBindings.Add(id, profile);
            string path = files.Path("mapping.pma");
            workspace.Save(path, Token);
            await Task.Run(() => workspace.Open(path, Token), Token);
            var restored = Assert.Single(workspace.MaterialBindings);
            Assert.Equal(id, restored.Key);
            Assert.Equal(D3D12MaterialBindingProfileJson.Encode(profile), D3D12MaterialBindingProfileJson.Encode(restored.Value));
            Assert.Equal(path, workspace.ProjectPath); Assert.False(workspace.IsDirty);
            return true;
        }, Token);

    [Theory]
    [InlineData(D3D9EmmPma.ExtensionName)]
    [InlineData(D3D9EffectResourceTree.ExtensionName)]
    public async Task MalformedTraditionalSchemaIsRejectedBeforeReplacingActiveContent(string extension)
        => await dispatcher.Dispatch(() =>
        {
            using var files = new Fixture();
            using var workspace = files.Workspace();
            files.AddModel(workspace);
            string original = files.Path("active.pmc");
            workspace.Save(original, Token); workspace.MarkChanged();
            string malformed = files.Path("malformed.pmc");
            D3D9ClassicProjectStore.Save(malformed, workspace.Session!, new()
            {
                CacheDirectory = files.Path("stage"),
                OptionalExtensions = new Dictionary<string, JsonElement>
                { [extension] = JsonSerializer.SerializeToElement(new { schemaVersion = 999 }) }
            }, Token);
            var active = workspace.Session; var document = workspace.DocumentId;
            bool undo = workspace.CanUndo, redo = workspace.CanRedo;
            Assert.Throws<InvalidDataException>(() => workspace.Open(malformed, Token));
            Assert.Same(active, workspace.Session); Assert.Equal(document, workspace.DocumentId);
            Assert.Equal(original, workspace.ProjectPath); Assert.True(workspace.IsDirty);
            Assert.Equal(undo, workspace.CanUndo); Assert.Equal(redo, workspace.CanRedo);
            Assert.Single(workspace.Session!.Objects);
            return true;
        }, Token);

    [Fact]
    public async Task ClassicUnknownOpaqueDataSurvivesDetachedSaveAfterClosingLoadedDocument()
        => await dispatcher.Dispatch(async () =>
        {
            using var files = new Fixture();
            using var workspace = files.Workspace();
            files.AddModel(workspace);
            const string extension = "org.example.future";
            const string resource = "render-resources/unknown/data.bin";
            string source = files.Path("opaque.pmc"), copy = files.Path("copy.pmc");
            D3D9ClassicProjectStore.Save(source, workspace.Session!, new()
            {
                CacheDirectory = files.Path("stage"),
                OptionalExtensions = new Dictionary<string, JsonElement>
                { [extension] = JsonSerializer.SerializeToElement(new { retained = true }) },
                AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { [resource] = new byte[] { 0, 127, 255 } }
            }, Token);
            await Task.Run(() => workspace.Open(source, Token), Token);
            using var snapshot = workspace.CaptureRecoverySnapshot(Token);
            workspace.New();
            await Task.Run(() => snapshot.Save(copy, Token), Token);
            using var loaded = PmcProjectStore.Load(copy, files.Path("readback"), EditorProjectWorkspace.LoadModelAsset,
                EditorProjectWorkspace.CreatePhysicsProvider(), cancellationToken: Token);
            Assert.True(loaded.OptionalExtensions[extension].GetProperty("retained").GetBoolean());
            Assert.Equal(new byte[] { 0, 127, 255 }, loaded.ReadResource(resource));
            return true;
        }, Token);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredTraditionalDataLoadsOnWorkerButOrdinarySurfaceBindingRejectsIt(bool tree)
        => await dispatcher.Dispatch(async () =>
        {
            using var files = new Fixture();
            using var workspace = files.Workspace();
            Guid id = files.AddModel(workspace);
            var options = new PmaSaveOptions { CacheDirectory = files.Path("stage") };
            if (tree)
            {
                string root = files.Path("effects"); Directory.CreateDirectory(root);
                File.WriteAllText(System.IO.Path.Combine(root, "fixture.fx"), "// own fixture bytes");
                options = D3D9EffectResourceTree.CreateSaveOptions(workspace.Session!,
                    D3D9EffectResourceTree.Capture(root, ["fixture.fx"], cancellationToken: Token), options);
            }
            else
            {
                var document = EmmReader.Read(Encoding.ASCII.GetBytes(
                    "[Info]\r\nVersion=3\r\n[Object]\r\nPmd0=model.pmx\r\n[Effect]\r\nDefault=main_default\r\nPmd0=fixture.fx\r\n"),
                    new() { AnsiCodePage = 936 });
                var plan = EmmImportPlan.Create(document, files.Path("fixture.emm"),
                    [new(id, true, files.Path("model.pmx"), 0)], files.DirectoryPath);
                var state = D3D9EmmPma.Capture(plan, [new(files.Path("fixture.fx"), Encoding.ASCII.GetBytes("// own fixture bytes"))]);
                options = D3D9EmmPma.CreateSaveOptions(workspace.Session!, state, options);
            }
            string path = files.Path("effects.pmc");
            D3D9ClassicProjectStore.Save(path, workspace.Session!, options, Token);
            await Task.Run(() => workspace.Open(path, Token), Token);
            var error = Assert.Throws<NotSupportedException>(() => workspace.CaptureRenderBinding());
            Assert.Contains("MME/EMM", error.Message, StringComparison.Ordinal);
            Assert.Equal(path, workspace.ProjectPath); Assert.False(workspace.IsDirty);
            return true;
        }, Token);

    [Fact]
    public async Task ClassicRecoveryProjectionPreservesOriginalLightCameraAndNumericDrafts()
        => await dispatcher.Dispatch(() =>
        {
            using var files = new Fixture();
            using var workspace = files.Workspace();
            Guid actor = files.AddModel(workspace);
            var motion = Assert.Single(workspace.Motions);
            var animation = new EditorAnimationViewModel(); animation.SetActor(actor, motion.Model, motion.Motion);
            var scene = new EditorSceneInspectorViewModel { LightDirectionX = 12.5m, CameraCenterX = 3.25m };
            var light = EditorRecoveryNativeLightingTests.Light("modern draft");
            workspace.EditLighting(new(new SceneLightingSnapshot([light])));
            var lights = new EditorLightInspector(); lights.SetLighting(workspace.Session!.Lighting, light.Id, workspace.DocumentId);
            var original = EditorRecoverySessionState.Capture(animation, scene, workspace.Motions) with
            {
                NumericInputText = ImmutableSortedDictionary<string, string>.Empty.Add("LightIntensity", "unfinished light"),
                NativeLighting = lights.CaptureRecoveryState(workspace.Session.Lighting, EditorInspectorPage.Lighting, workspace.DocumentId)
            };
            var projected = workspace.ProjectRecoveryState(original);
            Assert.Null(projected.NativeLighting); Assert.NotNull(original.NativeLighting);
            Assert.Same(original.Scene, projected.Scene); Assert.Same(original.Animation, projected.Animation);
            Assert.Same(original.NumericInputText, projected.NumericInputText);
            Assert.Equal(12.5m, projected.Scene.DirectionX); Assert.Equal(3.25m, projected.Scene.CenterX);
            Assert.Equal(light, Assert.Single(workspace.Session.Lighting.Lights));
            return true;
        }, Token);

    [Fact]
    public async Task CanceledOpenPreservesCurrentDocumentAndHistory()
        => await dispatcher.Dispatch(() =>
        {
            using var files = new Fixture();
            using var workspace = files.Workspace(); files.AddModel(workspace);
            string path = files.Path("valid.pmc"); workspace.Save(path, Token); workspace.MarkChanged();
            var active = workspace.Session; var document = workspace.DocumentId;
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => workspace.Open(path, canceled.Token));
            Assert.Same(active, workspace.Session); Assert.Equal(document, workspace.DocumentId);
            Assert.Equal(path, workspace.ProjectPath); Assert.True(workspace.IsDirty); Assert.True(workspace.CanUndo);
            return true;
        }, Token);

    [Fact]
    public async Task ForeignPackageOrWrongSuffixLeavesTheCurrentDocumentUntouched()
        => await dispatcher.Dispatch(() =>
        {
            using var files = new Fixture();
            string source = files.Path("model.pmx");
            var model = EditorRecoveryPmaIntegrationTests.Model();
            File.WriteAllBytes(source, PmxWriter.ToBytes(model));
            using var modern = new EditorProjectWorkspace { PackageCacheDirectory = files.Path("modern-cache") };
            modern.AddModel(source, EditorProjectWorkspace.CreateModelAsset(model, source, Token), ModelRole.Prop);
            modern.Save(files.Path("modern.pma"), Token);
            using var classic = files.Workspace();
            classic.AddModel(source, EditorProjectWorkspace.CreateModelAsset(model, source, Token), ModelRole.Prop);
            var current = classic.Session; var document = classic.DocumentId;
            Assert.Throws<ArgumentException>(() => classic.Save(files.Path("wrong.pma"), Token));
            Assert.Throws<ArgumentException>(() => classic.Open(files.Path("modern.pma"), Token));
            File.Copy(files.Path("modern.pma"), files.Path("renamed.pmc"));
            Assert.Throws<NotSupportedException>(() => classic.Open(files.Path("renamed.pmc"), Token));
            Assert.Same(current, classic.Session); Assert.Equal(document, classic.DocumentId);
            Assert.True(classic.IsDirty); Assert.Null(classic.ProjectPath); Assert.True(classic.CanUndo);
            return true;
        }, Token);

    [Fact]
    public async Task ClassicRecoveryRoundTripKeepsOriginalDraftsAndRejectsAModernWorkspace()
        => await dispatcher.Dispatch(async () =>
        {
            using var files = new Fixture();
            using var workspace = files.Workspace();
            Guid actorId = files.AddModel(workspace);
            var nativeLight = EditorRecoveryNativeLightingTests.Light("modern light excluded");
            workspace.EditLighting(new(new SceneLightingSnapshot([nativeLight])));
            var actor = Assert.Single(workspace.Motions);
            var animation = new EditorAnimationViewModel { CurrentFrame = 7 };
            var scene = new EditorSceneInspectorViewModel { CurrentFrame = 7, LightDirectionX = 12.5m, CameraCenterX = 3.25m };
            animation.SetActor(actorId, actor.Model, actor.Motion); animation.SelectBone(0);
            animation.PositionX = 8.5m;
            workspace.Session!.Seek(7, Token);
            var lightInspector = new EditorLightInspector();
            lightInspector.SetLighting(workspace.Session.Lighting, nativeLight.Id, workspace.DocumentId);
            var captured = EditorRecoverySessionState.Capture(animation, scene, workspace.Motions) with
            {
                NativeLighting = lightInspector.CaptureRecoveryState(workspace.Session.Lighting,
                    EditorInspectorPage.Lighting, workspace.DocumentId)
            };
            var projected = workspace.ProjectRecoveryState(captured);
            Assert.NotNull(captured.NativeLighting); Assert.Null(projected.NativeLighting);
            using var json = JsonDocument.Parse(projected.ToJsonBytes());
            using var snapshot = workspace.CaptureRecoverySnapshot(Token);
            var store = new EditorRecoveryStore(files.Path("recovery"));
            using var recoverySession = await store.CreateSessionAsync(null, "Classic recovery", Token);
            var metadata = new EditorRecoveryCheckpointMetadata(null, "Classic recovery", 1,
                json.RootElement, projectFormat: workspace.ProjectFormat);
            workspace.New();
            var checkpoint = await store.WriteCheckpointAsync(recoverySession, metadata,
                (path, token) => Task.Run(() => snapshot.Save(path, token), token), Token);
            recoverySession.Dispose();
            var rediscovered = Assert.Single(Assert.Single(await store.ListRecoverableAsync(Token)).Checkpoints);
            Assert.Equal(checkpoint.CheckpointId, rediscovered.CheckpointId);
            using var restored = files.Workspace();
            var state = await Task.Run(() => restored.OpenRecovery(rediscovered, Token), Token);
            Assert.True(restored.IsDirty); Assert.Null(restored.ProjectPath);
            Assert.Empty(restored.Session!.Lighting.Lights); Assert.Null(state.NativeLighting);
            Assert.Equal(12.5m, state.Scene.DirectionX); Assert.Equal(3.25m, state.Scene.CenterX);
            var restoredAnimation = new EditorAnimationViewModel();
            state.Restore(restoredAnimation, new EditorSceneInspectorViewModel(), restored.Motions);
            Assert.Equal(8.5m, restoredAnimation.PositionX);
            using var modern = new EditorProjectWorkspace { PackageCacheDirectory = files.Path("other-cache") };
            files.AddModel(modern);
            var active = modern.Session; var document = modern.DocumentId;
            Assert.Throws<InvalidDataException>(() => modern.OpenRecovery(rediscovered, Token));
            Assert.Same(active, modern.Session); Assert.Equal(document, modern.DocumentId);
            return true;
        }, Token);

    [Fact]
    public async Task ConvertedCopyCarriesUnregisteredDraftsAndUsesLatestClassicEditOnReturn()
        => await dispatcher.Dispatch(async () =>
        {
            using var files = new Fixture();
            var camera = new PmmCameraPose(new(0, 10, 0), default, -45, 30, true);
            using var modern = new EditorShellViewModel(camera, EditorModernRenderEngine.Instance);
            using var classic = new EditorShellViewModel(camera, files.ProductionEngine());
            modern.Workspace.PackageCacheDirectory = files.Path("source-cache");
            classic.Workspace.PackageCacheDirectory = files.Path("converted-cache");
            var actor = files.AddModel(modern.Workspace);
            modern.Browser.SelectedBoneIndex = 0;
            modern.CurrentFrame = 8; modern.Animation.PositionX = 3m;
            modern.CurrentFrame = 19; modern.Animation.PositionX = 5m;
            string converted = files.Path("drafts.pmc");
            var bitmap = EditorClassicToonPackageTests.Bitmap();
            await modern.Workspace.ExportConvertedCopyAsync(converted, Token, cancellation =>
                [new(0, "fixture", "default.bmp", bitmap, EditorTextureLoader.Decode(bitmap, "default.bmp", cancellation))]);
            Assert.True(modern.Workspace.IsDirty);
            Assert.Null(modern.Workspace.ProjectPath);
            classic.Workspace.Open(converted, Token);
            classic.CurrentFrame = 8; classic.Browser.SelectedBoneIndex = 0;
            Assert.Equal(actor, classic.Animation.ObjectId);
            Assert.Equal(new[] { 8, 19 }, classic.Animation.DraftFrames);
            Assert.Equal(3m, classic.Animation.PositionX);
            Assert.Empty(Assert.Single(classic.Workspace.Motions).Motion.BoneKeyframes);
            await classic.Animation.RegisterKeyframeAsync();
            classic.CurrentFrame = 19;
            Assert.Equal(5m, classic.Animation.PositionX);
            classic.Animation.PositionX = 7m;
            string restored = files.Path("edited-back.pma");
            await classic.Workspace.ExportConvertedCopyAsync(restored, Token);
            Assert.Equal(converted, classic.Workspace.ProjectPath);
            Assert.True(classic.Workspace.IsDirty);
            modern.Workspace.Open(restored, Token);
            modern.CurrentFrame = 19; modern.Browser.SelectedBoneIndex = 0;
            Assert.Equal(actor, modern.Animation.ObjectId);
            Assert.Equal(new[] { 19 }, modern.Animation.DraftFrames);
            Assert.Equal(7m, modern.Animation.PositionX);
            var registered = Assert.Single(Assert.Single(modern.Workspace.Motions).Motion.BoneKeyframes);
            Assert.Equal(8u, registered.Frame);
            Assert.Equal(3f, registered.Translation.X);
            return true;
        }, Token);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeSaveKeepsMultiFramePoseDraftsSeparateFromKeysAndSupportsRegisterUndo(bool classic)
        => await dispatcher.Dispatch(async () =>
        {
            using var files = new Fixture();
            using var compatible = files.Workspace();
            var engine = classic ? compatible.RenderEngine : EditorModernRenderEngine.Instance;
            using var shell = new EditorShellViewModel(new PmmCameraPose(new(0, 10, 0), default, -45, 30, true), engine);
            shell.Workspace.PackageCacheDirectory = files.Path("pose-cache");
            var actor = files.AddModel(shell.Workspace);
            shell.CurrentFrame = 10;
            shell.Animation.SelectBone(0); shell.Animation.PositionX = 3;
            shell.CurrentFrame = 20; shell.Animation.PositionX = 5;
            string path = files.Path(classic ? "poses.pmc" : "poses.pma");
            shell.Workspace.Save(path, Token);
            Assert.False(shell.Workspace.IsDirty);
            shell.Workspace.New();
            Assert.False(shell.Animation.HasAnyDraft);
            shell.Workspace.Open(path, Token);
            shell.CurrentFrame = 10; shell.Animation.SelectBone(0);
            Assert.Equal(actor, shell.Animation.ObjectId);
            Assert.Equal(3m, shell.Animation.PositionX); Assert.Equal(new[] { 10, 20 }, shell.Animation.DraftFrames);
            Assert.Empty(Assert.Single(shell.Workspace.Motions).Motion.BoneKeyframes);
            await shell.Animation.RegisterKeyframeAsync();
            Assert.Equal(10u, Assert.Single(Assert.Single(shell.Workspace.Motions).Motion.BoneKeyframes).Frame);
            Assert.Equal(new[] { 20 }, shell.Animation.DraftFrames);
            Assert.True(shell.Workspace.Undo());
            Assert.Empty(Assert.Single(shell.Workspace.Motions).Motion.BoneKeyframes);
            Assert.Equal(new[] { 10, 20 }, shell.Animation.DraftFrames);
            Assert.True(shell.Workspace.Redo());
            Assert.Equal(new[] { 20 }, shell.Animation.DraftFrames);
            shell.Workspace.Save(path, Token);
            shell.Workspace.Open(path, Token);
            Assert.Equal(new[] { 20 }, shell.Animation.DraftFrames);
            Assert.Single(Assert.Single(shell.Workspace.Motions).Motion.BoneKeyframes);
            return true;
        }, Token);

    [Fact]
    public void SavePreflightReportsMissingVmdHeaderAndCanRetryAfterCorrection()
    {
        using var files = new Fixture();
        using var workspace = new EditorProjectWorkspace { PackageCacheDirectory = files.Path("header-cache") };
        var id = files.AddModel(workspace);
        // A PMM import can retain a motion whose model name exceeds the VMD header width.
        workspace.Edit(edit => edit.Motions[0] = edit.Motions[0] with
        {
            Motion = new VmdMotion { ModelName = new string('x', 21) }, Asset = null
        });

        Assert.Equal(id, Assert.Single(workspace.GetMotionHeaderRequirements()).ObjectId);
        Assert.Empty(workspace.GetMotionPersistenceErrors());
        string path = files.Path("header.pma");
        var error = Assert.Throws<InvalidOperationException>(() => workspace.Save(path, Token));
        Assert.Contains(workspace.Session!.Objects.Single().Name, error.Message);
        Assert.False(File.Exists(path));
        Assert.True(workspace.IsDirty);

        workspace.SetMotionHeader(id, "short");
        workspace.Save(path, Token);
        workspace.Open(path, Token);
        Assert.Equal("short", Assert.Single(workspace.Motions).Motion.ModelName);
    }

    [Fact]
    public void SaveCapturedKeepsWorkspaceDirtyWhenEditedAfterCapture()
    {
        using var files = new Fixture();
        using var workspace = new EditorProjectWorkspace { PackageCacheDirectory = files.Path("dirty-race-cache") };
        files.AddModel(workspace);
        string path = files.Path("dirty-race.pma");
        workspace.Save(path, Token);
        Assert.False(workspace.IsDirty);

        workspace.MarkPoseDraftEdited();
        Assert.True(workspace.IsDirty);
        using var staleSnapshot = workspace.CaptureRecoverySnapshot(Token);
        workspace.MarkPoseDraftEdited();
        workspace.SaveCaptured(path, staleSnapshot, Token);
        Assert.True(workspace.IsDirty);

        using var freshSnapshot = workspace.CaptureRecoverySnapshot(Token);
        workspace.SaveCaptured(path, freshSnapshot, Token);
        Assert.False(workspace.IsDirty);

        workspace.SaveCaptured(path, staleSnapshot, Token);
        Assert.True(workspace.IsDirty);
    }

    [Fact]
    public void SavingPreviousDocumentSnapshotPreservesActiveDocumentAndWritesOriginalPackage()
    {
        using var files = new Fixture();
        using var workspace = new EditorProjectWorkspace { PackageCacheDirectory = files.Path("switch-cache") };
        var originalActor = files.AddModel(workspace);
        using var snapshot = workspace.CaptureRecoverySnapshot(Token);
        workspace.New();
        files.AddModel(workspace);
        string currentPath = files.Path("current.pma");
        workspace.Save(currentPath, Token);
        workspace.MarkPoseDraftEdited();
        int changed = 0;
        workspace.Changed += (_, _) => changed++;

        string previousPath = files.Path("previous.pma");
        workspace.SaveCaptured(previousPath, snapshot, Token);
        Assert.Equal(currentPath, workspace.ProjectPath);
        Assert.True(workspace.IsDirty);
        Assert.Equal(0, changed);
        workspace.Open(previousPath, Token);
        Assert.Equal(originalActor, Assert.Single(workspace.Motions).ObjectId);
    }

    [Fact]
    public async Task HostBusyBlocksAnimationDraftMutationsAndBoneSelectNarrowsAndMorphShortcutRegisters()
        => await dispatcher.Dispatch(async () =>
        {
            using var files = new Fixture();
            var basis = EditorRecoveryPmaIntegrationTests.Model();
            var rigModel = new PmxModel
            {
                Header = basis.Header, Name = "rig", EnglishName = "", Comment = "", EnglishComment = "",
                Vertices = basis.Vertices, Faces = basis.Faces, Textures = [], Materials = basis.Materials,
                Bones =
                [
                    new() { Name = "b0", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate },
                    new() { Name = "b1", EnglishName = "", Position = new(0, 1, 0), ParentIndex = 0, DeformDepth = 1, Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate }
                ],
                Morphs = [new() { Name = "blink", EnglishName = "", Type = PmxMorphType.Position, ControlPanel = 0 }],
                DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
            };
            using var shell = new EditorShellViewModel();
            shell.Workspace.PackageCacheDirectory = files.Path("host-busy-cache");
            var actor = files.AddModel(shell.Workspace, rigModel);

            bool hostBusy = true;
            var guarded = new EditorAnimationViewModel { IsHostBusy = () => hostBusy };
            guarded.SetActor(actor, rigModel, new(), "rig");
            guarded.SelectBone(0);
            Assert.False(guarded.CanTranslate);
            Assert.False(guarded.CanRotate);
            guarded.PositionX = 4m;
            Assert.Equal(0m, guarded.PositionX);
            guarded.SelectMorph(0);
            Assert.False(guarded.CanEditMorph);
            guarded.MorphWeight = 0.5m;
            Assert.Equal(0m, guarded.MorphWeight);

            hostBusy = false;
            guarded.RefreshInputValidation();
            Assert.True(guarded.CanEditMorph);
            guarded.MorphWeight = 0.5m;
            Assert.Equal(0.5m, guarded.MorphWeight);
            guarded.SelectBone(0);
            Assert.True(guarded.CanTranslate);
            Assert.True(guarded.CanRotate);
            guarded.PositionX = 4m;
            Assert.Equal(4m, guarded.PositionX);

            shell.RefreshWorkspace(actor);
            shell.Animation.SelectBones([0, 1]);
            Assert.Equal(new[] { 0, 1 }, shell.Animation.SelectedBoneIndices);
            shell.Animation.SelectBone(1);
            Assert.Equal(new[] { 1 }, shell.Animation.SelectedBoneIndices);

            var window = new EditorWindow(false) { DataContext = shell };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                shell.SceneSelectionIndex = -1;
                shell.CurrentFrame = 12;
                shell.Animation.SelectMorph(0);
                shell.Animation.MorphWeight = 0.6m;
                var timeline = window.FindControl<EditorTimeline>("FrameTimeline")!;
                timeline.Focus();
                Dispatcher.UIThread.RunJobs();
                var shortcut = typeof(EditorWindow).GetMethod("ExecuteEditorKey", BindingFlags.NonPublic | BindingFlags.Instance)!;
                Assert.True((bool)shortcut.Invoke(window, [Key.K, KeyModifiers.None, true])!);
                Dispatcher.UIThread.RunJobs();
                await Task.Yield();
                Dispatcher.UIThread.RunJobs();

                var motion = Assert.Single(shell.Workspace.Motions).Motion;
                Assert.Empty(motion.BoneKeyframes);
                var firstMorph = Assert.Single(motion.MorphKeyframes);
                Assert.Equal(12u, firstMorph.Frame);
                Assert.Equal(0.6f, firstMorph.Weight, 3);

                shell.CurrentFrame = 15;
                shell.Animation.MorphWeight = 0.8m;
                Dispatcher.UIThread.RunJobs();
                Assert.True((bool)shortcut.Invoke(window, [Key.Enter, KeyModifiers.None, true])!);
                Dispatcher.UIThread.RunJobs();
                await Task.Yield();
                Dispatcher.UIThread.RunJobs();

                motion = Assert.Single(shell.Workspace.Motions).Motion;
                Assert.Empty(motion.BoneKeyframes);
                Assert.Equal(new uint[] { 12u, 15u }, motion.MorphKeyframes.Select(k => k.Frame).Order().ToArray());
                return true;
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }, Token);

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mda-project-route-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(directory);
        internal string DirectoryPath => directory;
        internal string Path(string name) => System.IO.Path.Combine(directory, name);
        internal Guid AddModel(EditorProjectWorkspace workspace, PmxModel? model = null)
        {
            string source = Path("model.pmx"); model ??= EditorRecoveryPmaIntegrationTests.Model();
            File.WriteAllBytes(source, PmxWriter.ToBytes(model));
            return workspace.AddModel(source, EditorProjectWorkspace.CreateModelAsset(model, source, Token), ModelRole.Prop);
        }
        internal EditorClassicRenderEngine ProductionEngine()
        {
            string native = Path("Mmd.Modern.D3D9.Compatibility.dll"), d3dx = Path("d3dx9_43.dll"), compiler = Path("d3dcompiler_43.dll");
            foreach (string file in new[] { native, d3dx, compiler }) File.WriteAllBytes(file, [1]);
            return new(new(native, d3dx, compiler), directory);
        }

        internal EditorProjectWorkspace Workspace()
        {
            string native = Path("Mmd.Modern.D3D9.Compatibility.dll"), d3dx = Path("d3dx9_43.dll"), compiler = Path("d3dcompiler_43.dll");
            File.WriteAllText(native, "fixture"); File.WriteAllText(d3dx, "fixture"); File.WriteAllText(compiler, "fixture");
            var engine = new EditorCompatibilityRenderEngine(new(native, d3dx, compiler), D3D9SurfaceIsolation.NoConfiguredMmeOrEmm);
            return new(engine) { PackageCacheDirectory = Path("classic-cache") };
        }
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    public async ValueTask DisposeAsync() => await Task.Run(dispatcher.Dispose).ConfigureAwait(false);
}
