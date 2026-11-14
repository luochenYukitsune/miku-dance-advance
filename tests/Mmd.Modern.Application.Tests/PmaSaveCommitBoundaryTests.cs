using System.Collections;
using System.Numerics;
using System.Reflection;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmaSaveCommitBoundaryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "pma-save-boundary-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public PmaSaveCommitBoundaryTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedFormatAndExtensionArePublicReadOnlyAndSurviveDisposal(bool classic)
    {
        using var project = Fixture(out _, out _);
        var snapshot = Capture(classic, project, Options());
        snapshot.Dispose();
        foreach (var (name, expected) in new[] { ("Format", Format(classic)), ("FileExtension", Extension(classic)) })
        {
            var property = typeof(PmaSaveSnapshot).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(property);
            Assert.Equal(typeof(string), property.PropertyType);
            Assert.Null(property.SetMethod);
            Assert.Equal(expected, property.GetValue(snapshot));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublishedPackageIsReportedAsCommittedWhenStageDeletionFails(bool classic)
    {
        using var project = Fixture(out var model, out var id);
        string path = Package("committed-stage", classic);
        using var stageLock = new LockHolder();
        var options = Override(model, id, () => LockStagedOriginal(stageLock));

        var error = Record.Exception(() => Save(classic, path, project, options, Token));

        AssertCommitted(error, path);
        Assert.IsAssignableFrom<IOException>(error!.InnerException);
        Assert.True(File.Exists(path));
        using var loaded = Load(classic, path);
        Assert.Equal(Format(classic), loaded.Format);
        Assert.Equal(project.Objects.Select(o => o.Id), loaded.Session.Objects.Select(o => o.Id));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.NotNull(stageLock.Path);
        Assert.True(File.Exists(stageLock.Path));
        stageLock.Dispose();
        DeleteSaveStages();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void StageCleanupPreservesPrimaryFailureOrCancellationAndExistingPackage(bool classic, bool cancel)
    {
        using var project = Fixture(out var model, out var id);
        string path = Package("safe-stage", classic);
        Save(classic, path, project, Options(), Token);
        byte[] previous = File.ReadAllBytes(path);
        using var stageLock = new LockHolder();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var primary = new IOException("original model callback failure");
        var options = Override(model, id, () =>
        {
            LockStagedOriginal(stageLock);
            if (cancel) cancellation.Cancel();
            else throw primary;
        });

        var error = Record.Exception(() => Save(classic, path, project, options, cancellation.Token));

        if (cancel) Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error).CancellationToken);
        else Assert.Same(primary, error);
        AssertCleanupAttached(error!);
        Assert.Equal(previous, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        stageLock.Dispose();
        DeleteSaveStages();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPublicationDeletesCompletedTemporaryArchiveEvenWhenStageCleanupAlsoFails(bool classic)
    {
        using var project = Fixture(out var model, out var id);
        string path = Package("locked-destination", classic);
        Save(classic, path, project, Options(), Token);
        byte[] previous = File.ReadAllBytes(path);
        using var destinationLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var stageLock = new LockHolder();
        var options = Override(model, id, () => LockStagedOriginal(stageLock));

        var error = Record.Exception(() => Save(classic, path, project, options, Token));

        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.NotEqual("Mmd.Modern.Application.PmaSaveCommittedException", error!.GetType().FullName);
        AssertCleanupAttached(error);
        Assert.Equal(previous, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        stageLock.Dispose();
        DeleteSaveStages();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedCapturePreservesItsPrimaryErrorWhenLastResourceLeaseCleanupFails(bool classic)
    {
        using var project = Fixture(out _, out _);
        string source = Package("capture-source", classic);
        Save(classic, source, project, Options(), Token);
        var loaded = Load(classic, source);
        string extracted = loaded.CacheDirectory;
        var actor = Assert.Single(loaded.Session.Objects);
        var model = Assert.Single(loaded.Session.CaptureAuthoredContent().Motions).Model;
        using var resourceLock = new LockHolder();
        var primary = new IOException("original capture failure");
        var options = Options() with
        {
            ModelOverrides = new CallbackOverrides(actor.Id, model, () =>
            {
                resourceLock.Open(Path.Combine(extracted, Assert.Single(loaded.Models).OriginalSourcePath));
                loaded.Dispose();
                throw primary;
            }),
        };
        try
        {
            var error = Record.Exception(() => Capture(classic, loaded.Session, options));
            Assert.Same(primary, error);
            AssertCleanupAttached(error!);
            Assert.Empty(Directory.GetDirectories(Options().CacheDirectory));
        }
        finally
        {
            resourceLock.Dispose();
            loaded.Dispose();
            if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ActiveSnapshotOrFacadeLeaseCleanupPreservesCommitAndPrimaryFailure(bool classic, bool facade, bool fail)
    {
        using var project = Fixture(out _, out _);
        string source = Package("lease-source", classic);
        Save(classic, source, project, Options(), Token);
        var loaded = Load(classic, source);
        string extracted = loaded.CacheDirectory;
        var actor = Assert.Single(loaded.Session.Objects);
        var model = Assert.Single(loaded.Session.CaptureAuthoredContent().Motions).Model;
        using var resourceLock = new LockHolder();
        PmaSaveSnapshot? snapshot = null;
        var primary = new IOException("original save failure before publication");
        var options = Override(model, actor.Id, () =>
        {
            resourceLock.Open(Path.Combine(extracted, Assert.Single(loaded.Models).OriginalSourcePath));
            // This callback runs inside an active Save. Owner disposal leaves the save's lease
            // as the final owner; explicit snapshot disposal defers that release until Save exits.
            loaded.Dispose();
            if (!facade) snapshot!.Dispose();
            if (fail) throw primary;
        });
        if (!facade) snapshot = Capture(classic, loaded.Session, options);
        string destination = Package("lease-result", classic);
        try
        {
            var error = Record.Exception(() =>
            {
                if (facade) Save(classic, destination, loaded.Session, options, Token);
                else snapshot!.Save(destination, Token);
            });

            if (fail)
            {
                Assert.Same(primary, error);
                AssertCleanupAttached(error!);
                Assert.False(File.Exists(destination));
            }
            else
            {
                AssertCommitted(error, destination);
                Assert.IsAssignableFrom<IOException>(error!.InnerException);
                using var reopened = Load(classic, destination);
                Assert.Equal(Format(classic), reopened.Format);
            }
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            Assert.Empty(Directory.GetDirectories(Options().CacheDirectory));
            Assert.True(Directory.Exists(extracted));
        }
        finally
        {
            resourceLock.Dispose();
            snapshot?.Dispose();
            loaded.Dispose();
            if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        }
    }

    private void LockStagedOriginal(LockHolder holder)
    {
        string stage = Assert.Single(Directory.GetDirectories(Options().CacheDirectory));
        string original = Assert.Single(Directory.GetFiles(Path.Combine(stage, "assets", "models"), "model.pmx", SearchOption.AllDirectories));
        holder.Open(original);
    }
    private void DeleteSaveStages()
    {
        foreach (string stage in Directory.GetDirectories(Options().CacheDirectory)) Directory.Delete(stage, recursive: true);
    }
    private PmaSaveOptions Override(PmxModel model, Guid id, Action callback) => Options() with
    {
        ModelOverrides = new Dictionary<Guid, PmxModel> { [id] = Copy(model, new CallbackTextures(model.Textures, callback)) },
    };
    private static void AssertCleanupAttached(Exception error)
        => Assert.Contains(error.Data.Values.Cast<object>(), value => value is IOException);
    private static void AssertCommitted(Exception? error, string destination)
    {
        Assert.NotNull(error);
        Type type = error.GetType();
        Assert.Equal("Mmd.Modern.Application.PmaSaveCommittedException", type.FullName);
        Assert.True(type.IsPublic);
        Assert.True(type.IsSealed);
        Assert.Equal(typeof(IOException), type.BaseType);
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        var property = type.GetProperty("DestinationPath", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        Assert.Equal(typeof(string), property.PropertyType);
        Assert.Null(property.SetMethod);
        Assert.Equal(Path.GetFullPath(destination), property.GetValue(error));
    }
    private PmaSaveOptions Options() => new() { CacheDirectory = Path.Combine(directory, "save-cache") };
    private string Package(string name, bool classic) => Path.Combine(directory, name + Extension(classic));
    private static string Format(bool classic) => classic ? "mmd-classic-pmc" : "mmd-modern-pma";
    private static string Extension(bool classic) => classic ? ".pmc" : ".pma";
    private static PmaSaveSnapshot Capture(bool classic, SceneProjectSession project, PmaSaveOptions options)
        => classic ? PmcProjectStore.CaptureSnapshot(project, options, Token) : PmaProjectStore.CaptureSnapshot(project, options, Token);
    private static void Save(bool classic, string path, SceneProjectSession project, PmaSaveOptions options, CancellationToken token)
    {
        if (classic) PmcProjectStore.Save(path, project, options, token);
        else PmaProjectStore.Save(path, project, options, token);
    }
    private PmaLoadedProject Load(bool classic, string path)
        => classic ? PmcProjectStore.Load(path, Path.Combine(directory, "load-cache"), LoadModel, new NoPhysics(), cancellationToken: Token)
            : PmaProjectStore.Load(path, Path.Combine(directory, "load-cache"), LoadModel, new NoPhysics(), cancellationToken: Token);
    private SceneProjectSession Fixture(out PmxModel model, out Guid id)
    {
        var template = PmmMotionAdapterTests.Pmx(["root"]);
        model = new()
        {
            Header = template.Header, Name = "actor", EnglishName = "", Comment = "original", EnglishComment = "",
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex
            { Position = p, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Faces = [new(0, 1, 2)], Textures = ["texture.png"], Materials = [new() { Name = "mat", EnglishName = "", Diffuse = Vector4.One,
                Specular = Vector3.Zero, SpecularPower = 1, Ambient = Vector3.Zero, EdgeColor = Vector4.Zero, EdgeSize = 1, DrawMode = PmxDrawModeFlags.None,
                TextureIndex = 0, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate,
                ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = template.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [],
        };
        string source = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(source, PmxWriter.ToBytes(model));
        File.WriteAllBytes(Path.Combine(directory, "texture.png"), [1, 2, 3, 4]);
        var layout = new SceneCompositionEditor(); var actor = layout.Add("actor", source, ModelRole.Character, Rest(model)); id = actor.Id;
        var motion = SceneMotionAsset.FromMotion(new() { BoneKeyframes = [new() { BoneName = "root" }] });
        return new(layout, new(), [new(actor.Id, model, motion.Motion, PhysicsEnabled: false, Asset: motion)], new NoPhysics(), cancellationToken: Token);
    }
    private static PmxModel Copy(PmxModel model, IReadOnlyList<string> textures) => new()
    { Header = model.Header, Name = model.Name, EnglishName = model.EnglishName, Comment = "save override", EnglishComment = model.EnglishComment,
        Vertices = model.Vertices, Faces = model.Faces, Textures = textures, Materials = model.Materials, Bones = model.Bones, Morphs = model.Morphs,
        DisplayFrames = model.DisplayFrames, RigidBodies = model.RigidBodies, Joints = model.Joints, SoftBodies = model.SoftBodies };
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
        new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static SceneAnimationModelAsset LoadModel(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); using var input = File.OpenRead(path); var model = PmxReader.Read(input); return new(model, Rest(model)); }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject actor) => throw new InvalidOperationException("Save must not construct physics.");
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException("Save must not construct physics.");
    }
    private sealed class CallbackTextures(IReadOnlyList<string> source, Action callback) : IReadOnlyList<string>
    {
        private bool invoked;
        public int Count => source.Count;
        public string this[int index] => source[index];
        public IEnumerator<string> GetEnumerator()
        { if (!invoked) { invoked = true; callback(); } return source.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class CallbackOverrides(Guid id, PmxModel model, Action callback) : IReadOnlyDictionary<Guid, PmxModel>
    {
        public int Count => 1;
        public IEnumerable<Guid> Keys => [id];
        public IEnumerable<PmxModel> Values => [model];
        public PmxModel this[Guid key] => key == id ? model : throw new KeyNotFoundException();
        public bool ContainsKey(Guid key) => key == id;
        public bool TryGetValue(Guid key, out PmxModel value) { value = model; return key == id; }
        public IEnumerator<KeyValuePair<Guid, PmxModel>> GetEnumerator()
        { callback(); return new[] { new KeyValuePair<Guid, PmxModel>(id, model) }.AsEnumerable().GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class LockHolder : IDisposable
    {
        public string? Path { get; private set; }
        private FileStream? stream;
        public void Open(string path) { Path = path; stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        public void Dispose() { stream?.Dispose(); stream = null; }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
