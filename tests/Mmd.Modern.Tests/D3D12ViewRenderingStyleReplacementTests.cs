using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12ViewRenderingStyleReplacementTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string directory = Path.Combine(
        Environment.GetEnvironmentVariable("MMD_STYLE_PRESET_TEST_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "style-preset-tests"), Guid.NewGuid().ToString("N"));

    [Fact]
    public void InspectionIncludesMissingDefaultAndHiddenOverridesWithoutChangingTheProject()
    {
        using var project = Project(out var ids, out var physics);
        project.EditRenderingStyles(edit =>
        {
            edit.DefaultStyle = new("missing-default");
            edit.ModelOverrides[ids[0]] = RenderingStyleId.Mmd;
            edit.ModelOverrides[ids[1]] = new("missing-hidden");
        }, Token);
        var styles = project.RenderingStyles; var frame = project.Frame;
        int creates = physics.Creates;

        var inspection = D3D12ViewRenderingStyleReplacements.Inspect(project, TestStylePlugins.AllPresetCatalog);
        var plan = D3D12ViewRenderingStyleReplacements.Prepare(project, TestStylePlugins.AllPresetCatalog,
            Map(("missing-default", "mmd")));

        Assert.Equal(new[] { "missing-default", "missing-hidden" }, inspection.MissingStyles.Select(item => item.Preset.StableId));
        var missingDefault = inspection.MissingStyles[0];
        Assert.True(missingDefault.IsDefault);
        Assert.Empty(missingDefault.ExplicitObjectIds);
        Assert.Equal(new[] { ids[2] }, missingDefault.AffectedObjectIds);
        var hidden = inspection.MissingStyles[1];
        Assert.False(hidden.IsDefault);
        Assert.Equal(new[] { ids[1] }, hidden.ExplicitObjectIds);
        Assert.Equal(new[] { ids[1] }, hidden.AffectedObjectIds);
        Assert.False(project.Actors[1].Visible);
        Assert.All(inspection.MissingStyles, item => Assert.Equal(D3D12StylePresetStatus.Missing, item.Resolution.Status));
        Assert.Equal(new[] { "mmd", "genshin", "star-rail", "wuthering-waves", "endfield" }, inspection.AvailableStyles.Select(item => item.StableId));
        Assert.False(plan.CanConfirm);
        Assert.Equal("missing-hidden", Assert.Single(plan.UnresolvedStyles).Preset.StableId);
        Assert.Throws<InvalidOperationException>(() => plan.Confirm(project, Token));
        Assert.Same(styles, project.RenderingStyles); Assert.Same(frame, project.Frame);
        Assert.Equal(10, project.CurrentFrame); Assert.Equal(creates, physics.Creates);
    }

    [Fact]
    public void FullyOverriddenMissingDefaultStillRequiresAnExplicitReplacement()
    {
        using var project = Project(out var ids, out _);
        project.EditRenderingStyles(edit =>
        {
            edit.DefaultStyle = new("missing-default");
            foreach (var id in ids) edit.ModelOverrides[id] = RenderingStyleId.Mmd;
        }, Token);
        var missing = Assert.Single(D3D12ViewRenderingStyleReplacements.Inspect(project,
            D3D12StylePresetCatalog.BuiltIn).MissingStyles);
        Assert.True(missing.IsDefault); Assert.Empty(missing.AffectedObjectIds);
        Assert.False(D3D12ViewRenderingStyleReplacements.Prepare(project, D3D12StylePresetCatalog.BuiltIn, Map()).CanConfirm);
        var plan = D3D12ViewRenderingStyleReplacements.Prepare(project, D3D12StylePresetCatalog.BuiltIn,
            Map(("missing-default", "mmd")));
        plan.Confirm(project, Token);
        Assert.Equal("mmd", project.RenderingStyles.DefaultStyle.StableId);
        Assert.Equal(3, project.RenderingStyles.ModelOverrides.Count);
        project.Undo(Token);
        Assert.Equal("missing-default", project.RenderingStyles.DefaultStyle.StableId);
    }

    [Fact]
    public void AllOrdinaryPipelinesRemainAvailableWhileUnsupportedAndMissingPresetsRequireExplicitReplacement()
    {
        using var project = Project(out var ids, out _);
        var store = new D3D12StylePresetStore(directory);
        var unsupported = store.SaveAsNew("Future pipeline", "future-surface", null, null);
        var catalog = store.Load(TestStylePlugins.AllViewCatalog.Registrations);
        project.EditRenderingStyles(edit =>
        {
            edit.DefaultStyle = RenderingStyleId.Genshin;
            edit.ModelOverrides[ids[0]] = RenderingStyleId.StarRail;
            edit.ModelOverrides[ids[1]] = new(unsupported.StableId);
            edit.ModelOverrides[ids[2]] = new("custom-missing");
        }, Token);
        var before = project.RenderingStyles;
        var inspection = D3D12ViewRenderingStyleReplacements.Inspect(project, catalog);
        Assert.Equal(new[] { unsupported.StableId, "custom-missing" },
            inspection.MissingStyles.Select(item => item.Preset.StableId));
        Assert.Equal(D3D12StylePresetStatus.UnsupportedPipeline, inspection.MissingStyles[0].Resolution.Status);
        Assert.Equal(D3D12StylePresetStatus.Missing, inspection.MissingStyles[1].Resolution.Status);
        Assert.All(inspection.AvailableStyles, item => Assert.True(item.IsAvailable));
        Assert.Equal(5, inspection.AvailableStyles.Count);
        Assert.Equal(new[] { ids[1] }, inspection.MissingStyles[0].AffectedObjectIds);
        var genshin = inspection.References.Single(item => item.Preset.StableId == "genshin");
        Assert.True(genshin.Resolution.IsAvailable);
        Assert.True(genshin.IsDefault);
        Assert.Empty(genshin.AffectedObjectIds);
        Assert.NotNull(genshin.Resolution.PluginRegistration);
        Assert.Equal("genshin", genshin.Resolution.PluginRegistration!.StyleId);
        Assert.NotNull(genshin.Resolution.DefaultStylizedShading);
        var plan = D3D12ViewRenderingStyleReplacements.Prepare(project, catalog,
            inspection.MissingStyles.Select(item => KeyValuePair.Create(item.Preset.StableId, "mmd")));
        Assert.True(plan.CanConfirm); Assert.Same(before, project.RenderingStyles);
        plan.Confirm(project, Token);
        Assert.Equal("genshin", project.RenderingStyles.DefaultStyle.StableId);
        Assert.Equal("star-rail", project.GetRenderingStyle(ids[0]).StableId);
        Assert.Equal("mmd", project.GetRenderingStyle(ids[1]).StableId);
        Assert.Equal("mmd", project.GetRenderingStyle(ids[2]).StableId);
    }

    [Theory]
    [InlineData("missing-target")]
    [InlineData("unsupported-target")]
    [InlineData("available-source")]
    [InlineData("unrelated-source")]
    [InlineData("case-sensitive-source")]
    [InlineData("chained-target")]
    [InlineData("duplicate-source")]
    public void InvalidMappingsRejectBeforeChangingAnySelectionOrUndoHistory(string variant)
    {
        using var project = Project(out _, out _);
        project.SetDefaultRenderingStyle(new("missing-a"), Token);
        var store = new D3D12StylePresetStore(directory);
        var unsupported = store.SaveAsNew("Future pipeline", "future-surface", null, null);
        var catalog = store.Load(TestStylePlugins.AllViewCatalog.Registrations);
        var before = project.RenderingStyles; var frame = project.Frame;
        var mappings = variant switch
        {
            "missing-target" => Map(("missing-a", "absent")),
            "unsupported-target" => Map(("missing-a", unsupported.StableId)),
            "available-source" => Map(("mmd", "mmd")),
            "unrelated-source" => Map(("missing-unrelated", "mmd")),
            "case-sensitive-source" => Map(("MISSING-a", "mmd")),
            "chained-target" => Map(("missing-a", "missing-b"), ("missing-b", "mmd")),
            "duplicate-source" => Map(("missing-a", "mmd"), ("missing-a", "mmd")),
            _ => throw new InvalidOperationException(),
        };
        Assert.ThrowsAny<ArgumentException>(() => D3D12ViewRenderingStyleReplacements.Prepare(project,
            catalog, mappings));
        Assert.Same(before, project.RenderingStyles); Assert.Same(frame, project.Frame);
        Assert.True(project.Undo(Token)); Assert.Equal("mmd", project.RenderingStyles.DefaultStyle.StableId);
        Assert.False(project.CanUndo);
    }

    [Fact]
    public void DetachedPlansAndReportsCannotBeEditedAndCancellationPreservesRedo()
    {
        using var project = Project(out _, out _);
        project.SetDefaultRenderingStyle(new("missing-a"), Token);
        project.SetDefaultRenderingStyle(new("missing-b"), Token); project.Undo(Token);
        var mappings = new Dictionary<string, string> { ["missing-a"] = "mmd" };
        var plan = D3D12ViewRenderingStyleReplacements.Prepare(project, D3D12StylePresetCatalog.BuiltIn, mappings);
        mappings["missing-a"] = "genshin";
        Assert.Equal("mmd", plan.Replacements["missing-a"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)plan.Replacements)["missing-a"] = "genshin");
        Assert.Throws<NotSupportedException>(() => ((IList<D3D12ViewRenderingStyleReference>)plan.Inspection.MissingStyles).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<Guid>)plan.Inspection.MissingStyles[0].AffectedObjectIds).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<D3D12StylePresetResult>)plan.Inspection.AvailableStyles).Clear());
        var styles = project.RenderingStyles; var frame = project.Frame;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => plan.Confirm(project, cancellation.Token));
        Assert.Same(styles, project.RenderingStyles); Assert.Same(frame, project.Frame);
        Assert.True(project.CanRedo);
        project.Redo(Token); Assert.Equal("missing-b", project.RenderingStyles.DefaultStyle.StableId);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("explicit-inheritance")]
    [InlineData("role")]
    [InlineData("identity")]
    public void ChangedSelectionIdentityOrRoleMakesThePlanStale(string variant)
    {
        using var project = Project(out var ids, out _);
        project.SetDefaultRenderingStyle(new("missing-a"), Token);
        var plan = D3D12ViewRenderingStyleReplacements.Prepare(project, D3D12StylePresetCatalog.BuiltIn,
            Map(("missing-a", "mmd")));
        if (variant == "default") project.SetDefaultRenderingStyle(new("missing-b"), Token);
        if (variant == "explicit-inheritance") project.SetModelRenderingStyle(ids[0], new("missing-a"), Token);
        if (variant == "role") project.Edit(edit => edit.Lighting.SetModelRole(project.Objects[0].Key, ModelRole.Environment), Token);
        if (variant == "identity")
        {
            var old = project.Objects[2];
            project.Edit(edit => { edit.RemoveObject(old.Id); edit.Composition.Add(old.Name, old.SourcePath, old.Role, old.Source); }, Token);
        }
        var styles = project.RenderingStyles; var frame = project.Frame;
        Assert.Throws<InvalidOperationException>(() => plan.Confirm(project, Token));
        Assert.Same(styles, project.RenderingStyles); Assert.Same(frame, project.Frame);
    }

    [Fact]
    public void ConfirmUsesOneMetadataTransactionAndUndoPreservesTheAdvancedClockAndSolver()
    {
        using var project = Project(out var ids, out var physics);
        var store = new D3D12StylePresetStore(Path.Combine(directory, "presets"));
        var custom = store.SaveAsNew("Replacement", "mmd", new(.4f), new(.6f, .1f));
        var catalog = store.Load(TestStylePlugins.AllViewCatalog.Registrations);
        project.EditRenderingStyles(edit =>
        {
            edit.DefaultStyle = new("missing-a");
            edit.ModelOverrides[ids[0]] = new("missing-a");
            edit.ModelOverrides[ids[1]] = new("missing-b");
        }, Token);
        var before = project.RenderingStyles;
        var plan = D3D12ViewRenderingStyleReplacements.Prepare(project, catalog,
            Map(("missing-a", custom.StableId), ("missing-b", "mmd")));
        File.Delete(Path.Combine(directory, "presets", custom.StableId + ".json"));
        project.Advance(.1, Token);
        var frame = project.Frame; var actors = project.Actors; double clock = project.CurrentFrame;
        int creates = physics.Creates, disposals = physics.Disposals, steps = physics.Steps;

        plan.Confirm(project, Token);

        Assert.Equal(custom.StableId, project.RenderingStyles.DefaultStyle.StableId);
        Assert.Equal(custom.StableId, project.RenderingStyles.ModelOverrides[ids[0]].StableId);
        Assert.Equal("mmd", project.RenderingStyles.ModelOverrides[ids[1]].StableId);
        Assert.False(project.RenderingStyles.ModelOverrides.ContainsKey(ids[2]));
        Assert.Equal(custom.StableId, project.GetRenderingStyle(ids[2]).StableId);
        Assert.Same(frame, project.Frame); Assert.Same(actors, project.Actors); Assert.Equal(clock, project.CurrentFrame);
        Assert.Equal(creates, physics.Creates); Assert.Equal(disposals, physics.Disposals); Assert.Equal(steps, physics.Steps);
        Assert.True(project.Undo(Token));
        Assert.Equal(before.DefaultStyle, project.RenderingStyles.DefaultStyle);
        Assert.Equal(before.ModelOverrides.OrderBy(p => p.Key), project.RenderingStyles.ModelOverrides.OrderBy(p => p.Key));
        Assert.Same(frame, project.Frame); Assert.Same(actors, project.Actors); Assert.Equal(clock, project.CurrentFrame);
        Assert.Equal(creates, physics.Creates); Assert.Equal(disposals, physics.Disposals); Assert.Equal(steps, physics.Steps);
        Assert.True(project.Redo(Token)); Assert.Equal(custom.StableId, project.RenderingStyles.DefaultStyle.StableId);
    }

    [Fact]
    public void MissingPmaLoadsAndConfirmedReplacementPersistsOnlyOnTheNextExplicitSave()
    {
        using var project = Project(out var ids, out _);
        project.EditRenderingStyles(edit =>
        {
            edit.DefaultStyle = new("missing-a");
            edit.ModelOverrides[ids[0]] = new("missing-a");
            edit.ModelOverrides[ids[1]] = RenderingStyleId.Mmd;
        }, Token);
        string original = Save(project, "missing.pma"); byte[] originalBytes = File.ReadAllBytes(original);
        var store = new D3D12StylePresetStore(Path.Combine(directory, "presets"));
        var target = store.SaveAsNew("Installed replacement", "mmd", new(.4f), new(.5f, .1f));
        var catalog = store.Load(TestStylePlugins.AllViewCatalog.Registrations);
        using var loaded = Load(original);
        Assert.Equal("missing-a", loaded.Session.RenderingStyles.DefaultStyle.StableId);
        var plan = D3D12ViewRenderingStyleReplacements.Prepare(loaded.Session, catalog, Map(("missing-a", target.StableId)));
        Assert.Throws<InvalidOperationException>(() => plan.Confirm(project, Token));
        Assert.Equal("missing-a", project.RenderingStyles.DefaultStyle.StableId);
        plan.Confirm(loaded.Session, Token);
        Assert.Equal(originalBytes, File.ReadAllBytes(original));
        string saved = Save(loaded.Session, "confirmed.pma");
        using (var zip = ZipFile.OpenRead(saved))
        using (var input = zip.GetEntry("manifest.json")!.Open())
        using (var document = JsonDocument.Parse(input))
        {
            var styles = document.RootElement.GetProperty("optionalExtensions").GetProperty(SceneRenderingStyles.ExtensionName);
            Assert.Equal(new[] { "version", "defaultStyle", "modelOverrides" }, styles.EnumerateObject().Select(item => item.Name));
            Assert.Equal(target.StableId, styles.GetProperty("defaultStyle").GetString());
            Assert.Equal(target.StableId, styles.GetProperty("modelOverrides").GetProperty(ids[0].ToString("D")).GetString());
            Assert.DoesNotContain("Installed replacement", styles.GetRawText());
            Assert.DoesNotContain("characterOutlineStyle", styles.GetRawText());
            Assert.DoesNotContain("characterToonResponse", styles.GetRawText());
            Assert.All(zip.Entries, entry => Assert.False(entry.FullName.EndsWith(target.StableId + ".json", StringComparison.Ordinal)));
        }
        using var reopened = Load(saved);
        Assert.Equal(target.StableId, reopened.Session.GetRenderingStyle(ids[0]).StableId);
        Assert.Equal("mmd", reopened.Session.GetRenderingStyle(ids[1]).StableId);
        Assert.Equal(target.StableId, reopened.Session.GetRenderingStyle(ids[2]).StableId);
        Assert.False(reopened.Session.RenderingStyles.ModelOverrides.ContainsKey(ids[2]));
        Assert.Empty(D3D12ViewRenderingStyleReplacements.Inspect(reopened.Session, catalog).MissingStyles);
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task ConfirmedPmaExportsThroughActualD3D12AndKeepsExtractionUntilRendererDisposal()
    {
        using var project = Project(out _, out _);
        project.SetDefaultRenderingStyle(new("missing-a"), Token);
        string missing = Save(project, "gpu-missing.pma");
        var store = new D3D12StylePresetStore(Path.Combine(directory, "presets"));
        var target = store.SaveAsNew("GPU replacement", "mmd", new(.4f), new(.5f, .1f));
        var catalog = store.Load(TestStylePlugins.AllViewCatalog.Registrations);
        string saved;
        using (var loaded = Load(missing))
        {
            var plan = D3D12ViewRenderingStyleReplacements.Prepare(loaded.Session, catalog, Map(("missing-a", target.StableId)));
            plan.Confirm(loaded.Session, Token);
            saved = Save(loaded.Session, "gpu-confirmed.pma");
        }
        string? extraction = null; string[] sources = [];
        ResourceCheckingRenderer? renderer = null;
        var sink = new MemorySink(() => Assert.All(sources, source => Assert.True(File.Exists(source))));
        var coordinator = ExportCoordinator.ForPma(token =>
        {
            var owner = Load(saved); extraction = owner.CacheDirectory;
            sources = owner.Session.Objects.Select(item => item.SourcePath).ToArray();
            Assert.All(owner.Session.CaptureRenderSnapshot().RenderingStyles, item => Assert.Equal(target.StableId, item.Preset.StableId));
            return ValueTask.FromResult(owner);
        }, (request, _) =>
        {
            renderer = new(new(request, stylePresets: catalog), () =>
                Assert.All(sources, source => Assert.True(File.Exists(source))));
            return ValueTask.FromResult<IViewRenderer>(renderer);
        });
        var result = await coordinator.RunAsync(new(new(96, 72, new(30), 10, 10)), sink, cancellationToken: Token);
        Assert.Equal(1, result.CompletedFrames); Assert.Equal(ExportTaskState.Completed, coordinator.State);
        Assert.Equal(96 * 72 * 4, Assert.Single(sink.Frames).Length);
        Assert.True(sink.Completed); Assert.True(renderer!.Disposed);
        Assert.NotNull(extraction); Assert.False(Directory.Exists(extraction));
    }

    private static KeyValuePair<string, string>[] Map(params (string Source, string Target)[] pairs)
        => pairs.Select(pair => KeyValuePair.Create(pair.Source, pair.Target)).ToArray();

    private string Save(SceneProjectSession project, string name)
    {
        string path = Path.Combine(directory, name);
        PmaProjectStore.Save(path, project, new() { CacheDirectory = Path.Combine(directory, "save-cache") }, Token);
        return path;
    }

    private PmaLoadedProject Load(string path) => PmaProjectStore.Load(path, Path.Combine(directory, "load-cache"),
        LoadModel, new CountingPhysics(), cancellationToken: Token);

    private SceneProjectSession Project(out Guid[] ids, out CountingPhysics physics)
    {
        Directory.CreateDirectory(directory);
        using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
        var source = PmxReader.Read(input);
        // Reuse the format fixture's skeleton/geometry; omit external images and numerical bodies for pure setup.
        var model = new PmxModel
        {
            Header = source.Header, Name = "replacement fixture", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = source.Vertices, Faces = source.Faces, Textures = [], Bones = source.Bones,
            Materials = source.Materials.Select(item => new PmxMaterial
            {
                Name = item.Name, EnglishName = item.EnglishName, Diffuse = item.Diffuse, Specular = item.Specular,
                SpecularPower = item.SpecularPower, Ambient = item.Ambient, DrawMode = item.DrawMode,
                EdgeColor = item.EdgeColor, EdgeSize = item.EdgeSize, TextureIndex = -1, SphereTextureIndex = -1,
                SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1,
                Memo = "", FaceVertexCount = item.FaceVertexCount,
            }).ToArray(),
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [],
        };
        string path = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
        var composition = new SceneCompositionEditor();
        ids = new[] { ModelRole.Character, ModelRole.Environment, ModelRole.Prop }
            .Select(role => composition.Add("same name", path, role, Rest(model)).Id).ToArray();
        SceneActorMotion Motion(Guid id, bool visible)
        {
            var asset = SceneMotionAsset.FromMotion(new()
            {
                BoneKeyframes = [new() { BoneName = model.Bones[0].Name }, new() { BoneName = model.Bones[0].Name, Frame = 30 }],
                IkKeyframes = [new() { Frame = 0, Visible = visible, Switches = [] }],
            });
            return new(id, model, asset.Motion, PhysicsEnabled: false, Asset: asset);
        }
        physics = new();
        return new(composition, new(), ids.Select((id, index) => Motion(id, index != 1)), physics,
            new() { Physics = new() { SharedWorld = true } }, initialFrame: 10, cancellationToken: Token);
    }

    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));

    private static SceneAnimationModelAsset LoadModel(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); using var input = File.OpenRead(path); var model = PmxReader.Read(input);
        return new(model, Rest(model));
    }

    private sealed class CountingPhysics : IProjectPhysicsBackendProvider
    {
        internal int Creates, Disposals, Steps;
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
            => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings)
        { Creates++; return new EmptyBackend(() => Disposals++, () => Steps++); }
    }

    private sealed class EmptyBackend(Action dispose, Action step) : ICharacterPhysicsMutableBackend
    {
        public string Name => "replacement test";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> input) { }
        public void Step(float seconds) => step();
        public void Dispose() => dispose();
        public void SetGravity(Vector3 gravity) { }
        public void SetKinematicTarget(int index, Matrix4x4 world, float seconds) => throw new NotSupportedException();
        public Matrix4x4 GetBodyWorldTransform(int index) => throw new NotSupportedException();
        public void ApplyImpulse(int index, Vector3 linear, Vector3 angular) => throw new NotSupportedException();
        public int AddBody(PhysicsBodyDefinition body, Matrix4x4 world) => throw new NotSupportedException();
        public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 a, Matrix4x4 b) => throw new NotSupportedException();
        public void RemoveJoint(int index) => throw new NotSupportedException();
        public void RemoveBody(int index) => throw new NotSupportedException();
        public void ResetBody(int index, Matrix4x4 world) => throw new NotSupportedException();
        public void SetBodyKinematic(int index, bool state) => throw new NotSupportedException();
    }

    private sealed class ResourceCheckingRenderer(D3D12ViewRenderer inner, Action checkResources)
        : IViewRenderer, IViewRenderingStylePreflight
    {
        internal bool Disposed;
        public ViewRendererCapabilities Capabilities => inner.Capabilities;
        public void ValidateRenderingStyles(OriginalFrameScene scene, IReadOnlyList<SceneRenderingStyleBinding> bindings)
            => inner.ValidateRenderingStyles(scene, bindings);
        public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
        { checkResources(); return inner.CaptureAsync(frame, cancellationToken); }
        public async ValueTask DisposeAsync()
        {
            checkResources(); await inner.DisposeAsync(); checkResources(); Disposed = true;
        }
    }

    private sealed class MemorySink(Action checkResources) : IFrameSink
    {
        internal readonly List<byte[]> Frames = [];
        internal bool Completed;
        public ValueTask BeginAsync(ExportRequest request, CancellationToken cancellationToken = default)
        { checkResources(); return ValueTask.CompletedTask; }
        public ValueTask WriteAsync(OfflineFrameSample sample, FrameBufferLease frame, CancellationToken cancellationToken = default)
        { checkResources(); Frames.Add(frame.Pixels.ToArray()); return ValueTask.CompletedTask; }
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        { Completed = true; return ValueTask.CompletedTask; }
        public ValueTask AbortAsync(Exception? error, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        string cache = Path.GetFullPath(Environment.GetEnvironmentVariable("MMD_STYLE_PRESET_TEST_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "style-preset-tests"));
        string target = Path.GetFullPath(directory);
        if (!target.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Replacement test cleanup must stay within its test cache.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
