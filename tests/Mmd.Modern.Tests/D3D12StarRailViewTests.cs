using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12StarRailViewTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Environment.GetEnvironmentVariable("MMD_STYLE_PRESET_TEST_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "style-preset-tests"), Guid.NewGuid().ToString("N"));
    private static D3D12StylePresetCatalog Catalog => D3D12StylePresetCatalog.BuiltIn;

    [Fact]
    public void CatalogExposesBoundedStarRailSurfaceWithoutClaimingSpecializedMapsOrAdjustableSpecular()
    {
        var style = Catalog.Find("star-rail");
        Assert.True(style.IsAvailable);
        Assert.Equal("star-rail", style.Definition!.PipelineId);
        Assert.Equal(1, style.Definition.SchemaVersion);
        Assert.Contains("ordinary PMX basic body", style.Diagnostic);
        Assert.Contains("character-only", style.Diagnostic);
        Assert.Contains("fixed 0.45-0.55 band", style.Diagnostic);
        foreach (string input in new[] { "FaceSDF", "LightMap", "MatCap", "ramp-texture", "not implemented" })
            Assert.Contains(input, style.Diagnostic);
        Assert.True(Catalog.Find("wuthering-waves").IsAvailable);
        Assert.True(Catalog.Find("endfield").IsAvailable);
    }

    [Fact]
    public void SameSourceInstancesResolveThreeActualFamiliesInOriginalMaterialOrder()
    {
        var source = Frame();
        SceneRenderingStyleBinding[] bindings = [Binding(2, 1, "star-rail"), Binding(0, 1, "mmd"), Binding(1, 1, "genshin")];
        var original = source.Materials.ToArray();
        var result = D3D12ViewRenderingStyles.Resolve(source, Catalog, bindings);
        Assert.Equal(new[] { D3D12SurfaceStyle.Mmd, D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.StarRailPmx },
            result.SurfaceStyles.Materials);
        Assert.Same(source.Surface, result.Scene.Surface);
        Assert.Same(source.Options, result.Scene.Options);
        Assert.Equal(original, source.Materials);
        Assert.All(source.Surface.Snapshot.Materials, material => Assert.Equal("same-source.pmx", material.Name));
        Assert.Same(source.Materials[0], result.Scene.Materials[0]);
        Assert.All(result.Scene.Materials.Skip(1), material => Assert.Equal(new CharacterToonResponse(), material.CharacterToonResponse));
        Assert.Equal(2, result.Diagnostics.Count);
        bindings[0] = bindings[0] with { Preset = new("mmd") };
        Assert.Equal(D3D12SurfaceStyle.StarRailPmx, result.SurfaceStyles.Materials[2]);
        Assert.True(D3D12ViewRenderingStyles.Preflight(source, Catalog, bindings).IsAvailable);
    }

    [Fact]
    public void ExplicitFaceControlsApplyPerInstanceAndParticipateInEffectiveHistoryEquality()
    {
        var source = Frame();
        SceneRenderingStyleBinding[] objects = [Binding(0, 1, "mmd"), Binding(1, 1, "genshin"), Binding(2, 1, "star-rail")];
        var inputs = objects.Select((item, index) => new D3D12ViewCharacterMaterialBinding(item.ObjectId, 0,
            new(D3D12CharacterMaterialRole.Face, .2f + index * .1f, .3f + index * .1f))).ToArray();
        var result = D3D12ViewRenderingStyles.Resolve(source, Catalog, objects, inputs);
        Assert.Equal(1f, result.SurfaceStyles.EffectiveNormalShadeStrength(0));
        Assert.Equal(1f, result.SurfaceStyles.EffectiveGeometricShadowStrength(0));
        Assert.Equal(inputs[1].Style.FaceNormalShadeStrength, result.SurfaceStyles.EffectiveNormalShadeStrength(1));
        Assert.Equal(inputs[1].Style.FaceGeometricShadowStrength, result.SurfaceStyles.EffectiveGeometricShadowStrength(1));
        Assert.Equal(inputs[2].Style.FaceNormalShadeStrength, result.SurfaceStyles.EffectiveNormalShadeStrength(2));
        Assert.Equal(inputs[2].Style.FaceGeometricShadowStrength, result.SurfaceStyles.EffectiveGeometricShadowStrength(2));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(result.SurfaceStyles,
            new(result.SurfaceStyles.Materials, result.SurfaceStyles.CharacterMaterials)));
        inputs[2] = inputs[2] with { Style = inputs[2].Style with { FaceGeometricShadowStrength = .75f } };
        var changed = D3D12ViewRenderingStyles.Resolve(source, Catalog, objects, inputs);
        Assert.False(D3D12SurfaceStyleMap.Equivalent(result.SurfaceStyles, changed.SurfaceStyles));
        inputs[2] = inputs[2] with { Style = inputs[2].Style with { FaceGeometricShadowStrength = .5f, FaceNormalShadeStrength = .8f } };
        Assert.False(D3D12SurfaceStyleMap.Equivalent(result.SurfaceStyles,
            D3D12ViewRenderingStyles.Resolve(source, Catalog, objects, inputs).SurfaceStyles));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(result.SurfaceStyles,
            new([D3D12SurfaceStyle.Mmd, D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.GenshinPmx],
                result.SurfaceStyles.CharacterMaterials)));
    }

    [Theory]
    [InlineData(ModelRole.Environment)]
    [InlineData(ModelRole.Prop)]
    [InlineData(ModelRole.Unspecified)]
    public void NonCharacterStarRailPreservesItsMmdSurfaceWithContextualDiagnostic(ModelRole role)
    {
        var source = Frame();
        var binding = Binding(0, 3, "star-rail") with { Role = role };
        var result = D3D12ViewRenderingStyles.Resolve(source, Catalog, [binding]);
        Assert.Same(source, result.Scene);
        Assert.True(result.SurfaceStyles.IsDefault);
        string diagnostic = Assert.Single(result.Diagnostics);
        Assert.Contains(binding.ObjectId.ToString("D"), diagnostic);
        Assert.Contains("keeps the MMD surface", diagnostic);
        Assert.Contains("character-only", diagnostic);
        Assert.True(D3D12ViewRenderingStyles.Preflight(source, Catalog, [binding]).IsAvailable);
    }

    [Fact]
    public void HiddenStarRailRetainsReferencesAndExplicitFaceSelectionWithoutConsumingMaterials()
    {
        var source = Frame();
        var visible = Binding(0, 3, "mmd");
        var hidden = Binding(3, 0, "star-rail") with { Visible = false };
        D3D12ViewCharacterMaterialBinding[] materials = [new(hidden.ObjectId, 100, new(D3D12CharacterMaterialRole.Face))];
        Assert.True(D3D12ViewRenderingStyles.Preflight(source, Catalog, [visible, hidden], materials).IsAvailable);
        var result = D3D12ViewRenderingStyles.Resolve(source, Catalog, [visible, hidden], materials);
        Assert.True(result.SurfaceStyles.IsDefault);
        Assert.Contains(result.Diagnostics, text => text.Contains("Hidden") && text.Contains(hidden.ObjectId.ToString("D")));
        Assert.All(result.SurfaceStyles.CharacterMaterials, material => Assert.Equal(D3D12CharacterMaterialRole.Body, material.Role));
        var nowVisible = hidden with { MaterialStart = 2, MaterialCount = 1, Visible = true };
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(source, Catalog,
            [visible with { MaterialCount = 2 }, nowVisible], materials));
    }

    [Fact]
    public void StarRailSaveAsNewReloadsTheExistingSchemaAndNeverChangesPriorPreset()
    {
        var store = new D3D12StylePresetStore(directory);
        var first = store.SaveAsNew(Catalog.Find("star-rail").Definition!, "Star Rail ordinary PMX");
        byte[] before = File.ReadAllBytes(Path.Combine(directory, first.StableId + ".json"));
        var second = store.SaveAsNew("Different diffuse response", "star-rail", new(.22f), new(.7f, .18f));
        var reloaded = new D3D12StylePresetStore(directory).Load();
        var actual = reloaded.Find(second.StableId);
        Assert.True(actual.IsAvailable);
        Assert.Equal("star-rail", actual.Definition!.PipelineId);
        Assert.Equal(new CharacterToonResponse(.7f, .18f), actual.Definition.CharacterToonResponse);
        Assert.Equal(1, actual.Definition.SchemaVersion);
        Assert.NotEqual(first.StableId, second.StableId);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory, first.StableId + ".json")));
        var result = D3D12ViewRenderingStyles.Resolve(Frame(), reloaded, [Binding(0, 3, second.StableId)]);
        Assert.All(result.SurfaceStyles.Materials, material => Assert.Equal(D3D12SurfaceStyle.StarRailPmx, material));
        Assert.All(result.Scene.Materials, material => Assert.Equal(second.CharacterToonResponse, material.CharacterToonResponse));
    }

    [Fact]
    public void NullParametersDoNotAllowStarRailToUseTheLegacySurfaceEntry()
    {
        var source = Frame();
        var legacy = new OriginalFrameScene(source.Surface, materials: source.Materials);
        var store = new D3D12StylePresetStore(directory);
        var style = store.SaveAsNew("Original material parameters", "star-rail", null, null);
        Assert.Throws<NotSupportedException>(() => D3D12ViewRenderingStyles.Resolve(legacy,
            store.Load(), [Binding(0, 3, style.StableId)]));
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task CaptureDispatchesSameFrameMmdGenshinAndStarRailAndRestoresMmdExactly()
    {
        const int width = 257, height = 129;
        var source = Frame();
        var objects = new[] { Binding(0, 1, "mmd"), Binding(1, 1, "genshin"), Binding(2, 1, "star-rail") };
        var request = new ViewRenderRequest(width, height);
        var plan = new OfflineFramePlan(width, height, new(30), 0, 3);
        OfflineFrame At(int index, IReadOnlyList<SceneRenderingStyleBinding> bindings) => new(plan.GetSample(index), width, height, source, null, null, null)
        { RenderingStyles = bindings };
        var defaults = Array.AsReadOnly(objects.Select(item => item with { Preset = new("mmd") }).ToArray());
        var token = TestContext.Current.CancellationToken;
        await using var renderer = new D3D12ViewRenderer(request);
        Assert.False(renderer.IsWarp);
        using var baseline = await renderer.CaptureAsync(At(0, defaults), token);
        using var mixed = await renderer.CaptureAsync(At(1, Array.AsReadOnly(objects)), token);
        var viewScene = D3D12ViewScene.Resolve(source, null, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), request.EffectiveQuality);
        var expected = D3D12ViewRenderingStyles.Resolve(viewScene, Catalog, objects);
        using (var direct = new D3D12FrameSession())
            Assert.Equal(direct.Capture(expected.Scene, width, height, surfaceStyles: expected.SurfaceStyles).Rgba, mixed.Pixels.ToArray());
        // Derive the unchanged object's screen range from its actual vertices. This
        // fixture uses a right-handed camera looking toward +Z, so world X is mirrored.
        // Screen thirds would select the wrong family and include a changed surface.
        int[] mmdColumns = source.Surface.Snapshot.Vertices[..4].ToArray()
            .Select(vertex => ProjectX(source, vertex.Position.X, width, height)).ToArray();
        int mmdLeft = Math.Clamp(mmdColumns.Min(), 0, width - 1);
        int mmdRight = Math.Clamp(mmdColumns.Max() + 1, mmdLeft + 1, width);
        Assert.True(mmdRight - mmdLeft > 30, "The MMD isolation region must cover the actual fixture surface.");
        int[] genshinColumns = source.Surface.Snapshot.Vertices.Slice(4, 4).ToArray()
            .Select(vertex => ProjectX(source, vertex.Position.X, width, height)).ToArray();
        Assert.True(mmdRight <= genshinColumns.Min() || mmdLeft > genshinColumns.Max(),
            "The unchanged MMD region must exclude the neighboring Genshin surface.");
        for (int y = 0; y < height; y++)
        {
            int offset = (y * width + mmdLeft) * 4;
            int length = (mmdRight - mmdLeft) * 4;
            Assert.Equal(baseline.Pixels.Span.Slice(offset, length).ToArray(),
                mixed.Pixels.Span.Slice(offset, length).ToArray());
        }
        byte Pixel(int material) => mixed.Pixels.Span[(height / 2 * width + ProjectX(source, (material - 1) * 1.1f, width, height)) * 4];
        Assert.True(Pixel(1) > Pixel(0) + 5);
        Assert.True(Pixel(2) > Pixel(1) + 20);
        for (int i = 3; i < mixed.Pixels.Length; i += 4) Assert.Equal(baseline.Pixels.Span[i], mixed.Pixels.Span[i]);
        using var restored = await renderer.CaptureAsync(At(2, defaults), token);
        Assert.Equal(baseline.Pixels.ToArray(), restored.Pixels.ToArray());
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task ReloadedDiffuseParametersAndExplicitFaceStrengthReachActualCapture()
    {
        const int width = 129, height = 97;
        var source = Frame();
        var binding = Binding(0, 3, "star-rail");
        var store = new D3D12StylePresetStore(directory);
        var darker = store.SaveAsNew("Later one-sided ramp", "star-rail", new(), new(.7f, .1f));
        var request = new ViewRenderRequest(width, height);
        var plan = new OfflineFramePlan(width, height, new(30), 0, 3);
        OfflineFrame At(int index, string id) => new(plan.GetSample(index), width, height, source, null, null, null)
        { RenderingStyles = Array.AsReadOnly(new[] { binding with { Preset = new(id) } }) };
        D3D12ViewCharacterMaterialBinding[] face = [new(binding.ObjectId, 1, new(D3D12CharacterMaterialRole.Face, .35f, .35f))];
        var token = TestContext.Current.CancellationToken;
        await using var renderer = new D3D12ViewRenderer(request, stylePresets: store.Load(), characterMaterials: face);
        using var original = await renderer.CaptureAsync(At(0, "star-rail"), token);
        using var dark = await renderer.CaptureAsync(At(1, darker.StableId), token);
        int center = (height / 2 * width + width / 2) * 4;
        Assert.True(original.Pixels.Span[center] > dark.Pixels.Span[center] + 20);
        var viewScene = D3D12ViewScene.Resolve(source, null, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), request.EffectiveQuality);
        var resolved = D3D12ViewRenderingStyles.Resolve(viewScene, store.Load(), [binding], face);
        using var direct = new D3D12FrameSession();
        Assert.Equal(direct.Capture(resolved.Scene, width, height, surfaceStyles: resolved.SurfaceStyles).Rgba, original.Pixels.ToArray());
        var body = D3D12ViewRenderingStyles.Resolve(viewScene, store.Load(), [binding]);
        byte[] bodyPixels = direct.Capture(body.Scene, width, height, surfaceStyles: body.SurfaceStyles).Rgba;
        Assert.True(original.Pixels.Span[center] > bodyPixels[center] + 10);
        var before = renderer.Statistics;
        var invalid = At(2, "custom-missing");
        await Assert.ThrowsAsync<D3D12ViewRenderingStylesException>(() => renderer.CaptureAsync(invalid, token).AsTask());
        Assert.Same(before, renderer.Statistics);
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task PresentAcceptsAllThreeFamiliesWithoutOrdinaryReadbackAndPreflightsBeforeSubmit()
    {
        nint window = CreateWindowExW(0, "STATIC", "MMD Star Rail surface test", 0x80000000, 0, 0, 128, 96, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            var source = Frame();
            var objects = new[] { Binding(0, 1, "mmd"), Binding(1, 1, "genshin"), Binding(2, 1, "star-rail") };
            var frame = new PreviewFrame(96, 72, 0, source, null, null, null)
            { RenderingStyles = Array.AsReadOnly(objects) };
            var token = TestContext.Current.CancellationToken;
            await using var renderer = new D3D12RealtimeViewRenderer(window, new(96, 72),
                characterMaterials: [new(objects[2].ObjectId, 0, new(D3D12CharacterMaterialRole.Face, .35f, .35f))]);
            await renderer.PresentAsync(frame, token);
            var before = renderer.Statistics;
            var invalid = frame with { RenderingStyles = Array.AsReadOnly(objects.Append(
                Binding(3, 0, "custom-missing") with { Visible = false }).ToArray()) };
            await Assert.ThrowsAsync<D3D12ViewRenderingStylesException>(() => renderer.PresentAsync(invalid, token).AsTask());
            Assert.Equal(before.PresentCount, renderer.Statistics.PresentCount);
            Assert.Equal(before.Frame!.SubmittedFence, renderer.Statistics.Frame!.SubmittedFence);
            await renderer.PresentAsync(frame, token);
            Assert.Equal(2, renderer.Statistics.PresentCount);
            Assert.Equal(0, renderer.Statistics.ExplicitCaptureCount);
            Assert.Equal(0, renderer.Statistics.Frame!.TotalReadbacks);
        }
        finally { DestroyWindow(window); }
    }

    private static SceneRenderingStyleBinding Binding(int start, int count, string preset)
        => new(Guid.NewGuid(), new(preset), ModelRole.Character, start, count, true);

    private static OriginalFrameScene Frame()
    {
        const float d = -.05f;
        var normal = new Vector3(d, 0, -MathF.Sqrt(1 - d * d));
        SceneVertex[] Quad(float x) => [new(new(x - .4f, -.8f, 0), normal, Vector2.Zero),
            new(new(x + .4f, -.8f, 0), normal, Vector2.UnitX), new(new(x + .4f, .8f, 0), normal, Vector2.One),
            new(new(x - .4f, .8f, 0), normal, Vector2.UnitY)];
        var snapshot = new SceneSnapshot([.. Quad(-1.1f), .. Quad(0), .. Quad(1.1f)],
            [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6, 8, 10, 9, 8, 11, 10],
            Enumerable.Range(0, 3).Select(index => new SceneMaterial("same-source.pmx", index * 6, 6,
                new(.6f, .4f, .2f, 1), Vector3.Zero, Vector3.Zero, 1)),
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2.4f, .1f, 20), lightDirection: Vector3.UnitX);
        return new(new(snapshot, Enumerable.Repeat(new OriginalStyleMaterialBinding(new(.2f)), 3),
            new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: D3D12OriginalFrameTests.Options(shadow: new(Matrix4x4.CreateScale(.2f), 32, 32,
                false, false, OriginalTextureSampling.PointClamp, OriginalDepthComparison.LessEqual)));
    }

    private static int ProjectX(OriginalFrameScene scene, float x, int width, int height)
    {
        var clip = Vector4.Transform(new Vector4(x, 0, 0, 1), scene.Surface.Snapshot.Camera.ViewProjection(width, height));
        return (int)((clip.X / clip.W * .5f + .5f) * width);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extended, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    public void Dispose()
    {
        string cache = Path.GetFullPath(Environment.GetEnvironmentVariable("MMD_STYLE_PRESET_TEST_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "style-preset-tests"));
        string target = Path.GetFullPath(directory);
        if (!target.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Star Rail preset test cleanup must stay within its test cache.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
