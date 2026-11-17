using System.Numerics;
using System.Text;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed class ClassicObjectEditPreparationTests : IDisposable
{
    private readonly string directory = Path.Combine(MdaWorkRoot.Path("compat/20261110/rebind596/tests"), Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void RestoredDuplicateSurvivorKeepsItsRowAndFrozenBytesWhileOnlyAddedModelIsDiscovered()
    {
        Directory.CreateDirectory(directory);
        string originals = Path.Combine(directory, "originals"); Directory.CreateDirectory(originals);
        var asset = Asset(); string modelPath = Path.Combine(originals, "actor.pmx");
        File.WriteAllBytes(modelPath, PmxWriter.ToBytes(asset.Model));
        string oldFx = Path.Combine(originals, "actor.fx"); File.WriteAllText(oldFx, "retained fx bytes");
        var composition = new SceneCompositionEditor();
        var deleted = composition.Add("deleted", modelPath, ModelRole.Unspecified, asset.Source);
        var survivor = composition.Add("survivor", modelPath, ModelRole.Unspecified, asset.Source);
        var noEffect = composition.Add("null-choice", modelPath, ModelRole.Unspecified, asset.Source);
        using var project = new SceneProjectSession(composition, new(), [], new NoPhysics(), cancellationToken: Token);
        var document = EmmReader.Read(Encoding.ASCII.GetBytes("[Info]\nVersion=3\n[Object]\nPmd0=actor.pmx\nPmd1=actor.pmx\nPmd2=actor.pmx\n[Effect]\nDefault=none\n[Effect@Layer]\nOwner=Pmd1[0]\nPmd1[0]=none\nPmd1[0].show=false\n"), new() { AnsiCodePage = 936 });
        var plan = EmmImportPlan.Create(document, Path.Combine(originals, "project.emm"),
            project.Objects.Select((value, index) => new EmmObjectBinding(value.Id, true, modelPath, index)), originals);
        var captured = D3D9ClassicEffectsPreparation.Capture(plan,
            request => request.Object.ObjectId == noEffect.Id ? null : oldFx, new Inspector(), cancellationToken: Token).Preparation!;
        string initialPath = Path.Combine(directory, "initial.pmc");
        D3D9ClassicProjectStore.Save(initialPath, project, captured.CreateSaveOptions(project, cancellationToken: Token), Token);
        Directory.Move(originals, originals + ".moved");
        var restored = Load(initialPath);
        using (restored.Package)
        {
            var retained = D3D9ClassicEffectsPreparation.Recover(restored);
            var beforeBindings = retained.Emm.ImportPlan.LiveObjects.ToDictionary(value => value.ObjectId);
            var beforeIds = restored.Package.Session.Objects.Select(value => value.Id).ToArray();
            bool couldUndo = restored.Package.Session.CanUndo, couldRedo = restored.Package.Session.CanRedo;
            Assert.Throws<ArgumentException>(() => restored.Package.Session.Edit(edit =>
            {
                edit.RemoveObject(survivor.Id);
                retained.PrepareObjectEdit(edit.Composition.Objects.Select(value => beforeBindings[value.Id]),
                    new HashSet<Guid>(), _ => throw new InvalidOperationException("Retained objects must not rediscover effects."),
                    new Inspector(), materialCounts: edit.Composition.Objects.ToDictionary(value => value.Id, _ => 1),
                    cancellationToken: Token);
            }, Token));
            Assert.Equal(beforeIds, restored.Package.Session.Objects.Select(value => value.Id));
            Assert.Equal(couldUndo, restored.Package.Session.CanUndo); Assert.Equal(couldRedo, restored.Package.Session.CanRedo);
            string newSources = Path.Combine(directory, "new-sources"); Directory.CreateDirectory(newSources);
            string addedPath = Path.Combine(newSources, "added.pmx"); File.WriteAllBytes(addedPath, PmxWriter.ToBytes(asset.Model));
            string addedFx = Path.Combine(newSources, "added.fx"); File.WriteAllText(addedFx, "new fx bytes");
            var inspector = new Inspector(); int discoveries = 0; Guid addedId = Guid.Empty;
            D3D9ClassicEffectsPreparation? prepared = null;
            restored.Package.Session.Edit(edit =>
            {
                edit.RemoveObject(deleted.Id);
                addedId = edit.Composition.Add("added", addedPath, ModelRole.Unspecified, asset.Source).Id;
                var live = edit.Composition.Objects.Select(value => beforeBindings.TryGetValue(value.Id, out var old)
                    ? old : new EmmObjectBinding(value.Id, true, value.SourcePath, 3));
                var result = retained.PrepareObjectEdit(live, new HashSet<Guid> { addedId }, request =>
                {
                    discoveries++; Assert.Equal(addedId, request.Object.ObjectId);
                    Assert.Equal(Path.GetFullPath(addedPath), request.OriginAbsoluteObjectPath); return addedFx;
                }, inspector, materialCounts: edit.Composition.Objects.ToDictionary(value => value.Id, _ => 1), cancellationToken: Token);
                prepared = Assert.IsType<D3D9ClassicEffectsPreparation>(result.Preparation);
            }, Token);
            Assert.Equal(1, discoveries); Assert.Equal(1, inspector.Calls);
            Assert.Equal("Pmd1", prepared!.Emm.ImportPlan.Matches.Single(value => value.ObjectId == survivor.Id).RecordKey);
            Assert.Null(prepared.FrozenEffects.RootSeeds.Single(value => value.ObjectId == noEffect.Id).OriginAbsoluteFxPath);
            Assert.False(Directory.Exists(originals));
            string editedPath = Path.Combine(directory, "edited.pmc");
            var options = prepared.CreateSaveOptions(restored.Package.Session, cancellationToken: Token);
            Assert.Equal(2, options.OptionalExtensions[D3D9EmmPma.ExtensionName].GetProperty("schemaVersion").GetInt32());
            D3D9ClassicProjectStore.Save(editedPath, restored.Package.Session, options, Token);
            Directory.Move(newSources, newSources + ".moved");
            var reopened = Load(editedPath);
            using (reopened.Package)
            {
                var final = D3D9ClassicEffectsPreparation.Recover(reopened);
                Assert.Equal(prepared.Emm.ImportPlan.Matches, final.Emm.ImportPlan.Matches);
                Assert.Equal(new[] { survivor.Id, noEffect.Id, addedId }, final.Emm.ImportPlan.LiveObjects.Select(value => value.ObjectId));
                Assert.Equal(document.CopySourceBytes(), final.Emm.Document.CopySourceBytes());
                Assert.Equal("Pmd1", final.Emm.Document.EffectSections.Single(value => !value.IsDefault).Owner!.ObjectKey);
                Assert.Equal(0, final.Emm.Document.EffectSections.Single(value => !value.IsDefault).Owner!.Subset);
                var oldNode = final.Tree!.DependencyGraph!.Nodes.Single(value => value.OriginAbsolutePath == Path.GetFullPath(oldFx));
                Assert.Equal(Encoding.UTF8.GetBytes("retained fx bytes"), final.Tree.CopyFileBytes(oldNode.RelativePath));
                Assert.Null(final.FrozenEffects.RootSeeds.Single(value => value.ObjectId == noEffect.Id).OriginAbsoluteFxPath);
                Assert.Equal(2, final.FrozenEffects.Roots.Count);
                Assert.DoesNotContain(final.FrozenEffects.RootSeeds, value => value.ObjectId == deleted.Id);
                Assert.False(Directory.Exists(originals)); Assert.False(Directory.Exists(newSources));
            }
        }
    }

    private D3D9ClassicPreparedProject Load(string path)
    {
        string cache = Path.Combine(directory, "cache-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(cache);
        return D3D9ClassicProjectStore.LoadPrepared(path, cache, (modelPath, token) =>
        { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(modelPath); return new(model, Frame(model)); },
            new NoPhysics(), cancellationToken: Token);
    }

    private static SceneAnimationModelAsset Asset()
    {
        var model = new PmxModel
        {
            Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = "edit fixture", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0, 1, 2)],
            Materials = [new() { Name = "triangle", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
                SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
            { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
        return new(model, Frame(model));
    }

    private static OriginalFrameScene Frame(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -10), 30, 1, Eligible: false),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));

    private sealed class Inspector : ID3D9EffectDependencyInspector
    {
        internal int Calls { get; private set; }
        public D3D9EffectDependencyInspection Inspect(D3D9EffectDependencySnapshot topFx,
            Func<D3D9EffectDependencyIncludeRequest, D3D9EffectDependencySnapshot> captureInclude, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            return new(D3D9EffectDependencyStatus.Complete,
                new("fixture", new string('a', 64), 64, 936, new string('b', 64)),
                [new("effect-mip-off", 0x1000, [new("_INDEX", "PSIZE15")]),
                    new("effect-mip-on", 0x1000, [new("_INDEX", "PSIZE15"), new("MME_MIPMAP", "")])], []);
        }
    }

    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject obj) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(directory);
        if (!full.StartsWith(Path.GetFullPath(MdaWorkRoot.Path("compat/20261110/rebind596/tests/")), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its task directory.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
