using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterParallelSkinningTests
{
    private static readonly float NegativeZero = BitConverter.Int32BitsToSingle(int.MinValue);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactAndMixedSkinningMatchOneWorkerByteForByteWithEveryMorphChannel(bool mixed)
    {
        var rig = Rig(32769, mixed);
        Assert.Equal(!mixed, rig.HasCompactBdefBindings);
        var serial = new CharacterAnimator(rig, new([]), 1);
        var parallel = new CharacterAnimator(rig, new([]), 4);
        var worlds = Worlds(rigid: mixed);
        var inputBytes = Bytes(worlds);
        foreach (int mode in new[] { 0, 1, 2, 3, 4 })
        {
            var morphs = Pose(rig, mode);
            Exact(serial.SkinWorldPose(worlds, morphs), parallel.SkinWorldPose(worlds, morphs));
            Assert.Equal(inputBytes, Bytes(worlds));
        }
    }

    [Fact]
    public void LargeShortTailAndSourceNormalFallbackKeepSignedZeroBits()
    {
        var rig = Rig(65539, mixed: false);
        Matrix4x4[] worlds = [Matrix4x4.Identity, Matrix4x4.CreateScale(-1),
            Matrix4x4.Identity, Matrix4x4.Identity];
        var expected = new CharacterAnimator(rig, new([]), 1).SkinWorldPose(worlds, Pose(rig, 4));
        var actual = new CharacterAnimator(rig, new([]), 16).SkinWorldPose(worlds, Pose(rig, 4));
        Exact(expected, actual);
        // The BDEF2 template has equal opposing influences, so its source-normal
        // and zero fallbacks are both exercised across many block boundaries.
        Assert.Equal(Vector3.Normalize(rig.Vertices[1].Normal), actual.Vertices[1].Normal);
        Assert.Equal(Vector3.Zero, actual.Vertices[7].Normal);
    }

    [Theory]
    [InlineData(32767, false)]
    [InlineData(32768, true)]
    public void ThresholdAndChunksCoverEachVertexOnceIncludingShortTail(int count, bool eligible)
    {
        var rig = Rig(count, mixed: true);
        var ranges = new ConcurrentBag<(int Worker, int Start, int End)>();
        var animator = new CharacterAnimator(rig, new([]), 4,
            (worker, start, end) => ranges.Add((worker, start, end)));
        animator.SkinWorldPose(Worlds(rigid: true));
        int degree = eligible ? Math.Min(4, Math.Max(1, Environment.ProcessorCount)) : 1;
        var ordered = ranges.OrderBy(range => range.Start).ToArray();
        if (degree == 1)
        {
            Assert.Equal(new[] { (0, 0, count) }, ordered);
            return;
        }
        Assert.Equal((count + 4095) / 4096, ordered.Length);
        Assert.Equal(Enumerable.Range(0, degree), ordered.Select(range => range.Worker).Distinct().Order());
        for (int block = 0; block < ordered.Length; block++)
        {
            Assert.Equal(block * 4096, ordered[block].Start);
            Assert.Equal(Math.Min(count, (block + 1) * 4096), ordered[block].End);
            Assert.Equal(block % degree, ordered[block].Worker);
        }
    }

    [Fact]
    public void IndependentWorkersActuallyOverlapAndReuseTheirOwnQdefCaches()
    {
        if (Environment.ProcessorCount < 2) return;
        var rig = Rig(32769, mixed: true);
        using var firstRanges = new Barrier(2);
        var threads = new ConcurrentDictionary<int, int>();
        var ranges = new ConcurrentBag<(int Worker, int Start)>();
        var animator = new CharacterAnimator(rig, new([]), 2, (worker, start, _) =>
        {
            ranges.Add((worker, start));
            if (start >= 8192) return;
            threads[worker] = Environment.CurrentManagedThreadId;
            Assert.True(firstRanges.SignalAndWait(TimeSpan.FromSeconds(10)), "Skinning workers did not overlap.");
        });
        Exact(new CharacterAnimator(rig, new([]), 1).SkinWorldPose(Worlds(rigid: true), Pose(rig, 4)),
            animator.SkinWorldPose(Worlds(rigid: true), Pose(rig, 4)));
        Assert.Equal(2, threads.Values.Distinct().Count());
        Assert.Equal(9, ranges.Count);
        Assert.All(ranges.GroupBy(range => range.Worker), worker => Assert.True(worker.Count() >= 4));
    }

    [Fact]
    public void SamplePhysicsCompletionAndLaterSeeksPreserveEveryOwnedArray()
    {
        var rig = Rig(32769, mixed: true);
        var clip = new MotionClip([new("a", [new(0, Vector3.Zero, Quaternion.Identity, BoneCurves.Linear),
            new(30, new(3, -2, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .7f), BoneCurves.Linear)])]);
        var serial = new CharacterAnimator(rig, clip, 1);
        var parallel = new CharacterAnimator(rig, clip, 4);
        var options = new CharacterEvaluationOptions(Morphs: Pose(rig, 4));
        var retained = parallel.Sample(7.5, options);
        var vertices = Bytes(retained.Vertices); var worlds = Bytes(retained.BoneWorldMatrices); var skin = Bytes(retained.SkinMatrices);
        foreach (double frame in new[] { 0, 7.5, 100, -1, 7.5 })
        {
            Exact(serial.Sample(frame, options), parallel.Sample(frame, options));
            using var one = serial.BeginPhysicsSample(frame, options);
            using var many = parallel.BeginPhysicsSample(frame, options);
            Exact(one.Complete(one.BeforePhysicsBoneWorldMatrices, []), many.Complete(many.BeforePhysicsBoneWorldMatrices, []));
        }
        Assert.Equal(vertices, Bytes(retained.Vertices));
        Assert.Equal(worlds, Bytes(retained.BoneWorldMatrices));
        Assert.Equal(skin, Bytes(retained.SkinMatrices));
    }

    [Fact]
    public async Task ConcurrentSamplesOnOneAnimatorDoNotSharePalettesOrOutputs()
    {
        var rig = Rig(32769, mixed: true);
        var animator = new CharacterAnimator(rig, new([]), 4);
        var morphs = Pose(rig, 4);
        var worlds = Worlds(rigid: true);
        var expected = new CharacterAnimator(rig, new([]), 1).SkinWorldPose(worlds, morphs);
        var samples = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => animator.SkinWorldPose(worlds, morphs))));
        for (int i = 0; i < samples.Length; i++)
        {
            Exact(expected, samples[i]);
            for (int j = 0; j < i; j++)
            {
                Assert.NotSame(samples[i].Vertices, samples[j].Vertices);
                Assert.NotSame(samples[i].BoneWorldMatrices, samples[j].BoneWorldMatrices);
                Assert.NotSame(samples[i].SkinMatrices, samples[j].SkinMatrices);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QdefFailuresKeepFirstVertexExceptionAfterAllWorkersJoin(bool firstOverflows)
    {
        const int count = 32769;
        var vertices = Enumerable.Repeat(new SceneVertex(Vector3.UnitX, Vector3.UnitY, Vector2.Zero), count).ToArray();
        var bindings = Enumerable.Repeat(new SkinBinding(SkinningKind.Bdef1, 0, -1, -1, -1, new(1, 0, 0, 0)), count).ToArray();
        bindings[17] = new(SkinningKind.Qdef, firstOverflows ? 0 : 2, -1, -1, -1, new(1, 0, 0, 0));
        bindings[4097] = new(SkinningKind.Qdef, 1, -1, -1, -1, new(1, 0, 0, 0));
        if (firstOverflows) vertices[17] = vertices[17] with { Position = new(float.MaxValue, 0, 0) };
        var rig = new CharacterRig(Bones(), vertices, bindings);
        Matrix4x4[] worlds = [Matrix4x4.CreateTranslation(firstOverflows ? float.MaxValue : 0, 0, 0),
            Matrix4x4.CreateScale(2, 1, 1), Matrix4x4.CreateScale(3, 1, 1), Matrix4x4.Identity];
        var seen = new ConcurrentBag<int>();
        var serial = new CharacterAnimator(rig, new([]), 1);
        var parallel = new CharacterAnimator(rig, new([]), 4, (_, start, _) => seen.Add(start));
        var expected = Assert.ThrowsAny<Exception>(() => serial.SkinWorldPose(worlds));
        var actual = Assert.ThrowsAny<Exception>(() => parallel.SkinWorldPose(worlds));
        if (firstOverflows) Assert.IsType<InvalidOperationException>(expected);
        else { Assert.IsType<NotSupportedException>(expected); Assert.Contains("bone 2", expected.Message); }
        Assert.Equal(expected.GetType(), actual.GetType()); Assert.Equal(expected.Message, actual.Message);
        Assert.IsNotType<AggregateException>(actual);
        Assert.Equal(Environment.ProcessorCount > 1 ? 9 : 1, seen.Count);
    }

    [Fact]
    public void QdefCachePreparationStaysLazyForUnusedNonRigidBone()
    {
        const int count = 32769;
        var rig = new CharacterRig(Bones(),
            Enumerable.Repeat(new SceneVertex(new(1, 2, 3), Vector3.UnitY, Vector2.Zero), count),
            Enumerable.Repeat(new SkinBinding(SkinningKind.Qdef, -1, 0, -1, -1, new(NegativeZero, 1, 0, NegativeZero)), count));
        var worlds = Worlds(rigid: true); worlds[3] = Matrix4x4.CreateScale(2, 1, 1);
        Exact(new CharacterAnimator(rig, new([]), 1).SkinWorldPose(worlds), new CharacterAnimator(rig, new([]), 4).SkinWorldPose(worlds));
    }

    [Theory]
    [InlineData(32768, 3)]
    [InlineData(65536, 1)]
    public void SparseQdefOnManyBonesBoundsWorkerPalettesAndFallsBackToSerial(int boneCount, int budgetDegree)
    {
        var template = Rig(32769, mixed: false);
        var bones = Bones().Concat(Enumerable.Range(4, boneCount - 4)
            .Select(i => new RigBone("unused-" + i, Vector3.Zero, -1))).ToArray();
        var bindings = template.Bindings.ToArray();
        bindings[17] = new(SkinningKind.Qdef, 0, -1, -1, -1, new(1, 0, 0, 0));
        var rig = new CharacterRig(bones, template.Vertices, bindings);
        var worlds = Enumerable.Repeat(Matrix4x4.Identity, boneCount).ToArray();
        var ranges = new ConcurrentBag<(int Worker, int Start)>();
        var actual = new CharacterAnimator(rig, new([]), 4, (worker, start, _) => ranges.Add((worker, start)))
            .SkinWorldPose(worlds);
        Exact(new CharacterAnimator(rig, new([]), 1).SkinWorldPose(worlds), actual);
        int degree = Math.Min(budgetDegree, Math.Min(4, Math.Max(1, Environment.ProcessorCount)));
        Assert.Equal(Enumerable.Range(0, degree), ranges.Select(range => range.Worker).Distinct().Order());
        Assert.Equal(degree == 1 ? 1 : 9, ranges.Count);
    }

    private static CharacterRig Rig(int count, bool mixed)
    {
        SkinBinding[] templates = [new(SkinningKind.Bdef1, 0, -1, -1, -1, new(1, NegativeZero, 0, NegativeZero)),
            new(SkinningKind.Bdef2, 0, 1, -1, -1, new(.5f, .5f, NegativeZero, 0)),
            new(SkinningKind.Bdef4, 0, 1, 2, 3, new(.1f, .2f, .3f, .4f)),
            new(SkinningKind.Bdef2, -1, 2, -1, -1, new(NegativeZero, 1, 0, 0)),
            new(SkinningKind.Sdef, 0, 1, -1, -1, new(.37f, .63f, 0, 0), new(1, 2, 3), new(-3, 2, 5), new(2, -1, 4)),
            new(SkinningKind.Qdef, 0, 1, -1, 3, new(.2f, .3f, NegativeZero, .5f))];
        var vertices = new SceneVertex[count]; var bindings = new SkinBinding[count];
        for (int i = 0; i < count; i++)
        {
            Vector3 position = i % 4 == 0 ? new(NegativeZero, 0, NegativeZero) : new(.137f * (i % 31), -42.7f, 8.93f);
            Vector3 normal = i % 7 == 0 ? new(NegativeZero, 0, NegativeZero) : new(.37f, -.81f, .233f);
            vertices[i] = new(position, normal, i % 3 == 0 ? new(NegativeZero, 0) : new(.173f, -.829f));
            bindings[i] = templates[i % (mixed ? templates.Length : 4)];
        }
        return new(Bones(), vertices, bindings);
    }

    private static RigBone[] Bones() => [new("a", Vector3.Zero, -1), new("b", Vector3.Zero, -1),
        new("c", Vector3.Zero, -1), new("d", Vector3.Zero, -1)];

    private static Matrix4x4[] Worlds(bool rigid)
    {
        var worlds = new Matrix4x4[4];
        for (int i = 0; i < worlds.Length; i++)
        {
            var rotation = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(.13f + i * .19f, -.47f, .23f, .61f - i * .11f)));
            var scale = rigid ? Matrix4x4.Identity : Matrix4x4.CreateScale(.7f + i * .3f, 1.9f - i * .2f, 1.1f + i * .1f);
            worlds[i] = scale * rotation * Matrix4x4.CreateTranslation(3.7f - i * 5.3f, -8.1f + i * .7f, 2.9f + i * 1.1f);
        }
        return worlds;
    }

    private static MorphPose? Pose(CharacterRig rig, int mode)
    {
        if (mode == 0) return null;
        var vertices = Enumerable.Range(0, rig.Vertices.Count).Select(i => mode is 2 or 4 && i % 3 != 0
            ? new Vector3(.19f * (i % 17), -.37f, .83f) : new Vector3(NegativeZero, 0, NegativeZero));
        var uvs = Enumerable.Range(0, rig.Vertices.Count).Select(i => mode is 3 or 4 && i % 2 != 0
            ? new Vector2(-.137f, .251f * (i % 13)) : new Vector2(NegativeZero, 0));
        return new(vertices, new Vector3[rig.Bones.Count], Enumerable.Repeat(Quaternion.Identity, rig.Bones.Count), uvs);
    }

    private static byte[] Bytes<T>(T[] values) where T : struct => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    private static void Exact(CharacterSample expected, CharacterSample actual)
    {
        Assert.Equal(Bytes(expected.Vertices), Bytes(actual.Vertices));
        Assert.Equal(Bytes(expected.BoneWorldMatrices), Bytes(actual.BoneWorldMatrices));
        Assert.Equal(Bytes(expected.SkinMatrices), Bytes(actual.SkinMatrices));
    }
}
