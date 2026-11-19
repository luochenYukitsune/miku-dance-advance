using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class EnginePerfParityTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(BoneEvaluationSchedule.Mmd, IkAnglePolicy.Mmd)]
    [InlineData(BoneEvaluationSchedule.Saba, IkAnglePolicy.Saba)]
    public void ReusedConstraintEvaluatorMatchesFreshInstancesBitwiseAndPreservesErrors(
        BoneEvaluationSchedule schedule, IkAnglePolicy ikPolicy)
    {
        var (rig, clip) = CreateComplexConstraintFixture(schedule, ikPolicy);
        var reused = new CharacterAnimator(rig, clip);
        var boneMorph = new MorphPose(
            new Vector3[rig.Vertices.Count],
            [Vector3.Zero, new(0.1f, -0.05f, 0.02f), Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero],
            [
                Quaternion.Identity,
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.08f),
                Quaternion.Identity,
                Quaternion.Identity,
                Quaternion.Identity,
                Quaternion.Identity,
                Quaternion.Identity,
            ]);

        int[] orderedDriven = [1, 2, 4];
        int[] unorderedDriven = [4, 1, 2];
        var listDriven = new List<int> { 2, 4, 1 };

        // 1. Continuous multi-frame Sample & BeginPhysicsSample/Complete with mixed options + errors
        for (int step = 0; step < 12; step++)
        {
            double frame = step * 1.5;
            bool enableIk = step % 3 != 2;
            var ikOverrides = step % 2 == 0
                ? new Dictionary<int, bool> { [5] = step % 4 == 0 }
                : null;
            var outsideParents = step % 4 == 1
                ? new Dictionary<int, Matrix4x4> { [-1] = Matrix4x4.CreateTranslation(0.25f, -0.5f, 0.125f) }
                : step % 4 == 3
                    ? new Dictionary<int, Matrix4x4> { [1] = Matrix4x4.CreateRotationY(0.15f) * Matrix4x4.CreateTranslation(1f, 0f, -0.5f) }
                    : null;
            var localOverrides = step % 3 == 1
                ? new Dictionary<int, BoneLocalTransform> { [2] = new(new(0.05f, 0.02f, -0.01f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.1f)) }
                : null;
            IReadOnlyCollection<int> driven = (step % 3) switch
            {
                0 => orderedDriven,
                1 => unorderedDriven,
                _ => listDriven,
            };

            var sampleOptions = new CharacterEvaluationOptions(
                EnableIk: enableIk,
                IkEnabledOverrides: ikOverrides,
                Morphs: step % 2 == 1 ? boneMorph : null,
                OutsideParents: outsideParents,
                LocalBoneOverrides: localOverrides);

            var freshForSample = new CharacterAnimator(rig, clip);
            var expectedSample = freshForSample.Sample(frame, sampleOptions);
            var actualSample = reused.Sample(frame, sampleOptions);
            AssertSampleBitEqual(expectedSample, actualSample);

            var physicsOptions = sampleOptions with { PhysicsDrivenBoneIndices = driven };
            var freshForPhysics = new CharacterAnimator(rig, clip);
            using var expectedPhysics = freshForPhysics.BeginPhysicsSample(frame, physicsOptions);
            using var actualPhysics = reused.BeginPhysicsSample(frame, physicsOptions);
            AssertSpanBitEqual(expectedPhysics.BeforePhysicsBoneWorldMatrices, actualPhysics.BeforePhysicsBoneWorldMatrices);

            var reflectedWorlds = expectedPhysics.BeforePhysicsBoneWorldMatrices.ToArray();
            var delta = Matrix4x4.CreateRotationZ(0.05f * (step + 1)) * Matrix4x4.CreateTranslation(0.03f, -0.02f, 0.01f);
            reflectedWorlds[1] = Affine(delta * reflectedWorlds[1]);
            reflectedWorlds[2] = Affine(delta * reflectedWorlds[2]);
            reflectedWorlds[4] = Affine(delta * reflectedWorlds[4]);

            var expectedCompleted = expectedPhysics.Complete(reflectedWorlds, driven);
            var actualCompleted = actualPhysics.Complete(reflectedWorlds, driven);
            AssertSampleBitEqual(expectedCompleted, actualCompleted);

            // Also verify CompleteBoneWorldMatrices + Dispose return path
            using var expectedBoneOnly = new CharacterAnimator(rig, clip).BeginPhysicsSample(frame, physicsOptions);
            using var actualBoneOnly = reused.BeginPhysicsSample(frame, physicsOptions);
            AssertSpanBitEqual(
                expectedBoneOnly.CompleteBoneWorldMatrices(reflectedWorlds, driven),
                actualBoneOnly.CompleteBoneWorldMatrices(reflectedWorlds, driven));

            using var discarded = reused.BeginPhysicsSample(frame, physicsOptions);
            discarded.Dispose();
        }

        // Verify evaluator workspace is actually pooled and reused
        var pooledField = typeof(CharacterAnimator).GetField("pooledEvaluator", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var firstPooled = pooledField.GetValue(reused);
        Assert.NotNull(firstPooled);
        _ = reused.Sample(3.0);
        Assert.Same(firstPooled, pooledField.GetValue(reused));

        // 2. Exception parity (NaN, non-invertible, out-of-range, duplicate reflected indices) and clean recovery
        var freshOracle = new CharacterAnimator(rig, clip);
        Assert.Throws<ArgumentOutOfRangeException>(() => reused.Sample(double.NaN));
        Assert.Throws<ArgumentException>(() => reused.Sample(0, new(OutsideParents: new Dictionary<int, Matrix4x4>
        {
            [0] = Matrix4x4.CreateTranslation(float.NaN, 0, 0),
        })));
        Assert.Throws<ArgumentException>(() => reused.Sample(0, new(OutsideParents: new Dictionary<int, Matrix4x4>
        {
            [99] = Matrix4x4.Identity,
        })));
        Assert.Throws<ArgumentException>(() => reused.Sample(0, new(PhysicsDrivenBoneIndices: [-1])));
        Assert.Throws<ArgumentException>(() => reused.Sample(0, new(PhysicsDrivenBoneIndices: [rig.Bones.Count])));
        Assert.Throws<ArgumentException>(() => reused.Sample(0, new(IkEnabledOverrides: new Dictionary<int, bool> { [0] = true })));

        using (var failingPhysics = reused.BeginPhysicsSample(0, new(PhysicsDrivenBoneIndices: orderedDriven)))
        {
            var identityWorlds = Enumerable.Repeat(Matrix4x4.Identity, rig.Bones.Count).ToArray();
            Assert.Throws<ArgumentException>(() => failingPhysics.Complete(identityWorlds, [1, 0, 1]));
            Assert.Throws<ArgumentException>(() => failingPhysics.Complete(identityWorlds, [0, rig.Bones.Count]));
            var nanWorlds = (Matrix4x4[])identityWorlds.Clone();
            nanWorlds[0].M41 = float.PositiveInfinity;
            Assert.Throws<ArgumentException>(() => failingPhysics.Complete(nanWorlds, orderedDriven));
            // Sample remains usable after validation failures
            var ok = failingPhysics.Complete(identityWorlds, orderedDriven);
            using var oraclePending = freshOracle.BeginPhysicsSample(0, new(PhysicsDrivenBoneIndices: orderedDriven));
            AssertSampleBitEqual(oraclePending.Complete(identityWorlds, orderedDriven), ok);
        }

        // Subsequent valid sample matches fresh instance bitwise (zero historical residue from errors or earlier frames)
        var afterErrorActual = reused.Sample(4.5, new(Morphs: boneMorph));
        var afterErrorExpected = new CharacterAnimator(rig, clip).Sample(4.5, new(Morphs: boneMorph));
        AssertSampleBitEqual(afterErrorExpected, afterErrorActual);
    }

    [Fact]
    public async Task ConcurrentAnimatorSamplesAndPhysicsSamplesProduceIsolatedBitwiseIdenticalResults()
    {
        var (rig, clip) = CreateComplexConstraintFixture(BoneEvaluationSchedule.Mmd, IkAnglePolicy.Mmd);
        var sharedAnimator = new CharacterAnimator(rig, clip);
        double[] frames = [0, 2.5, 5.0, 7.5];
        var expectedSamples = frames.Select(f => new CharacterAnimator(rig, clip).Sample(f)).ToArray();
        var expectedPhysicsSamples = frames.Select(f =>
        {
            using var baselinePending = new CharacterAnimator(rig, clip).BeginPhysicsSample(f, new(PhysicsDrivenBoneIndices: new[] { 4, 1, 2 }));
            return baselinePending.Complete(baselinePending.BeforePhysicsBoneWorldMatrices, new[] { 4, 1, 2 });
        }).ToArray();

        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            int slot = i % frames.Length;
            var sample = sharedAnimator.Sample(frames[slot]);
            AssertSampleBitEqual(expectedSamples[slot], sample);

            using var pending = sharedAnimator.BeginPhysicsSample(frames[slot], new(PhysicsDrivenBoneIndices: new[] { 4, 1, 2 }));
            var completed = pending.Complete(pending.BeforePhysicsBoneWorldMatrices, new[] { 4, 1, 2 });
            AssertSampleBitEqual(expectedPhysicsSamples[slot], completed);
            return (slot, sample, completed);
        })).ToArray();

        var results = await Task.WhenAll(tasks);
        for (int i = 0; i < results.Length; i++)
        {
            for (int j = i + 1; j < results.Length; j++)
            {
                Assert.NotSame(results[i].sample.BoneWorldMatrices, results[j].sample.BoneWorldMatrices);
                Assert.NotSame(results[i].completed.BoneWorldMatrices, results[j].completed.BoneWorldMatrices);
            }
        }
    }

    [Fact]
    public void MorphPoseActiveRangeTrimmingPreservesPublicViewsDeferredDeformationAndValidation()
    {
        const int vertexCount = 1200;
        var model = CreateLargeMorphModel(vertexCount);
        var adapter = new PmxMorphAdapter(model);

        // Morph 0 touches only [40..45], morph 1 touches [900..904], morph 2 is bone-only,
        // morph 3 is cancelling group of morph 0 (+1, -1), morph 4 is overflow position
        var activePose = Assert.IsType<MorphPose>(adapter.Evaluate(new Dictionary<int, float> { [0] = 0.5f }));
        Assert.Equal(vertexCount, activePose.VertexDeltas.Count);
        Assert.Equal(vertexCount, activePose.VertexUvDeltas.Count);
        Assert.Equal(new Vector3(0.5f, -1f, 0.25f), activePose.VertexDeltas[42]);
        Assert.Equal(Vector3.Zero, activePose.VertexDeltas[0]);
        Assert.Equal(Vector3.Zero, activePose.VertexDeltas[vertexCount - 1]);

        var activeStartProp = typeof(MorphPose).GetProperty("ActiveVertexStart", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var activeEndProp = typeof(MorphPose).GetProperty("ActiveVertexEnd", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var firstVertexProp = typeof(MorphPose).GetProperty("FirstVertex", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var vertexCountProp = typeof(MorphPose).GetProperty("VertexCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var storedCountField = typeof(MorphPose).GetField("storedCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var hasVertexProp = typeof(MorphPose).GetProperty("HasVertexEffects", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(40, (int)activeStartProp.GetValue(activePose)!);
        Assert.Equal(45, (int)activeEndProp.GetValue(activePose)!);
        Assert.Equal(40, (int)firstVertexProp.GetValue(activePose)!);
        Assert.Equal(vertexCount, (int)vertexCountProp.GetValue(activePose)!);
        Assert.Equal(5, (int)storedCountField.GetValue(activePose)!);
        Assert.True((bool)hasVertexProp.GetValue(activePose)!);

        var boneOnlyPose = Assert.IsType<MorphPose>(adapter.Evaluate(new Dictionary<int, float> { [2] = 1f }));
        Assert.False((bool)hasVertexProp.GetValue(boneOnlyPose)!);
        Assert.Equal(0, (int)firstVertexProp.GetValue(boneOnlyPose)!);
        Assert.Equal(0, (int)storedCountField.GetValue(boneOnlyPose)!);

        var cancelledPose = Assert.IsType<MorphPose>(adapter.Evaluate(new Dictionary<int, float> { [3] = 1f }));
        Assert.False((bool)hasVertexProp.GetValue(cancelledPose)!);
        Assert.Equal(0, (int)activeStartProp.GetValue(cancelledPose)!);
        Assert.Equal(0, (int)activeEndProp.GetValue(cancelledPose)!);
        Assert.All(cancelledPose.VertexDeltas, d => Assert.Equal(Vector3.Zero, d));

        // Verify deferred deformation & eager skinning match un-trimmed full-range MorphPose bitwise
        var rig = PmxCharacterRigAdapter.Convert(model, PmxOriginalStyleAdapter.FromRestPose(model).Scene.Snapshot);
        var fullRangePose = new MorphPose(activePose.VertexDeltas, activePose.BoneTranslations, activePose.BoneRotations, activePose.VertexUvDeltas);

        var eagerAnimator = new CharacterAnimator(rig, new([]));
        var deferredAnimator = new CharacterAnimator(rig, new([]));
        typeof(CharacterAnimator).GetProperty("DeferVertexSkinning", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(deferredAnimator, true);

        var expectedEager = eagerAnimator.Sample(0, new(Morphs: fullRangePose));
        var actualEager = eagerAnimator.Sample(0, new(Morphs: activePose));
        AssertSampleBitEqual(expectedEager, actualEager);

        var expectedDeferred = deferredAnimator.Sample(0, new(Morphs: fullRangePose));
        var actualDeferred = deferredAnimator.Sample(0, new(Morphs: activePose));
        Assert.NotNull(expectedDeferred.Deformation);
        Assert.NotNull(actualDeferred.Deformation);
        AssertSpanBitEqual<SceneVertex>(expectedDeferred.Deformation!.CopyReferenceVertices(), actualDeferred.Deformation!.CopyReferenceVertices());

        // Validation parity: overflow & NaN throw exact ArgumentException and preserve prior pose cache
        var nonfinite = Assert.Throws<ArgumentException>(() => adapter.Evaluate(new Dictionary<int, float> { [0] = 0.5f, [99] = float.NaN }));
        Assert.Equal("weights", nonfinite.ParamName);
        var overflow = Assert.Throws<ArgumentException>(() => adapter.Evaluate(new Dictionary<int, float> { [4] = 2f }));
        Assert.Equal("vertexDeltas", overflow.ParamName);
        Assert.Same(cancelledPose, adapter.Evaluate(new Dictionary<int, float> { [3] = 1f }));

        // Direct internal factory checks invalid range and finite bounds
        var fromRange = typeof(MorphPose).GetMethod("FromOwnedPositionRange", BindingFlags.Static | BindingFlags.NonPublic)!;
        var zeroUvCtor = typeof(MorphPose).GetNestedType("ImmutableZeroUvChannel", BindingFlags.NonPublic)!.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(int)])!;
        var zeroUv4 = zeroUvCtor.Invoke([4]);
        var invalidRange = Assert.Throws<TargetInvocationException>(() =>
            fromRange.Invoke(null, [new Vector3[2], 3, Array.Empty<Vector3>(), Array.Empty<Quaternion>(), zeroUv4]));
        Assert.IsType<ArgumentException>(invalidRange.InnerException);
    }

    [Fact]
    public void MaterialMorphAdapterCachesIdentityAndContributionsAndRejectsInvalidInputsAndChangedBaselines()
    {
        var model = CreateMaterialMorphModel();
        var adapter = new PmxMaterialMorphAdapter(model);
        var baseline = (
            Surface: PmxOriginalStyleAdapter.FromRestPose(model).Scene,
            Outline: PmxOriginalOutlineAdapter.FromRestPose(model));

        // Zero contributions return cached static identity factors and exact input baseline
        var empty1 = adapter.EvaluateTextureFactors(new Dictionary<int, float>());
        var empty2 = adapter.EvaluateTextureFactors(new Dictionary<int, float> { [0] = 0f });
        Assert.Same(empty1, empty2);
        Assert.All(empty1, f => Assert.Equal(PmxMaterialTextureFactors.Identity, f));
        Assert.Throws<NotSupportedException>(() => ((IList<PmxMaterialTextureFactors>)empty1)[0] = default);

        var applyZero = adapter.Apply(baseline.Surface, baseline.Outline, new Dictionary<int, float> { [0] = 0f });
        Assert.Same(baseline.Surface, applyZero.Surface);
        Assert.Same(baseline.Outline, applyZero.Outline);

        // Cache hit across equivalent dictionaries (direct vs group)
        var tex1 = adapter.EvaluateTextureFactors(new Dictionary<int, float> { [0] = 0.5f });
        var tex2 = adapter.EvaluateTextureFactors(new Dictionary<int, float> { [1] = 1.0f });
        Assert.Same(tex1, tex2);

        var applied1 = adapter.Apply(baseline.Surface, baseline.Outline, new Dictionary<int, float> { [0] = 0.5f });
        var applied2 = adapter.Apply(baseline.Surface, baseline.Outline, new Dictionary<int, float> { [1] = 1.0f });
        Assert.Same(applied1.Surface, applied2.Surface);
        Assert.Same(applied1.Outline, applied2.Outline);

        // Single-ULP weight difference must invalidate the cache
        float bumped = MathF.BitIncrement(0.5f);
        var appliedBumped = adapter.Apply(baseline.Surface, baseline.Outline, new Dictionary<int, float> { [0] = bumped });
        Assert.NotSame(applied1.Surface, appliedBumped.Surface);
        Assert.Equal(baseline.Outline.Materials[0].Width + 2f * bumped, appliedBumped.Outline.Materials[0].Width);
        var texBumped = adapter.EvaluateTextureFactors(new Dictionary<int, float> { [0] = bumped });
        Assert.NotSame(tex1, texBumped);
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 0.8f) * bumped, texBumped[0].Diffuse.Add);

        // Signed zero bit difference is not conflated
        float negZero = BitConverter.Int32BitsToSingle(int.MinValue);
        Assert.NotEqual(
            BitConverter.SingleToInt32Bits(0f),
            BitConverter.SingleToInt32Bits(negZero));
        var posZeroResult = adapter.Apply(baseline.Surface, baseline.Outline, new Dictionary<int, float> { [2] = 0f });
        var negZeroResult = adapter.Apply(baseline.Surface, baseline.Outline, new Dictionary<int, float> { [2] = negZero });
        Assert.Same(baseline.Surface, posZeroResult.Surface);
        Assert.Same(baseline.Surface, negZeroResult.Surface);

        // Changed baseline materials must NOT return stale cached materials
        var scaledOutline = new OriginalOutlineData(
            baseline.Outline.Vertices,
            baseline.Outline.Materials.Select(m => m with { Width = m.Width * 3f }));
        var appliedScaled = adapter.Apply(baseline.Surface, scaledOutline, new Dictionary<int, float> { [0] = 0.5f });
        Assert.NotSame(applied1.Outline, appliedScaled.Outline);
        Assert.Equal(scaledOutline.Materials[0].Width + 2f * 0.5f, appliedScaled.Outline.Materials[0].Width);

        // Re-prime single-entry caches before deformed-vertex fast path & error preservation checks
        var primedApply = adapter.Apply(baseline.Surface, baseline.Outline, new Dictionary<int, float> { [0] = 0.5f });
        var primedTex = adapter.EvaluateTextureFactors(new Dictionary<int, float> { [0] = 0.5f });

        // Changed deformed vertices with same rest material collection preserves new vertices and reuses evaluated materials
        var deformedVerts = baseline.Surface.Snapshot.Vertices.ToArray();
        deformedVerts[0] = deformedVerts[0] with { Position = new(9f, 8f, 7f) };
        var deformedSurface = new OriginalStyleScene(
            baseline.Surface.Snapshot.WithDeformedVertices(deformedVerts),
            baseline.Surface.Materials,
            baseline.Surface.Profile);
        var appliedDeformed = adapter.Apply(deformedSurface, baseline.Outline, new Dictionary<int, float> { [0] = 0.5f });
        Assert.Equal(new Vector3(9f, 8f, 7f), appliedDeformed.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(primedApply.Surface.Snapshot.Materials, appliedDeformed.Surface.Snapshot.Materials);
        Assert.Equal(primedApply.Outline.Materials, appliedDeformed.Outline.Materials);

        // Exception parity: NaN weight, negative width, negative specular power, texture overflow
        Assert.Throws<ArgumentException>(() => adapter.Apply(baseline.Surface, baseline.Outline, new Dictionary<int, float> { [0] = float.NaN }));
        Assert.Throws<ArgumentException>(() => adapter.EvaluateTextureFactors(new Dictionary<int, float> { [99] = float.PositiveInfinity }));
        Assert.Throws<NotSupportedException>(() => adapter.Apply(baseline.Surface, baseline.Outline, new Dictionary<int, float> { [3] = 1f }));
        Assert.Throws<NotSupportedException>(() => adapter.Apply(baseline.Surface, baseline.Outline, new Dictionary<int, float> { [4] = 1f }));
        Assert.Throws<ArgumentException>(() => adapter.EvaluateTextureFactors(new Dictionary<int, float> { [5] = 2f }));

        // Cache integrity preserved after errors
        var afterErrors = adapter.Apply(baseline.Surface, baseline.Outline, new Dictionary<int, float> { [0] = 0.5f });
        Assert.Same(primedApply.Surface, afterErrors.Surface);
        Assert.Same(primedApply.Outline, afterErrors.Outline);
        Assert.Same(primedTex, adapter.EvaluateTextureFactors(new Dictionary<int, float> { [0] = 0.5f }));
    }

    [Fact]
    public void IndependentMultiActorParallelEvaluationAndStabilizationMatchSerialBitwise()
    {
        var model = CreateStandardPhysicsAndMorphModel();
        var layout = new SceneCompositionEditor();
        var rest = SharedPhysicsFixture.Rest(model);
        var actors = Enumerable.Range(0, 4).Select(i =>
        {
            var obj = layout.Add("actor" + i, $"actor{i}.pmx", ModelRole.Character, rest);
            layout.SetTransform(obj.Id, new(i * 4f, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f * i), 1);
            return layout.Objects[^1];
        }).ToArray();

        var motions = actors.Select((a, i) => new SceneActorMotion(
            a.Id, model, CreateActorMotion((uint)(30 + i * 5), i + 1), IkEnabled: true, MorphsEnabled: true, PhysicsEnabled: true)).ToArray();

        var parallelBackends = new List<ConcurrentIndependentBackend>();
        var serialBackends = new List<ConcurrentIndependentBackend>();

        using var parallelSession = new SceneAnimationSession(
            layout.Objects, motions,
            _ => { var b = new ConcurrentIndependentBackend(concurrent: true); parallelBackends.Add(b); return b; },
            cancellationToken: CancellationToken.None);
        using var serialSession = new SceneAnimationSession(
            layout.Objects, motions,
            _ => { var b = new ConcurrentIndependentBackend(concurrent: false); serialBackends.Add(b); return b; },
            cancellationToken: CancellationToken.None);

        // Verify StabilizePhysicsAtCurrentFrame parallel vs serial
        var pInit = parallelSession.StabilizePhysicsAtCurrentFrame(6, CancellationToken.None);
        var sInit = serialSession.StabilizePhysicsAtCurrentFrame(6, CancellationToken.None);
        AssertSceneBitEqual(sInit, pInit);
        AssertActorsEqual(serialSession.Actors, parallelSession.Actors);
        foreach (var a in actors)
            AssertBoneMatricesBitEqual(serialSession.GetBoneWorldMatrices(a.Id), parallelSession.GetBoneWorldMatrices(a.Id));

        if (Environment.ProcessorCount >= 2)
        {
            Assert.True(parallelBackends.SelectMany(b => b.ManagedThreadIds).Distinct().Count() >= 2
                || parallelBackends.Max(b => b.ObservedPeakConcurrency) >= 1);
        }
        Assert.All(serialBackends, b => Assert.Single(b.ManagedThreadIds.Distinct()));

        // Verify Seek & multi-frame Advance parallel vs serial
        var pSeek = parallelSession.Seek(10, CancellationToken.None);
        var sSeek = serialSession.Seek(10, CancellationToken.None);
        AssertSceneBitEqual(sSeek, pSeek);

        for (int step = 0; step < 5; step++)
        {
            var pAdv = parallelSession.Advance(1.0 / 30, CancellationToken.None);
            var sAdv = serialSession.Advance(1.0 / 30, CancellationToken.None);
            AssertSceneBitEqual(sAdv, pAdv);
            AssertActorsEqual(serialSession.Actors, parallelSession.Actors);
            foreach (var a in actors)
                AssertBoneMatricesBitEqual(serialSession.GetBoneWorldMatrices(a.Id), parallelSession.GetBoneWorldMatrices(a.Id));
        }

        // Verify exception order preservation in parallel Evaluate and StabilizePhysicsAtCurrentFrame
        parallelBackends[1].FailOnStep = true;
        parallelBackends[3].FailOnStep = true;
        var error = Assert.Throws<SceneActorAnimationException>(() => parallelSession.Advance(1.0 / 30, CancellationToken.None));
        Assert.Equal(actors[1].Id, error.ObjectId);
        Assert.True(parallelSession.IsFaulted);
    }

    [Fact]
    public void IndependentParallelFallsBackWhenPoseEvaluationMetricsAreActive()
    {
        var model = CreateStandardPhysicsAndMorphModel();
        var layout = new SceneCompositionEditor();
        var rest = SharedPhysicsFixture.Rest(model);
        var actors = Enumerable.Range(0, 3).Select(i => layout.Add("actor" + i, $"m{i}.pmx", ModelRole.Character, rest)).ToArray();
        var motions = actors.Select((a, i) => new SceneActorMotion(a.Id, model, CreateActorMotion(30, i + 1), PhysicsEnabled: true)).ToArray();
        var backends = new List<ConcurrentIndependentBackend>();

        using var listener = new PoseMetricsTestListener();
        using var session = new SceneAnimationSession(
            layout.Objects, motions,
            _ => { var b = new ConcurrentIndependentBackend(concurrent: true); backends.Add(b); return b; },
            cancellationToken: CancellationToken.None);

        foreach (var b in backends) b.ClearThreads();
        session.StabilizePhysicsAtCurrentFrame(4, CancellationToken.None);
        session.Advance(1.0 / 30, CancellationToken.None);
        // Metrics listener active -> strictly serial on the calling thread
        Assert.All(backends, b => Assert.All(b.ManagedThreadIds, tid => Assert.Equal(Environment.CurrentManagedThreadId, tid)));
    }

    [Fact]
    public void SharedPhysicsParallelPrepareAndCompleteMatchSerialBitwiseAndPreserveFaultOrder()
    {
        var model = CreateStandardPhysicsAndMorphModel();
        var layout = new SceneCompositionEditor();
        var rest = SharedPhysicsFixture.Rest(model);
        var actors = Enumerable.Range(0, 4).Select(i =>
        {
            var obj = layout.Add("shared" + i, $"shared{i}.pmx", ModelRole.Character, rest);
            layout.SetTransform(obj.Id, new(i * 5f, 0, 0), Quaternion.Identity, 1);
            return layout.Objects[^1];
        }).ToArray();

        var motions = actors.Select((a, i) => new SceneActorMotion(
            a.Id, model, CreateActorMotion((uint)(25 + i * 3), i + 1), PhysicsEnabled: i < 3)).ToArray();

        var parallelShared = new TrackingSharedBackend();
        var serialShared = new TrackingSharedBackend();

        using var parallelSession = new SceneAnimationSession(
            layout.Objects, motions,
            _ => throw new InvalidOperationException("Unexpected independent physics"),
            sharedPhysicsFactory: () => parallelShared,
            cancellationToken: CancellationToken.None);

        using var serialCts = new CancellationTokenSource();
        using var serialSession = new SceneAnimationSession(
            layout.Objects, motions,
            _ => throw new InvalidOperationException("Unexpected independent physics"),
            sharedPhysicsFactory: () => serialShared,
            cancellationToken: serialCts.Token); // CanBeCanceled == true forces serial boundary

        // Stabilize, Seek, Advance, fractional Advance
        AssertSceneBitEqual(
            serialSession.StabilizePhysicsAtCurrentFrame(5, serialCts.Token),
            parallelSession.StabilizePhysicsAtCurrentFrame(5, CancellationToken.None));
        AssertSceneBitEqual(
            serialSession.Seek(4, serialCts.Token),
            parallelSession.Seek(4, CancellationToken.None));

        foreach (double dt in new[] { 1.0 / 30, 1.0 / 90, 0.05 })
        {
            var s = serialSession.Advance(dt, serialCts.Token);
            var p = parallelSession.Advance(dt, CancellationToken.None);
            AssertSceneBitEqual(s, p);
            AssertActorsEqual(serialSession.Actors, parallelSession.Actors);
            foreach (var a in actors)
                AssertBoneMatricesBitEqual(serialSession.GetBoneWorldMatrices(a.Id), parallelSession.GetBoneWorldMatrices(a.Id));
        }

        // Verify Compose / CaptureStates reuse immutability and diagnostics updates
        var statesBefore = parallelSession.Actors;
        var diagBefore = statesBefore[0].Diagnostics;
        Assert.Throws<NotSupportedException>(() => ((IList<SceneActorPlaybackState>)statesBefore)[0] = statesBefore[1]);
        double localBefore = statesBefore[0].LocalFrame;
        parallelSession.Advance(1.0 / 30, CancellationToken.None);
        var statesAfter = parallelSession.Actors;
        Assert.Equal(localBefore, statesBefore[0].LocalFrame);
        Assert.Same(diagBefore, statesAfter[0].Diagnostics);

        // Corrupt shared backend output on actor 1 -> Complete fails cleanly, Faults session, and cleans up pooled workspaces
        parallelShared.CorruptFirstActorPose = true;
        Assert.Throws<ArgumentException>(() => parallelSession.Advance(1.0 / 30, CancellationToken.None));
        Assert.True(parallelSession.IsFaulted);
    }

    private static (CharacterRig Rig, MotionClip Clip) CreateComplexConstraintFixture(
        BoneEvaluationSchedule schedule, IkAnglePolicy ikPolicy)
    {
        var bones = new RigBone[]
        {
            new("root", Vector3.Zero, -1, DeformLayer: 0),
            new("upper", new(0, 1, 0), 0, DeformLayer: 0),
            new("lower", new(0, 2, 0), 1, DeformLayer: 0,
                Append: new(SourceBoneIndex: 1, Weight: 0.35f, Local: false, Rotate: true, Translate: true),
                FixedAxis: schedule == BoneEvaluationSchedule.Mmd ? Vector3.UnitZ : null),
            new("effector", new(0, 3, 0), 2, DeformLayer: 0),
            new("follower", new(1, 2, 0), 0, DeformLayer: 1,
                Append: new(SourceBoneIndex: 2, Weight: -0.5f, Local: true, Rotate: true, Translate: false)),
            new("ikController", new(0.5f, 2.5f, 0.25f), 0, DeformLayer: 0,
                Ik: new(targetBoneIndex: 3, iterations: 12, angleLimit: 0.5f,
                    links: new IkLinkDefinition[]
                    {
                        new(2, new(-1.2f, 0, 0), new(1.2f, 0, 0)),
                        new(1, new(-0.8f, -0.5f, -0.6f), new(0.8f, 0.5f, 0.6f)),
                    },
                    anglePolicy: ikPolicy)),
            new("afterPhysics", new(-1, 1, 0), 0, DeformLayer: 0, AfterPhysics: true,
                Append: new(SourceBoneIndex: 4, Weight: 0.75f, Local: false, Rotate: true, Translate: true)),
        };
        var vertices = new SceneVertex[]
        {
            new(new(0, 1.5f, 0), Vector3.UnitZ, new(0, 0)),
            new(new(0, 2.5f, 0), Vector3.UnitY, new(0.5f, 0.5f)),
            new(new(1, 2f, 0), Vector3.UnitX, new(1, 1)),
        };
        var bindings = new SkinBinding[]
        {
            new(SkinningKind.Bdef2, 1, 2, -1, -1, new(0.4f, 0.6f, 0, 0)),
            new(SkinningKind.Sdef, 2, 3, -1, -1, new(0.3f, 0.7f, 0, 0), new(0, 2.5f, 0), new(0, 2, 0), new(0, 3, 0)),
            new(SkinningKind.Bdef1, 4, -1, -1, -1, new(1, 0, 0, 0)),
        };
        var rig = new CharacterRig(bones, vertices, bindings, evaluationSchedule: schedule);
        var tracks = new BoneTrack[]
        {
            new("root", new BoneKeyframe[]
            {
                new(0, Vector3.Zero, Quaternion.Identity, BoneCurves.Linear),
                new(10, new(0.2f, -0.1f, 0.15f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f), BoneCurves.Linear),
                new(20, new(-0.1f, 0.2f, -0.05f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.3f), BoneCurves.Linear),
            }),
            new("ikController", new BoneKeyframe[]
            {
                new(0, Vector3.Zero, Quaternion.Identity, BoneCurves.Linear),
                new(10, new(0.4f, -0.6f, 0.3f), Quaternion.Identity, BoneCurves.Linear),
                new(20, new(-0.3f, -0.2f, -0.4f), Quaternion.Identity, BoneCurves.Linear),
            }),
            new("upper", new BoneKeyframe[]
            {
                new(0, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.1f), BoneCurves.Linear),
                new(20, new(0, 0.05f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.2f), BoneCurves.Linear),
            }),
        };
        return (rig, new MotionClip(tracks));
    }

    private static PmxModel CreateLargeMorphModel(int vertexCount)
    {
        var vertices = Enumerable.Range(0, vertexCount).Select(i => new PmxVertex
        {
            Position = new(i * 0.01f, 0, 0),
            Normal = Vector3.UnitZ,
            Uv = Vector2.Zero,
            AdditionalUvs = [],
            WeightType = PmxVertexWeightType.Bdef1,
            BoneIndices = [0, -1, -1, -1],
            BoneWeights = [1, 0, 0, 0],
            EdgeScale = 1,
        }).ToArray();

        return new PmxModel
        {
            Header = new()
            {
                Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 2, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1,
            },
            Name = "large-morph", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = vertices,
            Faces = new PmxTriangle[] { new(0, 1, 2) },
            Textures = Array.Empty<string>(),
            Materials = new PmxMaterial[]
            {
                new()
                {
                    Name = "mat", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                    Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0,
                    TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
                    ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3,
                },
            },
            Bones = new PmxBone[]
            {
                new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate },
            },
            Morphs = new PmxMorph[]
            {
                new()
                {
                    Name = "faceA", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Position,
                    PositionOffsets = Enumerable.Range(40, 5).Select(v => new PmxMorph.PositionOffset(v, new(1f, -2f, 0.5f))).ToArray(),
                },
                new()
                {
                    Name = "faceB", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Position,
                    PositionOffsets = Enumerable.Range(900, 4).Select(v => new PmxMorph.PositionOffset(v, new(0.25f, 0.5f, -0.75f))).ToArray(),
                },
                new()
                {
                    Name = "boneOnly", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Bone,
                    BoneOffsets = new PmxMorph.BoneOffset[] { new(0, new(0.1f, 0, 0), Quaternion.Identity) },
                },
                new()
                {
                    Name = "cancel", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Group,
                    GroupOffsets = new PmxMorph.GroupOffset[] { new(0, 1f), new(0, -1f) },
                },
                new()
                {
                    Name = "overflow", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Position,
                    PositionOffsets = new PmxMorph.PositionOffset[] { new(10, new(float.MaxValue, 0, 0)) },
                },
            },
            DisplayFrames = Array.Empty<PmxDisplayFrame>(),
            RigidBodies = Array.Empty<PmxRigidBody>(),
            Joints = Array.Empty<PmxJoint>(),
            SoftBodies = Array.Empty<PmxSoftBody>(),
        };
    }

    private static PmxModel CreateMaterialMorphModel()
    {
        return new PmxModel
        {
            Header = new()
            {
                Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1,
            },
            Name = "mat-morph", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = Enumerable.Range(0, 3).Select(i => new PmxVertex
            {
                Position = new(i, 0, 0), Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [],
                WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1,
            }).ToArray(),
            Faces = new PmxTriangle[] { new(0, 1, 2) },
            Textures = Array.Empty<string>(),
            Materials = new PmxMaterial[]
            {
                new()
                {
                    Name = "mat0", EnglishName = "", Diffuse = new(0.8f, 0.8f, 0.8f, 1f), Specular = new(0.5f),
                    SpecularPower = 4f, Ambient = new(0.2f), DrawMode = PmxDrawModeFlags.DrawEdge,
                    EdgeColor = new(0, 0, 0, 1), EdgeSize = 2f, TextureIndex = -1, SphereTextureIndex = -1,
                    SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3,
                },
            },
            Bones = new PmxBone[]
            {
                new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.AllowRotate },
            },
            Morphs = new PmxMorph[]
            {
                new()
                {
                    Name = "edgeAndTex", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Material,
                    MaterialOffsets = new PmxMorph.MaterialOffset[]
                    {
                        new(0, PmxMaterialMorphOperation.Add, Vector4.Zero, Vector3.Zero, 0, Vector3.Zero,
                            Vector4.Zero, 2f, new(0.2f, 0.4f, 0.6f, 0.8f), Vector4.Zero, Vector4.Zero),
                    },
                },
                new()
                {
                    Name = "groupHalf", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Group,
                    GroupOffsets = new PmxMorph.GroupOffset[] { new(0, 0.5f) },
                },
                new()
                {
                    Name = "zeroTex", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Material,
                    MaterialOffsets = new PmxMorph.MaterialOffset[]
                    {
                        new(0, PmxMaterialMorphOperation.Add, Vector4.Zero, Vector3.Zero, 0, Vector3.Zero,
                            Vector4.Zero, 0f, Vector4.Zero, Vector4.Zero, Vector4.Zero),
                    },
                },
                new()
                {
                    Name = "negWidth", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Material,
                    MaterialOffsets = new PmxMorph.MaterialOffset[]
                    {
                        new(0, PmxMaterialMorphOperation.Add, Vector4.Zero, Vector3.Zero, 0, Vector3.Zero,
                            Vector4.Zero, -10f, Vector4.Zero, Vector4.Zero, Vector4.Zero),
                    },
                },
                new()
                {
                    Name = "negPower", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Material,
                    MaterialOffsets = new PmxMorph.MaterialOffset[]
                    {
                        new(0, PmxMaterialMorphOperation.Add, Vector4.Zero, Vector3.Zero, -10f, Vector3.Zero,
                            Vector4.Zero, 0f, Vector4.Zero, Vector4.Zero, Vector4.Zero),
                    },
                },
                new()
                {
                    Name = "texOverflow", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Material,
                    MaterialOffsets = new PmxMorph.MaterialOffset[]
                    {
                        new(0, PmxMaterialMorphOperation.Add, Vector4.Zero, Vector3.Zero, 0f, Vector3.Zero,
                            Vector4.Zero, 0f, new(float.MaxValue), Vector4.Zero, Vector4.Zero),
                    },
                },
            },
            DisplayFrames = Array.Empty<PmxDisplayFrame>(),
            RigidBodies = Array.Empty<PmxRigidBody>(),
            Joints = Array.Empty<PmxJoint>(),
            SoftBodies = Array.Empty<PmxSoftBody>(),
        };
    }

    private static PmxModel CreateStandardPhysicsAndMorphModel()
    {
        return new PmxModel
        {
            Header = new()
            {
                Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1,
            },
            Name = "std-phys", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = new PmxVertex[]
            {
                new() { Position = new(0, 0, 0), Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 },
                new() { Position = new(1, 0, 0), Normal = Vector3.UnitZ, Uv = Vector2.UnitX, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [1, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 },
                new() { Position = new(0, 1, 0), Normal = Vector3.UnitZ, Uv = Vector2.UnitY, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [2, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 },
            },
            Faces = new PmxTriangle[] { new(0, 1, 2) },
            Textures = Array.Empty<string>(),
            Materials = new PmxMaterial[]
            {
                new()
                {
                    Name = "mat", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1f,
                    Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.DrawEdge, EdgeColor = Vector4.One, EdgeSize = 1f,
                    TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
                    ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3,
                },
            },
            Bones = new PmxBone[]
            {
                new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate, IkLinks = Array.Empty<PmxIkLink>() },
                new() { Name = "dyn", EnglishName = "", Position = new(0, 1, 0), ParentIndex = 0, DeformDepth = 0, Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate, IkLinks = Array.Empty<PmxIkLink>() },
                new() { Name = "append", EnglishName = "", Position = new(0, 2, 0), ParentIndex = 1, DeformDepth = 0, Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AppendRotate, AppendBoneIndex = 1, AppendWeight = 0.5f, IkLinks = Array.Empty<PmxIkLink>() },
                new() { Name = "effector", EnglishName = "", Position = new(0, 2, 0), ParentIndex = 1, DeformDepth = 0, Flags = PmxBoneFlags.AllowRotate, IkLinks = Array.Empty<PmxIkLink>() },
                new()
                {
                    Name = "ik", EnglishName = "", Position = new(0, 2, 0), ParentIndex = -1, DeformDepth = 0,
                    Flags = PmxBoneFlags.AllowTranslate | PmxBoneFlags.Ik,
                    IkTargetBoneIndex = 3, IkIterationCount = 6, IkLimit = 0.5f,
                    IkLinks = new PmxIkLink[] { new(1, false, default, default) },
                },
            },
            Morphs = new PmxMorph[]
            {
                new()
                {
                    Name = "lift", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Position,
                    PositionOffsets = new PmxMorph.PositionOffset[] { new(2, new(0, 0.25f, 0)) },
                    BoneOffsets = Array.Empty<PmxMorph.BoneOffset>(), UvOffsets = Array.Empty<PmxMorph.UvOffset>(),
                    MaterialOffsets = Array.Empty<PmxMorph.MaterialOffset>(), GroupOffsets = Array.Empty<PmxMorph.GroupOffset>(),
                    FlipOffsets = Array.Empty<PmxMorph.GroupOffset>(), ImpulseOffsets = Array.Empty<PmxMorph.ImpulseOffset>(),
                },
                new()
                {
                    Name = "fade", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Material,
                    PositionOffsets = Array.Empty<PmxMorph.PositionOffset>(), BoneOffsets = Array.Empty<PmxMorph.BoneOffset>(),
                    UvOffsets = Array.Empty<PmxMorph.UvOffset>(),
                    MaterialOffsets = new PmxMorph.MaterialOffset[]
                    {
                        new(0, PmxMaterialMorphOperation.Multiply, new(1, 1, 1, 0.5f), Vector3.One, 1f, Vector3.One, Vector4.One, 1f, Vector4.One, Vector4.One, Vector4.One),
                    },
                    GroupOffsets = Array.Empty<PmxMorph.GroupOffset>(), FlipOffsets = Array.Empty<PmxMorph.GroupOffset>(),
                    ImpulseOffsets = Array.Empty<PmxMorph.ImpulseOffset>(),
                },
            },
            DisplayFrames = Array.Empty<PmxDisplayFrame>(),
            RigidBodies = new PmxRigidBody[]
            {
                new()
                {
                    Name = "kin", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
                    Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(0.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
                    Mass = 0, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Static,
                },
                new()
                {
                    Name = "dynBody", EnglishName = "", BoneIndex = 1, Group = 0, CollisionGroup = 0,
                    Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(0.2f), Translation = new(0, 1, 0), Rotation = Vector3.Zero,
                    Mass = 1, TranslationDamping = 0.1f, RotationDamping = 0.1f, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic,
                },
            },
            Joints = Array.Empty<PmxJoint>(),
            SoftBodies = Array.Empty<PmxSoftBody>(),
        };
    }

    private static VmdMotion CreateActorMotion(uint duration, int seed) => new()
    {
        BoneKeyframes =
        [
            new() { BoneName = "root", Frame = 0, Translation = Vector3.Zero, Rotation = Quaternion.Identity, Interpolation = LinearInterpolation() },
            new() { BoneName = "root", Frame = duration, Translation = new(0.2f * seed, 0.1f * seed, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.1f * seed), Interpolation = LinearInterpolation() },
            new() { BoneName = "ik", Frame = 0, Translation = Vector3.Zero, Rotation = Quaternion.Identity, Interpolation = LinearInterpolation() },
            new() { BoneName = "ik", Frame = duration, Translation = new(0.1f * seed, -0.15f, 0.05f), Rotation = Quaternion.Identity, Interpolation = LinearInterpolation() },
        ],
        MorphKeyframes =
        [
            new() { MorphName = "lift", Frame = 0, Weight = 0f },
            new() { MorphName = "lift", Frame = duration, Weight = 0.8f },
            new() { MorphName = "fade", Frame = 0, Weight = 0f },
            new() { MorphName = "fade", Frame = duration, Weight = 0.5f },
        ],
    };

    private static byte[] LinearInterpolation()
    {
        var bytes = new byte[64];
        for (int i = 0; i < 4; i++)
        {
            bytes[i * 4 + 0] = 20; bytes[i * 4 + 1] = 20;
            bytes[i * 4 + 2] = 107; bytes[i * 4 + 3] = 107;
        }
        return bytes;
    }

    private static Matrix4x4 Affine(Matrix4x4 m) { m.M14 = m.M24 = m.M34 = 0; m.M44 = 1; return m; }

    private static void AssertSampleBitEqual(CharacterSample expected, CharacterSample actual)
    {
        AssertSpanBitEqual<Matrix4x4>(expected.BoneWorldMatrices, actual.BoneWorldMatrices);
        AssertSpanBitEqual<Matrix4x4>(expected.SkinMatrices, actual.SkinMatrices);
        AssertSpanBitEqual<SceneVertex>(expected.Vertices, actual.Vertices);
    }

    private static void AssertSceneBitEqual(OriginalFrameScene expected, OriginalFrameScene actual)
    {
        AssertSpanBitEqual<SceneVertex>(expected.Surface.Snapshot.Vertices, actual.Surface.Snapshot.Vertices);
        AssertSpanBitEqual<OriginalOutlineVertex>(expected.Outline!.Vertices.ToArray(), actual.Outline!.Vertices.ToArray());
        Assert.Equal(expected.Surface.Materials, actual.Surface.Materials);
        Assert.Equal(expected.Materials, actual.Materials);
    }

    private static void AssertBoneMatricesBitEqual(IReadOnlyList<Matrix4x4> expected, IReadOnlyList<Matrix4x4> actual)
        => AssertSpanBitEqual<Matrix4x4>(expected.ToArray(), actual.ToArray());

    private static void AssertActorsEqual(IReadOnlyList<SceneActorPlaybackState> expected, IReadOnlyList<SceneActorPlaybackState> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].ObjectId, actual[i].ObjectId);
            Assert.Equal(expected[i].HasMotion, actual[i].HasMotion);
            Assert.Equal(expected[i].DurationFrames, actual[i].DurationFrames);
            Assert.Equal(expected[i].LocalFrame, actual[i].LocalFrame);
            Assert.Equal(expected[i].IkEnabled, actual[i].IkEnabled);
            Assert.Equal(expected[i].MorphsEnabled, actual[i].MorphsEnabled);
            Assert.Equal(expected[i].SupportsPhysics, actual[i].SupportsPhysics);
            Assert.Equal(expected[i].PhysicsEnabled, actual[i].PhysicsEnabled);
            Assert.Equal(expected[i].PhysicsBackend, actual[i].PhysicsBackend);
            Assert.Equal(expected[i].Visible, actual[i].Visible);
            Assert.Equal(expected[i].Diagnostics, actual[i].Diagnostics);
        }
    }

    private static void AssertSpanBitEqual<T>(ReadOnlySpan<T> expected, ReadOnlySpan<T> actual) where T : unmanaged
        => Assert.True(MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)));

    private sealed class ConcurrentIndependentBackend(bool concurrent) : ICharacterPhysicsBackend
    {
        private Matrix4x4[] bodies = [];
        private Vector3[] velocities = [];
        private PhysicsBodyMode[] modes = [];
        private int activeCount;
        public int ObservedPeakConcurrency;
        public bool FailOnStep;
        public readonly ConcurrentBag<int> ManagedThreadIds = [];

        public string Name => "concurrent-independent-test";
        public IReadOnlyList<string> Diagnostics => Array.Empty<string>();
        public bool SupportsConcurrentIndependentWorlds => concurrent;

        public void ClearThreads() => ManagedThreadIds.Clear();

        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> bodyWorldTransforms)
        {
            bodies = bodyWorldTransforms.ToArray();
            velocities = new Vector3[bodies.Length];
            modes = definition.Bodies.Select(b => b.Mode).ToArray();
        }

        public void SetKinematicTarget(int bodyIndex, Matrix4x4 worldTransform, float stepSeconds)
            => bodies[bodyIndex] = worldTransform;

        public Matrix4x4 GetBodyWorldTransform(int bodyIndex) => bodies[bodyIndex];

        public void Step(float stepSeconds)
        {
            if (FailOnStep) throw new InvalidOperationException("Injected step failure.");
            ManagedThreadIds.Add(Environment.CurrentManagedThreadId);
            int now = Interlocked.Increment(ref activeCount);
            int peak;
            do
            {
                peak = ObservedPeakConcurrency;
                if (peak >= now) break;
            } while (Interlocked.CompareExchange(ref ObservedPeakConcurrency, now, peak) != peak);

            try
            {
                for (int i = 0; i < bodies.Length; i++)
                {
                    if (modes[i] == PhysicsBodyMode.Kinematic) continue;
                    velocities[i] += new Vector3(0, -9.8f, 0) * stepSeconds;
                    bodies[i].Translation += velocities[i] * stepSeconds;
                }
            }
            finally
            {
                Interlocked.Decrement(ref activeCount);
            }
        }

        public void Dispose() { }
    }

    private sealed class TrackingSharedBackend : ICharacterPhysicsMutableBackend
    {
        private sealed class Body(PhysicsBodyDefinition definition, Matrix4x4 transform)
        {
            public readonly PhysicsBodyDefinition Definition = definition;
            public Matrix4x4 Transform = transform;
            public Vector3 Velocity;
            public bool Kinematic = definition.Mode == PhysicsBodyMode.Kinematic;
            public bool Removed;
        }

        private readonly List<Body> bodies = [];
        public bool CorruptFirstActorPose;
        public string Name => "tracking-shared";
        public IReadOnlyList<string> Diagnostics => Array.Empty<string>();

        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms)
        {
            bodies.Clear();
            for (int i = 0; i < definition.Bodies.Count; i++) AddBody(definition.Bodies[i], transforms[i]);
        }

        public int AddBody(PhysicsBodyDefinition body, Matrix4x4 transform)
        {
            bodies.Add(new(body, transform));
            return bodies.Count - 1;
        }

        public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 a, Matrix4x4 b) => throw new NotSupportedException();
        public void RemoveJoint(int index) => throw new NotSupportedException();
        public void RemoveBody(int index) => bodies[index].Removed = true;
        public void ResetBody(int index, Matrix4x4 transform) { bodies[index].Transform = transform; bodies[index].Velocity = default; }

        public void SetBodyKinematic(int index, bool kinematic)
        {
            var body = bodies[index];
            kinematic |= body.Definition.Mode == PhysicsBodyMode.Kinematic;
            if (body.Kinematic != kinematic) body.Velocity = default;
            body.Kinematic = kinematic;
        }

        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds) => bodies[index].Transform = transform;

        public Matrix4x4 GetBodyWorldTransform(int index)
        {
            if (bodies[index].Removed) throw new ArgumentOutOfRangeException(nameof(index));
            if (CorruptFirstActorPose && index == 1)
            {
                var bad = bodies[index].Transform;
                bad.M11 = float.NaN;
                return bad;
            }
            return bodies[index].Transform;
        }

        public void SetGravity(Vector3 gravity) { }

        public void ApplyImpulse(int index, Vector3 linear, Vector3 angular)
        {
            if (!bodies[index].Kinematic) bodies[index].Velocity += linear / bodies[index].Definition.Mass;
        }

        public void Step(float seconds)
        {
            foreach (var body in bodies)
            {
                if (body.Removed || body.Kinematic) continue;
                body.Velocity += new Vector3(0, -9.8f, 0) * seconds;
                var pose = body.Transform;
                pose.Translation += body.Velocity * seconds;
                body.Transform = pose;
            }
        }

        public void Dispose() { }
    }

    private sealed class PoseMetricsTestListener : EventListener
    {
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "MDA-PoseEvaluation") EnableEvents(source, EventLevel.Informational);
        }
    }
}
