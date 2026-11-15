using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed class ClassicEffectsPreparationTests : IDisposable
{
    private readonly string directory = Path.Combine(Mmd.Modern.TestSupport.TestWorkspace.Root("compat/20261106/frozen560/tests"), Guid.NewGuid().ToString("N"));
    private int modelLoads;
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public ClassicEffectsPreparationTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void EmptyChoicesReplaceRetainedLegacyTreeAndSupplyRuntimeWithoutAnyInspection()
    {
        using var project = Project();
        string sources = Path.Combine(directory, "sources"); Directory.CreateDirectory(sources);
        File.WriteAllText(Path.Combine(sources, "old.fx"), "old bytes");
        var oldTree = D3D9EffectResourceTree.Capture(sources, ["old.fx"], cancellationToken: Token);
        string oldPath = Path.Combine(directory, "legacy.pmc");
        D3D9ClassicProjectStore.Save(oldPath, project, D3D9EffectResourceTree.CreateSaveOptions(project, oldTree), Token);
        var legacy = Load(oldPath);
        using (legacy.Package)
        {
            Assert.Null(legacy.FrozenEffects);
            Assert.Throws<NotSupportedException>(() => D3D9ClassicEffectsPreparation.Recover(legacy));
            var never = new Inspector { FailIfCalled = true };
            var result = D3D9ClassicEffectsPreparation.Capture(Plan(), _ => null, never, cancellationToken: Token);
            var preparation = Assert.IsType<D3D9ClassicEffectsPreparation>(result.Preparation);
            Assert.Null(preparation.Tree);
            var options = preparation.CreateSaveOptions(legacy.Package.Session, cancellationToken: Token);
            string nextPath = Path.Combine(directory, "empty.pmc");
            D3D9ClassicProjectStore.Save(nextPath, legacy.Package.Session, options, Token);
            var next = Load(nextPath);
            using (next.Package)
            {
                Assert.Null(next.Tree); Assert.NotNull(next.FrozenEffects);
                Assert.DoesNotContain(next.Package.ResourcePaths, path => path.StartsWith("render-resources/traditional/effect-tree/", StringComparison.OrdinalIgnoreCase));
                using var runtime = D3D9ClassicEffectsPreparation.Recover(next).PrepareRuntime(directory, never, cancellationToken: Token);
                Assert.All(runtime.Configuration.Objects, obj => Assert.Equal(D3D9EmmRuntimeEffectKind.None, obj.RootSeed.Kind));
                Assert.Null(runtime.Replay); Assert.Equal(0, never.Calls);
            }
        }
    }

    [Fact]
    public void RequiredMissingFxNeverExposesAPartialPreparation()
    {
        var result = D3D9ClassicEffectsPreparation.Capture(Plan(), _ => Path.Combine(directory, "missing.fx"),
            new Inspector { FailIfCalled = true }, cancellationToken: Token);
        Assert.Equal(D3D9EffectDependencyStatus.Incomplete, result.Status);
        Assert.Null(result.Preparation);
        Assert.Contains(result.Diagnostics, item => item.Code == "source-read" && item.RootIndex == 0);
    }

    [Fact]
    public void FrozenMetadataMismatchRejectsBeforeCallerPreflightOrModelLoading()
    {
        using var project = Project();
        var preparation = D3D9ClassicEffectsPreparation.Capture(Plan(), _ => null, new Inspector(), cancellationToken: Token).Preparation!;
        var options = preparation.CreateSaveOptions(project, cancellationToken: Token);
        var extensions = options.OptionalExtensions.ToDictionary(pair => pair.Key, pair => pair.Value);
        var metadata = JsonNode.Parse(extensions[D3D9FrozenEffectProject.ExtensionName].GetRawText())!.AsObject();
        metadata["schemaVersion"] = 999;
        using var json = JsonDocument.Parse(metadata.ToJsonString());
        extensions[D3D9FrozenEffectProject.ExtensionName] = json.RootElement.Clone();
        string path = Path.Combine(directory, "bad.pmc");
        D3D9ClassicProjectStore.Save(path, project, options with { OptionalExtensions = extensions }, Token);
        bool called = false;
        int before = modelLoads;
        Assert.Throws<InvalidDataException>(() => Load(path, new() { PackagePreflight = (_, _) => called = true }));
        Assert.False(called); Assert.Equal(before, modelLoads);
    }

    [Fact]
    public void PrivateReplayOwnsFilesAndCleansThemOnFailureAndCancellation()
    {
        string source = Path.Combine(directory, "main.fx"); File.WriteAllText(source, "frozen source");
        var original = new Inspector();
        var preparation = D3D9ClassicEffectsPreparation.Capture(Plan(), _ => source, original, cancellationToken: Token).Preparation!;
        File.Move(source, source + ".moved");
        string cache = Path.Combine(directory, "cache"); Directory.CreateDirectory(cache);
        using (var runtime = preparation.PrepareRuntime(cache, new Inspector(), cancellationToken: Token))
        {
            Assert.True(runtime.Replay!.Accepted);
            Assert.All(runtime.Configuration.Resources.Bindings, binding => Assert.True(File.Exists(binding.MaterializedAbsolutePath)));
            using (File.Open(runtime.Configuration.Resources.Bindings[0].MaterializedAbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.ThrowsAny<IOException>(runtime.Dispose);
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
        var error = Assert.Throws<D3D9ClassicEffectsReplayException>(() => preparation.PrepareRuntime(cache,
            new Inspector { AddMissingResource = true }, cancellationToken: Token));
        Assert.False(error.Result.Accepted); Assert.NotEmpty(error.Result.Diagnostics);
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Assert.ThrowsAny<OperationCanceledException>(() => preparation.PrepareRuntime(cache,
            new Inspector { Cancel = cancelled }, cancellationToken: cancelled.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
    }

    private D3D9ClassicPreparedProject Load(string path, PmaLoadOptions? options = null)
    {
        string cache = Path.Combine(directory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(cache);
        return D3D9ClassicProjectStore.LoadPrepared(path, cache, LoadModel,
            new NoPhysics(), options, Token);
    }
    private SceneAnimationModelAsset LoadModel(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); modelLoads++;
        var model = PmxReader.Read(path);
        return new(model, new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
            PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -10), 30, 1, Eligible: false),
            materials: PmxOriginalFrameAdapter.FromMaterials(model)));
    }
    private EmmImportPlan Plan()
    {
        var document = EmmReader.Read(Encoding.ASCII.GetBytes("[Info]\nVersion=3\n[Object]\nPmd0=actor.pmx\n[Effect]\nDefault=none\n"),
            new() { AnsiCodePage = 936 });
        return EmmImportPlan.Create(document, Path.Combine(directory, "project.emm"),
            [new EmmObjectBinding(new Guid("fe345423-1f83-4058-b026-a9d598882f72"), true, "actor.pmx", 0)], directory);
    }
    private SceneProjectSession Project()
    {
        var model = new PmxModel
        {
            Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = "frozen roots", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0, 1, 2)],
            Materials = [new() { Name = "triangle", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
                SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
            { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
        var frame = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -10), 30, 1, Eligible: false), materials: PmxOriginalFrameAdapter.FromMaterials(model));
        var composition = new SceneCompositionEditor(); composition.Add("actor", path, ModelRole.Unspecified, frame);
        return new(composition, new(), [], new NoPhysics(), cancellationToken: Token);
    }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject obj) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }
    private sealed class Inspector : ID3D9EffectDependencyInspector
    {
        public int Calls { get; private set; }
        public bool FailIfCalled { get; init; }
        public bool AddMissingResource { get; init; }
        public CancellationTokenSource? Cancel { get; init; }
        public D3D9EffectDependencyInspection Inspect(D3D9EffectDependencySnapshot topFx,
            Func<D3D9EffectDependencyIncludeRequest, D3D9EffectDependencySnapshot> captureInclude, CancellationToken cancellationToken)
        {
            Calls++; if (FailIfCalled) throw new InvalidOperationException("Inspector must not be called.");
            Cancel?.Cancel(); cancellationToken.ThrowIfCancellationRequested();
            var identity = new D3D9EffectDependencyCompilerIdentity("fixture", new string('a', 64), 64, 936, new string('b', 64));
            var profiles = new[] { new D3D9EffectDependencyProfile("effect-mip-off", 0x1000, [new("_INDEX", "PSIZE15")]),
                new D3D9EffectDependencyProfile("effect-mip-on", 0x1000, [new("_INDEX", "PSIZE15"), new("MME_MIPMAP", "")]) };
            D3D9EffectDependencySemanticReference[] references = AddMissingResource ?
                [new("effect-mip-off", D3D9EffectDependencyReferenceKind.Resource,
                    new(Encoding.ASCII.GetBytes("missing.png"), 936), "Texture", "ResourceName")] : [];
            return new(D3D9EffectDependencyStatus.Complete, identity, profiles, references);
        }
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(directory);
        if (!full.StartsWith(Path.GetFullPath((Mmd.Modern.TestSupport.TestWorkspace.Root("compat/20261106/frozen560/tests") + "/")), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
