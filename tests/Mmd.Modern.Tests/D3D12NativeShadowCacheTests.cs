using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class NativeShadowCacheContractTests
{
    [Fact]
    public void DrawKeyIsPaddingFreeAndVersionsTrackOnlyTouchedMaterials()
    {
        Assert.Equal(120, Unsafe.SizeOf<D3D12NativeShadowFaceCache.Draw>());
        var stage = QuadScene(Vector3.Zero, 1, "stage");
        var composed = Combine(stage, QuadScene(new(0, 0, 4), 1, "char"));
        var tracker = new D3D12NativeShadowCasterVersions();
        long[] v0 = tracker.Update(composed).ToArray();
        Assert.Equal(2, v0.Length);
        Assert.Equal(v0, tracker.Update(composed.WithCamera(SceneCamera.Default with { Eye = new(3, 5, -9) })).ToArray());
        var charMoved = PatchRange(composed, 4, 4, new(.2f, 0, 0));
        long[] v1 = tracker.Update(charMoved).ToArray();
        Assert.Equal(v0[0], v1[0]);
        Assert.True(v1[1] > v0[1]);
        var fullDeform = composed.WithDeformedVertices(composed.Vertices.ToArray());
        long[] v2 = tracker.Update(fullDeform).ToArray();
        Assert.True(v2[0] > v1[0] && v2[1] > v1[1]);
        tracker.Reset();
        long[] v3 = tracker.Update(composed).ToArray();
        Assert.True(v3[0] > v2[0] && v3[1] > v2[1]);
    }

    [Fact]
    public void FaceCacheDistinguishesSignedZeroAndObjectReferences()
    {
        var pso = new object();
        var draw = new D3D12NativeShadowFaceCache.Draw(0, 0, 6, 0, 1, Matrix4x4.Identity, Vector4.One, new(.5f, 1, 1, 0));
        var face = new D3D12NativeShadowFaceCache.Face(Matrix4x4.Identity, Matrix4x4.Identity, null, [draw], [pso, null, null]);
        Assert.True(face.Matches(Matrix4x4.Identity, Matrix4x4.Identity, null, [draw], [pso, null, null]));
        var negZero = Matrix4x4.Identity; negZero.M41 = -0f;
        Assert.False(face.Matches(negZero, Matrix4x4.Identity, null, [draw], [pso, null, null]));
        Assert.False(face.Matches(Matrix4x4.Identity, negZero, null, [draw], [pso, null, null]));
        var drawNegZero = draw with { SphereRotation = negZero };
        Assert.False(face.Matches(Matrix4x4.Identity, Matrix4x4.Identity, null, [drawNegZero], [pso, null, null]));
        Assert.False(face.Matches(Matrix4x4.Identity, Matrix4x4.Identity, new object(), [draw], [pso, null, null]));
        Assert.False(face.Matches(Matrix4x4.Identity, Matrix4x4.Identity, null, [draw], [new object(), null, null]));
    }

    private static SceneSnapshot QuadScene(Vector3 offset, float half, string name) => new(
        [new(offset + new Vector3(-half, -half, 0), Vector3.UnitZ, Vector2.Zero),
         new(offset + new Vector3(half, -half, 0), Vector3.UnitZ, new(1, 0)),
         new(offset + new Vector3(half, half, 0), Vector3.UnitZ, Vector2.One),
         new(offset + new Vector3(-half, half, 0), Vector3.UnitZ, new(0, 1))],
        [0u, 1u, 2u, 0u, 2u, 3u],
        [new(name, 0, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 1)],
        camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 4, .1f, 20));

    private static SceneSnapshot Combine(SceneSnapshot a, SceneSnapshot b)
    {
        var vertices = a.Vertices.ToArray().Concat(b.Vertices.ToArray()).ToArray();
        var indices = a.Indices.ToArray().Concat(b.Indices.ToArray().Select(i => i + (uint)a.VertexCount)).ToArray();
        var materials = a.Materials.Concat(b.Materials.Select(m => m with { StartIndex = m.StartIndex + a.Indices.Length })).ToArray();
        return new(vertices, indices, materials, camera: a.Camera);
    }

    private static SceneSnapshot PatchRange(SceneSnapshot scene, int start, int count, Vector3 delta)
    {
        var method = typeof(SceneSnapshot).GetMethod("WithComposedVertices", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var writer = new DeltaWriter(delta);
        var callback = Delegate.CreateDelegate(method.GetParameters()[1].ParameterType, writer, typeof(DeltaWriter).GetMethod(nameof(DeltaWriter.Write))!);
        return (SceneSnapshot)method.Invoke(scene, [new[] { (start, count) }, callback])!;
    }
    private sealed class DeltaWriter(Vector3 delta)
    {
        public void Write(int _, Span<SceneVertex> destination)
        { for (int i = 0; i < destination.Length; i++) destination[i] = destination[i] with { Position = destination[i].Position + delta }; }
    }
}

/// <summary>Per-face caster culling and exact face reuse must reproduce the full clear-and-redraw atlas bit for bit.</summary>
[Trait("Category", "Gpu")]
public sealed class D3D12NativeShadowCacheTests
{
    private const int Width = 160, Height = 120, CharacterStart = 4096;

    [Fact]
    public void CulledAndReusedFacesMatchTheFullRedrawAcrossCameraCharacterLightMaterialAndAtlasChanges()
    {
        using var reference = new D3D12FrameSession(); using var optimized = new D3D12FrameSession();
        reference.OriginalRecorder.NativeShadowReuse = false; reference.OriginalRecorder.NativeShadowCulling = false;
        reference.OriginalRecorder.NativeShadowDiagnostics = optimized.OriginalRecorder.NativeShadowDiagnostics = true;
        var log = new StringBuilder("frame\tstep\tfaces\tcasterDraws\tfullRedrawDraws\tdraws\tculled\treusedFaces\treusedDraws\tfullRedrawGpuMs\tgpuMs\n");
        var draws = new Dictionary<string, List<D3D12NativeLightStatistics>>();
        int index = 0;
        foreach (var (step, frame) in Sequence())
        {
            AssertStepMatch(reference, optimized, frame, index, step, log, draws);
            index++;
        }
        string? directory = Environment.GetEnvironmentVariable("MDA_NATIVE_SHADOW_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "native-shadow-sequence.tsv"), $"# {optimized.AdapterName}; shared GPU, timings are indicative only\n" + log);
        }
        // Every face sees at most part of the stage, so culling always removes some caster draws.
        Assert.All(draws.Values.SelectMany(value => value), value => Assert.True(value.ShadowCulledDraws > 0));
        // Each of the three recording slots draws its own atlas once; afterwards an unchanged scene records nothing.
        Assert.All(draws["static"].Skip(3).Concat(draws["camera"]), value =>
        { Assert.Equal(0, value.ShadowDrawCalls); Assert.Equal(value.ShadowFaces, value.ShadowReusedFaces); });
        // A moving character redraws only faces it reaches (or left); faces of the static stage stay reused.
        Assert.All(draws["character"], value => { Assert.True(value.ShadowDrawCalls > 0); Assert.True(value.ShadowReusedFaces > 0); });
        // Steady-state reuse resumes after three frames of any edit across all slots.
        foreach (string steady in new[] { "light-edit", "spot-edit", "directional-edit", "area-edit", "material-alpha", "shadow-double-sided", "world-transform", "atlas-shrink" })
        {
            Assert.Equal(0, draws[steady][^1].ShadowDrawCalls);
            Assert.Equal(draws[steady][^1].ShadowFaces, draws[steady][^1].ShadowReusedFaces);
        }
        // Rotating SphereRotation invalidates the crate's faces even without vertex/light changes; repeating the final angle hits cache.
        Assert.True(draws["sphere-rotation"][1].ShadowDrawCalls > 0);
        Assert.True(draws["sphere-rotation"][2].ShadowDrawCalls > 0);
        Assert.Equal(0, draws["sphere-rotation"][^1].ShadowDrawCalls);
        // A new atlas size is a new resource and starts from a full clear.
        Assert.Equal(0, draws["atlas-shrink"][0].ShadowReusedFaces);
    }

    [Fact]
    public void SubTextureSamplingTopologySwappedLightTypesEmptyFaceAndAbortPreserveExactAtlas()
    {
        using var reference = new D3D12FrameSession(); using var optimized = new D3D12FrameSession();
        reference.OriginalRecorder.NativeShadowReuse = false; reference.OriginalRecorder.NativeShadowCulling = false;
        reference.OriginalRecorder.NativeShadowDiagnostics = optimized.OriginalRecorder.NativeShadowDiagnostics = true;
        var (baseScene, baseMaterials, baseFlags) = Scene();
        var subUvs = Enumerable.Range(0, baseScene.VertexCount)
            .Select(i => new SceneAdditionalUvVertex(new(.25f, .25f, 0, 0), default, default, default)).ToArray();
        var subScene = baseScene.WithAdditionalUvs(new(1, subUvs));
        var subMaterials = baseMaterials.ToArray();
        subMaterials[5] = subMaterials[5] with { SphereMode = OriginalSphereMode.SubTexture, SphereTexture = 0 };
        var area = new NativeSceneLight(Guid.NewGuid(), "area", SceneLightKind.Area, new(3, 5, 3), Aim(new(3, 5, 3), new(3, 0, 3.01f)), new(1, .5f, .4f), 5, 15, AreaSize: new Vector2(2, 1.5f));
        var spot = new NativeSceneLight(Guid.NewGuid(), "spot", SceneLightKind.Spot, new(-3, 6, -3), Aim(new(-3, 6, -3), Vector3.Zero), new(.6f, .8f, 1), 6, 18);
        var point = new NativeSceneLight(Guid.NewGuid(), "point", SceneLightKind.Point, new(0, 3, 0), Quaternion.Identity, new(1, .9f, .8f), 4, 15);
        var directional = new NativeSceneLight(Guid.NewGuid(), "sun", SceneLightKind.Directional, new(0, 10, 0), Aim(new(5, 10, -4), Vector3.Zero), new(.5f, .5f, .45f), .8f);
        OriginalFrameScene MakeFrame(SceneSnapshot snapshot, OriginalStyleMaterialBinding[] mats, OriginalFrameMaterialFlags[] flags,
            NativeSceneLight[] lights, OriginalTextureSampling sampling = OriginalTextureSampling.PointClamp)
            => new(new(snapshot, mats, new(new(.3f), Vector3.Zero, Matrix4x4.Identity, sampling)),
                options: new(OriginalAlphaBlendMode.Normal, true, false), materials: flags, lighting: new(lights));

        int index = 0;
        // 1. SubTexture UV1 warm-up -> UV1 morph -> sampling mode change.
        for (int i = 0; i < 4; i++)
            AssertStepMatch(reference, optimized, MakeFrame(subScene, subMaterials, baseFlags, [area, spot]), index++, "subtex-warm");
        Assert.Equal(0, optimized.NativeLightStatistics!.ShadowDrawCalls);
        var uvAnimated = subScene.WithAdditionalUvs(new(1, subUvs.Select(v => v with { Uv1 = new(.75f, .25f, 0, 0) }).ToArray()));
        AssertStepMatch(reference, optimized, MakeFrame(uvAnimated, subMaterials, baseFlags, [area, spot]), index++, "subtex-uv1");
        Assert.True(optimized.NativeLightStatistics!.ShadowDrawCalls > 0);
        AssertStepMatch(reference, optimized, MakeFrame(uvAnimated, subMaterials, baseFlags, [area, spot], OriginalTextureSampling.LinearWrap), index++, "subtex-sampling");
        Assert.True(optimized.NativeLightStatistics!.ShadowDrawCalls > 0);

        // 2. Same 1536x1536 atlas dimensions: [Area(6 cells)+Spot(1)] <-> [Point(6)+Directional(1)].
        for (int i = 0; i < 4; i++)
            AssertStepMatch(reference, optimized, MakeFrame(subScene, subMaterials, baseFlags, [point, directional]), index++, "swap-to-point");
        Assert.Equal(0, optimized.NativeLightStatistics!.ShadowDrawCalls);
        for (int i = 0; i < 4; i++)
            AssertStepMatch(reference, optimized, MakeFrame(subScene, subMaterials, baseFlags, [area, spot]), index++, "swap-to-area");
        Assert.Equal(0, optimized.NativeLightStatistics!.ShadowDrawCalls);

        // 3. Topology add/remove: place a light whose +Z face is empty, add a model in +Z, then remove it (tile must clear to 1.0f).
        var (stageOnly, stageMats, stageFlags) = StageHalfScene();
        var zPoint = new NativeSceneLight(Guid.NewGuid(), "z-point", SceneLightKind.Point, new(0, 2, 0), Quaternion.Identity, Vector3.One, 4, 15);
        for (int i = 0; i < 4; i++)
            AssertStepMatch(reference, optimized, MakeFrame(stageOnly, stageMats, stageFlags, [zPoint]), index++, "topo-empty-warm");
        Assert.Equal(0, optimized.NativeLightStatistics!.ShadowDrawCalls);
        var (withBox, withBoxMats, withBoxFlags) = AppendBox(stageOnly, stageMats, stageFlags, new(-.5f, 1, 3), new(1, 2, 1));
        for (int i = 0; i < 4; i++)
            AssertStepMatch(reference, optimized, MakeFrame(withBox, withBoxMats, withBoxFlags, [zPoint]), index++, "topo-add");
        for (int i = 0; i < 4; i++)
            AssertStepMatch(reference, optimized, MakeFrame(stageOnly, stageMats, stageFlags, [zPoint]), index++, "topo-remove-clear");
        Assert.Equal(0, optimized.NativeLightStatistics!.ShadowDrawCalls);

        // 4. Aborted recording must invalidate the slot's cache so subsequent frames redraw cleanly.
        Assert.Throws<OperationCanceledException>(() => optimized.RenderForPresent(
            MakeFrame(withBox, withBoxMats, withBoxFlags, [zPoint]), Width, Height, null, (_, _) => throw new OperationCanceledException("abort")));
        for (int i = 0; i < 4; i++)
            AssertStepMatch(reference, optimized, MakeFrame(withBox, withBoxMats, withBoxFlags, [zPoint]), index++, "post-abort");

        // 5. Device reset (fresh D3D12FrameSession) produces byte-identical atlas and output.
        using var resetSession = new D3D12FrameSession();
        resetSession.OriginalRecorder.NativeShadowDiagnostics = true;
        AssertStepMatch(reference, resetSession, MakeFrame(withBox, withBoxMats, withBoxFlags, [zPoint]), index, "device-reset");
    }

    private static void AssertStepMatch(D3D12FrameSession reference, D3D12FrameSession optimized, OriginalFrameScene frame,
        int index, string step, StringBuilder? log = null, Dictionary<string, List<D3D12NativeLightStatistics>>? draws = null)
    {
        var expected = reference.Capture(frame, Width, Height);
        var expectedStatistics = reference.NativeLightStatistics!;
        var expectedDiagnostics = reference.OriginalRecorder.LastNativeShadowDiagnostics!;
        var expectedAtlas = expectedDiagnostics.ReadAtlas();
        var actual = optimized.Capture(frame, Width, Height);
        var statistics = optimized.NativeLightStatistics!;
        var diagnostics = optimized.OriginalRecorder.LastNativeShadowDiagnostics!;
        var atlas = diagnostics.ReadAtlas();
        Assert.Equal((expectedDiagnostics.Width, expectedDiagnostics.Height), (diagnostics.Width, diagnostics.Height));
        int different = 0;
        for (int i = 0; i < atlas.Length; i++) if (BitConverter.SingleToInt32Bits(atlas[i]) != BitConverter.SingleToInt32Bits(expectedAtlas[i])) different++;
        Assert.True(different == 0, $"{index} {step}: {different} atlas texels differ");
        Assert.True(expected.Rgba.AsSpan().SequenceEqual(actual.Rgba), $"{index} {step}: final frame differs");
        // The full redraw records every eligible caster of every face; the optimized path accounts for each one.
        Assert.Equal(0, expectedStatistics.ShadowCulledDraws + expectedStatistics.ShadowReusedFaces);
        Assert.Equal(expectedStatistics.ShadowCasterDraws, expectedStatistics.ShadowDrawCalls);
        Assert.Equal(expectedStatistics.ShadowCasterDraws, statistics.ShadowCasterDraws);
        Assert.Equal(statistics.ShadowCasterDraws, statistics.ShadowDrawCalls + statistics.ShadowCulledDraws + statistics.ShadowReusedDraws);
        Assert.Equal(expectedStatistics.ShadowFaces, statistics.ShadowFaces);
        if (draws is not null) (draws.TryGetValue(step, out var list) ? list : draws[step] = []).Add(statistics);
        log?.Append($"{index}\t{step}\t{statistics.ShadowFaces}\t{statistics.ShadowCasterDraws}\t{expectedStatistics.ShadowDrawCalls}\t{statistics.ShadowDrawCalls}\t" +
            $"{statistics.ShadowCulledDraws}\t{statistics.ShadowReusedFaces}\t{statistics.ShadowReusedDraws}\t" +
            $"{expectedDiagnostics.Timing.ReadResult().GpuMilliseconds:F4}\t{diagnostics.Timing.ReadResult().GpuMilliseconds:F4}\n");
    }

    private static IEnumerable<(string Step, OriginalFrameScene Frame)> Sequence()
    {
        var (scene, materials, flags) = Scene();
        var point = new NativeSceneLight(Guid.NewGuid(), "point", SceneLightKind.Point, new(1, 3, 1), Quaternion.Identity, new(1, .9f, .8f), 4, 15);
        var spot = new NativeSceneLight(Guid.NewGuid(), "spot", SceneLightKind.Spot, new(-3, 6, -3), Aim(new(-3, 6, -3), Vector3.Zero), new(.6f, .8f, 1), 6, 18);
        var area = new NativeSceneLight(Guid.NewGuid(), "area", SceneLightKind.Area, new(3, 5, 3), Aim(new(3, 5, 3), new(3, 0, 3.01f)), new(1, .5f, .4f), 5, 15,
            AreaSize: new Vector2(2, 1.5f));
        var directional = new NativeSceneLight(Guid.NewGuid(), "sun", SceneLightKind.Directional, new(0, 10, 0), Aim(new(5, 10, -4), Vector3.Zero), new(.5f, .5f, .45f), .8f);
        NativeSceneLight[] lights = [point, spot, area, directional];
        SceneCamera Camera(float angle) => new(new(14 * MathF.Sin(angle), 7, -14 * MathF.Cos(angle)), new(0, 1.5f, 0), Vector3.UnitY, 10, .1f, 60, 50);
        OriginalFrameScene Frame(SceneSnapshot snapshot, NativeSceneLight[] active, OriginalFrameMaterialFlags[]? frameFlags = null, float angle = 0, Matrix4x4? sphere = null)
            => new(new(snapshot.WithCamera(Camera(angle)), materials, new(new(.3f), Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
                options: new(OriginalAlphaBlendMode.Normal, true, false, SphereRotationOverride: sphere), materials: frameFlags ?? flags, lighting: new(active));
        for (int i = 0; i < 4; i++) yield return ("static", Frame(scene, lights));
        for (int i = 1; i <= 6; i++) yield return ("camera", Frame(scene, lights, angle: i * .15f));
        var moving = scene;
        for (int i = 1; i <= 6; i++) yield return ("character", Frame(moving = Patch(moving, i % 2 == 0 ? -.35f : .5f), lights, angle: .9f));
        var edited = lights.Select(light => light == point ? light with { Position = new(-1, 2.5f, 2) } : light).ToArray();
        for (int i = 0; i < 4; i++) yield return ("light-edit", Frame(moving, edited, angle: .9f));
        edited = edited.Select(light => light.Kind == SceneLightKind.Spot ? light with { Rotation = Aim(light.Position, new(2, 1, 2)), Range = 12, OuterConeRadians = .5f } : light).ToArray();
        for (int i = 0; i < 4; i++) yield return ("spot-edit", Frame(moving, edited, angle: .9f));
        edited = edited.Select(light => light.Kind == SceneLightKind.Directional ? light with { Rotation = Aim(new(-4, 9, 5), Vector3.Zero) } : light).ToArray();
        for (int i = 0; i < 4; i++) yield return ("directional-edit", Frame(moving, edited, angle: .9f));
        edited = edited.Select(light => light.Kind == SceneLightKind.Area ? light with { Position = new(2, 4.5f, 2), AreaSize = new(3, 1) } : light).ToArray();
        for (int i = 0; i < 4; i++) yield return ("area-edit", Frame(moving, edited, angle: .9f));
        var hidden = flags.Select((value, material) => material == 3 ? value with { CastSelfShadow = false } : value).ToArray();
        for (int i = 0; i < 3; i++) yield return ("hide-caster", Frame(moving, edited, hidden, .9f));
        for (int i = 0; i < 3; i++) yield return ("restore-caster", Frame(moving, edited, angle: .9f));
        var lowAlpha = moving.WithMaterials(moving.Materials.Select((m, idx) => idx == 4 ? m with { Diffuse = new(.7f, .7f, .7f, .4f) } : m));
        for (int i = 0; i < 3; i++) yield return ("material-alpha", Frame(lowAlpha, edited, angle: .9f));
        var highCutoff = moving.WithMaterials(moving.Materials.Select((m, idx) => idx == 4 ? m with { Diffuse = new(.7f, .7f, .7f, .75f), AlphaCutoff = .8f } : m));
        for (int i = 0; i < 4; i++) yield return ("material-alpha", Frame(highCutoff, edited, angle: .9f));
        var flippedDouble = flags.Select((value, idx) => idx == 2 ? value with { ShadowDoubleSided = !value.ShadowDoubleSided } : value).ToArray();
        for (int i = 0; i < 4; i++) yield return ("shadow-double-sided", Frame(moving, edited, flippedDouble, .9f));
        for (int i = 0; i < 3; i++) yield return ("sphere-rotation", Frame(moving, edited, angle: .9f, sphere: Matrix4x4.CreateRotationX(.8f * i)));
        for (int i = 0; i < 3; i++) yield return ("sphere-rotation", Frame(moving, edited, angle: .9f, sphere: Matrix4x4.CreateRotationX(1.6f)));
        var transformed = moving.WithWorld(Matrix4x4.CreateTranslation(1.2f, 0, -.6f));
        for (int i = 0; i < 4; i++) yield return ("world-transform", Frame(transformed, edited, angle: .9f));
        var noSpotShadow = edited.Select(light => light.Kind == SceneLightKind.Spot ? light with { CastShadows = false } : light).ToArray();
        for (int i = 0; i < 3; i++) yield return ("shadow-toggle", Frame(moving, noSpotShadow, angle: .9f));
        var noArea = edited.Where(light => light.Kind != SceneLightKind.Area).ToArray();
        for (int i = 0; i < 4; i++) yield return ("atlas-shrink", Frame(moving, noArea, angle: .9f));
        for (int i = 0; i < 4; i++) yield return ("atlas-grow", Frame(Patch(moving, .2f * i), edited, angle: .9f));
    }

    private static Quaternion Aim(Vector3 from, Vector3 to)
    {
        var up = MathF.Abs(Vector3.Normalize(to - from).Y) > .99f ? Vector3.UnitZ : Vector3.UnitY;
        Matrix4x4.Invert(Matrix4x4.CreateLookAt(from, to, up), out var world);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(world));
    }

    private static (SceneSnapshot Scene, OriginalStyleMaterialBinding[] Materials, OriginalFrameMaterialFlags[] Flags) StageHalfScene()
    {
        var vertices = new List<SceneVertex>(); var indices = new List<uint>();
        void Quad(Vector3 origin, Vector3 u, Vector3 v)
        {
            uint first = (uint)vertices.Count; var normal = Vector3.Normalize(Vector3.Cross(u, v));
            vertices.AddRange([new(origin, normal, new(0, 0)), new(origin + u, normal, new(1, 0)), new(origin + u + v, normal, new(1, 1)), new(origin + v, normal, new(0, 1))]);
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }
        Quad(new(-8, 0, -8), new(0, 10, 0), new(16, 0, 0));
        var scene = new SceneSnapshot(vertices.ToArray(), indices.ToArray(),
            [new("back-wall", 0, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 1)],
            camera: new(new(0, 4, -14), new(0, 2, 0), Vector3.UnitY, 10, .1f, 60, 50));
        return (scene, [new(new(.5f))], [new(true, true, false)]);
    }

    private static (SceneSnapshot Scene, OriginalStyleMaterialBinding[] Materials, OriginalFrameMaterialFlags[] Flags) AppendBox(
        SceneSnapshot source, OriginalStyleMaterialBinding[] mats, OriginalFrameMaterialFlags[] flags, Vector3 min, Vector3 size)
    {
        var vertices = source.Vertices.ToArray().ToList(); var indices = source.Indices.ToArray().ToList(); var ranges = source.Materials.ToList();
        int start = indices.Count;
        void Quad(Vector3 origin, Vector3 u, Vector3 v)
        {
            uint first = (uint)vertices.Count; var normal = Vector3.Normalize(Vector3.Cross(u, v));
            vertices.AddRange([new(origin, normal, new(0, 0)), new(origin + u, normal, new(1, 0)), new(origin + u + v, normal, new(1, 1)), new(origin + v, normal, new(0, 1))]);
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }
        var (x, y, z) = (new Vector3(size.X, 0, 0), new Vector3(0, size.Y, 0), new Vector3(0, 0, size.Z));
        Quad(min, y, x); Quad(min + z, x, y); Quad(min, z, y); Quad(min + x, y, z); Quad(min, x, z); Quad(min + y, z, x);
        ranges.Add(new("added-box", start, indices.Count - start, Vector4.One, Vector3.Zero, Vector3.Zero, 1));
        var next = new SceneSnapshot(vertices.ToArray(), indices.ToArray(), ranges, source.Textures, source.World, source.Camera, source.LightDirection);
        return (next, [..mats, new(new(.5f))], [..flags, new(true, true, false)]);
    }

    private static (SceneSnapshot Scene, OriginalStyleMaterialBinding[] Materials, OriginalFrameMaterialFlags[] Flags) Scene()
    {
        var vertices = new List<SceneVertex>(); var indices = new List<uint>(); var ranges = new List<SceneMaterial>();
        void Material(string name, int texture = -1) => ranges.Add(new(name, ranges.Sum(range => range.IndexCount), indices.Count - ranges.Sum(range => range.IndexCount),
            new(.7f, .7f, .7f, 1), Vector3.Zero, Vector3.Zero, 1, texture, AlphaCutoff: .5f));
        void Quad(Vector3 origin, Vector3 u, Vector3 v)
        {
            uint first = (uint)vertices.Count; var normal = Vector3.Normalize(Vector3.Cross(u, v));
            vertices.AddRange([new(origin, normal, new(0, 0)), new(origin + u, normal, new(1, 0)), new(origin + u + v, normal, new(1, 1)), new(origin + v, normal, new(0, 1))]);
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }
        void Box(Vector3 minimum, Vector3 size)
        {
            var (x, y, z) = (new Vector3(size.X, 0, 0), new Vector3(0, size.Y, 0), new Vector3(0, 0, size.Z));
            Quad(minimum, y, x); Quad(minimum + z, x, y); Quad(minimum, z, y); Quad(minimum + x, y, z); Quad(minimum, x, z); Quad(minimum + y, z, x);
        }
        Quad(new(-10, 0, -10), new(0, 0, 20), new(20, 0, 0)); Material("floor");
        Quad(new(-10, 0, 8), new(0, 10, 0), new(20, 0, 0)); Material("wall");
        Box(new(-4.5f, 0, 1.5f), new(1, 4, 1)); Material("pillar a");
        Box(new(3.5f, 0, -3.5f), new(1, 4, 1)); Material("pillar b");
        Box(new(-.5f, 0, -6.5f), new(1, 5, 1)); Material("pillar c", 0);
        Box(new(5.5f, 0, 4.5f), new(1.5f, 3, 1.5f)); Material("crate", 1);
        while (vertices.Count < CharacterStart) vertices.Add(vertices[0]);
        Box(new(-.4f, 0, -.3f), new(.8f, 2.6f, .6f)); Material("body");
        Box(new(-.3f, 2.8f, -.3f), new(.6f, .6f, .6f)); Material("head", 0);
        var cutout = new SceneTexture("cutout", 2, 1, [255, 255, 255, 255, 255, 255, 255, 0]);
        var sphere = new SceneTexture("sphere alpha", 1, 2, [255, 255, 255, 255, 255, 255, 255, 40]);
        var scene = new SceneSnapshot(vertices.ToArray(), indices.ToArray(), ranges, [cutout, sphere], lightDirection: Vector3.Normalize(new Vector3(-.3f, -.7f, .6f)));
        var materials = ranges.Select((_, material) => material == 5 ? new OriginalStyleMaterialBinding(new(.6f), 1, OriginalSphereMode.Multiply) :
            new OriginalStyleMaterialBinding(new(.6f), DoubleSided: material < 2)).ToArray();
        var flags = ranges.Select((_, material) => new OriginalFrameMaterialFlags(true, true, false, ShadowDoubleSided: material % 2 == 0)).ToArray();
        return (Patch(scene, 0), materials, flags);
    }

    // Core composition (as used by the scene assembler) changes only the character's own vertex chunk.
    private static SceneSnapshot Patch(SceneSnapshot baseline, float offset)
    {
        var method = typeof(SceneSnapshot).GetMethod("WithComposedVertices", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var writer = new Writer(offset);
        var callback = Delegate.CreateDelegate(method.GetParameters()[1].ParameterType, writer, typeof(Writer).GetMethod(nameof(Writer.Write))!);
        return (SceneSnapshot)method.Invoke(baseline, [new[] { (CharacterStart, baseline.VertexCount - CharacterStart) }, callback])!;
    }
    private sealed class Writer(float offset)
    {
        public void Write(int _, Span<SceneVertex> destination)
        { for (int i = 0; i < destination.Length; i++) destination[i] = destination[i] with { Position = destination[i].Position + new Vector3(offset, 0, offset * .5f) }; }
    }
}
