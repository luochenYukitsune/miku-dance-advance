using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12RemainingStyleViewTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Environment.GetEnvironmentVariable("MMD_STYLE_PRESET_TEST_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "style-preset-tests"), Guid.NewGuid().ToString("N"));
    private static D3D12StylePresetCatalog Catalog => TestStylePlugins.AllPresetCatalog;
    private static D3D12ViewStyleCatalog ViewCatalog => TestStylePlugins.AllViewCatalog;
    private static readonly string[] Presets = ["mmd", "genshin", "star-rail", "wuthering-waves", "endfield"];
    private static readonly string[] Families = Presets;

    [Theory]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void CatalogDistinguishesAvailableFaceShapesFromPendingCompletePalettes(string preset)
    {
        var result = Catalog.Find(preset);
        Assert.True(result.IsAvailable);
        Assert.Equal(preset, result.Definition!.PipelineId);
        Assert.Equal(1, result.Definition.SchemaVersion);
        Assert.NotNull(result.PluginRegistration);
        Assert.NotNull(result.DefaultStylizedShading);
        Assert.Contains("default", result.PluginRegistration!.ShaderModules!.Keys);
        Assert.Contains("face-sdf", result.PluginRegistration.ShaderModules.Keys);
        var style = ViewCatalog.FindStyle(preset)!;
        Assert.Contains(style.MaterialModes, mode => mode.ModeId == "face-sdf");
        Assert.Contains(style.MaterialModes, mode => mode.ModeId == "hair");
        Assert.All(Catalog.List(), entry => Assert.True(entry.IsAvailable));
    }

    [Fact]
    public void SameSourceInstancesDispatchFiveFamiliesInOriginalMaterialOrder()
    {
        var source = Frame();
        var objects = Objects();
        var before = source.Materials.ToArray();
        var result = D3D12ViewRenderingStyles.Resolve(source, Catalog, objects.Reverse(), styleCatalog: ViewCatalog);
        Assert.Equal(Families, result.SurfaceStyles.StyleIds);
        Assert.Same(source.Surface, result.Scene.Surface);
        Assert.Same(source.Options, result.Scene.Options);
        Assert.Equal(before, source.Materials);
        Assert.All(source.Surface.Snapshot.Materials, material => Assert.Equal("same-source.pmx", material.Name));
        Assert.Equal(4, result.Diagnostics.Count);
        Assert.True(D3D12ViewRenderingStyles.Preflight(source, Catalog, objects, styleCatalog: ViewCatalog).IsAvailable);
        objects[4] = objects[4] with { Preset = new("mmd") };
        Assert.Equal("endfield", result.SurfaceStyles.StyleIds[4]);
    }

    [Fact]
    public void AllFourStylizedFamiliesConsumeBothFaceControlsAndHistoryIgnoresUnusedMmdControls()
    {
        var source = Frame();
        var objects = Objects();
        var inputs = objects.Select(item => new D3D12ViewCharacterMaterial(item.ObjectId, 0,
            D3D12CharacterMaterialRole.Face, FaceNormalShadeStrength: .35f, FaceGeometricShadowStrength: .35f)).ToArray();
        var result = D3D12ViewRenderingStyles.Resolve(source, Catalog, objects, inputs, styleCatalog: ViewCatalog);
        Assert.Equal(1f, result.SurfaceStyles.EffectiveNormalShadeStrength(0));
        Assert.Equal(1f, result.SurfaceStyles.EffectiveGeometricShadowStrength(0));
        for (int index = 1; index < 5; index++)
        {
            Assert.Equal(.35f, result.SurfaceStyles.EffectiveNormalShadeStrength(index));
            Assert.Equal(.35f, result.SurfaceStyles.EffectiveGeometricShadowStrength(index));
            var normal = inputs.ToArray();
            normal[index] = normal[index] with { FaceNormalShadeStrength = .7f };
            Assert.False(D3D12SurfaceStyleMap.Equivalent(result.SurfaceStyles,
                D3D12ViewRenderingStyles.Resolve(source, Catalog, objects, normal, styleCatalog: ViewCatalog).SurfaceStyles));
            var shadow = inputs.ToArray();
            shadow[index] = shadow[index] with { FaceGeometricShadowStrength = .7f };
            Assert.False(D3D12SurfaceStyleMap.Equivalent(result.SurfaceStyles,
                D3D12ViewRenderingStyles.Resolve(source, Catalog, objects, shadow, styleCatalog: ViewCatalog).SurfaceStyles));
        }
        inputs[0] = inputs[0] with { FaceNormalShadeStrength = 0, FaceGeometricShadowStrength = 0 };
        Assert.True(D3D12SurfaceStyleMap.Equivalent(result.SurfaceStyles,
            D3D12ViewRenderingStyles.Resolve(source, Catalog, objects, inputs, styleCatalog: ViewCatalog).SurfaceStyles));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(result.SurfaceStyles,
            new(result.SurfaceStyles.StyleIds.Reverse(), result.SurfaceStyles.CharacterMaterials, Catalog)));
    }

    [Theory]
    [InlineData("wuthering-waves", ModelRole.Environment)]
    [InlineData("wuthering-waves", ModelRole.Prop)]
    [InlineData("wuthering-waves", ModelRole.Unspecified)]
    [InlineData("endfield", ModelRole.Environment)]
    [InlineData("endfield", ModelRole.Prop)]
    [InlineData("endfield", ModelRole.Unspecified)]
    public void NonCharacterDoesNotAcquireACharacterShader(string preset, ModelRole role)
    {
        var source = Frame();
        var binding = Binding(0, 5, preset) with { Role = role };
        var result = D3D12ViewRenderingStyles.Resolve(source, Catalog, [binding], styleCatalog: ViewCatalog);
        Assert.Same(source, result.Scene);
        Assert.True(result.SurfaceStyles.IsDefault);
        string diagnostic = Assert.Single(result.Diagnostics);
        Assert.Contains(binding.ObjectId.ToString("D"), diagnostic);
        Assert.Contains("keeps the MMD surface", diagnostic);
    }

    [Theory]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void HiddenStyleAndFaceMappingRemainDormantUntilVisible(string preset)
    {
        var source = Frame();
        var visible = Binding(0, 5, "mmd");
        var hidden = Binding(5, 0, preset) with { Visible = false };
        D3D12ViewCharacterMaterial[] face = [new(hidden.ObjectId, 100, D3D12CharacterMaterialRole.Face, FaceNormalShadeStrength: .35f, FaceGeometricShadowStrength: .35f)];
        Assert.True(D3D12ViewRenderingStyles.Preflight(source, Catalog, [visible, hidden], face, styleCatalog: ViewCatalog).IsAvailable);
        var result = D3D12ViewRenderingStyles.Resolve(source, Catalog, [visible, hidden], face, styleCatalog: ViewCatalog);
        Assert.True(result.SurfaceStyles.IsDefault);
        Assert.Contains(result.Diagnostics, text => text.Contains("Hidden") && text.Contains(hidden.ObjectId.ToString("D")));
        Assert.All(result.SurfaceStyles.CharacterMaterials, material => Assert.Equal(D3D12CharacterMaterialRole.Body, material.Role));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(source, Catalog,
            [visible with { MaterialCount = 4 }, hidden with { MaterialStart = 4, MaterialCount = 1, Visible = true }], face, styleCatalog: ViewCatalog));
    }

    [Theory]
    [InlineData("wuthering-waves", "wuthering-waves")]
    [InlineData("endfield", "endfield")]
    public void SaveAsNewReloadsExistingSchemaWithoutMutatingPriorDefinition(string preset, string family)
    {
        var store = new D3D12StylePresetStore(directory);
        var first = store.SaveAsNew(Catalog.Find(preset).Definition!, "First ordinary PMX style");
        byte[] before = File.ReadAllBytes(Path.Combine(directory, first.StableId + ".json"));
        var second = store.SaveAsNew("Adjusted diffuse only", preset, new(.22f), new(.7f, .18f));
        var catalog = new D3D12StylePresetStore(directory).Load(ViewCatalog.Registrations);
        var actual = catalog.Find(second.StableId);
        Assert.True(actual.IsAvailable);
        Assert.Equal(preset, actual.Definition!.PipelineId);
        Assert.Equal(new CharacterToonResponse(.7f, .18f), actual.Definition.CharacterToonResponse);
        Assert.Equal(1, actual.Definition.SchemaVersion);
        Assert.NotEqual(first.StableId, second.StableId);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory, first.StableId + ".json")));
        var result = D3D12ViewRenderingStyles.Resolve(Frame(), catalog, [Binding(0, 5, second.StableId)], styleCatalog: ViewCatalog);
        Assert.All(result.SurfaceStyles.StyleIds, value => Assert.Equal(family, value));
        Assert.All(result.Scene.Materials, value => Assert.Equal(second.CharacterToonResponse, value.CharacterToonResponse));
    }

    [Theory]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void NullPresetParametersDoNotBypassTheModernSurfaceRequirement(string preset)
    {
        var source = Frame();
        var legacy = new OriginalFrameScene(source.Surface, materials: source.Materials);
        var store = new D3D12StylePresetStore(directory);
        var saved = store.SaveAsNew("Keep material parameters", preset, null, null);
        Assert.Throws<NotSupportedException>(() => D3D12ViewRenderingStyles.Resolve(legacy, store.Load(ViewCatalog.Registrations),
            [Binding(0, 5, saved.StableId)], styleCatalog: ViewCatalog));
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task CaptureDispatchesAllFiveFamiliesAndRestoresTheOriginalMmdPixelsExactly()
    {
        const int width = 321, height = 129;
        var source = Frame();
        var objects = Objects();
        var request = new ViewRenderRequest(width, height);
        var plan = new OfflineFramePlan(width, height, new(30), 0, 3);
        OfflineFrame At(int index, IReadOnlyList<SceneRenderingStyleBinding> bindings) => new(plan.GetSample(index), width, height, source, null, null, null)
        { RenderingStyles = bindings };
        var defaults = Array.AsReadOnly(objects.Select(item => item with { Preset = new("mmd") }).ToArray());
        var token = TestContext.Current.CancellationToken;
        await using var renderer = new D3D12ViewRenderer(request, stylePresets: Catalog, styleCatalog: ViewCatalog);
        Assert.False(renderer.IsWarp);
        using var baseline = await renderer.CaptureAsync(At(0, defaults), token);
        using var mixed = await renderer.CaptureAsync(At(1, Array.AsReadOnly(objects)), token);
        var viewScene = D3D12ViewScene.Resolve(source, null, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), request.EffectiveQuality);
        var expected = D3D12ViewRenderingStyles.Resolve(viewScene, Catalog, objects, styleCatalog: ViewCatalog);
        using (var direct = new D3D12FrameSession())
            Assert.Equal(direct.Capture(expected.Scene, width, height, surfaceStyles: expected.SurfaceStyles).Rgba, mixed.Pixels.ToArray());
        int[] columns = source.Surface.Snapshot.Vertices[..4].ToArray()
            .Select(vertex => ProjectX(source, vertex.Position.X, width, height)).ToArray();
        int left = Math.Clamp(columns.Min(), 0, width - 1), right = Math.Clamp(columns.Max() + 1, left + 1, width);
        Assert.True(right - left > 20);
        for (int y = 0; y < height; y++)
        {
            int offset = (y * width + left) * 4, length = (right - left) * 4;
            Assert.Equal(baseline.Pixels.Span.Slice(offset, length).ToArray(), mixed.Pixels.Span.Slice(offset, length).ToArray());
        }
        foreach (int material in new[] { 3, 4 })
        {
            int pixel = (height / 2 * width + ProjectX(source, (material - 2) * .9f, width, height)) * 4;
            Assert.NotEqual(baseline.Pixels.Span[pixel], mixed.Pixels.Span[pixel]);
        }
        for (int i = 3; i < mixed.Pixels.Length; i += 4) Assert.Equal(baseline.Pixels.Span[i], mixed.Pixels.Span[i]);
        using var restored = await renderer.CaptureAsync(At(2, defaults), token);
        Assert.Equal(baseline.Pixels.ToArray(), restored.Pixels.ToArray());
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public async Task ReloadedPresetAndExplicitFaceControlsReachTheActualCapture(string preset)
    {
        const int width = 321, height = 129;
        var source = Frame();
        var store = new D3D12StylePresetStore(directory);
        var saved = store.SaveAsNew("Local ordinary style", preset, new(), new(.6f, .15f));
        var catalog = new D3D12StylePresetStore(directory).Load(ViewCatalog.Registrations);
        var binding = Binding(0, 5, saved.StableId);
        D3D12ViewCharacterMaterial[] face = [new(binding.ObjectId, 2, D3D12CharacterMaterialRole.Face, FaceNormalShadeStrength: .35f, FaceGeometricShadowStrength: .35f)];
        var request = new ViewRenderRequest(width, height);
        var plan = new OfflineFramePlan(width, height, new(30), 0, 1);
        var frame = new OfflineFrame(plan.GetSample(0), width, height, source, null, null, null)
        { RenderingStyles = Array.AsReadOnly(new[] { binding }) };
        var token = TestContext.Current.CancellationToken;
        await using var renderer = new D3D12ViewRenderer(request, stylePresets: catalog, styleCatalog: ViewCatalog, characterMaterials: face);
        using var captured = await renderer.CaptureAsync(frame, token);
        var viewScene = D3D12ViewScene.Resolve(source, null, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), request.EffectiveQuality);
        var expected = D3D12ViewRenderingStyles.Resolve(viewScene, catalog, [binding], face, styleCatalog: ViewCatalog);
        using var direct = new D3D12FrameSession();
        Assert.Equal(direct.Capture(expected.Scene, width, height, surfaceStyles: expected.SurfaceStyles).Rgba, captured.Pixels.ToArray());
        var body = D3D12ViewRenderingStyles.Resolve(viewScene, catalog, [binding], styleCatalog: ViewCatalog);
        byte[] bodyPixels = direct.Capture(body.Scene, width, height, surfaceStyles: body.SurfaceStyles).Rgba;
        int center = (height / 2 * width + width / 2) * 4;
        Assert.True(captured.Pixels.Span[center] > bodyPixels[center] + 10);
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task PresentAcceptsAllFiveFamiliesAndRejectsMissingHiddenStylesBeforeSubmission()
    {
        nint window = CreateWindowExW(0, "STATIC", "MDA ordinary PMX styles", 0x80000000, 0, 0, 128, 96, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            var source = Frame();
            var objects = Objects();
            var frame = new PreviewFrame(120, 72, 0, source, null, null, null)
            { RenderingStyles = Array.AsReadOnly(objects) };
            var token = TestContext.Current.CancellationToken;
            await using var renderer = new D3D12RealtimeViewRenderer(window, new(120, 72), stylePresets: Catalog, styleCatalog: ViewCatalog, characterMaterials:
                [new(objects[3].ObjectId, 0, D3D12CharacterMaterialRole.Face, FaceNormalShadeStrength: .35f, FaceGeometricShadowStrength: .35f),
                 new(objects[4].ObjectId, 0, D3D12CharacterMaterialRole.Face, FaceNormalShadeStrength: .35f, FaceGeometricShadowStrength: .35f)]);
            await renderer.PresentAsync(frame, token);
            var before = renderer.Statistics;
            var invalid = frame with { RenderingStyles = Array.AsReadOnly(objects.Append(
                Binding(5, 0, "custom-missing") with { Visible = false }).ToArray()) };
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

    private static SceneRenderingStyleBinding[] Objects()
        => Presets.Select((preset, index) => Binding(index, 1, preset)).ToArray();

    private static OriginalFrameScene Frame()
    {
        const float d = -.025f;
        var normal = new Vector3(d, 0, -MathF.Sqrt(1 - d * d));
        SceneVertex[] Quad(float x) => [new(new(x - .32f, -.8f, 0), normal, Vector2.Zero),
            new(new(x + .32f, -.8f, 0), normal, Vector2.UnitX), new(new(x + .32f, .8f, 0), normal, Vector2.One),
            new(new(x - .32f, .8f, 0), normal, Vector2.UnitY)];
        var snapshot = new SceneSnapshot(Enumerable.Range(0, 5).SelectMany(index => Quad((index - 2) * .9f)).ToArray(),
            Enumerable.Range(0, 5).SelectMany(index => new[] { 0, 2, 1, 0, 3, 2 }.Select(vertex => (uint)(index * 4 + vertex))).ToArray(),
            Enumerable.Range(0, 5).Select(index => new SceneMaterial("same-source.pmx", index * 6, 6,
                new(.6f, .4f, .2f, 1), Vector3.Zero, Vector3.Zero, 1)),
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2.4f, .1f, 20), lightDirection: Vector3.UnitX);
        return new(new(snapshot, Enumerable.Repeat(new OriginalStyleMaterialBinding(new(.2f)), 5),
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
            throw new InvalidOperationException("Remaining styles test cleanup must stay within its test cache.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
