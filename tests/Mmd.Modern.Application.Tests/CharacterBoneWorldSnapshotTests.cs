using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class CharacterBoneWorldSnapshotTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void OrdinarySamplingNeverMutatesAnEarlierBorrowedControllerPalette()
    {
        var model = SharedPhysicsFixture.Model(physics: false, precedingBone: true);
        var rest = SharedPhysicsFixture.Rest(model);
        var motion = new VmdMotion { BoneKeyframes =
            [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 10,
                Translation = new(8, 3, -2), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .6f) }] };
        using var controller = new CharacterAnimationController(model, motion, rest.Surface, rest.Outline!);
        var borrowed = new List<BorrowedPalette>();

        foreach (double frame in new[] { 0d, 3.5d, 10d, 2d })
        {
            _ = controller.SampleAt(frame);
            if (borrowed.Count != 0)
            {
                Assert.NotSame(borrowed[^1].Matrices, controller.BoneWorldMatrices);
                Assert.NotEqual(borrowed[^1].Matrices[1].Translation, controller.BoneWorldMatrices[1].Translation);
            }
            borrowed.Add(Borrow(controller));
            AssertUnchanged(borrowed);
        }
    }

    [Fact]
    public void SharedFrozenAttachmentReprojectionNeverMutatesBorrowedParentOrChildPalettes()
    {
        var model = SharedPhysicsFixture.Model(PmxRigidBodyOperation.Static);
        var layout = new SceneCompositionEditor();
        // Deliberately put the frozen physical child before its nonphysical parent in source order.
        var child = layout.Add("child", Path.GetFullPath("borrowed-palette-child.pmx"), ModelRole.Character, SharedPhysicsFixture.Rest(model));
        var parent = layout.Add("parent", Path.GetFullPath("borrowed-palette-parent.pmx"), ModelRole.Character, SharedPhysicsFixture.Rest(model));
        SceneActorMotion[] motions =
            [new(child.Id, model, SharedPhysicsFixture.Motion(1)),
             new(parent.Id, model, SharedPhysicsFixture.Motion(1), PhysicsEnabled: false)];
        var sourceFactory = new Factory();
        using var source = new SceneProjectSession(layout, new(), motions, sourceFactory.Provider(),
            new() { Physics = new() { SharedWorld = true, GroundEnabled = false } },
            outsideParents: [new(child.Id, -1, parent.Id, 0, Matrix4x4.Identity)], initialFrame: 7, cancellationToken: Token);
        var workerFactory = new Factory();
        using var owner = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>())
            .CreateSession(workerFactory.Provider(), Token);
        var childController = Controller(owner, child.Id);
        var parentController = Controller(owner, parent.Id);
        var borrowed = new List<BorrowedPalette> { Borrow(childController), Borrow(parentController) };
        var world = Assert.Single(workerFactory.Worlds);
        int initializations = world.Initializations;

        foreach (var gesture in new[] { (X: 8f, Angle: .15f, Steps: 8),
            (X: 17f, Angle: -.2f, Steps: 0), (X: -4f, Angle: .4f, Steps: 2) })
        {
            var oldChild = childController.BoneWorldMatrices;
            var oldParent = parentController.BoneWorldMatrices;
            owner.PreviewInteractiveBones(parent.Id, new Dictionary<int, BoneLocalTransform>
                { [0] = new(new(gesture.X, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, gesture.Angle)) },
                gesture.Steps * CharacterPhysicsSession.FixedStepSeconds, Token);

            // Observe the real controllers: the public render-input/bone queries already copy arrays.
            Assert.Same(childController, Controller(owner, child.Id));
            Assert.Same(parentController, Controller(owner, parent.Id));
            Assert.NotSame(oldChild, childController.BoneWorldMatrices);
            Assert.NotSame(oldParent, parentController.BoneWorldMatrices);
            Assert.Equal(new Vector3(gesture.X, 0, 0), childController.BoneWorldMatrices[0].Translation);
            Assert.Equal(Bytes(parentController.BoneWorldMatrices), Bytes(childController.BoneWorldMatrices));
            borrowed.Add(Borrow(childController)); borrowed.Add(Borrow(parentController));
            AssertUnchanged(borrowed);
            Assert.Equal(7, owner.CurrentFrame); Assert.All(owner.Actors, actor => Assert.Equal(1, actor.LocalFrame));
            Assert.Equal(initializations, world.Initializations); Assert.True(Assert.Single(world.Bodies).Kinematic);
        }
    }

    private sealed record BorrowedPalette(IReadOnlyList<Matrix4x4> Matrices, byte[] Expected);
    private static BorrowedPalette Borrow(CharacterAnimationController controller)
    {
        var matrices = controller.BoneWorldMatrices;
        return new(matrices, Bytes(matrices));
    }
    private static void AssertUnchanged(IEnumerable<BorrowedPalette> borrowed)
    {
        foreach (var palette in borrowed) Assert.Equal(palette.Expected, Bytes(palette.Matrices));
    }
    private static byte[] Bytes(IReadOnlyList<Matrix4x4> matrices)
        => MemoryMarshal.AsBytes(matrices.ToArray().AsSpan()).ToArray();

    private static CharacterAnimationController Controller(SceneProjectSession owner, Guid objectId)
    {
        var animation = (SceneAnimationSession)typeof(SceneProjectSession)
            .GetField("animation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        var entries = (Array)typeof(SceneAnimationSession)
            .GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(animation)!;
        foreach (object entry in entries)
        {
            var type = entry.GetType();
            var actor = (EditableSceneObject)type.GetProperty("Object")!.GetValue(entry)!;
            if (actor.Id == objectId)
                return (CharacterAnimationController)type.GetProperty("Controller")!.GetValue(entry)!;
        }
        throw new InvalidOperationException("The requested actor has no animation controller.");
    }

    private sealed class Factory
    {
        internal readonly List<TrackingSharedBackend> Worlds = [];
        internal ProjectSettingsBackendProvider Provider() => new((_, _) => Create(), _ => Create(), poseInitializationSubsteps: 0);
        private TrackingSharedBackend Create() { var world = new TrackingSharedBackend(); Worlds.Add(world); return world; }
    }
}
