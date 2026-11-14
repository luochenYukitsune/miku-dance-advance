using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PreparedOutsideParentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void KeepWorldUsesAccumulatedLiveSubjectAndTargetWithoutTouchingTheirPhysicalWorlds()
    {
        var (project, child, parent, provider) = Create(physicalChild: true, physicalParent: true);
        using (project)
        {
            project.Advance(1d / 30, Token);
            var before = project.Frame;
            var childWorld = World(project, child); var parentWorld = World(project, parent);
            Assert.True(childWorld.Translation.Y < 0);
            Assert.True(parentWorld.Translation.Y > 0);
            Assert.Equal(2, provider.Backends.Count);
            var counts = provider.Backends.Select(backend => (backend.Steps, backend.Initializations, backend.DisposeCount)).ToArray();
            double frame = project.CurrentFrame;

            var binding = project.PrepareOutsideParent(child, 0, parent, 0, cancellationToken: Token);

            // Both authored local root transforms are identity. Only the actual accumulated
            // physical worlds, rather than fresh solvers at the same clock, produce this offset.
            Near(childWorld, binding.Offset * parentWorld);
            Assert.Same(before, project.Frame);
            Assert.Equal(frame, project.CurrentFrame);
            Near(childWorld, World(project, child)); Near(parentWorld, World(project, parent));
            Assert.Empty(project.OutsideParents); Assert.False(project.CanUndo); Assert.False(project.CanRedo);
            Assert.Equal(2, provider.Backends.Count);
            Assert.Equal(counts, provider.Backends.Select(backend => (backend.Steps, backend.Initializations, backend.DisposeCount)));
        }
    }

    [Fact]
    public void LiveCompensatedOffsetCanBeAppliedInOneProjectTransactionWithOneUndoStep()
    {
        var (project, child, parent, provider) = Create(physicalChild: true);
        using (project)
        {
            project.Advance(1d / 30, Token);
            var world = World(project, child); var frame = project.Frame;
            var binding = project.PrepareOutsideParent(child, 0, parent, 0, cancellationToken: Token);
            Assert.Same(frame, project.Frame); Assert.False(project.CanUndo);
            Assert.Single(provider.Backends);

            project.EditWithWorldPreservation(edit => edit.OutsideParents.Add(binding), [(child, 0)], Token);

            Assert.Equal(binding, Assert.Single(project.OutsideParents));
            Near(world, World(project, child));
            Assert.True(project.CanUndo); Assert.False(project.CanRedo);
            Assert.Equal(2, provider.Backends.Count);
            Assert.Equal(1, provider.Backends[0].DisposeCount);
            Assert.True(project.Undo(Token)); Assert.Empty(project.OutsideParents);
            Assert.False(project.CanUndo); Assert.True(project.CanRedo);
            Assert.True(project.Redo(Token)); Assert.Equal(binding, Assert.Single(project.OutsideParents));
            Near(world, World(project, child));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NewlyImportedEmptyMotionSupportsKeepWorldWithoutInventingAnimationKeys(int subject)
    {
        var (project, child, parent, provider) = Create(emptyMotion: true, placed: true);
        using (project)
        {
            project.Seek(12, Token);
            var world = World(project, child); var before = project.Frame;
            var binding = project.PrepareOutsideParent(child, subject, parent, 0, cancellationToken: Token);

            Assert.Same(before, project.Frame); Assert.Empty(project.OutsideParents);
            Assert.False(project.CanUndo); Assert.Empty(provider.Backends);
            project.EditWithWorldPreservation(edit => edit.OutsideParents.Add(binding), [(child, subject)], Token);
            Near(world, World(project, child));
            Assert.All(project.CaptureAuthoredContent().Motions, actor =>
            {
                Assert.Empty(actor.Motion.BoneKeyframes);
                Assert.Empty(actor.Motion.MorphKeyframes);
                Assert.Empty(actor.Motion.IkKeyframes);
            });
            Assert.True(project.Undo(Token)); Assert.Empty(project.OutsideParents);
            Assert.False(project.CanUndo);
        }
    }

    [Fact]
    public void DynamicTargetRestartMismatchRejectsBeforePublishingAndReleasesOnlyCandidates()
    {
        var (project, child, parent, provider) = Create(physicalChild: true, physicalParent: true);
        using (project)
        {
            project.Advance(1d / 30, Token);
            var before = project.Frame; var source = World(project, child); var target = World(project, parent);
            var live = provider.Backends.ToArray();
            var counts = live.Select(backend => (backend.Steps, backend.Initializations, backend.DisposeCount)).ToArray();
            var binding = project.PrepareOutsideParent(child, 0, parent, 0, cancellationToken: Token);
            double frame = project.CurrentFrame;

            var error = Assert.Throws<InvalidOperationException>(() => project.EditWithWorldPreservation(
                edit => edit.OutsideParents.Add(binding), [(child, 0)], Token));

            Assert.Contains("keep-world", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Same(before, project.Frame); Assert.Equal(frame, project.CurrentFrame);
            Near(source, World(project, child)); Near(target, World(project, parent));
            Assert.Empty(project.OutsideParents); Assert.False(project.CanUndo); Assert.False(project.CanRedo); Assert.False(project.IsFaulted);
            Assert.Equal(4, provider.Backends.Count);
            Assert.Equal(counts, live.Select(backend => (backend.Steps, backend.Initializations, backend.DisposeCount)));
            Assert.All(provider.Backends.Skip(2), backend => Assert.Equal(1, backend.DisposeCount));
            project.Advance(1d / 30, Token);
            Assert.All(live, backend => Assert.True(backend.Steps > counts[Array.IndexOf(live, backend)].Steps));
        }
    }

    [Fact]
    public void WholeModelCheckIncludesDynamicDescendantsEvenWhenTheRootIsPreserved()
    {
        var (project, child, parent, provider) = Create(physicalChild: true, secondary: true);
        using (project)
        {
            project.Advance(1d / 30, Token);
            var root = World(project, child); var tip = World(project, child, 1); var before = project.Frame;
            var binding = project.PrepareOutsideParent(child, -1, parent, 0, cancellationToken: Token);
            var error = Assert.Throws<InvalidOperationException>(() => project.EditWithWorldPreservation(
                edit => edit.OutsideParents.Add(binding), [(child, -1)], Token));
            Assert.Contains("bone 1", error.Message, StringComparison.Ordinal);
            Assert.Same(before, project.Frame); Near(root, World(project, child)); Near(tip, World(project, child, 1));
            Assert.Empty(project.OutsideParents); Assert.False(project.CanUndo);
            Assert.Equal(2, provider.Backends.Count); Assert.Equal(0, provider.Backends[0].DisposeCount); Assert.Equal(1, provider.Backends[1].DisposeCount);
        }
    }

    [Fact]
    public void RejectedWorldPostconditionDoesNotClearExistingRedoOrPublishOtherChanges()
    {
        var (project, child, _, provider) = Create(emptyMotion: true);
        using (project)
        {
            project.Edit(edit => edit.Settings = edit.Settings with { Width = 2560 }, Token);
            Assert.True(project.Undo(Token)); Assert.True(project.CanRedo); Assert.False(project.CanUndo);
            var before = project.Frame;
            Assert.Throws<InvalidOperationException>(() => project.EditWithWorldPreservation(edit =>
            {
                edit.Settings = edit.Settings with { Width = 3840 };
                edit.Composition.SetTransform(child, Vector3.UnitX, Quaternion.Identity, 1);
            }, [(child, 0)], Token));
            Assert.Same(before, project.Frame); Assert.Equal(1920, project.Settings.Width);
            Assert.Equal(Vector3.Zero, project.Objects.Single(item => item.Id == child).Position);
            Assert.False(project.CanUndo); Assert.True(project.CanRedo); Assert.Empty(provider.Backends);
            Assert.True(project.Redo(Token)); Assert.Equal(2560, project.Settings.Width);
        }
    }

    [Fact]
    public void InvalidOrCancelledPreparationLeavesLiveStateAndHistoryUntouched()
    {
        var (project, child, parent, provider) = Create(physicalChild: true);
        using (project)
        {
            project.Advance(1d / 30, Token);
            var before = project.Frame; var world = World(project, child);
            var backend = Assert.Single(provider.Backends);
            var counts = (backend.Steps, backend.Initializations, backend.DisposeCount);
            Assert.Throws<ArgumentException>(() => project.PrepareOutsideParent(child, 1, parent, 0, cancellationToken: Token));
            Assert.Throws<ArgumentException>(() => project.PrepareOutsideParent(child, 0, parent, 1, cancellationToken: Token));
            Assert.Throws<ArgumentException>(() => project.PrepareOutsideParent(child, 0, child, 0, cancellationToken: Token));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => project.PrepareOutsideParent(child, 0, parent, 0, cancellationToken: cancelled.Token));
            Assert.Same(before, project.Frame); Near(world, World(project, child));
            Assert.Empty(project.OutsideParents); Assert.False(project.CanUndo); Assert.False(project.IsFaulted);
            Assert.Single(provider.Backends);
            Assert.Equal(counts, (backend.Steps, backend.Initializations, backend.DisposeCount));
        }
    }

    [Fact]
    public void ExplicitSnapPreparationReturnsIdentityWithoutSamplingOrPublishing()
    {
        var (project, child, parent, provider) = Create(physicalChild: true);
        using (project)
        {
            project.Advance(1d / 30, Token);
            var before = project.Frame;
            var backend = Assert.Single(provider.Backends); int steps = backend.Steps;
            var binding = project.PrepareOutsideParent(child, 0, parent, 0, keepWorld: false, cancellationToken: Token);
            Assert.Equal(Matrix4x4.Identity, binding.Offset);
            Assert.Same(before, project.Frame); Assert.Empty(project.OutsideParents); Assert.False(project.CanUndo);
            Assert.Single(provider.Backends); Assert.Equal(steps, backend.Steps);
        }
    }

    private static (SceneProjectSession Project, Guid Child, Guid Parent, TrackingProvider Provider) Create(
        bool physicalChild = false, bool physicalParent = false, bool emptyMotion = false, bool placed = false, bool secondary = false)
    {
        var plain = EditedMotionProjectTests.Model();
        var model = physicalChild || physicalParent ? WithRigidBody(plain, secondary) : plain;
        var rest = EditedMotionProjectTests.Rest(model);
        var composition = new SceneCompositionEditor();
        var child = composition.Add("child", "prepared-child.pmx", ModelRole.Character, rest);
        var parent = composition.Add("parent", "prepared-parent.pmx", ModelRole.Character, rest);
        composition.SetTransform(parent.Id, new(5, 0, 0), Quaternion.Identity, 1);
        if (placed)
        {
            composition.SetTransform(child.Id, new(8, 2, -1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .4f), 2);
            composition.SetTransform(parent.Id, new(-3, 5, 2), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -.2f), .5f);
        }
        VmdMotion Motion() => emptyMotion ? new() : new()
        { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30 }] };
        var provider = new TrackingProvider(child.Id);
        var project = new SceneProjectSession(composition, new(),
            [new(child.Id, model, Motion(), PhysicsEnabled: physicalChild), new(parent.Id, model, Motion(), PhysicsEnabled: physicalParent)],
            provider, cancellationToken: Token);
        return (project, child.Id, parent.Id, provider);
    }

    private static Matrix4x4 World(SceneProjectSession project, Guid id, int boneIndex = 0)
        => project.CaptureRenderSnapshot([new(id, [boneIndex])]).SceneInputs[id].BoneWorldMatrices[boneIndex];

    private static void Near(Matrix4x4 expected, Matrix4x4 actual)
    {
        var first = new[] { expected.M11, expected.M12, expected.M13, expected.M14, expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34, expected.M41, expected.M42, expected.M43, expected.M44 };
        var second = new[] { actual.M11, actual.M12, actual.M13, actual.M14, actual.M21, actual.M22, actual.M23, actual.M24,
            actual.M31, actual.M32, actual.M33, actual.M34, actual.M41, actual.M42, actual.M43, actual.M44 };
        for (int index = 0; index < first.Length; index++) Assert.InRange(MathF.Abs(first[index] - second[index]), 0, 2e-4f);
    }

    private static PmxModel WithRigidBody(PmxModel model, bool secondary) => new()
    {
        Header = model.Header, Name = model.Name, EnglishName = model.EnglishName, Comment = model.Comment, EnglishComment = model.EnglishComment,
        Vertices = model.Vertices, Faces = model.Faces, Textures = model.Textures, Materials = model.Materials,
        Bones = secondary ? [model.Bones[0], new() { Name = "tip", EnglishName = "", Position = Vector3.UnitY,
            ParentIndex = 0, DeformDepth = 0, Flags = PmxBoneFlags.None }] : model.Bones,
        Morphs = model.Morphs, DisplayFrames = model.DisplayFrames, Joints = model.Joints, SoftBodies = model.SoftBodies,
        RigidBodies = [new() { Name = "body", EnglishName = "", BoneIndex = secondary ? 1 : 0, Group = 0, CollisionGroup = 0,
            Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = secondary ? Vector3.UnitY : Vector3.Zero, Rotation = Vector3.Zero,
            Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic }]
    };

    private sealed class TrackingProvider(Guid childId) : IProjectPhysicsBackendProvider
    {
        public List<TrackingBackend> Backends { get; } = [];
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
        {
            var backend = new TrackingBackend(item.Id == childId ? new(0, -30, 0) : new(0, 15, 0));
            Backends.Add(backend); return backend;
        }
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException("No shared fixture world requested.");
    }

    private sealed class TrackingBackend(Vector3 velocity) : ICharacterPhysicsBackend
    {
        private Matrix4x4[] worlds = [];
        public int Steps, Initializations, DisposeCount;
        public string Name => "read-only attachment preparation fixture";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms)
        { worlds = transforms.ToArray(); Initializations++; }
        public void SetKinematicTarget(int index, Matrix4x4 world, float seconds) => worlds[index] = world;
        public Matrix4x4 GetBodyWorldTransform(int index) => worlds[index];
        public void Step(float seconds)
        { Steps++; for (int index = 0; index < worlds.Length; index++) worlds[index].Translation += velocity * seconds; }
        public void Dispose() => DisposeCount++;
    }
}
