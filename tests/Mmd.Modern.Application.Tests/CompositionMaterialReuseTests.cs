using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class CompositionMaterialReuseTests
{
    private sealed class Cache
    {
        private static readonly Type Type = typeof(ProjectSceneAssembler).GetNestedType("CompositionCache", BindingFlags.NonPublic)!;
        private static readonly MethodInfo ComposeMethod = Type.GetMethod("Compose", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly MethodInfo ClearMethod = Type.GetMethod("Clear", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly object owner = Activator.CreateInstance(Type, [null])!;

        internal OriginalFrameScene Compose(OriginalFrameScene? baseline, Guid[] ids, OriginalFrameScene[] sources, params OriginalFrameScene[] frames)
        {
            try { return (OriginalFrameScene)ComposeMethod.Invoke(owner, [baseline, ids, sources, frames, Vector3.One, Vector3.UnitY])!; }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }

        internal void Clear() => ClearMethod.Invoke(owner, null);
    }

    [Fact]
    public void ContinuousSkeletalAnimationReusesUnchangedMaterialsBindingsUvsSeedsAndFlagsAcrossMultiActorProps()
    {
        var cache = new Cache();
        var texCharDiff = Tex("char-diff", 10);
        var texCharToon = Tex("char-toon", 20);
        var texCharSphere = Tex("char-sphere", 30);
        var texPropShared = Tex("prop-shared", 40);
        var texPropUnique = Tex("prop-unique", 50);

        var hero = BuildActorFrame("hero", vertexCount: 12, materialCount: 3, [texCharDiff, texCharToon, texCharSphere],
            withUvs: true, withSeeds: true, deferred: false, world: Matrix4x4.Identity, seedTag: 1);
        var sidekick = BuildActorFrame("sidekick", vertexCount: 9, materialCount: 2, [texCharDiff, texPropShared],
            withUvs: true, withSeeds: false, deferred: true, world: Matrix4x4.CreateTranslation(2, 0, 1), seedTag: 2);
        var propA = BuildActorFrame("prop-a", vertexCount: 6, materialCount: 4, [texPropShared, texPropUnique],
            withUvs: false, withSeeds: true, deferred: false, world: Matrix4x4.CreateTranslation(-3, 1, 0), seedTag: 3);
        var propB = BuildActorFrame("prop-b", vertexCount: 6, materialCount: 3, [texPropUnique, texCharToon],
            withUvs: true, withSeeds: false, deferred: false, world: Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateTranslation(0, 2, -1), seedTag: 4);

        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        OriginalFrameScene[] sources = [hero, sidekick, propA, propB];

        var baseline = cache.Compose(null, ids, sources, hero, sidekick, propA, propB);
        var initialExpected = Combine(hero, sidekick, propA, propB);
        AssertSceneParity(initialExpected, baseline);

        // Simulate continuous skeletal animation frames on both CPU-deformed hero and deferred sidekick,
        // including a seedless prop moving via World and value-equal re-evaluated flags (as produced by FromEvaluatedMaterials).
        OriginalFrameScene current = baseline;
        for (int frameIndex = 1; frameIndex <= 5; frameIndex++)
        {
            var deformedHero = DeformVertices(hero, frameIndex * 1.25f, newFlagsInstances: true);
            var deformedSidekick = DeformDeferred(sidekick, frameIndex * 0.75f,
                Matrix4x4.CreateRotationY(frameIndex * 0.1f) * Matrix4x4.CreateTranslation(2, frameIndex * 0.2f, 1));
            var movedSeedlessProp = Reframe(propB, propB.Surface.Snapshot.WithWorld(
                Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateTranslation(frameIndex * 0.5f, 2, -1)));

            OriginalFrameScene[] stepInputs = [deformedHero, deformedSidekick, propA, movedSeedlessProp];
            var next = cache.Compose(current, ids, sources, stepInputs);
            var expected = Combine(stepInputs);

            AssertSceneParity(expected, next);
            Assert.Same(baseline.Surface.Snapshot.Materials, next.Surface.Snapshot.Materials);
            Assert.Same(baseline.Surface.Snapshot.Textures, next.Surface.Snapshot.Textures);
            Assert.Same(baseline.Surface.Snapshot.AdditionalUvs, next.Surface.Snapshot.AdditionalUvs);
            Assert.Same(baseline.Surface.Snapshot.RawRadialSeeds, next.Surface.Snapshot.RawRadialSeeds);
            Assert.Same(baseline.Outline!.Materials, next.Outline!.Materials);
            for (int m = 0; m < baseline.Surface.Materials.Count; m++)
                Assert.Same(baseline.Surface.Materials[m], next.Surface.Materials[m]);
            for (int m = 0; m < baseline.Materials.Count; m++)
                Assert.Same(baseline.Materials[m], next.Materials[m]);

            // Repeated identical inputs must return the exact published scene reference.
            Assert.Same(next, cache.Compose(next, ids, sources, stepInputs));
            current = next;
        }
    }

    [Fact]
    public void MaterialTransparencyTextureVisibilityAndPropListChangesInvalidateSelectivelyAndMatchFullRebuild()
    {
        var cache = new Cache();
        var texA = Tex("tex-a", 1);
        var texB = Tex("tex-b", 2);
        var texC = Tex("tex-c", 3);

        var hero = BuildActorFrame("hero", 6, 2, [texA, texB], withUvs: true, withSeeds: true, deferred: false, Matrix4x4.Identity, 1);
        var prop1 = BuildActorFrame("prop-1", 6, 3, [texB, texC], withUvs: true, withSeeds: false, deferred: false, Matrix4x4.CreateTranslation(1, 0, 0), 2);
        var prop2 = BuildActorFrame("prop-2", 6, 2, [texC, texA], withUvs: false, withSeeds: true, deferred: false, Matrix4x4.CreateTranslation(0, 1, 0), 3);

        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        OriginalFrameScene[] sources = [hero, prop1, prop2];
        var baseline = cache.Compose(null, ids, sources, hero, prop1, prop2);

        // 1. Transparency/color morph on prop1 only -> unchanged actors' SceneMaterial instances stay reused; UVs/Seeds/Textures/Outline/Flags reused.
        var prop1FadedMats = prop1.Surface.Snapshot.Materials
            .Select((m, idx) => idx == 1 ? m with { Diffuse = new Vector4(0.8f, 0.6f, 0.4f, 0.25f) } : m)
            .ToArray();
        var prop1Faded = Reframe(prop1, prop1.Surface.Snapshot.WithMaterials(prop1FadedMats));
        var afterAlpha = cache.Compose(baseline, ids, sources, hero, prop1Faded, prop2);

        AssertSceneParity(Combine(hero, prop1Faded, prop2), afterAlpha);
        Assert.NotSame(baseline.Surface.Snapshot.Materials, afterAlpha.Surface.Snapshot.Materials);
        Assert.Equal(0.25f, afterAlpha.Surface.Snapshot.Materials[3].Diffuse.W);
        Assert.Same(baseline.Surface.Snapshot.Materials[0], afterAlpha.Surface.Snapshot.Materials[0]);
        Assert.Same(baseline.Surface.Snapshot.Materials[1], afterAlpha.Surface.Snapshot.Materials[1]);
        Assert.Same(baseline.Surface.Snapshot.Materials[2], afterAlpha.Surface.Snapshot.Materials[2]);
        Assert.Same(baseline.Surface.Snapshot.Materials[4], afterAlpha.Surface.Snapshot.Materials[4]);
        Assert.Same(baseline.Surface.Snapshot.Materials[5], afterAlpha.Surface.Snapshot.Materials[5]);
        Assert.Same(baseline.Surface.Snapshot.Materials[6], afterAlpha.Surface.Snapshot.Materials[6]);
        Assert.Same(baseline.Surface.Snapshot.AdditionalUvs, afterAlpha.Surface.Snapshot.AdditionalUvs);
        Assert.Same(baseline.Surface.Snapshot.RawRadialSeeds, afterAlpha.Surface.Snapshot.RawRadialSeeds);
        Assert.Same(baseline.Surface.Snapshot.Textures, afterAlpha.Surface.Snapshot.Textures);
        Assert.Same(baseline.Outline!.Materials, afterAlpha.Outline!.Materials);
        for (int m = 0; m < baseline.Materials.Count; m++)
            Assert.Same(baseline.Materials[m], afterAlpha.Materials[m]);
        // Original baseline remains immutable.
        Assert.Equal(1f, baseline.Surface.Snapshot.Materials[3].Diffuse.W);

        // 2. Sphere binding & outline & frame material flag change on prop2 only.
        var prop2Bindings = prop2.Surface.Materials
            .Select((b, idx) => idx == 0 ? b with { SphereTexture = 1, SphereMode = OriginalSphereMode.Add } : b)
            .ToArray();
        var prop2Edges = prop2.Outline!.Materials
            .Select((e, idx) => idx == 1 ? e with { Width = 0.55f } : e)
            .ToArray();
        var prop2Flags = prop2.Materials
            .Select((f, idx) => idx == 0 ? f with { SurfaceEnabled = false } : f)
            .ToArray();
        var prop2Changed = new OriginalFrameScene(
            new OriginalStyleScene(prop2.Surface.Snapshot, prop2Bindings, prop2.Surface.Profile),
            new OriginalOutlineData(prop2.Outline.Vertices, prop2Edges),
            prop2.OutlineOptions, prop2.Options, prop2Flags);
        var afterMultiMat = cache.Compose(afterAlpha, ids, sources, hero, prop1Faded, prop2Changed);

        AssertSceneParity(Combine(hero, prop1Faded, prop2Changed), afterMultiMat);
        Assert.Same(afterAlpha.Surface.Snapshot.Materials, afterMultiMat.Surface.Snapshot.Materials);
        Assert.False(afterMultiMat.Materials[5].SurfaceEnabled);
        Assert.Same(afterAlpha.Materials[0], afterMultiMat.Materials[0]);
        Assert.Equal(0.55f, afterMultiMat.Outline!.Materials[6].Width);
        Assert.Same(afterAlpha.Outline!.Materials[0], afterMultiMat.Outline.Materials[0]);
        Assert.Equal(OriginalSphereMode.Add, afterMultiMat.Surface.Materials[5].SphereMode);
        Assert.Same(afterAlpha.Surface.Materials[0], afterMultiMat.Surface.Materials[0]);

        // 3. Texture instance replacement on prop1 -> invalidates topology and reflects new texture immediately.
        var texReplacement = Tex("tex-replacement", 99);
        var prop1NewTexSnap = new SceneSnapshot(
            prop1Faded.Surface.Snapshot.Vertices, prop1Faded.Surface.Snapshot.Indices,
            prop1Faded.Surface.Snapshot.Materials, [texB, texReplacement],
            prop1Faded.Surface.Snapshot.World, additionalUvs: prop1Faded.Surface.Snapshot.AdditionalUvs);
        var prop1NewTex = Reframe(prop1Faded, prop1NewTexSnap);
        var afterTexture = cache.Compose(afterMultiMat, ids, sources, hero, prop1NewTex, prop2Changed);

        AssertSceneParity(Combine(hero, prop1NewTex, prop2Changed), afterTexture);
        Assert.Contains(texReplacement, afterTexture.Surface.Snapshot.Textures);

        // 4. Visibility toggle (hide prop1) -> shrinks composition cleanly.
        var afterHide = cache.Compose(afterTexture, [ids[0], ids[2]], [sources[0], sources[2]], hero, prop2Changed);
        AssertSceneParity(Combine(hero, prop2Changed), afterHide);
        Assert.Equal(12, afterHide.Surface.Snapshot.VertexCount);
        Assert.Equal(4, afterHide.Surface.Snapshot.Materials.Count);

        // 5. Re-show / reorder prop list + add a new prop -> full rebuild matches Combine.
        var prop3 = BuildActorFrame("prop-3", 3, 1, [texA], withUvs: true, withSeeds: true, deferred: false, Matrix4x4.CreateTranslation(3, 0, 0), 5);
        Guid id3 = Guid.NewGuid();
        var afterAdd = cache.Compose(afterHide, [ids[2], ids[0], ids[1], id3], [sources[2], sources[0], sources[1], prop3],
            prop2Changed, hero, prop1NewTex, prop3);
        AssertSceneParity(Combine(prop2Changed, hero, prop1NewTex, prop3), afterAdd);
        Assert.Equal(21, afterAdd.Surface.Snapshot.VertexCount);

        cache.Clear();
    }

    [Fact]
    public void IncrementalSeedRecomputeCopiesUnchangedActorsAndInvalidatesOnlyChangedActors()
    {
        var cache = new Cache();
        var tex = Tex("shared", 7);
        var a = BuildActorFrame("a", 6, 1, [tex], withUvs: true, withSeeds: true, deferred: false, Matrix4x4.CreateRotationY(0.5f), 1);
        var b = BuildActorFrame("b", 6, 1, [tex], withUvs: true, withSeeds: true, deferred: false, Matrix4x4.CreateTranslation(4, 0, 0), 2);
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];
        OriginalFrameScene[] sources = [a, b];

        var baseline = cache.Compose(null, ids, sources, a, b);
        var movedB = Reframe(b, b.Surface.Snapshot.WithWorld(Matrix4x4.CreateRotationZ(0.4f) * Matrix4x4.CreateTranslation(4, 1, 0)));
        var updated = cache.Compose(baseline, ids, sources, a, movedB);

        AssertSceneParity(Combine(a, movedB), updated);
        Assert.NotSame(baseline.Surface.Snapshot.RawRadialSeeds, updated.Surface.Snapshot.RawRadialSeeds);
        Assert.Same(baseline.Surface.Snapshot.AdditionalUvs, updated.Surface.Snapshot.AdditionalUvs);
        Assert.Same(baseline.Surface.Snapshot.Materials, updated.Surface.Snapshot.Materials);
        Assert.Equal(
            MemoryMarshal.AsBytes(baseline.Surface.Snapshot.RawRadialSeeds!.Vertices.Slice(0, 6)).ToArray(),
            MemoryMarshal.AsBytes(updated.Surface.Snapshot.RawRadialSeeds!.Vertices.Slice(0, 6)).ToArray());
    }

    [Fact]
    public void MultiPropSceneAnimationSessionMatchesFullRebuildThroughBoneVisibilityAndAlphaMorphs()
    {
        var heroModel = BuildPmxWithAlphaMorph("hero", diffuseAlpha: 1f);
        var propModel = BuildPmxWithAlphaMorph("prop", diffuseAlpha: 0.9f);
        var heroRest = EditedMotionProjectTests.Rest(heroModel);
        var propRest = EditedMotionProjectTests.Rest(propModel);

        var layout = new SceneCompositionEditor();
        var heroItem = layout.Add("hero", "hero.pmx", ModelRole.Character, heroRest);
        var propItem = layout.Add("prop", "prop.pmx", ModelRole.Prop, propRest);

        var heroMotion = new VmdMotion
        {
            BoneKeyframes =
            [
                new() { BoneName = "root", Frame = 0, Translation = Vector3.Zero },
                new() { BoneName = "root", Frame = 10, Translation = new(1.5f, 0.5f, 0f) },
            ],
            MorphKeyframes =
            [
                new() { MorphName = "fade", Frame = 0, Weight = 0f },
                new() { MorphName = "fade", Frame = 10, Weight = 0.5f },
            ],
        };
        var propMotion = new VmdMotion
        {
            BoneKeyframes = [new() { BoneName = "root", Frame = 0 }],
            IkKeyframes =
            [
                new() { Frame = 0, Visible = true },
                new() { Frame = 5, Visible = false },
                new() { Frame = 8, Visible = true },
            ],
        };

        using var session = new SceneAnimationSession(
            layout.Objects,
            [
                new(heroItem.Id, heroModel, heroMotion, PhysicsEnabled: false),
                new(propItem.Id, propModel, propMotion, PhysicsEnabled: false),
            ],
            _ => throw new InvalidOperationException("No physics expected."),
            cancellationToken: CancellationToken.None);

        var frame0 = session.Seek(0, CancellationToken.None);
        Assert.Equal(6, frame0.Surface.Snapshot.VertexCount);
        Assert.Equal(1f, frame0.Surface.Snapshot.Materials[0].Diffuse.W, precision: 4);

        var frame2 = session.Seek(2, CancellationToken.None);
        Assert.Equal(6, frame2.Surface.Snapshot.VertexCount);
        Assert.True(frame2.Surface.Snapshot.Materials[0].Diffuse.W < 1f);

        var hiddenFrame = session.Seek(5, CancellationToken.None);
        Assert.Equal(3, hiddenFrame.Surface.Snapshot.VertexCount);

        var restoredFrame = session.Seek(8, CancellationToken.None);
        Assert.Equal(6, restoredFrame.Surface.Snapshot.VertexCount);
        Assert.True(restoredFrame.Surface.Snapshot.Materials[0].Diffuse.W < 0.7f);
    }

    [Fact]
    public void ManyPropSkeletalFrameSkipsMaterialAndUvRebuildsInSteadyState()
    {
        var cache = new Cache();
        const int propCount = 20;
        const int materialsPerProp = 8;
        var sharedTextures = Enumerable.Range(0, 6).Select(i => Tex($"shared-{i}", (byte)(i + 1))).ToArray();

        var hero = BuildActorFrame("hero", 6, 4, sharedTextures[..4], withUvs: true, withSeeds: false, deferred: false, Matrix4x4.Identity, 1);
        var props = Enumerable.Range(0, propCount)
            .Select(p => BuildActorFrame($"prop-{p}", 6, materialsPerProp, [sharedTextures[p % sharedTextures.Length], sharedTextures[(p + 1) % sharedTextures.Length]],
                withUvs: p % 2 == 0, withSeeds: false, deferred: false, Matrix4x4.CreateTranslation(p, 0, 0), p + 10))
            .ToArray();

        OriginalFrameScene[] all = [hero, .. props];
        Guid[] ids = all.Select(_ => Guid.NewGuid()).ToArray();

        var baseline = cache.Compose(null, ids, all, all);
        var warmMoved = DeformVertices(hero, 1f, newFlagsInstances: true);
        OriginalFrameScene[] warmInputs = [warmMoved, .. props];
        baseline = cache.Compose(baseline, ids, all, warmInputs);

        var nextMoved = DeformVertices(hero, 2f, newFlagsInstances: true);
        OriginalFrameScene[] nextInputs = [nextMoved, .. props];

        long allocStart = GC.GetAllocatedBytesForCurrentThread();
        var next = cache.Compose(baseline, ids, all, nextInputs);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocStart;

        _ = Combine(warmInputs);
        long fullRebuildStart = GC.GetAllocatedBytesForCurrentThread();
        var fullRebuild = Combine(nextInputs);
        long fullRebuildAllocated = GC.GetAllocatedBytesForCurrentThread() - fullRebuildStart;

        AssertSceneParity(fullRebuild, next);
        Assert.Same(baseline.Surface.Snapshot.Materials, next.Surface.Snapshot.Materials);
        Assert.Same(baseline.Surface.Snapshot.Textures, next.Surface.Snapshot.Textures);
        Assert.Same(baseline.Surface.Snapshot.AdditionalUvs, next.Surface.Snapshot.AdditionalUvs);
        Assert.Same(baseline.Outline!.Materials, next.Outline!.Materials);
        // Incremental steady-state composition only copies the changed chunk without rebuilding
        // 164 materials, texture dictionary, or multi-prop AdditionalUvs stream.
        Assert.InRange(allocated, 1L, 64 * 1024L);
        Assert.True(allocated < fullRebuildAllocated);
    }

    [Fact]
    public void SameClockSeekInvalidatesPosePreviewEvenWhenCompositionReusesFrame()
    {
        var model = SharedPhysicsFixture.Model();
        var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", "same-clock-seek-reuse.pmx", ModelRole.Character, SharedPhysicsFixture.Rest(model));
        var authored = new VmdMotion
        {
            BoneKeyframes =
            [
                new() { BoneName = "root", Frame = 0 },
                new() { BoneName = "root", Frame = 30, Translation = new(1f, 0, 0) },
            ],
        };
        var draft = new VmdMotion
        {
            BoneKeyframes =
            [
                new() { BoneName = "root", Frame = 0 },
                new() { BoneName = "root", Frame = 30, Translation = new(2f, 0, 0) },
            ],
        };
        var provider = new ProjectSettingsBackendProvider(
            (_, _) => new TrackingSharedBackend(),
            _ => new TrackingSharedBackend(),
            poseInitializationSubsteps: 0);

        using var project = new SceneProjectSession(
            layout,
            new(),
            [new(actor.Id, model, authored)],
            provider,
            initialFrame: 7,
            cancellationToken: CancellationToken.None);

        var frameBefore = project.Frame;
        var draftMap = new Dictionary<Guid, VmdMotion> { [actor.Id] = draft };
        var requestBefore = project.CapturePosePreviewRequest(draftMap);
        using var preview = project.CreatePosePreview(actor.Id, draft, CancellationToken.None);

        Assert.True(requestBefore.IsCurrent(project));
        Assert.True(preview.CanReusePosePreview(project));

        var frameAfter = project.Seek(project.CurrentFrame, CancellationToken.None);
        Assert.Same(frameBefore, frameAfter);

        Assert.False(requestBefore.IsCurrent(project));
        Assert.False(preview.CanReusePosePreview(project));

        var requestAfter = project.CapturePosePreviewRequest(draftMap);
        Assert.False(requestBefore.HasSameSource(requestAfter));
        Assert.True(requestAfter.IsCurrent(project));
    }

    private static SceneTexture Tex(string name, byte tag) => new(name, 1, 1, [tag, (byte)(tag + 1), (byte)(tag + 2), 255]);

    private static OriginalFrameScene BuildActorFrame(
        string prefix,
        int vertexCount,
        int materialCount,
        SceneTexture[] textures,
        bool withUvs,
        bool withSeeds,
        bool deferred,
        Matrix4x4 world,
        int seedTag)
    {
        int triIndices = vertexCount - (vertexCount % 3);
        var vertices = Enumerable.Range(0, vertexCount)
            .Select(i => new SceneVertex(new(seedTag + i * 0.2f, i % 3, (seedTag + i) * 0.1f), Vector3.Normalize(new(1, 2, 3)), new(i * 0.1f, 0.5f)))
            .ToArray();
        var indices = Enumerable.Range(0, triIndices).Select(i => (uint)i).ToArray();
        int perMat = (triIndices / 3 / materialCount) * 3;
        var materials = new SceneMaterial[materialCount];
        var bindings = new OriginalStyleMaterialBinding[materialCount];
        var edgeMaterials = new OriginalOutlineMaterial[materialCount];
        var flags = new OriginalFrameMaterialFlags[materialCount];
        int start = 0;
        for (int m = 0; m < materialCount; m++)
        {
            int count = m == materialCount - 1 ? triIndices - start : perMat;
            int diffTex = textures.Length > 0 ? m % textures.Length : -1;
            int toonTex = textures.Length > 1 ? (m + 1) % textures.Length : -1;
            int sphereTex = textures.Length > 0 ? (m + textures.Length - 1) % textures.Length : -1;
            materials[m] = new SceneMaterial($"{prefix}-m{m}", start, count, new(0.9f, 0.8f, 0.7f, 1f), new(0.2f), new(0.3f), 8f, diffTex, toonTex);
            bindings[m] = new OriginalStyleMaterialBinding(new(0.4f), sphereTex, sphereTex >= 0 ? OriginalSphereMode.Multiply : OriginalSphereMode.None);
            edgeMaterials[m] = new OriginalOutlineMaterial(0.15f + m * 0.02f, new(0.1f, 0.2f, 0.3f, 1f));
            flags[m] = new OriginalFrameMaterialFlags(true, true, true);
            start += count;
        }

        var uvs = withUvs
            ? new SceneAdditionalUvs(1, Enumerable.Range(0, vertexCount).Select(i => new SceneAdditionalUvVertex(new(seedTag, i, 2, 3), default, default, default)).ToArray())
            : null;
        var seeds = withSeeds
            ? new SceneRawRadialSeeds(Enumerable.Range(0, vertexCount).Select(i => new Vector3(seedTag + i, 1, 2)).ToArray())
            : null;

        var snapshot = new SceneSnapshot(vertices, indices, materials, textures, world, additionalUvs: uvs, rawRadialSeeds: seeds);
        if (deferred)
        {
            var skinBindings = Enumerable.Range(0, vertexCount)
                .Select(_ => new SceneSkinBinding(SceneSkinKind.Bdef1, 0, -1, -1, -1, Vector4.UnitX))
                .ToArray();
            var skinModel = new SceneSkinningModel(vertices, skinBindings, [], boneCount: 1);
            var deformation = new SceneDeformation(skinModel, [Matrix4x4.Identity], [], [], [], null, () => vertices);
            snapshot = snapshot.WithDeferredDeformation(deformation).WithWorld(world);
        }

        var outline = new OriginalOutlineData(
            vertices.Select((v, i) => new OriginalOutlineVertex(v.Position, v.Normal, OriginalOutlineVertexMode.Expand, 0.8f, i % materialCount)),
            edgeMaterials);
        return new OriginalFrameScene(
            new OriginalStyleScene(snapshot, bindings),
            outline,
            new OriginalOutlineOptions(Vector4.Zero, Matrix4x4.Identity, new(0, 9, -30), 30, 1),
            materials: flags);
    }

    private static OriginalFrameScene DeformVertices(OriginalFrameScene source, float delta, bool newFlagsInstances)
    {
        var snap = source.Surface.Snapshot;
        var verts = snap.Vertices.ToArray();
        for (int i = 0; i < verts.Length; i++)
            verts[i] = verts[i] with { Position = verts[i].Position + new Vector3(delta * 0.1f, delta * 0.05f, 0) };
        var flags = newFlagsInstances
            ? source.Materials.Select(f => f with { }).ToArray()
            : source.Materials.ToArray();
        return new OriginalFrameScene(
            new OriginalStyleScene(snap.WithDeformedVertices(verts), source.Surface.Materials, source.Surface.Profile),
            source.Outline,
            source.OutlineOptions,
            source.Options,
            flags,
            source.Lighting,
            source.EditorOverlay);
    }

    private static OriginalFrameScene DeformDeferred(OriginalFrameScene source, float delta, Matrix4x4 world)
    {
        var snap = source.Surface.Snapshot;
        var restVerts = snap.StaticVertexView.Vertices.ToArray();
        var skinBindings = Enumerable.Range(0, restVerts.Length)
            .Select(_ => new SceneSkinBinding(SceneSkinKind.Bdef1, 0, -1, -1, -1, Vector4.UnitX))
            .ToArray();
        var skinModel = new SceneSkinningModel(restVerts, skinBindings, [], boneCount: 1);
        var boneMatrix = Matrix4x4.CreateTranslation(delta, 0, 0);
        var deformation = new SceneDeformation(skinModel, [boneMatrix], [], [], [], null,
            () => restVerts.Select(v => v with { Position = Vector3.Transform(v.Position, boneMatrix) }).ToArray());
        var updated = snap.StaticVertexView.WithDeferredDeformation(deformation).WithWorld(world);
        return Reframe(source, updated);
    }

    private static OriginalFrameScene Reframe(OriginalFrameScene frame, SceneSnapshot snapshot)
        => new(new OriginalStyleScene(snapshot, frame.Surface.Materials, frame.Surface.Profile),
            frame.Outline, frame.OutlineOptions, frame.Options, frame.Materials, frame.Lighting, frame.EditorOverlay);

    private static OriginalFrameScene Combine(params OriginalFrameScene[] frames)
        => ProjectSceneAssembler.Combine(frames, Vector3.One, Vector3.UnitY);

    private static PmxModel BuildPmxWithAlphaMorph(string name, float diffuseAlpha)
    {
        var baseModel = EditedMotionProjectTests.Model();
        return new PmxModel
        {
            Header = baseModel.Header,
            Name = name,
            EnglishName = name,
            Comment = "",
            EnglishComment = "",
            Vertices = baseModel.Vertices,
            Faces = baseModel.Faces,
            Textures = [],
            Materials =
            [
                new PmxMaterial
                {
                    Name = $"{name}-mat",
                    EnglishName = $"{name}-mat",
                    Diffuse = new(0.8f, 0.8f, 0.8f, diffuseAlpha),
                    Specular = Vector3.Zero,
                    SpecularPower = 4f,
                    Ambient = new(0.3f),
                    DrawMode = PmxDrawModeFlags.CastSelfShadow | PmxDrawModeFlags.ReceiveSelfShadow,
                    EdgeColor = new(0, 0, 0, 1),
                    EdgeSize = 1f,
                    TextureIndex = -1,
                    SphereTextureIndex = -1,
                    SphereMode = PmxSphereMode.None,
                    ToonMode = PmxToonMode.Separate,
                    ToonTextureIndex = -1,
                    Memo = "",
                    FaceVertexCount = 3,
                }
            ],
            Bones = baseModel.Bones,
            Morphs =
            [
                new PmxMorph
                {
                    Name = "fade",
                    EnglishName = "fade",
                    ControlPanel = 1,
                    Type = PmxMorphType.Material,
                    MaterialOffsets =
                    [
                        new PmxMorph.MaterialOffset(
                            0,
                            PmxMaterialMorphOperation.Add,
                            new(0, 0, 0, -0.8f),
                            Vector3.Zero,
                            0f,
                            Vector3.Zero,
                            Vector4.Zero,
                            0f,
                            Vector4.Zero,
                            Vector4.Zero,
                            Vector4.Zero)
                    ],
                }
            ],
            DisplayFrames = [],
            Joints = [],
            RigidBodies = [],
            SoftBodies = [],
        };
    }

    private static byte[] Bytes<T>(ReadOnlySpan<T> values) where T : unmanaged => MemoryMarshal.AsBytes(values).ToArray();

    private static void AssertSceneParity(OriginalFrameScene expected, OriginalFrameScene actual)
    {
        Assert.Equal(Bytes(expected.Surface.Snapshot.Vertices), Bytes(actual.Surface.Snapshot.Vertices));
        Assert.Equal(Bytes<OriginalOutlineVertex>(expected.Outline!.Vertices.ToArray()), Bytes<OriginalOutlineVertex>(actual.Outline!.Vertices.ToArray()));
        Assert.Equal(expected.Surface.Snapshot.Indices.ToArray(), actual.Surface.Snapshot.Indices.ToArray());
        Assert.Equal(expected.Surface.Snapshot.Materials, actual.Surface.Snapshot.Materials);
        Assert.Equal(expected.Surface.Materials, actual.Surface.Materials);
        Assert.Equal(expected.Materials, actual.Materials);
        Assert.Equal(expected.Outline.Materials, actual.Outline.Materials);
        Assert.Equal(expected.Surface.Snapshot.Textures.ToArray(), actual.Surface.Snapshot.Textures.ToArray());

        if (expected.Surface.Snapshot.AdditionalUvs is { } expectedUvs)
        {
            Assert.NotNull(actual.Surface.Snapshot.AdditionalUvs);
            Assert.Equal(expectedUvs.ChannelCount, actual.Surface.Snapshot.AdditionalUvs!.ChannelCount);
            Assert.Equal(Bytes(expectedUvs.Vertices), Bytes(actual.Surface.Snapshot.AdditionalUvs.Vertices));
        }
        else
        {
            Assert.Null(actual.Surface.Snapshot.AdditionalUvs);
        }

        if (expected.Surface.Snapshot.RawRadialSeeds is { } expectedSeeds)
        {
            Assert.NotNull(actual.Surface.Snapshot.RawRadialSeeds);
            Assert.Equal(Bytes(expectedSeeds.Vertices), Bytes(actual.Surface.Snapshot.RawRadialSeeds!.Vertices));
        }
        else
        {
            Assert.Null(actual.Surface.Snapshot.RawRadialSeeds);
        }
    }
}
