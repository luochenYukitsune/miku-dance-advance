using System.Numerics;
using System.Reflection;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

/// <summary>Numerical-owner probes verify orchestration; these do not claim visual or Bullet equivalence.</summary>
public sealed class ProjectPoseInitializationTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "pose-initialization-" + Guid.NewGuid().ToString("N"));
    public ProjectPoseInitializationTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void LegacyProviderPreservesZeroStepPreparationSeekAndPreview()
    {
        var f = Fixture(); var factory = new BackendFactory();
        IProjectPhysicsBackendProvider provider = new LegacyProvider(factory);
        Assert.Equal(0, provider.PoseInitializationSubsteps);
        Assert.Equal(0, new ProjectSettingsBackendProvider((_, _) => factory.Create(), _ => factory.Create()).PoseInitializationSubsteps);
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, provider, initialFrame: 7.5, cancellationToken: Token);
        Assert.Equal(7.5, project.CurrentFrame); Assert.All(factory.Worlds, world => Assert.Empty(world.StepSeconds));
        project.Seek(9, Token);
        using var preview = project.CreatePosePreview(f.Motions[0].ObjectId, Motion(30, 60), Token);
        Assert.Equal(9, preview.CurrentFrame); Assert.All(factory.Worlds, world => Assert.Empty(world.StepSeconds));
        Assert.False(project.CanUndo); Assert.False(preview.CanUndo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationSeekAndPreviewUseExactSubstepsWithoutMovingTheAuthoredClock(bool shared)
    {
        const int steps = 7;
        var f = Fixture(); var factory = new BackendFactory(); var provider = factory.Provider(steps);
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, provider, Settings(shared), initialFrame: 7.5, cancellationToken: Token);
        var initialWorld = Assert.Single(factory.Worlds); AssertSteps(initialWorld, steps);
        Assert.Equal(7.5, project.CurrentFrame); Assert.Equal(7.5, Assert.Single(project.Actors).LocalFrame);
        Assert.Equal(30u, project.DurationFrames); Assert.True(project.Frame.Surface.Snapshot.Vertices[0].Position.Y < 0);
        var authored = Assert.Single(project.CaptureAuthoredContent().Motions);
        project.Seek(11, Token);
        var seekWorld = factory.Worlds[^1]; Assert.NotSame(initialWorld, seekWorld); AssertSteps(seekWorld, steps);
        Assert.Equal(11, project.CurrentFrame); Assert.Equal(11, Assert.Single(project.Actors).LocalFrame);
        var liveFrame = project.Frame;
        using var preview = project.CreatePosePreview(authored.ObjectId, Motion(30, 60), Token);
        var previewWorld = factory.Worlds[^1]; Assert.NotSame(seekWorld, previewWorld); AssertSteps(previewWorld, steps);
        Assert.Equal(11, preview.CurrentFrame); Assert.Equal(11, Assert.Single(preview.Actors).LocalFrame);
        Assert.Same(liveFrame, project.Frame); Assert.Same(authored.Motion, Assert.Single(project.CaptureAuthoredContent().Motions).Motion);
        Assert.Same(authored.Asset, Assert.Single(project.CaptureAuthoredContent().Motions).Asset);
        Assert.True(preview.Frame.Surface.Snapshot.Vertices[0].Position.X > liveFrame.Surface.Snapshot.Vertices[0].Position.X);
        preview.UpdatePosePreviewMotion(authored.ObjectId, Motion(30, 90), Token);
        AssertSteps(factory.Worlds[^1], steps); Assert.True(previewWorld.Disposed);
        Assert.Equal(11, preview.CurrentFrame); Assert.Same(liveFrame, project.Frame);
        Assert.False(project.CanUndo); Assert.False(preview.CanUndo); Assert.True(preview.CanReusePosePreview(project));
        Assert.All(factory.Worlds, world => AssertSteps(world, steps));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitializedSeekReusesPackedTexturesWithAFreshPoseAndSolver(bool shared)
    {
        const int steps = 120;
        var texture = new SceneTexture("seek", 1, 1, [1, 2, 3, 255]);
        var f = Fixture(texture: texture);
        var factory = new BackendFactory();
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, factory.Provider(steps), Settings(shared), cancellationToken: Token);
        var before = project.Frame;
        Assert.Same(texture, Assert.Single(before.Surface.Snapshot.Textures));
        var beforeGi = RayGiScene.FromOriginal(before);

        var sought = project.Seek(11, Token);
        var soughtGi = RayGiScene.FromOriginal(sought);
        Assert.Equal(11, project.CurrentFrame);
        Assert.NotEqual(before.Surface.Snapshot.GetVertex(0), sought.Surface.Snapshot.GetVertex(0));
        Assert.NotEqual(beforeGi.GeometryIdentity, soughtGi.GeometryIdentity);
        Assert.Same(before.Surface.Snapshot.Textures, sought.Surface.Snapshot.Textures);
        Assert.Equal(beforeGi.TexelIdentity, soughtGi.TexelIdentity);
        Assert.Equal(2, factory.Worlds.Count);
        Assert.True(factory.Worlds[0].Disposed);
        Assert.All(factory.Worlds, world => AssertSteps(world, steps));

        var advanced = project.Advance(1d / 30, Token);
        Assert.Equal(12, project.CurrentFrame);
        Assert.Same(sought.Surface.Snapshot.Textures, advanced.Surface.Snapshot.Textures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitializedSeekKeepsNewTextureOrderWhenVisibleActorsChange(bool sameCount)
    {
        var first = new SceneTexture("seek", 1, 1, [1, 2, 3, 255]);
        var second = new SceneTexture("seek", 1, 1, [1, 2, 3, 255]);
        var f = Fixture(twoActors: true, texture: first, secondTexture: second);
        var factory = new BackendFactory();
        var motions = f.Motions.Select((actor, index) =>
        {
            var motion = new VmdMotion
            {
                BoneKeyframes = actor.Motion.BoneKeyframes,
                IkKeyframes = [new() { Visible = index == 0 || !sameCount }, new() { Frame = 10, Visible = index == 1 }]
            };
            var asset = SceneMotionAsset.FromMotion(motion);
            return actor with { Motion = asset.Motion, Asset = asset };
        }).ToArray();
        using var project = new SceneProjectSession(f.Layout, new(), motions, factory.Provider(4), Settings(true), cancellationToken: Token);
        var before = project.Frame;
        Assert.Equal(sameCount ? new[] { first } : new[] { first, second }, before.Surface.Snapshot.Textures);

        var sought = project.Seek(10, Token);
        Assert.Same(second, Assert.Single(sought.Surface.Snapshot.Textures));
        Assert.NotSame(before.Surface.Snapshot.Textures, sought.Surface.Snapshot.Textures);
        Assert.NotEqual(RayGiScene.FromOriginal(before).TexelIdentity, RayGiScene.FromOriginal(sought).TexelIdentity);
    }

    [Fact]
    public void InitializedSeekReadsPublishedTexturesWithoutCompletingPendingPreview()
    {
        var texture = new SceneTexture("seek", 1, 1, [1, 2, 3, 255]);
        var f = Fixture(twoActors: true, texture: texture);
        var factory = new BackendFactory();
        f.Motions[1] = f.Motions[1] with { PhysicsEnabled = false };
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, factory.Provider(4), Settings(true), cancellationToken: Token);
        var before = project.Frame;
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var live = typeof(SceneProjectSession).GetField("animation", fields)!.GetValue(project);
        var completed = new List<Guid>();
        typeof(SceneAnimationSession).GetField("PreviewLeafObserverForTesting", fields)!.SetValue(live, (Action<Guid>)completed.Add);
        project.AdvancePreview(1d / 30, Token);
        Assert.Empty(completed);

        factory.ConfigureNext = world => world.FailAtStep = 2;
        Assert.ThrowsAny<InvalidOperationException>(() => project.Seek(10, Token));
        Assert.Equal(1, project.CurrentFrame);
        Assert.Empty(completed);

        var sought = project.Seek(10, Token);
        Assert.Equal(10, project.CurrentFrame);
        Assert.Empty(completed);
        Assert.Same(before.Surface.Snapshot.Textures, sought.Surface.Snapshot.Textures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedSeekIsDeterministicAndPreservesMotionAndUndoRedo(bool shared)
    {
        const int steps = 5;
        var f = Fixture(); var factory = new BackendFactory();
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, factory.Provider(steps), Settings(shared), initialFrame: 4, cancellationToken: Token);
        var original = Assert.Single(project.CaptureAuthoredContent().Motions);
        var replacement = SceneMotionAsset.FromMotion(Motion(30, 60), "edited.vmd");
        project.Edit(edit => edit.Motions[0] = edit.Motions[0] with { Motion = replacement.Motion, Asset = replacement }, Token);
        project.Seek(12, Token); var expected = project.Frame.Surface.Snapshot.Vertices.ToArray();
        project.Advance(1d / 30, Token); project.Seek(12, Token);
        Assert.Equal(expected, project.Frame.Surface.Snapshot.Vertices.ToArray()); Assert.Equal(12, project.CurrentFrame);
        Assert.Same(replacement, Assert.Single(project.CaptureAuthoredContent().Motions).Asset); Assert.True(project.CanUndo); Assert.False(project.CanRedo);
        Assert.True(project.Undo(Token)); Assert.Equal(12, project.CurrentFrame);
        Assert.Same(original.Motion, Assert.Single(project.CaptureAuthoredContent().Motions).Motion);
        Assert.Same(original.Asset, Assert.Single(project.CaptureAuthoredContent().Motions).Asset);
        Assert.True(project.CanRedo); Assert.NotEqual(expected[0].Position.X, project.Frame.Surface.Snapshot.Vertices[0].Position.X);
        Assert.True(project.Redo(Token)); Assert.Equal(12, project.CurrentFrame);
        Assert.Same(replacement, Assert.Single(project.CaptureAuthoredContent().Motions).Asset);
        Assert.Equal(expected, project.Frame.Surface.Snapshot.Vertices.ToArray());
        // Only the first seek's world was subsequently played. The final replacement worlds each
        // received precisely the requested initialization, including Undo and Redo candidates.
        AssertSteps(factory.Worlds[0], steps); AssertSteps(factory.Worlds[1], steps);
        AssertSteps(factory.Worlds[^3], steps); AssertSteps(factory.Worlds[^2], steps); AssertSteps(factory.Worlds[^1], steps);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedOrCancelledInitializationRetainsLiveSolverClockAndBothHistoryStacks(bool shared, bool cancel)
    {
        var f = Fixture(); var factory = new BackendFactory();
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, factory.Provider(6), Settings(shared), initialFrame: 3, cancellationToken: Token);
        project.Edit(edit => edit.Composition.SetTransform(f.Motions[0].ObjectId, Vector3.UnitX, Quaternion.Identity, 1), Token);
        project.Edit(edit => edit.Composition.SetTransform(f.Motions[0].ObjectId, 2 * Vector3.UnitX, Quaternion.Identity, 1), Token);
        Assert.True(project.Undo(Token)); Assert.True(project.CanUndo); Assert.True(project.CanRedo);
        var before = project.Frame; var live = factory.Worlds[^1]; int liveSteps = live.StepSeconds.Count;
        var authored = Assert.Single(project.CaptureAuthoredContent().Motions);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        factory.ConfigureNext = world =>
        {
            if (cancel) world.AfterStep = _ => cancellation.Cancel();
            else world.FailAtStep = 2;
        };
        if (cancel) Assert.ThrowsAny<OperationCanceledException>(() => project.Seek(15, cancellation.Token));
        else Assert.ThrowsAny<InvalidOperationException>(() => project.Seek(15, Token));
        var rejected = factory.Worlds[^1]; Assert.NotSame(live, rejected); Assert.True(rejected.Disposed);
        Assert.False(live.Disposed); Assert.Equal(liveSteps, live.StepSeconds.Count);
        Assert.Same(before, project.Frame); Assert.Equal(3, project.CurrentFrame);
        Assert.Same(authored.Motion, Assert.Single(project.CaptureAuthoredContent().Motions).Motion);
        Assert.True(project.CanUndo); Assert.True(project.CanRedo);
        project.Advance(1d / 120, Token); Assert.Equal(liveSteps + 1, live.StepSeconds.Count);
        Assert.True(project.Undo(Token)); Assert.True(project.Redo(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedPreviewReplacementRetainsSourceAndLastPublishedDraft(bool cancel)
    {
        var f = Fixture(); var factory = new BackendFactory();
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, factory.Provider(4), initialFrame: 10, cancellationToken: Token);
        using var preview = project.CreatePosePreview(f.Motions[0].ObjectId, Motion(30, 60), Token);
        var liveFrame = project.Frame; var draftFrame = preview.Frame; var draft = Assert.Single(preview.CaptureAuthoredContent().Motions).Motion;
        var liveWorld = factory.Worlds[0]; var draftWorld = factory.Worlds[1];
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        factory.ConfigureNext = world =>
        {
            if (cancel) world.AfterStep = _ => cancellation.Cancel();
            else world.FailAtStep = 1;
        };
        if (cancel) Assert.ThrowsAny<OperationCanceledException>(() => preview.UpdatePosePreviewMotion(f.Motions[0].ObjectId, Motion(30, 90), cancellation.Token));
        else Assert.ThrowsAny<InvalidOperationException>(() => preview.UpdatePosePreviewMotion(f.Motions[0].ObjectId, Motion(30, 90), Token));
        Assert.Same(liveFrame, project.Frame); Assert.Same(draftFrame, preview.Frame);
        Assert.Same(draft, Assert.Single(preview.CaptureAuthoredContent().Motions).Motion);
        Assert.False(liveWorld.Disposed); Assert.False(draftWorld.Disposed); Assert.True(factory.Worlds[^1].Disposed);
        Assert.Equal(10, project.CurrentFrame); Assert.Equal(10, preview.CurrentFrame); Assert.True(preview.CanReusePosePreview(project));
        Assert.False(project.CanUndo); Assert.False(preview.CanUndo);
    }

    [Fact]
    public void SharedZeroDurationActorsInitializeTogetherEvenAtTheirFinalPose()
    {
        const int steps = 8;
        var f = Fixture(end: 0, twoActors: true); var world = new ProbeBackend();
        using var session = new SceneAnimationSession(f.Layout.Objects, f.Motions, _ => throw new InvalidOperationException("Expected one shared world"),
            initialFrame: 12, sharedPhysicsFactory: () => world, cancellationToken: Token);
        Assert.Equal(0u, session.DurationFrames); Assert.Equal(0, session.SharedPhysicsStepCount);
        var before = session.Frame.Surface.Snapshot.Vertices.ToArray();
        session.StabilizePhysicsAtCurrentFrame(steps, Token);
        AssertSteps(world, steps); Assert.Equal(steps, session.SharedPhysicsStepCount);
        Assert.Equal(12, session.CurrentFrame); Assert.All(session.Actors, actor => Assert.Equal(0, actor.LocalFrame));
        Assert.All(world.DynamicBodyCounts, count => Assert.Equal(2, count));
        Assert.True(session.Frame.Surface.Snapshot.Vertices[0].Position.Y < before[0].Position.Y);
        Assert.True(session.Frame.Surface.Snapshot.Vertices[3].Position.Y < before[3].Position.Y);
        var final = session.Frame; Assert.Same(final, session.Advance(1d / 120, Token)); AssertSteps(world, steps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitializationDoesNotReplayImpulseMorphsAndRegularPlaybackStillAppliesThem(bool shared)
    {
        const int steps = 9;
        var f = Fixture(impulse: true); var factory = new BackendFactory();
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, factory.Provider(steps), Settings(shared), initialFrame: 5, cancellationToken: Token);
        var world = Assert.Single(factory.Worlds); AssertSteps(world, steps); Assert.Equal(0, world.ImpulseCount);
        project.Advance(1d / 120, Token); Assert.Equal(1, world.ImpulseCount); Assert.Equal(steps + 1, world.StepSeconds.Count);
        project.Seek(5, Token); AssertSteps(factory.Worlds[^1], steps); Assert.Equal(0, factory.Worlds[^1].ImpulseCount);
        using var preview = project.CreatePosePreview(f.Motions[0].ObjectId, f.Motions[0].Motion, Token);
        AssertSteps(factory.Worlds[^1], steps); Assert.Equal(0, factory.Worlds[^1].ImpulseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PmmControlledWorldsKeepTargetFlagsAndGravityDuringInitialization(bool shared)
    {
        const int steps = 8;
        var f = Fixture(twoActors: true); var controls = Controls(f); var factory = new BackendFactory();
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, factory.Provider(steps), Settings(shared),
            initialFrame: 8, pmmControls: controls, cancellationToken: Token);
        Assert.Equal(shared ? 1 : 2, factory.Worlds.Count);
        var expectedGravity = controls.Sample(8).Gravity.WorldGravity;
        Assert.All(factory.Worlds, world =>
        {
            AssertSteps(world, steps); Assert.All(world.GravitySamples, gravity => Assert.Equal(expectedGravity, gravity));
            Assert.All(world.DynamicBodyCounts, count => Assert.Equal(shared ? 2 : 1, count));
        });
        Assert.Equal(8, project.CurrentFrame); Assert.All(project.Actors, actor => Assert.Equal(8, actor.LocalFrame));
        Assert.Equal(controls.Sha256, project.PmmControls!.Sha256); Assert.False(project.CanUndo);
        var frame = project.Frame.Surface.Snapshot.Vertices.ToArray(); project.Seek(8, Token);
        Assert.Equal(frame, project.Frame.Surface.Snapshot.Vertices.ToArray()); Assert.Equal(controls.Sha256, project.PmmControls!.Sha256);
        Assert.All(factory.Worlds, world => AssertSteps(world, steps));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReopenedProjectUsesTheCurrentHostPolicyExactlyOnce(bool shared)
    {
        var f = Fixture(); var originalFactory = new BackendFactory();
        using var original = new SceneProjectSession(f.Layout, new(), f.Motions, originalFactory.Provider(3), Settings(shared), initialFrame: 7.5, cancellationToken: Token);
        string path = Path.Combine(directory, "saved.mmdscene"); original.Save(path, new() { CopyModelAssets = false }, Token);
        var factory = new BackendFactory();
        using var reopened = SceneProjectSession.Load(path, (_, _) => new(f.Motions[0].Model, f.Layout.Objects[0].Source), factory.Provider(6), Token);
        AssertSteps(Assert.Single(factory.Worlds), 6); Assert.Equal(7.5, reopened.CurrentFrame);
        Assert.Equal(7.5, Assert.Single(reopened.Actors).LocalFrame); Assert.False(reopened.CanUndo);
        Assert.Equal(f.Motions[0].Asset!.Sha256, Assert.Single(reopened.CaptureAuthoredContent().Motions).Asset!.Sha256);
        var zeroFactory = new BackendFactory();
        using var zero = SceneProjectSession.Load(path, (_, _) => new(f.Motions[0].Model, f.Layout.Objects[0].Source), new LegacyProvider(zeroFactory), Token);
        Assert.Empty(Assert.Single(zeroFactory.Worlds).StepSeconds); Assert.Equal(7.5, zero.CurrentFrame);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(241)]
    [InlineData(int.MaxValue)]
    public void InvalidHostAndDirectBudgetsRejectBeforeChangingSolverOrFrame(int steps)
    {
        var factory = new BackendFactory(); Assert.Throws<ArgumentOutOfRangeException>(() => factory.Provider(steps)); Assert.Empty(factory.Worlds);
        var f = Fixture(); var world = new ProbeBackend();
        using var session = new SceneAnimationSession(f.Layout.Objects, f.Motions, _ => world, cancellationToken: Token);
        var before = session.Frame;
        Assert.Throws<ArgumentOutOfRangeException>(() => session.StabilizePhysicsAtCurrentFrame(steps, Token));
        Assert.Same(before, session.Frame); Assert.Empty(world.StepSeconds); Assert.False(session.IsFaulted); Assert.Equal(0, session.CurrentFrame);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(240)]
    public void HostAcceptsBothBudgetEndpointsAndRunsOnlyTheRequestedSteps(int steps)
    {
        var f = Fixture(end: 0); var factory = new BackendFactory(); var provider = factory.Provider(steps);
        Assert.Equal(steps, provider.PoseInitializationSubsteps);
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, provider, cancellationToken: Token);
        AssertSteps(Assert.Single(factory.Worlds), steps); Assert.Equal(0, project.CurrentFrame);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdditionalLightEditsAndHistoryRetainTheSolvedPoseAndFractionalPhysicsProgress(bool shared)
    {
        var f = Fixture(); var factory = new BackendFactory();
        var light = new NativeSceneLight(Guid.NewGuid(), "key", SceneLightKind.Point, new(0, 4, 0), Quaternion.Identity, Vector3.One);
        var lights = new SceneLightingEditor(); lights.Add(light);
        using var project = new SceneProjectSession(f.Layout, lights, f.Motions, factory.Provider(6), Settings(shared), initialFrame: 7.5, cancellationToken: Token);
        project.Advance(1d / 240, Token); // Half a fixed step must survive every lighting/history update.
        var world = Assert.Single(factory.Worlds); int steps = world.StepSeconds.Count;
        var before = project.Frame; var actors = project.Actors;
        var changed = light with { Color = new(.2f, .4f, .8f), Intensity = 3, Position = new(2, 8, 1), Range = 20 };
        project.Edit(edit => edit.Lighting.Update(changed), Token);
        var after = project.Frame;
        Assert.Same(before.Surface, after.Surface); Assert.Same(before.Outline, after.Outline);
        Assert.Equal(light, Assert.Single(before.Lighting!.Lights));
        Assert.Same(actors, project.Actors); Assert.Equal(7.625, project.CurrentFrame);
        Assert.Equal(changed, Assert.Single(after.Lighting!.Lights));
        Assert.Same(after, project.CaptureRenderSnapshot().Scene);
        Assert.True(project.Undo(Token));
        Assert.Equal(light, Assert.Single(project.Frame.Lighting!.Lights));
        Assert.Same(before.Surface, project.Frame.Surface);
        Assert.True(project.Redo(Token));
        Assert.Equal(changed, Assert.Single(project.CaptureRenderSnapshot().Scene.Lighting!.Lights));
        project.SetDefaultRenderingStyle(RenderingStyleId.Genshin, Token);
        Assert.True(project.Undo(Token)); Assert.True(project.Redo(Token));
        Assert.Equal(changed, Assert.Single(project.Frame.Lighting!.Lights));
        Assert.Single(factory.Worlds); Assert.False(world.Disposed); Assert.Equal(steps, world.StepSeconds.Count);

        project.AdvancePreview(1d / 240, Token);
        var sampled = project.CaptureRenderSnapshot();
        Assert.Equal(7.75, sampled.SourceFrame); Assert.Equal(steps + 1, world.StepSeconds.Count);
        Assert.Equal(changed, Assert.Single(sampled.Scene.Lighting!.Lights));
        Assert.Single(factory.Worlds); Assert.False(world.Disposed);
        Assert.True(sampled.Scene.Surface.Snapshot.Vertices[0].Position.Y < after.Surface.Snapshot.Vertices[0].Position.Y);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlobalLightingOnlyEditsAndHistoryRetainSolvedPoseAndFractionalPhysicsProgress(bool shared)
    {
        var f = Fixture(); var factory = new BackendFactory();
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, factory.Provider(6), Settings(shared),
            initialFrame: 7.5, cancellationToken: Token);
        project.Advance(1d / 240, Token); var before = project.Frame; var actors = project.Actors;
        var world = Assert.Single(factory.Worlds); int steps = world.StepSeconds.Count;
        var settings = new SceneLightingSettings(.75f, -2.5f);
        project.Edit(edit => edit.Lighting.UpdateSettings(settings), Token);
        var changed = project.CaptureRenderSnapshot().Scene;
        Assert.Equal(settings, changed.Lighting!.Settings); Assert.Equal(SceneLightingSettings.Default, before.Lighting!.Settings);
        Assert.Same(before.Surface, changed.Surface); Assert.Same(actors, project.Actors); Assert.Equal(7.625, project.CurrentFrame);
        Assert.True(project.Undo(Token)); Assert.Equal(SceneLightingSettings.Default, project.Frame.Lighting!.Settings);
        Assert.True(project.Redo(Token)); Assert.Equal(settings, project.Frame.Lighting!.Settings);
        Assert.Single(factory.Worlds); Assert.False(world.Disposed); Assert.Equal(steps, world.StepSeconds.Count);
        string path = Path.Combine(directory, "global-lighting.mmdscene"); project.Save(path, new() { CopyModelAssets = false }, Token);
        project.AdvancePreview(1d / 240, Token);
        Assert.Equal(7.75, project.CurrentFrame); Assert.Equal(steps + 1, world.StepSeconds.Count);
        Assert.Equal(settings, project.CaptureRenderSnapshot().Scene.Lighting!.Settings); Assert.Single(factory.Worlds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupOnlyEditsAndHistoryRetainSolvedPoseAndFractionalPhysicsProgress(bool shared)
    {
        var f = Fixture(); var factory = new BackendFactory();
        var group = new SceneLightGroup(Guid.NewGuid(), "人物");
        var light = new NativeSceneLight(Guid.NewGuid(), "key", SceneLightKind.Point, Vector3.UnitY,
            Quaternion.Identity, Vector3.One, Intensity: 2, GroupId: group.Id);
        var lighting = new SceneLightingEditor(); lighting.ReplaceSnapshot(new([light], [group]), light.Id);
        using var project = new SceneProjectSession(f.Layout, lighting, f.Motions, factory.Provider(6), Settings(shared),
            initialFrame: 7.5, cancellationToken: Token);
        project.Advance(1d / 240, Token);
        var world = Assert.Single(factory.Worlds); int steps = world.StepSeconds.Count;
        var before = project.Frame; var actors = project.Actors; SceneLightingEditor? escaped = null;
        var changed = group with { Name = "舞台", Enabled = false, Intensity = 3 };
        project.Edit(edit => { escaped = edit.Lighting; edit.Lighting.UpdateGroup(changed); }, Token);
        escaped!.RemoveGroup(group.Id);
        var after = project.CaptureRenderSnapshot().Scene;
        Assert.Equal(group, Assert.Single(before.Lighting!.Groups));
        Assert.Equal(changed, Assert.Single(after.Lighting!.Groups));
        Assert.Equal(light, Assert.Single(after.Lighting.Lights));
        var effective = Assert.Single(after.Lighting.EffectiveLights); Assert.False(effective.Enabled); Assert.Equal(6, effective.Intensity);
        Assert.Same(before.Surface, after.Surface); Assert.Same(before.Outline, after.Outline);
        Assert.Same(actors, project.Actors); Assert.Equal(7.625, project.CurrentFrame);
        Assert.True(project.Undo(Token)); Assert.Equal(group, Assert.Single(project.Frame.Lighting!.Groups));
        Assert.True(Assert.Single(project.Frame.Lighting.EffectiveLights).Enabled);
        Assert.True(project.Redo(Token)); Assert.Equal(changed, Assert.Single(project.Frame.Lighting!.Groups));
        Assert.Single(factory.Worlds); Assert.False(world.Disposed); Assert.Equal(steps, world.StepSeconds.Count);
        string saved = Path.Combine(directory, "group-selection.mmdscene");
        project.Save(saved, new() { CopyModelAssets = false }, Token);
        using (var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(saved)))
            Assert.Equal(light.Id, json.RootElement.GetProperty("layout").GetProperty("selectedLightId").GetGuid());
        project.AdvancePreview(1d / 240, Token);
        Assert.Equal(7.75, project.CurrentFrame); Assert.Equal(steps + 1, world.StepSeconds.Count);
        Assert.Equal(changed, Assert.Single(project.CaptureRenderSnapshot().Scene.Lighting!.Groups));
        Assert.Single(factory.Worlds); Assert.False(world.Disposed);
    }

    [Fact]
    public void LightPublicationKeepsFrozenGeometryCapturesAndSavedAnimationInputsConsistent()
    {
        var f = Fixture(end: 0); var factory = new BackendFactory();
        using var project = new SceneProjectSession(f.Layout, new(), [f.Motions[0] with { PhysicsEnabled = false }],
            factory.Provider(0), cancellationToken: Token);
        var before = project.Frame;
        var light = new NativeSceneLight(Guid.NewGuid(), "fill", SceneLightKind.Point, Vector3.UnitY, Quaternion.Identity, Vector3.One);
        project.Edit(edit => edit.Lighting.Add(light), Token);
        var captured = project.CaptureRenderSnapshot();
        Assert.Empty(before.Lighting!.Lights); Assert.Same(before.Surface, captured.Scene.Surface);
        Assert.Same(captured.Scene, project.Advance(1d / 30, Token));
        Assert.Equal(light, Assert.Single(captured.Scene.Lighting!.Lights));
        var changed = light with { Intensity = 2, Position = Vector3.UnitX };
        project.Edit(edit => edit.Lighting.Update(changed), Token);
        Assert.Equal(light, Assert.Single(captured.Scene.Lighting!.Lights));
        Assert.Equal(changed, Assert.Single(project.CaptureRenderSnapshot().Scene.Lighting!.Lights));
        string path = Path.Combine(directory, "lights.mmdscene"); project.Save(path, cancellationToken: Token);
        using var loaded = SceneProjectSession.Load(path, (_, _) => new(f.Motions[0].Model, f.Layout.Objects[0].Source), factory.Provider(0), Token);
        Assert.Equal(changed, Assert.Single(loaded.Lighting.Lights));
        Assert.Equal(changed, Assert.Single(loaded.CaptureRenderSnapshot().Scene.Lighting!.Lights));
        Assert.Empty(factory.Worlds);
    }

    [Fact]
    public void LightHistorySurvivesSharedSeekCompositionWithOnlyStaticObjects()
    {
        var f = Fixture(); var factory = new BackendFactory();
        using var project = new SceneProjectSession(f.Layout, new(), [], factory.Provider(0), Settings(shared: true), cancellationToken: Token);
        var before = project.Frame;
        var light = new NativeSceneLight(Guid.NewGuid(), "static fill", SceneLightKind.Point, Vector3.UnitY, Quaternion.Identity, Vector3.One);
        project.Edit(edit => edit.Lighting.Add(light), Token);
        var lit = project.Frame;
        // Shared Seek always composes, including an unchanged static scene with no controllers.
        // The composition cache must retain the current baseline's lights with its geometry.
        Assert.Same(lit, project.Seek(7, Token));
        Assert.Same(before.Surface, project.Frame.Surface);
        Assert.Equal(light, Assert.Single(project.CaptureRenderSnapshot().Scene.Lighting!.Lights));
        Assert.True(project.Undo(Token)); var unlit = project.Frame;
        Assert.Same(unlit, project.Seek(0, Token)); Assert.Empty(project.Frame.Lighting!.Lights);
        Assert.True(project.Redo(Token)); var relit = project.Frame;
        Assert.Same(relit, project.Seek(3, Token));
        Assert.Equal(light, Assert.Single(project.CaptureRenderSnapshot().Scene.Lighting!.Lights));
        Assert.Empty(before.Lighting!.Lights);
        var world = Assert.Single(factory.Worlds); Assert.Empty(world.StepSeconds); Assert.False(world.Disposed);
    }

    [Fact]
    public void GlobalLightingSurvivesStaticSharedCompositionAndHistory()
    {
        var f = Fixture(); var factory = new BackendFactory();
        using var project = new SceneProjectSession(f.Layout, new(), [], factory.Provider(0), Settings(shared: true), cancellationToken: Token);
        var before = project.Frame; var settings = new SceneLightingSettings(.25f, -1);
        project.Edit(edit => edit.Lighting.UpdateSettings(settings), Token); var changed = project.Frame;
        Assert.Same(changed, project.Seek(7, Token)); Assert.Equal(settings, project.Frame.Lighting!.Settings);
        Assert.Same(before.Surface, changed.Surface); Assert.Equal(SceneLightingSettings.Default, before.Lighting!.Settings);
        Assert.True(project.Undo(Token)); var neutral = project.Frame;
        Assert.Same(neutral, project.Seek(0, Token)); Assert.Equal(SceneLightingSettings.Default, neutral.Lighting!.Settings);
        Assert.True(project.Redo(Token)); var restored = project.Frame;
        Assert.Same(restored, project.Seek(3, Token)); Assert.Equal(settings, project.Frame.Lighting!.Settings);
        var world = Assert.Single(factory.Worlds); Assert.Empty(world.StepSeconds); Assert.False(world.Disposed);
    }

    [Fact]
    public void LightUpdatesSurviveEmptyCompositionHiddenPlaybackAndReappearance()
    {
        var f = Fixture(); var factory = new BackendFactory();
        var asset = SceneMotionAsset.FromMotion(new()
        {
            BoneKeyframes = f.Motions[0].Motion.BoneKeyframes,
            IkKeyframes = [new() { Frame = 0, Visible = false }, new() { Frame = 10, Visible = true }, new() { Frame = 20, Visible = false }]
        });
        var motion = f.Motions[0] with { Motion = asset.Motion, Asset = asset, PhysicsEnabled = false };
        using var project = new SceneProjectSession(f.Layout, new(), [motion], factory.Provider(0), initialFrame: 5, cancellationToken: Token);
        var light = new NativeSceneLight(Guid.NewGuid(), "hidden fill", SceneLightKind.Point, Vector3.UnitY, Quaternion.Identity, Vector3.One);
        project.Edit(edit => edit.Lighting.Add(light), Token); var hidden = project.Frame;
        var nextHidden = project.Seek(6, Token);
        Assert.NotSame(hidden, nextHidden); Assert.True(nextHidden.Surface.Snapshot.IsEmpty);
        Assert.Equal(light, Assert.Single(nextHidden.Lighting!.Lights));
        Assert.False(project.Seek(10, Token).Surface.Snapshot.IsEmpty);
        project.AdvancePreview(1d / 30, Token);
        Assert.Equal(light, Assert.Single(project.CaptureRenderSnapshot().Scene.Lighting!.Lights));
        var changed = light with { Intensity = 3, Color = Vector3.UnitX };
        project.Edit(edit => edit.Lighting.Update(changed), Token);
        Assert.True(project.Seek(20, Token).Surface.Snapshot.IsEmpty);
        Assert.True(project.Advance(1d / 30, Token).Surface.Snapshot.IsEmpty);
        Assert.Equal(changed, Assert.Single(project.CaptureRenderSnapshot().Scene.Lighting!.Lights));
        Assert.True(project.Undo(Token));
        Assert.False(project.Seek(10, Token).Surface.Snapshot.IsEmpty);
        Assert.Equal(light, Assert.Single(project.CaptureRenderSnapshot().Scene.Lighting!.Lights));
        Assert.Equal(light, Assert.Single(nextHidden.Lighting!.Lights)); Assert.Empty(factory.Worlds);
    }

    [Theory]
    [InlineData("placement")]
    [InlineData("motion")]
    [InlineData("physics")]
    [InlineData("purpose")]
    public void LightTransactionsThatAlsoChangeAnimationOrShadingKeepCompleteCandidatePreparation(string change)
    {
        var f = Fixture(); var factory = new BackendFactory();
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, factory.Provider(5), initialFrame: 7.5, cancellationToken: Token);
        var oldWorld = Assert.Single(factory.Worlds);
        var light = new NativeSceneLight(Guid.NewGuid(), "fill", SceneLightKind.Point, Vector3.UnitY, Quaternion.Identity, Vector3.One);
        var replacement = SceneMotionAsset.FromMotion(Motion(30, 60));
        project.Edit(edit =>
        {
            edit.Lighting.Add(light);
            switch (change)
            {
                case "placement": edit.Composition.SetTransform(f.Motions[0].ObjectId, Vector3.UnitX, Quaternion.Identity, 1); break;
                case "motion": edit.Motions[0] = edit.Motions[0] with { Motion = replacement.Motion, Asset = replacement }; break;
                case "physics": edit.Settings = edit.Settings with { Physics = edit.Settings.Physics with { GravityY = -20 } }; break;
                case "purpose": edit.Lighting.SetModelRole(project.Objects[0].Key, ModelRole.Environment, true); break;
            }
        }, Token);
        Assert.Equal(2, factory.Worlds.Count); Assert.True(oldWorld.Disposed);
        AssertSteps(factory.Worlds[1], 5); Assert.Equal(7.5, project.CurrentFrame);
        Assert.Equal(light, Assert.Single(project.CaptureRenderSnapshot().Scene.Lighting!.Lights));
        Assert.True(project.Undo(Token)); Assert.Equal(3, factory.Worlds.Count); AssertSteps(factory.Worlds[2], 5);
        Assert.Empty(project.Frame.Lighting!.Lights);
        Assert.True(project.Redo(Token)); Assert.Equal(4, factory.Worlds.Count); AssertSteps(factory.Worlds[3], 5);
        Assert.Equal(light, Assert.Single(project.Frame.Lighting!.Lights));
    }

    [Fact]
    public void RejectedCancelledAndEscapedLightEditsRetainThePublishedFrameAndHistory()
    {
        var f = Fixture(); var factory = new BackendFactory();
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, factory.Provider(5), cancellationToken: Token);
        var light = new NativeSceneLight(Guid.NewGuid(), "fill", SceneLightKind.Point, Vector3.UnitY, Quaternion.Identity, Vector3.One);
        project.Edit(edit => edit.Lighting.Add(light), Token); Assert.True(project.Undo(Token));
        var before = project.Frame; var world = Assert.Single(factory.Worlds);
        Assert.Throws<ArgumentException>(() => project.Edit(edit => edit.Lighting.Add(light with { Intensity = -1 }), Token));
        Assert.Throws<InvalidOperationException>(() => project.Edit(edit =>
        { edit.Lighting.Add(light); edit.Lighting.BeginEdit(); }, Token));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Assert.Throws<OperationCanceledException>(() => project.Edit(edit =>
        { edit.Lighting.Add(light); cancellation.Cancel(); }, cancellation.Token));
        Assert.Same(before, project.Frame); Assert.True(project.CanRedo); Assert.False(project.CanUndo);
        SceneProjectEdit? escaped = null;
        project.Edit(edit => { escaped = edit; edit.Lighting.Add(light); }, Token);
        escaped!.Lighting.Update(light with { Intensity = 99 });
        Assert.Equal(light, Assert.Single(project.Lighting.Lights));
        Assert.Equal(light, Assert.Single(project.CaptureRenderSnapshot().Scene.Lighting!.Lights));
        Assert.False(project.CanRedo); Assert.Single(factory.Worlds); AssertSteps(world, 5); Assert.False(world.Disposed);
    }

    private (SceneCompositionEditor Layout, SceneActorMotion[] Motions) Fixture(uint end = 30, bool twoActors = false, bool impulse = false,
        SceneTexture? texture = null, SceneTexture? secondTexture = null)
    {
        var model = SharedPhysicsFixture.Model();
        if (impulse)
            model = new()
            {
                Header = new() { Version = 2.1f, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                    VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
                Name = model.Name, EnglishName = model.EnglishName, Comment = model.Comment, EnglishComment = model.EnglishComment,
                Vertices = model.Vertices, Faces = model.Faces, Textures = model.Textures, Materials = model.Materials, Bones = model.Bones,
                RigidBodies = model.RigidBodies, Joints = model.Joints, SoftBodies = model.SoftBodies, DisplayFrames = model.DisplayFrames,
                Morphs = [new() { Name = "push", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Impulse,
                    ImpulseOffsets = [new(0, false, Vector3.UnitX, Vector3.Zero)] }]
            };
        var layout = new SceneCompositionEditor(); var bindings = new List<SceneActorMotion>();
        for (int i = 0; i < (twoActors ? 2 : 1); i++)
        {
            string path = Path.Combine(directory, "actor-" + i + ".pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
            var rest = SharedPhysicsFixture.Rest(model);
            if ((i == 0 ? texture : secondTexture) is { } image)
            {
                var source = rest.Surface.Snapshot;
                var snapshot = new SceneSnapshot(source.Vertices, source.Indices,
                    source.Materials.Select(material => material with { DiffuseTexture = 0 }), [image],
                    source.World, source.Camera, source.LightDirection, source.AdditionalUvs, source.RawRadialSeeds);
                rest = new(new(snapshot, rest.Surface.Materials, rest.Surface.Profile), rest.Outline, rest.OutlineOptions, materials: rest.Materials);
            }
            var actor = layout.Add("actor" + i, path, ModelRole.Character, rest);
            if (i != 0) layout.SetTransform(actor.Id, new(20, 0, 0), Quaternion.Identity, 1);
            var motion = Motion(end, end, impulse); var asset = SceneMotionAsset.FromMotion(motion);
            bindings.Add(new(actor.Id, model, asset.Motion, Asset: asset));
        }
        return (layout, bindings.ToArray());
    }

    private static VmdMotion Motion(uint end, float x, bool impulse = false) => new()
    {
        BoneKeyframes = end == 0 ? [new() { BoneName = "root" }] :
            [new() { BoneName = "root" }, new() { BoneName = "root", Frame = end, Translation = new(x, 0, 0) }],
        MorphKeyframes = impulse ? [new() { MorphName = "push", Weight = 1 }] : []
    };

    private static ProjectSettings Settings(bool shared) => new() { Physics = new() { SharedWorld = shared, GroundEnabled = false } };
    private static void AssertSteps(ProbeBackend world, int steps)
    {
        Assert.Equal(steps, world.StepSeconds.Count);
        Assert.All(world.StepSeconds, seconds => Assert.Equal(1f / 120, seconds));
    }

    private static ScenePmmControlState Controls((SceneCompositionEditor Layout, SceneActorMotion[] Motions) f)
    {
        PmmString S(string value) => new(0, value, []);
        byte[] linear = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(value => value).ToArray();
        var models = f.Motions.Select((binding, i) => new PmmModel
        {
            Index = (byte)i, Name = S("actor" + i), Path = S("actor-" + i + ".pmx"), BoneNames = [S("root")],
            SelectedMorphIndices = [-1, -1, -1, -1], Visible = true, LastFrame = 30,
            InitialBoneKeyframes = [new() { ObjectIndex = 0, NextIndex = 1, Rotation = Quaternion.Identity, Interpolation = linear }],
            BoneKeyframes = [new() { ObjectIndex = 1, PreviousIndex = 0, NextIndex = 2, Frame = 9, Rotation = Quaternion.Identity, Interpolation = linear, PhysicsDisabled = true },
                new() { ObjectIndex = 2, PreviousIndex = 1, Frame = 30, Rotation = Quaternion.Identity, Interpolation = linear }],
            InitialModelKeyframe = new() { Visible = true },
            BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)]
        }).ToArray();
        var document = new PmmDocument
        {
            AnsiCodePage = 932, PanelStates = new byte[7], Models = models, OutputWidth = 640, OutputHeight = 480, CameraFov = 30,
            Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
            Settings = new() { PhysicsSimulationMode = 1, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], SelectionTrailerFlag = 0 },
            Gravity = new() { InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY, NextIndex = 1 },
                Keyframes = [new() { ObjectIndex = 1, PreviousIndex = 0, Frame = 20, Acceleration = 3, Direction = Vector3.UnitX }] }
        };
        return ScenePmmControlState.Create(document, f.Motions.Select((binding, i) => new ScenePmmActorSource(binding.ObjectId, (byte)i, binding.Model)));
    }

    private sealed class LegacyProvider(BackendFactory factory) : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item) => factory.Create();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => factory.Create();
    }

    private sealed class BackendFactory
    {
        internal readonly List<ProbeBackend> Worlds = [];
        internal Action<ProbeBackend>? ConfigureNext;
        internal ProjectSettingsBackendProvider Provider(int steps) => new((_, _) => Create(), _ => Create(), poseInitializationSubsteps: steps);
        internal ProbeBackend Create()
        {
            var world = new ProbeBackend(); var configure = ConfigureNext; ConfigureNext = null;
            configure?.Invoke(world); Worlds.Add(world); return world;
        }
    }

    private sealed class ProbeBackend : ICharacterPhysicsMutableBackend
    {
        private readonly TrackingSharedBackend inner = new();
        internal readonly List<float> StepSeconds = [];
        internal readonly List<int> DynamicBodyCounts = [];
        internal readonly List<Vector3> GravitySamples = [];
        internal int ImpulseCount, FailAtStep;
        internal bool Disposed;
        internal Action<ProbeBackend>? AfterStep;
        public string Name => "pose initialization probe";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms) => inner.Initialize(definition, transforms);
        public int AddBody(PhysicsBodyDefinition body, Matrix4x4 transform) => inner.AddBody(body, transform);
        public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 a, Matrix4x4 b) => inner.AddJoint(joint, a, b);
        public void RemoveBody(int index) => inner.RemoveBody(index);
        public void RemoveJoint(int index) => inner.RemoveJoint(index);
        public void ResetBody(int index, Matrix4x4 transform) => inner.ResetBody(index, transform);
        public void SetBodyKinematic(int index, bool state) => inner.SetBodyKinematic(index, state);
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds) => inner.SetKinematicTarget(index, transform, seconds);
        public Matrix4x4 GetBodyWorldTransform(int index) => inner.GetBodyWorldTransform(index);
        public void SetGravity(Vector3 gravity) => inner.SetGravity(gravity);
        public void ApplyImpulse(int index, Vector3 linear, Vector3 angular) { ImpulseCount++; inner.ApplyImpulse(index, linear, angular); }
        public void Step(float seconds)
        {
            StepSeconds.Add(seconds); DynamicBodyCounts.Add(inner.Bodies.Count(body => !body.Removed && !body.Kinematic));
            GravitySamples.Add(inner.Gravity); inner.Step(seconds); AfterStep?.Invoke(this);
            if (FailAtStep == StepSeconds.Count) throw new InvalidOperationException("Injected initialization step failure");
        }
        public void Dispose() { Disposed = true; inner.Dispose(); }
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
