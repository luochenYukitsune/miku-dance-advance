using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.D3DCompiler;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class ClusteredLightingAndPathTracingMathTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DepthSlicingCoversNearToFarMonotonicallyAndRoundTrips(bool orthographic, bool leftHanded)
    {
        float near = 0.25f, far = 250f;
        int gridZ = 24;
        float prevFar = near;
        for (int z = 0; z < gridZ; z++)
        {
            var (dNear, dFar) = ClusteredLightingMath.SliceDepthBounds(z, near, far, gridZ, orthographic);
            Assert.True(dFar > dNear, $"Slice {z}: {dNear}..{dFar}");
            Assert.InRange(MathF.Abs(dNear - prevFar), 0f, 1e-4f);
            float mid = orthographic ? 0.5f * (dNear + dFar) : MathF.Sqrt(dNear * dFar);
            Assert.Equal(z, ClusteredLightingMath.SliceFromViewDepth(mid, near, far, gridZ, orthographic));
            prevFar = dFar;
        }
        Assert.InRange(MathF.Abs(prevFar - far), 0f, 1e-3f);
        Assert.Equal(0, ClusteredLightingMath.SliceFromViewDepth(near * 0.5f, near, far, gridZ, orthographic));
        Assert.Equal(gridZ - 1, ClusteredLightingMath.SliceFromViewDepth(far * 2f, near, far, gridZ, orthographic));
        _ = leftHanded;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void BuiltClusterAabbsTightlyContainSampledFrustumFragments(bool orthographic, bool leftHanded)
    {
        var camera = new SceneCamera(
            Eye: new Vector3(1.5f, 3.0f, leftHanded ? -15f : 15f),
            Target: new Vector3(0.5f, 2.0f, 0f),
            Up: Vector3.UnitY,
            OrthographicHeight: 16f,
            Near: 0.5f,
            Far: 100f,
            VerticalFovDegrees: orthographic ? null : 45f,
            LeftHanded: leftHanded)
        {
            ProjectionJitterPixels = new Vector2(0.25f, -0.15f),
        };

        int width = 1280, height = 720;
        var grid = ClusterGridDimensions.Default;
        var aabbs = ClusteredLightingMath.BuildAllClusterAabbs(grid, camera, width, height);
        Assert.Equal(grid.TotalClusters, aabbs.Length);

        var view = ClusteredLightingMath.CreateViewMatrix(camera);
        var proj = ClusteredLightingMath.CreateProjectionMatrix(camera, width, height);
        Assert.True(Matrix4x4.Invert(view * proj, out var invViewProj));

        // Sample interior points across the frustum and confirm each fragment's view-space position
        // falls inside the AABB of the cluster chosen by ClusterIndexForFragment.
        foreach (int cx in new[] { 0, 5, 11, 15 })
        foreach (int cy in new[] { 0, 3, 6, 8 })
        foreach (int cz in new[] { 0, 5, 12, 23 })
        {
            float px = (cx + 0.5f) / grid.X * width;
            float py = (cy + 0.5f) / grid.Y * height;
            float ndcX = (px / width) * 2f - 1f;
            float ndcY = 1f - (py / height) * 2f;

            var (dNear, dFar) = ClusteredLightingMath.SliceDepthBounds(cz, camera.Near, camera.Far, grid.Z, orthographic);
            float midDepth = orthographic ? 0.5f * (dNear + dFar) : MathF.Sqrt(dNear * dFar);
            float viewZ = leftHanded ? midDepth : -midDepth;

            // Project a point on that viewZ plane to clip space to recover exact NDC Z.
            var clipProbe = Vector4.Transform(new Vector4(0, 0, viewZ, 1), proj);
            float ndcZ = clipProbe.Z / clipProbe.W;
            var worldH = Vector4.Transform(new Vector4(ndcX, ndcY, ndcZ, 1), invViewProj);
            Vector3 worldPos = new Vector3(worldH.X, worldH.Y, worldH.Z) / worldH.W;
            Vector3 viewPos = Vector3.Transform(worldPos, view);

            int clusterIdx = ClusteredLightingMath.ClusterIndexForFragment(
                px, py, worldPos, width, height, grid, view, camera.Near, camera.Far, leftHanded, orthographic);
            Assert.Equal(grid.ClusterIndex(cx, cy, cz), clusterIdx);

            var aabb = aabbs[clusterIdx];
            const float eps = 1e-3f;
            Assert.True(viewPos.X >= aabb.Min.X - eps && viewPos.X <= aabb.Max.X + eps, $"X {viewPos.X} outside [{aabb.Min.X}, {aabb.Max.X}]");
            Assert.True(viewPos.Y >= aabb.Min.Y - eps && viewPos.Y <= aabb.Max.Y + eps, $"Y {viewPos.Y} outside [{aabb.Min.Y}, {aabb.Max.Y}]");
            Assert.True(viewPos.Z >= aabb.Min.Z - eps && viewPos.Z <= aabb.Max.Z + eps, $"Z {viewPos.Z} outside [{aabb.Min.Z}, {aabb.Max.Z}]");
        }
    }

    [Fact]
    public void ClusteredLightCullingHasZeroFalseNegativesAndCullsOutOfRangeLights()
    {
        var camera = new SceneCamera(new Vector3(0, 2, -20), new Vector3(0, 2, 0), Vector3.UnitY, 20f, 0.5f, 80f, VerticalFovDegrees: 45f, LeftHanded: true);
        int width = 640, height = 360;
        var grid = ClusterGridDimensions.Default;
        var view = ClusteredLightingMath.CreateViewMatrix(camera);
        var aabbs = ClusteredLightingMath.BuildAllClusterAabbs(grid, camera, width, height);

        var directional = new NativeSceneLight(Guid.NewGuid(), "dir", SceneLightKind.Directional, Vector3.Zero, Quaternion.Identity, Vector3.One, 1f, 10f);
        var nearLeftPoint = new NativeSceneLight(Guid.NewGuid(), "pt-left", SceneLightKind.Point, new Vector3(-6, 2, -10), Quaternion.Identity, Vector3.One, 2f, 4f);
        var farRightSpot = new NativeSceneLight(Guid.NewGuid(), "spot-right", SceneLightKind.Spot, new Vector3(8, 3, 15),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), Vector3.One, 3f, 8f, 0.2f, 0.45f);
        var centerArea = new NativeSceneLight(Guid.NewGuid(), "area", SceneLightKind.Area, new Vector3(0, 2, 0),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), Vector3.One, 2f, 5f, AreaSize: new Vector2(3f, 2f));

        NativeSceneLight[] nativeLights = [directional, nearLeftPoint, farRightSpot, centerArea];
        ClusterLightEntry[] entries = nativeLights.Select(ClusteredLightingMath.FromNativeLight).ToArray();
        var (ranges, indices) = ClusteredLightingMath.CullLights(grid, aabbs, view, entries);

        int dirClusters = 0, pointClusters = 0, spotClusters = 0, areaClusters = 0;
        for (int c = 0; c < grid.TotalClusters; c++)
        {
            var r = ranges[c];
            for (uint k = 0; k < r.Count; k++)
            {
                uint li = indices[r.Offset + k];
                if (li == 0) dirClusters++;
                else if (li == 1) pointClusters++;
                else if (li == 2) spotClusters++;
                else if (li == 3) areaClusters++;
            }
        }

        // Directional hits all clusters; local lights hit a strict non-empty subset.
        Assert.Equal(grid.TotalClusters, dirClusters);
        Assert.InRange(pointClusters, 1, grid.TotalClusters / 2);
        Assert.InRange(spotClusters, 1, grid.TotalClusters / 2);
        Assert.InRange(areaClusters, 1, grid.TotalClusters / 2);

        // Zero false negatives: sample world positions across the scene and verify every affecting light is in its cluster.
        var proj = ClusteredLightingMath.CreateProjectionMatrix(camera, width, height);
        var vp = view * proj;
        for (int ix = -8; ix <= 8; ix += 2)
        for (int iy = 0; iy <= 4; iy += 2)
        for (int iz = -12; iz <= 18; iz += 3)
        {
            var wp = new Vector3(ix, iy, iz);
            var clip = Vector4.Transform(new Vector4(wp, 1), vp);
            if (clip.W <= 0) continue;
            float ndcX = clip.X / clip.W, ndcY = clip.Y / clip.W, ndcZ = clip.Z / clip.W;
            if (ndcX < -1 || ndcX > 1 || ndcY < -1 || ndcY > 1 || ndcZ < 0 || ndcZ > 1) continue;
            float px = (ndcX * 0.5f + 0.5f) * width;
            float py = (0.5f - ndcY * 0.5f) * height;
            int clusterIdx = ClusteredLightingMath.ClusterIndexForFragment(
                px, py, wp, width, height, grid, view, camera.Near, camera.Far, camera.LeftHanded, false);

            var r = ranges[clusterIdx];
            var assigned = new HashSet<uint>();
            for (uint k = 0; k < r.Count; k++) assigned.Add(indices[r.Offset + k]);

            for (int li = 0; li < entries.Length; li++)
            {
                float dist = Vector3.Distance(wp, entries[li].WorldPosition);
                bool withinBound = entries[li].Kind == SceneLightKind.Directional
                    || dist <= entries[li].Range + entries[li].AreaExtent;
                if (entries[li].Kind == SceneLightKind.Spot && withinBound && dist > 1e-4f)
                {
                    float cosA = Vector3.Dot(Vector3.Normalize(wp - entries[li].WorldPosition), entries[li].WorldDirection);
                    withinBound = cosA >= MathF.Cos(entries[li].OuterConeRadians);
                }
                if (withinBound)
                    Assert.Contains((uint)li, assigned);
            }
        }
    }

    [Fact]
    public void PathTracingHistoryKeyDetectsEveryResetTriggerAndRetainsStationaryFrames()
    {
        var baseFrame = CreateUnitTestFrame();
        var options = new PathTracingOptions(
            ShadingMode: PathTracingShadingMode.ToonHybrid,
            SamplesPerFrame: 2,
            MaxBounces: 4,
            ProgressiveAccumulation: true,
            MaxAccumulatedFrames: 128,
            FireflyClamp: 10f,
            Denoiser: RayGiDenoiser.TemporalSpatial,
            UseHardwareRayTracing: true);

        var key0 = PathTracingHistoryKey.Create(baseFrame, 640, 360, options);
        Assert.Equal("FirstFrame", PathTracingHistoryKey.EvaluateResetReason(null, key0, explicitReset: false));
        Assert.Null(PathTracingHistoryKey.EvaluateResetReason(key0, key0, explicitReset: false));

        // Changing SamplesPerFrame / MaxAccumulatedFrames / Denoiser alone does NOT reset history.
        var tweakedNonReset = options with { SamplesPerFrame = 4, MaxAccumulatedFrames = 256, Denoiser = RayGiDenoiser.Spatial };
        var keyNonReset = PathTracingHistoryKey.Create(baseFrame, 640, 360, tweakedNonReset);
        Assert.Null(PathTracingHistoryKey.EvaluateResetReason(key0, keyNonReset, explicitReset: false));

        // Explicit reset & Progressive disabled
        Assert.Equal("ExplicitReset", PathTracingHistoryKey.EvaluateResetReason(key0, key0, explicitReset: true));
        var keyNoProg = PathTracingHistoryKey.Create(baseFrame, 640, 360, options with { ProgressiveAccumulation = false });
        Assert.Equal("ProgressiveDisabled", PathTracingHistoryKey.EvaluateResetReason(key0, keyNoProg, explicitReset: false));

        // Resize, Generation, CameraCut
        Assert.Equal("Resized", PathTracingHistoryKey.EvaluateResetReason(key0, PathTracingHistoryKey.Create(baseFrame, 641, 360, options), false));
        Assert.Equal("GenerationChanged", PathTracingHistoryKey.EvaluateResetReason(key0, PathTracingHistoryKey.Create(baseFrame, 640, 360, options with { HistoryGeneration = 1 }), false));
        Assert.Equal("CameraCut", PathTracingHistoryKey.EvaluateResetReason(key0, PathTracingHistoryKey.Create(baseFrame, 640, 360, options with { CameraCutKey = 2 }), false));

        // Structural path-tracing parameter changes
        Assert.Equal("SettingsChanged", PathTracingHistoryKey.EvaluateResetReason(key0, PathTracingHistoryKey.Create(baseFrame, 640, 360, options with { ShadingMode = PathTracingShadingMode.Physical }), false));
        Assert.Equal("SettingsChanged", PathTracingHistoryKey.EvaluateResetReason(key0, PathTracingHistoryKey.Create(baseFrame, 640, 360, options with { MaxBounces = 6 }), false));
        Assert.Equal("SettingsChanged", PathTracingHistoryKey.EvaluateResetReason(key0, PathTracingHistoryKey.Create(baseFrame, 640, 360, options with { FireflyClamp = 20f }), false));
        Assert.Equal("SettingsChanged", PathTracingHistoryKey.EvaluateResetReason(key0, PathTracingHistoryKey.Create(baseFrame, 640, 360, options with { UseHardwareRayTracing = false }), false));

        // Camera change
        var movedCamera = baseFrame.Surface.Snapshot.Camera with { Eye = new Vector3(1, 2, -10) };
        var movedCamFrame = new OriginalFrameScene(baseFrame.Surface,
            options: baseFrame.Options! with { CameraOverride = movedCamera }, materials: baseFrame.Materials, lighting: baseFrame.Lighting);
        Assert.Equal("CameraChanged", PathTracingHistoryKey.EvaluateResetReason(key0, PathTracingHistoryKey.Create(movedCamFrame, 640, 360, options), false));

        // Geometry change (deformed vertex)
        var verts = baseFrame.Surface.Snapshot.Vertices.ToArray();
        verts[0] = verts[0] with { Position = verts[0].Position + new Vector3(0.1f, 0, 0) };
        var deformed = baseFrame.Surface.Snapshot.WithDeformedVertices(verts);
        var deformedFrame = new OriginalFrameScene(new OriginalStyleScene(deformed, baseFrame.Surface.Materials, baseFrame.Surface.Profile),
            options: baseFrame.Options, materials: baseFrame.Materials, lighting: baseFrame.Lighting);
        Assert.Equal("SceneGeometryChanged", PathTracingHistoryKey.EvaluateResetReason(key0, PathTracingHistoryKey.Create(deformedFrame, 640, 360, options), false));

        // Material change
        var tintedMats = baseFrame.Surface.Snapshot.Materials.Select(m => m with { Diffuse = new Vector4(0.2f, 0.8f, 0.3f, 1f) }).ToArray();
        var tintedFrame = new OriginalFrameScene(new OriginalStyleScene(baseFrame.Surface.Snapshot.WithMaterials(tintedMats), baseFrame.Surface.Materials, baseFrame.Surface.Profile),
            options: baseFrame.Options, materials: baseFrame.Materials, lighting: baseFrame.Lighting);
        Assert.Equal("SceneMaterialChanged", PathTracingHistoryKey.EvaluateResetReason(key0, PathTracingHistoryKey.Create(tintedFrame, 640, 360, options), false));

        // Lighting change
        var litFrame = new OriginalFrameScene(baseFrame.Surface, options: baseFrame.Options, materials: baseFrame.Materials,
            lighting: new SceneLightingSnapshot([new NativeSceneLight(Guid.NewGuid(), "L", SceneLightKind.Point, new Vector3(0, 2, -2), Quaternion.Identity, Vector3.One, 2f, 10f)]));
        Assert.Equal("LightingChanged", PathTracingHistoryKey.EvaluateResetReason(key0, PathTracingHistoryKey.Create(litFrame, 640, 360, options), false));

        // Background change
        var bgFrame = new OriginalFrameScene(baseFrame.Surface,
            options: baseFrame.Options! with { BackgroundColor = new Vector4(0.5f, 0.5f, 0.5f, 1f) }, materials: baseFrame.Materials, lighting: baseFrame.Lighting);
        Assert.Equal("BackgroundChanged", PathTracingHistoryKey.EvaluateResetReason(key0, PathTracingHistoryKey.Create(bgFrame, 640, 360, options), false));
    }

    [Fact]
    public void ClusteredComputeAndPixelShaderVariantsCompileCleanlyWithFxc()
    {
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness;
        var ct = TestContext.Current.CancellationToken;
        Assert.NotEmpty(D3D12FxcShaderCache.Compile(D3D12ClusteredLightPass.BuildSource, "CSBuildClusters", "clustered-build.hlsl", "cs_5_1", flags, cancellationToken: ct));
        Assert.NotEmpty(D3D12FxcShaderCache.Compile(D3D12ClusteredLightPass.CullSource, "CSCullLights", "clustered-cull.hlsl", "cs_5_1", flags, cancellationToken: ct));

        // Verify all clustered forward pixel shader families compile cleanly via FXC ps_5_1.
        Assert.NotEmpty(OriginalFrameShaders.CompileNative("PSSurfaceNative", vertex: false, clustered: true));
        Assert.NotEmpty(OriginalFrameShaders.CompileNative("PSSurfaceGiNative", vertex: false, clustered: true));
        Assert.NotEmpty(OriginalFrameShaders.CompileTextureMorph("PSSurfaceNative", native: true, gi: false, clustered: true));
        Assert.NotEmpty(OriginalFrameShaders.CompileSubTexture("PSSurfaceNative", vertex: false, native: true, gi: false, clustered: true));
        Assert.NotEmpty(OriginalFrameShaders.CompileCharacterToon("PSSurfaceNative", native: true, gi: false, textureMorph: false, subTexture: false, clustered: true));
        Assert.NotEmpty(OriginalFrameShaders.CompileCharacterDirectPalette("PSSurfaceNative", gi: false, textureMorph: false, subTexture: false, characterToon: true, clustered: true));
        Assert.NotEmpty(D3D12StyleShaderAssembler.CompilePixel(
            "stylized-test", "PSSurfaceNative", D3D12StyleShaderAssembler.DefaultStylizedHostModule,
            D3D12StyleShaderAssembler.DefaultStylizedHostModule.EffectiveHooks,
            D3D12StyleGeometryRequirements.None,
            nativeLights: true, gi: false, textureMorph: false, subTexture: false,
            characterToon: true, preserveDirectLightPalette: true, clusteredLights: true));

        // Path tracing resolve shaders compile cleanly with FXC.
        Assert.NotEmpty(D3D12FxcShaderCache.Compile(D3D12PathTracingShaders.ResolveSource, "VSResolve", "pathtrace-resolve.hlsl", "vs_5_1", flags, cancellationToken: ct));
        Assert.NotEmpty(D3D12FxcShaderCache.Compile(D3D12PathTracingShaders.ResolveSource, "PSResolve", "pathtrace-resolve.hlsl", "ps_5_1", flags, cancellationToken: ct));
    }

    [Fact]
    public void PathTracingComputeAndDenoiserShadersCompileCleanlyWithDxc()
    {
        string? dxcPath;
        try { dxcPath = DxcShaderCompiler.ResolveCompilerPath(); }
        catch { return; }
        Assert.False(string.IsNullOrWhiteSpace(dxcPath));

        var compiler = new DxcShaderCompiler(dxcPath);
        foreach (bool textureMorph in new[] { false, true })
        foreach (bool subTexture in new[] { false, true })
        {
            var sw = compiler.CompileCompute(
                D3D12PathTracingShaders.TraceShaderSource(hardware: false, textureMorph, subTexture),
                "CSPathTrace", "cs_6_0");
            Assert.NotEmpty(sw.Dxil);

            var hw = compiler.CompileCompute(
                D3D12PathTracingShaders.TraceShaderSource(hardware: true, textureMorph, subTexture),
                "CSPathTrace", "cs_6_5",
                new Dictionary<string, string?> { ["PT_HARDWARE"] = "1" });
            Assert.NotEmpty(hw.Dxil);
        }

        var denoise = compiler.CompileCompute(D3D12PathTracingShaders.DenoiserSource, "CSDenoise", "cs_6_0");
        Assert.NotEmpty(denoise.Dxil);
    }

    private static OriginalFrameScene CreateUnitTestFrame()
    {
        SceneVertex[] vertices =
        [
            new(new(-1, -1, 0), -Vector3.UnitZ, new(0, 1)),
            new(new(1, -1, 0), -Vector3.UnitZ, new(1, 1)),
            new(new(0, 1, 0), -Vector3.UnitZ, new(0.5f, 0)),
        ];
        uint[] indices = [0, 1, 2];
        var material = new SceneMaterial("tri", 0, 3, new Vector4(0.8f, 0.7f, 0.6f, 1f), Vector3.One * 0.2f, Vector3.One * 0.4f, 16f);
        var snapshot = new SceneSnapshot(vertices, indices, [material],
            camera: new SceneCamera(new Vector3(0, 0, -5), Vector3.Zero, Vector3.UnitY, 4f, 0.1f, 50f, VerticalFovDegrees: 45f));
        var style = new OriginalStyleScene(snapshot, [new OriginalStyleMaterialBinding(ToonShadowColor: new Vector3(0.5f, 0.5f, 0.6f))], OriginalStyleProfile.SelectedReference);
        var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false);
        return new OriginalFrameScene(style, options: options, materials: [new OriginalFrameMaterialFlags(true, true, false)]);
    }
}
