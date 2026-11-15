using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput.D3D12;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorProjectPersistenceRoutingTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession dispatcher = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
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

    [Fact]
    public async Task ClearingTheLastMaterialMappingIsSavedAndReopensWithoutIt()
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
            Assert.Single(workspace.MaterialBindings);
            // Previously an empty mapping set skipped the codec and the opened package's index was saved again.
            workspace.MaterialBindings.Clear();
            string cleared = files.Path("cleared.pma");
            workspace.Save(cleared, Token);
            await Task.Run(() => workspace.Open(cleared, Token), Token);
            Assert.Empty(workspace.MaterialBindings);
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

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mda-project-route-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(directory);
        internal string DirectoryPath => directory;
        internal string Path(string name) => System.IO.Path.Combine(directory, name);
        internal Guid AddModel(EditorProjectWorkspace workspace)
        {
            string source = Path("model.pmx"); var model = EditorRecoveryPmaIntegrationTests.Model();
            File.WriteAllBytes(source, PmxWriter.ToBytes(model));
            return workspace.AddModel(source, EditorProjectWorkspace.CreateModelAsset(model, source, Token), ModelRole.Prop);
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
