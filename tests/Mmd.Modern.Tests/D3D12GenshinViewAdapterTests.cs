using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12GenshinViewAdapterTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Environment.GetEnvironmentVariable("MMD_STYLE_PRESET_TEST_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "style-preset-tests"), Guid.NewGuid().ToString("N"));

    [Fact]
    public void ResolvedPipelineIdMapsSamePmxInstancesIndependentlyAndDetachesTheResult()
    {
        var store = new D3D12StylePresetStore(directory);
        var custom = store.SaveAsNew("Body surface", "genshin", new(.3f), new(.6f, .12f));
        var catalog = store.Load();
        var source = Frame();
        var flags = source.Materials.ToArray();
        SceneRenderingStyleBinding[] bindings =
        [Binding(2, 2, custom.StableId), Binding(0, 2, "mmd")];

        var result = D3D12ViewRenderingStyles.Resolve(source, catalog, bindings);

        Assert.Equal(new[] { D3D12SurfaceStyle.Mmd, D3D12SurfaceStyle.Mmd,
            D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.GenshinPmx }, result.SurfaceStyles.Materials);
        Assert.Equal(source.Materials.Count, result.SurfaceStyles.Count);
        Assert.False(result.SurfaceStyles.IsDefault);
        Assert.Same(source.Surface, result.Scene.Surface);
        Assert.Same(source.Outline, result.Scene.Outline);
        Assert.Same(source.Options, result.Scene.Options);
        Assert.All(source.Surface.Snapshot.Materials, material => Assert.Equal("same-source.pmx", material.Name));
        Assert.Equal(custom.CharacterOutlineStyle, result.Scene.Materials[2].CharacterOutlineStyle);
        Assert.Equal(custom.CharacterToonResponse, result.Scene.Materials[2].CharacterToonResponse);
        Assert.Equal(RayGiToonResponse.PreservePaletteBands, result.Scene.Materials[2].GiToonResponseOverride);
        Assert.Equal(flags, source.Materials);
        Assert.Contains(result.Diagnostics, message => message.Contains("ordinary PMX basic body", StringComparison.Ordinal));
        bindings[0] = bindings[0] with { Preset = new("mmd") };
        File.Delete(Path.Combine(directory, custom.StableId + ".json"));
        Assert.Equal(D3D12SurfaceStyle.GenshinPmx, result.SurfaceStyles.Materials[2]);
        Assert.Equal(D3D12SurfaceStyle.Mmd,
            D3D12ViewRenderingStyles.Resolve(source, catalog, bindings).SurfaceStyles.Materials[2]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.Diagnostics)[0] = "changed");
        Assert.Equal(result.Scene.Materials,
            D3D12ViewRenderingStyles.Apply(source, catalog,
                [Binding(0, 2, "mmd"), Binding(2, 2, custom.StableId)]).Materials);
    }

    [Theory]
    [InlineData(ModelRole.Environment)]
    [InlineData(ModelRole.Prop)]
    [InlineData(ModelRole.Unspecified)]
    public void NonCharacterGenshinSelectionKeepsMmdAndExplainsTheCharacterOnlyScope(ModelRole role)
    {
        var source = Frame();
        var binding = Binding(0, source.Materials.Count, "genshin", role);

        var result = D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn, [binding]);

        Assert.Same(source, result.Scene);
        Assert.True(result.SurfaceStyles.IsDefault);
        Assert.All(result.SurfaceStyles.Materials, style => Assert.Equal(D3D12SurfaceStyle.Mmd, style));
        string diagnostic = Assert.Single(result.Diagnostics);
        Assert.Contains(binding.ObjectId.ToString("D"), diagnostic);
        Assert.Contains("character-only", diagnostic);
        Assert.Contains("keeps the MMD surface", diagnostic);
        Assert.True(D3D12ViewRenderingStyles.Preflight(source, D3D12StylePresetCatalog.BuiltIn, [binding]).IsAvailable);
    }

    [Fact]
    public void HiddenGenshinAndEmptyLegacyBindingsConsumeNoSurfaceRange()
    {
        var source = Frame();
        var catalog = D3D12StylePresetCatalog.BuiltIn;
        var legacy = D3D12ViewRenderingStyles.Resolve(source, catalog, []);
        Assert.Same(source, legacy.Scene);
        Assert.True(legacy.SurfaceStyles.IsDefault);
        Assert.Equal(source.Materials.Count, legacy.SurfaceStyles.Count);
        Assert.Empty(legacy.Diagnostics);

        var hidden = Binding(4, 0, "genshin", visible: false);
        var result = D3D12ViewRenderingStyles.Resolve(source, catalog,
            [Binding(0, 4, role: ModelRole.Environment), hidden]);
        Assert.Same(source, result.Scene);
        Assert.True(result.SurfaceStyles.IsDefault);
        Assert.NotEmpty(Assert.Single(result.Diagnostics));
        Assert.Contains(hidden.ObjectId.ToString("D"), result.Diagnostics[0]);
        Assert.Contains("Hidden", result.Diagnostics[0]);
    }

    [Fact]
    public void InvalidOrUnavailableWholeSelectionsRejectBeforeAnyResultIsPublished()
    {
        var source = Frame();
        var original = source.Materials.ToArray();
        var first = Binding(0, 2, "genshin");
        var invalid = Binding(1, 2);
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(source,
            D3D12StylePresetCatalog.BuiltIn, [first, invalid]));
        foreach (string id in new[] { "custom-missing-a", "custom-missing-b", "custom-missing" })
        {
            var hidden = Binding(4, 0, id, visible: false);
            var error = Assert.Throws<D3D12ViewRenderingStylesException>(() => D3D12ViewRenderingStyles.Resolve(source,
                D3D12StylePresetCatalog.BuiltIn, [first, Binding(2, 2), hidden]));
            var failure = Assert.Single(error.Failures);
            Assert.Equal(id, failure.Preset.StableId);
            Assert.Equal(new[] { hidden.ObjectId }, failure.AffectedObjectIds);
            Assert.Equal(original, source.Materials);
        }
    }

    [Fact]
    public void CustomGenshinWithNullParametersStillRequiresTheModernSurfaceEntry()
    {
        var source = Frame();
        var legacy = new OriginalFrameScene(source.Surface, materials: source.Materials);
        var store = new D3D12StylePresetStore(directory);
        var custom = store.SaveAsNew("Keep parameters", "genshin", null, null);

        var error = Assert.Throws<NotSupportedException>(() => D3D12ViewRenderingStyles.Resolve(legacy,
            store.Load(), [Binding(0, legacy.Materials.Count, custom.StableId)]));

        Assert.Contains("modern frame options", error.Message);
        Assert.Null(legacy.Options);
        Assert.Equal(source.Materials, legacy.Materials);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public async Task CaptureForwardsInjectedGenshinSurfaceAndRejectsMissingStylesBeforeSubmission()
    {
        const int width = 257, height = 191;
        var source = GpuFrame();
        var original = source.Materials.ToArray();
        var store = new D3D12StylePresetStore(directory);
        var genshin = store.SaveAsNew("Ordinary body", "genshin", new(), new());
        var mmd = store.SaveAsNew("Same parameters", "mmd", new(), new());
        var catalog = store.Load();
        var request = new ViewRenderRequest(width, height);
        var plan = new OfflineFramePlan(width, height, new(30), 0, 8);
        var binding = Binding(0, source.Materials.Count, genshin.StableId);
        OfflineFrame Selected(int index, string id) => new(plan.GetSample(index), width, height, source, null, null, null)
        { RenderingStyles = Array.AsReadOnly(new[] { binding with { Preset = new(id) } }) };
        var token = TestContext.Current.CancellationToken;
        await using var renderer = new D3D12ViewRenderer(request, stylePresets: catalog);
        Assert.False(renderer.IsWarp);
        async Task<byte[]> Capture(OfflineFrame frame)
        {
            using var pixels = await renderer.CaptureAsync(frame, token);
            return pixels.Pixels.ToArray();
        }
        var mmdPixels = await Capture(Selected(0, mmd.StableId));
        var genshinPixels = await Capture(Selected(1, genshin.StableId));
        Assert.False(mmdPixels.AsSpan().SequenceEqual(genshinPixels), "The adapter must dispatch the actual Genshin body surface.");
        var viewScene = D3D12ViewScene.Resolve(source, null, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), request.EffectiveQuality);
        var expected = D3D12ViewRenderingStyles.Resolve(viewScene, catalog, Selected(1, genshin.StableId).RenderingStyles);
        using (var direct = new D3D12FrameSession())
            Assert.Equal(direct.Capture(expected.Scene, width, height, surfaceStyles: expected.SurfaceStyles).Rgba, genshinPixels);
        var before = renderer.Statistics!;
        foreach (string id in new[] { "custom-missing-a", "custom-missing-b", "custom-missing" })
        {
            var invalid = Selected(2, genshin.StableId) with
            {
                RenderingStyles = Array.AsReadOnly(new[] { binding, Binding(source.Materials.Count, 0, id, visible: false) }),
            };
            await Assert.ThrowsAsync<D3D12ViewRenderingStylesException>(() => renderer.CaptureAsync(invalid, token).AsTask());
            Assert.Same(before, renderer.Statistics);
        }
        Assert.Equal(mmdPixels, await Capture(Selected(2, mmd.StableId)));
        Assert.Equal(before.FrameId + 1, renderer.Statistics!.FrameId);
        Assert.Equal(before.TotalSubmissions + 1, renderer.Statistics.TotalSubmissions);
        Assert.Equal(before.TotalReadbacks + 1, renderer.Statistics.TotalReadbacks);
        Assert.Equal(original, source.Materials);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public async Task PresentAcceptsGenshinAndRejectsMissingStylesWithoutReadbackOrSubmission()
    {
        nint window = CreateWindowExW(0, "STATIC", "MMD Genshin view adapter test", 0x80000000, 0, 0, 128, 96, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            var scene = GpuFrame();
            var binding = Binding(0, scene.Materials.Count, "genshin");
            var frame = new PreviewFrame(96, 72, 0, scene, null, null, null)
            { RenderingStyles = Array.AsReadOnly(new[] { binding }) };
            await using var renderer = new D3D12RealtimeViewRenderer(window, new(96, 72));
            var token = TestContext.Current.CancellationToken;
            await renderer.PresentAsync(frame, token);
            var before = renderer.Statistics;
            foreach (string id in new[] { "custom-missing-a", "custom-missing-b", "custom-missing" })
            {
                var invalid = frame with
                {
                    RenderingStyles = Array.AsReadOnly(new[] { binding, Binding(scene.Materials.Count, 0, id, visible: false) }),
                };
                await Assert.ThrowsAsync<D3D12ViewRenderingStylesException>(() => renderer.PresentAsync(invalid, token).AsTask());
                Assert.Equal(before.PresentCount, renderer.Statistics.PresentCount);
                Assert.Equal(before.Frame!.FrameId, renderer.Statistics.Frame!.FrameId);
                Assert.Equal(before.Frame.TotalSubmissions, renderer.Statistics.Frame.TotalSubmissions);
                Assert.Equal(before.Frame.SubmittedFence, renderer.Statistics.Frame.SubmittedFence);
            }
            await renderer.PresentAsync(frame with
            { RenderingStyles = Array.AsReadOnly(new[] { binding with { Preset = new("mmd") } }) }, token);
            Assert.Equal(2, renderer.Statistics.PresentCount);
            Assert.Equal(before.Frame!.FrameId + 1, renderer.Statistics.Frame!.FrameId);
            Assert.Equal(0, renderer.Statistics.ExplicitCaptureCount);
            Assert.Equal(0, renderer.Statistics.Frame.TotalReadbacks);
        }
        finally { DestroyWindow(window); }
    }

    private static SceneRenderingStyleBinding Binding(int start, int count, string id = "mmd",
        ModelRole role = ModelRole.Character, bool visible = true) => new(Guid.NewGuid(), new(id), role, start, count, visible);

    private static OriginalFrameScene Frame()
    {
        SceneVertex[] vertices = [new(new(-1, -1, 0), Vector3.UnitZ, Vector2.Zero),
            new(new(0, 1, 0), Vector3.UnitZ, Vector2.UnitY), new(new(1, -1, 0), Vector3.UnitZ, Vector2.UnitX)];
        var materials = Enumerable.Range(0, 4).Select(index =>
            new SceneMaterial("same-source.pmx", index * 3, 3, new(.8f, .6f, .4f, 1), new(.1f), new(.2f), 8)).ToArray();
        var snapshot = new SceneSnapshot(vertices, Enumerable.Repeat(new uint[] { 0, 1, 2 }, 4).SelectMany(v => v).ToArray(), materials);
        var surface = new OriginalStyleScene(snapshot, materials.Select(_ => new OriginalStyleMaterialBinding(new(.3f))));
        var flags = Enumerable.Range(0, 4).Select(index => new OriginalFrameMaterialFlags(index % 2 == 0, index % 2 != 0, true)
        { GiToonResponseOverride = index % 2 == 0 ? RayGiToonResponse.Continuous : null });
        return new(surface, options: new(OriginalAlphaBlendMode.Normal, true, false), materials: flags);
    }

    private static OriginalFrameScene GpuFrame()
    {
        var box = D3D12OriginalOutlineTests.Box();
        var original = box.Surface.Snapshot;
        const float nDotL = .175f;
        var normal = new Vector3(nDotL, 0, -MathF.Sqrt(1 - nDotL * nDotL));
        var vertices = original.Vertices.ToArray().Select(vertex => vertex with { Normal = normal }).ToArray();
        var material = original.Materials[0] with { Diffuse = new(.6f, .4f, .2f, 1) };
        var mesh = new SceneSnapshot(vertices, original.Indices, [material], camera: original.Camera, lightDirection: Vector3.UnitX);
        var surface = new OriginalStyleScene(mesh, [new(new(.2f))], box.Surface.Profile);
        var options = D3D12OriginalFrameTests.Options(shadow: new(Matrix4x4.CreateScale(.2f, .2f, .1f),
            32, 32, false, false, OriginalTextureSampling.PointClamp, OriginalDepthComparison.LessEqual));
        return new(surface, box.Outline, box.OutlineOptions, options);
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
            throw new InvalidOperationException("Preset test cleanup must stay within its test cache.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
