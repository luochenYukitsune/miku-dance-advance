using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneOutsideParentSessionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ICharacterPhysicsBackend NoPhysics(EditableSceneObject _) => throw new InvalidOperationException("No rigid bodies.");
    private static VmdMotion Motion(uint duration, Vector3 translation) => new()
    { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = duration, Translation = translation }] };
    private static (SceneCompositionEditor Layout, SceneAnimationSession Session, Guid Child, Guid Parent) Make(bool placed = false, bool shared = false)
    {
        var model = EditedMotionProjectTests.Model(); var rest = EditedMotionProjectTests.Rest(model);
        var layout = new SceneCompositionEditor();
        var child = layout.Add("child", "child.pmx", ModelRole.Character, rest);
        var parent = layout.Add("parent", "parent.pmx", ModelRole.Character, rest);
        if (placed)
        {
            layout.SetTransform(child.Id, new(8, 1, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .4f), 2);
            layout.SetTransform(parent.Id, new(-3, 5, 2), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -.2f), .5f);
        }
        var session = new SceneAnimationSession(layout.Objects,
            [new(child.Id, model, Motion(10, new(2, 0, 0)), PhysicsEnabled: false),
             new(parent.Id, model, Motion(30, new(0, 6, 0)), PhysicsEnabled: false)], NoPhysics, cancellationToken: Token,
            sharedPhysicsFactory: shared ? () => new TrackingSharedBackend() : null);
        return (layout, session, child.Id, parent.Id);
    }
    private static void Near(Matrix4x4 a, Matrix4x4 b)
    {
        var aa = new[] { a.M11,a.M12,a.M13,a.M14,a.M21,a.M22,a.M23,a.M24,a.M31,a.M32,a.M33,a.M34,a.M41,a.M42,a.M43,a.M44 };
        var bb = new[] { b.M11,b.M12,b.M13,b.M14,b.M21,b.M22,b.M23,b.M24,b.M31,b.M32,b.M33,b.M34,b.M41,b.M42,b.M43,b.M44 };
        for (int i = 0; i < aa.Length; i++) Assert.InRange(MathF.Abs(aa[i] - bb[i]), 0, 2e-4f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultKeepWorldPreservesCurrentPoseAndUndoRedoRestoresBinding(bool placed)
    {
        var (_, session, child, parent) = Make(placed); using var owned = session;
        session.Seek(10, Token); var before = session.GetBoneWorldMatrices(child)[0];
        session.SetOutsideParent(child, 0, parent, 0, token: Token);
        Near(before, session.GetBoneWorldMatrices(child)[0]); Assert.Single(session.OutsideParents);
        session.Seek(20, Token); var later = session.GetBoneWorldMatrices(child)[0];
        Assert.NotEqual(before.Translation, later.Translation);
        Assert.True(session.UndoOutsideParent(Token)); Assert.Empty(session.OutsideParents);
        Assert.True(session.RedoOutsideParent(Token)); Assert.Single(session.OutsideParents);
        Near(later, session.GetBoneWorldMatrices(child)[0]);
        session.RemoveOutsideParent(child, 0, Token); Assert.Empty(session.OutsideParents);
        Assert.True(session.CanUndoOutsideParent); Assert.False(session.CanRedoOutsideParent);
    }

    [Fact]
    public void ExplicitOffsetUsesParentSceneWorldAndShortFinishedChildStillFollows()
    {
        var (layout, session, child, parent) = Make(true); using var owned = session;
        var offset = Matrix4x4.CreateRotationZ(.6f) * Matrix4x4.CreateTranslation(0, 3, 1);
        session.SetOutsideParent(child, 0, parent, 0, keepWorld: false, offset: offset, token: Token);
        session.Seek(10, Token);
        Near(Matrix4x4.CreateTranslation(2, 0, 0) * offset * session.GetBoneWorldMatrices(parent)[0], session.GetBoneWorldMatrices(child)[0]);
        session.Advance(10.0 / 30, Token);
        Near(Matrix4x4.CreateTranslation(2, 0, 0) * offset * session.GetBoneWorldMatrices(parent)[0], session.GetBoneWorldMatrices(child)[0]);
        Assert.Equal(10, session.Actors.Single(actor => actor.ObjectId == child).LocalFrame);
        Assert.Equal(layout.Objects[0].Source.Surface.Snapshot.Vertices[0].Position, Vector3.Zero);
    }

    [Fact]
    public void CyclesBadBonesAndSingularOffsetsRejectWithoutChangingPublishedState()
    {
        var (_, session, child, parent) = Make(); using var owned = session;
        session.SetOutsideParent(child, 0, parent, 0, keepWorld: false, token: Token);
        var before = session.Frame; var binding = Assert.Single(session.OutsideParents);
        Assert.ThrowsAny<ArgumentException>(() => session.SetOutsideParent(parent, 0, child, 0, keepWorld: false, token: Token));
        Assert.ThrowsAny<ArgumentException>(() => session.SetOutsideParent(child, 3, parent, 0, keepWorld: false, token: Token));
        Assert.ThrowsAny<ArgumentException>(() => session.SetOutsideParent(child, 0, parent, 3, keepWorld: false, token: Token));
        Assert.ThrowsAny<ArgumentException>(() => session.SetOutsideParent(child, 0, parent, 0, false, new Matrix4x4(), Token));
        Assert.Throws<InvalidOperationException>(() => session.ClearMotion(parent));
        Assert.Same(before, session.Frame); Assert.Equal(binding, Assert.Single(session.OutsideParents));
        Assert.False(session.IsFaulted);
        Assert.Throws<NotSupportedException>(() => ((IList<SceneOutsideParentBinding>)session.OutsideParents).Clear());
    }

    [Fact]
    public void ConstructorPreflightsDuplicatesAndAcceptsWholeModelSlot()
    {
        var (layout, original, child, parent) = Make(); using var owned = original;
        var model = EditedMotionProjectTests.Model();
        SceneActorMotion[] motions = [new(child, model, Motion(10, Vector3.Zero), PhysicsEnabled: false), new(parent, model, Motion(30, Vector3.UnitY), PhysicsEnabled: false)];
        var binding = new SceneOutsideParentBinding(child, -1, parent, 0, Matrix4x4.CreateTranslation(2, 0, 0));
        using var session = new SceneAnimationSession(layout.Objects, motions, NoPhysics, cancellationToken: Token, outsideParents: [binding]);
        session.Seek(30, Token); Near(Matrix4x4.CreateTranslation(2, 1, 0), session.GetBoneWorldMatrices(child)[0]);
        Assert.ThrowsAny<ArgumentException>(() => new SceneAnimationSession(layout.Objects, motions, NoPhysics, cancellationToken: Token, outsideParents: [binding, binding]));
    }

    [Fact]
    public void ParentMotionReplacementRefreshesDependentsAndFailedCandidateLeavesOldSessionUsable()
    {
        var (_, session, child, parent) = Make(); using var owned = session;
        session.SetOutsideParent(child, 0, parent, 0, keepWorld: false, token: Token); session.Seek(10, Token);
        var before = session.Frame; var model = EditedMotionProjectTests.Model();
        Assert.Throws<SceneActorAnimationException>(() => session.BindMotion(new(parent, model,
            new() { BoneKeyframes = [new() { BoneName = "unmatched" }] }, PhysicsEnabled: false), Token));
        Assert.Same(before, session.Frame); Assert.False(session.IsFaulted);
        session.BindMotion(new(parent, model, Motion(30, new(9, 0, 0)), PhysicsEnabled: false), Token);
        Near(Matrix4x4.CreateTranslation(5, 0, 0), session.GetBoneWorldMatrices(child)[0]);
        session.Advance(10.0 / 30, Token);
        Near(Matrix4x4.CreateTranslation(8, 0, 0), session.GetBoneWorldMatrices(child)[0]);
    }

    [Fact]
    public void ReverseListedThreeModelChainEvaluatesParentsBeforeDependents()
    {
        var model = EditedMotionProjectTests.Model(); var rest = EditedMotionProjectTests.Rest(model);
        var layout = new SceneCompositionEditor();
        var child = layout.Add("child", "child.pmx", ModelRole.Character, rest);
        var middle = layout.Add("middle", "middle.pmx", ModelRole.Character, rest);
        var parent = layout.Add("parent", "parent.pmx", ModelRole.Character, rest);
        SceneOutsideParentBinding[] bindings = [new(child.Id, 0, middle.Id, 0, Matrix4x4.Identity), new(middle.Id, 0, parent.Id, 0, Matrix4x4.Identity)];
        using var session = new SceneAnimationSession(layout.Objects,
            [new(child.Id, model, Motion(30, new(2, 0, 0)), PhysicsEnabled: false), new(middle.Id, model, Motion(30, new(3, 0, 0)), PhysicsEnabled: false),
             new(parent.Id, model, Motion(30, new(4, 0, 0)), PhysicsEnabled: false)], NoPhysics, cancellationToken: Token, outsideParents: bindings);
        session.Seek(30, Token); Near(Matrix4x4.CreateTranslation(9, 0, 0), session.GetBoneWorldMatrices(child.Id)[0]);
        bindings[0] = bindings[0] with { Offset = Matrix4x4.CreateTranslation(999, 0, 0) };
        session.Seek(0, Token); session.Seek(30, Token);
        Near(Matrix4x4.CreateTranslation(9, 0, 0), session.GetBoneWorldMatrices(child.Id)[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void KeepWorldRejectsChangedPhysicalPoseBeforePublishingAndReleasesCandidates(int subject)
    {
        var model = PhysicsModel(); var layout = new SceneCompositionEditor(); var rest = EditedMotionProjectTests.Rest(model);
        var child = layout.Add("child", "child.pmx", ModelRole.Character, rest);
        var parent = layout.Add("parent", "parent.pmx", ModelRole.Character, rest);
        var backends = new List<FixtureBackend>();
        using var session = new SceneAnimationSession(layout.Objects,
            [new(child.Id, model, Motion(10, Vector3.Zero)), new(parent.Id, model, Motion(30, Vector3.UnitX), PhysicsEnabled: false)],
            _ => { var backend = new FixtureBackend { InitializeOffset = backends.Count == 0 ? Vector3.Zero : Vector3.UnitY }; backends.Add(backend); return backend; }, cancellationToken: Token);
        session.Advance(1.0 / 30, Token); // A genuine dynamic displacement cannot be reconstructed by the fresh solver.
        var before = session.Frame; var matrix = session.GetBoneWorldMatrices(child.Id)[0];
        var error = Assert.Throws<InvalidOperationException>(() => session.SetOutsideParent(child.Id, subject, parent.Id, 0, token: Token));
        Assert.Contains("keep-world", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(before, session.Frame); Near(matrix, session.GetBoneWorldMatrices(child.Id)[0]);
        Assert.Empty(session.OutsideParents); Assert.False(session.CanUndoOutsideParent); Assert.False(session.IsFaulted);
        Assert.Equal(2, backends.Count); Assert.Equal(0, backends[0].DisposeCount); Assert.Equal(1, backends[1].DisposeCount);
        session.Advance(1.0 / 30, Token); Assert.True(backends[0].Steps > 4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FinishedDynamicChildFollowsRepeatedRotatingParentWithoutSolverWork(int subject)
    {
        var model = PhysicsModel(); var layout = new SceneCompositionEditor(); var rest = EditedMotionProjectTests.Rest(model);
        var child = layout.Add("child", "child.pmx", ModelRole.Character, rest);
        var parent = layout.Add("parent", "parent.pmx", ModelRole.Character, rest);
        layout.SetTransform(child.Id, new(2, 1, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f), 2);
        // Physics consumes rigid model-space transforms; equal nonidentity placements preserve that contract.
        layout.SetTransform(parent.Id, new(-1, 4, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -.2f), 2);
        var parentMotion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30,
            Translation = new(3, 1, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1) }] };
        var backend = new FixtureBackend();
        using var session = new SceneAnimationSession(layout.Objects,
            [new(child.Id, model, Motion(1, Vector3.Zero)), new(parent.Id, model, parentMotion, PhysicsEnabled: false)],
            _ => backend, cancellationToken: Token,
            outsideParents: [new(child.Id, subject, parent.Id, 0, Matrix4x4.Identity)]);
        session.Advance(1.0 / 30, Token);
        int steps = backend.Steps; int initializations = backend.Initializations;
        var frozen = session.GetBoneWorldMatrices(child.Id)[0]; var frozenVertex = session.Frame.Surface.Snapshot.Vertices[0].Position;
        Assert.True(Matrix4x4.Invert(session.GetBoneWorldMatrices(parent.Id)[0], out var inverseParent));
        for (int i = 0; i < 3; i++)
        {
            session.Advance(2.0 / 30, Token);
            var delta = inverseParent * session.GetBoneWorldMatrices(parent.Id)[0];
            Near(frozen * delta, session.GetBoneWorldMatrices(child.Id)[0]);
            Assert.InRange(Vector3.Distance(Vector3.Transform(frozenVertex, delta), session.Frame.Surface.Snapshot.Vertices[0].Position), 0, 2e-4f);
            Assert.Equal(steps, backend.Steps); Assert.Equal(initializations, backend.Initializations);
            Assert.Equal(1, session.Actors.Single(actor => actor.ObjectId == child.Id).LocalFrame);
        }
    }

    [Fact]
    public void WholeModelKeepWorldAlsoChecksNonRootDynamicBones()
    {
        var model = PhysicsModel(secondary: true); var layout = new SceneCompositionEditor(); var rest = EditedMotionProjectTests.Rest(model);
        var child = layout.Add("child", "child.pmx", ModelRole.Character, rest);
        var parent = layout.Add("parent", "parent.pmx", ModelRole.Character, rest);
        var backends = new List<FixtureBackend>();
        using var session = new SceneAnimationSession(layout.Objects,
            [new(child.Id, model, Motion(10, Vector3.Zero)), new(parent.Id, model, Motion(30, Vector3.Zero), PhysicsEnabled: false)],
            _ => { var backend = new FixtureBackend(); backends.Add(backend); return backend; }, cancellationToken: Token);
        session.Advance(1.0 / 30, Token); var before = session.Frame;
        var error = Assert.Throws<InvalidOperationException>(() => session.SetOutsideParent(child.Id, -1, parent.Id, 0, token: Token));
        Assert.Contains("bone 1", error.Message, StringComparison.Ordinal);
        Assert.Same(before, session.Frame); Assert.Empty(session.OutsideParents);
        Assert.Equal(0, backends[0].DisposeCount); Assert.Equal(1, backends[1].DisposeCount);
    }

    [Fact]
    public void FrozenNestedBoneAttachmentOverridesWholeModelAndTransformsDescendantsOnce()
    {
        var model = PhysicsModel(secondary: true); var layout = new SceneCompositionEditor(); var rest = EditedMotionProjectTests.Rest(model);
        var child = layout.Add("child", "child.pmx", ModelRole.Character, rest);
        var first = layout.Add("first", "first.pmx", ModelRole.Character, rest);
        var second = layout.Add("second", "second.pmx", ModelRole.Character, rest);
        var backend = new FixtureBackend();
        using var session = new SceneAnimationSession(layout.Objects,
            [new(child.Id, model, Motion(1, Vector3.Zero)), new(first.Id, model, Motion(30, new(6, 0, 0)), PhysicsEnabled: false),
             new(second.Id, model, Motion(30, new(0, 3, 0)), PhysicsEnabled: false)], _ => backend, cancellationToken: Token,
            outsideParents: [new(child.Id, -1, first.Id, 0, Matrix4x4.Identity), new(child.Id, 1, second.Id, 0, Matrix4x4.Identity)]);
        session.Advance(1.0 / 30, Token);
        var root = session.GetBoneWorldMatrices(child.Id)[0]; var tip = session.GetBoneWorldMatrices(child.Id)[1];
        var descendant = session.GetBoneWorldMatrices(child.Id)[2];
        int steps = backend.Steps;
        for (int i = 1; i <= 3; i++)
        {
            session.Advance(1.0 / 30, Token);
            Near(root * Matrix4x4.CreateTranslation(.2f*i, 0, 0), session.GetBoneWorldMatrices(child.Id)[0]);
            Near(tip * Matrix4x4.CreateTranslation(0, .1f*i, 0), session.GetBoneWorldMatrices(child.Id)[1]);
            Near(descendant * Matrix4x4.CreateTranslation(0, .1f*i, 0), session.GetBoneWorldMatrices(child.Id)[2]);
            Assert.Equal(steps, backend.Steps);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacingModelWithAnActiveBindingInvalidatesHistoryOnlyAfterSuccess(bool shared)
    {
        var (_, session, child, parent) = Make(shared: shared);
        using var owned = session;
        Assert.Equal(shared, session.UsesSharedPhysicsWorld);
        session.SetOutsideParent(child, 0, parent, 0, false, token: Token);
        session.RemoveOutsideParent(child, 0, Token);
        Assert.True(session.UndoOutsideParent(Token));
        Assert.True(session.CanUndoOutsideParent);
        Assert.True(session.CanRedoOutsideParent);
        var binding = Assert.Single(session.OutsideParents);

        var replacementMotion = Motion(30, Vector3.Zero);
        Assert.Throws<SceneActorAnimationException>(() =>
            session.BindMotion(new(parent, EditedMotionProjectTests.Model("other"), replacementMotion, PhysicsEnabled: false), Token));
        Assert.True(session.CanUndoOutsideParent);
        Assert.True(session.CanRedoOutsideParent);
        session.BindMotion(new(parent, EditedMotionProjectTests.Model(), replacementMotion, PhysicsEnabled: false), Token);
        Assert.Equal(binding, Assert.Single(session.OutsideParents));
        Assert.False(session.CanUndoOutsideParent);
        Assert.False(session.CanRedoOutsideParent);
    }

    [Fact]
    public void ClearingMotionOrReplacingModelInvalidatesDetachedBindingHistoryOnlyAfterSuccess()
    {
        var (_, session, child, parent) = Make(); using var owned = session;
        void DetachedHistory()
        {
            session.SetOutsideParent(child, 0, parent, 0, false, token: Token);
            session.RemoveOutsideParent(child, 0, Token);
            Assert.True(session.CanUndoOutsideParent);
        }
        DetachedHistory(); session.ClearMotion(parent);
        Assert.False(session.CanUndoOutsideParent); Assert.False(session.CanRedoOutsideParent);
        session.BindMotion(new(parent, EditedMotionProjectTests.Model(), Motion(30, Vector3.UnitX), PhysicsEnabled: false), Token);
        DetachedHistory();
        var before = session.Frame;
        Assert.Throws<SceneActorAnimationException>(() => session.BindMotion(new(parent, EditedMotionProjectTests.Model("other"), Motion(30, Vector3.Zero), PhysicsEnabled: false), Token));
        Assert.Same(before, session.Frame); Assert.True(session.CanUndoOutsideParent);
        session.BindMotion(new(parent, EditedMotionProjectTests.Model(), Motion(30, Vector3.Zero), PhysicsEnabled: false), Token);
        Assert.False(session.CanUndoOutsideParent); Assert.False(session.CanRedoOutsideParent);
    }

    private static PmxModel PhysicsModel(bool secondary = false)
    {
        var model = EditedMotionProjectTests.Model();
        return new() { Header = model.Header, Name = model.Name, EnglishName = model.EnglishName, Comment = model.Comment,
            EnglishComment = model.EnglishComment, Vertices = model.Vertices, Faces = model.Faces, Textures = model.Textures,
            Materials = model.Materials, Bones = secondary ? [model.Bones[0], new() { Name = "tip", EnglishName = "", Position = Vector3.UnitY,
                ParentIndex = 0, DeformDepth = 0, Flags = PmxBoneFlags.None }, new() { Name = "descendant", EnglishName = "", Position = new(0, 2, 0),
                ParentIndex = 1, DeformDepth = 0, Flags = PmxBoneFlags.None }] : model.Bones, Morphs = model.Morphs, DisplayFrames = model.DisplayFrames,
            Joints = [], SoftBodies = [], RigidBodies = [new() { Name = "body", EnglishName = "", BoneIndex = secondary ? 1 : 0, Group = 0, CollisionGroup = 0,
                Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = secondary ? Vector3.UnitY : Vector3.Zero, Rotation = Vector3.Zero,
                Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic }] };
    }

    private sealed class FixtureBackend : ICharacterPhysicsBackend
    {
        private Matrix4x4[] worlds = [];
        public int Steps, DisposeCount, Initializations;
        public Vector3 InitializeOffset;
        public string Name => "outside-parent dynamic fixture";
        public IReadOnlyList<string> Diagnostics => Array.Empty<string>();
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> input)
        { worlds = input.ToArray(); foreach (ref var world in worlds.AsSpan()) world.Translation += InitializeOffset; Initializations++; }
        public void SetKinematicTarget(int index, Matrix4x4 world, float seconds) => worlds[index] = world;
        public Matrix4x4 GetBodyWorldTransform(int index) => worlds[index];
        public void Step(float seconds)
        { Steps++; for (int i = 0; i < worlds.Length; i++) worlds[i].Translation += new Vector3(0, -seconds, 0); }
        public void Dispose() => DisposeCount++;
    }
}
