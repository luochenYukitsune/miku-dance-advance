using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneMotionProjectTests : IDisposable
{
    private static readonly SceneProjectSaveOptions ExternalAssets = new() { CopyModelAssets = false };
    readonly string directory = Path.Combine(Path.GetTempPath(), "scene-motion-" + Guid.NewGuid().ToString("N"));
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static ICharacterPhysicsBackend NoPhysics(EditableSceneObject _) => throw new InvalidOperationException("No bodies.");
    public SceneMotionProjectTests() => Directory.CreateDirectory(directory);

    static VmdMotion Motion(string name = "camera") => new()
    {
        ModelName = name,
        CameraKeyframes = [new() { Frame = 0, Distance = -30, Interest = new(0, 1, 0), ViewAngle = 30 },
            new() { Frame = 30, Distance = -60, Interest = new(3, 4, 5), ViewAngle = 60 }],
        LightKeyframes = [new() { Frame = 0, Color = Vector3.One, Direction = new(0, -1, 0) },
            new() { Frame = 40, Color = new(.2f, .4f, .6f), Direction = new(1, -2, 3) }],
        SelfShadowKeyframes = [new() { Frame = 0, Mode = 1, Distance = .001f }, new() { Frame = 20, Mode = 2, Distance = .002f }]
    };

    (SceneCompositionEditor Layout, SceneLightingEditor Lighting, PmxModel Model, Guid Id) Fixture()
    {
        var model = EditedMotionProjectTests.Model(); var layout = new SceneCompositionEditor();
        string modelPath = Path.Combine(directory, "model.pmx"); File.WriteAllText(modelPath, "injected fixture");
        var actor = layout.Add("actor", modelPath, ModelRole.Character, EditedMotionProjectTests.Rest(model));
        return (layout, new(), model, actor.Id);
    }

    [Fact]
    public void SceneDurationKeepsClockMovingWhileShortActorFreezes()
    {
        var (layout, lighting, model, id) = Fixture();
        var actorAsset = SceneMotionAsset.FromMotion(new() { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 1, Translation = Vector3.UnitX }] });
        // Retained character keys in the scene asset must not extend the scene-track duration.
        var source = Motion(); source = new() { ModelName = source.ModelName, CameraKeyframes = source.CameraKeyframes,
            LightKeyframes = source.LightKeyframes, SelfShadowKeyframes = source.SelfShadowKeyframes,
            BoneKeyframes = [new() { BoneName = "unrelated", Frame = 1000 }] };
        var sceneAsset = SceneMotionAsset.FromMotion(source);
        using var session = new SceneAnimationSession(layout.Objects,
            [new(id, model, actorAsset.Motion, PhysicsEnabled: false, Asset: actorAsset)], NoPhysics, lighting.Snapshot, Token, sceneMotion: sceneAsset);
        Assert.Equal(40u, session.DurationFrames); session.Advance(1.0 / 30, Token);
        var frozen = session.Frame.Surface.Snapshot.Vertices.ToArray(); session.Advance(14.0 / 30, Token);
        Assert.Equal(15, session.CurrentFrame); Assert.Equal(1, session.Actors[0].LocalFrame);
        Assert.Equal(frozen, session.Frame.Surface.Snapshot.Vertices);
        Assert.Equal(new VmdSceneTracks(sceneAsset.Motion).CameraAt(15), session.CameraPose);
        Assert.Equal(new VmdSceneTracks(sceneAsset.Motion).LightAt(15), session.LightPose);
    }

    [Fact]
    public void SceneOnlyMotionCanPlayOverStaticObjectsAndUnbind()
    {
        var (layout, lighting, _, _) = Fixture();
        using var session = new SceneAnimationSession(layout.Objects, [], NoPhysics, lighting.Snapshot, Token,
            sceneMotion: SceneMotionAsset.FromMotion(Motion()));
        session.Advance(.5, Token); Assert.Equal(15, session.CurrentFrame); Assert.NotNull(session.CameraPose);
        Assert.All(session.Actors, actor => Assert.False(actor.HasMotion));
        session.BindSceneMotion(null, Token); Assert.Null(session.SceneMotion); Assert.Null(session.CameraPose);
        Assert.Null(session.LightPose); Assert.Null(session.SelfShadowPose); Assert.Equal(0u, session.DurationFrames);
    }

    [Fact]
    public void InvalidOrCancelledSceneBindPreservesPublishedState()
    {
        var (layout, lighting, _, _) = Fixture(); var original = SceneMotionAsset.FromMotion(Motion());
        using var session = new SceneAnimationSession(layout.Objects, [], NoPhysics, lighting.Snapshot, Token, sceneMotion: original);
        var frame = session.Frame; var camera = session.CameraPose;
        var characterOnly = SceneMotionAsset.FromMotion(new() { BoneKeyframes = [new() { BoneName = "root" }] });
        Assert.Throws<NotSupportedException>(() => session.BindSceneMotion(characterOnly, Token));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => session.BindSceneMotion(null, cancel.Token));
        Assert.Same(frame, session.Frame); Assert.Same(original, session.SceneMotion); Assert.Equal(camera, session.CameraPose);
        Assert.False(session.IsFaulted);
    }

    [Theory]
    [InlineData(932, false)]
    [InlineData(936, false)]
    [InlineData(932, true)]
    public void SceneAssetAndFractionalPoseSurviveVersionSixSaveReopen(int codePage, bool mixed)
    {
        var (layout, lighting, model, _) = Fixture();
        var motion = Motion(mixed ? "汉字" : "camera");
        if (mixed) motion = new() { ModelName = motion.ModelName,
            OriginalModelName = new(motion.ModelName, CodePagesEncodingProvider.Instance.GetEncoding(936)!.GetBytes(motion.ModelName), 936),
            CameraKeyframes = motion.CameraKeyframes, LightKeyframes = motion.LightKeyframes, SelfShadowKeyframes = motion.SelfShadowKeyframes };
        var asset = SceneMotionAsset.FromMotion(motion, "scene.vmd", new() { AnsiCodePage = codePage });
        using var session = new SceneAnimationSession(layout.Objects, [], NoPhysics, lighting.Snapshot, Token, initialFrame: 12.5, sceneMotion: asset);
        string path = Path.Combine(directory, "scene.mmdscene");
        SceneAnimationProjectStore.Save(path, layout, lighting, session, ExternalAssets, Token);
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(6, document["version"]!.GetValue<int>()); Assert.NotNull(document["sceneMotion"]);
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "assets", "motions"), "*.vmd"));
        using var reopened = SceneAnimationProjectStore.Load(path, (_, _) => new(model, EditedMotionProjectTests.Rest(model)), NoPhysics, Token);
        Assert.Equal(12.5, reopened.Animation.CurrentFrame); Assert.Equal(session.CameraPose, reopened.Animation.CameraPose);
        Assert.Equal(session.LightPose, reopened.Animation.LightPose); Assert.Equal(session.SelfShadowPose, reopened.Animation.SelfShadowPose);
        Assert.Equal(asset.Sha256, reopened.Animation.SceneMotion!.Sha256); Assert.Equal(codePage, reopened.Animation.SceneMotion.AnsiCodePage);
        Assert.Equal(asset.Motion.ModelName, reopened.Animation.SceneMotion.Motion.ModelName);
        Assert.Equal(asset.NameOverrides.Count, reopened.Animation.SceneMotion.NameOverrides.Count);
    }

    [Theory]
    [InlineData("oldVersion")]
    [InlineData("assetPath")]
    [InlineData("badCodePage")]
    [InlineData("badHash")]
    [InlineData("corruptAsset")]
    public void InvalidSceneResourceFailsBeforeAnyModelLoader(string fault)
    {
        var (layout, lighting, _, _) = Fixture(); var asset = SceneMotionAsset.FromMotion(Motion());
        using var session = new SceneAnimationSession(layout.Objects, [], NoPhysics, lighting.Snapshot, Token, sceneMotion: asset);
        string path = Path.Combine(directory, "scene.mmdscene"); SceneAnimationProjectStore.Save(path, layout, lighting, session, ExternalAssets, Token);
        var document = JsonNode.Parse(File.ReadAllText(path))!; var resource = document["sceneMotion"]!;
        switch (fault)
        {
            case "oldVersion": document["version"] = 5; break;
            case "assetPath": resource["assetPath"] = "elsewhere.vmd"; break;
            case "badCodePage": resource["ansiCodePage"] = 65001; break;
            case "badHash": resource["sha256"] = new string('a', 64); resource["assetPath"] = "assets/motions/" + new string('a', 64) + ".vmd"; break;
            default: File.WriteAllBytes(Path.Combine(directory, "assets", "motions", asset.Sha256 + ".vmd"), [1, 2, 3]); break;
        }
        int loads = 0;
        Assert.ThrowsAny<Exception>(() => SceneAnimationProjectStore.Prepare(document.ToJsonString(), path,
            (_, _) => { loads++; throw new InvalidOperationException("Must not load models."); }, NoPhysics, Token));
        Assert.Equal(0, loads); Assert.False(session.IsFaulted);
    }

    [Fact]
    public void CancelledSaveLeavesExistingVersionSixProjectIntact()
    {
        var (layout, lighting, _, _) = Fixture();
        using var session = new SceneAnimationSession(layout.Objects, [], NoPhysics, lighting.Snapshot, Token, sceneMotion: SceneMotionAsset.FromMotion(Motion()));
        string path = Path.Combine(directory, "scene.mmdscene"); SceneAnimationProjectStore.Save(path, layout, lighting, session, ExternalAssets, Token);
        byte[] before = File.ReadAllBytes(path); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SceneAnimationProjectStore.Save(path, layout, lighting, session, ExternalAssets, cancel.Token));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void SharedActorSceneAssetAndOutsideParentsSurviveVersionSixTogether()
    {
        var (layout, lighting, model, first) = Fixture();
        var second = layout.Add("second", Path.Combine(directory, "model.pmx"), ModelRole.Character, EditedMotionProjectTests.Rest(model));
        var source = Motion(); var asset = SceneMotionAsset.FromMotion(new() { ModelName = "shared",
            CameraKeyframes = source.CameraKeyframes, LightKeyframes = source.LightKeyframes, SelfShadowKeyframes = source.SelfShadowKeyframes,
            BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 1, Translation = Vector3.UnitX }] });
        using var session = new SceneAnimationSession(layout.Objects,
            [new(first, model, asset.Motion, PhysicsEnabled: false, Asset: asset), new(second.Id, model, asset.Motion, PhysicsEnabled: false, Asset: asset)],
            NoPhysics, lighting.Snapshot, Token, sceneMotion: asset,
            outsideParents: [new(first, 0, second.Id, 0, Matrix4x4.Identity)]);
        session.Advance(.5, Token); string path = Path.Combine(directory, "shared.mmdscene");
        SceneAnimationProjectStore.Save(path, layout, lighting, session, ExternalAssets, Token);
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "assets", "motions"), "*.vmd"));
        using var reopened = SceneAnimationProjectStore.Load(path, (_, _) => new(model, EditedMotionProjectTests.Rest(model)), NoPhysics, Token);
        Assert.Equal(session.OutsideParents, reopened.Animation.OutsideParents);
        Assert.Equal(session.Frame.Surface.Snapshot.Vertices, reopened.Animation.Frame.Surface.Snapshot.Vertices);
        Assert.Equal(session.CameraPose, reopened.Animation.CameraPose); Assert.Equal(40u, reopened.Animation.DurationFrames);
        Assert.All(reopened.Animation.Actors, actor => Assert.Equal(1, actor.LocalFrame));
    }

    [Fact]
    public void ValidCharacterOnlyAssetInSceneSlotRejectsBeforeModelLoading()
    {
        var (layout, lighting, _, _) = Fixture();
        using var session = new SceneAnimationSession(layout.Objects, [], NoPhysics, lighting.Snapshot, Token, sceneMotion: SceneMotionAsset.FromMotion(Motion()));
        string path = Path.Combine(directory, "scene.mmdscene"); SceneAnimationProjectStore.Save(path, layout, lighting, session, ExternalAssets, Token);
        var actorOnly = SceneMotionAsset.FromMotion(new() { BoneKeyframes = [new() { BoneName = "root" }] });
        string relative = "assets/motions/" + actorOnly.Sha256 + ".vmd";
        File.WriteAllBytes(Path.GetFullPath(relative, directory), VmdWriter.ToBytes(actorOnly.Motion));
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["sceneMotion"]!["sha256"] = actorOnly.Sha256; document["sceneMotion"]!["assetPath"] = relative;
        int loads = 0;
        Assert.Throws<SceneProjectSceneMotionException>(() => SceneAnimationProjectStore.Prepare(document.ToJsonString(), path,
            (_, _) => { loads++; throw new InvalidOperationException("Must not load models."); }, NoPhysics, Token));
        Assert.Equal(0, loads);
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(directory), temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid temp scope.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
