using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class DeferredPhysicsSkinningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StatelessBoneSamplingRetainsTheOrdinaryFastAndConstrainedArithmetic(bool constrained)
    {
        const int vertices = 20000;
        RigBone[] bones = [new("root", Vector3.Zero, -1),
            new("child", Vector3.UnitY, 0, AfterPhysics: constrained,
                Append: constrained ? new(0, .3f, false, true, true) : null)];
        var rig = new CharacterRig(bones,
            Enumerable.Repeat(new SceneVertex(new(1, 2, 3), Vector3.UnitY, new(.2f, .8f)), vertices),
            Enumerable.Repeat(new SkinBinding(SkinningKind.Bdef2, 0, 1, -1, -1, new(.3f, .7f, 0, 0)), vertices));
        var animator = new CharacterAnimator(rig, new([]));
        var morphs = new MorphPose(Enumerable.Repeat(new Vector3(.2f, .3f, 0), vertices),
            [Vector3.Zero, new(.1f, 0, 0)], [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, .2f)]);
        for (int index = 0; index < 3; index++)
        {
            var options = new CharacterEvaluationOptions(EnableIk: index % 2 == 0, Morphs: morphs,
                OutsideParents: index == 0 ? null : new Dictionary<int, Matrix4x4> { [-1] = Matrix4x4.CreateTranslation(index, 0, 0) },
                LocalBoneOverrides: new Dictionary<int, BoneLocalTransform> { [0] = new(new(.1f * index, 0, 0), Quaternion.Identity) });
            var expected = animator.Sample(index, options);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var worlds = animator.SampleBoneWorldMatrices(index, options);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(expected.BoneWorldMatrices, worlds);
            Assert.True(allocated < 128 * 1024, $"Stateless bone sampling allocated vertex-sized output: {allocated} bytes.");
            var skin = animator.SkinWorldPose(worlds, morphs);
            Assert.Equal(expected.SkinMatrices, skin.SkinMatrices); Assert.Equal(expected.Vertices, skin.Vertices);
            var retained = (Matrix4x4[])worlds.Clone(); animator.Sample(100);
            Assert.Equal(retained, worlds);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => animator.SampleBoneWorldMatrices(double.NaN));
    }

    [Fact]
    public void IntermediateBoneCompletionAvoidsThePerVertexAllocation()
    {
        const int vertexCount = 20000;
        var rig = new CharacterRig([new RigBone("root", Vector3.Zero, -1)],
            Enumerable.Repeat(new SceneVertex(Vector3.UnitX, Vector3.UnitY, Vector2.Zero), vertexCount),
            Enumerable.Repeat(new SkinBinding(SkinningKind.Bdef1, 0, -1, -1, -1, new(1, 0, 0, 0)), vertexCount));
        var animator = new CharacterAnimator(rig, new([]));
        using (var warm = animator.BeginPhysicsSample(0)) warm.Complete([Matrix4x4.Identity], [0]);
        using (var warm = animator.BeginPhysicsSample(0)) warm.CompleteBoneWorldMatrices([Matrix4x4.Identity], [0]);
        Matrix4x4[] input = [Matrix4x4.Identity]; int[] driven = [0];
        using var full = animator.BeginPhysicsSample(0);
        long start = GC.GetAllocatedBytesForCurrentThread();
        var expected = full.Complete(input, driven);
        long fullBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        using var deferred = animator.BeginPhysicsSample(0);
        start = GC.GetAllocatedBytesForCurrentThread();
        var worlds = deferred.CompleteBoneWorldMatrices(input, driven);
        long deferredBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(expected.BoneWorldMatrices, worlds);
        Assert.True(fullBytes - deferredBytes >= vertexCount * 24L,
            $"Full completion allocated {fullBytes} bytes; bone completion allocated {deferredBytes} bytes.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoneOnlyCompletionRetainsAfterPhysicsIkAppendParentsAndEverySkinningKind(bool enableIk)
    {
        RigBone[] bones = [new("link", Vector3.Zero, -1), new("tip", Vector3.UnitX, 0),
            new("goal", Vector3.UnitY, 3, AfterPhysics: true, Ik: new(1, 16, MathF.PI, [new(0)])),
            new("physical", Vector3.Zero, -1),
            new("grant", new(2, 0, 0), 3, AfterPhysics: true, Append: new(0, .3f, false, true, true))];
        SkinBinding[] bindings = [new(SkinningKind.Bdef1, 1, -1, -1, -1, new(1, 0, 0, 0)),
            new(SkinningKind.Bdef2, 0, 4, -1, -1, new(.3f, .7f, 0, 0)),
            new(SkinningKind.Bdef4, 0, 1, 3, 4, new(.1f, .2f, .3f, .4f)),
            new(SkinningKind.Sdef, 0, 4, -1, -1, new(.3f, .7f, 0, 0), new(1, 2, 3), new(2, 4, 6), new(-3, 1, 5)),
            new(SkinningKind.Qdef, 0, 1, 3, 4, new(.1f, .2f, .3f, .4f))];
        var rig = new CharacterRig(bones,
            Enumerable.Repeat(new SceneVertex(new(4, 7, 9), Vector3.Normalize(new(1, 2, 3)), new(.2f, .8f)), bindings.Length), bindings);
        var animator = new CharacterAnimator(rig, new([]));
        var morphs = new MorphPose(Enumerable.Repeat(new Vector3(.2f, .3f, -.1f), bindings.Length),
            [Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.UnitY],
            Enumerable.Repeat(Quaternion.Identity, bones.Length), Enumerable.Repeat(new Vector2(.1f, .2f), bindings.Length));
        var options = new CharacterEvaluationOptions(EnableIk: enableIk, Morphs: morphs,
            OutsideParents: new Dictionary<int, Matrix4x4> { [3] = Matrix4x4.CreateTranslation(0, -2, 0) },
            PhysicsDrivenBoneIndices: [3]);
        for (int step = 0; step < 12; step++)
        {
            using var full = animator.BeginPhysicsSample(0, options);
            using var deferred = animator.BeginPhysicsSample(0, options);
            var input = full.BeforePhysicsBoneWorldMatrices.ToArray();
            input[3] = Matrix4x4.CreateRotationZ(.01f * step) * Matrix4x4.CreateTranslation(.02f * step, -2, 0);
            var original = (Matrix4x4[])input.Clone();
            var expected = full.Complete(input, [3]);
            var worlds = deferred.CompleteBoneWorldMatrices(input, [3]);
            Assert.Equal(expected.BoneWorldMatrices, worlds);
            var actual = animator.SkinWorldPose(worlds, morphs);
            Assert.Equal(expected.SkinMatrices, actual.SkinMatrices);
            Assert.Equal(expected.Vertices, actual.Vertices);
            var trusted = animator.SkinCompletedPhysicsWorlds(worlds, morphs);
            Assert.Equal(expected.BoneWorldMatrices, trusted.BoneWorldMatrices);
            Assert.Equal(expected.SkinMatrices, trusted.SkinMatrices);
            Assert.Equal(expected.Vertices, trusted.Vertices);
            Assert.Equal(original, input);
            Assert.Throws<InvalidOperationException>(() => deferred.Complete(input, [3]));
            Assert.Throws<InvalidOperationException>(() => deferred.CompleteBoneWorldMatrices(input, [3]));
            var snapshot = (Matrix4x4[])worlds.Clone();
            animator.Sample(100);
            Assert.Equal(snapshot, worlds);
        }
    }

    [Fact]
    public void TrustedCompletedPhysicsSkinningDoesNotAddPublicPaletteValidation()
    {
        var rig = new CharacterRig([new RigBone("root", Vector3.Zero, -1)],
            [new SceneVertex(Vector3.UnitX, Vector3.UnitY, Vector2.Zero)],
            [new SkinBinding(SkinningKind.Bdef1, 0, -1, -1, -1, new(1, 0, 0, 0))]);
        var animator = new CharacterAnimator(rig, new([]));
        Matrix4x4[] worlds = [Matrix4x4.CreateScale(0)];
        Assert.Throws<ArgumentException>(() => animator.SkinWorldPose(worlds));
        var result = animator.SkinCompletedPhysicsWorlds(worlds, null);
        Assert.Equal(worlds, result.BoneWorldMatrices);
        Assert.Equal(rig.InverseBindMatrices[0] * worlds[0], result.SkinMatrices[0]);
    }

    [Fact]
    public void TrustedCompletionMatchesFullCompletionWhenAfterPhysicsGrantOverflows()
    {
        RigBone[] bones = [new("physical", Vector3.Zero, -1),
            new("grant", Vector3.Zero, -1, AfterPhysics: true,
                Append: new(0, 1000, false, false, true))];
        var rig = new CharacterRig(bones, [new SceneVertex(Vector3.UnitX, Vector3.UnitY, Vector2.Zero)],
            [new SkinBinding(SkinningKind.Bdef1, 1, -1, -1, -1, new(1, 0, 0, 0))],
            evaluationSchedule: BoneEvaluationSchedule.Mmd);
        var animator = new CharacterAnimator(rig, new([]));
        var options = new CharacterEvaluationOptions(PhysicsDrivenBoneIndices: [0]);
        using var full = animator.BeginPhysicsSample(0, options);
        using var deferred = animator.BeginPhysicsSample(0, options);
        var worlds = full.BeforePhysicsBoneWorldMatrices.ToArray();
        worlds[0] = Matrix4x4.CreateTranslation(1e38f, 0, 0);
        var expected = full.Complete(worlds, [0]);
        var solved = deferred.CompleteBoneWorldMatrices(worlds, [0]);
        Assert.False(float.IsFinite(solved[1].M41));
        Assert.Throws<ArgumentException>(() => animator.SkinWorldPose(solved));
        var actual = animator.SkinCompletedPhysicsWorlds(solved, null);
        Assert.Equal(expected.BoneWorldMatrices, actual.BoneWorldMatrices);
        Assert.Equal(expected.SkinMatrices, actual.SkinMatrices);
        Assert.Equal(expected.Vertices, actual.Vertices);
    }

    [Fact]
    public void BoneOnlyCompletionSharesValidationAndConsumptionWithFullCompletion()
    {
        var animator = new CharacterAnimator(new([new RigBone("root", Vector3.Zero, -1)], [], []), new([]));
        using var sample = animator.BeginPhysicsSample(0);
        Assert.Throws<ArgumentException>(() => sample.CompleteBoneWorldMatrices([], []));
        Assert.Throws<ArgumentNullException>(() => sample.CompleteBoneWorldMatrices([Matrix4x4.Identity], null!));
        Assert.Throws<ArgumentException>(() => sample.CompleteBoneWorldMatrices([Matrix4x4.Identity], [-1]));
        Assert.Throws<ArgumentException>(() => sample.CompleteBoneWorldMatrices([Matrix4x4.Identity], [0, 0]));
        Assert.Throws<ArgumentException>(() => sample.CompleteBoneWorldMatrices([Matrix4x4.CreateScale(2)], [0]));
        Assert.Equal(Matrix4x4.Identity, Assert.Single(sample.CompleteBoneWorldMatrices([Matrix4x4.Identity], [0])));
        Assert.Throws<InvalidOperationException>(() => sample.Complete([Matrix4x4.Identity], [0]));
        using var full = animator.BeginPhysicsSample(0);
        full.Complete([Matrix4x4.Identity], [0]);
        Assert.Throws<InvalidOperationException>(() => full.CompleteBoneWorldMatrices([Matrix4x4.Identity], [0]));
        var disposed = animator.BeginPhysicsSample(0); disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => disposed.CompleteBoneWorldMatrices([Matrix4x4.Identity], []));
    }
}
