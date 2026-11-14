using System.Numerics;
using System.Reflection;
using Mmd.Modern.Animation;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed partial class SharedRigidPlaybackTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PreviewDemandCoalescesVisualLeafAndPreservesPhysicsAndCapture(bool shared, bool pmm)
    {
        var model = Model(); var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
        var actors = Enumerable.Range(0, 4).Select(i => layout.Add("actor" + i, "preview" + i + ".pmx", ModelRole.Character, rest)).ToArray();
        var motions = actors.Select((actor, i) => new SceneActorMotion(actor.Id, model, Motion(), PhysicsEnabled: i is 1 or 2)).ToArray();
        var controls = pmm ? Controls(actors, model) : null;
        SceneOutsideParentBinding[] parents = pmm ? [] : [new(actors[1].Id, 0, actors[0].Id, 0, Matrix4x4.Identity)];
        var settings = new ProjectSettings { Physics = new() { SharedWorld = shared, GroundEnabled = false } };
        var expectedFactory = new Factory(); var actualFactory = new Factory();
        using var expected = Create(expectedFactory); using var actual = Create(actualFactory);
        var leaf = PreviewController(actual, 3); var initialBones = leaf.BoneWorldMatrices;
        foreach (var world in expectedFactory.Worlds) world.Clear();
        foreach (var world in actualFactory.Worlds) world.Clear();
        foreach (double delta in new[] { 1.0 / 30, 1.0 / 30, 1.0 / 60 })
        {
            expected.Advance(delta, Token); AdvancePreview(actual, delta);
            Assert.Same(initialBones, leaf.BoneWorldMatrices);
            Assert.Equal(expected.CurrentFrame, actual.CurrentFrame);
        }
        for (int i = 0; i < actualFactory.Worlds.Count; i++)
        {
            Assert.Equal(expectedFactory.Worlds[i].Boundaries, actualFactory.Worlds[i].Boundaries);
            Assert.Equal(expectedFactory.Worlds[i].Targets, actualFactory.Worlds[i].Targets);
            Assert.Equal(expectedFactory.Worlds[i].Impulses, actualFactory.Worlds[i].Impulses);
        }
        var requests = actors.Select(actor => new SceneObjectRenderRequest(actor.Id, [0], includeSourceGeometry: true)).ToArray();
        var a = expected.CaptureRenderSnapshot(requests); var b = actual.CaptureRenderSnapshot(requests);
        Assert.Equal(a.SourceFrame, b.SourceFrame);
        AssertSpanBitsEqual<SceneVertex>(a.Scene.Surface.Snapshot.Vertices, b.Scene.Surface.Snapshot.Vertices);
        AssertBitsEqual(a.Scene.Outline!.Vertices, b.Scene.Outline!.Vertices);
        foreach (var actor in actors)
            AssertBoneBitsEqual(a.SceneInputs[actor.Id].BoneWorldMatrices, b.SceneInputs[actor.Id].BoneWorldMatrices);
        Assert.NotSame(initialBones, leaf.BoneWorldMatrices);
        Assert.Equal(expectedFactory.Worlds.Select(w => w.Impulses), actualFactory.Worlds.Select(w => w.Impulses));
        SceneProjectSession Create(Factory factory) => new(layout, new(), motions, factory.Provider(), settings,
            outsideParents: parents, pmmControls: controls, cancellationToken: Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewDemandCompletesShortTailAndDynamicAttachmentBeforeCanonicalAdvance(bool dynamicParents)
    {
        var model = Model(); var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
        var actors = Enumerable.Range(0, 4).Select(i => layout.Add("actor" + i, "tail" + i + ".pmx", ModelRole.Character, rest)).ToArray();
        var motions = actors.Select((actor, i) => new SceneActorMotion(actor.Id, model, Motion(i == 3 ? 1u : 16u), PhysicsEnabled: i is 1 or 2)).ToArray();
        var controls = Controls(actors, model, leafSubject: dynamicParents);
        if (dynamicParents) controls = controls.Edit(editor => editor.PrepareRegister([
            new(new(PmmTrackKind.Model, 3, "", 1), new PmmModelKeyframe { Visible = true, OutsideParents = [new(0, 2, 0)] }),
            new(new(PmmTrackKind.Model, 3, "", 2), new PmmModelKeyframe { Visible = true, OutsideParents = [new(0, -1, -1)] })]), true);
        var settings = new ProjectSettings { Physics = new() { SharedWorld = true, GroundEnabled = false } };
        var expectedFactory = new Factory(); var actualFactory = new Factory();
        using var expected = Create(expectedFactory); using var actual = Create(actualFactory);
        foreach (double delta in new[] { 1.0 / 60, 1.0 / 30, 1.0 / 30, 1.0 / 30 })
        { expected.Advance(delta, Token); AdvancePreview(actual, delta); }
        expected.Advance(1.0 / 30, Token); actual.Advance(1.0 / 30, Token);
        AssertSpanBitsEqual<SceneVertex>(expected.Frame.Surface.Snapshot.Vertices, actual.Frame.Surface.Snapshot.Vertices);
        AssertBitsEqual(expected.Frame.Outline!.Vertices, actual.Frame.Outline!.Vertices);
        var request = new[] { new SceneObjectRenderRequest(actors[3].Id, [0]) };
        AssertBoneBitsEqual(expected.CaptureRenderSnapshot(request).SceneInputs[actors[3].Id].BoneWorldMatrices,
            actual.CaptureRenderSnapshot(request).SceneInputs[actors[3].Id].BoneWorldMatrices);
        Assert.Equal(expectedFactory.Worlds[0].Boundaries, actualFactory.Worlds[0].Boundaries);
        Assert.Equal(expectedFactory.Worlds[0].Impulses, actualFactory.Worlds[0].Impulses);
        SceneProjectSession Create(Factory factory) => new(layout, new(), motions, factory.Provider(), settings,
            pmmControls: controls, cancellationToken: Token);
    }

    [Theory]
    [InlineData("Frame")]
    [InlineData("Actors")]
    [InlineData("Snapshot")]
    public void PreviewDemandRealReadersMaterializeAndSeekClearsPending(string reader)
    {
        var model = Model(); var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
        var actors = Enumerable.Range(0, 2).Select(i => layout.Add("actor" + i, "reader" + i + ".pmx", ModelRole.Character, rest)).ToArray();
        var motions = actors.Select((actor, i) => new SceneActorMotion(actor.Id, model, Motion(), PhysicsEnabled: i == 0)).ToArray();
        var settings = new ProjectSettings { Physics = new() { SharedWorld = true, GroundEnabled = false } };
        var factory = new Factory(); using var actual = new SceneProjectSession(layout, new(), motions, factory.Provider(), settings, cancellationToken: Token);
        var leaf = PreviewController(actual, 1); var initial = leaf.BoneWorldMatrices;
        AdvancePreview(actual, 1.0 / 30); Assert.Same(initial, leaf.BoneWorldMatrices);
        _ = actual.CurrentFrame; _ = actual.DurationFrames; _ = actual.Lighting;
        Assert.Same(initial, leaf.BoneWorldMatrices);
        if (reader == "Frame") _ = actual.Frame;
        else if (reader == "Actors") _ = actual.Actors;
        else _ = actual.CaptureRenderSnapshot([new SceneObjectRenderRequest(actors[1].Id, [0])]);
        Assert.NotSame(initial, leaf.BoneWorldMatrices);
        AdvancePreview(actual, 1.0 / 30); actual.Seek(0, Token);
        Assert.Equal(0, actual.CaptureRenderSnapshot().SourceFrame);
    }

    [Fact]
    public void PreviewDemandResolvesCameraParentFromCurrentLeafPose()
    {
        var model = Model(); var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
        var actors = Enumerable.Range(0, 4).Select(i => layout.Add("actor" + i, "camera" + i + ".pmx", ModelRole.Character, rest)).ToArray();
        var motions = actors.Select((actor, i) => new SceneActorMotion(actor.Id, model, Motion(), PhysicsEnabled: i is 1 or 2)).ToArray();
        var controls = Controls(actors, model);
        controls = controls.WithCameraParent(0, controls.CameraAt(0) with { ParentModelIndex = 3, ParentBoneIndex = 0 });
        var settings = new ProjectSettings { Physics = new() { SharedWorld = true, GroundEnabled = false } };
        var expectedFactory = new Factory(); var actualFactory = new Factory();
        using var expected = Create(expectedFactory); using var actual = Create(actualFactory);
        foreach (double delta in new[] { 1.0 / 30, 1.0 / 30 })
        { expected.Advance(delta, Token); AdvancePreview(actual, delta); }
        var a = expected.CaptureRenderSnapshot(); var b = actual.CaptureRenderSnapshot();
        Assert.NotNull(a.CameraParentTransform); Assert.NotNull(b.CameraParentTransform);
        AssertBitsEqual(new[] { a.CameraParentTransform!.Value }, new[] { b.CameraParentTransform!.Value });
        AssertSpanBitsEqual<SceneVertex>(a.Scene.Surface.Snapshot.Vertices, b.Scene.Surface.Snapshot.Vertices);
        SceneProjectSession Create(Factory factory) => new(layout, new(), motions, factory.Provider(), settings,
            pmmControls: controls, cancellationToken: Token);
    }

    [Fact]
    public void PreviewDemandCancellationAndFaultRetainLastGoodFrameUntilSeek()
    {
        var model = Model(); var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
        var actors = Enumerable.Range(0, 2).Select(i => layout.Add("actor" + i, "fault" + i + ".pmx", ModelRole.Character, rest)).ToArray();
        var motions = actors.Select((actor, i) => new SceneActorMotion(actor.Id, model, Motion(), PhysicsEnabled: i == 0)).ToArray();
        var settings = new ProjectSettings { Physics = new() { SharedWorld = true, GroundEnabled = false } };
        var factory = new Factory(); using var actual = new SceneProjectSession(layout, new(), motions, factory.Provider(), settings, cancellationToken: Token);
        var lastGood = actual.Frame;
        AdvancePreview(actual, 1.0 / 30);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => AdvancePreview(actual, 1.0 / 30, canceled.Token));
        Assert.Equal(1, actual.CurrentFrame); Assert.False(actual.IsFaulted);
        factory.Worlds[0].BeforeStep = () => throw new InvalidOperationException("injected solver fault");
        Assert.ThrowsAny<Exception>(() => AdvancePreview(actual, 1.0 / 30));
        Assert.True(actual.IsFaulted); Assert.Same(lastGood, actual.Frame);
        Assert.Throws<InvalidOperationException>(() => actual.CaptureRenderSnapshot());
        factory.Worlds[0].BeforeStep = null; actual.Seek(0, Token);
        Assert.False(actual.IsFaulted); Assert.Equal(0, actual.CaptureRenderSnapshot().SourceFrame);
    }

    [Fact]
    public void PreviewDemandCanonicalValidationDoesNotMaterializeButZeroAdvanceDoes()
    {
        var (project, actor, leaf) = DemandFixture(); using (project)
        {
            var initialBones = leaf.BoneWorldMatrices; var animation = PreviewAnimation(project);
            var published = typeof(SceneAnimationSession).GetField("published", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var initialFrame = published.GetValue(animation);
            AdvancePreview(project, 1.0 / 30);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => project.Advance(1.0 / 30, canceled.Token));
            Assert.Throws<ArgumentOutOfRangeException>(() => project.Advance(double.NaN, Token));
            Assert.Throws<ArgumentNullException>(() => animation.BindMotion(null!, Token));
            Assert.Same(initialBones, leaf.BoneWorldMatrices); Assert.Same(initialFrame, published.GetValue(animation));
            var result = project.Advance(0, Token);
            Assert.NotSame(initialBones, leaf.BoneWorldMatrices);
            Assert.Same(result, project.Frame); Assert.Equal(1, project.CurrentFrame);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewDemandCancellationDuringLeafCompletionFaultsAndKeepsLastGood(bool sceneBind)
    {
        var (project, _, leaf) = DemandFixture(); using (project)
        using (var canceled = new CancellationTokenSource())
        {
            var animation = PreviewAnimation(project);
            var original = SceneMotionAsset.FromMotion(new VmdMotion
            { CameraKeyframes = [new() { Frame = 0, Distance = -30, ViewAngle = 30 }] });
            animation.BindSceneMotion(original, Token);
            var lastGood = project.Frame; var beforeBones = leaf.BoneWorldMatrices;
            AdvancePreview(project, 1.0 / 30);
            // Trigger cancellation synchronously inside the leaf's completion, after the
            // caller's initial token check. No scheduling or sleep-based race is involved.
            var model = (PmxModel)typeof(CharacterAnimationController).GetField("model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(leaf)!;
            var originalSoftBodies = model.SoftBodies;
            var trigger = new CancelOnCount<PmxSoftBody>(originalSoftBodies, canceled);
            typeof(PmxModel).GetProperty(nameof(PmxModel.SoftBodies))!.SetValue(model, trigger);
            try
            {
                if (sceneBind)
                    Assert.ThrowsAny<OperationCanceledException>(() => animation.BindSceneMotion(null, canceled.Token));
                else
                    Assert.ThrowsAny<OperationCanceledException>(() => project.Advance(0, canceled.Token));
                Assert.True(trigger.Triggered); Assert.NotSame(beforeBones, leaf.BoneWorldMatrices);
                Assert.True(project.IsFaulted); Assert.Same(lastGood, project.Frame);
                Assert.Same(original, animation.SceneMotion);
                Assert.Throws<InvalidOperationException>(() => project.CaptureRenderSnapshot());
            }
            finally { typeof(PmxModel).GetProperty(nameof(PmxModel.SoftBodies))!.SetValue(model, originalSoftBodies); }
            project.Seek(0, Token);
            Assert.False(project.IsFaulted); Assert.Equal(0, project.CaptureRenderSnapshot().SourceFrame);
        }
    }

    private sealed class CancelOnCount<T>(IReadOnlyList<T> source, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public bool Triggered { get; private set; }
        public int Count { get { Triggered = true; cancellation.Cancel(); return source.Count; } }
        public T this[int index] => source[index];
        public IEnumerator<T> GetEnumerator() => source.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData("bones")]
    [InlineData("selected-object")]
    [InlineData("selected-index")]
    [InlineData("source")]
    [InlineData("snapshot-null")]
    [InlineData("snapshot-duplicate")]
    [InlineData("snapshot-object")]
    public void PreviewDemandInvalidReadersDoNotMaterializePendingLeaf(string reader)
    {
        var (project, actor, leaf) = DemandFixture(); using (project)
        {
            var before = leaf.BoneWorldMatrices; AdvancePreview(project, 1.0 / 30);
            var animation = PreviewAnimation(project); var unknown = Guid.NewGuid();
            if (reader == "bones")
                Assert.Throws<ArgumentException>(() => animation.GetBoneWorldMatrices(unknown));
            else if (reader == "selected-object" || reader == "selected-index" || reader == "source")
            {
                string name = reader == "source" ? "CaptureSourceFrame" : "CaptureSelectedBoneWorldMatrices";
                var method = typeof(SceneAnimationSession).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
                object[] args = reader == "source" ? [unknown] : reader == "selected-object" ? [unknown, new[] { 0 }] : [actor.Id, new[] { 999 }];
                var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(animation, args));
                Assert.IsAssignableFrom<ArgumentException>(error.InnerException);
            }
            else if (reader == "snapshot-null")
                Assert.Throws<ArgumentException>(() => project.CaptureRenderSnapshot([null!]));
            else if (reader == "snapshot-duplicate")
                Assert.Throws<ArgumentException>(() => project.CaptureRenderSnapshot([new(actor.Id), new(actor.Id)]));
            else
                Assert.Throws<ArgumentException>(() => project.CaptureRenderSnapshot([new(unknown)]));
            Assert.False(project.IsFaulted); Assert.Same(before, leaf.BoneWorldMatrices);
            _ = project.Frame; Assert.NotSame(before, leaf.BoneWorldMatrices);
        }
    }

    [Fact]
    public void PreviewDemandSaveSnapshotDoesNotCompleteLeaf()
    {
        var (project, _, leaf) = DemandFixture(); using (project)
        {
            var before = leaf.BoneWorldMatrices; AdvancePreview(project, 1.0 / 30);
            var method = typeof(SceneAnimationSession).GetMethod("CaptureSaveSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
            _ = method.Invoke(PreviewAnimation(project), null);
            Assert.Same(before, leaf.BoneWorldMatrices); Assert.Equal(1, project.CurrentFrame);
            _ = project.Frame; Assert.NotSame(before, leaf.BoneWorldMatrices);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewDemandInvalidPoseRequestDoesNotCompleteLeaf(bool unknown)
    {
        var (project, _, leaf) = DemandFixture(); using (project)
        {
            var before = leaf.BoneWorldMatrices; AdvancePreview(project, 1.0 / 30);
            if (unknown)
                Assert.Throws<ArgumentException>(() => project.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion> { [Guid.NewGuid()] = Motion() }));
            else
                Assert.Throws<ArgumentNullException>(() => project.CapturePosePreviewRequest(null!));
            Assert.Same(before, leaf.BoneWorldMatrices); Assert.False(project.IsFaulted);
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("request")]
    public void PreviewDemandIndirectCancellationFaultsSourceBeforeReturning(string operation)
    {
        var (project, _, leaf) = DemandFixture(); using (project)
        using (var cancellation = new CancellationTokenSource())
        {
            using var preview = operation == "update" ? project.CreatePosePreview(new Dictionary<Guid, VmdMotion>(), Token) : null;
            var lastGood = project.Frame; AdvancePreview(project, 1.0 / 30);
            var model = (PmxModel)typeof(CharacterAnimationController).GetField("model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(leaf)!;
            var original = model.SoftBodies; var trigger = new CancelInsideCompletion<PmxSoftBody>(original, cancellation);
            typeof(PmxModel).GetProperty(nameof(PmxModel.SoftBodies))!.SetValue(model, trigger);
            try
            {
                var drafts = new Dictionary<Guid, VmdMotion>();
                if (operation == "create")
                    Assert.ThrowsAny<OperationCanceledException>(() => project.CreatePosePreview(drafts, cancellation.Token));
                else if (operation == "update")
                    Assert.ThrowsAny<OperationCanceledException>(() => preview!.UpdatePosePreviewMotions(drafts, cancellation.Token));
                else
                {
                    var method = typeof(SceneProjectSession).GetMethod("CapturePosePreviewRequestWithCancellation");
                    Assert.NotNull(method);
                    var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(project, [drafts, cancellation.Token]));
                    Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
                }
                Assert.True(trigger.Triggered); Assert.True(project.IsFaulted); Assert.Same(lastGood, project.Frame);
            }
            finally { typeof(PmxModel).GetProperty(nameof(PmxModel.SoftBodies))!.SetValue(model, original); }
            project.Seek(0, Token); Assert.False(project.IsFaulted);
        }
    }

    [Fact]
    public async Task PreviewDemandOfflinePreflightPropagatesCancellationDuringCompletion()
    {
        var (project, actor, leaf) = DemandFixture(); using (project)
        using (var cancellation = new CancellationTokenSource())
        {
            var lastGood = project.Frame; AdvancePreview(project, 1.0 / 30);
            var model = (PmxModel)typeof(CharacterAnimationController).GetField("model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(leaf)!;
            var original = model.SoftBodies; var trigger = new CancelInsideCompletion<PmxSoftBody>(original, cancellation);
            typeof(PmxModel).GetProperty(nameof(PmxModel.SoftBodies))!.SetValue(model, trigger);
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OfflineFrameRunner.RunProjectAsync(project,
                    new(2, 2, new(30), 0, 1), (_, _) => ValueTask.CompletedTask,
                    cancellationToken: cancellation.Token, sceneInputRequests: [new(actor.Id, [0])]));
                Assert.True(trigger.Triggered); Assert.True(project.IsFaulted); Assert.Same(lastGood, project.Frame);
            }
            finally { typeof(PmxModel).GetProperty(nameof(PmxModel.SoftBodies))!.SetValue(model, original); }
            project.Seek(0, Token); Assert.False(project.IsFaulted);
        }
    }

    private sealed class CancelInsideCompletion<T>(IReadOnlyList<T> source, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public bool Triggered { get; private set; }
        public int Count
        {
            get
            {
                if (!Triggered && new System.Diagnostics.StackTrace().GetFrames().Any(frame => frame.GetMethod()?.Name == "CompletePreviewLeaves"))
                { Triggered = true; cancellation.Cancel(); }
                return source.Count;
            }
        }
        public T this[int index] => source[index];
        public IEnumerator<T> GetEnumerator() => source.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData("bones")]
    [InlineData("morph")]
    [InlineData("source")]
    [InlineData("pose")]
    public void PreviewDemandIndividualConsumersCompletePendingLeaf(string reader)
    {
        var (project, actor, leaf) = DemandFixture(); using (project)
        {
            var initialBones = leaf.BoneWorldMatrices; AdvancePreview(project, 1.0 / 30);
            var animation = PreviewAnimation(project);
            if (reader == "bones") _ = animation.GetBoneWorldMatrices(actor.Id);
            else if (reader == "morph") _ = animation.GetMorphChannels(actor.Id);
            else if (reader == "source")
                _ = typeof(SceneAnimationSession).GetMethod("CaptureSourceFrame", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(animation, [actor.Id]);
            else _ = project.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
            Assert.NotSame(initialBones, leaf.BoneWorldMatrices);
            Assert.Equal(1, project.CaptureRenderSnapshot().SourceFrame);
        }
    }

    [Fact]
    public void PreviewDemandRetainsConstantLeafAndAdvancesItsVisibilityClock()
    {
        var model = Model(); var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
        var actors = Enumerable.Range(0, 2).Select(i => layout.Add("actor" + i, "constant" + i + ".pmx", ModelRole.Character, rest)).ToArray();
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }],
            IkKeyframes = [new() { Visible = true }, new() { Frame = 1, Visible = false }, new() { Frame = 16, Visible = true }] };
        var motions = new[] { new SceneActorMotion(actors[0].Id, model, Motion()),
            new SceneActorMotion(actors[1].Id, model, motion, PhysicsEnabled: false) };
        var settings = new ProjectSettings { Physics = new() { SharedWorld = true, GroundEnabled = false } };
        using var project = new SceneProjectSession(layout, new(), motions, new Factory().Provider(), settings, cancellationToken: Token);
        var leaf = PreviewController(project, 1); var bones = leaf.BoneWorldMatrices;
        AdvancePreview(project, 1.0 / 30); var snapshot = project.CaptureRenderSnapshot();
        Assert.Same(bones, leaf.BoneWorldMatrices); Assert.False(snapshot.RenderingStyles[1].Visible);
        var clock = typeof(CharacterAnimationController).GetField("sampledFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal(1d, (double?)clock.GetValue(leaf));
    }

    private static (SceneProjectSession Project, EditableSceneObject Actor, CharacterAnimationController Leaf) DemandFixture()
    {
        var model = Model(); var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
        var actors = Enumerable.Range(0, 2).Select(i => layout.Add("actor" + i, "demand" + i + ".pmx", ModelRole.Character, rest)).ToArray();
        var motions = actors.Select((actor, i) => new SceneActorMotion(actor.Id, model, Motion(), PhysicsEnabled: i == 0)).ToArray();
        var settings = new ProjectSettings { Physics = new() { SharedWorld = true, GroundEnabled = false } };
        var project = new SceneProjectSession(layout, new(), motions, new Factory().Provider(), settings, cancellationToken: Token);
        return (project, actors[1], PreviewController(project, 1));
    }

    private static SceneAnimationSession PreviewAnimation(SceneProjectSession project)
        => (SceneAnimationSession)typeof(SceneProjectSession).GetField("animation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(project)!;

    private static CharacterAnimationController PreviewController(SceneProjectSession project, int index)
    {
        var animation = PreviewAnimation(project);
        var entries = (Array)typeof(SceneAnimationSession).GetField("entries", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(animation)!;
        return (CharacterAnimationController)entries.GetValue(index)!.GetType().GetProperty("Controller")!.GetValue(entries.GetValue(index))!;
    }

    private static void AdvancePreview(SceneProjectSession project, double elapsed, CancellationToken? token = null)
    {
        var method = typeof(SceneProjectSession).GetMethod("AdvancePreview", [typeof(double), typeof(CancellationToken)]);
        Assert.NotNull(method);
        try { method.Invoke(project, [elapsed, token ?? Token]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }
}
