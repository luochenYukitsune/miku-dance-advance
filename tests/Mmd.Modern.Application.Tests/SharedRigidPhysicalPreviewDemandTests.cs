using System.Diagnostics.Tracing;
using System.Numerics;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed partial class SharedRigidPlaybackTests
{
    private const string PhysicalPreviewSwitch = "Mmd.Modern.Application.ExperimentalPhysicalPreviewLeaves";

    [Fact]
    public void PhysicalPreviewParentKeepsBeforePhysicsChildInputAndDefersItsGeometry()
    {
        AppContext.SetSwitch(PhysicalPreviewSwitch, true);
        try
        {
            var model = Model(); var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
            var parent = layout.Add("parent", "physical-parent.pmx", ModelRole.Character, rest);
            var child = layout.Add("child", "physical-child.pmx", ModelRole.Character, rest);
            var motions = new[] { new SceneActorMotion(parent.Id, model, Motion(), PhysicsEnabled: true),
                new SceneActorMotion(child.Id, model, Motion(), PhysicsEnabled: false) };
            SceneOutsideParentBinding[] parents = [new(child.Id, 0, parent.Id, 0, Matrix4x4.Identity)];
            var settings = new ProjectSettings { Physics = new() { SharedWorld = true, GroundEnabled = false } };
            var ef = new Factory(); var af = new Factory();
            using var expected = Create(ef); using var actual = Create(af);
            using var parentOracle = new CharacterAnimationController(model, Motion(), rest.Surface, rest.Outline!);
            using var childOracle = new CharacterAnimationController(model, Motion(), rest.Surface, rest.Outline!);
            var childController = PreviewController(actual, 1);
            var begin = typeof(CharacterAnimationController).GetMethod("BeginSharedPhysicsSample", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            int physicalSteps = 0;
            af.Worlds[0].BeforeStep = () =>
            {
                double frame = ++physicalSteps * .25;
                using var prepared = (IDisposable)begin.Invoke(parentOracle, [frame, true, true, null, false, null])!;
                var before = (Matrix4x4[])prepared.GetType().GetProperty("BeforePhysicsBoneWorlds")!.GetValue(prepared)!;
                childOracle.SetOutsideParents(new Dictionary<int, Matrix4x4> { [0] = before[0] });
                childOracle.SampleAt(frame, true, true);
                AssertBitsEqual(childOracle.BoneWorldMatrices, childController.BoneWorldMatrices);
            };
            ef.Worlds[0].Clear(); af.Worlds[0].Clear();
            using var phases = new PhysicalPreviewPhases(parent.Id);
            int canonicalFull = 0, previewFull = 0;
            foreach (double delta in new[] { 1.0 / 60, 1.0 / 30, 1.0 / 1000 })
            {
                int before = phases.Count("boundary.physical.complete-full");
                expected.Advance(delta, Token);
                int afterCanonical = phases.Count("boundary.physical.complete-full");
                canonicalFull += afterCanonical - before;
                AdvancePreview(actual, delta);
                previewFull += phases.Count("boundary.physical.complete-full") - afterCanonical;
            }
            Assert.Equal(6, physicalSteps);
            Assert.Equal(ef.Worlds[0].Boundaries, af.Worlds[0].Boundaries);
            Assert.Equal(ef.Worlds[0].Targets, af.Worlds[0].Targets);
            Assert.Equal(ef.Worlds[0].Impulses, af.Worlds[0].Impulses);
            var requests = new[] { new SceneObjectRenderRequest(parent.Id, [0], includeSourceGeometry: true),
                new SceneObjectRenderRequest(child.Id, [0], includeSourceGeometry: true) };
            var a = expected.CaptureRenderSnapshot(requests); var b = actual.CaptureRenderSnapshot(requests);
            Assert.Equal(a.SourceFrame, b.SourceFrame);
            AssertSpanBitsEqual<SceneVertex>(a.Scene.Surface.Snapshot.Vertices, b.Scene.Surface.Snapshot.Vertices);
            AssertBitsEqual(a.Scene.Outline!.Vertices, b.Scene.Outline!.Vertices);
            foreach (var actor in new[] { parent, child })
            {
                AssertBoneBitsEqual(a.SceneInputs[actor.Id].BoneWorldMatrices, b.SceneInputs[actor.Id].BoneWorldMatrices);
                AssertSpanBitsEqual<SceneVertex>(a.SceneInputs[actor.Id].SourceGeometry!.Vertices, b.SceneInputs[actor.Id].SourceGeometry!.Vertices);
            }
            Assert.True(canonicalFull > 0);
            Assert.Equal(0, previewFull);
            Assert.Equal(1, phases.Count("preview.physical.complete"));
            SceneProjectSession Create(Factory factory) => new(layout, new(), motions, factory.Provider(), settings,
                outsideParents: parents, cancellationToken: Token);
        }
        finally { AppContext.SetSwitch(PhysicalPreviewSwitch, false); }
    }

    [Theory]
    [InlineData("frame", false)]
    [InlineData("actors", false)]
    [InlineData("snapshot", false)]
    [InlineData("bones", false)]
    [InlineData("morph", false)]
    [InlineData("source", false)]
    [InlineData("pose", false)]
    [InlineData("snapshot", true)]
    public void PhysicalPreviewCompletesOnlyAtConsumerWithoutRepeatingPhysics(string consumer, bool pmmControlled)
    {
        AppContext.SetSwitch(PhysicalPreviewSwitch, true);
        try
        {
            var model = Model(); var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
            var actor = layout.Add("physical", "physical-demand.pmx", ModelRole.Character, rest);
            var motions = new[] { new SceneActorMotion(actor.Id, model, Motion(), PhysicsEnabled: true) };
            var settings = new ProjectSettings { Physics = new() { SharedWorld = !pmmControlled, GroundEnabled = false } };
            var controls = pmmControlled ? Controls([actor], model) : null;
            var expectedFactory = new Factory(); var actualFactory = new Factory();
            using var expected = Create(expectedFactory); using var actual = Create(actualFactory);
            var before = actual.Frame; var controller = PreviewController(actual, 0);
            expectedFactory.Worlds[0].Clear(); actualFactory.Worlds[0].Clear();
            using var phases = new PhysicalPreviewPhases(actor.Id);
            int canonicalFull = 0, previewFull = 0;
            foreach (double delta in new[] { 1.0 / 120, 1.0 / 60, 1.0 / 30, 1.0 / 90 })
            {
                int beforeCount = phases.Count("boundary.physical.complete-full");
                expected.Advance(delta, Token);
                int afterCanonical = phases.Count("boundary.physical.complete-full");
                canonicalFull += afterCanonical - beforeCount;
                AdvancePreview(actual, delta);
                previewFull += phases.Count("boundary.physical.complete-full") - afterCanonical;
            }
            Assert.True(canonicalFull > 0);
            Assert.Equal(0, previewFull);
            Assert.Equal(expectedFactory.Worlds[0].Boundaries, actualFactory.Worlds[0].Boundaries);
            Assert.Equal(expectedFactory.Worlds[0].Targets, actualFactory.Worlds[0].Targets);
            Assert.Equal(expectedFactory.Worlds[0].Impulses, actualFactory.Worlds[0].Impulses);
            int steps = actualFactory.Worlds[0].Boundaries.Count, impulses = actualFactory.Worlds[0].Impulses;
            var animation = PreviewAnimation(actual);
            if (consumer == "frame") _ = actual.Frame;
            else if (consumer == "actors") _ = actual.Actors;
            else if (consumer == "snapshot") _ = actual.CaptureRenderSnapshot();
            else if (consumer == "bones") _ = animation.GetBoneWorldMatrices(actor.Id);
            else if (consumer == "morph") _ = animation.GetMorphChannels(actor.Id);
            else if (consumer == "source") _ = typeof(SceneAnimationSession).GetMethod("CaptureSourceFrame", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(animation, [actor.Id]);
            else _ = actual.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
            var request = new[] { new SceneObjectRenderRequest(actor.Id, [0], includeSourceGeometry: true) };
            var a = expected.CaptureRenderSnapshot(request); var b = actual.CaptureRenderSnapshot(request);
            Assert.Equal(a.SourceFrame, b.SourceFrame);
            AssertSpanBitsEqual<SceneVertex>(a.Scene.Surface.Snapshot.Vertices, b.Scene.Surface.Snapshot.Vertices);
            AssertBitsEqual(a.Scene.Outline!.Vertices, b.Scene.Outline!.Vertices);
            AssertBoneBitsEqual(a.SceneInputs[actor.Id].BoneWorldMatrices, b.SceneInputs[actor.Id].BoneWorldMatrices);
            Assert.Equal(a.Scene.Surface.Materials, b.Scene.Surface.Materials);
            AssertSpanBitsEqual<SceneVertex>(a.SceneInputs[actor.Id].SourceGeometry!.Vertices, b.SceneInputs[actor.Id].SourceGeometry!.Vertices);
            Assert.NotSame(before, actual.Frame);
            Assert.Equal(steps, actualFactory.Worlds[0].Boundaries.Count);
            Assert.Equal(impulses, actualFactory.Worlds[0].Impulses);
            _ = controller; // the snapshot also verifies the solved palette remains the latest one
            Assert.Equal(1, phases.Count("preview.physical.complete"));
            SceneProjectSession Create(Factory factory) => new(layout, new(), motions, factory.Provider(), settings,
                pmmControls: controls, cancellationToken: Token);
        }
        finally { AppContext.SetSwitch(PhysicalPreviewSwitch, false); }
    }

    [Fact]
    public void PhysicalPreviewSwitchOffKeepsFullCompletion()
    {
        AppContext.SetSwitch(PhysicalPreviewSwitch, false);
        var model = Model(); var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
        var actor = layout.Add("physical", "physical-switch.pmx", ModelRole.Character, rest);
        var motions = new[] { new SceneActorMotion(actor.Id, model, Motion(), PhysicsEnabled: true) };
        var settings = new ProjectSettings { Physics = new() { SharedWorld = true, GroundEnabled = false } };
        using var actual = new SceneProjectSession(layout, new(), motions, new Factory().Provider(), settings, cancellationToken: Token);
        using var phases = new PhysicalPreviewPhases(actor.Id);
        AdvancePreview(actual, 1.0 / 30);
        Assert.True(phases.Count("boundary.physical.complete-full") > 0);
        Assert.Equal(0, phases.Count("boundary.physical.complete-deferred-bones"));
    }

    [Fact]
    public void PhysicalPreviewFallsBackForAnySoftModelEvenWhenItsPhysicsIsDisabled()
    {
        AppContext.SetSwitch(PhysicalPreviewSwitch, true);
        try
        {
            var rigid = Model(); var soft = PmxSoftBodyTests.MaterialFixture(); var layout = new SceneCompositionEditor();
            var actor = layout.Add("rigid", "rigid-soft-fallback.pmx", ModelRole.Character, SharedPhysicsFixture.Rest(rigid));
            var cloth = layout.Add("cloth", "cloth-soft-fallback.pmx", ModelRole.Character, SharedPhysicsFixture.Rest(soft));
            var motions = new[] { new SceneActorMotion(actor.Id, rigid, Motion(), PhysicsEnabled: true),
                new SceneActorMotion(cloth.Id, soft, Motion(), PhysicsEnabled: false) };
            var settings = new ProjectSettings { Physics = new() { SharedWorld = true, GroundEnabled = false } };
            using var actual = new SceneProjectSession(layout, new(), motions, new Factory().Provider(), settings, cancellationToken: Token);
            using var phases = new PhysicalPreviewPhases(actor.Id);
            AdvancePreview(actual, 1.0 / 30);
            Assert.True(phases.Count("boundary.physical.complete-full") > 0);
            Assert.Equal(0, phases.Count("boundary.physical.complete-deferred-bones"));
            Assert.Equal(1, actual.CaptureRenderSnapshot().SourceFrame);
        }
        finally { AppContext.SetSwitch(PhysicalPreviewSwitch, false); }
    }

    [Fact]
    public void PhysicalPreviewWithoutSharedOrPmmWorldUsesIndependentSolverNormally()
    {
        AppContext.SetSwitch(PhysicalPreviewSwitch, true);
        try
        {
            var model = Model(); var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
            var actor = layout.Add("independent", "independent-preview.pmx", ModelRole.Character, rest);
            var motions = new[] { new SceneActorMotion(actor.Id, model, Motion(), PhysicsEnabled: true) };
            var settings = new ProjectSettings { Physics = new() { SharedWorld = false, GroundEnabled = false } };
            var ef = new Factory(); var af = new Factory();
            using var expected = Create(ef); using var actual = Create(af);
            using var phases = new PhysicalPreviewPhases(actor.Id);
            expected.Advance(1.0 / 30, Token); AdvancePreview(actual, 1.0 / 30);
            Assert.Equal(0, phases.Count("boundary.physical.complete-deferred-bones"));
            Assert.Equal(0, phases.Count("preview.physical.complete"));
            AssertSpanBitsEqual<SceneVertex>(expected.Frame.Surface.Snapshot.Vertices, actual.Frame.Surface.Snapshot.Vertices);
            AssertBitsEqual(expected.Frame.Outline!.Vertices, actual.Frame.Outline!.Vertices);
            Assert.Equal(ef.Worlds[0].Boundaries, af.Worlds[0].Boundaries);
            SceneProjectSession Create(Factory factory) => new(layout, new(), motions, factory.Provider(), settings, cancellationToken: Token);
        }
        finally { AppContext.SetSwitch(PhysicalPreviewSwitch, false); }
    }

    [Fact]
    public void PhysicalPreviewZeroStepSaveAndRepeatedCaptureCompleteOnce()
    {
        AppContext.SetSwitch(PhysicalPreviewSwitch, true);
        try
        {
            var model = Model(); var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
            var actor = layout.Add("physical", "zero-step-preview.pmx", ModelRole.Character, rest);
            var motions = new[] { new SceneActorMotion(actor.Id, model, Motion(), PhysicsEnabled: true) };
            var settings = new ProjectSettings { Physics = new() { SharedWorld = true, GroundEnabled = false } };
            var factory = new Factory(); using var actual = new SceneProjectSession(layout, new(), motions, factory.Provider(), settings, cancellationToken: Token);
            factory.Worlds[0].Clear(); using var phases = new PhysicalPreviewPhases(actor.Id);
            AdvancePreview(actual, 1.0 / 1000);
            Assert.Empty(factory.Worlds[0].Boundaries);
            Assert.Equal(1, phases.Count("boundary.physical.complete-deferred-bones"));
            var animation = PreviewAnimation(actual);
            _ = typeof(SceneAnimationSession).GetMethod("CaptureSaveSnapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(animation, null);
            Assert.Equal(0, phases.Count("preview.physical.complete"));
            int targets = factory.Worlds[0].Targets.Count, impulses = factory.Worlds[0].Impulses;
            var first = actual.CaptureRenderSnapshot(); var second = actual.CaptureRenderSnapshot();
            Assert.Equal(0.03, first.SourceFrame, 10); Assert.Equal(first.SourceFrame, second.SourceFrame);
            Assert.Equal(1, phases.Count("preview.physical.complete"));
            Assert.Empty(factory.Worlds[0].Boundaries);
            Assert.Equal(targets, factory.Worlds[0].Targets.Count); Assert.Equal(impulses, factory.Worlds[0].Impulses);
        }
        finally { AppContext.SetSwitch(PhysicalPreviewSwitch, false); }
    }

    [Fact]
    public void PhysicalPreviewCancellationDuringCompletionFaultsAndSeekRecovers()
    {
        AppContext.SetSwitch(PhysicalPreviewSwitch, true);
        try
        {
            var model = Model(); var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
            var actor = layout.Add("physical", "physical-cancel.pmx", ModelRole.Character, rest);
            var motions = new[] { new SceneActorMotion(actor.Id, model, Motion(), PhysicsEnabled: true) };
            var settings = new ProjectSettings { Physics = new() { SharedWorld = true, GroundEnabled = false } };
            using var actual = new SceneProjectSession(layout, new(), motions, new Factory().Provider(), settings, cancellationToken: Token);
            var lastGood = actual.Frame; AdvancePreview(actual, 1.0 / 30);
            using var canceled = new CancellationTokenSource();
            var animation = PreviewAnimation(actual);
            var observer = typeof(SceneAnimationSession).GetField("PreviewLeafObserverForTesting", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            observer.SetValue(animation, (Action<Guid>)(_ => canceled.Cancel()));
            try
            {
                Assert.ThrowsAny<OperationCanceledException>(() => actual.Advance(0, canceled.Token));
                Assert.True(actual.IsFaulted); Assert.Same(lastGood, actual.Frame);
            }
            finally { observer.SetValue(animation, null); }
            actual.Seek(0, Token); Assert.False(actual.IsFaulted);
            Assert.Equal(0, actual.CaptureRenderSnapshot().SourceFrame);
        }
        finally { AppContext.SetSwitch(PhysicalPreviewSwitch, false); }
    }

    [Fact]
    public void PhysicalPreviewFallsBackAtShortTailAndDynamicParentTransition()
    {
        AppContext.SetSwitch(PhysicalPreviewSwitch, true);
        try
        {
            var model = Model(); var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
            var actors = Enumerable.Range(0, 2).Select(i => layout.Add("actor" + i, "physical-tail" + i + ".pmx", ModelRole.Character, rest)).ToArray();
            var motions = actors.Select((actor, i) => new SceneActorMotion(actor.Id, model, Motion(i == 0 ? 2u : 16u), PhysicsEnabled: true)).ToArray();
            var controls = Controls(actors, model).Edit(editor => editor.PrepareRegister([
                new(new(PmmTrackKind.Model, 1, "", 1), new PmmModelKeyframe { Visible = true, OutsideParents = [new(0, -1, -1)] }),
                new(new(PmmTrackKind.Model, 1, "", 2), new PmmModelKeyframe { Visible = true, OutsideParents = [new(0, 0, 0)] })]), true);
            var settings = new ProjectSettings { Physics = new() { SharedWorld = false, GroundEnabled = false } };
            var ef = new Factory(); var af = new Factory();
            using var expected = Create(ef); using var actual = Create(af);
            foreach (var world in ef.Worlds) world.Clear(); foreach (var world in af.Worlds) world.Clear();
            foreach (double delta in new[] { 1.0 / 60, 1.0 / 30, 1.0 / 30, 1.0 / 30 })
            { expected.Advance(delta, Token); AdvancePreview(actual, delta); }
            var requests = actors.Select(actor => new SceneObjectRenderRequest(actor.Id, [0], includeSourceGeometry: true)).ToArray();
            var a = expected.CaptureRenderSnapshot(requests); var b = actual.CaptureRenderSnapshot(requests);
            Assert.Equal(a.SourceFrame, b.SourceFrame);
            AssertSpanBitsEqual<SceneVertex>(a.Scene.Surface.Snapshot.Vertices, b.Scene.Surface.Snapshot.Vertices);
            AssertBitsEqual(a.Scene.Outline!.Vertices, b.Scene.Outline!.Vertices);
            foreach (var actor in actors) AssertBoneBitsEqual(a.SceneInputs[actor.Id].BoneWorldMatrices, b.SceneInputs[actor.Id].BoneWorldMatrices);
            for (int i = 0; i < ef.Worlds.Count; i++)
            { Assert.Equal(ef.Worlds[i].Boundaries, af.Worlds[i].Boundaries); Assert.Equal(ef.Worlds[i].Impulses, af.Worlds[i].Impulses); }
            SceneProjectSession Create(Factory factory) => new(layout, new(), motions, factory.Provider(), settings, pmmControls: controls, cancellationToken: Token);
        }
        finally { AppContext.SetSwitch(PhysicalPreviewSwitch, false); }
    }

    [Fact]
    public void PhysicalPreviewCancellationAndSolverFaultPreserveLastGoodUntilSeek()
    {
        AppContext.SetSwitch(PhysicalPreviewSwitch, true);
        try
        {
            var model = Model(); var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
            var actor = layout.Add("physical", "physical-fault.pmx", ModelRole.Character, rest);
            var motions = new[] { new SceneActorMotion(actor.Id, model, Motion(), PhysicsEnabled: true) };
            var settings = new ProjectSettings { Physics = new() { SharedWorld = true, GroundEnabled = false } };
            var factory = new Factory(); using var actual = new SceneProjectSession(layout, new(), motions, factory.Provider(), settings, cancellationToken: Token);
            var lastGood = actual.Frame; AdvancePreview(actual, 1.0 / 30);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => AdvancePreview(actual, 1.0 / 30, canceled.Token));
            Assert.False(actual.IsFaulted);
            factory.Worlds[0].BeforeStep = () => throw new InvalidOperationException("injected solver failure");
            Assert.ThrowsAny<Exception>(() => AdvancePreview(actual, 1.0 / 30));
            Assert.True(actual.IsFaulted); Assert.Same(lastGood, actual.Frame);
            factory.Worlds[0].BeforeStep = null; actual.Seek(0, Token);
            Assert.False(actual.IsFaulted); Assert.Equal(0, actual.CaptureRenderSnapshot().SourceFrame);
        }
        finally { AppContext.SetSwitch(PhysicalPreviewSwitch, false); }
    }

    private sealed class PhysicalPreviewPhases(Guid actor) : EventListener
    {
        private readonly int thread = Environment.CurrentManagedThreadId;
        private readonly List<string> stages = [];
        protected override void OnEventSourceCreated(EventSource source)
        { if (source.Name == "MDA-PoseEvaluation") EnableEvents(source, EventLevel.Informational); }
        protected override void OnEventWritten(EventWrittenEventArgs value)
        {
            if (Environment.CurrentManagedThreadId == thread && value.EventId == 1
                && value.Payload?[0] is string stage && value.Payload[1] is string id && id == actor.ToString("D")) stages.Add(stage);
        }
        internal int Count(string stage) => stages.Count(s => s == stage);
    }
}
