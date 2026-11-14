using System.Numerics;
using System.Text;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorRecoverySessionStateTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    [Fact]
    public async Task DetachedRoundTripPreservesIncompleteDraftsAcrossActorsWithoutRegistering() => await session.Dispatch(() =>
    {
        var first = new SceneActorMotion(Guid.NewGuid(), Model(), Motion());
        var second = new SceneActorMotion(Guid.NewGuid(), Model(), Motion());
        SceneActorMotion[] actors = [first, second];
        var animation = new EditorAnimationViewModel { CurrentFrame = 7 };
        var scene = new EditorSceneInspectorViewModel { CurrentFrame = 7 };
        animation.SetActor(first.ObjectId, first.Model, first.Motion); animation.SelectBone(0);
        animation.PositionX = 12.125m; animation.RotationZ = null;
        animation.SelectMorph(0); animation.MorphWeight = .875m;
        animation.SetActor(second.ObjectId, second.Model, second.Motion); animation.SelectBone(1);
        animation.PositionY = -9m;
        animation.SelectMorph(0); animation.MorphWeight = null;
        animation.SetTimelineTrackMode(EditorTimelineTrackMode.FollowSelection);
        animation.SelectedKeyframes = [KeyframeAddress.Bone("root", 10), KeyframeAddress.Morph("smile", 20)];
        animation.TimelineRangeStartText = "invalid frame"; animation.TimelineRangeEndText = "123";
        scene.CameraCenterX = 4m; scene.CameraFov = null;
        scene.LightColorR = .25m; scene.LightIntensity = 3m; scene.LightDirectionY = null;
        int commits = 0;
        animation.CommitMotion = (_, _) => commits++; scene.CommitSceneMotion = _ => commits++;

        var state = EditorRecoverySessionState.Capture(animation, scene, actors) with
        { SceneSelectionIndex = 2, HierarchyTab = 1, PixelsPerFrame = 3.5, FirstVisibleFrame = 12.25 };
        byte[] bytes = state.ToJsonBytes();
        animation.PositionY = 99m; // The captured values must be detached from the live dictionaries.
        var restored = EditorRecoverySessionState.FromJsonBytes(bytes);
        var target = new EditorAnimationViewModel(); var targetScene = new EditorSceneInspectorViewModel();
        target.CommitMotion = (_, _) => commits++; targetScene.CommitSceneMotion = _ => commits++;
        restored.Restore(target, targetScene, actors);

        Assert.Equal(0, commits); Assert.Equal(7, target.CurrentFrame); Assert.Equal(7, targetScene.CurrentFrame);
        Assert.Equal(second.ObjectId, target.ObjectId); Assert.Equal(0, target.SelectedMorphIndex);
        Assert.Null(target.MorphWeight); Assert.True(target.HasAnyDraft); Assert.True(target.HasInvalidDrafts);
        Assert.Equal(EditorTimelineTrackMode.FollowSelection, target.TimelineTrackMode);
        Assert.Equal(state.Animation.SelectedKeyframes, target.SelectedKeyframes);
        Assert.Equal("invalid frame", target.TimelineRangeStartText); Assert.Equal("123", target.TimelineRangeEndText);
        target.SelectBone(1); Assert.Equal(-9m, target.PositionY);
        target.SetActor(first.ObjectId, first.Model, first.Motion); target.SelectBone(0);
        Assert.Equal(12.125m, target.PositionX); Assert.Null(target.RotationZ);
        target.SelectMorph(0); Assert.Equal(.875m, target.MorphWeight);
        Assert.True(targetScene.HasCameraDraft); Assert.True(targetScene.HasLightDraft);
        Assert.Equal(4m, targetScene.CameraCenterX); Assert.Null(targetScene.CameraFov);
        Assert.Equal(.25m, targetScene.LightColorR); Assert.Equal(3m, targetScene.LightIntensity);
        Assert.Null(targetScene.LightDirectionY);
        Assert.Equal(2, restored.SceneSelectionIndex); Assert.Equal(1, restored.HierarchyTab);
        Assert.Equal(3.5, restored.PixelsPerFrame); Assert.Equal(12.25, restored.FirstVisibleFrame);
        Assert.Equal(2, first.Motion.BoneKeyframes.Count); Assert.Single(first.Motion.MorphKeyframes);
        Assert.Equal(2, second.Motion.BoneKeyframes.Count); Assert.Single(second.Motion.MorphKeyframes);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task TranslationOnlyDraftPreservesTheAuthoredQuaternionExactly() => await session.Dispatch(() =>
    {
        var rotation = new Quaternion(.1f, .2f, .3f, .8f);
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root", Frame = 7, Rotation = rotation }] };
        var actor = new SceneActorMotion(Guid.NewGuid(), Model(), motion);
        var source = new EditorAnimationViewModel { CurrentFrame = 7 };
        source.SetActor(actor.ObjectId, actor.Model, actor.Motion); source.SelectBone(0); source.PositionX = 8m;
        var state = EditorRecoverySessionState.Capture(source, new() { CurrentFrame = 7 }, [actor]);
        var target = new EditorAnimationViewModel(); state.Restore(target, new(), [actor]);
        Assert.Equal(rotation, Assert.Single(target.SnapshotDrafts()).Rotation);
        Assert.Equal(rotation, actor.Motion.BoneKeyframes[0].Rotation);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task CameraPanPoseAndInvalidTypedValuesRoundTripWithoutPublishingAnInvalidCamera() => await session.Dispatch(() =>
    {
        var source = new EditorSceneInspectorViewModel();
        var pose = source.CameraPose with { LookAt = new(2, 5, 8), ViewOffset = new(1.5f, -2f) };
        source.SetCameraDraft(pose); source.CameraDistance = null;
        source.LightColorG = -1m; // Invalid user edits are retained as drafts, not repaired silently.
        var state = EditorRecoverySessionState.Capture(new(), source, []);
        var target = new EditorSceneInspectorViewModel(); int published = 0;
        target.CameraPoseChanged += _ => published++;
        EditorRecoverySessionState.FromJsonBytes(state.ToJsonBytes()).Restore(new(), target, []);
        Assert.Equal(pose, target.CameraPose); Assert.Null(target.CameraDistance);
        Assert.Equal(-1m, target.LightColorG); Assert.True(target.HasLightValidation);
        Assert.Equal(0, published);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ChangedModelStructureAndMissingActorRejectBeforeEitherViewModelChanges() => await session.Dispatch(() =>
    {
        var actor = new SceneActorMotion(Guid.NewGuid(), Model(), Motion());
        var source = new EditorAnimationViewModel(); source.SetActor(actor.ObjectId, actor.Model, actor.Motion);
        source.SelectBone(0); source.PositionX = 8m;
        var state = EditorRecoverySessionState.Capture(source, new(), [actor]);
        var target = new EditorAnimationViewModel { CurrentFrame = 42 };
        var scene = new EditorSceneInspectorViewModel { CurrentFrame = 42, CameraCenterX = 15m };
        Assert.Throws<InvalidDataException>(() => state.Restore(target, scene, []));
        Assert.Throws<InvalidDataException>(() => state.Restore(target, scene,
            [actor with { Model = Model("renamed-root") }]));
        Assert.Equal(42, target.CurrentFrame); Assert.Null(target.ObjectId);
        Assert.Equal(42, scene.CurrentFrame); Assert.Equal(15m, scene.CameraCenterX);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task MatchingStableActorIdentityAllowsSceneDisplayRenameAndFreshModelInstances() => await session.Dispatch(() =>
    {
        var actor = new SceneActorMotion(Guid.NewGuid(), Model(), Motion());
        var source = new EditorAnimationViewModel(); source.SetActor(actor.ObjectId, actor.Model, actor.Motion, "old label");
        source.SelectBone(1); source.PositionZ = 3m;
        var state = EditorRecoverySessionState.Capture(source, new(), [actor]);
        var fresh = actor with { Model = Model(), Motion = Motion() };
        var target = new EditorAnimationViewModel(); target.SetActor(fresh.ObjectId, fresh.Model, fresh.Motion, "renamed label");
        state.Restore(target, new(), [fresh]);
        Assert.Equal(3m, target.PositionZ); Assert.Contains("renamed label", target.SelectionLabel);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task InvalidSceneStateCannotPartiallyRestoreValidAnimationDrafts() => await session.Dispatch(() =>
    {
        var actor = new SceneActorMotion(Guid.NewGuid(), Model(), Motion());
        var source = new EditorAnimationViewModel(); source.SetActor(actor.ObjectId, actor.Model, actor.Motion);
        source.SelectBone(0); source.PositionX = 8m;
        var state = EditorRecoverySessionState.Capture(source, new(), [actor]);
        state = state with { Scene = state.Scene with { CurrentFrame = 1 } };
        var target = new EditorAnimationViewModel { CurrentFrame = 42 }; var scene = new EditorSceneInspectorViewModel();
        Assert.Throws<InvalidDataException>(() => state.Restore(target, scene, [actor]));
        Assert.Equal(42, target.CurrentFrame); Assert.Null(target.ObjectId); Assert.False(target.HasAnyDraft);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task InvalidDraftIndexQuaternionAndDuplicateIdentityAreRejected() => await session.Dispatch(() =>
    {
        var actor = new SceneActorMotion(Guid.NewGuid(), Model(), Motion());
        var source = new EditorAnimationViewModel(); source.SetActor(actor.ObjectId, actor.Model, actor.Motion);
        source.SelectBone(0); source.PositionX = 8m;
        var state = EditorRecoverySessionState.Capture(source, new(), [actor]);
        var pending = Assert.Single(state.Animation.Actors); var bone = Assert.Single(pending.Bones);
        foreach (var changed in new[] { bone with { Index = 100 }, bone with { SampledRotation = new(0, 0, 0, 0) },
            bone with { SampledRotation = new(float.NaN, 0, 0, 1) } })
        {
            var invalid = state with { Animation = state.Animation with { Actors = [pending with { Bones = [changed] }] } };
            Assert.Throws<InvalidDataException>(() => invalid.ValidateForActors([actor]));
        }
        var duplicate = state with { Animation = state.Animation with { Actors = [pending, pending] } };
        Assert.Throws<InvalidDataException>(() => duplicate.ValidateForActors([actor]));
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task JsonLimitsUnknownSchemaAndUnknownMembersAreRejected() => await session.Dispatch(() =>
    {
        var state = EditorRecoverySessionState.Capture(new(), new(), []);
        Assert.Throws<InvalidDataException>(() => (state with { SchemaVersion = 999 }).ToJsonBytes());
        Assert.Throws<InvalidDataException>(() => EditorRecoverySessionState.FromJsonBytes(new byte[EditorRecoverySessionState.MaximumJsonBytes + 1]));
        var text = Encoding.UTF8.GetString(state.ToJsonBytes());
        Assert.Throws<InvalidDataException>(() => EditorRecoverySessionState.FromJsonBytes(Encoding.UTF8.GetBytes(text[..^1] + ",\"Unexpected\":true}")));
        var invalid = state with { Animation = state.Animation with { TimelineRangeStartText = new('x', 4097) } };
        Assert.Throws<InvalidDataException>(() => invalid.ToJsonBytes());
        Assert.Throws<InvalidDataException>(() => (state with { ProjectFrame = 1.25 }).ToJsonBytes());
        Assert.Throws<InvalidDataException>(() => (state with { ProjectFrame = double.NaN }).ToJsonBytes());
        var fractional = EditorRecoverySessionState.FromJsonBytes((state with { ProjectFrame = .25 }).ToJsonBytes());
        Assert.Equal(.25, fractional.ProjectFrame);
        Assert.Throws<InvalidDataException>(() => (state with
            { NumericInputText = state.NumericInputText.Add("FfmpegPath", "forged") }).ToJsonBytes());
        Assert.Throws<InvalidDataException>(() => (state with
            { NumericInputText = state.NumericInputText.Add("CurrentFrameInput", new string('x', 4097)) }).ToJsonBytes());
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task UnparentedPmmCameraRetainsItsMinusOneBoneSentinelAndPublishesOnce() => await session.Dispatch(() =>
    {
        var source = new EditorSceneInspectorViewModel();
        var pose = source.CameraPose with { ParentBoneIndex = -1, ViewOffset = new(2, -3) };
        source.SetCameraDraft(pose);
        var state = EditorRecoverySessionState.Capture(new(), source, []);
        var target = new EditorSceneInspectorViewModel(); int published = 0;
        target.CameraPoseChanged += _ => published++;
        EditorRecoverySessionState.FromJsonBytes(state.ToJsonBytes()).Restore(new(), target, []);
        Assert.Equal(pose, target.CameraPose);
        Assert.Equal(1, published);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ForgedJsonCameraParentRejectsBeforeAnimationOrSceneMutation() => await session.Dispatch(() =>
    {
        var actor = new SceneActorMotion(Guid.NewGuid(), Model(), Motion());
        var source = new EditorAnimationViewModel(); source.SetActor(actor.ObjectId, actor.Model, actor.Motion);
        source.SelectBone(0); source.PositionX = 8m;
        var state = EditorRecoverySessionState.Capture(source, new(), [actor]);
        string json = Encoding.UTF8.GetString(state.ToJsonBytes());
        string forged = json.Replace("\"ParentModelIndex\":-1", "\"ParentModelIndex\":42", StringComparison.Ordinal);
        Assert.NotEqual(json, forged);
        var restored = EditorRecoverySessionState.FromJsonBytes(Encoding.UTF8.GetBytes(forged));
        var target = new EditorAnimationViewModel { CurrentFrame = 42 };
        var scene = new EditorSceneInspectorViewModel { CurrentFrame = 42, CameraCenterX = 15m };
        Assert.Throws<InvalidDataException>(() => restored.Restore(target, scene, [actor]));
        Assert.Equal(42, target.CurrentFrame); Assert.Null(target.ObjectId); Assert.False(target.HasAnyDraft);
        Assert.Equal(42, scene.CurrentFrame); Assert.Equal(15m, scene.CameraCenterX);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task FiniteNonzeroTinySampledQuaternionIsRetainedWithoutNormalization() => await session.Dispatch(() =>
    {
        var actor = new SceneActorMotion(Guid.NewGuid(), Model(), Motion());
        var source = new EditorAnimationViewModel(); source.SetActor(actor.ObjectId, actor.Model, actor.Motion);
        source.SelectBone(0); source.PositionX = 8m;
        var state = EditorRecoverySessionState.Capture(source, new(), [actor]);
        var pending = Assert.Single(state.Animation.Actors); var bone = Assert.Single(pending.Bones);
        var tiny = new Quaternion(float.Epsilon, 0, 0, 0);
        state = state with { Animation = state.Animation with
        { Actors = [pending with { Bones = [bone with { SampledRotation = tiny }] }] } };
        var target = new EditorAnimationViewModel();
        EditorRecoverySessionState.FromJsonBytes(state.ToJsonBytes()).Restore(target, new(), [actor]);
        Assert.Equal(tiny, Assert.Single(target.SnapshotDrafts()).Rotation);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ParentedCameraValidatesNativeBoneMappingBeforeAnimationRestore() => await session.Dispatch(() =>
    {
        using var playback = ResolvedOutputCameraFixture.Playback();
        var plan = playback.Plan.ConfirmRoles(new Dictionary<int, ModelPurpose> { [0] = new(ModelRole.Character) });
        using var candidate = EditorProjectInterchange.CreatePmmCandidate(plan,
            (model, token) => EditorProjectWorkspace.CreateModelAsset(model.Model, model.Path, token),
            new NoPhysics(), token: TestContext.Current.CancellationToken);
        var actors = candidate.Session.CaptureAuthoredContent().Motions.ToArray();
        var actor = Assert.Single(actors);
        var source = new EditorAnimationViewModel(); source.SetActor(actor.ObjectId, actor.Model, actor.Motion);
        source.SelectBone(0); source.PositionX = 8m;
        var sourceScene = new EditorSceneInspectorViewModel();
        sourceScene.SetPmmCameraContext(candidate.Session);
        sourceScene.SetSceneMotion(candidate.Session.CaptureAuthoredContent().SceneMotion);
        var state = EditorRecoverySessionState.Capture(source, sourceScene, actors);
        var target = new EditorAnimationViewModel { CurrentFrame = 42 };
        var targetScene = new EditorSceneInspectorViewModel();
        targetScene.SetPmmCameraContext(candidate.Session);
        targetScene.SetSceneMotion(candidate.Session.CaptureAuthoredContent().SceneMotion);
        var invalid = state with { Scene = state.Scene with
        { CameraPose = state.Scene.CameraPose with { ParentBoneIndex = 100 } } };
        Assert.Throws<InvalidDataException>(() => invalid.Restore(target, targetScene, actors));
        Assert.Equal(42, target.CurrentFrame); Assert.Null(target.ObjectId); Assert.False(target.HasAnyDraft);
        int published = 0; targetScene.CameraPoseChanged += _ => published++;
        state.Restore(target, targetScene, actors);
        Assert.Equal(8m, target.PositionX); Assert.Equal(state.Scene.CameraPose, targetScene.CameraPose);
        Assert.Equal(1, published);
        return true;
    }, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);

    [Fact]
    public async Task OverflowedLightDraftDoesNotPreventUnrelatedStateRecoveryOrPublishInvalidRadiance() => await session.Dispatch(() =>
    {
        const decimal huge = 100000000000000000000m;
        var source = new EditorSceneInspectorViewModel();
        source.LightColorR = huge;
        int invalidPublications = 0;
        source.LightPoseChanged += pose =>
        {
            if (!float.IsFinite(pose.Color.X) || !float.IsFinite(pose.Color.Y) || !float.IsFinite(pose.Color.Z)) invalidPublications++;
        };
        source.LightIntensity = huge;
        Assert.True(source.HasLightValidation);
        var state = EditorRecoverySessionState.Capture(new(), source, []);
        var restored = new EditorSceneInspectorViewModel();
        int publications = 0; restored.LightPoseChanged += _ => publications++;
        EditorRecoverySessionState.FromJsonBytes(state.ToJsonBytes()).Restore(new(), restored, []);
        Assert.Equal(huge, restored.LightColorR); Assert.Equal(huge, restored.LightIntensity);
        Assert.True(restored.HasLightDraft); Assert.True(restored.HasLightValidation);
        Assert.False(restored.CanRegisterLight); Assert.Equal(0, publications); Assert.Equal(0, invalidPublications);
        Assert.True(float.IsFinite(restored.LightPose.Color.X));
        return true;
    }, TestContext.Current.CancellationToken);

    private static VmdMotion Motion() => new()
    {
        BoneKeyframes = [new() { BoneName = "root", Frame = 10 }, new() { BoneName = "hand", Frame = 15 }],
        MorphKeyframes = [new() { MorphName = "smile", Frame = 20, Weight = .5f }]
    };

    private static PmxModel Model(string root = "root") => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "recovery-fixture", EnglishName = "", Comment = "", EnglishComment = "", Vertices = [], Faces = [], Textures = [], Materials = [],
        RigidBodies = [], Joints = [], SoftBodies = [], DisplayFrames = [],
        Bones = [Bone(root), Bone("hand")],
        Morphs = [new() { Name = "smile", EnglishName = "", Type = PmxMorphType.Position, ControlPanel = 0 }]
    };
    private static PmxBone Bone(string name) => new()
    { Name = name, EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0,
        Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate };

    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
            => throw new InvalidOperationException("The recovery camera fixture has no physics bodies.");
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings)
            => throw new InvalidOperationException("The recovery camera fixture has no physics bodies.");
    }
}
