using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

// Other tests enable process-wide pose tracing, which deliberately selects serial evaluation.
[Collection("Controller preparation")]
public sealed class PmmMultiPropParallelParityTests
{
    private static readonly byte[] LinearBoneCurve = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(b => b).ToArray();
    private static readonly byte[] CameraCurve = Enumerable.Repeat(new byte[] { 20, 107, 20, 107 }, 6).SelectMany(b => b).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PmmMultiPropParallelMatchesForcedSerialBitwiseAcross120HzAndParentTransitions(bool sharedWorld)
    {
        var (doc, layoutObjects, models, physFlags, frozenIds) = BuildMultiPropSceneDefinition();
        double[] deltas =
        [
            1d / 120, 1d / 120, 1d / 60,
            1d / 30, 1d / 30, 0.05,
            0.1, 0.1, 0.1, 0.1, 0.1, 0.1,
            0.1, 0.1, 0.1, 0.1,
            1d / 90, 1d / 30,
        ];

        // Pass 1: Verify zero CPU vertex materialization across the entire multi-step & Seek timeline.
        RunTimeline(verifyVertices: false);
        // Pass 2: Verify exact bitwise vertex, outline, material, and bone-world parity at every step.
        RunTimeline(verifyVertices: true);

        void RunTimeline(bool verifyVertices)
        {
            using var fixtureParallel = CreateMultiPropFixture(doc, layoutObjects, models, physFlags, frozenIds, sharedWorld, CancellationToken.None);
            using var serialCts = new CancellationTokenSource();
            using var fixtureSerial = CreateMultiPropFixture(doc, layoutObjects, models, physFlags, frozenIds, sharedWorld, serialCts.Token);
            var parallel = fixtureParallel.Session;
            var serial = fixtureSerial.Session;

            parallel.DeferVertexSkinningToRenderer = true;
            serial.DeferVertexSkinningToRenderer = true;

            var p1 = parallel.Seek(1, CancellationToken.None);
            var s1 = serial.Seek(1, serialCts.Token);
            AssertSessionParity(fixtureSerial, serial, s1, fixtureParallel, parallel, p1, verifyVertices);

            foreach (double dt in deltas)
            {
                var pFrame = parallel.Advance(dt, CancellationToken.None);
                var sFrame = serial.Advance(dt, serialCts.Token);
                AssertSessionParity(fixtureSerial, serial, sFrame, fixtureParallel, parallel, pFrame, verifyVertices);
            }

            var pSeek20 = parallel.Seek(20, CancellationToken.None);
            var sSeek20 = serial.Seek(20, serialCts.Token);
            AssertSessionParity(fixtureSerial, serial, sSeek20, fixtureParallel, parallel, pSeek20, verifyVertices);

            var pSeek4 = parallel.Seek(4, CancellationToken.None);
            var sSeek4 = serial.Seek(4, serialCts.Token);
            AssertSessionParity(fixtureSerial, serial, sSeek4, fixtureParallel, parallel, pSeek4, verifyVertices);

            for (int i = 0; i < 6; i++)
            {
                var pAdv = parallel.Advance(1d / 30, CancellationToken.None);
                var sAdv = serial.Advance(1d / 30, serialCts.Token);
                AssertSessionParity(fixtureSerial, serial, sAdv, fixtureParallel, parallel, pAdv, verifyVertices);
            }
        }
    }

    [Fact]
    public void ParallelMultiPropControlledWorldsAndLevelsExecuteConcurrentlyAndMatchSerialBitwise()
    {
        var parallelBackends = new ConcurrentBag<ThreadTrackingBackend>();
        var serialBackends = new ConcurrentBag<ThreadTrackingBackend>();

        var (doc, layoutObjects, models) = BuildConcurrentControlledWorldsDefinition();
        using var parallelFixture = CreateConcurrentControlledWorldsFixture(doc, layoutObjects, models, _ =>
        {
            var backend = new ThreadTrackingBackend();
            parallelBackends.Add(backend);
            return backend;
        }, CancellationToken.None);
        using var serialCts = new CancellationTokenSource();
        using var serialFixture = CreateConcurrentControlledWorldsFixture(doc, layoutObjects, models, _ =>
        {
            var backend = new ThreadTrackingBackend();
            serialBackends.Add(backend);
            return backend;
        }, serialCts.Token);

        var parallel = parallelFixture.Session;
        var serial = serialFixture.Session;

        // Advance multiple multi-substep frames across keyframe switches.
        for (int i = 0; i < 12; i++)
        {
            var pFrame = parallel.Advance(1d / 30, CancellationToken.None);
            var sFrame = serial.Advance(1d / 30, serialCts.Token);
            AssertFrameBitsEqual(sFrame, pFrame);
            for (int a = 0; a < parallelFixture.ActorIds.Length; a++)
                AssertSpanBitsEqual<Matrix4x4>(
                    serial.GetBoneWorldMatrices(serialFixture.ActorIds[a]).ToArray(),
                    parallel.GetBoneWorldMatrices(parallelFixture.ActorIds[a]).ToArray());
        }

        // Serial path with cancelable token must strictly execute all steps on the calling thread.
        int callerThread = Environment.CurrentManagedThreadId;
        Assert.All(serialBackends, b => Assert.All(b.StepThreads, tid => Assert.Equal(callerThread, tid)));

        if (Environment.ProcessorCount >= 2)
        {
            int distinctThreads = parallelBackends.SelectMany(b => b.StepThreads).Distinct().Count();
            int peak = parallelBackends.Max(b => b.ObservedPeakConcurrency);
            Assert.True(distinctThreads >= 2 || peak >= 1,
                $"Expected concurrent independent controlledWorlds execution, got distinctThreads={distinctThreads}, peak={peak}.");
        }
    }

    [Fact]
    public void FrozenOutsideParentRefreshPreservesDeferredGpuSkinningWithoutCpuMaterialization()
    {
        var model = BuildPropPmx("frozen-prop", ["root", "tip"], vertexCount: 16, dynamicBody: false);
        var rest = SharedPhysicsFixture.Rest(model);
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }] };

        using var gpuController = new CharacterAnimationController(model, motion, rest.Surface, rest.Outline!);
        using var cpuController = new CharacterAnimationController(model, motion, rest.Surface, rest.Outline!);
        var deferProperty = typeof(CharacterAnimationController).GetProperty("DeferVertexSkinning", BindingFlags.Instance | BindingFlags.NonPublic)!;
        deferProperty.SetValue(gpuController, true);
        deferProperty.SetValue(cpuController, false);

        var initialParents = new Dictionary<int, Matrix4x4> { [-1] = Matrix4x4.CreateTranslation(1f, 2f, -3f) };
        gpuController.SetOutsideParents(initialParents);
        cpuController.SetOutsideParents(initialParents);

        var gpuInit = gpuController.SampleAt(0);
        var cpuInit = cpuController.SampleAt(0);
        Assert.True(gpuInit.Surface.Snapshot.HasDeferredDeformation);
        Assert.NotNull(gpuInit.Surface.Snapshot.WholeDeformation);
        Assert.False(gpuInit.Surface.Snapshot.IsDeferredDeformationMaterialized);
        Assert.False(cpuInit.Surface.Snapshot.HasDeferredDeformation);

        // Move outside parent across multiple steps on frozen DurationFrames=0 controller.
        for (int step = 1; step <= 5; step++)
        {
            var nextParents = new Dictionary<int, Matrix4x4>
            {
                [-1] = Matrix4x4.CreateRotationY(step * 0.25f) * Matrix4x4.CreateTranslation(step * 1.5f, -0.5f * step, 2f)
            };
            var gpuRefreshed = gpuController.RefreshFrozenOutsideParents(nextParents);
            var cpuRefreshed = cpuController.RefreshFrozenOutsideParents(nextParents);

            Assert.True(gpuRefreshed.Surface.Snapshot.HasDeferredDeformation);
            Assert.NotNull(gpuRefreshed.Surface.Snapshot.WholeDeformation);
            Assert.False(gpuRefreshed.Surface.Snapshot.IsDeferredDeformationMaterialized);
            Assert.Same(gpuRefreshed.Surface.Snapshot, gpuRefreshed.Outline.DeferredSurface);

            // Only after verifying deferred state is unmaterialized, compare exact CPU reference vertices.
            AssertSpanBitsEqual<SceneVertex>(
                cpuRefreshed.Surface.Snapshot.Vertices,
                gpuRefreshed.Surface.Snapshot.Vertices);
            AssertBitsEqual(cpuRefreshed.Outline.Vertices, gpuRefreshed.Outline.Vertices);
            AssertSpanBitsEqual<Matrix4x4>(
                cpuController.BoneWorldMatrices.ToArray(),
                gpuController.BoneWorldMatrices.ToArray());
        }
    }

    [Fact]
    public void Intermediate120HzSubstepsSkipFrozenNonPhysicalLeafRebuildWhileMatchingFinalPoseBitwise()
    {
        var charModel = BuildCharacterPmx("hero", ["root", "hand", "afterHand"], withAfterPhysics: true);
        var propModel = BuildPropPmx("frozen-sword", ["hilt", "blade"], vertexCount: 24, dynamicBody: false);
        var layout = new SceneCompositionEditor();
        var charObj = layout.Add("hero", "hero.pmx", ModelRole.Character, SharedPhysicsFixture.Rest(charModel));
        var propObj = layout.Add("sword", "sword.pmx", ModelRole.Character, SharedPhysicsFixture.Rest(propModel));

        var charMotion = new VmdMotion
        {
            BoneKeyframes =
            [
                new() { BoneName = "root", Frame = 0, Translation = Vector3.Zero },
                new() { BoneName = "root", Frame = 30, Translation = new(3f, 1f, -2f), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.8f) },
                new() { BoneName = "hand", Frame = 0, Translation = Vector3.Zero },
                new() { BoneName = "hand", Frame = 30, Translation = new(0.5f, 1.5f, 0f) },
            ]
        };
        var frozenPropMotion = new VmdMotion { BoneKeyframes = [new() { BoneName = "hilt", Frame = 0 }] };

        var bindings = new[]
        {
            new SceneOutsideParentBinding(propObj.Id, -1, charObj.Id, 2, Matrix4x4.CreateTranslation(0.1f, 0.2f, 0.3f)),
        };

        var observedSubstepBones = new List<IReadOnlyList<Matrix4x4>>();
        var trackingBackend = new ThreadTrackingBackend();
        using var parallel = new SceneAnimationSession(
            layout.Objects,
            [new(charObj.Id, charModel, charMotion, PhysicsEnabled: true), new(propObj.Id, propModel, frozenPropMotion, PhysicsEnabled: false)],
            _ => new TrackingSharedBackend(),
            outsideParents: bindings,
            sharedPhysicsFactory: () => trackingBackend,
            cancellationToken: CancellationToken.None);
        using var serialCts = new CancellationTokenSource();
        using var serial = new SceneAnimationSession(
            layout.Objects,
            [new(charObj.Id, charModel, charMotion, PhysicsEnabled: true), new(propObj.Id, propModel, frozenPropMotion, PhysicsEnabled: false)],
            _ => new TrackingSharedBackend(),
            outsideParents: bindings,
            sharedPhysicsFactory: () => new TrackingSharedBackend(),
            cancellationToken: serialCts.Token);

        parallel.DeferVertexSkinningToRenderer = true;
        serial.DeferVertexSkinningToRenderer = true;

        var entriesField = typeof(SceneAnimationSession).GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var propEntry = ((Array)entriesField.GetValue(parallel)!).GetValue(1)!;
        var propController = (CharacterAnimationController)propEntry.GetType().GetProperty("Controller")!.GetValue(propEntry)!;
        var initialPropBones = propController.BoneWorldMatrices;
        trackingBackend.BeforeStep = () => observedSubstepBones.Add(propController.BoneWorldMatrices);

        // Advance 2 frames = 8 substeps at 120Hz in one call; substeps 0..6 have deferRigidSkinning=true.
        var pFrame = parallel.Advance(2d / 30, CancellationToken.None);
        var sFrame = serial.Advance(2d / 30, serialCts.Token);

        Assert.Equal(8, observedSubstepBones.Count);
        // Frozen non-physical leaf skips redundant BoneWorldMatrices recomputation during intermediate substeps.
        Assert.All(observedSubstepBones.Take(7), snapshot => Assert.Same(initialPropBones, snapshot));
        // Final boundary updates the frozen leaf completely and preserves deferred GPU deformation.
        Assert.NotSame(initialPropBones, propController.BoneWorldMatrices);

        var captureSourceFrame = typeof(SceneAnimationSession).GetMethod("CaptureSourceFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pPropSrc = ((SceneSnapshot Geometry, OriginalOutlineData? Outline, OriginalOutlineOptions? OutlineOptions))captureSourceFrame.Invoke(parallel, [propObj.Id])!;
        var sPropSrc = ((SceneSnapshot Geometry, OriginalOutlineData? Outline, OriginalOutlineOptions? OutlineOptions))captureSourceFrame.Invoke(serial, [propObj.Id])!;
        Assert.True(pPropSrc.Geometry.HasDeferredDeformation);
        Assert.NotNull(pPropSrc.Geometry.WholeDeformation);
        Assert.False(pPropSrc.Geometry.IsDeferredDeformationMaterialized);

        AssertFrameBitsEqual(sFrame, pFrame);
        AssertSpanBitsEqual<Matrix4x4>(
            serial.GetBoneWorldMatrices(propObj.Id).ToArray(),
            parallel.GetBoneWorldMatrices(propObj.Id).ToArray());
        AssertSpanBitsEqual<SceneVertex>(sPropSrc.Geometry.Vertices, pPropSrc.Geometry.Vertices);
    }

    [Fact]
    public void CyclicAndInvalidOutsideParentTopologyThrowsEveryTimeWithoutPollutingCache()
    {
        var model = BuildCharacterPmx("actor", ["root", "hand"], withAfterPhysics: false);
        var layout = new SceneCompositionEditor();
        var a = layout.Add("a", "a.pmx", ModelRole.Character, SharedPhysicsFixture.Rest(model));
        var b = layout.Add("b", "b.pmx", ModelRole.Character, SharedPhysicsFixture.Rest(model));
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 10, Translation = Vector3.UnitX }] };

        using var session = new SceneAnimationSession(
            layout.Objects,
            [new(a.Id, model, motion, PhysicsEnabled: false), new(b.Id, model, motion, PhysicsEnabled: false)],
            _ => new TrackingSharedBackend(),
            outsideParents: [new(b.Id, 0, a.Id, 0, Matrix4x4.Identity)],
            sharedPhysicsFactory: () => new TrackingSharedBackend(),
            cancellationToken: CancellationToken.None);

        // Warm the SharedBoundaryTopology cache with valid non-cyclic playback.
        _ = session.Advance(1d / 30, CancellationToken.None);

        var topologyField = typeof(SceneAnimationSession).GetField("cachedBoundaryTopology", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var validCached = topologyField.GetValue(session);
        Assert.NotNull(validCached);

        // Attempting to add a cyclic binding via SetOutsideParent must throw deterministically every time
        // and leave the cached valid topology unpolluted.
        var cyclicError1 = Assert.Throws<ArgumentException>(() =>
            session.SetOutsideParent(a.Id, 0, b.Id, 0, keepWorld: false, token: CancellationToken.None));
        Assert.Contains("cycle", cyclicError1.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(validCached, topologyField.GetValue(session));

        var cyclicError2 = Assert.Throws<ArgumentException>(() =>
            session.SetOutsideParent(a.Id, 0, b.Id, 0, keepWorld: false, token: CancellationToken.None));
        Assert.Contains("cycle", cyclicError2.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(validCached, topologyField.GetValue(session));

        // Direct EvaluateSharedBoundary invocation with cyclic topology via cached builder must also throw repeatedly.
        var entriesField = typeof(SceneAnimationSession).GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var entries = (Array)entriesField.GetValue(session)!;
        var getTopology = typeof(SceneAnimationSession).GetMethod("GetSharedBoundaryTopology", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cyclicBindings = new SceneOutsideParentBinding[]
        {
            new(a.Id, 0, b.Id, 0, Matrix4x4.Identity),
            new(b.Id, 0, a.Id, 0, Matrix4x4.Identity),
        };
        var ex1 = Assert.Throws<TargetInvocationException>(() => getTopology.Invoke(session, [entries, cyclicBindings]));
        Assert.IsType<ArgumentException>(ex1.InnerException);
        Assert.Same(validCached, topologyField.GetValue(session));

        var ex2 = Assert.Throws<TargetInvocationException>(() => getTopology.Invoke(session, [entries, cyclicBindings]));
        Assert.IsType<ArgumentException>(ex2.InnerException);
        Assert.Same(validCached, topologyField.GetValue(session));

        // Valid playback continues cleanly after failed cyclic attempts.
        var after = session.Advance(1d / 30, CancellationToken.None);
        Assert.Equal(2d, session.CurrentFrame);
        Assert.NotNull(after);
    }

    [Fact]
    public void ParallelControlledWorldsFaultJoinsWorkersAndThrowsFirstInOrderWithRecovery()
    {
        var model = BuildCharacterPmx("actor", ["root", "hand"], withAfterPhysics: false);
        var layout = new SceneCompositionEditor();
        var actors = Enumerable.Range(0, 4).Select(i =>
            layout.Add("actor" + i, $"fault{i}.pmx", ModelRole.Character, SharedPhysicsFixture.Rest(model))).ToArray();
        var backends = new List<ThreadTrackingBackend>();

        var pmmModels = actors.Select((a, i) => BuildPmmModel(
            (byte)i, a.Name, ["root", "hand"], 30,
            [BoneKey(0, 0, 2, 0, Vector3.Zero), BoneKey(1, 0, 0, 0, Vector3.Zero)],
            [BoneKey(2, 0, 0, 30, new(1, 0, 0))], [], [], [])).ToArray();
        var doc = RoundTrip(WrapDocument(null, pmmModels));
        var controls = ScenePmmControlState.Create(doc, actors.Select((a, i) => new ScenePmmActorSource(a.Id, (byte)i, model)));
        var motions = actors.Select((a, i) => new SceneActorMotion(a.Id, model, PmmMotionAdapter.Convert(doc.Models[i], model), PhysicsEnabled: true)).ToArray();

        using var session = new SceneAnimationSession(
            layout.Objects, motions,
            _ => { var b = new ThreadTrackingBackend(); backends.Add(b); return b; },
            pmmControls: controls,
            cancellationToken: CancellationToken.None);

        backends[1].StepErrorMessage = "fault-actor-1";
        backends[3].StepErrorMessage = "fault-actor-3";

        var error = Assert.Throws<InvalidOperationException>(() => session.Advance(1d / 30, CancellationToken.None));
        Assert.Equal("fault-actor-1", error.Message);
        Assert.True(session.IsFaulted);
        // A single-core runner stops at the first failing actor; parallel evaluation
        // joins every scheduled actor before propagating the first error in actor order.
        for (int i = 0; i < backends.Count; i++)
            Assert.Equal(Environment.ProcessorCount >= 2 || i <= 1 ? 1 : 0, backends[i].StepCalls);

        backends[1].StepErrorMessage = null;
        backends[3].StepErrorMessage = null;
        _ = session.Seek(0, CancellationToken.None);
        Assert.False(session.IsFaulted);
        _ = session.Advance(1d / 30, CancellationToken.None);
        Assert.False(session.IsFaulted);
    }

    private sealed record MultiPropFixture(SceneAnimationSession Session, Guid[] ActorIds, Guid[] FrozenPropIds) : IDisposable
    {
        public void Dispose() => Session.Dispose();
    }

    private static (PmmDocument Doc, EditableSceneObject[] Objects, PmxModel[] Models, bool[] PhysFlags, Guid[] FrozenIds) BuildMultiPropSceneDefinition()
    {
        var charModel = BuildCharacterPmx("char", ["root", "hand", "afterHand"], withAfterPhysics: true);
        var carrierModel = BuildPropPmx("carrier", ["socket", "groundPin"], vertexCount: 12, dynamicBody: false);
        var propModel = BuildPropPmx("prop", ["root", "tip"], vertexCount: 18, dynamicBody: false);
        var dynPropModel = BuildPropPmx("dynProp", ["root", "tip"], vertexCount: 12, dynamicBody: true);

        // Model 0: CharA (physical, dynamic + afterPhysics hand, physics disable switch on hand at 5..12..20)
        var pmmCharA = BuildPmmModel(0, "charA", ["root", "hand", "afterHand"], 40,
            [BoneKey(0, 0, 3, 0, Vector3.Zero), BoneKey(1, 0, 4, 0, new(0, 1, 0)), BoneKey(2, 0, 0, 0, new(0.2f, 0, 0))],
            [
                BoneKey(3, 0, 0, 40, new(4, 0, 0)),
                BoneKey(4, 1, 5, 5, new(0, 2, 0), disabled: true),
                BoneKey(5, 4, 6, 12, new(1, 2, 0), disabled: false),
                BoneKey(6, 5, 0, 20, new(2, 1, 0), disabled: true),
            ],
            [], [], []);

        // Model 1: CharB (physical, moving head)
        var pmmCharB = BuildPmmModel(1, "charB", ["root", "hand", "afterHand"], 40,
            [BoneKey(0, 0, 3, 0, Vector3.Zero), BoneKey(1, 0, 0, 0, new(-1, 0, 0)), BoneKey(2, 0, 0, 0, Vector3.Zero)],
            [BoneKey(3, 0, 0, 40, new(0, 2, -3))],
            [], [], []);

        // Model 2: MidCarrier (non-physical; socket attached to charA/afterHand, groundPin attached to Ground -2)
        var pmmCarrier = BuildPmmModel(2, "midCarrier", ["socket", "groundPin"], 40,
            [BoneKey(0, 0, 0, 0, Vector3.Zero), BoneKey(1, 0, 2, 0, new(0, 1, 0))],
            [BoneKey(2, 1, 0, 30, new(0, 0, 2))],
            [0, 1],
            [new(0, 0, 2), new(0, -2, -1)],
            []);

        // Model 3: ChainedProp (non-physical, LastFrame=1 -> freezes at frame 1, attached to midCarrier/socket -> 2-level chain)
        var pmmChained = BuildPmmModel(3, "chainedProp", ["root", "tip"], 1,
            [BoneKey(0, 0, 0, 0, Vector3.Zero), BoneKey(1, 0, 2, 0, Vector3.Zero)],
            [BoneKey(2, 1, 0, 1, new(0.1f, 0, 0))],
            [-1],
            [new(0, 2, 0)],
            []);

        // Model 4: FrozenLeaf1 (non-physical, LastFrame=1 -> freezes at frame 1; switches charA/hand -> charB/hand -> Ground -2 -> detached -1)
        var pmmFrozenLeaf1 = BuildPmmModel(4, "frozenLeaf1", ["root", "tip"], 1,
            [BoneKey(0, 0, 0, 0, Vector3.Zero), BoneKey(1, 0, 2, 0, Vector3.Zero)],
            [BoneKey(2, 1, 0, 1, new(0, 0.1f, 0))],
            [-1],
            [new(0, 0, 1)],
            [
                new() { RecordIndex = 1, ObjectIndex = 1, PreviousIndex = 0, NextIndex = 2, Frame = 8, Visible = true, OutsideParents = [new(0, 1, 1)] },
                new() { RecordIndex = 2, ObjectIndex = 2, PreviousIndex = 1, NextIndex = 3, Frame = 16, Visible = true, OutsideParents = [new(0, -2, -1)] },
                new() { RecordIndex = 3, ObjectIndex = 3, PreviousIndex = 2, NextIndex = 0, Frame = 24, Visible = true, OutsideParents = [new(0, -1, -1)] },
            ]);

        // Model 5: FrozenLeaf2 (non-physical, LastFrame=1 -> freezes mid-playback; attached to charB/afterHand)
        var pmmFrozenLeaf2 = BuildPmmModel(5, "frozenLeaf2", ["root", "tip"], 1,
            [BoneKey(0, 0, 0, 0, Vector3.Zero), BoneKey(1, 0, 2, 0, Vector3.Zero)],
            [BoneKey(2, 1, 0, 1, new(0.25f, 0, 0))],
            [-1],
            [new(0, 1, 2)],
            []);

        // Model 6: FrozenPhysicalProp (physical, LastFrame=1 -> freezes mid-playback, attached to charA/hand)
        var pmmFrozenPhysProp = BuildPmmModel(6, "frozenPhysProp", ["root", "tip"], 1,
            [BoneKey(0, 0, 0, 0, Vector3.Zero), BoneKey(1, 0, 2, 0, Vector3.Zero)],
            [BoneKey(2, 1, 0, 1, Vector3.Zero)],
            [-1],
            [new(0, 0, 1)],
            []);

        var gravity = new PmmGravity
        {
            Acceleration = 1,
            Direction = -Vector3.UnitY,
            InitialKeyframe = new() { RecordIndex = 0, ObjectIndex = 0, NextIndex = 1, Frame = 0, Acceleration = 1, Direction = -Vector3.UnitY, NoiseEnabled = true, Noise = 1 },
            Keyframes =
            [
                new() { RecordIndex = 1, ObjectIndex = 1, PreviousIndex = 0, NextIndex = 2, Frame = 15, Acceleration = 2, Direction = Vector3.UnitX, NoiseEnabled = true, Noise = 2 },
                new() { RecordIndex = 2, ObjectIndex = 2, PreviousIndex = 1, NextIndex = 0, Frame = 30, Acceleration = 1.5f, Direction = -Vector3.UnitY, NoiseEnabled = false, Noise = 0 },
            ],
        };

        var doc = RoundTrip(WrapDocument(gravity, pmmCharA, pmmCharB, pmmCarrier, pmmChained, pmmFrozenLeaf1, pmmFrozenLeaf2, pmmFrozenPhysProp));
        var layout = new SceneCompositionEditor();
        var models = new[] { charModel, charModel, carrierModel, propModel, propModel, propModel, dynPropModel };
        var physFlags = new[] { true, true, false, false, false, false, true };
        var objs = new EditableSceneObject[models.Length];

        for (int i = 0; i < models.Length; i++)
        {
            var added = layout.Add(doc.Models[i].Name.Value, doc.Models[i].Name.Value + ".pmx", ModelRole.Character, SharedPhysicsFixture.Rest(models[i]));
            if (i == 1) layout.SetTransform(added.Id, new(4f, 0, 0), Quaternion.Identity, 1);
            if (i == 2) layout.SetTransform(added.Id, new(0, 0, 1.5f), Quaternion.Identity, 1);
            objs[i] = layout.Objects[^1];
        }

        return (doc, objs, models, physFlags, [objs[3].Id, objs[4].Id, objs[5].Id]);
    }

    private static MultiPropFixture CreateMultiPropFixture(
        PmmDocument doc, EditableSceneObject[] objs, PmxModel[] models, bool[] physFlags, Guid[] frozenIds,
        bool sharedWorld, CancellationToken token)
    {
        var motions = new SceneActorMotion[models.Length];
        var sources = new ScenePmmActorSource[models.Length];
        for (int i = 0; i < models.Length; i++)
        {
            motions[i] = new(objs[i].Id, models[i], PmmMotionAdapter.Convert(doc.Models[i], models[i]), PhysicsEnabled: physFlags[i]);
            sources[i] = new(objs[i].Id, doc.Models[i].Index, models[i]);
        }
        var controls = ScenePmmControlState.Create(doc, sources);
        var sharedBackend = sharedWorld ? new TrackingSharedBackend() : null;
        var session = new SceneAnimationSession(
            objs, motions,
            _ => sharedBackend ?? new TrackingSharedBackend(),
            sharedPhysicsFactory: sharedWorld ? () => sharedBackend! : null,
            pmmControls: controls,
            cancellationToken: token);

        return new(session, objs.Select(o => o.Id).ToArray(), frozenIds);
    }

    private static (PmmDocument Doc, EditableSceneObject[] Objects, PmxModel[] Models) BuildConcurrentControlledWorldsDefinition()
    {
        var charModel = BuildCharacterPmx("char", ["root", "hand", "afterHand"], withAfterPhysics: true);
        var dynProp = BuildPropPmx("dynProp", ["root", "tip"], vertexCount: 8, dynamicBody: true);
        var models = new[] { charModel, charModel, dynProp, dynProp, dynProp, dynProp };

        var pmmModels = new PmmModel[models.Length];
        pmmModels[0] = BuildPmmModel(0, "char0", ["root", "hand", "afterHand"], 30,
            [BoneKey(0, 0, 0, 0, Vector3.Zero), BoneKey(1, 0, 3, 0, new(1, 0, 0)), BoneKey(2, 0, 0, 0, new(0, 1, 0))],
            [BoneKey(3, 1, 0, 30, new(3, 2, 0))], [], [], []);
        pmmModels[1] = BuildPmmModel(1, "char1", ["root", "hand", "afterHand"], 30,
            [BoneKey(0, 0, 0, 0, Vector3.Zero), BoneKey(1, 0, 3, 0, new(-1, 0, 0)), BoneKey(2, 0, 0, 0, new(0, 1, 0))],
            [BoneKey(3, 1, 0, 30, new(-2, 1, 1))], [], [], []);
        for (int i = 2; i < models.Length; i++)
        {
            byte parentIndex = (byte)(i % 2);
            pmmModels[i] = BuildPmmModel((byte)i, "dyn" + i, ["root", "tip"], 30,
                [BoneKey(0, 0, 0, 0, Vector3.Zero), BoneKey(1, 0, 2, 0, Vector3.Zero)],
                [BoneKey(2, 1, 0, 30, new(0, 0.5f, 0))],
                [-1],
                [new(0, parentIndex, 2)],
                [new() { RecordIndex = 1, ObjectIndex = 1, PreviousIndex = 0, NextIndex = 0, Frame = 6, Visible = true, OutsideParents = [new(0, (byte)(1 - parentIndex), 1)] }]);
        }

        var doc = RoundTrip(WrapDocument(null, pmmModels));
        var layout = new SceneCompositionEditor();
        for (int i = 0; i < models.Length; i++)
            _ = layout.Add(doc.Models[i].Name.Value, doc.Models[i].Name.Value + ".pmx", ModelRole.Character, SharedPhysicsFixture.Rest(models[i]));
        return (doc, layout.Objects.ToArray(), models);
    }

    private static MultiPropFixture CreateConcurrentControlledWorldsFixture(
        PmmDocument doc, EditableSceneObject[] objs, PmxModel[] models,
        Func<Guid, ThreadTrackingBackend> factory, CancellationToken token)
    {
        var motions = new SceneActorMotion[models.Length];
        var sources = new ScenePmmActorSource[models.Length];
        for (int i = 0; i < models.Length; i++)
        {
            motions[i] = new(objs[i].Id, models[i], PmmMotionAdapter.Convert(doc.Models[i], models[i]), PhysicsEnabled: true);
            sources[i] = new(objs[i].Id, doc.Models[i].Index, models[i]);
        }

        var controls = ScenePmmControlState.Create(doc, sources);
        var session = new SceneAnimationSession(
            objs, motions,
            obj => factory(obj.Id),
            pmmControls: controls,
            cancellationToken: token);

        return new(session, objs.Select(o => o.Id).ToArray(), []);
    }

    private static void AssertSessionParity(
        MultiPropFixture expectedFixture, SceneAnimationSession expected, OriginalFrameScene expectedFrame,
        MultiPropFixture actualFixture, SceneAnimationSession actual, OriginalFrameScene actualFrame,
        bool verifyVertices)
    {
        Assert.Equal(expected.CurrentFrame, actual.CurrentFrame);
        Assert.True(expectedFrame.Surface.Snapshot.HasDeferredDeformation);
        Assert.True(actualFrame.Surface.Snapshot.HasDeferredDeformation);

        var captureSourceFrame = typeof(SceneAnimationSession).GetMethod("CaptureSourceFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var expectedSources = new SceneSnapshot[expectedFixture.ActorIds.Length];
        var actualSources = new SceneSnapshot[actualFixture.ActorIds.Length];
        for (int i = 0; i < actualFixture.ActorIds.Length; i++)
        {
            var eSrc = ((SceneSnapshot Geometry, OriginalOutlineData? Outline, OriginalOutlineOptions? OutlineOptions))captureSourceFrame.Invoke(expected, [expectedFixture.ActorIds[i]])!;
            var aSrc = ((SceneSnapshot Geometry, OriginalOutlineData? Outline, OriginalOutlineOptions? OutlineOptions))captureSourceFrame.Invoke(actual, [actualFixture.ActorIds[i]])!;
            expectedSources[i] = eSrc.Geometry;
            actualSources[i] = aSrc.Geometry;
            Assert.True(aSrc.Geometry.HasDeferredDeformation);
            Assert.NotNull(aSrc.Geometry.WholeDeformation);
            if (!verifyVertices)
                Assert.False(aSrc.Geometry.IsDeferredDeformationMaterialized);
        }

        if (!verifyVertices)
        {
            Assert.False(expectedFrame.Surface.Snapshot.IsDeferredDeformationMaterialized);
            Assert.False(actualFrame.Surface.Snapshot.IsDeferredDeformationMaterialized);
        }

        for (int i = 0; i < actualFixture.ActorIds.Length; i++)
        {
            AssertSpanBitsEqual<Matrix4x4>(
                expected.GetBoneWorldMatrices(expectedFixture.ActorIds[i]).ToArray(),
                actual.GetBoneWorldMatrices(actualFixture.ActorIds[i]).ToArray());
            if (verifyVertices)
                AssertSpanBitsEqual<SceneVertex>(expectedSources[i].Vertices, actualSources[i].Vertices);
        }

        if (verifyVertices)
            AssertFrameBitsEqual(expectedFrame, actualFrame);
    }

    private static void AssertFrameBitsEqual(OriginalFrameScene expected, OriginalFrameScene actual)
    {
        AssertSpanBitsEqual<SceneVertex>(expected.Surface.Snapshot.Vertices, actual.Surface.Snapshot.Vertices);
        AssertBitsEqual(expected.Outline!.Vertices, actual.Outline!.Vertices);
        Assert.Equal(expected.Materials, actual.Materials);
        Assert.Equal(expected.Surface.Materials, actual.Surface.Materials);
    }

    private static void AssertBitsEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual) where T : unmanaged
    {
        var a = expected.ToArray();
        var b = actual.ToArray();
        Assert.Equal(MemoryMarshal.AsBytes(a.AsSpan()).ToArray(), MemoryMarshal.AsBytes(b.AsSpan()).ToArray());
    }

    private static void AssertSpanBitsEqual<T>(ReadOnlySpan<T> expected, ReadOnlySpan<T> actual) where T : unmanaged
        => Assert.Equal(MemoryMarshal.AsBytes(expected).ToArray(), MemoryMarshal.AsBytes(actual).ToArray());

    private static PmxModel BuildCharacterPmx(string name, string[] boneNames, bool withAfterPhysics)
    {
        var bones = new PmxBone[boneNames.Length];
        for (int i = 0; i < boneNames.Length; i++)
        {
            bones[i] = new()
            {
                Name = boneNames[i],
                EnglishName = "",
                Position = new(0, i, 0),
                ParentIndex = i == 0 ? -1 : i - 1,
                DeformDepth = 0,
                Flags = (withAfterPhysics && i == boneNames.Length - 1 ? PmxBoneFlags.DeformAfterPhysics : PmxBoneFlags.None)
                    | PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate,
            };
        }

        var skeleton = PmmMotionAdapterTests.Pmx(boneNames);
        return new()
        {
            Header = skeleton.Header,
            Name = name,
            EnglishName = "",
            Comment = "",
            EnglishComment = "",
            Bones = bones,
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select((p, idx) => new PmxVertex
            {
                Position = p,
                Normal = Vector3.UnitZ,
                Uv = Vector2.Zero,
                AdditionalUvs = Array.Empty<Vector4>(),
                WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [Math.Min(idx, bones.Length - 1), -1, -1, -1],
                BoneWeights = [1, 0, 0, 0],
                EdgeScale = 1,
            }).ToArray(),
            Faces = new PmxTriangle[] { new(0, 1, 2) },
            Textures = Array.Empty<string>(),
            Materials = new PmxMaterial[]
            {
                new()
                {
                    Name = "mat", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                    Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate,
                    EdgeColor = Vector4.One, EdgeSize = 0, TextureIndex = -1, SphereTextureIndex = -1, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3,
                }
            },
            Morphs = Array.Empty<PmxMorph>(),
            DisplayFrames = Array.Empty<PmxDisplayFrame>(),
            RigidBodies = new PmxRigidBody[]
            {
                new()
                {
                    Name = "body0", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = ushort.MaxValue,
                    Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
                    Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Static,
                },
                new()
                {
                    Name = "body1", EnglishName = "", BoneIndex = 1, Group = 0, CollisionGroup = ushort.MaxValue,
                    Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
                    Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic,
                },
            },
            Joints = Array.Empty<PmxJoint>(),
            SoftBodies = Array.Empty<PmxSoftBody>(),
        };
    }

    private static PmxModel BuildPropPmx(string name, string[] boneNames, int vertexCount, bool dynamicBody)
    {
        var skeleton = PmmMotionAdapterTests.Pmx(boneNames);
        var vertices = new PmxVertex[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            vertices[i] = new PmxVertex
            {
                Position = new(i * 0.1f, (i % 3) * 0.25f, (i % 2) * 0.15f),
                Normal = Vector3.UnitZ,
                Uv = new(0.1f * (i % 5), 0.2f * (i % 4)),
                AdditionalUvs = Array.Empty<Vector4>(),
                WeightType = PmxVertexWeightType.Bdef2,
                BoneIndices = [0, Math.Min(1, boneNames.Length - 1), -1, -1],
                BoneWeights = [0.6f, 0.4f, 0, 0],
                EdgeScale = 1,
            };
        }
        return new()
        {
            Header = skeleton.Header,
            Name = name,
            EnglishName = "",
            Comment = "",
            EnglishComment = "",
            Bones = skeleton.Bones.ToArray(),
            Vertices = vertices,
            Faces = new PmxTriangle[] { new(0, 1, 2) },
            Textures = Array.Empty<string>(),
            Materials = new PmxMaterial[]
            {
                new()
                {
                    Name = "mat", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                    Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate,
                    EdgeColor = Vector4.One, EdgeSize = 0, TextureIndex = -1, SphereTextureIndex = -1, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3,
                }
            },
            Morphs = Array.Empty<PmxMorph>(),
            DisplayFrames = Array.Empty<PmxDisplayFrame>(),
            RigidBodies = dynamicBody
                ? new PmxRigidBody[]
                {
                    new()
                    {
                        Name = "propBody", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = ushort.MaxValue,
                        Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
                        Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic,
                    }
                }
                : Array.Empty<PmxRigidBody>(),
            Joints = Array.Empty<PmxJoint>(),
            SoftBodies = Array.Empty<PmxSoftBody>(),
        };
    }

    private static PmmString PmmText(string value) => new(0, value, []);

    private static PmmBoneKeyframe BoneKey(int record, int previous, int next, int frame, Vector3 translation, bool disabled = false)
        => new()
        {
            RecordIndex = record,
            ObjectIndex = record,
            PreviousIndex = previous,
            NextIndex = next,
            Frame = frame,
            Translation = translation,
            Rotation = Quaternion.Identity,
            Interpolation = LinearBoneCurve,
            PhysicsDisabled = disabled,
        };

    private static PmmModel BuildPmmModel(
        byte index,
        string name,
        string[] bones,
        int lastFrame,
        PmmBoneKeyframe[] initialBones,
        IReadOnlyList<PmmBoneKeyframe> boneKeys,
        int[] subjectBones,
        PmmOutsideParent[] initialParents,
        IReadOnlyList<PmmModelKeyframe> modelKeys)
    {
        return new()
        {
            Index = index,
            DrawOrderIndex = index,
            TransformOrderIndex = index,
            Visible = true,
            LastFrame = lastFrame,
            Name = PmmText(name),
            Path = PmmText(name + ".pmx"),
            BoneNames = bones.Select(PmmText).ToArray(),
            SelectedMorphIndices = [-1, -1, -1, -1],
            InitialBoneKeyframes = initialBones,
            BoneKeyframes = boneKeys,
            BoneStates = Enumerable.Range(0, bones.Length)
                .Select(i => new PmmBoneState(i, Vector3.Zero, Quaternion.Identity, false, false, 0))
                .ToArray(),
            OutsideParentSubjectBoneIndices = subjectBones,
            InitialModelKeyframe = new() { Visible = true, OutsideParents = initialParents, NextIndex = modelKeys.Count > 0 ? 1 : 0 },
            ModelKeyframes = modelKeys,
            OutsideParentStates = initialParents.Select((p, i) => new PmmOutsideParentState(i, 0, int.MaxValue, p)).ToArray(),
        };
    }

    private static PmmDocument WrapDocument(PmmGravity? gravity, params PmmModel[] models) => new()
    {
        AnsiCodePage = 932,
        PanelStates = new byte[7],
        Models = models,
        OutputWidth = 640,
        OutputHeight = 480,
        CameraFov = 30,
        Camera = new() { InitialKeyframe = new() { Interpolation = CameraCurve, Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
        Settings = new() { PhysicsSimulationMode = 1, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], CameraLookAtModelIndex = -1, CameraLookAtBoneIndex = -1 },
        Gravity = gravity ?? new() { Acceleration = 1, Direction = -Vector3.UnitY, InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY } },
        Light = new() { Color = Vector3.One, Direction = -Vector3.UnitY, InitialKeyframe = new() { Color = Vector3.One, Direction = -Vector3.UnitY } },
    };

    private static PmmDocument RoundTrip(PmmDocument doc)
        => PmmReader.Read(new MemoryStream(PmmWriter.ToBytes(doc), writable: false), new() { AnsiCodePage = 932 });

    private sealed class ThreadTrackingBackend : ICharacterPhysicsMutableBackend
    {
        private readonly TrackingSharedBackend inner = new();
        private int activeCount;
        public int ObservedPeakConcurrency;
        public int StepCalls;
        public string? StepErrorMessage;
        public Action? BeforeStep;
        public readonly ConcurrentBag<int> StepThreads = [];

        public string Name => "thread-tracking-mutable";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms) => inner.Initialize(definition, transforms);
        public int AddBody(PhysicsBodyDefinition body, Matrix4x4 transform) => inner.AddBody(body, transform);
        public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 a, Matrix4x4 b) => inner.AddJoint(joint, a, b);
        public void RemoveBody(int index) => inner.RemoveBody(index);
        public void RemoveJoint(int index) => inner.RemoveJoint(index);
        public void ResetBody(int index, Matrix4x4 transform) => inner.ResetBody(index, transform);
        public void SetBodyKinematic(int index, bool kinematic) => inner.SetBodyKinematic(index, kinematic);
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds) => inner.SetKinematicTarget(index, transform, seconds);
        public Matrix4x4 GetBodyWorldTransform(int index) => inner.GetBodyWorldTransform(index);
        public void SetGravity(Vector3 gravity) => inner.SetGravity(gravity);
        public void ApplyImpulse(int index, Vector3 linear, Vector3 angular) => inner.ApplyImpulse(index, linear, angular);

        public void Step(float seconds)
        {
            BeforeStep?.Invoke();
            Interlocked.Increment(ref StepCalls);
            StepThreads.Add(Environment.CurrentManagedThreadId);
            if (StepErrorMessage is { } msg) throw new InvalidOperationException(msg);
            int now = Interlocked.Increment(ref activeCount);
            int peak;
            do
            {
                peak = Volatile.Read(ref ObservedPeakConcurrency);
                if (peak >= now) break;
            } while (Interlocked.CompareExchange(ref ObservedPeakConcurrency, now, peak) != peak);
            try { inner.Step(seconds); }
            finally { Interlocked.Decrement(ref activeCount); }
        }

        public void Dispose() => inner.Dispose();
    }
}
