using System.Collections.ObjectModel;
using System.Numerics;
using System.Reflection;
using Mmd.Modern.Animation;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmControlCachingParityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly byte[] LinearBoneCurve = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(b => b).ToArray();
    private static readonly byte[] CameraCurve = Enumerable.Repeat(new byte[] { 20, 107, 20, 107 }, 6).SelectMany(b => b).ToArray();

    private static readonly MethodInfo ControlSampleMethod = typeof(SceneAnimationSession).GetMethod("ControlSample", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ControlParentsMethod = typeof(SceneAnimationSession).GetMethod("ControlParents", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ControlGroundParentsMethod = typeof(SceneAnimationSession).GetMethod("ControlGroundParents", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ControlTrackWindowsMethod = typeof(SceneAnimationSession).GetMethod("ControlTrackWindows", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ControlDisabledMethod = typeof(SceneAnimationSession).GetMethod("ControlDisabled", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ActiveBoneIndicesMethod = typeof(PmmPhysicsControlTracks).GetMethod("GetActiveBoneIndices", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void EmptyTracksReturnStableZeroAllocationDefaultsAndRejectInvalidTime()
    {
        var emptyDoc = WrapDocument();
        var emptyTracks = new PmmPhysicsControlTracks(emptyDoc);
        Assert.Equal(0u, emptyTracks.DurationFrames);
        Assert.Throws<KeyNotFoundException>(() => emptyTracks.DisabledBonesAt(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => emptyTracks.DisabledBonesAt(0, -3));
        Assert.Throws<ArgumentOutOfRangeException>(() => emptyTracks.GravityAt(-3));

        var noSwitchModel = BuildModel(
            index: 3,
            name: "plain",
            bones: ["root", "spine", "head", "arm", "hand", "leg"],
            lastFrame: 0,
            initialBones: Enumerable.Range(0, 6).Select(i => BoneKey(i, 0, 0, 0, Vector3.Zero)).ToArray(),
            boneKeys: [],
            boneDisabledStates: [],
            subjectBones: [],
            initialParents: [],
            modelKeys: []);
        var plainDoc = RoundTrip(WrapDocument(noSwitchModel));
        var tracks = new PmmPhysicsControlTracks(plainDoc);
        Assert.Empty((IReadOnlyList<int>)ActiveBoneIndicesMethod.Invoke(tracks, [3])!);

        var d0 = tracks.DisabledBonesAt(3, 0);
        var dFrac = tracks.DisabledBonesAt(3, 0.25);
        var d100 = tracks.DisabledBonesAt(3, 100);
        var dSaved = tracks.DisabledBonesAt(3, 5, saved: true);
        Assert.Empty(d0);
        Assert.Same(d0, dFrac);
        Assert.Same(d0, d100);
        Assert.Same(d0, tracks.DisabledBonesAt(3, 5));
        Assert.Same(d0, tracks.DisabledBonesAt(3, 0));
        Assert.Same(d0, dSaved);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)d0).Add(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracks.DisabledBonesAt(3, -3));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracks.DisabledBonesAt(3, double.NaN));

        var pmx = BuildPmx("plain", ["hand", "arm", "head", "spine", "root", "leg"]);
        var actorId = Guid.NewGuid();
        var state = ScenePmmControlState.Create(plainDoc, [new(actorId, 3, pmx)]);
        Assert.Equal([0], state.BoundaryFrames);

        var s100a = state.Sample(100);
        var s5 = state.Sample(5);
        var s100b = state.Sample(100);
        var s0 = state.Sample(0);
        var sSaved0 = state.Sample(0, saved: true);
        var sSaved100 = state.Sample(100, saved: true);
        Assert.Same(s100a, s5);
        Assert.Same(s100a, s100b);
        Assert.Same(s100a, s0);
        Assert.Same(sSaved0, sSaved100);
        Assert.Empty(s100a.DisabledBoneIndices[actorId]);
        Assert.Empty(s100a.OutsideParents);
        Assert.Empty(s100a.GroundOutsideParents);
        Assert.Empty(s100a.OutsideParentTrackWindows);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Sample(-3));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Sample(-3, saved: true));

        var layout = new SceneCompositionEditor();
        var obj = layout.Add("plain", "plain.pmx", ModelRole.Character, SharedPhysicsFixture.Rest(pmx));
        var motion = new SceneActorMotion(obj.Id, pmx, PmmMotionAdapter.Convert(plainDoc.Models[0], pmx), PhysicsEnabled: false);
        var controlState = ScenePmmControlState.Create(plainDoc, [new(obj.Id, 3, pmx)]);
        using var controlledSession = new SceneAnimationSession(
            layout.Objects, [motion], _ => throw new InvalidOperationException(), pmmControls: controlState, cancellationToken: Token);

        var p100a = InvokeControlParents(controlledSession, 100);
        var p5 = InvokeControlParents(controlledSession, 5);
        var p100b = InvokeControlParents(controlledSession, 100);
        var p0 = InvokeControlParents(controlledSession, 0);
        Assert.Empty(p100a);
        Assert.Same(p100a, p5);
        Assert.Same(p100a, p100b);
        Assert.Same(p100a, p0);

        var g100 = InvokeControlGroundParents(controlledSession, obj.Id, 100);
        var g0 = InvokeControlGroundParents(controlledSession, obj.Id, 0);
        Assert.Empty(g100);
        Assert.Same(g100, g0);

        Assert.Null(InvokeControlTrackWindows(controlledSession, obj.Id, 100));
        var dis100 = InvokeControlDisabled(controlledSession, obj.Id, 100);
        var dis0 = InvokeControlDisabled(controlledSession, obj.Id, 0);
        Assert.Empty(dis100);
        Assert.Same(dis100, dis0);

        Assert.Throws<ArgumentOutOfRangeException>(() => InvokeUnwrapped(() => ControlSampleMethod.Invoke(controlledSession, [-3.0])));
        Assert.Throws<ArgumentOutOfRangeException>(() => InvokeUnwrapped(() => ControlParentsMethod.Invoke(controlledSession, [-3.0])));
        Assert.Throws<ArgumentOutOfRangeException>(() => InvokeUnwrapped(() => ControlGroundParentsMethod.Invoke(controlledSession, [obj.Id, -3.0])));
        Assert.Throws<ArgumentOutOfRangeException>(() => InvokeUnwrapped(() => ControlTrackWindowsMethod.Invoke(controlledSession, [obj.Id, -3.0])));
        Assert.Throws<ArgumentOutOfRangeException>(() => InvokeUnwrapped(() => ControlDisabledMethod.Invoke(controlledSession, [obj.Id, -3.0])));

        // Session without PMM controls returns stable static/empty defaults even across frames.
        using var plainSession = new SceneAnimationSession(
            layout.Objects, [motion], _ => throw new InvalidOperationException(), cancellationToken: Token);
        Assert.Same(InvokeControlParents(plainSession, 100), InvokeControlParents(plainSession, 0));
        Assert.Same(InvokeControlGroundParents(plainSession, obj.Id, 100), InvokeControlGroundParents(plainSession, obj.Id, 0));
        Assert.Null(InvokeControlTrackWindows(plainSession, obj.Id, 5));
        Assert.Same(InvokeControlDisabled(plainSession, obj.Id, 100), InvokeControlDisabled(plainSession, obj.Id, 0));
    }

    [Fact]
    public void MultiActorSparseTracksMatchUncachedOracleAcrossBoundaryAndFractionalFrames()
    {
        var fixture = CreateRichMultiActorFixture();
        var tracks = new PmmPhysicsControlTracks(fixture.Document);

        // Verify ActiveBoneIndices only includes bones that actually have disabled switches.
        var activeParent = (IReadOnlyList<int>)ActiveBoneIndicesMethod.Invoke(tracks, [0])!;
        var activeChildA = (IReadOnlyList<int>)ActiveBoneIndicesMethod.Invoke(tracks, [1])!;
        var activeChildB = (IReadOnlyList<int>)ActiveBoneIndicesMethod.Invoke(tracks, [2])!;
        Assert.Equal([1, 2], activeParent); // parent: root(0) never disabled; hand(1), dummy(2) have switches
        Assert.Equal([2], activeChildA);    // childA: hip(0), unused(3) never disabled; arm(1) duplicate frame 30 cancels to false; prop(2) active
        Assert.Equal([1], activeChildB);    // childB: base(0) never disabled; tip(1) has switch

        // Verify BoundaryFrames preserves every model keyframe frame + 0 (including IK-only key at frame 8).
        Assert.Equal([0, 8, 10, 15, 30, 35], fixture.State.BoundaryFrames);

        var testFrames = new List<double>();
        for (int f = 0; f <= 70; f++) testFrames.Add(f);
        for (int step = 0; step <= 260; step++) testFrames.Add(step * 0.25);
        testFrames.AddRange([4.9999, 5.0, 7.9999, 8.0, 9.9999, 10.0, 11.9999, 12.0, 14.9999, 15.0, 21.9999, 22.0, 29.9999, 30.0, 34.9999, 35.0, 100.0, double.MaxValue]);

        var mapByModel = fixture.Document.Models.ToDictionary(m => m.Index, m => fixture.Actors.Single(a => a.PmmModelIndex == m.Index));
        foreach (bool saved in new[] { false, true })
        {
            foreach (double frame in testFrames)
            {
                // 1. PmmPhysicsControlTracks parity
                foreach (var model in fixture.Document.Models)
                {
                    var expectedDisabled = ReferenceDisabledBonesAt(model, frame, saved);
                    var actualDisabled = tracks.DisabledBonesAt(model.Index, frame, saved);
                    Assert.Equal(expectedDisabled, actualDisabled);
                }
                Assert.Equal(ReferenceGravityAt(fixture.Document.Gravity, frame, saved), saved ? tracks.SavedGravity : tracks.GravityAt(frame));

                // 2. ScenePmmControlState.Sample parity
                var actualSample = fixture.State.Sample(frame, saved);
                var expectedSample = ReferenceSample(fixture.Document, mapByModel, frame, saved);
                AssertSamplesEqual(expectedSample, actualSample);

                // 3. SceneAnimationSession.PmmControls parity (timeline evaluation, saved = false)
                if (!saved && !double.IsInfinity(frame) && frame <= 1000)
                {
                    Assert.Equal(expectedSample.OutsideParents, InvokeControlParents(fixture.Session, frame));
                    AssertSamplesEqual(expectedSample, InvokeControlSample(fixture.Session, frame));
                    foreach (var actor in fixture.Actors)
                    {
                        var expectedGround = expectedSample.GroundOutsideParents.Where(g => g.ObjectId == actor.ObjectId).ToArray();
                        Assert.Equal(expectedGround, InvokeControlGroundParents(fixture.Session, actor.ObjectId, frame));

                        expectedSample.OutsideParentTrackWindows.TryGetValue(actor.ObjectId, out var expectedWin);
                        AssertWindowsEqual(expectedWin, InvokeControlTrackWindows(fixture.Session, actor.ObjectId, frame));

                        var expectedDisSet = expectedSample.DisabledBoneIndices[actor.ObjectId].ToHashSet();
                        var actualDisSet = InvokeControlDisabled(fixture.Session, actor.ObjectId, frame);
                        Assert.True(expectedDisSet.SetEquals(actualDisSet));
                    }
                }
            }
        }
    }

    [Fact]
    public void WithinIntervalQueriesReturnStableReferencesAndOutOfOrderJumpsInvalidateCleanly()
    {
        var fixture = CreateRichMultiActorFixture();
        var tracks = new PmmPhysicsControlTracks(fixture.Document);

        // 1. PmmPhysicsControlTracks cache stability, boundary invalidation, and 100 -> 5 -> 100 -> 0 -> -3
        var p0 = tracks.DisabledBonesAt(1, 0);
        var p4_75 = tracks.DisabledBonesAt(1, 4.75);
        var p10 = tracks.DisabledBonesAt(1, 10);
        var p29_75 = tracks.DisabledBonesAt(1, 29.75);
        var p30 = tracks.DisabledBonesAt(1, 30);
        var p100a = tracks.DisabledBonesAt(1, 100);
        var p5 = tracks.DisabledBonesAt(1, 5);
        var p100b = tracks.DisabledBonesAt(1, 100);
        var p0Again = tracks.DisabledBonesAt(1, 0);

        Assert.Same(p0, p4_75);
        Assert.NotSame(p4_75, p10);
        Assert.Same(p10, p29_75);
        Assert.NotSame(p29_75, p30);
        Assert.Same(p100a, p100b);
        Assert.Same(p0, p5);
        Assert.Same(p0, p0Again);
        Assert.Throws<ArgumentOutOfRangeException>(() => tracks.DisabledBonesAt(1, -3));
        Assert.Same(p100a, tracks.DisabledBonesAt(1, 100));
        Assert.Same(p0, tracks.DisabledBonesAt(1, 5));

        // 2. ScenePmmControlState: interpolating gravity keeps discrete sub-collections identical;
        //    constant gravity segment returns the exact same ScenePmmControlSample reference.
        var s1_25 = fixture.State.Sample(1.25);
        var s2_50 = fixture.State.Sample(2.50);
        Assert.NotEqual(s1_25.Gravity, s2_50.Gravity); // gravity interpolates between frame 0 and 20
        Assert.Same(s1_25.DisabledBoneIndices, s2_50.DisabledBoneIndices);
        Assert.Same(s1_25.DisabledBoneIndices[fixture.ChildA], s2_50.DisabledBoneIndices[fixture.ChildA]);
        Assert.Same(s1_25.OutsideParents, s2_50.OutsideParents);
        Assert.Same(s1_25.GroundOutsideParents, s2_50.GroundOutsideParents);
        Assert.Same(s1_25.OutsideParentTrackWindows, s2_50.OutsideParentTrackWindows);
        Assert.Same(s1_25.OutsideParentTrackWindows[fixture.ChildA], s2_50.OutsideParentTrackWindows[fixture.ChildA]);

        // Crossing IK-only boundary at frame 8 keeps OutsideParents / Ground / TrackWindows identical.
        var s7_75 = fixture.State.Sample(7.75);
        var s8_25 = fixture.State.Sample(8.25);
        Assert.Same(s7_75.OutsideParents, s8_25.OutsideParents);
        Assert.Same(s7_75.GroundOutsideParents, s8_25.GroundOutsideParents);
        Assert.Same(s7_75.OutsideParentTrackWindows, s8_25.OutsideParentTrackWindows);

        // Crossing physics-only boundary at frame 5 invalidates DisabledBoneIndices while keeping parent tracks stable.
        var s4_75 = fixture.State.Sample(4.75);
        var s5_25 = fixture.State.Sample(5.25);
        Assert.NotSame(s4_75.DisabledBoneIndices, s5_25.DisabledBoneIndices);
        Assert.Same(s4_75.OutsideParents, s5_25.OutsideParents);
        Assert.Same(s4_75.GroundOutsideParents, s5_25.GroundOutsideParents);
        Assert.Same(s4_75.OutsideParentTrackWindows, s5_25.OutsideParentTrackWindows);

        // Crossing parent-only boundary at frame 35 invalidates OutsideParents while keeping DisabledBoneIndices stable.
        var s34_75 = fixture.State.Sample(34.75);
        var s35_25 = fixture.State.Sample(35.25);
        Assert.NotSame(s34_75.OutsideParents, s35_25.OutsideParents);
        Assert.Same(s34_75.DisabledBoneIndices, s35_25.DisabledBoneIndices);

        // Constant gravity flat segment [40, 60] (with physics switch at 50) reuses exact ScenePmmControlSample within each sub-interval.
        var s42 = fixture.State.Sample(42);
        var s48 = fixture.State.Sample(48);
        var s52 = fixture.State.Sample(52);
        var s58 = fixture.State.Sample(58);
        Assert.Same(s42, s48);
        Assert.NotSame(s42, s52);
        Assert.Same(s52, s58);

        // Out-of-order jumps 100 -> 5 -> 100 -> 0 -> -3 on ScenePmmControlState:
        var c100a = fixture.State.Sample(100);
        var c5 = fixture.State.Sample(5);
        var c100b = fixture.State.Sample(100);
        var c0 = fixture.State.Sample(0);
        Assert.Same(c100a.DisabledBoneIndices, c100b.DisabledBoneIndices);
        Assert.Same(c100a.OutsideParents, c100b.OutsideParents);
        Assert.Same(c100a.GroundOutsideParents, c100b.GroundOutsideParents);
        Assert.Same(c100a.OutsideParentTrackWindows, c100b.OutsideParentTrackWindows);
        Assert.NotSame(c100a.OutsideParents, c5.OutsideParents);
        Assert.Same(c0.OutsideParents, c5.OutsideParents);
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.State.Sample(-3));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.State.Sample(-3, saved: true));
        Assert.Same(c100a.OutsideParents, fixture.State.Sample(100).OutsideParents);

        // 3. SceneAnimationSession out-of-order jumps 100 -> 5 -> 100 -> 0 -> -3
        var sp100a = InvokeControlParents(fixture.Session, 100);
        var sp5 = InvokeControlParents(fixture.Session, 5);
        var sp100b = InvokeControlParents(fixture.Session, 100);
        var sp0 = InvokeControlParents(fixture.Session, 0);
        Assert.Same(sp100a, sp100b);
        Assert.Same(sp0, sp5);
        Assert.NotSame(sp100a, sp5);

        var sg100a = InvokeControlGroundParents(fixture.Session, fixture.ChildA, 100);
        var sg5 = InvokeControlGroundParents(fixture.Session, fixture.ChildA, 5);
        var sg100b = InvokeControlGroundParents(fixture.Session, fixture.ChildA, 100);
        var sg0 = InvokeControlGroundParents(fixture.Session, fixture.ChildA, 0);
        Assert.Same(sg100a, sg100b);
        Assert.Same(sg0, sg5);
        Assert.NotSame(sg100a, sg5);

        var sd100a = InvokeControlDisabled(fixture.Session, fixture.Parent, 100);
        var sd5 = InvokeControlDisabled(fixture.Session, fixture.Parent, 5);
        var sd100b = InvokeControlDisabled(fixture.Session, fixture.Parent, 100);
        var sd0 = InvokeControlDisabled(fixture.Session, fixture.Parent, 0);
        Assert.Same(sd100a, sd100b);
        Assert.NotSame(sd0, sd5);

        Assert.Throws<ArgumentOutOfRangeException>(() => InvokeUnwrapped(() => ControlParentsMethod.Invoke(fixture.Session, [-3.0])));
        Assert.Throws<ArgumentOutOfRangeException>(() => InvokeUnwrapped(() => ControlDisabledMethod.Invoke(fixture.Session, [fixture.Parent, -3.0])));
        Assert.Same(sp100a, InvokeControlParents(fixture.Session, 100));
        Assert.Same(sd5, InvokeControlDisabled(fixture.Session, fixture.Parent, 5));

        // 4. Static outside parent + PMM outside parent combination and conflict validation
        // childA's PMX has ["unused"(0), "prop"(1), "arm"(2), "hip"(3)]; unused(0) is never authored by PMM.
        var disjointStatic = new SceneOutsideParentBinding(fixture.ChildA, 0, fixture.Parent, 2, Matrix4x4.Identity);
        using var combinedSession = fixture.CreateSession([disjointStatic]);
        var cp100a = InvokeControlParents(combinedSession, 100);
        var cp5 = InvokeControlParents(combinedSession, 5);
        var cp100b = InvokeControlParents(combinedSession, 100);
        Assert.Same(cp100a, cp100b);
        Assert.Contains(disjointStatic, cp100a);
        Assert.Contains(disjointStatic, cp5);

        // Conflicting static binding on ground-authored bone (childA/prop = PMX bone 1 at frame 0) must throw InvalidDataException.
        var conflictingStatic = new SceneOutsideParentBinding(fixture.ChildA, 1, fixture.Parent, 2, Matrix4x4.Identity);
        Assert.Throws<InvalidDataException>(() => fixture.CreateSession([conflictingStatic]));
    }

    [Fact]
    public void InitialKeyPastZeroDuplicateBoneFramesAndLargeLazyFallbackMatchReference()
    {
        // Bone 0: Initial keyframe at frame 10 (> 0) with PhysicsDisabled=true, key at frame 20 with PhysicsDisabled=false.
        // Bone 1: Duplicate keys at frame 5 (true then false) -> last duplicate wins -> never disabled after dedup.
        // Bone 2: Alternating keys at frames 0, 4, 8, 12.
        var model = new PmmModel
        {
            Index = 9,
            BoneNames = [PmmText("b0"), PmmText("b1"), PmmText("b2")],
            InitialBoneKeyframes =
            [
                new() { RecordIndex = 0, ObjectIndex = 0, NextIndex = 3, Frame = 10, PhysicsDisabled = true },
                new() { RecordIndex = 1, ObjectIndex = 1, NextIndex = 4, Frame = 0, PhysicsDisabled = false },
                new() { RecordIndex = 2, ObjectIndex = 2, NextIndex = 6, Frame = 0, PhysicsDisabled = false },
            ],
            BoneKeyframes =
            [
                new() { RecordIndex = 3, ObjectIndex = 3, PreviousIndex = 0, NextIndex = 0, Frame = 20, PhysicsDisabled = false },
                new() { RecordIndex = 4, ObjectIndex = 4, PreviousIndex = 1, NextIndex = 5, Frame = 5, PhysicsDisabled = true },
                new() { RecordIndex = 5, ObjectIndex = 5, PreviousIndex = 4, NextIndex = 0, Frame = 5, PhysicsDisabled = false },
                new() { RecordIndex = 6, ObjectIndex = 6, PreviousIndex = 2, NextIndex = 7, Frame = 4, PhysicsDisabled = true },
                new() { RecordIndex = 7, ObjectIndex = 7, PreviousIndex = 6, NextIndex = 8, Frame = 8, PhysicsDisabled = false },
                new() { RecordIndex = 8, ObjectIndex = 8, PreviousIndex = 7, NextIndex = 0, Frame = 12, PhysicsDisabled = true },
            ],
            BoneStates =
            [
                new(0, Vector3.Zero, Quaternion.Identity, false, false, 0),
                new(1, Vector3.Zero, Quaternion.Identity, false, false, 0),
                new(2, Vector3.Zero, Quaternion.Identity, false, false, 0),
            ],
        };
        var tracks = new PmmPhysicsControlTracks(new PmmDocument { Models = [model] });
        Assert.Equal([0, 2], (IReadOnlyList<int>)ActiveBoneIndicesMethod.Invoke(tracks, [9])!);
        foreach (double frame in new[] { 0.0, 3.99, 4.0, 5.0, 7.99, 8.0, 10.0, 11.99, 12.0, 19.99, 20.0, 50.0 })
            Assert.Equal(ReferenceDisabledBonesAt(model, frame, saved: false), tracks.DisabledBonesAt(9, frame));

        // Large synthetic model (> 250_000 matrix entries) triggers lazy Interlocked.CompareExchange interval path.
        const int boneCount = 300;
        const int transitionFrames = 900; // 300 * 901 = 270,300 > 250,000
        var largeInitial = new PmmBoneKeyframe[boneCount];
        var largeExtra = new List<PmmBoneKeyframe>(transitionFrames);
        int recordId = boneCount;
        for (int b = 0; b < boneCount; b++)
        {
            largeInitial[b] = new() { RecordIndex = b, ObjectIndex = b, Frame = 0, PhysicsDisabled = false };
        }
        var prevRecord = Enumerable.Range(0, boneCount).ToArray();
        for (int step = 1; step <= transitionFrames; step++)
        {
            int b = step % boneCount;
            int prev = prevRecord[b];
            int cur = recordId++;
            if (prev < boneCount) largeInitial[prev].NextIndex = cur;
            else largeExtra[prev - boneCount].NextIndex = cur;
            largeExtra.Add(new()
            {
                RecordIndex = cur, ObjectIndex = cur, PreviousIndex = prev, NextIndex = 0,
                Frame = step * 2, PhysicsDisabled = (step / boneCount) % 2 == 0,
            });
            prevRecord[b] = cur;
        }
        var largeModel = new PmmModel
        {
            Index = 1,
            BoneNames = Enumerable.Range(0, boneCount).Select(i => PmmText($"b{i}")).ToArray(),
            InitialBoneKeyframes = largeInitial,
            BoneKeyframes = largeExtra,
            BoneStates = Enumerable.Range(0, boneCount).Select(i => new PmmBoneState(i, Vector3.Zero, Quaternion.Identity, false, false, 0)).ToArray(),
        };
        var largeTracks = new PmmPhysicsControlTracks(new PmmDocument { Models = [largeModel] });
        double[] sampleFrames = [0, 1, 2, 3, 250, 599, 600, 1200, 1800, 2500];
        var expectedLarge = sampleFrames.Select(f => ReferenceDisabledBonesAt(largeModel, f, saved: false)).ToArray();

        Parallel.For(0, 64, i =>
        {
            int slot = i % sampleFrames.Length;
            Assert.Equal(expectedLarge[slot], largeTracks.DisabledBonesAt(1, sampleFrames[slot]));
        });
        Assert.Same(largeTracks.DisabledBonesAt(1, 250), largeTracks.DisabledBonesAt(1, 250.5));
        Assert.Same(largeTracks.DisabledBonesAt(1, 1800), largeTracks.DisabledBonesAt(1, 2500));
    }

    [Fact]
    public async Task ConcurrentReadsAcrossTracksControlsAndSessionAreThreadSafeAndReferenceStable()
    {
        var fixture = CreateRichMultiActorFixture();
        var tracks = new PmmPhysicsControlTracks(fixture.Document);
        double[] frames = [0, 4.75, 5, 8.25, 10, 14.5, 15, 30, 55.5, 100];

        var baselineDisabled = frames.Select(f => tracks.DisabledBonesAt(1, f)).ToArray();
        var baselineSamples = frames.Select(f => fixture.State.Sample(f)).ToArray();
        var baselineParents = frames.Select(f => InvokeControlParents(fixture.Session, f)).ToArray();
        var baselineGround = frames.Select(f => InvokeControlGroundParents(fixture.Session, fixture.ChildA, f)).ToArray();
        var baselineWindows = frames.Select(f => InvokeControlTrackWindows(fixture.Session, fixture.ChildA, f)).ToArray();
        var baselineSessionDisabled = frames.Select(f => InvokeControlDisabled(fixture.Session, fixture.Parent, f)).ToArray();

        var tasks = Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
        {
            for (int iter = 0; iter < 50; iter++)
            {
                int slot = (worker * 7 + iter) % (frames.Length + 1);
                if (slot == frames.Length)
                {
                    Assert.Throws<ArgumentOutOfRangeException>(() => tracks.DisabledBonesAt(1, -3));
                    Assert.Throws<ArgumentOutOfRangeException>(() => fixture.State.Sample(-3));
                    Assert.Throws<ArgumentOutOfRangeException>(() => InvokeUnwrapped(() => ControlParentsMethod.Invoke(fixture.Session, [-3.0])));
                    continue;
                }
                double f = frames[slot];
                Assert.Same(baselineDisabled[slot], tracks.DisabledBonesAt(1, f));
                var sample = fixture.State.Sample(f);
                AssertSamplesEqual(baselineSamples[slot], sample);
                Assert.Same(baselineSamples[slot].DisabledBoneIndices, sample.DisabledBoneIndices);
                Assert.Same(baselineSamples[slot].OutsideParents, sample.OutsideParents);
                Assert.Same(baselineSamples[slot].GroundOutsideParents, sample.GroundOutsideParents);
                Assert.Same(baselineSamples[slot].OutsideParentTrackWindows, sample.OutsideParentTrackWindows);

                Assert.Same(baselineParents[slot], InvokeControlParents(fixture.Session, f));
                Assert.Same(baselineGround[slot], InvokeControlGroundParents(fixture.Session, fixture.ChildA, f));
                Assert.Same(baselineWindows[slot], InvokeControlTrackWindows(fixture.Session, fixture.ChildA, f));
                Assert.Same(baselineSessionDisabled[slot], InvokeControlDisabled(fixture.Session, fixture.Parent, f));
            }
        }, Token)).ToArray();

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void EndToEndSession120HzPlaybackAndOutOfOrderSeekRemainBitwiseConsistent()
    {
        var fixture = CreateRichMultiActorFixture();
        var backendA = new TrackingSharedBackend();
        var backendB = new TrackingSharedBackend();
        using var sessionA = fixture.CreateSession(sharedFactory: () => backendA);
        using var sessionB = fixture.CreateSession(sharedFactory: () => backendB);

        var gravitiesA = new List<Vector3>();
        var transformsA = new List<Matrix4x4[]>();
        for (int step = 0; step < 160; step++) // 40 frames @ 120Hz
        {
            sessionA.Advance(1d / 120, Token);
            gravitiesA.Add(backendA.Gravity);
            transformsA.Add(sessionA.GetBoneWorldMatrices(fixture.ChildA).ToArray());
        }

        // Out-of-order jumps before replaying forward from 0 on sessionB
        sessionB.Seek(30, Token);
        sessionB.Seek(5, Token);
        sessionB.Seek(30, Token);
        sessionB.Seek(0, Token);
        Assert.Equal(sessionA.GetBoneWorldMatrices(fixture.ChildA).Count, sessionB.GetBoneWorldMatrices(fixture.ChildA).Count);

        var gravitiesB = new List<Vector3>();
        var transformsB = new List<Matrix4x4[]>();
        for (int step = 0; step < 160; step++)
        {
            sessionB.Advance(1d / 120, Token);
            gravitiesB.Add(backendB.Gravity);
            transformsB.Add(sessionB.GetBoneWorldMatrices(fixture.ChildA).ToArray());
        }

        Assert.Equal(gravitiesA, gravitiesB);
        for (int i = 0; i < transformsA.Count; i++)
            Assert.Equal(transformsA[i], transformsB[i]);
    }

    private sealed class RichFixture : IDisposable
    {
        public required PmmDocument Document { get; init; }
        public required ScenePmmActorSource[] Actors { get; init; }
        public required ScenePmmControlState State { get; init; }
        public required SceneAnimationSession Session { get; init; }
        public required EditableSceneObject[] Objects { get; init; }
        public required SceneActorMotion[] Motions { get; init; }
        public Guid Parent => Actors[0].ObjectId;
        public Guid ChildA => Actors[1].ObjectId;
        public Guid ChildB => Actors[2].ObjectId;

        public SceneAnimationSession CreateSession(IEnumerable<SceneOutsideParentBinding>? staticParents = null, Func<ICharacterPhysicsMutableBackend>? sharedFactory = null)
            => new(Objects, Motions, _ => new TrackingSharedBackend(), sharedPhysicsFactory: sharedFactory,
                outsideParents: staticParents, pmmControls: State, cancellationToken: Token);

        public void Dispose() => Session.Dispose();
    }

    private static RichFixture CreateRichMultiActorFixture()
    {
        var pmxParent = BuildPmx("parent", ["dummy", "hand", "root"], rigidBoneIndex: 2, dynamicBody: true);
        var pmxChildA = BuildPmx("childA", ["unused", "prop", "arm", "hip"], rigidBoneIndex: 2, dynamicBody: true);
        var pmxChildB = BuildPmx("childB", ["tip", "base"], rigidBoneIndex: 0, dynamicBody: true);

        // Parent (model 0): PMM bones ["root"(0), "hand"(1), "dummy"(2)]
        var pmmParent = BuildModel(
            index: 0,
            name: "parent",
            bones: ["root", "hand", "dummy"],
            lastFrame: 60,
            initialBones:
            [
                BoneKey(0, 0, 3, 0, Vector3.Zero, disabled: false),
                BoneKey(1, 0, 4, 0, new(1, 0, 0), disabled: false),
                BoneKey(2, 0, 6, 0, new(0, 2, 0), disabled: false),
            ],
            boneKeys:
            [
                BoneKey(3, 0, 0, 60, new(10, 0, 0), disabled: false),
                BoneKey(4, 1, 5, 12, new(1, 3, 0), disabled: true),
                BoneKey(5, 4, 0, 22, new(1, 0, 0), disabled: false),
                BoneKey(6, 2, 7, 5, new(0, 2, 0), disabled: true),
                BoneKey(7, 6, 0, 15, new(0, 4, 0), disabled: false),
            ],
            boneDisabledStates: [false, true, false],
            subjectBones: [],
            initialParents: [],
            modelKeys: []);

        // ChildA (model 1): PMM bones ["hip"(0), "arm"(1), "prop"(2), "unused"(3)]
        var pmmChildA = BuildModel(
            index: 1,
            name: "childA",
            bones: ["hip", "arm", "prop", "unused"],
            lastFrame: 60,
            initialBones:
            [
                BoneKey(0, 0, 0, 0, Vector3.Zero, disabled: false),
                BoneKey(1, 0, 4, 0, Vector3.Zero, disabled: false),
                BoneKey(2, 0, 6, 0, Vector3.Zero, disabled: true),
                BoneKey(3, 0, 0, 0, Vector3.Zero, disabled: false),
            ],
            boneKeys:
            [
                BoneKey(4, 1, 5, 30, Vector3.Zero, disabled: true),
                BoneKey(5, 4, 0, 30, Vector3.Zero, disabled: false), // duplicate frame 30 -> false wins
                BoneKey(6, 2, 7, 10, Vector3.Zero, disabled: false),
                BoneKey(7, 6, 8, 30, Vector3.Zero, disabled: true),
                BoneKey(8, 7, 0, 50, Vector3.Zero, disabled: false),
            ],
            boneDisabledStates: [false, false, true, false],
            subjectBones: [-1, 1, 2],
            initialParents: [new(0, -1, -1), new(0, 0, 1), new(0, -2, -1)],
            modelKeys:
            [
                // Frame 8: IK-only switch (OutsideParents identical to initial!)
                new() { RecordIndex = 1, ObjectIndex = 1, PreviousIndex = 0, NextIndex = 2, Frame = 8, Visible = true, OutsideParents = [new(0, -1, -1), new(0, 0, 1), new(0, -2, -1)] },
                new() { RecordIndex = 2, ObjectIndex = 2, PreviousIndex = 1, NextIndex = 3, Frame = 15, Visible = true, OutsideParents = [new(0, 0, 0), new(0, -1, -1), new(0, 0, 1)] },
                new() { RecordIndex = 3, ObjectIndex = 3, PreviousIndex = 2, NextIndex = 0, Frame = 35, Visible = true, OutsideParents = [new(0, -1, -1), new(0, -2, -1), new(0, -1, -1)] },
            ],
            savedParentStates:
            [
                new(0, 0, int.MaxValue, new(0, -1, -1)),
                new(1, 5, 25, new(0, -2, -1)),
                new(2, 10, int.MaxValue, new(0, 0, 0)),
            ]);

        // ChildB (model 2): PMM bones ["base"(0), "tip"(1)]
        var pmmChildB = BuildModel(
            index: 2,
            name: "childB",
            bones: ["base", "tip"],
            lastFrame: 60,
            initialBones:
            [
                BoneKey(0, 0, 0, 0, Vector3.Zero, disabled: false),
                BoneKey(1, 0, 2, 0, Vector3.Zero, disabled: false),
            ],
            boneKeys:
            [
                BoneKey(2, 1, 3, 7, Vector3.Zero, disabled: true),
                BoneKey(3, 2, 0, 25, Vector3.Zero, disabled: false),
            ],
            boneDisabledStates: [false, true],
            subjectBones: [1],
            initialParents: [new(0, -1, -1)],
            modelKeys:
            [
                new() { RecordIndex = 1, ObjectIndex = 1, PreviousIndex = 0, NextIndex = 2, Frame = 10, Visible = true, OutsideParents = [new(0, 1, 1)] },
                new() { RecordIndex = 2, ObjectIndex = 2, PreviousIndex = 1, NextIndex = 0, Frame = 30, Visible = true, OutsideParents = [new(0, -1, -1)] },
            ]);

        var doc = RoundTrip(WrapDocumentWithGravity(
            new()
            {
                Acceleration = 2,
                Direction = Vector3.UnitZ,
                InitialKeyframe = new() { RecordIndex = 0, ObjectIndex = 0, PreviousIndex = 0, NextIndex = 1, Frame = 0, Acceleration = 1, Direction = -Vector3.UnitY },
                Keyframes =
                [
                    new() { RecordIndex = 1, ObjectIndex = 1, PreviousIndex = 0, NextIndex = 2, Frame = 20, Acceleration = 3, Direction = Vector3.UnitX },
                    new() { RecordIndex = 2, ObjectIndex = 2, PreviousIndex = 1, NextIndex = 3, Frame = 40, Acceleration = 4, Direction = Vector3.UnitY },
                    new() { RecordIndex = 3, ObjectIndex = 3, PreviousIndex = 2, NextIndex = 0, Frame = 60, Acceleration = 4, Direction = Vector3.UnitY },
                ],
            },
            pmmParent, pmmChildA, pmmChildB));

        var layout = new SceneCompositionEditor();
        var pmxs = new[] { pmxParent, pmxChildA, pmxChildB };
        var objs = new EditableSceneObject[3];
        var motions = new SceneActorMotion[3];
        var sources = new ScenePmmActorSource[3];
        for (int i = 0; i < 3; i++)
        {
            var rest = SharedPhysicsFixture.Rest(pmxs[i]);
            objs[i] = layout.Add(pmxs[i].Name, pmxs[i].Name + ".pmx", ModelRole.Character, rest);
            var vmd = PmmMotionAdapter.Convert(doc.Models[i], pmxs[i]);
            motions[i] = new(objs[i].Id, pmxs[i], vmd, PhysicsEnabled: true);
            sources[i] = new(objs[i].Id, doc.Models[i].Index, pmxs[i]);
        }
        var state = ScenePmmControlState.Create(doc, sources);
        var session = new SceneAnimationSession(
            layout.Objects, motions, _ => new TrackingSharedBackend(),
            sharedPhysicsFactory: () => new TrackingSharedBackend(),
            pmmControls: state, cancellationToken: Token);
        return new()
        {
            Document = doc,
            Actors = sources,
            State = state,
            Session = session,
            Objects = [.. layout.Objects],
            Motions = motions,
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

    private static PmmModel BuildModel(
        byte index,
        string name,
        string[] bones,
        int lastFrame,
        PmmBoneKeyframe[] initialBones,
        IReadOnlyList<PmmBoneKeyframe> boneKeys,
        bool[] boneDisabledStates,
        int[] subjectBones,
        PmmOutsideParent[] initialParents,
        IReadOnlyList<PmmModelKeyframe> modelKeys,
        PmmOutsideParentState[]? savedParentStates = null)
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
                .Select(i => new PmmBoneState(i, Vector3.Zero, Quaternion.Identity, false, i < boneDisabledStates.Length && boneDisabledStates[i], 0))
                .ToArray(),
            OutsideParentSubjectBoneIndices = subjectBones,
            InitialModelKeyframe = new() { Visible = true, OutsideParents = initialParents, NextIndex = modelKeys.Count > 0 ? 1 : 0 },
            ModelKeyframes = modelKeys,
            OutsideParentStates = savedParentStates ?? initialParents.Select((p, i) => new PmmOutsideParentState(i, 0, int.MaxValue, p)).ToArray(),
        };
    }

    private static PmmDocument WrapDocument(params PmmModel[] models) => WrapDocumentWithGravity(null, models);

    private static PmmDocument WrapDocumentWithGravity(PmmGravity? gravity, params PmmModel[] models) => new()
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

    private static PmxModel BuildPmx(string name, string[] boneNames, int rigidBoneIndex = 0, bool dynamicBody = false)
    {
        var skeleton = PmmMotionAdapterTests.Pmx(boneNames);
        return new()
        {
            Header = skeleton.Header,
            Name = name,
            EnglishName = "",
            Comment = "",
            EnglishComment = "",
            Bones = skeleton.Bones,
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex
            {
                Position = p,
                Normal = Vector3.UnitZ,
                Uv = Vector2.Zero,
                AdditionalUvs = [],
                WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [rigidBoneIndex, -1, -1, -1],
                BoneWeights = [1, 0, 0, 0],
                EdgeScale = 1,
            }).ToArray(),
            Faces = [new(0, 1, 2)],
            Textures = [],
            Materials = [new()
            {
                Name = "mat", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate,
                EdgeColor = Vector4.One, EdgeSize = 0, TextureIndex = -1, SphereTextureIndex = -1, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3,
            }],
            Morphs = [],
            DisplayFrames = [],
            RigidBodies = dynamicBody ? [new()
            {
                Name = "body", EnglishName = "", BoneIndex = rigidBoneIndex, Group = 0, CollisionGroup = ushort.MaxValue,
                Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
                Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic,
            }] : [],
            Joints = [],
            SoftBodies = [],
        };
    }

    // Pure uncached reference implementations reproducing original semantics for parity assertions.
    private static int[] ReferenceDisabledBonesAt(PmmModel model, double frame, bool saved)
    {
        if (saved)
            return model.BoneStates.Select((s, idx) => (s, idx)).Where(p => p.s.PhysicsDisabled).Select(p => p.idx).ToArray();
        var all = model.InitialBoneKeyframes.Concat(model.BoneKeyframes).ToArray();
        var byObjectId = new Dictionary<int, int>(all.Length);
        for (int i = 0; i < all.Length; i++) byObjectId[all[i].ObjectIndex] = i;
        var disabled = new List<int>();
        for (int b = 0; b < model.BoneNames.Count; b++)
        {
            var track = new List<PmmBoneKeyframe>();
            int cur = b;
            while (true)
            {
                var key = all[cur];
                track.Add(key);
                if (key.NextIndex == 0) break;
                cur = byObjectId[key.NextIndex];
            }
            var keys = track.GroupBy(k => k.Frame).Select(g => g.Last()).OrderBy(k => k.Frame).ToArray();
            int lo = 0, hi = keys.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (keys[mid].Frame <= frame) lo = mid + 1; else hi = mid;
            }
            if (keys[Math.Max(0, lo - 1)].PhysicsDisabled) disabled.Add(b);
        }
        return disabled.ToArray();
    }

    private static PmmGravityPose ReferenceGravityAt(PmmGravity source, double frame, bool saved)
    {
        if (saved)
            return new(source.Acceleration, source.Direction, source.NoiseEnabled, source.Noise, PmmPhysicsSettings.ToGravity(source.Acceleration, source.Direction));
        var keys = new[] { source.InitialKeyframe }.Concat(source.Keyframes)
            .GroupBy(k => k.Frame).Select(g => g.Last()).OrderBy(k => k.Frame).ToArray();
        int lo = 0, hi = keys.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (keys[mid].Frame <= frame) lo = mid + 1; else hi = mid;
        }
        int idx = Math.Max(0, lo - 1);
        var a = keys[idx];
        if (frame <= a.Frame || idx == keys.Length - 1)
            return new(a.Acceleration, a.Direction, a.NoiseEnabled, a.Noise, PmmPhysicsSettings.ToGravity(a.Acceleration, a.Direction));
        var b = keys[idx + 1];
        float weight = (float)(frame - a.Frame) / (float)(b.Frame - a.Frame);
        float LerpScalar(float x, float y) => (y - x) * weight + x;
        float acc = LerpScalar(a.Acceleration, b.Acceleration);
        var dir = new Vector3(LerpScalar(a.Direction.X, b.Direction.X), LerpScalar(a.Direction.Y, b.Direction.Y), LerpScalar(a.Direction.Z, b.Direction.Z));
        float scaledNoise = (float)unchecked(b.Noise - a.Noise) * weight;
        int integralNoise = scaledNoise >= 2147483648f || scaledNoise < -2147483648f ? int.MinValue : (int)scaledNoise;
        return new(acc, dir, a.NoiseEnabled, unchecked(integralNoise + a.Noise), PmmPhysicsSettings.ToGravity(acc, dir));
    }

    private static ScenePmmControlSample ReferenceSample(PmmDocument doc, Dictionary<byte, ScenePmmActorSource> actors, double frame, bool saved)
    {
        int MapBone(byte modelIdx, int ordinal)
        {
            if (ordinal < 0) return -1;
            string name = doc.Models.Single(m => m.Index == modelIdx).BoneNames[ordinal].Value;
            return Array.FindIndex(actors[modelIdx].Model.Bones.ToArray(), b => b.Name == name);
        }
        var disabled = actors.ToDictionary(p => p.Value.ObjectId, p =>
        {
            var model = doc.Models.Single(m => m.Index == p.Key);
            return (IReadOnlyList<int>)Array.AsReadOnly(ReferenceDisabledBonesAt(model, frame, saved).Select(o => MapBone(p.Key, o)).Distinct().ToArray());
        });
        var outside = new List<SceneOutsideParentBinding>();
        var ground = new List<ScenePmmGroundOutsideParent>();
        var windows = new Dictionary<Guid, IReadOnlyDictionary<int, OutsideParentTrackWindow>>();
        foreach (var model in doc.Models)
        {
            var ordered = new[] { model.InitialModelKeyframe }.Concat(model.ModelKeyframes).OrderBy(k => k.Frame).ToArray();
            int low = 0, high = ordered.Length;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (ordered[mid].Frame <= frame) low = mid + 1; else high = mid;
            }
            int activeIdx = saved ? 0 : Math.Max(0, low - 1);
            IReadOnlyList<PmmOutsideParent> parents = saved ? model.OutsideParentStates.Select(s => s.Parent).ToArray() : ordered[activeIdx].OutsideParents;
            Guid actorId = actors[model.Index].ObjectId;
            for (int slot = 0; slot < parents.Count; slot++)
            {
                int subject = model.OutsideParentSubjectBoneIndices[slot];
                var parent = parents[slot];
                if (parent.ModelIndex == -2 && subject >= 0)
                    ground.Add(new(actorId, MapBone(model.Index, subject)));
                else if (parent.ModelIndex >= 0)
                    outside.Add(new(actorId, subject < 0 ? -1 : MapBone(model.Index, subject), actors[(byte)parent.ModelIndex].ObjectId, MapBone((byte)parent.ModelIndex, parent.BoneIndex), Matrix4x4.Identity));
            }
            var w = ReferenceTrackWindows(model, ordered, activeIdx, saved, o => MapBone(model.Index, o));
            if (w is { Count: > 0 }) windows[actorId] = w;
        }
        return new(ReferenceGravityAt(doc.Gravity, frame, saved), new ReadOnlyDictionary<Guid, IReadOnlyList<int>>(disabled), outside.AsReadOnly())
        {
            GroundOutsideParents = ground.AsReadOnly(),
            OutsideParentTrackWindows = new ReadOnlyDictionary<Guid, IReadOnlyDictionary<int, OutsideParentTrackWindow>>(windows),
        };
    }

    private static IReadOnlyDictionary<int, OutsideParentTrackWindow>? ReferenceTrackWindows(
        PmmModel model, PmmModelKeyframe[] ordered, int activeIdx, bool saved, Func<int, int> mapBone)
    {
        static bool Same(PmmOutsideParent a, PmmOutsideParent b)
            => a.ModelIndex == b.ModelIndex && (a.ModelIndex < 0 || a.BoneIndex == b.BoneIndex);

        Dictionary<int, OutsideParentTrackWindow>? result = null;
        for (int slot = 0; slot < model.OutsideParentSubjectBoneIndices.Count; slot++)
        {
            int subject = model.OutsideParentSubjectBoneIndices[slot];
            if (subject < 0) continue;
            int mapped = mapBone(subject);
            if (mapped < 0) continue;
            uint begin, end;
            if (saved)
            {
                var st = model.OutsideParentStates[slot];
                begin = st.BeginFrame > 0 ? (uint)st.BeginFrame : 0u;
                end = st.EndFrame > 0 && st.EndFrame < int.MaxValue ? (uint)st.EndFrame : 0u;
            }
            else
            {
                var active = ordered[activeIdx].OutsideParents[slot];
                int beginIdx = activeIdx;
                while (beginIdx > 0 && Same(ordered[beginIdx - 1].OutsideParents[slot], active)) beginIdx--;
                begin = beginIdx == 0 ? 0u : (uint)ordered[beginIdx].Frame;
                int endIdx = activeIdx + 1;
                while (endIdx < ordered.Length && Same(ordered[endIdx].OutsideParents[slot], active)) endIdx++;
                end = endIdx < ordered.Length ? (uint)ordered[endIdx].Frame : 0u;
            }
            if (begin == 0u && end == 0u) continue;
            (result ??= [])[mapped] = new(begin, end);
        }
        return result;
    }

    private static void AssertSamplesEqual(ScenePmmControlSample expected, ScenePmmControlSample actual)
    {
        Assert.Equal(expected.Gravity, actual.Gravity);
        Assert.Equal(expected.OutsideParents, actual.OutsideParents);
        Assert.Equal(expected.GroundOutsideParents, actual.GroundOutsideParents);
        Assert.Equal(expected.DisabledBoneIndices.Keys.Order(), actual.DisabledBoneIndices.Keys.Order());
        foreach (var (id, expList) in expected.DisabledBoneIndices)
            Assert.Equal(expList, actual.DisabledBoneIndices[id]);
        Assert.Equal(expected.OutsideParentTrackWindows.Keys.Order(), actual.OutsideParentTrackWindows.Keys.Order());
        foreach (var (id, expWin) in expected.OutsideParentTrackWindows)
            AssertWindowsEqual(expWin, actual.OutsideParentTrackWindows[id]);
    }

    private static void AssertWindowsEqual(IReadOnlyDictionary<int, OutsideParentTrackWindow>? expected, IReadOnlyDictionary<int, OutsideParentTrackWindow>? actual)
    {
        if (expected is null || expected.Count == 0)
        {
            Assert.True(actual is null || actual.Count == 0);
            return;
        }
        Assert.NotNull(actual);
        Assert.Equal(expected.Count, actual!.Count);
        foreach (var (bone, win) in expected)
            Assert.Equal(win, actual[bone]);
    }

    private static ScenePmmControlSample InvokeControlSample(SceneAnimationSession session, double frame)
        => (ScenePmmControlSample)ControlSampleMethod.Invoke(session, [frame])!;
    private static SceneOutsideParentBinding[] InvokeControlParents(SceneAnimationSession session, double frame)
        => (SceneOutsideParentBinding[])ControlParentsMethod.Invoke(session, [frame])!;
    private static ScenePmmGroundOutsideParent[] InvokeControlGroundParents(SceneAnimationSession session, Guid id, double frame)
        => (ScenePmmGroundOutsideParent[])ControlGroundParentsMethod.Invoke(session, [id, frame])!;
    private static IReadOnlyDictionary<int, OutsideParentTrackWindow>? InvokeControlTrackWindows(SceneAnimationSession session, Guid id, double frame)
        => (IReadOnlyDictionary<int, OutsideParentTrackWindow>?)ControlTrackWindowsMethod.Invoke(session, [id, frame]);
    private static IReadOnlySet<int> InvokeControlDisabled(SceneAnimationSession session, Guid id, double frame)
        => (IReadOnlySet<int>)ControlDisabledMethod.Invoke(session, [id, frame])!;

    private static void InvokeUnwrapped(Action call)
    {
        try { call(); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
    }
}
