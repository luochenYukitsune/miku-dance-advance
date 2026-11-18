using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneAnimationProjectStoreTests : IDisposable
{
    private static readonly SceneProjectSaveOptions ExternalAssets = new() { CopyModelAssets = false };
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "animation-project-" + Guid.NewGuid().ToString("N"));
    private string ProjectPath => Path.Combine(directory, "animation.mmdscene");

    public SceneAnimationProjectStoreTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void ReopenPreservesIndependentMotionsFractionalMorphsOptionsStaticBackgroundAndLights()
    {
        var model = Model(morph: true);
        var (scene, lighting, first, second) = Scene(model);
        var a = SceneMotionAsset.FromBytes(MotionBytes(30, new(3, 0, 0), morph: true), "first.vmd");
        var b = SceneMotionAsset.FromBytes(MotionBytes(60, new(0, 6, 0), morph: true), "second.vmd");
        using var session = new SceneAnimationSession(scene.Objects,
            [new(first.Id, model, a.Motion, false, true, false, a), new(second.Id, model, b.Motion, true, false, false, b)],
            NoPhysics, lighting.Snapshot, Token);
        session.Advance(1.0 / 60, Token);
        var expected = session.Frame;
        SceneAnimationProjectStore.Save(ProjectPath, scene, lighting, session, ExternalAssets, Token);

        using var reopened = SceneAnimationProjectStore.Load(ProjectPath, (_, _) => new(model, Rest(model)), NoPhysics, Token);
        Assert.Equal(.5, reopened.Animation.CurrentFrame);
        Assert.Equal(scene.Objects.Select(item => item.Id), reopened.Composition.Objects.Select(item => item.Id));
        Assert.Equal(scene.SelectedId, reopened.Composition.SelectedId);
        Assert.Equal(lighting.SelectedId, reopened.Lighting.SelectedId);
        Assert.Equal(lighting.Lights, reopened.Lighting.Lights);
        Assert.Equal(expected.Surface.Snapshot.Vertices, reopened.Animation.Frame.Surface.Snapshot.Vertices);
        Assert.Equal(expected.Outline!.Vertices, reopened.Animation.Frame.Outline!.Vertices);
        Assert.Equal(new ModelPurpose(ModelRole.Environment, true), reopened.Lighting.GetModelRole(reopened.Composition.Objects[2].Key));
        Assert.Equal(60u, reopened.Animation.DurationFrames);
        var states = reopened.Animation.Actors;
        Assert.False(states[0].IkEnabled); Assert.True(states[0].MorphsEnabled);
        Assert.True(states[1].IkEnabled); Assert.False(states[1].MorphsEnabled);
        Assert.False(states[2].HasMotion);
        // Both bone and morph channels retain the half-frame pose across project reopen.
        Near(new(.05f, 0, 1), reopened.Animation.Frame.Surface.Snapshot.Vertices[0].Position);
        Assert.False(reopened.Composition.CanUndo); Assert.False(reopened.Lighting.CanUndo);
        Assert.All(scene.Objects, item => Assert.False(item.Source.Surface.Snapshot.HasDynamicVertices));
    }

    [Fact]
    public void CapturedBytesAreCopiedOnceByHashAndSharedMotionAssetsAreDeduplicated()
    {
        var model = Model(); var (scene, lighting, first, second) = Scene(model);
        byte[] bytes = MotionBytes(30, new(3, 0, 0)); byte[] expected = bytes.ToArray();
        var asset = SceneMotionAsset.FromBytes(bytes, "dance.vmd");
        Array.Fill(bytes, (byte)0);
        Assert.Equal(expected.Length, asset.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(), asset.Sha256.ToLowerInvariant());
        Assert.Equal("dance.vmd", asset.Name);
        using var session = new SceneAnimationSession(scene.Objects,
            [new(first.Id, model, asset.Motion, Asset: asset), new(second.Id, model, asset.Motion, Asset: asset)], NoPhysics,
            lighting.Snapshot, Token);
        SceneAnimationProjectStore.Save(ProjectPath, scene, lighting, session, ExternalAssets, Token);

        var json = Document(); var motions = json["motions"]!.AsArray();
        Assert.Equal("mmd-modern-scene", json["format"]!.GetValue<string>());
        Assert.Equal(2, json["version"]!.GetValue<int>());
        Assert.Equal("mmd-modern-static-scene", json["layout"]!["format"]!.GetValue<string>());
        Assert.Equal(lighting.Lights.Count == 0 ? 1 : SceneProjectStore.LightingFalloffVersion, json["layout"]!["version"]!.GetValue<int>());
        Assert.Null(json["layout"]!["portableModels"]);
        Assert.False(Directory.Exists(Path.Combine(directory, "assets", "models")));
        Assert.Equal(2, motions.Count);
        string relative = motions[0]!["assetPath"]!.GetValue<string>();
        Assert.Equal("assets/motions/" + asset.Sha256.ToLowerInvariant() + ".vmd", relative);
        Assert.Equal(relative, motions[1]!["assetPath"]!.GetValue<string>());
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(directory, relative)));
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "assets", "motions"), "*.vmd"));
        Assert.DoesNotContain(directory.Replace("\\", "\\\\"), File.ReadAllText(ProjectPath));
    }

    [Fact]
    public void EntireProjectTreeCanMoveWhenModelsAndCopiedMotionsAreInsideIt()
    {
        string portable = Path.Combine(directory, "portable"); Directory.CreateDirectory(portable);
        var model = Model(); var (scene, lighting, first, _) = Scene(model, portable);
        var asset = SceneMotionAsset.FromBytes(MotionBytes(30, Vector3.One));
        using var session = Session(scene, lighting, model, first.Id, asset);
        string oldPath = Path.Combine(portable, "animation.mmdscene");
        SceneAnimationProjectStore.Save(oldPath, scene, lighting, session, ExternalAssets, Token);
        string relocated = Path.Combine(directory, "relocated");
        Assert.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, Path.GetFullPath(portable), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, Path.GetFullPath(relocated), StringComparison.OrdinalIgnoreCase);
        Directory.Move(portable, relocated);
        var loadedPaths = new List<string>();
        using var reopened = SceneAnimationProjectStore.Load(Path.Combine(relocated, "animation.mmdscene"), (path, _) =>
        { loadedPaths.Add(path); Assert.StartsWith(relocated + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase); return new(model, Rest(model)); }, NoPhysics, Token);
        Assert.Equal(3, loadedPaths.Count);
        Assert.All(loadedPaths, path => Assert.True(File.Exists(path)));
        Assert.Single(reopened.Animation.Actors, actor => actor.HasMotion);
        Assert.False(Directory.Exists(portable));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavingAfterOriginalVmdChangesOrDisappearsUsesCapturedBytes(bool delete)
    {
        var model = Model(); var (scene, lighting, first, _) = Scene(model);
        string original = Path.Combine(directory, "original.vmd"); byte[] bytes = MotionBytes(30, new(3, 0, 0));
        File.WriteAllBytes(original, bytes); var asset = SceneMotionAsset.Load(original, Token);
        using var session = Session(scene, lighting, model, first.Id, asset);
        if (delete) File.Delete(original); else File.WriteAllBytes(original, MotionBytes(60, new(99, 0, 0)));
        SceneAnimationProjectStore.Save(ProjectPath, scene, lighting, session, ExternalAssets, Token);
        Assert.Equal(bytes, File.ReadAllBytes(MotionPath(Document())));
        using var reopened = SceneAnimationProjectStore.Load(ProjectPath, (_, _) => new(model, Rest(model)), NoPhysics, Token);
        reopened.Animation.Seek(30, Token);
        Near(new(3, 0, 0), reopened.Animation.Frame.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(30u, reopened.Animation.DurationFrames);
    }

    [Fact]
    public void LegacyMotionWithoutCapturedAssetCannotSilentlyReplaceSavedProject()
    {
        var model = Model(); var (scene, lighting, first, _) = Scene(model);
        var asset = SceneMotionAsset.FromBytes(MotionBytes(30, Vector3.One));
        using var saved = Session(scene, lighting, model, first.Id, asset);
        SceneAnimationProjectStore.Save(ProjectPath, scene, lighting, saved, ExternalAssets, Token);
        var before = SavedFiles();
        using var legacy = new SceneAnimationSession(scene.Objects, [new(first.Id, model, asset.Motion)], NoPhysics, lighting.Snapshot, Token);
        Assert.ThrowsAny<Exception>(() => SceneAnimationProjectStore.Save(ProjectPath, scene, lighting, legacy, ExternalAssets, Token));
        AssertFilesUnchanged(before);
    }

    [Fact]
    public void MismatchedMotionAssetIsRejectedWithoutReplacingActiveMotion()
    {
        var model = Model(); var (scene, lighting, first, _) = Scene(model);
        var a = SceneMotionAsset.FromBytes(MotionBytes(30, Vector3.One));
        var b = SceneMotionAsset.FromBytes(MotionBytes(60, new(6, 0, 0)));
        using var session = Session(scene, lighting, model, first.Id, a);
        session.Advance(.1, Token); var before = session.Frame; var states = session.Actors;
        Assert.ThrowsAny<Exception>(() => session.BindMotion(new(first.Id, model, b.Motion, Asset: a), Token));
        Assert.Same(before, session.Frame); Assert.Same(states, session.Actors);
        Assert.Equal(30u, session.DurationFrames); Assert.False(session.IsFaulted);
        Assert.ThrowsAny<Exception>(() => new SceneAnimationSession(scene.Objects,
            [new(first.Id, model, b.Motion, Asset: a)], NoPhysics, lighting.Snapshot, Token));
    }

    [Theory]
    [InlineData("transform")]
    [InlineData("objects")]
    [InlineData("lights")]
    [InlineData("groups")]
    public void EditedLayoutCannotBeSavedWithAnOlderAnimationSnapshot(string edit)
    {
        var model = Model(); var (scene, lighting, first, _) = Scene(model);
        var asset = SceneMotionAsset.FromBytes(MotionBytes(30, Vector3.One));
        using var session = Session(scene, lighting, model, first.Id, asset);
        SceneAnimationProjectStore.Save(ProjectPath, scene, lighting, session, ExternalAssets, Token); var before = SavedFiles();
        if (edit == "transform") scene.SetTransform(first.Id, new(99, 0, 0), Quaternion.Identity, 1);
        else if (edit == "objects") scene.Add("extra", first.SourcePath, ModelRole.Prop, Rest(model));
        else if (edit == "groups") lighting.AddGroup(new(Guid.NewGuid(), "新的灯组"));
        else lighting.Update(lighting.Lights[0] with { Intensity = 4 });
        Assert.ThrowsAny<Exception>(() => SceneAnimationProjectStore.Save(ProjectPath, scene, lighting, session, ExternalAssets, Token));
        AssertFilesUnchanged(before);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidInitialFrameFailsBeforeAllocatingPhysics(double initialFrame)
    {
        var model = Model(physics: true); var (scene, lighting, first, _) = Scene(model);
        var asset = SceneMotionAsset.FromBytes(MotionBytes(30, Vector3.One)); bool called = false;
        Assert.ThrowsAny<ArgumentException>(() => new SceneAnimationSession(scene.Objects,
            [new(first.Id, model, asset.Motion, Asset: asset)], _ => { called = true; return new TrackingBackend(); },
            lighting.Snapshot, Token, initialFrame: initialFrame));
        Assert.False(called);
    }

    [Theory]
    [InlineData("unknown-document-field")]
    [InlineData("unknown-motion-field")]
    [InlineData("unknown-layout-field")]
    [InlineData("object-id")]
    [InlineData("duplicate-motion")]
    [InlineData("negative-frame")]
    [InlineData("nonnumeric-frame")]
    [InlineData("path-traversal")]
    [InlineData("absolute-path")]
    [InlineData("path-extension")]
    [InlineData("hash")]
    [InlineData("version")]
    public void InvalidDocumentMetadataFailsBeforeInvokingModelLoader(string defect)
    {
        var (model, _, _, session) = SaveFixture(); using var owned = session;
        var json = Document(); var motions = json["motions"]!.AsArray(); var first = motions[0]!.AsObject();
        switch (defect)
        {
            case "unknown-document-field": json["unrecognized"] = 1; break;
            case "unknown-motion-field": first["unrecognized"] = 1; break;
            case "unknown-layout-field": json["layout"]!["unrecognized"] = 1; break;
            case "object-id": first["objectId"] = Guid.NewGuid(); break;
            case "duplicate-motion": motions.Add(first.DeepClone()); break;
            case "negative-frame": json["currentFrame"] = -1; break;
            case "nonnumeric-frame": json["currentFrame"] = "NaN"; break;
            case "path-traversal": first["assetPath"] = "../outside.vmd"; break;
            case "absolute-path": first["assetPath"] = Path.Combine(directory, "outside.vmd"); break;
            case "path-extension": first["assetPath"] = "assets/motions/" + first["sha256"]!.GetValue<string>() + ".pmx"; break;
            case "hash": first["sha256"] = "invalid-sha256"; break;
            case "version": json["version"] = 99; break;
        }
        AssertRejectedBeforeModels(json.ToJsonString(), model);
    }

    [Fact]
    public void EveryDocumentAndMotionFieldIsRequired()
    {
        var (model, _, _, session) = SaveFixture(); using var owned = session;
        foreach (string field in new[] { "format", "version", "layout", "currentFrame", "motions" })
        {
            var json = Document(); Assert.True(json.Remove(field));
            AssertRejectedBeforeModels(json.ToJsonString(), model);
        }
        foreach (string field in new[] { "objectId", "assetPath", "sha256", "name", "ikEnabled", "morphsEnabled", "physicsEnabled" })
        {
            var json = Document(); Assert.True(json["motions"]![0]!.AsObject().Remove(field));
            AssertRejectedBeforeModels(json.ToJsonString(), model);
        }
    }

    [Fact]
    public void DuplicatePropertiesAtDocumentAndMotionDepthAreRejectedBeforeModels()
    {
        var (model, _, _, session) = SaveFixture(); using var owned = session;
        string json = File.ReadAllText(ProjectPath);
        foreach (string name in new[] { "currentFrame", "ikEnabled" })
        {
            string property = "\"" + name + "\":"; int index = json.IndexOf(property, StringComparison.Ordinal);
            Assert.True(index >= 0);
            string duplicate = json.Insert(index, property + (name == "ikEnabled" ? "false," : "-1,"));
            AssertRejectedBeforeModels(duplicate, model);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("different-valid-vmd")]
    [InlineData("truncated")]
    public void MissingOrCorruptedMotionAssetFailsBeforeAnyModelIsLoaded(string defect)
    {
        var (model, _, _, session) = SaveFixture(); using var owned = session;
        var json = Document(); string path = MotionPath(json);
        if (defect == "missing") File.Delete(path);
        else File.WriteAllBytes(path, defect == "truncated" ? [1, 2, 3] : MotionBytes(30, new(99, 0, 0)));
        int calls = 0;
        var error = Assert.Throws<SceneProjectMotionException>(() => SceneAnimationProjectStore.Prepare(json.ToJsonString(), ProjectPath,
            (_, _) => { calls++; return new(model, Rest(model)); }, NoPhysics, Token));
        Assert.Equal(0, calls);
        Assert.Equal(json["motions"]![0]!["objectId"]!.GetValue<Guid>(), error.ObjectId);
        Assert.Equal(Path.GetFullPath(path), error.SourcePath);
    }

    [Fact]
    public void LaterCandidatePhysicsFailureDisposesAllNewWorldsAndLeavesActiveSessionUsable()
    {
        var model = Model(physics: true); var (scene, lighting, first, second) = Scene(model);
        var asset = SceneMotionAsset.FromBytes(MotionBytes(30, Vector3.One));
        var activeWorlds = new List<TrackingBackend>();
        using var active = new SceneAnimationSession(scene.Objects,
            [new(first.Id, model, asset.Motion, Asset: asset), new(second.Id, model, asset.Motion, Asset: asset)],
            _ => AddWorld(activeWorlds), lighting.Snapshot, Token);
        active.Advance(.1, Token); var before = active.Frame; var states = active.Actors;
        SceneAnimationProjectStore.Save(ProjectPath, scene, lighting, active, ExternalAssets, Token);
        var candidates = new List<TrackingBackend>();
        Assert.ThrowsAny<Exception>(() => SceneAnimationProjectStore.Load(ProjectPath, (_, _) => new(model, Rest(model)),
            _ => AddWorld(candidates, fail: candidates.Count == 1), Token));
        Assert.Equal(2, candidates.Count); Assert.All(candidates, backend => Assert.Equal(1, backend.DisposeCount));
        Assert.All(activeWorlds, backend => Assert.Equal(0, backend.DisposeCount));
        Assert.Same(before, active.Frame); Assert.Same(states, active.Actors); Assert.Equal(3, active.CurrentFrame);
        active.Advance(.1, Token); Assert.Equal(6, active.CurrentFrame); Assert.False(active.IsFaulted);
    }

    [Fact]
    public void CancellationDuringLaterPhysicsPreparationReleasesCandidatesAndKeepsActiveFrame()
    {
        var model = Model(physics: true); var (scene, lighting, first, second) = Scene(model);
        var asset = SceneMotionAsset.FromBytes(MotionBytes(30, Vector3.One)); var activeWorlds = new List<TrackingBackend>();
        using var active = new SceneAnimationSession(scene.Objects,
            [new(first.Id, model, asset.Motion, Asset: asset), new(second.Id, model, asset.Motion, Asset: asset)],
            _ => AddWorld(activeWorlds), lighting.Snapshot, Token);
        SceneAnimationProjectStore.Save(ProjectPath, scene, lighting, active, ExternalAssets, Token); var before = active.Frame;
        using var cancel = new CancellationTokenSource(); var candidates = new List<TrackingBackend>();
        Assert.ThrowsAny<OperationCanceledException>(() => SceneAnimationProjectStore.Load(ProjectPath, (_, _) => new(model, Rest(model)), _ =>
        { var backend = AddWorld(candidates); if (candidates.Count == 2) cancel.Cancel(); return backend; }, cancel.Token));
        Assert.Equal(2, candidates.Count); Assert.All(candidates, backend => Assert.Equal(1, backend.DisposeCount));
        Assert.All(activeWorlds, backend => Assert.Equal(0, backend.DisposeCount));
        Assert.Same(before, active.Frame); active.Advance(.1, Token); Assert.False(active.IsFaulted);
    }

    [Fact]
    public void ReadOnlyDocumentWriteFailurePreservesOldDocumentAndPreviouslyReferencedResources()
    {
        var (model, scene, lighting, session) = SaveFixture(); using var owned = session;
        var before = SavedFiles(); var replacement = SceneMotionAsset.FromBytes(MotionBytes(60, new(9, 0, 0)));
        session.BindMotion(new(scene.Objects[0].Id, model, replacement.Motion, Asset: replacement), Token);
        File.SetAttributes(ProjectPath, FileAttributes.ReadOnly);
        try { Assert.ThrowsAny<Exception>(() => SceneAnimationProjectStore.Save(ProjectPath, scene, lighting, session, ExternalAssets, Token)); }
        finally { File.SetAttributes(ProjectPath, FileAttributes.Normal); }
        AssertFilesUnchanged(before);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        using var reopened = SceneAnimationProjectStore.Load(ProjectPath, (_, _) => new(model, Rest(model)), NoPhysics, Token);
        Assert.Equal(30u, reopened.Animation.DurationFrames);
    }

    [Fact]
    public void ExistingContentAddressedResourceIsNeverOverwrittenOnHashCollisionOrCorruption()
    {
        var (model, scene, lighting, session) = SaveFixture(); using var owned = session;
        var before = SavedFiles(); var replacement = SceneMotionAsset.FromBytes(MotionBytes(60, new(9, 0, 0)));
        session.BindMotion(new(scene.Objects[0].Id, model, replacement.Motion, Asset: replacement), Token);
        string collision = Path.Combine(directory, "assets", "motions", replacement.Sha256.ToLowerInvariant() + ".vmd");
        byte[] foreign = [8, 7, 6]; File.WriteAllBytes(collision, foreign);
        Assert.ThrowsAny<Exception>(() => SceneAnimationProjectStore.Save(ProjectPath, scene, lighting, session, ExternalAssets, Token));
        AssertFilesUnchanged(before); Assert.Equal(foreign, File.ReadAllBytes(collision));
    }

    [Fact]
    public void ReopenRestartsPhysicsAtSavedPoseAndAlreadyFinishedActorRemainsFrozen()
    {
        var model = Model(physics: true); var (scene, lighting, first, second) = Scene(model);
        var shortAsset = SceneMotionAsset.FromBytes(MotionBytes(1, Vector3.UnitX));
        var longAsset = SceneMotionAsset.FromBytes(MotionBytes(30, new(30, 0, 0)));
        using var active = new SceneAnimationSession(scene.Objects,
            [new(first.Id, model, shortAsset.Motion, Asset: shortAsset), new(second.Id, model, longAsset.Motion, Asset: longAsset)],
            _ => new TrackingBackend(), lighting.Snapshot, Token);
        active.Advance(.1, Token); SceneAnimationProjectStore.Save(ProjectPath, scene, lighting, active, ExternalAssets, Token);
        var worlds = new List<TrackingBackend>();
        using var reopened = SceneAnimationProjectStore.Load(ProjectPath, (_, _) => new(model, Rest(model)), _ => AddWorld(worlds), Token);
        Assert.Equal(3, reopened.Animation.CurrentFrame);
        Assert.Equal(1, reopened.Animation.Actors[0].LocalFrame); Assert.Equal(3, reopened.Animation.Actors[1].LocalFrame);
        Assert.Equal(2, worlds.Count); Assert.All(worlds, world => Assert.Equal(0, world.Steps));
        Assert.All(reopened.Animation.Actors.Take(2), actor => Assert.True(actor.PhysicsEnabled));
        Near(Vector3.UnitX, reopened.Animation.Frame.Surface.Snapshot.Vertices[0].Position);
        var stopped = reopened.Animation.Frame.Surface.Snapshot.Vertices[0]; reopened.Animation.Advance(.1, Token);
        Assert.Equal(0, worlds[0].Steps); Assert.True(worlds[1].Steps > 0);
        Assert.Equal(stopped, reopened.Animation.Frame.Surface.Snapshot.Vertices[0]);
    }

    [Fact]
    public void RequestedPhysicsOnWithoutRigidBodiesSurvivesReopenAndSecondSave()
    {
        var (model, _, _, session) = SaveFixture(); using var owned = session;
        using var reopened = SceneAnimationProjectStore.Load(ProjectPath, (_, _) => new(model, Rest(model)), NoPhysics, Token);
        Assert.False(reopened.Animation.Actors[0].SupportsPhysics); Assert.False(reopened.Animation.Actors[0].PhysicsEnabled);
        SceneAnimationProjectStore.Save(ProjectPath, reopened.Composition, reopened.Lighting, reopened.Animation, ExternalAssets, Token);
        Assert.True(Document()["motions"]![0]!["physicsEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void VersionOneStaticSceneRestoresAsZeroFrameSessionWithoutMotionsOrPhysics()
    {
        var model = Model(); var (scene, lighting, _, _) = Scene(model);
        SceneProjectStore.Save(ProjectPath, scene, lighting, ExternalAssets, Token);
        using var reopened = SceneAnimationProjectStore.Load(ProjectPath, (_, _) => new(model, Rest(model)), NoPhysics, Token);
        Assert.Equal(0, reopened.Animation.CurrentFrame); Assert.Equal(0u, reopened.Animation.DurationFrames);
        Assert.All(reopened.Animation.Actors, actor => Assert.False(actor.HasMotion));
        Assert.Equal(scene.Objects.Select(item => item.Id), reopened.Composition.Objects.Select(item => item.Id));
        Assert.Equal(scene.Frame.Surface.Snapshot.Vertices, reopened.Animation.Frame.Surface.Snapshot.Vertices);
        Assert.Equal(lighting.Lights, reopened.Lighting.Lights);
    }

    [Fact]
    public void RealPmxQdefAnimationSurvivesProjectRoundTrip()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx");
        byte[] before = File.ReadAllBytes(fixture);
        SceneAnimationModelAsset Read(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); using var stream = File.OpenRead(path); var model = PmxReader.Read(stream);
            return new(model, Rest(model));
        }
        var loaded = Read(fixture, Token); var scene = new SceneCompositionEditor(); var lighting = new SceneLightingEditor();
        var actor = scene.Add("real fixture", fixture, ModelRole.Character, loaded.Source);
        var asset = SceneMotionAsset.FromBytes(MotionBytes(30, Vector3.UnitX, boneName: loaded.Model.Bones[0].Name));
        using var session = new SceneAnimationSession(scene.Objects,
            [new(actor.Id, loaded.Model, asset.Motion, PhysicsEnabled: false, Asset: asset)], NoPhysics, lighting.Snapshot, Token, initialFrame: .5);
        SceneAnimationProjectStore.Save(ProjectPath, scene, lighting, session, ExternalAssets, Token);
        using var reopened = SceneAnimationProjectStore.Load(ProjectPath, Read, NoPhysics, Token);
        Assert.Equal(.5, reopened.Animation.CurrentFrame);
        Assert.All(reopened.Animation.Actors, state => Assert.True(state.HasMotion));
        Assert.Equal(session.Frame.Surface.Snapshot.Vertices, reopened.Animation.Frame.Surface.Snapshot.Vertices);
        Assert.Equal(session.Frame.Surface.Snapshot.Indices, reopened.Animation.Frame.Surface.Snapshot.Indices);
        Assert.Equal(before, File.ReadAllBytes(fixture));
    }

    [Fact]
    public void CancelledSaveKeepsDocumentAndReferencedAssetsUnchanged()
    {
        var (_, scene, lighting, session) = SaveFixture(); using var owned = session; var before = SavedFiles();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SceneAnimationProjectStore.Save(ProjectPath, scene, lighting, session, ExternalAssets, cancel.Token));
        AssertFilesUnchanged(before);
    }

    private (PmxModel Model, SceneCompositionEditor Scene, SceneLightingEditor Lighting, SceneAnimationSession Session) SaveFixture()
    {
        var model = Model(); var (scene, lighting, first, _) = Scene(model);
        var asset = SceneMotionAsset.FromBytes(MotionBytes(30, Vector3.One));
        var session = Session(scene, lighting, model, first.Id, asset);
        try { SceneAnimationProjectStore.Save(ProjectPath, scene, lighting, session, ExternalAssets, Token); return (model, scene, lighting, session); }
        catch { session.Dispose(); throw; }
    }

    private static SceneAnimationSession Session(SceneCompositionEditor scene, SceneLightingEditor lighting, PmxModel model, Guid actor, SceneMotionAsset asset) =>
        new(scene.Objects, [new(actor, model, asset.Motion, Asset: asset)], NoPhysics, lighting.Snapshot, Token);
    private JsonObject Document() => JsonNode.Parse(File.ReadAllText(ProjectPath))!.AsObject();
    private string MotionPath(JsonObject json) => Path.Combine(directory, json["motions"]![0]!["assetPath"]!.GetValue<string>());
    private Dictionary<string, byte[]> SavedFiles() => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
    private static void AssertFilesUnchanged(Dictionary<string, byte[]> before)
    { foreach (var item in before) Assert.Equal(item.Value, File.ReadAllBytes(item.Key)); }
    private void AssertRejectedBeforeModels(string json, PmxModel model)
    {
        int calls = 0;
        Assert.ThrowsAny<Exception>(() => SceneAnimationProjectStore.Prepare(json, ProjectPath,
            (_, _) => { calls++; return new(model, Rest(model)); }, NoPhysics, Token));
        Assert.Equal(0, calls);
    }

    private (SceneCompositionEditor Scene, SceneLightingEditor Lighting, EditableSceneObject First, EditableSceneObject Second) Scene(PmxModel model, string? parent = null)
    {
        string assets = Path.Combine(parent ?? directory, "models"); Directory.CreateDirectory(assets);
        string path = Path.Combine(assets, "actor.pmx"); File.WriteAllText(path, "PMX loader supplied by test");
        var scene = new SceneCompositionEditor(); var lighting = new SceneLightingEditor(); var rest = Rest(model);
        var first = scene.Add("first", path, ModelRole.Character, rest);
        var second = scene.Add("second", path, ModelRole.Character, rest);
        scene.SetTransform(second.Id, new(10, 2, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f), 2);
        var room = scene.Add("room", path, ModelRole.Environment, rest);
        scene.SetTransform(room.Id, new(-3, 0, 0), Quaternion.Identity, 3);
        lighting.SetModelRole(room.Key, ModelRole.Environment, true);
        lighting.Add(new(Guid.NewGuid(), "key", SceneLightKind.Point, new(0, 4, 0), Quaternion.Identity, new(1, .8f, .5f)));
        return (scene, lighting, first, scene.Objects[1]);
    }

    private static ICharacterPhysicsBackend NoPhysics(EditableSceneObject _) => throw new InvalidOperationException("Unexpected physics allocation.");
    private static TrackingBackend AddWorld(List<TrackingBackend> worlds, bool fail = false)
    { var backend = new TrackingBackend { FailInitialize = fail }; worlds.Add(backend); return backend; }
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, 1e-4f);
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));

    private static byte[] MotionBytes(uint duration, Vector3 movement, bool morph = false, string boneName = "root")
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        Name(writer, "Vocaloid Motion Data 0002", 30); Name(writer, "fixture", 20); writer.Write(2u);
        Bone(writer, boneName, 0, Vector3.Zero); Bone(writer, boneName, duration, movement);
        writer.Write(morph ? 2u : 0u);
        if (morph)
        {
            Name(writer, "smile", 15); writer.Write(0u); writer.Write(0f);
            Name(writer, "smile", 15); writer.Write(1u); writer.Write(1f);
        }
        writer.Flush(); return stream.ToArray();
    }
    private static void Bone(BinaryWriter writer, string name, uint frame, Vector3 position)
    {
        Name(writer, name, 15); writer.Write(frame); writer.Write(position.X); writer.Write(position.Y); writer.Write(position.Z);
        writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f); writer.Write(new byte[64]);
    }
    private static void Name(BinaryWriter writer, string value, int size)
    {
        var bytes = Encoding.GetEncoding(932).GetBytes(value);
        Assert.True(bytes.Length <= size, "Fixture name exceeds its VMD field.");
        writer.Write(bytes); writer.Write(new byte[size - bytes.Length]);
    }

    private static PmxModel Model(bool physics = false, bool morph = false) => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "fixture", EnglishName = "", Comment = "", EnglishComment = "",
        Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
        { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
            BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
        Faces = [new(0, 1, 2)], Textures = [],
        Materials = [new() { Name = "toon", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 4,
            Ambient = new(.3f), DrawMode = PmxDrawModeFlags.DrawEdge, EdgeColor = new(0, 0, 0, 1), EdgeSize = 1,
            TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate,
            ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
        Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
        Morphs = morph ? [new() { Name = "smile", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Position,
            PositionOffsets = [new(0, new(0, 0, 2))] }] : [], DisplayFrames = [], Joints = [], SoftBodies = [],
        RigidBodies = physics ? [new() { Name = "body", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
            Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
            Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic }] : [],
    };

    private sealed class TrackingBackend : ICharacterPhysicsBackend
    {
        private Matrix4x4[] worlds = [];
        public int Steps, DisposeCount; public bool FailInitialize;
        public string Name => "project test fixture";
        public IReadOnlyList<string> Diagnostics => Array.Empty<string>();
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> input)
        { if (FailInitialize) throw new InvalidOperationException("fixture initialization failed"); worlds = input.ToArray(); Steps = 0; }
        public void SetKinematicTarget(int index, Matrix4x4 world, float seconds) => worlds[index] = world;
        public Matrix4x4 GetBodyWorldTransform(int index) => worlds[index];
        public void Step(float seconds)
        { Steps++; for (int i = 0; i < worlds.Length; i++) worlds[i].Translation += new Vector3(0, -seconds, 0); }
        public void Dispose() => DisposeCount++;
    }

    public void Dispose()
    {
        string fullPath = Path.GetFullPath(directory);
        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(fullPath), tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).StartsWith("animation-project-", StringComparison.Ordinal))
            throw new InvalidOperationException("Test cleanup must remain inside its dedicated temporary directory.");
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
    }
}
