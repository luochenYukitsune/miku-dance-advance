using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12GpuSkinningAcceptanceTests(ITestOutputHelper output)
{
    private const float MaxPositionError = 1e-4f;
    private const float MaxNormalAngleDegrees = 0.01f;
    private const float MaxUvError = 1e-6f;

    [Fact]
    public void GpuSkinningReadbackMatchesCpuReferenceAcrossAllKindsMorphsNonUniformScaleWorldAndMultiSlotReuse()
    {
        var model = BuildMultiKindSkinningModel(out var restSurface, out _);
        var (skin, sdefRot, qdefReal, qdefDual, morphs) = BuildRichDeformationPalettes(model);
        var world = Matrix4x4.CreateScale(1.3f, 0.75f, 1.15f)
            * Matrix4x4.CreateFromYawPitchRoll(0.35f, -0.25f, 0.4f)
            * Matrix4x4.CreateTranslation(2.5f, -1.25f, 3.0f);

        var deformation = new SceneDeformation(model, skin, sdefRot, qdefReal, qdefDual, morphs,
            () => EvalCpuReference(model, skin, sdefRot, qdefReal, qdefDual, morphs));

        // 1) Direct deferred deformation + non-identity SceneWorld + 3-slot cache & OnAbort test.
        var deferredSingle = restSurface.Snapshot.WithDeferredDeformation(deformation).WithWorld(world);

        using var runtime = new D3D12GpuRuntime();
        using var cache = new D3D12GpuCache(runtime);

        var singleRef = deformation.CopyReferenceVertices();

        SceneVertex[] ReadbackGpuVertices(ID3D12Resource vb, int count)
        {
            ulong bytes = (ulong)count * 32UL;
            using var readback = runtime.Device.CreateCommittedResource(HeapType.Readback,
                ResourceDescription.Buffer(bytes), ResourceStates.CopyDest);
            var ctx = runtime.Begin();
            try
            {
                ctx.Commands.ResourceBarrierTransition(vb, ResourceStates.VertexAndConstantBuffer, ResourceStates.CopySource);
                ctx.Commands.CopyBufferRegion(readback, 0, vb, 0, bytes);
                ctx.Commands.ResourceBarrierTransition(vb, ResourceStates.CopySource, ResourceStates.VertexAndConstantBuffer);
                var fence = runtime.Submit();
                runtime.Wait(fence);
            }
            catch
            {
                runtime.Abort();
                throw;
            }
            var mapped = readback.Map<SceneVertex>(0, count);
            try { return mapped.ToArray(); }
            finally { readback.Unmap(0); }
        }

        // Frame 0..2 populates all 3 ring slots; Frame 3..5 verifies unchanged skip without re-dispatch!
        long matBefore = SceneDeformation.CpuMaterializations;
        ID3D12Resource? vb0 = null;
        for (int f = 0; f < 6; f++)
        {
            _ = runtime.Begin();
            var vb = cache.SkinnedVertexBuffer("acc-skin", deferredSingle.StaticVertexView, out bool wrote);
            cache.GpuSkinning.Skin(vb, deferredSingle, wrote);
            if (f == 0) vb0 = vb;
            runtime.Wait(runtime.Submit());
        }
        Assert.Equal(matBefore, SceneDeformation.CpuMaterializations);
        Assert.True(cache.GpuSkinning.Statistics.SkippedSkinRanges >= 3,
            $"Expected unchanged slots to skip re-skinning; Skipped={cache.GpuSkinning.Statistics.SkippedSkinRanges}");
        Assert.Equal(3, cache.GpuSkinning.Statistics.SkinDispatches);

        var gpuSingle = ReadbackGpuVertices(vb0!, singleRef.Length);
        AssertVerticesMatch(singleRef, gpuSingle, 0, singleRef.Length, "DirectDeferred");

        // Test Abort rollback + replacement/dirty static overwrite -> must re-skin cleanly!
        var (pmx, vmd, pmxSurface, pmxOutline) = BuildFixturePmxScene(includeSoftBody: false);
        var staticPropVerts = new SceneVertex[]
        {
            new(new(-10, 4, -2), Vector3.Normalize(new(0, 1, 0.2f)), new(0.15f, 0.85f)),
            new(new(-9, 4, -2), Vector3.Normalize(new(0, 1, 0.2f)), new(0.25f, 0.85f)),
            new(new(-9, 5, -2), Vector3.Normalize(new(0, 1, 0.2f)), new(0.25f, 0.95f)),
        };
        var staticSnap = new SceneSnapshot(staticPropVerts, [0u, 1u, 2u],
            [new("prop", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)], camera: pmxSurface.Snapshot.Camera);
        var staticOutline = new OriginalOutlineData(
            staticPropVerts.Select(v => new OriginalOutlineVertex(v.Position, v.Normal, OriginalOutlineVertexMode.Expand, 1f, 0)),
            [new(1f, Vector4.One, false)]);
        var staticFrame = new OriginalFrameScene(
            new(staticSnap, [new(Vector3.One)], OriginalStyleProfile.SelectedReference),
            staticOutline,
            new(Vector4.Zero, Matrix4x4.Identity, Vector3.Zero, 30, 1f));
        var staticObj = new EditableSceneObject(Guid.NewGuid(), "prop", "prop", ModelRole.Prop, staticFrame,
            Vector3.Zero, Quaternion.Identity, 1f);
        var placedRot = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.45f, -0.3f, 0.25f));
        var actorObj = new EditableSceneObject(Guid.NewGuid(), "actor", "actor", ModelRole.Character,
            new(pmxSurface, pmxOutline, new(Vector4.Zero, Matrix4x4.Identity, Vector3.Zero, 30, 1f)),
            new(1.5f, -0.75f, 2.0f), placedRot, 1.35f);

        using var mixedSession = new SceneAnimationSession([staticObj, actorObj], [new SceneActorMotion(actorObj.Id, pmx, vmd)],
            _ => new BulletCharacterPhysicsBackend(new(0, -9.8f, 0), null),
            cancellationToken: TestContext.Current.CancellationToken);
        mixedSession.DeferVertexSkinningToRenderer = true;
        var mixedFrame = mixedSession.Seek(18, TestContext.Current.CancellationToken);
        var mixedSnap = mixedFrame.Surface.Snapshot;
        Assert.True(mixedSnap.HasDeferredDeformation);
        var placedRange = Assert.Single(mixedSnap.DeformationRanges);
        Assert.NotEqual(Matrix4x4.Identity, placedRange.World); // non-identity range.World!

        var mixedRef = mixedSnap.Vertices.ToArray();
        matBefore = SceneDeformation.CpuMaterializations;

        // Abort before submit must NOT mark slot cached; retry must produce exact GPU vertices.
        long dispatchesBeforeAbort = cache.GpuSkinning.Statistics.SkinDispatches;
        _ = runtime.Begin();
        var abortedVb = cache.SkinnedVertexBuffer("acc-mixed", mixedSnap.StaticVertexView, out bool abortWrote);
        cache.GpuSkinning.Skin(abortedVb, mixedSnap, abortWrote);
        runtime.Abort();
        Assert.Equal(dispatchesBeforeAbort + 1, cache.GpuSkinning.Statistics.SkinDispatches);

        _ = runtime.Begin();
        var retriedVb = cache.SkinnedVertexBuffer("acc-mixed", mixedSnap.StaticVertexView, out bool retryWrote);
        Assert.True(retryWrote, "Aborted upload must not leave slot entry reusable.");
        cache.GpuSkinning.Skin(retriedVb, mixedSnap, retryWrote);
        runtime.Wait(runtime.Submit());
        Assert.Equal(matBefore, SceneDeformation.CpuMaterializations);

        var gpuMixed = ReadbackGpuVertices(retriedVb, mixedRef.Length);
        AssertVerticesMatch(mixedRef, gpuMixed, 0, mixedRef.Length, "MixedPlacedRangeWorld");
    }

    private void AssertVerticesMatch(SceneVertex[] expected, SceneVertex[] actual, int start, int end, string label)
    {
        float maxPosErr = 0, maxNormDeg = 0, maxUvErr = 0;
        for (int i = start; i < end; i++)
        {
            var e = expected[i];
            var a = actual[i];
            float posErr = Vector3.Distance(e.Position, a.Position);
            float uvErr = Vector2.Distance(e.Uv, a.Uv);
            float angleDeg = 0;
            if (e.Normal.LengthSquared() > 1e-12f && a.Normal.LengthSquared() > 1e-12f)
            {
                float cos = Math.Clamp(Vector3.Dot(Vector3.Normalize(e.Normal), Vector3.Normalize(a.Normal)), -1f, 1f);
                angleDeg = MathF.Acos(cos) * (180f / MathF.PI);
            }
            else if (e.Normal.LengthSquared() != a.Normal.LengthSquared())
            {
                angleDeg = 180f;
            }
            maxPosErr = MathF.Max(maxPosErr, posErr);
            maxNormDeg = MathF.Max(maxNormDeg, angleDeg);
            maxUvErr = MathF.Max(maxUvErr, uvErr);

            Assert.True(posErr <= MaxPositionError, $"{label} vertex {i} position error {posErr} > {MaxPositionError}");
            Assert.True(angleDeg <= MaxNormalAngleDegrees, $"{label} vertex {i} normal angle error {angleDeg} > {MaxNormalAngleDegrees}");
            Assert.True(uvErr <= MaxUvError, $"{label} vertex {i} UV error {uvErr} > {MaxUvError}");
        }
        output.WriteLine($"{label} accuracy: maxPosErr={maxPosErr:E3}, maxNormalAngleDeg={maxNormDeg:E3}, maxUvErr={maxUvErr:E3}");
    }

    [Fact]
    public async Task DeferredModernPipelineZeroCpuMaterializationsAndSoftBodiesStayExplicitlyOnCpu()
    {
        var ct = TestContext.Current.CancellationToken;

        var (pmx, vmd, surface, outline) = BuildFixturePmxScene(includeSoftBody: false);
        var obj = new EditableSceneObject(Guid.NewGuid(), "char", "char", ModelRole.Character,
            new(surface, outline, new(Vector4.Zero, Matrix4x4.Identity, Vector3.Zero, 30, 1f)),
            Vector3.Zero, Quaternion.Identity, 1f);
        SceneActorMotion motion = new(obj.Id, pmx, vmd);
        using var anim = new SceneAnimationSession([obj], [motion],
            _ => new BulletCharacterPhysicsBackend(new(0, -9.8f, 0), null),
            lighting: null, cancellationToken: ct, initialFrame: 0);
        anim.DeferVertexSkinningToRenderer = true;
        var frame1 = anim.Seek(10, ct);
        Assert.True(frame1.Surface.Snapshot.HasDeferredDeformation);
        Assert.False(frame1.Surface.Snapshot.IsDeferredDeformationMaterialized);

        var styled = new OriginalFrameScene(
            frame1.Surface,
            frame1.Outline,
            new(new(0, 2, 0, 1), Matrix4x4.Identity, frame1.Surface.Snapshot.Camera.Eye, 30f, 1.0f),
            new(OriginalAlphaBlendMode.Normal, true, false, EnableFrustumCulling: true,
                GroundShadow: new(Vector3.Normalize(new(-0.3f, -1f, 0.2f)), new(0, 0, 0, 0.5f), false, false)),
            [
                new OriginalFrameMaterialFlags(true, false, true) { OutlineWidthScale = 1.5f },
                new OriginalFrameMaterialFlags(true, false, false) { CharacterOutlineStyle = new(0.8f, new(0.1f, 0.1f, 0.9f, 1f)), OutlineWidthScale = 1.25f },
            ],
            editorOverlay: new ViewportOverlaySnapshot([
                new ViewportOverlayLine(new(-1, 0), new(1, 0), new(1, 1, 0, 1), DeviceDepth: new(0.5f, 0.5f)),
            ]));

        long matBefore = SceneDeformation.CpuMaterializations;

        using (var session = new D3D12FrameSession())
        {
            var caps = session.QueryRayGiCapabilities();
            RayGiOptions giOpts = caps.HardwareAvailable
                ? new(LightingMode.HardwareRayTracing, Strength: 2.0f, SamplesPerPixel: 4, Seed: 7)
                : RayGiOptions.Off;

            var rendered = session.Capture(styled, 128, 96, rayGiOptions: giOpts);
            Assert.NotEmpty(rendered.Rgba);
            Assert.NotNull(session.GpuSkinningStatistics);
            Assert.True(session.GpuSkinningStatistics!.SkinDispatches > 0);
            Assert.True(session.GpuSkinningStatistics.OutlineDispatches > 0);
            if (caps.HardwareAvailable)
                Assert.True(session.GpuSkinningStatistics.RayGiTriangleDispatches > 0);

            var frame2 = anim.Seek(20, ct);
            var styled2 = new OriginalFrameScene(
                frame2.Surface, frame2.Outline, styled.OutlineOptions, styled.Options, styled.Materials, editorOverlay: styled.EditorOverlay);
            using var gpuOut = session.Render(styled2, 128, 96, rayGiOptions: giOpts);
            session.WaitForIdle();

            _ = ModernDirectionalShadowProjection.Fit(styled2.Surface.Snapshot);
            Assert.True(D3D12GpuSkinning.HaveSameBaseUvs(styled.Surface.Snapshot, styled.Surface.Snapshot));
        }

        using (var runtime = new D3D12GpuRuntime())
        using (var tempPass = new D3D12TemporalInputPass(runtime))
        {
            var opaqueStyled1 = MakeOpaqueSnapshot(styled);
            var frame2 = anim.Seek(22, ct);
            var opaqueStyled2 = MakeOpaqueSnapshot(frame2);
            var ctx = runtime.Begin();
            try
            {
                _ = tempPass.Record(ctx, opaqueStyled2.Surface.Snapshot, opaqueStyled1.Surface.Snapshot, 64, 48);
                runtime.Wait(runtime.Submit());
            }
            catch { runtime.Abort(); throw; }
        }

        var coordinator = new ExportCoordinator(
            token =>
            {
                var s = new SceneAnimationSession([obj], [motion],
                    _ => new BulletCharacterPhysicsBackend(new(0, -9.8f, 0), null), cancellationToken: token);
                s.DeferVertexSkinningToRenderer = true;
                return ValueTask.FromResult(s);
            },
            (req, token) => ValueTask.FromResult<IViewRenderer>(new D3D12ViewRenderer(req)));
        await using (var sink = new InMemoryFrameSink())
        {
            var exportReq = new ExportRequest(new OfflineFramePlan(128, 96, new OfflineFrameRate(30), 0, 1));
            var result = await coordinator.RunAsync(exportReq, sink, cancellationToken: ct);
            Assert.Equal(2, sink.Frames.Count);
            Assert.Equal(2, result.CompletedFrames);
        }

        Assert.Equal(matBefore, SceneDeformation.CpuMaterializations);

        // Verify soft-body models explicitly stay on CPU and report diagnostic!
        var (softPmx, softVmd, softSurface, softOutline) = BuildFixturePmxScene(includeSoftBody: true);
        var softObj = new EditableSceneObject(Guid.NewGuid(), "soft", "soft", ModelRole.Character,
            new(softSurface, softOutline, new(Vector4.Zero, Matrix4x4.Identity, Vector3.Zero, 30, 1f)),
            Vector3.Zero, Quaternion.Identity, 1f);
        using var softSession = new SceneAnimationSession([softObj], [new SceneActorMotion(softObj.Id, softPmx, softVmd)],
            _ => new BulletCharacterPhysicsBackend(new(0, -9.8f, 0), null),
            softPhysicsFactory: _ => new StubSoftBodyBackend(),
            cancellationToken: ct);
        softSession.DeferVertexSkinningToRenderer = true;
        var softFrame = softSession.Seek(5, ct);
        Assert.False(softFrame.Surface.Snapshot.HasDeferredDeformation);
        var actorState = Assert.Single(softSession.Actors);
        Assert.Contains(actorState.Diagnostics,
            d => d.Contains("soft bodies", StringComparison.OrdinalIgnoreCase) && d.Contains("CPU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GpuSkinnedRenderMatchesCpuReferenceRenderPixelByPixelWithinMeasuredTolerance()
    {
        var ct = TestContext.Current.CancellationToken;
        var (pmx, vmd, surface, outline) = BuildFixturePmxScene(includeSoftBody: false);
        var obj = new EditableSceneObject(Guid.NewGuid(), "render-compare", "render-compare", ModelRole.Character,
            new(surface, outline, new(Vector4.Zero, Matrix4x4.Identity, Vector3.Zero, 30, 1f)),
            Vector3.Zero, Quaternion.Identity, 1f);
        SceneActorMotion motion = new(obj.Id, pmx, vmd);

        using var cpuAnim = new SceneAnimationSession([obj], [motion],
            _ => new BulletCharacterPhysicsBackend(new(0, -9.8f, 0), null), cancellationToken: ct);
        cpuAnim.DeferVertexSkinningToRenderer = false;

        using var gpuAnim = new SceneAnimationSession([obj], [motion],
            _ => new BulletCharacterPhysicsBackend(new(0, -9.8f, 0), null), cancellationToken: ct);
        gpuAnim.DeferVertexSkinningToRenderer = true;

        var cpuBase = cpuAnim.Seek(15, ct);
        var gpuBase = gpuAnim.Seek(15, ct);
        Assert.False(cpuBase.Surface.Snapshot.HasDeferredDeformation);
        Assert.True(gpuBase.Surface.Snapshot.HasDeferredDeformation);

        OriginalFrameScene Wrap(OriginalFrameScene src) => new(
            src.Surface,
            src.Outline,
            new(new(0, 2, 0, 1), Matrix4x4.Identity, new(0, 2, -14), 30f, 1.0f),
            new(OriginalAlphaBlendMode.Normal, true, false,
                GroundShadow: new(Vector3.Normalize(new(-0.3f, -1f, 0.2f)), new(0, 0, 0, 0.5f), false, false)),
            [
                new OriginalFrameMaterialFlags(true, false, false) { OutlineWidthScale = 1.5f },
                new OriginalFrameMaterialFlags(true, false, false) { CharacterOutlineStyle = new(1.2f, new(0.85f, 0.1f, 0.1f, 1f)), OutlineWidthScale = 1.0f },
            ]);

        var cpuFrame = Wrap(cpuBase);
        var gpuFrame = Wrap(gpuBase);

        long matBefore = SceneDeformation.CpuMaterializations;
        using var cpuSession = new D3D12FrameSession();
        using var gpuSession = new D3D12FrameSession();

        var cpuImg = cpuSession.Capture(cpuFrame, 192, 144);
        var gpuImg = gpuSession.Capture(gpuFrame, 192, 144);
        Assert.Equal(matBefore, SceneDeformation.CpuMaterializations);

        Assert.Equal(cpuImg.Rgba.Length, gpuImg.Rgba.Length);
        int maxDiff = 0;
        long sumDiff = 0;
        int nonzeroChannels = 0;
        for (int i = 0; i < cpuImg.Rgba.Length; i++)
        {
            int d = Math.Abs(cpuImg.Rgba[i] - gpuImg.Rgba[i]);
            if (d > 0) nonzeroChannels++;
            if (d > maxDiff) maxDiff = d;
            sumDiff += d;
        }
        double meanDiff = (double)sumDiff / cpuImg.Rgba.Length;
        output.WriteLine($"Raster+Outline Pixel Diff: maxDiff={maxDiff}, meanDiff={meanDiff:F6}, nonZeroChannels={nonzeroChannels}/{cpuImg.Rgba.Length}");

        Assert.True(maxDiff <= 1, $"Measured max channel pixel diff {maxDiff} exceeded tight tolerance 1.");
        Assert.True(meanDiff <= 0.01, $"Measured mean channel pixel diff {meanDiff:F6} exceeded 0.01.");

        var caps = gpuSession.QueryRayGiCapabilities();
        if (caps.HardwareAvailable)
        {
            var gi = new RayGiOptions(LightingMode.HardwareRayTracing, Strength: 4.0f, SamplesPerPixel: 16, Seed: 42);
            var cpuGi = cpuSession.Capture(cpuFrame, 192, 144, rayGiOptions: gi);
            var gpuGi = gpuSession.Capture(gpuFrame, 192, 144, rayGiOptions: gi);
            Assert.Equal(matBefore, SceneDeformation.CpuMaterializations);
            int giMax = 0;
            long giSum = 0;
            for (int i = 0; i < cpuGi.Rgba.Length; i++)
            {
                int d = Math.Abs(cpuGi.Rgba[i] - gpuGi.Rgba[i]);
                if (d > giMax) giMax = d;
                giSum += d;
            }
            double giMean = (double)giSum / cpuGi.Rgba.Length;
            output.WriteLine($"HardwareRayGi Pixel Diff: maxDiff={giMax}, meanDiff={giMean:F6}");
            Assert.True(giMax <= 2, $"Measured GI max channel diff {giMax} exceeded tolerance 2.");
            Assert.True(giMean <= 0.05, $"Measured GI mean channel diff {giMean:F6} exceeded 0.05.");
        }
    }

    private sealed class InMemoryFrameSink : IFrameSink
    {
        public List<byte[]> Frames { get; } = [];
        public ValueTask BeginAsync(ExportRequest request, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(OfflineFrameSample sample, FrameBufferLease frame, CancellationToken cancellationToken = default)
        { Frames.Add(frame.Pixels.ToArray()); return ValueTask.CompletedTask; }
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask AbortAsync(Exception? error, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubSoftBodyBackend : ISoftBodyBackend
    {
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(IReadOnlyList<SoftBodyDefinition> definitions, IReadOnlyList<PhysicsBodyDefinition> colliders) { }
        public void Reset(ReadOnlySpan<Vector3> globalSkinnedPositions, ReadOnlySpan<Matrix4x4> rigidWorlds) { }
        public void Step(float seconds, ReadOnlySpan<Vector3> globalSkinnedPositions, ReadOnlySpan<Matrix4x4> rigidWorlds) { }
        public IReadOnlyList<SoftBodySample> Sample() => [new([0, 1, 2, 3],
            [new(-3.5f, 0f, 0f), new(-0.5f, 0f, 0f), new(-0.5f, 4f, 0f), new(-3.5f, 4f, 0f)],
            [-Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ])];
        public void SetGravity(Vector3 gravity) { }
        public void Dispose() { }
    }

    private static OriginalFrameScene MakeOpaqueSnapshot(OriginalFrameScene frame)
    {
        var s = frame.Surface.Snapshot;
        var mats = s.Materials.Select(m => m with { Diffuse = new(m.Diffuse.X, m.Diffuse.Y, m.Diffuse.Z, 1f) }).ToArray();
        var opaqueSnap = s.WithMaterials(mats);
        return new(new(opaqueSnap, frame.Surface.Materials, frame.Surface.Profile),
            frame.Outline, frame.OutlineOptions, null, frame.Materials);
    }

    private static SceneSkinningModel BuildMultiKindSkinningModel(out OriginalStyleScene surface, out OriginalOutlineData outline)
    {
        var tex = new SceneTexture("tex", 2, 2, [255, 255, 255, 255, 200, 100, 50, 255, 50, 200, 100, 255, 255, 255, 255, 255]);
        SceneVertex[] verts =
        [
            new(new(1.0f, 0.5f, -0.3f), Vector3.Normalize(new(0.2f, 0.9f, 0.3f)), new(0.1f, 0.2f)),
            new(new(-1.2f, 1.4f, 0.7f), Vector3.Normalize(new(-0.3f, 0.6f, 0.7f)), new(0.4f, 0.5f)),
            new(new(0.4f, -0.8f, 1.1f), Vector3.Normalize(new(0.5f, 0.5f, -0.7f)), new(0.7f, 0.3f)),
            new(new(0.9f, 1.8f, 0.2f), Vector3.Normalize(new(0.1f, 0.4f, 0.9f)), new(0.2f, 0.8f)),
            new(new(-0.7f, 2.2f, -0.6f), Vector3.Normalize(new(-0.4f, 0.8f, -0.4f)), new(0.6f, 0.9f)),
            new(new(0.0f, 0.0f, 0.0f), Vector3.Zero, new(0.0f, 0.0f)),
        ];
        SceneSkinBinding[] bindings =
        [
            new(SceneSkinKind.Bdef1, 0, -1, -1, -1, new(1f, 0, 0, 0)),
            new(SceneSkinKind.Bdef2, 0, 1, -1, -1, new(0.35f, 0.65f, 0, 0)),
            new(SceneSkinKind.Bdef4, 0, 1, 2, -1, new(0.2f, 0.5f, 0.3f, 0)),
            new(SceneSkinKind.Sdef, 3, 4, -1, -1, new(0.4f, 0.6f, 0, 0)),
            new(SceneSkinKind.Qdef, 3, 4, -1, -1, new(0.3f, 0.7f, 0, 0)),
            new(SceneSkinKind.Bdef2, 1, 2, -1, -1, new(0.5f, 0.5f, 0, 0)),
        ];
        SceneSdefBinding[] sdef = new SceneSdefBinding[verts.Length];
        sdef[3] = new(new(0.5f, 1.5f, 0.1f), new(0.4f, 1.2f, 0.0f), new(0.7f, 1.8f, 0.3f));

        surface = new(new(verts, [0u, 1u, 2u, 3u, 4u, 5u],
            [new("mat", 0, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 1, 0)], [tex], camera: SceneCamera.Default),
            [new(Vector3.One)], OriginalStyleProfile.SelectedReference);
        outline = new(verts.Select((v, idx) => new OriginalOutlineVertex(v.Position, v.Normal,
            idx == 4 ? OriginalOutlineVertexMode.Inward : OriginalOutlineVertexMode.Expand, 1f, 0)),
            [new(1f, Vector4.One, true)]);
        return new(verts, bindings, sdef, 5);
    }

    private static (Matrix4x4[] Skin, Quaternion[] SdefRot, Quaternion[] QdefReal, Quaternion[] QdefDual, SceneMorphDeltas Morphs)
        BuildRichDeformationPalettes(SceneSkinningModel model)
    {
        var q0 = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.3f, -0.4f, 0.2f));
        var q1 = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-0.5f, 0.2f, 0.6f));
        var q2 = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.8f, 0.1f, -0.3f));
        var q3 = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(1, 2, -1)), 0.7f));
        var q4 = -Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(-1, 1, 2)), 1.9f));

        Matrix4x4[] skin =
        [
            Matrix4x4.CreateScale(1.7f, 0.45f, 1.3f) * Matrix4x4.CreateFromQuaternion(q0) * Matrix4x4.CreateTranslation(0.4f, -0.2f, 0.6f),
            Matrix4x4.CreateScale(0.6f, 1.9f, 0.85f) * Matrix4x4.CreateFromQuaternion(q1) * Matrix4x4.CreateTranslation(-0.5f, 0.8f, -0.3f),
            Matrix4x4.CreateScale(1.2f, 1.1f, 0.5f) * Matrix4x4.CreateFromQuaternion(q2) * Matrix4x4.CreateTranslation(0.1f, 0.3f, 1.2f),
            Matrix4x4.CreateFromQuaternion(q3) * Matrix4x4.CreateTranslation(0.6f, 0.4f, -0.5f),
            Matrix4x4.CreateFromQuaternion(q4) * Matrix4x4.CreateTranslation(-0.8f, 1.1f, 0.7f),
        ];
        Quaternion[] sdefRot = [q0, q1, q2, q3, q4];
        Quaternion[] qdefReal = [q0, q1, q2, q3, q4];
        Quaternion[] qdefDual = new Quaternion[5];
        for (int b = 0; b < 5; b++)
            qdefDual[b] = (new Quaternion(skin[b].Translation, 0) * qdefReal[b]) * 0.5f;

        var morphs = new SceneMorphDeltas(model.VertexCount, [1, 3, 4],
            [new(0.25f, -0.15f, 0.35f), new(-0.1f, 0.4f, 0.2f), new(0.12f, -0.22f, -0.18f)],
            [new(0.125f, -0.05f), new(0.2f, 0.3f), new(-0.15f, 0.08f)]);
        return (skin, sdefRot, qdefReal, qdefDual, morphs);
    }

    private static SceneVertex[] EvalCpuReference(SceneSkinningModel model, Matrix4x4[] skin,
        Quaternion[] sdefRot, Quaternion[] qdefReal, Quaternion[] qdefDual, SceneMorphDeltas? morphs)
    {
        var result = new SceneVertex[model.VertexCount];
        var rest = model.RestVertices;
        var bindings = model.Bindings;
        var sdef = model.Sdef;
        for (int i = 0; i < result.Length; i++)
        {
            var v = rest[i];
            if (morphs is not null)
            {
                int m = morphs.Indices.BinarySearch(i);
                if (m >= 0) v = v with { Position = v.Position + morphs.Positions[m], Uv = v.Uv + morphs.Uvs[m] };
            }
            var b = bindings[i];
            Vector3 pos = Vector3.Zero, normal = Vector3.Zero;
            if (b.Kind == SceneSkinKind.Qdef)
            {
                Quaternion refQ = default, br = default, bd = default;
                bool hasRef = false;
                for (int s = 0; s < 4; s++)
                {
                    float w = s switch { 0 => b.Weights.X, 1 => b.Weights.Y, 2 => b.Weights.Z, _ => b.Weights.W };
                    if (w == 0) continue;
                    int bone = s switch { 0 => b.Bone0, 1 => b.Bone1, 2 => b.Bone2, _ => b.Bone3 };
                    if (!hasRef) { refQ = qdefReal[bone]; hasRef = true; }
                    if (Quaternion.Dot(refQ, qdefReal[bone]) < 0) w = -w;
                    br += qdefReal[bone] * w; bd += qdefDual[bone] * w;
                }
                float inv = 1f / MathF.Sqrt(br.LengthSquared());
                br *= inv; bd *= inv; bd -= br * Quaternion.Dot(br, bd);
                var t = (bd * Quaternion.Conjugate(br)) * 2f;
                pos = Vector3.Transform(v.Position, br) + new Vector3(t.X, t.Y, t.Z);
                normal = Vector3.Transform(v.Normal, br);
            }
            else if (b.Kind == SceneSkinKind.Sdef)
            {
                int b0 = b.Weights.X > 0 ? b.Bone0 : b.Bone1;
                int b1 = b.Weights.Y > 0 ? b.Bone1 : b.Bone0;
                var rot = Quaternion.Normalize(Quaternion.Slerp(sdefRot[b0], sdefRot[b1], b.Weights.Y));
                var sb = sdef[i];
                var off = Vector3.Transform(sb.Anchor0, skin[b0]) * b.Weights.X + Vector3.Transform(sb.Anchor1, skin[b1]) * b.Weights.Y;
                pos = Vector3.Transform(v.Position - sb.C, rot) + off;
                normal = Vector3.Transform(v.Normal, rot);
            }
            else
            {
                int slots = b.Kind == SceneSkinKind.Bdef1 ? 1 : b.Kind == SceneSkinKind.Bdef2 ? 2 : 4;
                for (int s = 0; s < slots; s++)
                {
                    float w = s switch { 0 => b.Weights.X, 1 => b.Weights.Y, 2 => b.Weights.Z, _ => b.Weights.W };
                    if (w == 0) continue;
                    int bone = s switch { 0 => b.Bone0, 1 => b.Bone1, 2 => b.Bone2, _ => b.Bone3 };
                    pos += Vector3.Transform(v.Position, skin[bone]) * w;
                    normal += Vector3.TransformNormal(v.Normal, skin[bone]) * w;
                }
            }
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal)
                : v.Normal.LengthSquared() > 1e-12f ? Vector3.Normalize(v.Normal) : Vector3.Zero;
            result[i] = v with { Position = pos, Normal = normal };
        }
        return result;
    }

    private static (PmxModel Model, VmdMotion Motion, OriginalStyleScene Surface, OriginalOutlineData Outline) BuildFixturePmxScene(bool includeSoftBody)
    {
        var vertices = new PmxVertex[]
        {
            new() { Position = new(-3.5f, 0f, 0f), Normal = -Vector3.UnitZ, Uv = new(0, 1), AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef2, BoneIndices = [0, 1, -1, -1], BoneWeights = [0.5f, 0.5f, 0, 0], EdgeScale = 1 },
            new() { Position = new(-0.5f, 0f, 0f), Normal = -Vector3.UnitZ, Uv = new(1, 1), AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef4, BoneIndices = [0, 1, 2, -1], BoneWeights = [0.4f, 0.4f, 0.2f, 0], EdgeScale = 1 },
            new() { Position = new(-0.5f, 4f, 0f), Normal = -Vector3.UnitZ, Uv = new(1, 0), AdditionalUvs = [], WeightType = PmxVertexWeightType.Sdef, BoneIndices = [0, 1, -1, -1], BoneWeights = [0.35f, 0.65f, 0, 0], SdefC = new(-1f, 2f, 0f), SdefR0 = new(-2f, 1f, 0f), SdefR1 = new(0f, 3f, 0f), EdgeScale = 1 },
            new() { Position = new(-3.5f, 4f, 0f), Normal = -Vector3.UnitZ, Uv = new(0, 0), AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [1, -1, -1, -1], BoneWeights = [1f, 0, 0, 0], EdgeScale = 1 },

            new() { Position = new(-1.5f, 0.2f, 0.004f), Normal = -Vector3.UnitZ, Uv = new(0, 1), AdditionalUvs = [], WeightType = PmxVertexWeightType.Qdef, BoneIndices = [1, 2, -1, -1], BoneWeights = [0.3f, 0.7f, 0, 0], EdgeScale = 1 },
            new() { Position = new(1.5f, 0.2f, 0.004f), Normal = -Vector3.UnitZ, Uv = new(1, 1), AdditionalUvs = [], WeightType = PmxVertexWeightType.Qdef, BoneIndices = [1, 2, -1, -1], BoneWeights = [0.6f, 0.4f, 0, 0], EdgeScale = 1 },
            new() { Position = new(1.5f, 4.2f, 0.004f), Normal = -Vector3.UnitZ, Uv = new(1, 0), AdditionalUvs = [], WeightType = PmxVertexWeightType.Qdef, BoneIndices = [1, 2, -1, -1], BoneWeights = [0.5f, 0.5f, 0, 0], EdgeScale = 1 },
            new() { Position = new(-1.5f, 4.2f, 0.004f), Normal = -Vector3.UnitZ, Uv = new(0, 0), AdditionalUvs = [], WeightType = PmxVertexWeightType.Qdef, BoneIndices = [1, 2, -1, -1], BoneWeights = [0.8f, 0.2f, 0, 0], EdgeScale = 1 },
        };
        var model = new PmxModel
        {
            Header = new()
            {
                Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 4, TextureIndexSize = 4, MaterialIndexSize = 4, BoneIndexSize = 4, MorphIndexSize = 4, RigidBodyIndexSize = 4
            },
            Name = "gpu-skin-fixture", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = vertices,
            Faces = [new(0, 2, 1), new(0, 3, 2), new(4, 6, 5), new(4, 7, 6)],
            Textures = [],
            Materials =
            [
                new() { Name = "mat0", EnglishName = "", Diffuse = new(0.8f, 0.75f, 0.7f, 1), Specular = Vector3.Zero, SpecularPower = 1, Ambient = new(0.2f), DrawMode = PmxDrawModeFlags.DrawEdge | PmxDrawModeFlags.GroundShadow, EdgeColor = new(0.1f, 0.1f, 0.1f, 1), EdgeSize = 1f, TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 6 },
                new() { Name = "mat1", EnglishName = "", Diffuse = new(0.3f, 0.65f, 0.9f, 1), Specular = Vector3.Zero, SpecularPower = 1, Ambient = new(0.2f), DrawMode = PmxDrawModeFlags.DrawEdge | PmxDrawModeFlags.BothFaces, EdgeColor = new(0.2f, 0.2f, 0.2f, 1), EdgeSize = 1f, TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 6 },
            ],
            Bones =
            [
                new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate },
                new() { Name = "upper", EnglishName = "", Position = new(0, 2, 0), ParentIndex = 0, DeformDepth = 1, Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate },
                new() { Name = "tip", EnglishName = "", Position = new(2, 2, 0), ParentIndex = 1, DeformDepth = 2, Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate },
            ],
            Morphs =
            [
                new() { Name = "vmorph", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Position, PositionOffsets = [new PmxMorph.PositionOffset(2, new(0.3f, 0.5f, -0.2f)), new PmxMorph.PositionOffset(5, new(-0.25f, 0.2f, 0.1f))] },
                new() { Name = "uvmorph", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Uv, UvOffsets = [new PmxMorph.UvOffset(1, new(0.2f, -0.1f, 0, 0)), new PmxMorph.UvOffset(6, new(-0.1f, 0.25f, 0, 0))] },
            ],
            DisplayFrames = [],
            RigidBodies = [],
            Joints = [],
            SoftBodies = includeSoftBody
                ? [new() { Name = "soft", EnglishName = "", Type = PmxSoftBodyType.TriMesh, MaterialIndex = 0, Group = 0, CollisionGroup = 0xFFFF, Flags = PmxSoftBodyFlags.None, BLinkLength = 1, ClusterCount = 1, TotalMass = 1, CollisionMargin = 0.05f, AeroModel = 0, Config = default, ClusterConfig = default, Iterations = new(1, 1, 0, 0), MaterialConfig = new(1, 1, 1), Anchors = [], PinVertexIndices = [] }]
                : [],
        };

        var motion = new VmdMotion
        {
            BoneKeyframes =
            [
                new() { BoneName = "root", Frame = 0 },
                new() { BoneName = "root", Frame = 30, Translation = new(0.4f, 0.2f, -0.3f), Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.25f, -0.15f, 0.1f)) },
                new() { BoneName = "upper", Frame = 0 },
                new() { BoneName = "upper", Frame = 30, Translation = new(-0.2f, 0.1f, 0.2f), Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-0.4f, 0.3f, 0.2f)) },
                new() { BoneName = "tip", Frame = 0 },
                new() { BoneName = "tip", Frame = 30, Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(1, 1, 1)), 0.8f)) },
            ],
            MorphKeyframes =
            [
                new() { MorphName = "vmorph", Frame = 0, Weight = 0f },
                new() { MorphName = "vmorph", Frame = 30, Weight = 1f },
                new() { MorphName = "uvmorph", Frame = 0, Weight = 0f },
                new() { MorphName = "uvmorph", Frame = 30, Weight = 1f },
            ],
        };

        var adapted = PmxOriginalStyleAdapter.FromRestPose(model).Scene;
        var cam = new SceneCamera(new(0, 2, -14), new(0, 2, 0), Vector3.UnitY, 9f, 0.1f, 50f);
        var surface = new OriginalStyleScene(adapted.Snapshot.WithCamera(cam).WithLighting(Vector3.Normalize(new(0.4f, 0.8f, -0.6f))),
            adapted.Materials, adapted.Profile);
        var outline = PmxOriginalOutlineAdapter.FromRestPose(model);
        return (model, motion, surface, outline);
    }

    [Fact]
    public void DeferredGpuSkinningReusesAndInvalidatesNativeShadowCacheWithoutCpuMaterialization()
    {
        var ct = TestContext.Current.CancellationToken;
        var (pmx, vmd, surface, outline) = BuildFixturePmxScene(includeSoftBody: false);
        var obj = new EditableSceneObject(Guid.NewGuid(), "shadow-skin", "shadow-skin", ModelRole.Character,
            new(surface, outline, new(Vector4.Zero, Matrix4x4.Identity, Vector3.Zero, 30, 1f)),
            Vector3.Zero, Quaternion.Identity, 1f);
        SceneActorMotion motion = new(obj.Id, pmx, vmd);

        using var cpuAnim = new SceneAnimationSession([obj], [motion],
            _ => new BulletCharacterPhysicsBackend(new(0, -9.8f, 0), null), cancellationToken: ct);
        cpuAnim.DeferVertexSkinningToRenderer = false;

        using var gpuAnim = new SceneAnimationSession([obj], [motion],
            _ => new BulletCharacterPhysicsBackend(new(0, -9.8f, 0), null), cancellationToken: ct);
        gpuAnim.DeferVertexSkinningToRenderer = true;

        var point = new NativeSceneLight(Guid.NewGuid(), "pt", SceneLightKind.Point,
            new(0f, 2f, -5f), Quaternion.Identity, new(1f, 0.9f, 0.8f), 4f, 20f, CastShadows: true);
        var dir = new NativeSceneLight(Guid.NewGuid(), "sun", SceneLightKind.Directional,
            new(0f, 6f, -5f), Quaternion.CreateFromYawPitchRoll(0.3f, 0.4f, 0f), new(0.8f, 0.7f, 0.6f), 1.5f, CastShadows: true);
        var lighting = new SceneLightingSnapshot([point, dir]);

        OriginalFrameScene Wrap(OriginalFrameScene src) => new(
            src.Surface,
            src.Outline,
            new(new(0, 2, 0, 1), Matrix4x4.Identity, new(0, 2, -14), 30f, 1.0f),
            new(OriginalAlphaBlendMode.Normal, true, false, EnableFrustumCulling: true),
            [
                new OriginalFrameMaterialFlags(true, false, false),
                new OriginalFrameMaterialFlags(true, false, false),
            ],
            lighting: lighting);

        var gpuF1 = Wrap(gpuAnim.Seek(10, ct));
        var gpuF2 = Wrap(gpuAnim.Seek(25, ct));
        var cpuF2 = Wrap(cpuAnim.Seek(25, ct));
        Assert.True(gpuF1.Surface.Snapshot.HasDeferredDeformation);
        Assert.True(gpuF2.Surface.Snapshot.HasDeferredDeformation);
        Assert.False(cpuF2.Surface.Snapshot.HasDeferredDeformation);

        long matBefore = SceneDeformation.CpuMaterializations;
        using var cpuSession = new D3D12FrameSession();
        using var gpuSession = new D3D12FrameSession();

        _ = gpuSession.Capture(gpuF1, 160, 120);
        var s1 = gpuSession.NativeLightStatistics!;
        Assert.Equal(matBefore, SceneDeformation.CpuMaterializations);
        Assert.True(s1.ShadowDrawCalls > 0);
        Assert.Equal(0, s1.ShadowReusedFaces);

        _ = gpuSession.Capture(gpuF1, 160, 120);
        var s2 = gpuSession.NativeLightStatistics!;
        Assert.Equal(matBefore, SceneDeformation.CpuMaterializations);
        Assert.True(s2.ShadowReusedFaces > 0);
        Assert.Equal(0, s2.ShadowDrawCalls);

        var gpuImg2 = gpuSession.Capture(gpuF2, 160, 120);
        var s3 = gpuSession.NativeLightStatistics!;
        Assert.Equal(matBefore, SceneDeformation.CpuMaterializations);
        Assert.Equal(0, s3.ShadowReusedFaces);
        Assert.True(s3.ShadowDrawCalls > 0);

        var cpuImg2 = cpuSession.Capture(cpuF2, 160, 120);
        Assert.Equal(matBefore, SceneDeformation.CpuMaterializations);

        int maxDiff = 0;
        long sumDiff = 0;
        for (int i = 0; i < cpuImg2.Rgba.Length; i++)
        {
            int d = Math.Abs(cpuImg2.Rgba[i] - gpuImg2.Rgba[i]);
            if (d > maxDiff) maxDiff = d;
            sumDiff += d;
        }
        double meanDiff = (double)sumDiff / cpuImg2.Rgba.Length;
        output.WriteLine($"NativeLight Directional+Point Shadow Pixel Diff: maxDiff={maxDiff}, meanDiff={meanDiff:F6}");
        Assert.True(maxDiff <= 2, $"NativeLight shadow pixel maxDiff {maxDiff} > 2.");
        Assert.True(meanDiff <= 0.05, $"NativeLight shadow pixel meanDiff {meanDiff:F6} > 0.05.");
    }

    [Fact]
    public void DetachedScrubSeekPreservesDeferredVertexSkinningAndAvoidsCpuMaterializations()
    {
        var ct = TestContext.Current.CancellationToken;
        var (pmx, vmd, surface, outline) = BuildFixturePmxScene(includeSoftBody: false);
        var rest = new OriginalFrameScene(surface, outline,
            new(Vector4.One, Matrix4x4.Identity, surface.Snapshot.Camera.Eye, 30, 1),
            materials: [new OriginalFrameMaterialFlags(true, false, false), new OriginalFrameMaterialFlags(true, false, false)]);
        var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, rest);
        var motion = new SceneActorMotion(actor.Id, pmx, vmd, PhysicsEnabled: false);
        var provider = new ProjectSettingsBackendProvider(
            (s, _) => new BulletCharacterPhysicsBackend(s.Gravity, null),
            s => new BulletCharacterPhysicsBackend(s.Gravity, null),
            poseInitializationSubsteps: 12);

        using var project = new SceneProjectSession(layout, new SceneLightingEditor(), [motion], provider, cancellationToken: ct);
        long matBefore = SceneDeformation.CpuMaterializations;
        project.DeferVertexSkinningToRenderer = true;
        project.Seek(5, ct);
        Assert.True(project.Frame.Surface.Snapshot.HasDeferredDeformation);
        Assert.Equal(matBefore, SceneDeformation.CpuMaterializations);

        var req = project.TryCaptureSeekRequest(20);
        Assert.NotNull(req);
        using var prepared = req!.Prepare(ct);
        Assert.True(project.TryPublishSeek(prepared));
        Assert.True(project.Frame.Surface.Snapshot.HasDeferredDeformation);
        Assert.False(project.Frame.Surface.Snapshot.IsDeferredDeformationMaterialized);
        Assert.Equal(matBefore, SceneDeformation.CpuMaterializations);
    }
}
