using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterPhysicsAnimationTests
{
    private static BoneTrack Track(string name, Vector3 translation, Quaternion? rotation = null) => new(name,
        [new(0, translation, rotation ?? Quaternion.Identity, BoneCurves.Linear)]);
    private static CharacterAnimator Animator(RigBone[] bones, params BoneTrack[] tracks) => new(new(bones, [], []), new(tracks));
    private static void Near(Vector3 expected, Vector3 actual, float tolerance = .00005f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"Expected {expected}; actual {actual}");

    [Fact]
    public void PhysicsParentPropagatesToUnreflectedChildAndSharedSkinning()
    {
        var rig = new CharacterRig([new("root", Vector3.Zero, -1), new("child", Vector3.UnitX, 0)],
            [new SceneVertex(new(2, 0, 0), Vector3.UnitY, new(.2f, .8f))],
            [new SkinBinding(SkinningKind.Bdef1, 1, -1, -1, -1, new(1, 0, 0, 0))]);
        var animator = new CharacterAnimator(rig, new([]));
        using var evaluation = animator.BeginPhysicsSample(0);
        var physics = evaluation.BeforePhysicsBoneWorldMatrices.ToArray();
        physics[0] = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(5, 6, 0);
        // Unreflected world slots are deliberately stale: only driven bones may be imported.
        var originalInput = (Matrix4x4[])physics.Clone();
        var sample = evaluation.Complete(physics, [0]);
        Near(new(5, 7, 0), sample.BoneWorldMatrices[1].Translation);
        Near(new(5, 8, 0), sample.Vertices[0].Position);
        Near(-Vector3.UnitX, sample.Vertices[0].Normal);
        Assert.Equal(originalInput, physics);
        Assert.Equal(Matrix4x4.Identity, evaluation.BeforePhysicsBoneWorldMatrices[0]);
        Assert.Equal(new Vector2(.2f, .8f), sample.Vertices[0].Uv);
    }

    [Fact]
    public void DrivenParentAndChildKeepTheirIndependentWorldsRegardlessOfIndexOrder()
    {
        // Child has a lower PMX index than its parent; reflected index enumeration is child first.
        var animator = Animator([new("child", Vector3.UnitY, 1), new("root", Vector3.Zero, -1),
            new("tip", new(0, 2, 0), 0)]);
        using var evaluation = animator.BeginPhysicsSample(0);
        var physics = evaluation.BeforePhysicsBoneWorldMatrices.ToArray();
        physics[1] = Matrix4x4.CreateRotationY(.7f) * Matrix4x4.CreateTranslation(8, 3, 4);
        physics[0] = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(2, 5, 1);
        var sample = evaluation.Complete(physics, [0, 1]);
        Near(new(2, 5, 1), sample.BoneWorldMatrices[0].Translation);
        Near(new(1, 5, 1), sample.BoneWorldMatrices[2].Translation);
        Near(Vector3.UnitY, Vector3.TransformNormal(Vector3.UnitX, sample.BoneWorldMatrices[0]));
    }

    [Fact]
    public void AfterPhysicsAppendRunsUnderTheReflectedParent()
    {
        var animator = Animator([new("root", Vector3.Zero, -1), new("source", Vector3.Zero, -1),
            new("grant", Vector3.UnitX, 0, AfterPhysics: true, Append: new(1, .5f, false, false, true))],
            Track("source", new(0, 4, 0)));
        using var evaluation = animator.BeginPhysicsSample(0);
        Near(Vector3.UnitX, evaluation.BeforePhysicsBoneWorldMatrices[2].Translation);
        var physics = evaluation.BeforePhysicsBoneWorldMatrices.ToArray();
        physics[0] = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(5, 6, 0);
        var sample = evaluation.Complete(physics, [0]);
        Near(new(3, 7, 0), sample.BoneWorldMatrices[2].Translation);
    }

    [Fact]
    public void ImportedLocalDoesNotReplaceTheAnimationDeltaUsedByAfterPhysicsAppend()
    {
        var animator = Animator([new("source", Vector3.Zero, -1),
            new("grant", Vector3.Zero, -1, AfterPhysics: true, Append: new(0, .5f, false, false, true))],
            Track("source", new(2, 0, 0)));
        using var evaluation = animator.BeginPhysicsSample(0);
        var physics = evaluation.BeforePhysicsBoneWorldMatrices.ToArray();
        physics[0] = Matrix4x4.CreateTranslation(9, 0, 0);
        var sample = evaluation.Complete(physics, [0]);
        Near(new(9, 0, 0), sample.BoneWorldMatrices[0].Translation);
        Near(Vector3.UnitX, sample.BoneWorldMatrices[1].Translation);
    }

    [Fact]
    public void AfterPhysicsLocalIsRebuiltFromAnimationAsInSaba()
    {
        var animator = Animator([new("late", Vector3.Zero, -1, AfterPhysics: true)], Track("late", new(2, 0, 0)));
        using var evaluation = animator.BeginPhysicsSample(0);
        var sample = evaluation.Complete([Matrix4x4.CreateTranslation(9, 0, 0)], [0]);
        Near(new(2, 0, 0), sample.BoneWorldMatrices[0].Translation);
    }

    private static CharacterAnimator AfterPhysicsIkAnimator() => Animator([
        new("joint", Vector3.Zero, -1), new("tip", Vector3.UnitX, 0),
        new("goal", Vector3.UnitY, 3, AfterPhysics: true, Ik: new(1, 16, MathF.PI, [new(0)])),
        new("physicsParent", Vector3.Zero, -1)]);

    [Fact]
    public void AfterPhysicsIkSolvesAgainstThePhysicsMovedGoal()
    {
        using var evaluation = AfterPhysicsIkAnimator().BeginPhysicsSample(0);
        Near(Vector3.UnitX, evaluation.BeforePhysicsBoneWorldMatrices[1].Translation);
        var physics = evaluation.BeforePhysicsBoneWorldMatrices.ToArray();
        physics[3] = Matrix4x4.CreateTranslation(0, -2, 0);
        var sample = evaluation.Complete(physics, [3]);
        Near(-Vector3.UnitY, sample.BoneWorldMatrices[2].Translation);
        Near(-Vector3.UnitY, sample.BoneWorldMatrices[1].Translation);
    }

    [Fact]
    public void BeforeAndAfterPhasesShareACopiedIkOptionSnapshot()
    {
        var overrides = new Dictionary<int, bool> { [2] = false };
        using var evaluation = AfterPhysicsIkAnimator().BeginPhysicsSample(0, new(IkEnabledOverrides: overrides));
        overrides[2] = true;
        var physics = evaluation.BeforePhysicsBoneWorldMatrices.ToArray();
        physics[3] = Matrix4x4.CreateTranslation(0, -2, 0);
        Near(Vector3.UnitX, evaluation.Complete(physics, [3]).BoneWorldMatrices[1].Translation);
    }

    [Fact]
    public void NoReflectionKeepsTheExistingConstrainedSampleExactly()
    {
        var animator = AfterPhysicsIkAnimator();
        var expected = animator.Sample(0);
        using var evaluation = animator.BeginPhysicsSample(0);
        var input = evaluation.BeforePhysicsBoneWorldMatrices.ToArray();
        var sample = evaluation.Complete(input, []);
        Assert.Equal(expected.BoneWorldMatrices, sample.BoneWorldMatrices);
        Assert.Equal(expected.SkinMatrices, sample.SkinMatrices);
        Assert.Equal(expected.Vertices, sample.Vertices);
        Assert.Equal(expected.BoneWorldMatrices, animator.Sample(0).BoneWorldMatrices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoReflectionMatchesLegacySamplingExactlyForEverySkinningKind(bool constrained)
    {
        var vertex = new SceneVertex(new(4, 7, 9), Vector3.Normalize(new(1, 2, 3)), new(.2f, .8f));
        SkinBinding[] bindings = [new(SkinningKind.Bdef1, 0, -1, -1, -1, new(1, 0, 0, 0)),
            new(SkinningKind.Bdef2, 0, 1, -1, -1, new(.3f, .7f, 0, 0)),
            new(SkinningKind.Bdef4, 0, 1, -1, -1, new(.4f, .6f, 0, 0)),
            new(SkinningKind.Sdef, 0, 1, -1, -1, new(.3f, .7f, 0, 0), new(1, 2, 3), new(2, 4, 6), new(-3, 1, 5))];
        var rig = new CharacterRig([new("root", new(2, -1, 3), -1),
            new("child", new(4, 2, 5), 0, AfterPhysics: true, Append: constrained ? new(0, .3f, false, true, true) : null)],
            Enumerable.Repeat(vertex, 4), bindings);
        var animator = new CharacterAnimator(rig, new([new("root", [
            new(0, Vector3.UnitX, new(.13f, .47f, .63f, .29f), BoneCurves.Linear),
            new(10, new(0, 3, 2), new(.61f, -.31f, .2f, -.7f), BoneCurves.Linear)])]));
        foreach (double frame in new[] { -1, 0, 2.5, 10, 50 })
        {
            var expected = animator.Sample(frame);
            using var evaluation = animator.BeginPhysicsSample(frame);
            var sample = evaluation.Complete(evaluation.BeforePhysicsBoneWorldMatrices, []);
            Assert.Equal(expected.BoneWorldMatrices, sample.BoneWorldMatrices);
            Assert.Equal(expected.SkinMatrices, sample.SkinMatrices);
            Assert.Equal(expected.Vertices, sample.Vertices);
        }
    }

    [Fact]
    public void CallerCopiesAndLaterSamplesCannotModifyAnOwnedEvaluation()
    {
        var animator = Animator([new("root", Vector3.Zero, -1)], Track("root", Vector3.UnitX));
        using var evaluation = animator.BeginPhysicsSample(0);
        var copy = evaluation.BeforePhysicsBoneWorldMatrices.ToArray();
        copy[0] = default;
        animator.Sample(300).BoneWorldMatrices[0] = default;
        Near(Vector3.UnitX, evaluation.BeforePhysicsBoneWorldMatrices[0].Translation);
        Near(Vector3.UnitX, evaluation.Complete(evaluation.BeforePhysicsBoneWorldMatrices, []).BoneWorldMatrices[0].Translation);
        Assert.Throws<InvalidOperationException>(() => evaluation.Complete([Matrix4x4.Identity], []));
    }

    [Fact]
    public void InvalidInputsFailBeforeConsumingTheEvaluation()
    {
        var animator = Animator([new("root", Vector3.Zero, -1)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => animator.BeginPhysicsSample(double.NaN));
        Assert.Throws<ArgumentException>(() => animator.BeginPhysicsSample(0, new(IkEnabledOverrides: new Dictionary<int, bool> { [0] = true })));
        using var evaluation = animator.BeginPhysicsSample(0);
        Assert.Throws<ArgumentException>(() => evaluation.Complete([], []));
        Assert.Throws<ArgumentNullException>(() => evaluation.Complete([Matrix4x4.Identity], null!));
        Assert.Throws<ArgumentException>(() => evaluation.Complete([Matrix4x4.Identity], [-1]));
        Assert.Throws<ArgumentException>(() => evaluation.Complete([Matrix4x4.Identity], [1]));
        Assert.Throws<ArgumentException>(() => evaluation.Complete([Matrix4x4.Identity], [0, 0]));
        Assert.Throws<ArgumentException>(() => evaluation.Complete([default(Matrix4x4)], [0]));
        Assert.Throws<ArgumentException>(() => evaluation.Complete([Matrix4x4.CreateScale(2)], [0]));
        var nan = Matrix4x4.Identity; nan.M41 = float.NaN;
        Assert.Throws<ArgumentException>(() => evaluation.Complete([nan], [0]));
        Assert.Equal(Matrix4x4.Identity, evaluation.Complete([Matrix4x4.Identity], []).BoneWorldMatrices[0]);
    }

    [Fact]
    public void DisposingAnUnfinishedSampleDoesNotAffectTheAnimator()
    {
        var animator = Animator([new("root", Vector3.Zero, -1)]);
        var evaluation = animator.BeginPhysicsSample(0);
        evaluation.Dispose(); evaluation.Dispose();
        Assert.Throws<ObjectDisposedException>(() => evaluation.Complete([Matrix4x4.Identity], []));
        Assert.Equal(Matrix4x4.Identity, animator.Sample(0).BoneWorldMatrices[0]);
    }
}
