using System.Numerics;
using System.Reflection;
using System.Text;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmOutsideParentParityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ICharacterPhysicsBackend NoPhysics(EditableSceneObject _) => throw new InvalidOperationException("No rigid bodies.");
    private static PmmString PmmText(string value) => new(0, value, []);
    private static readonly byte[] LinearCurves = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(b => b).ToArray();

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 2e-3f)
        => Assert.InRange(Vector3.Distance(expected, actual), 0, tolerance);

    private static PmxModel BuildPmx(string name, PmxBone[] bones, PmxRigidBody[]? rigidBodies = null)
    {
        var skeleton = PmmMotionAdapterTests.Pmx(bones.Select(b => b.Name).ToArray());
        return new()
        {
            Header = skeleton.Header, Name = name, EnglishName = "", Comment = "", EnglishComment = "",
            Bones = bones,
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex
            {
                Position = p, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [],
                WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1
            }).ToArray(),
            Faces = [new(0, 1, 2)],
            Textures = [],
            Materials = [new()
            {
                Name = "mat", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate,
                EdgeColor = Vector4.One, EdgeSize = 0, TextureIndex = -1, SphereTextureIndex = -1, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3
            }],
            Morphs = [],
            DisplayFrames = [],
            RigidBodies = rigidBodies ?? [],
            Joints = [],
            SoftBodies = [],
        };
    }

    private static PmxBone SimpleBone(string name, Vector3 position = default, int parent = -1, PmxBoneFlags flags = PmxBoneFlags.None)
        => new() { Name = name, EnglishName = "", Position = position, ParentIndex = parent, DeformDepth = 0, Flags = flags };

    private static PmmModel BuildPmmModel(byte index, string name, string[] bones, int lastFrame,
        PmmBoneKeyframe[] initialBones, IReadOnlyList<PmmBoneKeyframe> boneKeys,
        int[] subjectBones, PmmOutsideParent[] initialParents, IReadOnlyList<PmmModelKeyframe> modelKeys,
        PmmOutsideParent[]? savedParents = null)
    {
        return new()
        {
            Index = index, DrawOrderIndex = index, TransformOrderIndex = index, Visible = true, LastFrame = lastFrame,
            Name = PmmText(name), Path = PmmText(name + ".pmx"),
            BoneNames = bones.Select(PmmText).ToArray(),
            SelectedMorphIndices = [-1, -1, -1, -1],
            InitialBoneKeyframes = initialBones,
            BoneKeyframes = boneKeys,
            BoneStates = Enumerable.Range(0, bones.Length).Select(i => new PmmBoneState(i, Vector3.Zero, Quaternion.Identity, false, false, 0)).ToArray(),
            OutsideParentSubjectBoneIndices = subjectBones,
            InitialModelKeyframe = new() { Visible = true, OutsideParents = initialParents, NextIndex = modelKeys.Count > 0 ? 1 : 0 },
            ModelKeyframes = modelKeys,
            OutsideParentStates = (savedParents ?? initialParents).Select((p, i) => new PmmOutsideParentState(i, 0, int.MaxValue, p)).ToArray(),
        };
    }

    private static PmmBoneKeyframe BoneKey(int record, int previous, int next, int frame, Vector3 translation, Quaternion? rotation = null)
        => new() { RecordIndex = record, ObjectIndex = record, PreviousIndex = previous, NextIndex = next, Frame = frame,
            Translation = translation, Rotation = rotation ?? Quaternion.Identity, Interpolation = LinearCurves };

    private static PmmDocument WrapDocument(params PmmModel[] models) => new()
    {
        AnsiCodePage = 932, PanelStates = new byte[7], Models = models, OutputWidth = 640, OutputHeight = 480, CameraFov = 30,
        Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
        Settings = new() { PhysicsSimulationMode = 1, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], CameraLookAtModelIndex = -1, CameraLookAtBoneIndex = -1 },
        Gravity = new() { InitialKeyframe = new() { Acceleration = 10, Direction = -Vector3.UnitY } },
        Light = new() { Color = Vector3.One, Direction = -Vector3.UnitY, InitialKeyframe = new() { Color = Vector3.One, Direction = -Vector3.UnitY } },
    };

    private static PmmDocument RoundTrip(PmmDocument document)
        => PmmReader.Read(new MemoryStream(PmmWriter.ToBytes(document), writable: false), new() { AnsiCodePage = 932 });

    private static (SceneSessionState SessionState, PmmProjectPlayback Playback) CreateBoth(PmmDocument rawDoc,
        (string Name, PmxModel Model, Vector3? WorldOffset, bool Physics)[] actors,
        Func<TrackingSharedBackend>? createBackend = null)
    {
        var doc = RoundTrip(rawDoc);
        var layout = new SceneCompositionEditor();
        var motions = new SceneActorMotion[actors.Length];
        var pmmSources = new ScenePmmActorSource[actors.Length];
        var prepared = new List<PreparedPmmModel>(actors.Length);
        var restScenes = new Dictionary<int, OriginalFrameScene>(actors.Length);
        for (int i = 0; i < actors.Length; i++)
        {
            var (name, model, worldOffset, phys) = actors[i];
            var rest = SharedPhysicsFixture.Rest(model);
            var obj = layout.Add(name, name + ".pmx", ModelRole.Character, rest);
            if (worldOffset is { } offset) layout.SetTransform(obj.Id, offset, Quaternion.Identity, 1);
            var pmmModel = doc.Models[i];
            var vmd = PmmMotionAdapter.Convert(pmmModel, model);
            motions[i] = new(obj.Id, model, vmd, PhysicsEnabled: phys);
            pmmSources[i] = new(obj.Id, pmmModel.Index, model);
            prepared.Add(new(pmmModel, name + ".pmx", model, vmd, PmmMotionAdapter.ConvertSavedPose(pmmModel, model)));
            restScenes[pmmModel.Index] = rest;
        }
        var sessionBackend = createBackend?.Invoke();
        var playbackBackend = createBackend?.Invoke();
        var controls = ScenePmmControlState.Create(doc, pmmSources);
        var session = new SceneAnimationSession(layout.Objects, motions, _ => sessionBackend ?? throw new InvalidOperationException(),
            sharedPhysicsFactory: sessionBackend is not null ? () => sessionBackend : null, pmmControls: controls, cancellationToken: Token);
        var plan = (PmmProjectImportPlan)Activator.CreateInstance(typeof(PmmProjectImportPlan), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [doc, "synthetic.pmm", prepared, new List<PmmUnresolvedAsset>(), new List<string>()], null)!;
        var playback = new PmmProjectPlayback(plan, restScenes, (_, _) => playbackBackend ?? throw new InvalidOperationException(), sharedPhysicsWorld: playbackBackend is not null);
        return (new(session, motions.Select(m => m.ObjectId).ToArray()), playback);
    }

    private sealed record SceneSessionState(SceneAnimationSession Session, Guid[] ActorIds) : IDisposable
    {
        public void Dispose() => Session.Dispose();
    }

    [Fact]
    public void PmmModelKeyframeSwitchesResetPostKeyOffsetsAndSuppressCrossBoundaryInterpolation()
    {
        var pmxParent = BuildPmx("parent", [SimpleBone("root")]);
        var pmxChild = BuildPmx("child", [SimpleBone("root")]);
        var parentPmm = BuildPmmModel(0, "parent", ["root"], 30,
            [BoneKey(0, 0, 0, 0, new(0, 10, 0))], [], [], [], []);
        var childPmm = BuildPmmModel(1, "child", ["root"], 30,
            [BoneKey(0, 0, 1, 0, Vector3.Zero)],
            [BoneKey(1, 0, 0, 10, new(10, 0, 0))],
            [0],
            [new(0, -1, -1)],
            [
                new() { RecordIndex = 1, ObjectIndex = 1, PreviousIndex = 0, NextIndex = 2, Frame = 6, Visible = true, OutsideParents = [new(0, 0, 0)] },
                new() { RecordIndex = 2, ObjectIndex = 2, PreviousIndex = 1, NextIndex = 3, Frame = 15, Visible = true, OutsideParents = [new(0, -1, -1)] },
                new() { RecordIndex = 3, ObjectIndex = 3, PreviousIndex = 2, NextIndex = 0, Frame = 20, Visible = true, OutsideParents = [new(0, 0, 0)] },
            ]);
        var (state, playback) = CreateBoth(WrapDocument(parentPmm, childPmm),
            [("parent", pmxParent, null, false), ("child", pmxChild, null, false)]);
        using var _1 = state; using var _2 = playback;

        Guid child = state.ActorIds[1];
        // Frame 4 precedes EndFrame=6 of [0, 6): hold frame-0 key (0,0,0), no interpolation toward key 10.
        state.Session.Seek(4, Token); playback.Seek(4);
        Near(Vector3.Zero, state.Session.GetBoneWorldMatrices(child)[0].Translation);
        Near(Vector3.Zero, playback.GetBoneWorldTransform(1, 0).Translation);

        // Frame 8 is after BeginFrame=6 inside (0, 10): hold frame-10 key (10,0,0) composed on parent (0,10,0).
        state.Session.Seek(8, Token); playback.Seek(8);
        Near(new(10, 10, 0), state.Session.GetBoneWorldMatrices(child)[0].Translation);
        Near(new(10, 10, 0), playback.GetBoneWorldTransform(1, 0).Translation);

        // Frame 14 stays on key 10 before BeginFrame=15.
        state.Session.Seek(14, Token); playback.Seek(14);
        Near(new(10, 10, 0), state.Session.GetBoneWorldMatrices(child)[0].Translation);
        Near(new(10, 10, 0), playback.GetBoneWorldTransform(1, 0).Translation);

        // Frame 15 >= BeginFrame=15 > lastKey=10: local offset resets to zero (Rule 1A).
        state.Session.Seek(15, Token); playback.Seek(15);
        Near(Vector3.Zero, state.Session.GetBoneWorldMatrices(child)[0].Translation);
        Near(Vector3.Zero, playback.GetBoneWorldTransform(1, 0).Translation);

        // Frame 20 attaches to parent/0 with reset local offset => exact parent bone world (0,10,0).
        state.Session.Seek(20, Token); playback.Seek(20);
        Near(new(0, 10, 0), state.Session.GetBoneWorldMatrices(child)[0].Translation);
        Near(new(0, 10, 0), playback.GetBoneWorldTransform(1, 0).Translation);
    }

    [Fact]
    public void GroundOutsideParentDetachesBoneFromOrdinaryParentAndWholeModelBinding()
    {
        var pmxParent = BuildPmx("parent", [SimpleBone("root")]);
        var pmxChild = BuildPmx("child", [SimpleBone("root"), SimpleBone("arm", new(1, 2, 3), parent: 0)]);
        var parentPmm = BuildPmmModel(0, "parent", ["root"], 10, [BoneKey(0, 0, 0, 0, new(50, 0, 0))], [], [], [], []);
        var childPmm = BuildPmmModel(1, "child", ["root", "arm"], 10,
            [BoneKey(0, 0, 0, 0, new(10, 0, 0)), BoneKey(1, 0, 0, 0, new(0, 5, 0))],
            [],
            [-1, 1],
            [new(0, 0, 0), new(0, -2, -1)],
            []);
        var childOffset = new Vector3(0, 0, 5);
        var (state, playback) = CreateBoth(WrapDocument(parentPmm, childPmm),
            [("parent", pmxParent, null, false), ("child", pmxChild, childOffset, false)]);
        using var _1 = state; using var _2 = playback;

        Guid child = state.ActorIds[1];
        state.Session.Seek(0, Token); playback.Seek(0);
        // Root follows whole-model binding (-1) onto parent (50,0,0) + local (10,0,0) in scene world space.
        var rootWorld = state.Session.GetBoneWorldMatrices(child)[0].Translation;
        Near(new(60, 0, 0), rootWorld);
        Near(new(60, 0, 0), playback.GetBoneWorldTransform(1, 0).Translation);

        // Arm bound to Ground (-2) bypasses BOTH ordinary parent root (60,0,0) AND whole-model (-1) parent (50,0,0),
        // even when child itself has a non-identity object placement transform (0,0,5).
        var armWorldInSession = state.Session.GetBoneWorldMatrices(child)[1].Translation;
        Near(new(1, 7, 3), armWorldInSession);
        Near(new(1, 7, 3), playback.GetBoneWorldTransform(1, 1).Translation);
    }

    [Fact]
    public void AfterPhysicsAndPhysicsDrivenParentFeedsChildOnResetAndAdvance()
    {
        static PmxRigidBody MakeBody(int boneIndex, PmxRigidBodyOperation operation) => new()
        {
            Name = "body" + boneIndex, EnglishName = "", BoneIndex = boneIndex, Group = 0, CollisionGroup = ushort.MaxValue,
            Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
            Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = operation
        };
        var pmxParent = BuildPmx("parent", [
            SimpleBone("waist"),
            SimpleBone("dummy", new(0, 2, 0), parent: 0, flags: PmxBoneFlags.DeformAfterPhysics)
        ], [MakeBody(0, PmxRigidBodyOperation.Dynamic)]);
        var pmxNonPhysChild = BuildPmx("prop", [SimpleBone("held")]);
        var pmxPhysChild = BuildPmx("physChild", [
            SimpleBone("anchor"),
            SimpleBone("effector", new(1, 0, 0), parent: 0, flags: PmxBoneFlags.DeformAfterPhysics)
        ], [MakeBody(0, PmxRigidBodyOperation.Static)]);

        var parentPmm = BuildPmmModel(0, "parent", ["waist", "dummy"], 30,
            [BoneKey(0, 0, 2, 0, Vector3.Zero), BoneKey(1, 0, 0, 0, new(1, 0, 0))],
            [BoneKey(2, 0, 0, 30, Vector3.Zero)], [], [], []);
        var propPmm = BuildPmmModel(1, "prop", ["held"], 30,
            [BoneKey(0, 0, 1, 0, Vector3.Zero)], [BoneKey(1, 0, 0, 30, Vector3.Zero)], [0], [new(0, 0, 1)], []);
        var physChildPmm = BuildPmmModel(2, "physChild", ["anchor", "effector"], 30,
            [BoneKey(0, 0, 2, 0, Vector3.Zero), BoneKey(1, 0, 0, 0, Vector3.Zero)],
            [BoneKey(2, 0, 0, 30, Vector3.Zero)], [0], [new(0, 0, 1)], []);

        var (state, playback) = CreateBoth(WrapDocument(parentPmm, propPmm, physChildPmm),
            [("parent", pmxParent, null, true), ("prop", pmxNonPhysChild, null, false), ("physChild", pmxPhysChild, null, true)],
            () => new TrackingSharedBackend());
        using var _1 = state; using var _2 = playback;
        Assert.True(playback.PhysicsEnabled);

        Guid parentId = state.ActorIds[0], propId = state.ActorIds[1], physChildId = state.ActorIds[2];

        // On Seek(0) reset, AfterPhysics parent bone 'dummy' is valid and non-zero, feeding both children.
        state.Session.Seek(0, Token); playback.Seek(0);
        var parentDummyReset = state.Session.GetBoneWorldMatrices(parentId)[1].Translation;
        Near(new(1, 2, 0), parentDummyReset);
        Near(parentDummyReset, state.Session.GetBoneWorldMatrices(propId)[0].Translation);
        Near(parentDummyReset, state.Session.GetBoneWorldMatrices(physChildId)[0].Translation);
        Near(parentDummyReset + Vector3.UnitX, state.Session.GetBoneWorldMatrices(physChildId)[1].Translation);

        var playbackDummyReset = playback.GetBoneWorldTransform(0, 1).Translation;
        Near(new(1, 2, 0), playbackDummyReset);
        Near(playbackDummyReset, playback.GetBoneWorldTransform(1, 0).Translation);
        Near(playbackDummyReset, playback.GetBoneWorldTransform(2, 0).Translation);
        Near(playbackDummyReset + Vector3.UnitX, playback.GetBoneWorldTransform(2, 1).Translation);

        // Advance past multiple Bullet substeps: parent's dynamic 'waist' drops under gravity, 'dummy' follows it,
        // and Phase 3 updates both non-physical and physical children to the same frame's post-physics 'dummy'.
        state.Session.Advance(1d / 30, Token);
        playback.Advance(1, 1d / 30);

        var parentWaist = state.Session.GetBoneWorldMatrices(parentId)[0].Translation;
        var parentDummy = state.Session.GetBoneWorldMatrices(parentId)[1].Translation;
        Assert.True(parentWaist.Y < -1e-4f);
        Near(parentWaist + new Vector3(1, 2, 0), parentDummy);
        Near(parentDummy, state.Session.GetBoneWorldMatrices(propId)[0].Translation);
        Near(parentDummy, state.Session.GetBoneWorldMatrices(physChildId)[0].Translation);
        Near(parentDummy + Vector3.UnitX, state.Session.GetBoneWorldMatrices(physChildId)[1].Translation);

        var playbackWaist = playback.GetBoneWorldTransform(0, 0).Translation;
        var playbackDummy = playback.GetBoneWorldTransform(0, 1).Translation;
        Assert.True(playbackWaist.Y < -1e-4f);
        Near(playbackWaist + new Vector3(1, 2, 0), playbackDummy);
        Near(playbackDummy, playback.GetBoneWorldTransform(1, 0).Translation);
        Near(playbackDummy, playback.GetBoneWorldTransform(2, 0).Translation);
        Near(playbackDummy + Vector3.UnitX, playback.GetBoneWorldTransform(2, 1).Translation);
    }

    [Fact]
    public void FrozenChildSingleBoneSlotSwitchPreservesRestOffsetAndResolvesCrossSubtreeIk()
    {
        var pmxParent = BuildPmx("parent", [SimpleBone("goal")]);
        var pmxChild = BuildPmx("child", [
            SimpleBone("hip", Vector3.Zero, parent: -1),
            SimpleBone("knee", new(0, -1, 0), parent: 0),
            SimpleBone("foot", new(0, -2, 0), parent: 1),
            new()
            {
                Name = "legIk", EnglishName = "", Position = new(0, -2, 0), ParentIndex = -1, DeformDepth = 0,
                Flags = PmxBoneFlags.Ik, IkTargetBoneIndex = 2, IkIterationCount = 24, IkLimit = MathF.PI / 2,
                IkLinks = [new(1, false, Vector3.Zero, Vector3.Zero), new(0, false, Vector3.Zero, Vector3.Zero)]
            },
            SimpleBone("hand", new(1, 2, 0), parent: 0),
        ]);

        var parentPmm = BuildPmmModel(0, "parent", ["goal"], 30,
            [BoneKey(0, 0, 1, 0, new(0, -2, 0))],
            [BoneKey(1, 0, 0, 30, new(1.6f, -1, 0))],
            [], [], []);

        var childPmm = BuildPmmModel(1, "child", ["hip", "knee", "foot", "legIk", "hand"], 5,
            [
                BoneKey(0, 0, 5, 0, new(0.2f, 0, 0)),
                BoneKey(1, 0, 0, 0, Vector3.Zero),
                BoneKey(2, 0, 0, 0, Vector3.Zero),
                BoneKey(3, 0, 0, 0, Vector3.Zero),
                BoneKey(4, 0, 0, 0, Vector3.Zero),
            ],
            [BoneKey(5, 0, 0, 5, new(0.2f, 0, 0))],
            [3, 4],
            [new(0, 0, 0), new(0, -1, -1)],
            [
                new() { RecordIndex = 1, ObjectIndex = 1, PreviousIndex = 0, NextIndex = 2, Frame = 10, Visible = true, OutsideParents = [new(0, 0, 0), new(0, 0, 0)] },
                new() { RecordIndex = 2, ObjectIndex = 2, PreviousIndex = 1, NextIndex = 0, Frame = 20, Visible = true, OutsideParents = [new(0, 0, 0), new(0, -1, -1)] },
            ]);

        var (state, playback) = CreateBoth(WrapDocument(parentPmm, childPmm),
            [("parent", pmxParent, null, false), ("child", pmxChild, null, false)]);
        using var _1 = state; using var _2 = playback;

        Guid parentId = state.ActorIds[0], childId = state.ActorIds[1];
        // Advance past child's DurationFrames=5 so child enters frozen tail while parent continues to frame 15.
        for (int f = 1; f <= 15; f++)
        {
            state.Session.Advance(1d / 30, Token);
            playback.Advance(f, 1d / 30);
        }

        // Verify IK re-solved on frozen child when legIk moved with parent at frame 15.
        var parentGoal15 = state.Session.GetBoneWorldMatrices(parentId)[0].Translation;
        var childWorlds15 = state.Session.GetBoneWorldMatrices(childId);
        Near(parentGoal15, childWorlds15[3].Translation);
        Near(childWorlds15[3].Translation, childWorlds15[2].Translation, 1e-2f);
        // Verify single-bone slot 4 ('hand') attached cleanly at BeginFrame=10 without corrupted rest offset.
        Near(parentGoal15, childWorlds15[4].Translation);

        var pbGoal15 = playback.GetBoneWorldTransform(0, 0).Translation;
        Near(pbGoal15, playback.GetBoneWorldTransform(1, 3).Translation);
        Near(playback.GetBoneWorldTransform(1, 3).Translation, playback.GetBoneWorldTransform(1, 2).Translation, 1e-2f);
        Near(pbGoal15, playback.GetBoneWorldTransform(1, 4).Translation);

        // Advance across EndFrame/BeginFrame=20 where single-bone 'hand' detaches while child stays frozen.
        for (int f = 16; f <= 20; f++)
        {
            state.Session.Advance(1d / 30, Token);
            playback.Advance(f, 1d / 30);
        }

        // Hand returns cleanly to ordinary parent hip (including hip's current IK rotation) + rest (1,2,0), NOT inv(oldParent).
        var sessionWorlds20 = state.Session.GetBoneWorldMatrices(childId);
        Near(Vector3.Transform(new(1, 2, 0), sessionWorlds20[0]), sessionWorlds20[4].Translation);
        Near(Vector3.Transform(new(1, 2, 0), playback.GetBoneWorldTransform(1, 0)), playback.GetBoneWorldTransform(1, 4).Translation);
    }

    [Fact]
    public void DetachedAndGroundOutsideParentsWithDifferentStaleBoneIndicesRemainContinuousAndEquivalent()
    {
        var pmxParent = BuildPmx("parent", [SimpleBone("root")]);
        var pmxChild = BuildPmx("child", [
            SimpleBone("root"),
            SimpleBone("detachedBone", parent: 0),
            SimpleBone("groundBone", new(1, 2, 3), parent: 0),
        ]);
        var parentPmm = BuildPmmModel(0, "parent", ["root"], 20,
            [BoneKey(0, 0, 0, 0, new(50, 0, 0))], [], [], [], []);
        var childPmm = BuildPmmModel(1, "child", ["root", "detachedBone", "groundBone"], 20,
            [
                BoneKey(0, 0, 0, 0, Vector3.Zero),
                BoneKey(1, 0, 3, 0, Vector3.Zero),
                BoneKey(2, 0, 4, 0, Vector3.Zero),
            ],
            [
                BoneKey(3, 1, 0, 20, new(20, 0, 0)),
                BoneKey(4, 2, 0, 20, new(0, 20, 0)),
            ],
            [-1, 1, 2],
            [new(0, 0, 0), new(0, -1, 0), new(0, -2, 0)],
            [
                new()
                {
                    RecordIndex = 1, ObjectIndex = 1, PreviousIndex = 0, NextIndex = 0, Frame = 10, Visible = true,
                    OutsideParents = [new(0, 0, 0), new(0, -1, -1), new(0, -2, -1)],
                },
            ]);

        var rawDoc = WrapDocument(parentPmm, childPmm);
        var roundTripped = RoundTrip(rawDoc);
        Assert.Equal(new PmmOutsideParent(0, -1, 0), roundTripped.Models[1].InitialModelKeyframe.OutsideParents[1] with { Offset = 0 });
        Assert.Equal(new PmmOutsideParent(0, -2, 0), roundTripped.Models[1].InitialModelKeyframe.OutsideParents[2] with { Offset = 0 });
        Assert.Equal(new PmmOutsideParent(0, -1, -1), roundTripped.Models[1].ModelKeyframes[0].OutsideParents[1] with { Offset = 0 });
        Assert.Equal(new PmmOutsideParent(0, -2, -1), roundTripped.Models[1].ModelKeyframes[0].OutsideParents[2] with { Offset = 0 });

        var childOffset = new Vector3(0, 0, 5);
        var (state, playback) = CreateBoth(rawDoc,
            [("parent", pmxParent, null, false), ("child", pmxChild, childOffset, false)]);
        using var _1 = state; using var _2 = playback;

        Guid childId = state.ActorIds[1];
        Assert.Empty(state.Session.PmmControls!.Sample(5).OutsideParentTrackWindows);
        Assert.Empty(state.Session.PmmControls!.Sample(15).OutsideParentTrackWindows);

        foreach (int frame in new[] { 0, 5, 10, 15, 20 })
        {
            state.Session.Seek(frame, Token);
            playback.Seek(frame);

            var sessionWorlds = state.Session.GetBoneWorldMatrices(childId);
            Near(new(50, 0, 0), sessionWorlds[0].Translation);
            Near(new(50 + frame, 0, 0), sessionWorlds[1].Translation);
            Near(new(1, 2 + frame, 3), sessionWorlds[2].Translation);

            Near(new(50, 0, 0), playback.GetBoneWorldTransform(1, 0).Translation);
            Near(new(50 + frame, 0, 0), playback.GetBoneWorldTransform(1, 1).Translation);
            Near(new(1, 2 + frame, 3), playback.GetBoneWorldTransform(1, 2).Translation);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossModelMutualBindingAcrossDistinctBonesThrowsDeterministically(bool delayedToFrame10)
    {
        var pmxA = BuildPmx("modelA", [SimpleBone("rootA"), SimpleBone("boneA")]);
        var pmxB = BuildPmx("modelB", [SimpleBone("rootB"), SimpleBone("boneB")]);

        PmmModel BuildActor(byte index, string name, string[] bones, byte targetModel)
            => delayedToFrame10
                ? BuildPmmModel(index, name, bones, 20,
                    [BoneKey(0, 0, 0, 0, Vector3.Zero), BoneKey(1, 0, 0, 0, Vector3.Zero)],
                    [],
                    [1],
                    [new(0, -1, -1)],
                    [new() { RecordIndex = 1, ObjectIndex = 1, PreviousIndex = 0, NextIndex = 0, Frame = 10, Visible = true, OutsideParents = [new(0, targetModel, 0)] }])
                : BuildPmmModel(index, name, bones, 20,
                    [BoneKey(0, 0, 0, 0, Vector3.Zero), BoneKey(1, 0, 0, 0, Vector3.Zero)],
                    [],
                    [1],
                    [new(0, targetModel, 0)],
                    []);

        var pmmA = BuildActor(0, "modelA", ["rootA", "boneA"], targetModel: 1);
        var pmmB = BuildActor(1, "modelB", ["rootB", "boneB"], targetModel: 0);
        var doc = RoundTrip(WrapDocument(pmmA, pmmB));

        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Create(doc,
            [new(Guid.NewGuid(), 0, pmxA), new(Guid.NewGuid(), 1, pmxB)]));

        var prepared = new List<PreparedPmmModel>
        {
            new(doc.Models[0], "modelA.pmx", pmxA, PmmMotionAdapter.Convert(doc.Models[0], pmxA), PmmMotionAdapter.ConvertSavedPose(doc.Models[0], pmxA)),
            new(doc.Models[1], "modelB.pmx", pmxB, PmmMotionAdapter.Convert(doc.Models[1], pmxB), PmmMotionAdapter.ConvertSavedPose(doc.Models[1], pmxB)),
        };
        var plan = (PmmProjectImportPlan)Activator.CreateInstance(typeof(PmmProjectImportPlan), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [doc, "mutual-cycle.pmm", prepared, new List<PmmUnresolvedAsset>(), new List<string>()], null)!;
        var restScenes = new Dictionary<int, OriginalFrameScene>
        {
            [0] = SharedPhysicsFixture.Rest(pmxA),
            [1] = SharedPhysicsFixture.Rest(pmxB),
        };
        Assert.Throws<InvalidDataException>(() => new PmmProjectPlayback(plan, restScenes, (_, _) => throw new InvalidOperationException()));
    }
}
