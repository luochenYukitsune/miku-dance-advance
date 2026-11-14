using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SoftLifecycleTests
{
    [Theory]
    [InlineData(PmxVertexWeightType.Bdef1, false)]
    [InlineData(PmxVertexWeightType.Bdef2, false)]
    [InlineData(PmxVertexWeightType.Bdef4, false)]
    [InlineData(PmxVertexWeightType.Sdef, false)]
    [InlineData(PmxVertexWeightType.Qdef, false)]
    [InlineData(PmxVertexWeightType.Bdef2, true)]
    [InlineData(PmxVertexWeightType.Sdef, true)]
    [InlineData(PmxVertexWeightType.Qdef, true)]
    public void FrozenSoftVerticesUseExactSkinMapAndImmutableBaseline(PmxVertexWeightType kind, bool morph)
    {
        var model = Model(kind, morph: morph);
        using var controller = Controller(model);
        var old = Matrix4x4.CreateRotationX(.4f) * Matrix4x4.CreateTranslation(4, -1, 2);
        controller.SetOutsideParents(Parents(old, old));
        var soft = new FrozenSoft(); controller.EnableSoftBodies(soft, 0);
        var before = controller.Advance(60, .1); var vertices = before.Surface.Snapshot.Vertices.ToArray();
        int resets = soft.Resets, steps = soft.Steps;
        Matrix4x4.Invert(old, out var inverse);
        var a = Matrix4x4.CreateRotationZ(.8f) * Matrix4x4.CreateTranslation(-2, 3, 1);
        var b = Matrix4x4.CreateRotationY(-.6f) * Matrix4x4.CreateTranslation(5, 1, -3);
        // An independent ordinary skin of the deformed material-space points is the position oracle.
        var deformed = Model(kind, vertices.Select(v => Vector3.Transform(v.Position, inverse)).ToArray());
        using var oracle = Controller(deformed); oracle.SetOutsideParents(Parents(a, b));
        var expected = oracle.SampleAt(60);
        var moved = controller.RefreshFrozenOutsideParents(Parents(a, b));
        foreach (int id in new[] { 4, 5, 6 })
        {
            Near(expected.Surface.Snapshot.Vertices[id].Position, moved.Surface.Snapshot.Vertices[id].Position);
            Assert.Equal(moved.Surface.Snapshot.Vertices[id].Position, moved.Outline.Vertices[id].Position);
            Assert.InRange(moved.Surface.Snapshot.Vertices[id].Normal.Length(), .99999f, 1.00001f);
        }
        using var nonsoft = Controller(model); nonsoft.SetOutsideParents(Parents(a, b));
        Assert.Equal(nonsoft.SampleAt(60).Surface.Snapshot.Vertices[0], moved.Surface.Snapshot.Vertices[0]);
        var first = moved.Surface.Snapshot.Vertices.ToArray();
        for (int i = 0; i < 25; i++)
        {
            var returned = controller.RefreshFrozenOutsideParents(Parents(old, old));
            foreach (int id in new[] { 4, 5, 6 }) Assert.Equal(vertices[id], returned.Surface.Snapshot.Vertices[id]);
            moved = controller.RefreshFrozenOutsideParents(Parents(a, b));
        }
        Assert.Equal(first, moved.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(vertices, before.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(resets, soft.Resets); Assert.Equal(steps, soft.Steps);
        Assert.Throws<InvalidOperationException>(() => controller.Advance(60, .01));
        controller.ResetPhysics(0); controller.Advance(1, .01);
        Assert.True(soft.Resets > resets); Assert.True(soft.Steps > steps);
    }

    [Fact]
    public void FrozenSoftNormalUsesInverseTransposeOfFullBlendedMap()
    {
        using var controller = Controller(Model(PmxVertexWeightType.Bdef2));
        controller.SetOutsideParents(Parents(Matrix4x4.Identity, Matrix4x4.Identity));
        var soft = new FrozenSoft(); controller.EnableSoftBodies(soft, 0);
        var before = controller.Advance(60, .1);
        var a = Matrix4x4.CreateScale(2, 1, 1); var b = Matrix4x4.CreateScale(1, 3, 1);
        var moved = controller.RefreshFrozenOutsideParents(Parents(a, b));
        // BDEF2 weights .35/.65 give diag(1.35,2.3,1), a geometric normal uses reciprocal scale.
        Vector3 n = before.Surface.Snapshot.Vertices[4].Normal;
        Near(Vector3.Normalize(new(n.X / 1.35f, n.Y / 2.3f, n.Z)), moved.Surface.Snapshot.Vertices[4].Normal);
    }

    [Fact]
    public void SingularFrozenBlendRejectsAtomicallyWithoutTouchingSoftHistory()
    {
        using var controller = Controller(Model(PmxVertexWeightType.Bdef4, equalWeights: true));
        controller.SetOutsideParents(Parents(Matrix4x4.Identity, Matrix4x4.Identity));
        var soft = new FrozenSoft(); controller.EnableSoftBodies(soft, 0);
        var before = controller.Advance(60, .1); var worlds = controller.BoneWorldMatrices.ToArray();
        int resets = soft.Resets, steps = soft.Steps;
        // Each bone world remains invertible, while the equal-weight vertex map collapses X/Y.
        Assert.Throws<NotSupportedException>(() => controller.RefreshFrozenOutsideParents(
            Parents(Matrix4x4.Identity, Matrix4x4.CreateScale(-1, -1, 1))));
        Assert.Same(before.Surface, controller.SampleAt(60).Surface);
        Assert.Equal(worlds, controller.BoneWorldMatrices); Assert.Equal(resets, soft.Resets); Assert.Equal(steps, soft.Steps);
        var valid = controller.RefreshFrozenOutsideParents(Parents(Matrix4x4.CreateTranslation(1, 0, 0), Matrix4x4.Identity));
        Assert.NotSame(before.Surface, valid.Surface);
    }

    [Fact]
    public void SharedFrozenCompletionRetainsProjectedClothWhileMovingParentContinues()
    {
        var model = Model(PmxVertexWeightType.Bdef2); var layout = new SceneCompositionEditor();
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1));
        var child = layout.Add("short cloth", "cloth.pmx", ModelRole.Character, rest);
        var parent = layout.Add("moving parent", "parent.pmx", ModelRole.Character, rest);
        var shortMotion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 1 }] };
        var longMotion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 10, Translation = new(4, 0, 0) }] };
        var soft = new FrozenSoft();
        using var session = new SceneAnimationSession(layout.Objects,
            [new(child.Id, model, shortMotion), new(parent.Id, model, longMotion, PhysicsEnabled: false)],
            _ => throw new InvalidOperationException(), outsideParents: [new(child.Id, -1, parent.Id, 0, Matrix4x4.Identity)],
            sharedPhysicsFactory: () => new TrackingSharedBackend(), softPhysicsFactory: _ => soft);
        session.Advance(1d / 30, TestContext.Current.CancellationToken);
        var first = session.Frame.Surface.Snapshot.Vertices[4]; int steps = soft.Steps, resets = soft.Resets;
        session.Advance(2d / 30, TestContext.Current.CancellationToken);
        Near(first.Position + new Vector3(.8f, 0, 0), session.Frame.Surface.Snapshot.Vertices[4].Position);
        Assert.Equal(steps, soft.Steps); Assert.Equal(resets, soft.Resets);
    }

    [Fact]
    public void ReplacingRigidBackendCommitsProxyBatchAndPreservesSoftPoseAndRemainder()
    {
        using var controller = Controller(Model(PmxVertexWeightType.Bdef1, rigid: true));
        var old = new SceneAnimationSessionTests.FallingBackend(); controller.EnablePhysicsAt(old, 0);
        var soft = new FrozenSoft(); controller.EnableSoftBodies(soft, 0);
        var before = controller.Advance(3, .1 + 1d / 240); int resets = soft.Resets, steps = soft.Steps;
        var candidate = new SceneAnimationSessionTests.FallingBackend(); controller.EnablePhysicsAt(candidate, 3);
        Assert.Equal(1, old.DisposeCount); Assert.Equal(0, candidate.DisposeCount);
        Assert.Equal(1, soft.BatchCalls); Assert.Equal(candidate.GetBodyWorldTransform(0), Assert.Single(soft.Rigids));
        foreach (int id in new[] { 4, 5, 6 })
            Assert.Equal(before.Surface.Snapshot.Vertices[id], controller.SampleAt(3).Surface.Snapshot.Vertices[id]);
        Assert.Equal(resets, soft.Resets); Assert.Equal(steps, soft.Steps);
        controller.Advance(3.125, 1d / 240);
        Assert.Equal(steps + 1, soft.Steps); Assert.Equal(resets, soft.Resets);
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("sample")]
    [InlineData("proxy")]
    public void FailedRigidReplacementPreservesPublishedPoseAndBothOldHistories(string failure)
    {
        using var controller = Controller(Model(PmxVertexWeightType.Bdef1, rigid: true));
        var old = new SceneAnimationSessionTests.FallingBackend(); controller.EnablePhysicsAt(old, 0);
        var soft = new FrozenSoft(); controller.EnableSoftBodies(soft, 0);
        var before = controller.Advance(3, .1); int resets = soft.Resets, steps = soft.Steps;
        var proxies = soft.Rigids.ToArray();
        var candidate = new SceneAnimationSessionTests.FallingBackend { FailInitialize = failure == "initialize" };
        soft.FailSample = failure == "sample"; soft.FailBatch = failure == "proxy";
        Assert.Throws<InvalidOperationException>(() => controller.EnablePhysicsAt(candidate, 3));
        Assert.Equal(1, candidate.DisposeCount); Assert.Equal(0, old.DisposeCount);
        Assert.Same(before.Surface, controller.SampleAt(3).Surface);
        Assert.Equal(proxies, soft.Rigids); Assert.Equal(resets, soft.Resets); Assert.Equal(steps, soft.Steps);
        soft.FailSample = soft.FailBatch = false;
        controller.Advance(4, 1d / 30); Assert.True(soft.Steps > steps); Assert.True(old.Steps > 0);
    }

    [Fact]
    public void RigidReplacementAtDifferentSoftTimeRejectsBeforeAnyHistoryChanges()
    {
        using var controller = Controller(Model(PmxVertexWeightType.Bdef1, rigid: true));
        var soft = new FrozenSoft(); controller.EnableSoftBodies(soft, 0);
        var before = controller.Advance(3, .1); int resets = soft.Resets, steps = soft.Steps;
        var candidate = new SceneAnimationSessionTests.FallingBackend();
        Assert.Throws<InvalidOperationException>(() => controller.EnablePhysicsAt(candidate, 0));
        Assert.Equal(1, candidate.DisposeCount); Assert.Same(before.Surface, controller.SampleAt(3).Surface);
        Assert.Equal(resets, soft.Resets); Assert.Equal(steps, soft.Steps); Assert.Equal(0, soft.BatchCalls);
    }

    [Fact]
    public void RetiredRigidCleanupFailureIsRecordedWithoutInvalidatingCommittedReplacement()
    {
        var controller = Controller(Model(PmxVertexWeightType.Bdef1, rigid: true));
        var old = new SceneAnimationSessionTests.FallingBackend { FailDispose = true }; controller.EnablePhysicsAt(old, 0);
        var soft = new FrozenSoft(); controller.EnableSoftBodies(soft, 0); controller.Advance(3, .1);
        var candidate = new SceneAnimationSessionTests.FallingBackend();
        controller.EnablePhysicsAt(candidate, 3);
        Assert.Equal(1, old.DisposeCount); Assert.Equal(0, candidate.DisposeCount);
        Assert.Contains("cleanup failed", Assert.Single(controller.CleanupDiagnostics), StringComparison.Ordinal);
        Assert.Contains(controller.CleanupDiagnostics[0], controller.Diagnostics);
        controller.Advance(4, 1d / 30); Assert.True(candidate.Steps > 0);
        controller.Dispose(); Assert.Equal(1, candidate.DisposeCount); Assert.Equal(1, old.DisposeCount);
    }

    [Fact]
    public void ActualBulletProxyReplacementPreservesVelocityAndFractionalStepHistory()
    {
        var model = Model(PmxVertexWeightType.Bdef1, rigid: true);
        var vertices = model.Vertices.Select(v => new SceneVertex(v.Position, v.Normal, v.Uv)).ToArray();
        using var changed = new PmxSoftBodyPlayback(model, new SoftBodyBulletBackend(new(0, -9.8f, 0), null));
        using var control = new PmxSoftBodyPlayback(model, new SoftBodyBulletBackend(new(0, -9.8f, 0), null));
        Matrix4x4[] proxy = [Matrix4x4.CreateTranslation(100, 100, 100)];
        changed.Reset(vertices, proxy); control.Reset(vertices, proxy);
        changed.Advance(.2 + 1d / 240, vertices, proxy); control.Advance(.2 + 1d / 240, vertices, proxy);
        var before = control.Apply(vertices);
        proxy[0] = Matrix4x4.CreateTranslation(200, 100, 100);
        changed.UpdateRigidProxies(proxy);
        Assert.Equal(before, changed.Apply(vertices));
        var expected = control.Advance(1d / 240, vertices, proxy);
        var actual = changed.Advance(1d / 240, vertices, proxy);
        Assert.Equal(expected, actual);
        Assert.NotEqual(before[4].Position, actual[4].Position);
    }

    private static Dictionary<int, Matrix4x4> Parents(Matrix4x4 a, Matrix4x4 b) => new() { [-1] = a, [1] = b };
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, 3e-5f);
    private static CharacterAnimationController Controller(PmxModel model) => new(model,
        new() { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 60 }],
            MorphKeyframes = model.Morphs.Count > 0 ? [new() { MorphName = "shift", Weight = .2f }, new() { MorphName = "shift", Frame = 60, Weight = .7f }] : [] },
        PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model));

    private static PmxModel Model(PmxVertexWeightType kind, Vector3[]? positions = null, bool equalWeights = false, bool rigid = false, bool morph = false)
    {
        var m = PmxSoftBodyTests.MaterialFixture();
        return new() { Header = m.Header, Name = m.Name, EnglishName = m.EnglishName, Comment = m.Comment, EnglishComment = m.EnglishComment,
            Vertices = m.Vertices.Select((v, i) => new PmxVertex { Position = positions?[i] ?? v.Position,
                Normal = v.Normal, Uv = v.Uv, AdditionalUvs = [], WeightType = i < 4 ? PmxVertexWeightType.Bdef1 : kind,
                BoneIndices = i < 4 || kind == PmxVertexWeightType.Bdef1 ? [0, -1, -1, -1]
                    : kind is PmxVertexWeightType.Bdef4 or PmxVertexWeightType.Qdef ? [0, 1, 0, 1] : [0, 1, -1, -1],
                BoneWeights = i < 4 || kind == PmxVertexWeightType.Bdef1 ? [1, 0, 0, 0]
                    : kind is PmxVertexWeightType.Bdef4 or PmxVertexWeightType.Qdef ? equalWeights ? [.25f, .25f, .25f, .25f] : [.1f, .2f, .25f, .45f] : [.35f, .65f, 0, 0],
                SdefC = new(.2f, .7f, -.3f), SdefR0 = new(1, 2, 3), SdefR1 = new(-2, 1, .5f), EdgeScale = v.EdgeScale }).ToArray(),
            Faces = m.Faces, Textures = m.Textures, Materials = m.Materials,
            Bones = PmmMotionAdapterTests.Pmx(["root", "nested"]).Bones,
            Morphs = morph ? [new() { Name = "shift", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Position,
                PositionOffsets = [new(4, new(.7f, -1.3f, .5f)), new(5, new(.3f, .2f, -.6f))] }] : [],
            DisplayFrames = m.DisplayFrames, Joints = m.Joints, SoftBodies = m.SoftBodies,
            RigidBodies = rigid ? [new() { Name = "far proxy", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
                Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.1f), Translation = new(100), Rotation = Vector3.Zero,
                Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic }] : [] };
    }

    private sealed class FrozenSoft : ISoftBodyBackend, ISoftBodyRigidProxyUpdater
    {
        private int[] ids = []; private Vector3[] positions = [];
        public int Resets, Steps, BatchCalls;
        public bool FailSample, FailBatch;
        public Matrix4x4[] Rigids = [];
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(IReadOnlyList<SoftBodyDefinition> definitions, IReadOnlyList<PhysicsBodyDefinition> colliders)
            => ids = definitions.SelectMany(d => d.VertexIndices).Distinct().ToArray();
        public void Reset(ReadOnlySpan<Vector3> targets, ReadOnlySpan<Matrix4x4> rigids) { Resets++; positions = targets.ToArray(); Rigids = rigids.ToArray(); }
        public void Step(float seconds, ReadOnlySpan<Vector3> targets, ReadOnlySpan<Matrix4x4> rigids)
        { Steps++; Rigids = rigids.ToArray(); foreach (int id in ids) positions[id] += new Vector3(seconds * 2, -seconds, seconds * .5f); }
        public IReadOnlyList<SoftBodySample> Sample()
        {
            if (FailSample) throw new InvalidOperationException("injected soft sample");
            return [new(ids, ids.Select(id => positions[id]).ToArray(), ids.Select(_ => Vector3.Normalize(new Vector3(1, 2, 3))).ToArray())];
        }
        public void UpdateRigidProxies(ReadOnlySpan<Matrix4x4> rigids)
        { if (FailBatch) throw new InvalidOperationException("injected atomic proxy batch"); BatchCalls++; Rigids = rigids.ToArray(); }
        public void SetGravity(Vector3 gravity) { }
        public void Dispose() { }
    }
}
