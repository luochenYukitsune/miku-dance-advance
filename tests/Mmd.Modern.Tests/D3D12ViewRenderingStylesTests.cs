using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12ViewRenderingStylesTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Environment.GetEnvironmentVariable("MMD_STYLE_PRESET_TEST_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "style-preset-tests"), Guid.NewGuid().ToString("N"));

    [Fact]
    public void InstanceIdentityMapsDifferentPresetsWithoutNamesOrSourceMutation()
    {
        var store = new D3D12StylePresetStore(directory);
        var first = store.SaveAsNew("Same name", "mmd", new(.3f), new(.6f, .12f));
        var second = store.SaveAsNew("Same name", "mmd", new(.7f, new Vector4(.1f, .2f, .3f, 1)), new(.4f, .2f));
        var catalog = store.Load();
        var source = Frame();
        var original = source.Materials.ToArray();
        SceneRenderingStyleBinding[] bindings =
        [Binding(2, 2, second.StableId), Binding(0, 2, first.StableId), Binding(2, 0, "mmd", visible: false)];

        var output = D3D12ViewRenderingStyles.Apply(source, catalog, bindings);

        Assert.Same(source.Surface, output.Surface);
        Assert.Same(source.Outline, output.Outline);
        Assert.Same(source.OutlineOptions, output.OutlineOptions);
        Assert.Same(source.Options, output.Options);
        Assert.Same(source.Lighting, output.Lighting);
        Assert.Same(source.EditorOverlay, output.EditorOverlay);
        for (int i = 0; i < source.Materials.Count; i++)
        {
            var preset = i < 2 ? first : second;
            Assert.Equal(original[i] with
            {
                CharacterOutlineStyle = preset.CharacterOutlineStyle,
                CharacterToonResponse = preset.CharacterToonResponse,
                GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands,
            }, output.Materials[i]);
        }
        Assert.Equal(original, source.Materials);
        Assert.All(source.Surface.Snapshot.Materials, material => Assert.Equal("same-source.pmx", material.Name));
        bindings[0] = bindings[0] with { Preset = new(first.StableId) };
        Assert.Equal(second.CharacterOutlineStyle, output.Materials[2].CharacterOutlineStyle);
        // A detached injected catalog is usable without opening the library again on a frame.
        File.Delete(Path.Combine(directory, second.StableId + ".json"));
        Assert.Equal(second.CharacterOutlineStyle,
            D3D12ViewRenderingStyles.Apply(source, catalog, [Binding(0, 4, second.StableId)]).Materials[0].CharacterOutlineStyle);
    }

    [Fact]
    public void MixedCharacterEnvironmentPropAndExplicitUnspecifiedPreserveTheIncomingRolePolicy()
    {
        var source = Frame();
        var flags = source.Materials.ToArray();
        SceneRenderingStyleBinding[] bindings =
        [Binding(0, 1), Binding(1, 1, role: ModelRole.Environment), Binding(2, 1, role: ModelRole.Prop),
            Binding(3, 1, role: ModelRole.Unspecified), Binding(4, 0, visible: false)];

        var output = D3D12ViewRenderingStyles.Apply(source, D3D12StylePresetCatalog.BuiltIn, bindings);

        Assert.Same(source, output);
        for (int i = 0; i < 4; i++) Assert.Same(flags[i], output.Materials[i]);
        Assert.Equal(flags, source.Materials);
    }

    [Fact]
    public void DefaultProjectMmdPreservesExplicitPerMaterialOutlinesWhileCustomMmdOverrides()
    {
        var original = Frame();
        CharacterOutlineStyle[] explicitStyles =
        [new(.23f, new Vector4(.2f, .3f, .4f, .8f), Enabled: false), new(.41f), new(0)];
        var source = new OriginalFrameScene(original.Surface, original.Outline, original.OutlineOptions, original.Options,
            original.Materials.Select((flags, index) => flags with
            { CharacterOutlineStyle = index < explicitStyles.Length ? explicitStyles[index] : null }),
            original.Lighting, original.EditorOverlay);
        var originalFlags = source.Materials.ToArray();
        var policy = SceneModelShadingPolicy.Apply(source, ModelRole.Character);
        var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", "same-source.pmx", ModelRole.Character, source);
        using var project = new SceneProjectSession(layout, new(), [], new ProjectSettingsBackendProvider(
            (_, _) => throw new InvalidOperationException("No physics in the static style fixture."),
            _ => throw new InvalidOperationException("No physics in the static style fixture.")));
        var snapshot = project.CaptureRenderSnapshot();
        Assert.Equal("mmd", Assert.Single(snapshot.RenderingStyles).Preset.StableId);
        Assert.Equal(policy.Materials, snapshot.Scene.Materials);
        var resolved = D3D12ViewScene.Resolve(snapshot.Scene, snapshot.Camera, snapshot.Light,
            snapshot.SelfShadow, snapshot.MorphChannels, new(), snapshot.ResolvedCamera);
        var incomingFlags = resolved.Materials.ToArray();

        var output = D3D12ViewRenderingStyles.Apply(resolved, D3D12StylePresetCatalog.BuiltIn, snapshot.RenderingStyles);

        Assert.Equal(incomingFlags, output.Materials);
        for (int index = 0; index < explicitStyles.Length; index++)
            Assert.Same(explicitStyles[index], output.Materials[index].CharacterOutlineStyle);
        Assert.Null(output.Materials[3].CharacterOutlineStyle);
        Assert.Same(resolved.Surface, output.Surface);
        Assert.Same(resolved.Outline, output.Outline);
        Assert.Same(resolved.OutlineOptions, output.OutlineOptions);
        Assert.Same(resolved.Options, output.Options);
        Assert.Same(resolved.Lighting, output.Lighting);
        Assert.Same(resolved.EditorOverlay, output.EditorOverlay);

        var store = new D3D12StylePresetStore(directory);
        var custom = store.SaveAsNew("Explicit MMD", "mmd", new(.7f, Vector4.One), new(.6f, .12f));
        project.SetModelRenderingStyle(actor.Id, new(custom.StableId), TestContext.Current.CancellationToken);
        var selected = project.CaptureRenderSnapshot();
        Assert.Same(snapshot.Scene, selected.Scene);
        var customOutput = D3D12ViewRenderingStyles.Apply(resolved, store.Load(), selected.RenderingStyles);
        for (int index = 0; index < incomingFlags.Length; index++)
            Assert.Equal(incomingFlags[index] with
            {
                CharacterOutlineStyle = custom.CharacterOutlineStyle,
                CharacterToonResponse = custom.CharacterToonResponse,
                GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands,
            }, customOutput.Materials[index]);
        Assert.Equal(incomingFlags, resolved.Materials);
        Assert.Equal(policy.Materials, project.Frame.Materials);
        Assert.Equal(originalFlags, source.Materials);
        Assert.Null(source.Materials[3].CharacterOutlineStyle);
    }

    [Fact]
    public void PreflightReportsEveryUnresolvedReferenceAndAllAffectedVisibleAndHiddenInstances()
    {
        var source = Frame();
        var flags = source.Materials.ToArray();
        SceneRenderingStyleBinding[] bindings =
        [Binding(0, 1, "custom-missing-a"), Binding(1, 0, "custom-missing-a", visible: false),
            Binding(1, 1, "custom-missing-b", ModelRole.Environment), Binding(2, 1, "custom-missing-c"), Binding(3, 1)];

        var report = D3D12ViewRenderingStyles.Preflight(source, D3D12StylePresetCatalog.BuiltIn, bindings);

        Assert.False(report.IsAvailable);
        Assert.Equal(new[] { "custom-missing-a", "custom-missing-b", "custom-missing-c" },
            report.Failures.Select(failure => failure.Preset.StableId));
        Assert.Equal(new[] { bindings[0].ObjectId, bindings[1].ObjectId }, report.Failures[0].AffectedObjectIds);
        Assert.Equal(new[] { bindings[2].ObjectId }, report.Failures[1].AffectedObjectIds);
        Assert.Equal(new[] { bindings[3].ObjectId }, report.Failures[2].AffectedObjectIds);
        Assert.Equal(D3D12StylePresetStatus.Missing, report.Failures[0].Resolution.Status);
        Assert.Equal(D3D12StylePresetStatus.Missing, report.Failures[2].Resolution.Status);
        var preflightError = Assert.Throws<D3D12ViewRenderingStylesException>(report.EnsureAvailable);
        Assert.Same(report, preflightError.Preflight);
        var error = Assert.Throws<D3D12ViewRenderingStylesException>(() =>
            D3D12ViewRenderingStyles.Apply(source, D3D12StylePresetCatalog.BuiltIn, bindings));
        Assert.Equal(3, error.Failures.Count);
        Assert.Contains("custom-missing-a", error.Message);
        Assert.Contains("custom-missing-b", error.Message);
        Assert.Contains("custom-missing-c", error.Message);
        Assert.Throws<NotSupportedException>(() => ((IList<Guid>)report.Failures[0].AffectedObjectIds)[0] = Guid.NewGuid());
        Assert.Equal(flags, source.Materials);
        Assert.All(source.Materials, item => Assert.Null(item.CharacterOutlineStyle));
    }

    [Theory]
    [InlineData("custom-missing-c")]
    [InlineData("custom-missing-d")]
    public void HiddenMissingPresetNeverFallsBackToMmd(string preset)
    {
        var report = D3D12ViewRenderingStyles.Preflight(Frame(), D3D12StylePresetCatalog.BuiltIn,
            [Binding(0, 4), Binding(4, 0, preset, visible: false)]);
        Assert.False(report.IsAvailable);
        var failure = Assert.Single(report.Failures);
        Assert.Equal(preset, failure.Preset.StableId);
        Assert.Equal(D3D12StylePresetStatus.Missing, failure.Resolution.Status);
    }

    [Theory]
    [InlineData("negative-start")]
    [InlineData("negative-count")]
    [InlineData("outside-start")]
    [InlineData("outside-count")]
    [InlineData("overflow-start")]
    [InlineData("overflow-count")]
    [InlineData("empty-identity")]
    [InlineData("duplicate-identity")]
    [InlineData("duplicate-hidden-identity")]
    [InlineData("undefined-role")]
    [InlineData("default-preset")]
    [InlineData("null-binding")]
    [InlineData("hidden-materials")]
    [InlineData("gap")]
    [InlineData("overlap")]
    [InlineData("incomplete-end")]
    public void InvalidWholeBindingListsRejectBeforeChangingAnyFrame(string kind)
    {
        var source = Frame();
        var flags = source.Materials.ToArray();
        var first = Binding(0, 2);
        var second = Binding(2, 2);
        second = kind switch
        {
            "negative-start" => second with { MaterialStart = -1 },
            "negative-count" => second with { MaterialCount = -1 },
            "outside-start" => second with { MaterialStart = 5 },
            "outside-count" => second with { MaterialCount = 3 },
            "overflow-start" => second with { MaterialStart = int.MaxValue },
            "overflow-count" => second with { MaterialCount = int.MaxValue },
            "empty-identity" => second with { ObjectId = Guid.Empty },
            "duplicate-identity" => second with { ObjectId = first.ObjectId },
            "duplicate-hidden-identity" => second with { ObjectId = first.ObjectId, Visible = false, MaterialCount = 0 },
            "undefined-role" => second with { Role = (ModelRole)999 },
            "default-preset" => second with { Preset = default },
            "null-binding" => null!,
            "hidden-materials" => second with { Visible = false },
            "gap" => second with { MaterialStart = 3, MaterialCount = 1 },
            "overlap" => second with { MaterialStart = 1 },
            "incomplete-end" => second with { MaterialCount = 1 },
            _ => throw new InvalidOperationException(),
        };
        Assert.ThrowsAny<ArgumentException>(() => D3D12ViewRenderingStyles.Apply(source,
            D3D12StylePresetCatalog.BuiltIn, [first, second]));
        Assert.Equal(flags, source.Materials);
    }

    [Fact]
    public void LegacyEmptyBindingsKeepIdentityAndZeroMaterialInstancesConsumeNoRange()
    {
        var source = Frame();
        var catalog = D3D12StylePresetCatalog.BuiltIn;
        Assert.Same(source, D3D12ViewRenderingStyles.Apply(source, catalog, []));
        Assert.True(D3D12ViewRenderingStyles.Preflight(source, catalog, []).IsAvailable);
        Assert.Same(source, D3D12ViewRenderingStyles.Apply(source, catalog,
            [Binding(0, 4, role: ModelRole.Environment), Binding(2, 0), Binding(4, 0, visible: false)]));
        Assert.Throws<ArgumentNullException>(() => D3D12ViewRenderingStyles.Apply(null!, catalog, []));
        Assert.Throws<ArgumentNullException>(() => D3D12ViewRenderingStyles.Apply(source, null!, []));
        Assert.Throws<ArgumentNullException>(() => D3D12ViewRenderingStyles.Apply(source, catalog, null!));
    }

    [Fact]
    public void ViewResolverSuppliesModernOptionsBeforeStylesAndKeepsCameraQualityAndLighting()
    {
        var original = Frame();
        var legacy = new OriginalFrameScene(original.Surface, original.Outline, original.OutlineOptions,
            materials: original.Materials, lighting: original.Lighting, editorOverlay: original.EditorOverlay);
        var camera = original.Surface.Snapshot.Camera;
        var quality = new ViewQualityOptions(Antialiasing: FrameAntialiasing.Smaa1X, SpatialSamples: 4,
            SmaaQuality: SmaaQuality.Ultra);
        var resolved = D3D12ViewScene.Resolve(legacy, null, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), quality, camera);

        var output = D3D12ViewRenderingStyles.Apply(resolved, D3D12StylePresetCatalog.BuiltIn, [Binding(0, 4)]);

        Assert.Null(legacy.Options);
        Assert.Same(resolved.Options, output.Options);
        Assert.Same(camera, output.Options!.CameraOverride);
        Assert.Equal(FrameAntialiasing.Smaa1X, output.Options.Antialiasing);
        Assert.Equal(4, output.Options.SpatialSamples);
        Assert.Equal(SmaaQuality.Ultra, output.Options.SmaaQuality);
        Assert.Same(legacy.Lighting, output.Lighting);
        Assert.Same(legacy.EditorOverlay, output.EditorOverlay);
        Assert.Equal(resolved.Materials, output.Materials);
        Assert.Null(output.Materials[0].CharacterToonResponse);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public async Task CaptureConsumesInjectedStylesRestoresDefaultsAndRejectsBeforeGpuCountersAdvance()
    {
        const int width = 257, height = 191;
        var source = GpuFrame();
        var flags = source.Materials.ToArray();
        var store = new D3D12StylePresetStore(directory);
        var defaults = store.SaveAsNew("Explicit modern parameters", "mmd", new(), new());
        var darker = store.SaveAsNew("Darker", "mmd", new(), new(.7f, .1f));
        var wider = store.SaveAsNew("Wider", "mmd", new(1.5f), new());
        var catalog = store.Load();
        var request = new ViewRenderRequest(width, height);
        var plan = new OfflineFramePlan(width, height, new(30), 0, 10);
        var instance = Guid.NewGuid();
        OfflineFrame Selected(int index, string id) => new(plan.GetSample(index), width, height, source, null, null, null)
        { RenderingStyles = Array.AsReadOnly(new[] { Binding(0, source.Materials.Count, id) with { ObjectId = instance } }) };
        var token = TestContext.Current.CancellationToken;
        await using var renderer = new D3D12ViewRenderer(request, stylePresets: catalog);
        Assert.False(renderer.IsWarp);
        // The renderer's preflight also serves the neutral preview/export coordinators.
        var invalid = Selected(0, "custom-missing");
        var preflight = (IViewRenderingStylePreflight)renderer;
        Assert.Throws<D3D12ViewRenderingStylesException>(() => preflight.ValidateRenderingStyles(invalid.Scene, invalid.RenderingStyles));
        await Assert.ThrowsAsync<D3D12ViewRenderingStylesException>(() => renderer.CaptureAsync(invalid, token).AsTask());
        Assert.Null(renderer.Statistics);

        async Task<byte[]> Capture(OfflineFrame frame)
        {
            using var pixels = await renderer.CaptureAsync(frame, token);
            return pixels.Pixels.ToArray();
        }
        var original = await Capture(Selected(0, "mmd") with { RenderingStyles = [] });
        Assert.Equal(1, renderer.Statistics!.FrameId);
        var manual = new OriginalFrameScene(source.Surface, source.Outline, source.OutlineOptions, source.Options,
            source.Materials.Select(item => item with
            {
                CharacterOutlineStyle = new(), CharacterToonResponse = new(),
                GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands,
            }));
        var expected = await Capture(Selected(1, "mmd") with { Scene = manual, RenderingStyles = [] });
        var selectedDefault = await Capture(Selected(2, defaults.StableId));
        Assert.Equal(expected, selectedDefault);
        var darkPixels = await Capture(Selected(3, darker.StableId));
        int center = (height / 2 * width + width / 2) * 4;
        Assert.True(selectedDefault[center] > darkPixels[center] + 50);
        var widePixels = await Capture(Selected(4, wider.StableId));
        Assert.True(Enumerable.Range(0, width * height).Count(pixel =>
            !selectedDefault.AsSpan(pixel * 4, 4).SequenceEqual(widePixels.AsSpan(pixel * 4, 4))) > 50);
        Assert.Equal(selectedDefault.AsSpan(center, 4).ToArray(), widePixels.AsSpan(center, 4).ToArray());
        Assert.Equal(selectedDefault, await Capture(Selected(5, defaults.StableId)));
        var before = renderer.Statistics!;
        await Assert.ThrowsAsync<D3D12ViewRenderingStylesException>(() => renderer.CaptureAsync(Selected(6, "custom-missing-c"), token).AsTask());
        Assert.Same(before, renderer.Statistics);
        Assert.Equal(original, await Capture(Selected(6, "mmd") with { RenderingStyles = [] }));
        Assert.Equal(before.FrameId + 1, renderer.Statistics!.FrameId);
        Assert.Equal(before.TotalSubmissions + 1, renderer.Statistics.TotalSubmissions);
        Assert.Equal(before.TotalReadbacks + 1, renderer.Statistics.TotalReadbacks);
        Assert.Equal(before.SubmittedFence + 1, renderer.Statistics.SubmittedFence);
        Assert.Equal(flags, source.Materials);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public async Task RejectedCaptureDoesNotCommitOfflineTemporalContinuity()
    {
        const int width = 96, height = 72;
        var source = D3D12RayGiTests.Bounce();
        var request = new ViewRenderRequest(width, height, new(Gi: ViewGiTemporalAdapterTests.Options));
        var plan = new OfflineFramePlan(width, height, new(30), 0, 3);
        var binding = Binding(0, source.Materials.Count, role: ModelRole.Environment);
        OfflineFrame FrameAt(int index, string id) => new(plan.GetSample(index), width, height, source, null, null, null)
        { RenderingStyles = Array.AsReadOnly(new[] { binding with { Preset = new(id) } }) };
        await using var renderer = new D3D12ViewRenderer(request);
        var token = TestContext.Current.CancellationToken;
        using (await renderer.CaptureAsync(FrameAt(0, "mmd"), token)) { }
        await Assert.ThrowsAsync<D3D12ViewRenderingStylesException>(() => renderer.CaptureAsync(FrameAt(1, "custom-missing"), token).AsTask());
        using (await renderer.CaptureAsync(FrameAt(1, "mmd"), token)) { }
        Assert.False(renderer.RayGiStatistics!.TemporalResetHistory);
        Assert.Equal(2UL, renderer.RayGiStatistics.TemporalHistoryFrames);
        Assert.Equal(2, renderer.Statistics!.FrameId);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public async Task RealtimeRejectsBeforePresentAndContinuesWithoutReadback()
    {
        nint window = CreateWindowExW(0, "STATIC", "MMD style view adapter test", 0x80000000, 0, 0, 128, 96, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            var scene = GpuFrame();
            var binding = Binding(0, scene.Materials.Count);
            var frame = new PreviewFrame(96, 72, 0, scene, null, null, null)
            { RenderingStyles = Array.AsReadOnly(new[] { binding }) };
            await using var renderer = new D3D12RealtimeViewRenderer(window, new(96, 72));
            var token = TestContext.Current.CancellationToken;
            await renderer.PresentAsync(frame, token);
            var before = renderer.Statistics;
            var invalid = frame with { RenderingStyles = Array.AsReadOnly(new[] { binding with { Preset = new("custom-missing") } }) };
            Assert.Throws<D3D12ViewRenderingStylesException>(() =>
                ((IViewRenderingStylePreflight)renderer).ValidateRenderingStyles(invalid.Scene, invalid.RenderingStyles));
            await Assert.ThrowsAsync<D3D12ViewRenderingStylesException>(() => renderer.PresentAsync(invalid, token).AsTask());
            Assert.Equal(before.PresentCount, renderer.Statistics.PresentCount);
            Assert.Equal(before.Frame!.FrameId, renderer.Statistics.Frame!.FrameId);
            Assert.Equal(before.Frame.TotalSubmissions, renderer.Statistics.Frame.TotalSubmissions);
            Assert.Equal(before.Frame.SubmittedFence, renderer.Statistics.Frame.SubmittedFence);
            await renderer.PresentAsync(frame, token);
            Assert.Equal(2, renderer.Statistics.PresentCount);
            Assert.Equal(before.Frame.FrameId + 1, renderer.Statistics.Frame!.FrameId);
            Assert.Equal(before.Frame.TotalSubmissions + 1, renderer.Statistics.Frame.TotalSubmissions);
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
        var outline = new OriginalOutlineData(vertices.Select(vertex =>
            new OriginalOutlineVertex(vertex.Position, vertex.Normal, OriginalOutlineVertexMode.Expand, 1, 0)),
            materials.Select(_ => new OriginalOutlineMaterial(2, Vector4.One, false)));
        var flags = Enumerable.Range(0, 4).Select(index => new OriginalFrameMaterialFlags(index % 2 == 0, index % 2 != 0, true,
            SurfaceEnabled: index != 2, ShadowDoubleSided: index == 1, CullTransparentSurface: index == 3)
            { GiToonResponseOverride = index % 2 == 0 ? RayGiToonResponse.Continuous : null });
        return new(surface, outline, OriginalOutlineTests.Options(), new(OriginalAlphaBlendMode.Normal, true, false), flags,
            SceneLightingSnapshot.Empty, new ViewportOverlaySnapshot([]));
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
