using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class OutsideParentAnimationTests
{
    private static readonly Matrix4x4 Parent = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 20, 30);

    [Fact] public void SingleBoneBindingReplacesOrdinaryParentAndRestOffsetThenChildrenInherit()
    {
        var animator = Chain();
        var sample = animator.Sample(0, new(OutsideParents: new Dictionary<int, Matrix4x4> { [1] = Parent }));
        Near(new(8, 0, 0), sample.BoneWorldMatrices[0].Translation);
        Near(new(10, 22, 30), sample.BoneWorldMatrices[1].Translation);
        Near(new(9, 22, 30), sample.BoneWorldMatrices[2].Translation);
        Near(-Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitX, sample.BoneWorldMatrices[1]));
        Near(sample.BoneWorldMatrices[2].Translation, sample.Vertices[0].Position);
    }

    [Fact] public void WholeModelBindingPreservesRestPositionsAndSingleBoneBindingOverridesIt()
    {
        var animator = Chain();
        var options = new CharacterEvaluationOptions(OutsideParents: new Dictionary<int, Matrix4x4> { [-1] = Parent });
        var sample = animator.Sample(0, options);
        Near(new(10, 28, 30), sample.BoneWorldMatrices[0].Translation);
        Near(new(10, 31, 30), sample.BoneWorldMatrices[1].Translation);
        var both = animator.Sample(0, options with { OutsideParents = new Dictionary<int, Matrix4x4> { [-1] = Parent, [1] = Matrix4x4.Identity } });
        Near(new(2, 0, 0), both.BoneWorldMatrices[1].Translation);
        Near(new(2, 1, 0), both.BoneWorldMatrices[2].Translation);
    }

    [Fact] public void WholeModelBindingUsesTheSameWorldSpaceInsideIk()
    {
        RigBone[] bones = [new("link", Vector3.Zero, -1), new("tip", Vector3.UnitX, 0),
            new("goal", Vector3.UnitY, -1, Ik: new(1, 4, MathF.PI, [new(0)]))];
        var animator = new CharacterAnimator(new(bones, [], []), new([]));
        var sample = animator.Sample(0, new(OutsideParents: new Dictionary<int, Matrix4x4> { [-1] = Parent }));
        Near(Vector3.Transform(Vector3.UnitY, Parent), sample.BoneWorldMatrices[1].Translation);
        Near(sample.BoneWorldMatrices[2].Translation, sample.BoneWorldMatrices[1].Translation);
    }

    [Fact] public void PhysicsReflectionRemovesExternalParentBeforeRecomposingAndSnapshotsInputs()
    {
        var parents = new Dictionary<int, Matrix4x4> { [1] = Parent };
        using var evaluation = Chain().BeginPhysicsSample(0, new(OutsideParents: parents));
        Near(new(10, 22, 30), evaluation.BeforePhysicsBoneWorldMatrices[1].Translation);
        parents[1] = Matrix4x4.Identity;
        var worlds = evaluation.BeforePhysicsBoneWorldMatrices.ToArray();
        worlds[1].Translation = new(50, 60, 70);
        var result = evaluation.Complete(worlds, [1]);
        Near(new(50, 60, 70), result.BoneWorldMatrices[1].Translation);
        Near(new(49, 60, 70), result.BoneWorldMatrices[2].Translation);
    }

    [Fact] public void WholeModelPhysicsReflectionDoesNotApplyTheParentTwice()
    {
        using var evaluation = Chain().BeginPhysicsSample(0, new(OutsideParents: new Dictionary<int, Matrix4x4> { [-1] = Parent }));
        var worlds = evaluation.BeforePhysicsBoneWorldMatrices.ToArray();
        worlds[0].Translation = new(50, 60, 70);
        var result = evaluation.Complete(worlds, [0]);
        Near(new(50, 60, 70), result.BoneWorldMatrices[0].Translation);
        Near(new(50, 63, 70), result.BoneWorldMatrices[1].Translation);
    }

    [Fact] public void EmptyAndClearedBindingsRestoreExactDefaultAndRejectInvalidInputs()
    {
        var animator = Chain(); var baseline = animator.Sample(0);
        Assert.Equal(baseline.BoneWorldMatrices, animator.Sample(0, new(OutsideParents: new Dictionary<int, Matrix4x4>())).BoneWorldMatrices);
        animator.Sample(0, new(OutsideParents: new Dictionary<int, Matrix4x4> { [1] = Parent }));
        Assert.Equal(baseline.BoneWorldMatrices, animator.Sample(0).BoneWorldMatrices);
        foreach (int index in new[] { -2, 3 })
            Assert.Throws<ArgumentException>(() => animator.Sample(0, new(OutsideParents: new Dictionary<int, Matrix4x4> { [index] = Parent })));
        Assert.Throws<ArgumentException>(() => animator.Sample(0, new(OutsideParents: new Dictionary<int, Matrix4x4> { [0] = Matrix4x4.CreateTranslation(float.NaN, 0, 0) })));
    }

    [Fact] public void ControllerCopiesParentsInvalidatesSameFrameCacheAndRetainsReadOnlyLastWorlds()
    {
        var model = MorphApplicationTests.Model([]);
        using var controller = MorphApplicationTests.Controller(model, new() { BoneKeyframes = [new() { BoneName = "root", Frame = 0,
            Translation = Vector3.Zero, Rotation = Quaternion.Identity, Interpolation = new byte[64] }] });
        Assert.Empty(controller.BoneWorldMatrices);
        var baseline = controller.Sample(0); var originalWorlds = controller.BoneWorldMatrices;
        var parents = new Dictionary<int, Matrix4x4> { [-1] = Parent };
        controller.SetOutsideParents(parents); parents[-1] = Matrix4x4.Identity;
        var bound = controller.Sample(0);
        Assert.NotSame(baseline.Surface, bound.Surface);
        Near(Parent.Translation, controller.BoneWorldMatrices[0].Translation);
        Near(Vector3.Zero, originalWorlds[0].Translation);
        Assert.Throws<NotSupportedException>(() => ((IList<Matrix4x4>)controller.BoneWorldMatrices)[0] = Matrix4x4.Identity);
        Assert.Same(bound.Surface, controller.Sample(0).Surface);
        controller.SetOutsideParents(null);
        Assert.Equal(baseline.Surface.Snapshot.Vertices.ToArray(), controller.Sample(0).Surface.Snapshot.Vertices.ToArray());
        Near(Vector3.Zero, controller.BoneWorldMatrices[0].Translation);
    }

    private static CharacterAnimator Chain()
    {
        RigBone[] bones = [new("root", new(5, 0, 0), -1), new("bound", new(6, 0, 0), 0), new("child", new(7, 0, 0), 1)];
        var rig = new CharacterRig(bones, [new(new(7, 0, 0), Vector3.UnitY, Vector2.Zero)],
            [new(SkinningKind.Bdef1, 2, -1, -1, -1, new(1, 0, 0, 0))]);
        var clip = new MotionClip([new("root", [new(0, new(3, 0, 0), Quaternion.Identity, BoneCurves.Linear)]),
            new("bound", [new(0, new(2, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), BoneCurves.Linear)])]);
        return new(rig, clip);
    }
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, 1e-4f);
}
