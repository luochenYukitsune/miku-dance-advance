using System.Collections.ObjectModel;
using System.Numerics;
using System.Threading.Channels;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(EditorFrameAgeDiagnosticsCollection.Name)]
public sealed class EditorViewportRecordAheadParityTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private readonly CancellationToken Token = TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [MemberData(nameof(GiModesForRecordAhead))]
    public async Task PlaybackEnablesRecordAheadRegardlessOfGiModeWhilePausedAndTemporalUpscalingUsePermitPath(
        ViewQualityOptions previewQuality) => await session.Dispatch(async () =>
    {
        var renderer = new FakeRenderer { GateFramePermits = true, SupportsRecordingAhead = true };
        using var f = Create(_ => renderer, host => host.Quality = previewQuality);
        try
        {
            // Paused frame 0: must acquire permit 0 and present synchronously via PresentAsync.
            Assert.Equal(0, await Next(renderer.PermitWaits));
            Assert.False(renderer.AheadFrames.Reader.TryRead(out _));
            renderer.ReleasePermit(0);
            Assert.Equal(0, (await Next(renderer.Frames)).SourceFrame);
            renderer.Release(0);
            Assert.Equal(1, (await Next(f.Callbacks)).PresentCount);

            // Paused frame 1: maintains single-frame edit latency contract (still waits for permit).
            f.Request(1);
            Assert.Equal(1, await Next(renderer.PermitWaits));
            Assert.False(renderer.AheadFrames.Reader.TryRead(out _));
            renderer.ReleasePermit(1);
            Assert.Equal(1, (await Next(renderer.Frames)).SourceFrame);
            renderer.Release(1);
            Assert.Equal(2, (await Next(f.Callbacks)).PresentCount);

            // Enter playback: GI Off / Software / Hardware all record ahead via PresentAheadAsync.
            f.Host.SetPlaybackActive(true);
            f.Request(2);
            Assert.Equal(2, (await Next(renderer.AheadFrames)).SourceFrame);
            Assert.False(renderer.PermitWaits.Reader.TryRead(out _));
            renderer.ReleaseAhead(0);
            Assert.Equal(2, (await Next(renderer.Frames)).SourceFrame);
            renderer.Release(2);
            Assert.Equal(3, (await Next(f.Callbacks)).PresentCount);

            // Next playback tick also records ahead.
            f.Request(3);
            Assert.Equal(3, (await Next(renderer.AheadFrames)).SourceFrame);
            Assert.False(renderer.PermitWaits.Reader.TryRead(out _));

            // Pause during an active ahead recording: cancels ahead recording and restores permit wait.
            f.Host.SetPlaybackActive(false);
            f.Request(4);
            Assert.Equal(2, await Next(renderer.PermitWaits));
            Assert.False(renderer.Frames.Reader.TryRead(out _));
            renderer.ReleasePermit(2);
            Assert.Equal(4, (await Next(renderer.Frames)).SourceFrame);
            renderer.Release(3);
            Assert.Equal(4, (await Next(f.Callbacks)).PresentCount);
            Assert.False(f.Errors.Reader.TryRead(out _));
            return true;
        }
        finally { Cleanup(f, renderer); }
    }, Token).WaitAsync(Timeout + TimeSpan.FromSeconds(2), Token);

    [Fact]
    public async Task TemporalUpscalingDisablesRecordAheadAndSwitchingQualityTransitionsCleanly() => await session.Dispatch(async () =>
    {
        var renderer = new FakeRenderer { GateFramePermits = true, SupportsRecordingAhead = true };
        var temporalQuality = new ViewQualityOptions(
            Gi: RayGiOptions.Off,
            Antialiasing: null,
            SpatialSamples: 1,
            TemporalUpscaling: new ViewTemporalUpscalingOptions { Mode = ViewTemporalUpscalingMode.Fsr });
        var previewOffQuality = EditorPreferences.DefaultPreviewQuality;

        using var f = Create(_ => renderer, host =>
        {
            host.Quality = temporalQuality;
            host.SetPlaybackActive(true);
        });
        try
        {
            // Initial frame with TemporalUpscaling != Off uses strict permit + PresentAsync.
            Assert.Equal(0, await Next(renderer.PermitWaits));
            renderer.ReleasePermit(0);
            Assert.Equal(0, (await Next(renderer.Frames)).SourceFrame);
            renderer.Release(0);
            Assert.Equal(1, (await Next(f.Callbacks)).PresentCount);

            // Even during playback, TemporalUpscaling != Off must not call PresentAheadAsync.
            f.Request(1);
            Assert.Equal(1, await Next(renderer.PermitWaits));
            Assert.False(renderer.AheadFrames.Reader.TryRead(out _));
            renderer.ReleasePermit(1);
            Assert.Equal(1, (await Next(renderer.Frames)).SourceFrame);
            renderer.Release(1);
            Assert.Equal(2, (await Next(f.Callbacks)).PresentCount);

            // Switch TemporalUpscaling Off: one strict permit frame establishes new recording epoch,
            // then subsequent playback frames resume PresentAheadAsync.
            f.Host.Quality = previewOffQuality;
            f.Request(2);
            Assert.Equal(2, await Next(renderer.PermitWaits));
            Assert.False(renderer.AheadFrames.Reader.TryRead(out _));
            renderer.ReleasePermit(2);
            Assert.Equal(2, (await Next(renderer.Frames)).SourceFrame);
            renderer.Release(2);
            Assert.Equal(3, (await Next(f.Callbacks)).PresentCount);

            f.Request(3);
            Assert.Equal(3, (await Next(renderer.AheadFrames)).SourceFrame);

            // Re-enable TemporalUpscaling during ahead recording: cancels ahead and falls back to permit.
            f.Host.Quality = temporalQuality;
            f.Request(4);
            Assert.Equal(3, await Next(renderer.PermitWaits));
            renderer.ReleasePermit(3);
            Assert.Equal(4, (await Next(renderer.Frames)).SourceFrame);
            renderer.Release(3);
            Assert.Equal(4, (await Next(f.Callbacks)).PresentCount);
            Assert.False(f.Errors.Reader.TryRead(out _));
            return true;
        }
        finally { Cleanup(f, renderer); }
    }, Token).WaitAsync(Timeout + TimeSpan.FromSeconds(2), Token);

    public static TheoryData<ViewQualityOptions> GiModesForRecordAhead() =>
    [
        EditorPreferences.DefaultPreviewQuality,
        new ViewQualityOptions(Gi: RayGiOptions.Off),
        new ViewQualityOptions(Gi: new RayGiOptions(Mode: LightingMode.SoftwareRayTracing, Strength: 1f)),
        new ViewQualityOptions(Gi: new RayGiOptions(Mode: LightingMode.HardwareRayTracing, Strength: 1f)),
    ];

    [Fact]
    public void DefaultMmdFastPathAndCacheMatchDirectResolveAndReuseAcrossFrames()
    {
        var charId = Guid.NewGuid();
        var propId = Guid.NewGuid();
        var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false);
        var frame1 = CreateTwoObjectFrame(Vector3.UnitZ, new Vector3(0, 1, -3), options);
        var frame2 = CreateTwoObjectFrame(Vector3.Normalize(new Vector3(1, 1, 0)), new Vector3(2, 2, -4), options);
        var bindings1 = Array.AsReadOnly(new SceneRenderingStyleBinding[]
        {
            new(charId, new("mmd"), ModelRole.Character, 0, 2, true),
            new(propId, new("mmd"), ModelRole.Prop, 2, 1, true),
        });
        var bindings2 = Array.AsReadOnly(new SceneRenderingStyleBinding[]
        {
            new(charId, new("mmd"), ModelRole.Character, 0, 2, true),
            new(propId, new("mmd"), ModelRole.Prop, 2, 1, true),
        });

        var direct1 = D3D12ViewRenderingStyles.Resolve(frame1, D3D12StylePresetCatalog.BuiltIn, bindings1);
        var direct2 = D3D12ViewRenderingStyles.Resolve(frame2, D3D12StylePresetCatalog.BuiltIn, bindings2);
        Assert.Same(frame1, direct1.Scene);
        Assert.Same(frame2, direct2.Scene);
        Assert.Same(direct1.SurfaceStyles, direct2.SurfaceStyles);
        Assert.True(direct1.SurfaceStyles.IsDefault);

        // Fixed-config cache (mirrors D3D12RealtimeViewRenderer) and dynamic cache both reuse SurfaceStyles.
        var fixedCache = new D3D12ViewRenderingStylesCache(
            D3D12StylePresetCatalog.BuiltIn,
            [],
            [],
            [],
            D3D12ViewStyleCatalog.Empty,
            null);
        var dynamicCache = new D3D12ViewRenderingStylesCache();

        var fixed1 = fixedCache.Resolve(frame1, bindings1);
        var fixed2 = fixedCache.Resolve(frame2, bindings2);
        var fixedRepeat = fixedCache.Resolve(frame2, bindings2);
        Assert.Same(frame1, fixed1.Scene);
        Assert.Same(frame2, fixed2.Scene);
        Assert.Same(direct1.SurfaceStyles, fixed1.SurfaceStyles);
        Assert.Same(fixed1.SurfaceStyles, fixed2.SurfaceStyles);
        Assert.Same(fixed2, fixedRepeat);
        AssertResultEquivalent(direct1, fixed1);
        AssertResultEquivalent(direct2, fixed2);

        var dyn1 = dynamicCache.Resolve(frame1, D3D12StylePresetCatalog.BuiltIn, bindings1);
        var dyn2 = dynamicCache.Resolve(frame2, D3D12StylePresetCatalog.BuiltIn, bindings2);
        var dynEmpty1 = dynamicCache.Resolve(frame1, D3D12StylePresetCatalog.BuiltIn, []);
        var dynEmpty2 = dynamicCache.Resolve(frame2, D3D12StylePresetCatalog.BuiltIn, []);
        AssertResultEquivalent(direct1, dyn1);
        AssertResultEquivalent(direct2, dyn2);
        Assert.Same(direct1.SurfaceStyles, dynEmpty1.SurfaceStyles);
        Assert.Same(dynEmpty1.SurfaceStyles, dynEmpty2.SurfaceStyles);

        // Zero-allocation check on repeated identical call to fixed/dynamic cache.
        _ = MeasureFixedRepeat(fixedCache, frame2, bindings2);
        Assert.Equal(0, MeasureFixedRepeat(fixedCache, frame2, bindings2));
    }

    [Fact]
    public void CustomPresetsAndMaterialOverridesMatchDirectResolveAndRecomputeOnOptionOrOverrideChanges()
    {
        var charId = Guid.NewGuid();
        var propId = Guid.NewGuid();
        var customPreset = new D3D12StylePresetDefinition(
            "custom-cel",
            "Custom Cel",
            "mmd",
            new CharacterOutlineStyle(2.5f, new Vector4(0.1f, 0.2f, 0.3f, 1f), true),
            new CharacterToonResponse(0.45f, 0.08f));
        var customCatalog = new D3D12StylePresetCatalog([
            new D3D12StylePresetResult("custom-cel", D3D12StylePresetStatus.Available, customPreset, "")
        ]);

        var baseOptions = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false);
        var customOptions = baseOptions with { Antialiasing = FrameAntialiasing.Smaa1X, SpatialSamples = 2 };
        var frameA = CreateTwoObjectFrame(Vector3.UnitZ, new Vector3(0, 1, -3), baseOptions);
        var frameB = CreateTwoObjectFrame(Vector3.UnitX, new Vector3(1, 2, -5), customOptions);

        var bindings = new SceneRenderingStyleBinding[]
        {
            new(charId, new("custom-cel"), ModelRole.Character, 0, 2, true),
            new(propId, new("mmd"), ModelRole.Prop, 2, 1, true),
        };
        var charMats = new List<D3D12ViewCharacterMaterial>
        {
            new(charId, 0, D3D12CharacterMaterialRole.Face, FaceNormalShadeStrength: 0.4f, FaceGeometricShadowStrength: 0.8f),
        };
        var outlines = new List<D3D12ViewOutlineWidthBinding>
        {
            new(charId, 1, 0.5f),
        };

        var cache = new D3D12ViewRenderingStylesCache();
        var directA = D3D12ViewRenderingStyles.Resolve(frameA, customCatalog, bindings, charMats, outlineWidths: outlines);
        var cachedA1 = cache.Resolve(frameA, customCatalog, bindings, charMats, outlineWidths: outlines);
        var cachedA2 = cache.Resolve(frameA, customCatalog, bindings, charMats, outlineWidths: outlines);
        var cachedB = cache.Resolve(frameB, customCatalog, bindings, charMats, outlineWidths: outlines);

        AssertResultEquivalent(directA, cachedA1);
        Assert.Same(cachedA1, cachedA2);
        AssertResultEquivalent(D3D12ViewRenderingStyles.Resolve(frameB, customCatalog, bindings, charMats, outlineWidths: outlines), cachedB);
        Assert.Same(cachedA1.SurfaceStyles, cachedB.SurfaceStyles);
        Assert.Same(cachedB.Scene.Options, customOptions);

        // Mutate external lists (charMats and outlines) and verify the cache detects changes accurately.
        charMats.Add(new(charId, 1, D3D12CharacterMaterialRole.Hair, FaceNormalShadeStrength: 0.9f));
        outlines[0] = new(charId, 1, 1.5f);
        var directUpdated = D3D12ViewRenderingStyles.Resolve(frameB, customCatalog, bindings, charMats, outlineWidths: outlines);
        var cachedUpdated = cache.Resolve(frameB, customCatalog, bindings, charMats, outlineWidths: outlines);
        AssertResultEquivalent(directUpdated, cachedUpdated);
        Assert.NotSame(cachedB.SurfaceStyles, cachedUpdated.SurfaceStyles);

        // Switching to null options rejects non-null CharacterToonResponse preset identically to direct Resolve.
        var noOptionsFrame = CreateTwoObjectFrame(Vector3.UnitZ, new Vector3(0, 1, -3), null);
        Assert.Throws<NotSupportedException>(() => D3D12ViewRenderingStyles.Resolve(noOptionsFrame, customCatalog, bindings, charMats, outlineWidths: outlines));
        Assert.Throws<NotSupportedException>(() => cache.Resolve(noOptionsFrame, customCatalog, bindings, charMats, outlineWidths: outlines));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PluginBindingsGlobalParametersAndSceneInputsMatchDirectResolveAndReuseWhenUnchanged(bool reuseInputDictionary)
    {
        var charId = Guid.NewGuid();
        var propId = Guid.NewGuid();
        var viewCatalog = TestStylePlugins.AllViewCatalog;
        var presetCatalog = TestStylePlugins.AllPresetCatalog;

        var baseOptions = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false);
        var baseFrame = CreateTwoObjectFrame(
            Vector3.UnitZ,
            new Vector3(0, 1, -3),
            baseOptions,
            materialNames: ["face", "hair", "body", "prop"]);
        var movedCameraFrame = CreateTwoObjectFrame(
            Vector3.UnitZ,
            new Vector3(1, 2, -4),
            baseOptions,
            materialNames: ["face", "hair", "body", "prop"]);

        var bindings = new SceneRenderingStyleBinding[]
        {
            new(charId, new("genshin"), ModelRole.Character, 0, 3, true),
            new(propId, new("mmd"), ModelRole.Prop, 3, 1, true),
        };
        var charMats = new D3D12ViewCharacterMaterial[]
        {
            new(charId, 0, D3D12CharacterMaterialRole.Face, FaceNormalShadeStrength: 0.5f),
            new(charId, 1, D3D12CharacterMaterialRole.Hair),
        };
        var pluginBindings = new List<D3D12ViewPluginMaterialBinding>
        {
            TestStylePlugins.Bind(charId, 0, TestStylePlugins.FaceSdf("genshin", 0, secondaryTextureSlot: 1, transition: 0.02f, offset: 0.5f,
                trackingMode: StyleBoneTrackingMode.FixedWorld,
                worldForward: Vector3.UnitZ, worldRight: Vector3.UnitX)),
            TestStylePlugins.Bind(charId, 1, TestStylePlugins.GenshinHair(1)),
        };
        var outlines = new D3D12ViewOutlineWidthBinding[]
        {
            new(charId, 0, 0.75f),
        };
        var globals = new Dictionary<string, StyleMaterialBindingData>(StringComparer.Ordinal)
        {
            ["genshin"] = new StyleMaterialBindingData(
                "genshin",
                "global",
                NumberValues: new Dictionary<string, double>(StringComparer.Ordinal) { ["shadowStrength"] = 0.7 }),
        };

        var inputs1 = new Dictionary<Guid, SceneObjectRenderInputs>
        {
            [charId] = TestStylePlugins.CreateSceneInputs(charId, 2),
        };
        var inputs2Same = new Dictionary<Guid, SceneObjectRenderInputs>
        {
            [charId] = TestStylePlugins.CreateSceneInputs(charId, 2),
        };
        var inputs3MovedBone = new Dictionary<Guid, SceneObjectRenderInputs>
        {
            [charId] = TestStylePlugins.CreateSceneInputs(charId, 2, new Dictionary<int, Matrix4x4>
            {
                [0] = Matrix4x4.CreateRotationY(0.5f),
            }),
        };

        var cache = new D3D12ViewRenderingStylesCache();

        var direct1 = D3D12ViewRenderingStyles.Resolve(
            baseFrame, presetCatalog, bindings, charMats, pluginBindings, inputs1, outlines, viewCatalog, globals);
        var cached1 = cache.Resolve(
            baseFrame, presetCatalog, bindings, charMats, pluginBindings, inputs1, outlines, viewCatalog, globals);
        AssertResultEquivalent(direct1, cached1);

        // Identical sceneInputs content in a new dictionary + new frame wrapper reuses SurfaceStyles.
        var baseFrameCopy = new OriginalFrameScene(
            baseFrame.Surface, baseFrame.Outline, baseFrame.OutlineOptions, baseFrame.Options,
            baseFrame.Materials, baseFrame.Lighting, baseFrame.EditorOverlay);
        var cached2 = cache.Resolve(
            baseFrameCopy, presetCatalog, bindings.ToArray(), charMats, pluginBindings, inputs2Same, outlines, viewCatalog, globals);
        AssertResultEquivalent(direct1, cached2);
        Assert.Same(cached1.SurfaceStyles, cached2.SurfaceStyles);

        // Camera change recomputes accurately if needed and matches direct Resolve.
        var directMovedCam = D3D12ViewRenderingStyles.Resolve(
            movedCameraFrame, presetCatalog, bindings, charMats, pluginBindings, inputs1, outlines, viewCatalog, globals);
        var cachedMovedCam = cache.Resolve(
            movedCameraFrame, presetCatalog, bindings, charMats, pluginBindings, inputs1, outlines, viewCatalog, globals);
        AssertResultEquivalent(directMovedCam, cachedMovedCam);

        // Changing plugin binding + global parameters recomputes and matches direct Resolve.
        pluginBindings[0] = TestStylePlugins.Bind(charId, 0, TestStylePlugins.FaceSdf("genshin", 0, secondaryTextureSlot: 1, transition: 0.05f, offset: 0.35f,
            trackingMode: StyleBoneTrackingMode.SelectedBone, boneName: "head"));
        globals["genshin"] = new StyleMaterialBindingData(
            "genshin",
            "global",
            NumberValues: new Dictionary<string, double>(StringComparer.Ordinal) { ["shadowStrength"] = 0.25 });
        var boneMap = new Dictionary<Guid, IReadOnlyDictionary<string, int>>
        {
            [charId] = new Dictionary<string, int>(StringComparer.Ordinal) { ["head"] = 0 },
        };

        var directUpdated = D3D12ViewRenderingStyles.Resolve(
            movedCameraFrame, presetCatalog, bindings, charMats, pluginBindings, inputs3MovedBone, outlines, viewCatalog, globals, boneIndicesByObject: boneMap);
        var cachedUpdated1 = cache.Resolve(
            movedCameraFrame, presetCatalog, bindings, charMats, pluginBindings, inputs3MovedBone, outlines, viewCatalog, globals, boneIndicesByObject: boneMap);
        var cachedUpdated2 = cache.Resolve(
            movedCameraFrame, presetCatalog, bindings, charMats, pluginBindings, inputs3MovedBone, outlines, viewCatalog, globals, boneIndicesByObject: boneMap);
        AssertResultEquivalent(directUpdated, cachedUpdated1);
        Assert.Same(cachedUpdated1, cachedUpdated2);
        Assert.NotSame(cached1.SurfaceStyles, cachedUpdated1.SurfaceStyles);

        // Rotating the evaluated head bone invalidates the cached plugin draw data and matches direct Resolve.
        var inputs4RotatedBone = reuseInputDictionary ? inputs3MovedBone : new Dictionary<Guid, SceneObjectRenderInputs>();
        inputs4RotatedBone[charId] = TestStylePlugins.CreateSceneInputs(charId, 2, new Dictionary<int, Matrix4x4>
        {
            [0] = Matrix4x4.CreateRotationY(1.25f),
        });
        var directRotated = D3D12ViewRenderingStyles.Resolve(
            movedCameraFrame, presetCatalog, bindings, charMats, pluginBindings, inputs4RotatedBone, outlines, viewCatalog, globals, boneIndicesByObject: boneMap);
        var cachedRotated = cache.Resolve(
            movedCameraFrame, presetCatalog, bindings, charMats, pluginBindings, inputs4RotatedBone, outlines, viewCatalog, globals, boneIndicesByObject: boneMap);
        AssertResultEquivalent(directRotated, cachedRotated);
        Assert.False(D3D12SurfaceStyleMap.Equivalent(cachedUpdated1.SurfaceStyles, cachedRotated.SurfaceStyles));
        Assert.Same(cachedRotated, cache.Resolve(
            movedCameraFrame, presetCatalog, bindings, charMats, pluginBindings, inputs4RotatedBone, outlines, viewCatalog, globals, boneIndicesByObject: boneMap));

        inputs4RotatedBone.Remove(charId);
        var missing = Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(
            movedCameraFrame, presetCatalog, bindings, charMats, pluginBindings, inputs4RotatedBone, outlines, viewCatalog, globals, boneIndicesByObject: boneMap));
        var cachedMissing = Assert.Throws<ArgumentException>(() => cache.Resolve(
            movedCameraFrame, presetCatalog, bindings, charMats, pluginBindings, inputs4RotatedBone, outlines, viewCatalog, globals, boneIndicesByObject: boneMap));
        Assert.Equal(missing.ParamName, cachedMissing.ParamName);
    }

    [Fact]
    public void MultiPropOutOfOrderAndHiddenInstancesMatchDirectResolveAndReuseResult()
    {
        const int visibleObjects = 24;
        var names = Enumerable.Range(0, visibleObjects).Select(i => $"obj{i}").ToArray();
        var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false);
        var frame1 = CreateTwoObjectFrame(Vector3.UnitZ, new Vector3(0, 1, -3), options, names);
        var frame2 = CreateTwoObjectFrame(Vector3.UnitX, new Vector3(1, 2, -4), options, names);

        var ids = Enumerable.Range(0, visibleObjects + 1).Select(_ => Guid.NewGuid()).ToArray();
        var orderedBindings = new List<SceneRenderingStyleBinding>(visibleObjects + 1);
        for (int i = 0; i < visibleObjects; i++)
        {
            var role = i == 0 ? ModelRole.Character : ModelRole.Prop;
            var preset = i == 1 ? new RenderingPresetReference("genshin") : new RenderingPresetReference("mmd");
            orderedBindings.Add(new SceneRenderingStyleBinding(ids[i], preset, role, i, 1, true));
        }
        orderedBindings.Add(new SceneRenderingStyleBinding(ids[visibleObjects], new RenderingPresetReference("genshin"), ModelRole.Character, visibleObjects, 0, false));
        // Reverse order to exercise out-of-order diagnostics and validation parity.
        var reversedBindings1 = Array.AsReadOnly(orderedBindings.AsEnumerable().Reverse().ToArray());
        var reversedBindings2 = Array.AsReadOnly(orderedBindings.AsEnumerable().Reverse().ToArray());

        var direct1 = D3D12ViewRenderingStyles.Resolve(frame1, TestStylePlugins.AllPresetCatalog, reversedBindings1, styleCatalog: TestStylePlugins.AllViewCatalog);
        var direct2 = D3D12ViewRenderingStyles.Resolve(frame2, TestStylePlugins.AllPresetCatalog, reversedBindings2, styleCatalog: TestStylePlugins.AllViewCatalog);

        var cache = new D3D12ViewRenderingStylesCache(
            TestStylePlugins.AllPresetCatalog,
            [],
            [],
            [],
            TestStylePlugins.AllViewCatalog,
            null);

        var cached1 = cache.Resolve(frame1, reversedBindings1);
        var cached2 = cache.Resolve(frame2, reversedBindings2);
        AssertResultEquivalent(direct1, cached1);
        AssertResultEquivalent(direct2, cached2);
        Assert.Same(cached1.SurfaceStyles, cached2.SurfaceStyles);
        Assert.Same(cached1.Diagnostics, cached2.Diagnostics);

        // Even with 25 objects across a new OriginalFrameScene and a new ReadOnlyCollection wrapper,
        // only the tiny result object is allocated (no dictionaries/arrays/LINQ).
        _ = MeasureFixedRepeat(cache, frame2, reversedBindings2);
        Assert.Equal(0, MeasureFixedRepeat(cache, frame2, reversedBindings2));
        long newFrameAlloc = MeasureFixedRepeat(cache, frame1, reversedBindings1);
        Assert.InRange(newFrameAlloc, 0, 64);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long MeasureFixedRepeat(
        D3D12ViewRenderingStylesCache cache,
        OriginalFrameScene scene,
        IReadOnlyList<SceneRenderingStyleBinding> bindings)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = cache.Resolve(scene, bindings);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void AssertResultEquivalent(
        D3D12ViewRenderingStylesResult expected,
        D3D12ViewRenderingStylesResult actual)
    {
        Assert.True(D3D12SurfaceStyleMap.Equivalent(expected.SurfaceStyles, actual.SurfaceStyles));
        Assert.Equal(expected.SurfaceStyles.IsDefault, actual.SurfaceStyles.IsDefault);
        Assert.Equal(expected.SurfaceStyles.StyleIds, actual.SurfaceStyles.StyleIds);
        Assert.Equal(expected.SurfaceStyles.StylizedRoles, actual.SurfaceStyles.StylizedRoles);
        Assert.Equal(expected.SurfaceStyles.CharacterMaterials.Count, actual.SurfaceStyles.CharacterMaterials.Count);
        for (int i = 0; i < expected.SurfaceStyles.CharacterMaterials.Count; i++)
        {
            var e = expected.SurfaceStyles.CharacterMaterials[i];
            var a = actual.SurfaceStyles.CharacterMaterials[i];
            Assert.Equal(e.Role, a.Role);
            Assert.Equal(e.FaceNormalShadeStrength, a.FaceNormalShadeStrength);
            Assert.Equal(e.FaceGeometricShadowStrength, a.FaceGeometricShadowStrength);
            Assert.Equal(e.EyeGeometricShadowStrength, a.EyeGeometricShadowStrength);
            Assert.Equal(e.PmxMaterialTreatment, a.PmxMaterialTreatment);
            Assert.Equal(e.StylizedShading, a.StylizedShading);
            Assert.Equal(e.ToonResponse, a.ToonResponse);
            Assert.Equal(e.PreserveDirectLightPalette, a.PreserveDirectLightPalette);
        }
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
        Assert.Equal(expected.Scene.Materials, actual.Scene.Materials);
        Assert.Same(expected.Scene.Surface, actual.Scene.Surface);
        Assert.Same(expected.Scene.Outline, actual.Scene.Outline);
        Assert.Same(expected.Scene.OutlineOptions, actual.Scene.OutlineOptions);
        Assert.Same(expected.Scene.Options, actual.Scene.Options);
        Assert.Same(expected.Scene.Lighting, actual.Scene.Lighting);
        Assert.Same(expected.Scene.EditorOverlay, actual.Scene.EditorOverlay);
    }

    private static OriginalFrameScene CreateTwoObjectFrame(
        Vector3 lightDirection,
        Vector3 cameraEye,
        OriginalFrameOptions? options,
        string[]? materialNames = null)
    {
        materialNames ??= ["mat0", "mat1", "prop"];
        var camera = new SceneCamera(cameraEye, Vector3.Zero, Vector3.UnitY, 4, 0.1f, 100);
        var vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }
            .Select(p => new SceneVertex(p, Vector3.UnitZ, Vector2.Zero)).ToArray();
        var indices = new uint[materialNames.Length * 3];
        var materials = new SceneMaterial[materialNames.Length];
        for (int i = 0; i < materialNames.Length; i++)
        {
            indices[i * 3] = 0u;
            indices[i * 3 + 1] = 1u;
            indices[i * 3 + 2] = 2u;
            materials[i] = new SceneMaterial(materialNames[i], i * 3, 3, Vector4.One, Vector3.Zero, new Vector3(0.3f), 1, DiffuseTexture: 0);
        }
        var textures = new[]
        {
            new SceneTexture("t0", 2, 2, new byte[16]),
            new SceneTexture("t1", 2, 2, new byte[16]),
        };
        var snapshot = new SceneSnapshot(vertices, indices, materials, textures, camera: camera, lightDirection: lightDirection);
        var originalMaterials = materialNames.Select(_ => new OriginalStyleMaterialBinding(new Vector3(0.5f))).ToArray();
        var surface = new OriginalStyleScene(snapshot, originalMaterials);
        var outlineVertices = vertices.Select(v =>
            new OriginalOutlineVertex(v.Position, v.Normal, OriginalOutlineVertexMode.Expand, 1, 0)).ToArray();
        var outlineMaterials = materialNames.Select(_ => new OriginalOutlineMaterial(1, Vector4.One)).ToArray();
        var outline = new OriginalOutlineData(outlineVertices, outlineMaterials);
        var outlineOptions = new OriginalOutlineOptions(Vector4.Zero, Matrix4x4.Identity, cameraEye, 30, 1);
        var flags = materialNames.Select(_ => new OriginalFrameMaterialFlags(false, false, false)).ToArray();
        return new OriginalFrameScene(surface, outline, outlineOptions, options, flags);
    }

    private Fixture Create(
        Func<ViewRenderRequest, IEditorViewportRenderer> factory,
        Action<EditorViewportHost>? configure = null)
    {
        var camera = new SceneCamera(new(0, 1, -3), Vector3.Zero, Vector3.UnitY, 4, 0.1f, 100);
        var layout = new SceneCompositionEditor();
        var vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }
            .Select(position => new SceneVertex(position, Vector3.UnitZ, Vector2.Zero)).ToArray();
        var mesh = new SceneSnapshot(vertices, [0u, 1u, 2u],
            [new SceneMaterial("triangle", 0, 3, Vector4.One, Vector3.Zero, new(0.3f), 1)], [], camera: camera);
        var outline = new OriginalOutlineData(vertices.Select(vertex =>
            new OriginalOutlineVertex(vertex.Position, vertex.Normal, OriginalOutlineVertexMode.Expand, 1, 0)).ToArray(),
            [new(1, Vector4.One)]);
        layout.Add("triangle", "triangle.pmx", ModelRole.Character, new OriginalFrameScene(new(mesh, [new(new(0.5f))]),
            outline, new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1)));
        var project = new SceneProjectSession(layout, new(), [], new StarMcBodyViewProfileTests.NoPhysics(), cancellationToken: Token);
        var host = new EditorViewportHost(() => (100, 80), factory);
        var f = new Fixture(host, project, Token);
        host.SnapshotProvider = requests => { f.Captures++; return project.CaptureRenderSnapshot(requests); };
        host.FramePresented += value => f.Callbacks.Writer.TryWrite(value);
        host.Error += value => f.Errors.Writer.TryWrite(value);
        configure?.Invoke(host);
        host.Project = project;
        host.RequestRender();
        return f;
    }

    private static void Cleanup(Fixture f, params FakeRenderer[] renderers)
    {
        foreach (var renderer in renderers) renderer.ReleaseAll();
        f.Dispose();
    }

    private Task<T> Next<T>(Channel<T> channel) => channel.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);

    private sealed class Fixture(EditorViewportHost host, SceneProjectSession project, CancellationToken token) : IDisposable
    {
        public EditorViewportHost Host { get; } = host;
        public SceneProjectSession Project { get; } = project;
        public int Captures;
        public Channel<EditorViewportPresentationStatistics> Callbacks { get; } = Channel.CreateUnbounded<EditorViewportPresentationStatistics>();
        public Channel<Exception> Errors { get; } = Channel.CreateUnbounded<Exception>();
        public void Request(int frame) { Project.Seek(frame, token); Host.RequestRender(); }
        public void Dispose() { Host.Dispose(); Project.Dispose(); }
    }

    private sealed class FakeRenderer : IEditorViewportRenderer
    {
        private readonly TaskCompletionSource[] gates = [NewSignal(), NewSignal(), NewSignal(), NewSignal(), NewSignal()];
        private readonly TaskCompletionSource[] permits = [NewSignal(), NewSignal(), NewSignal(), NewSignal(), NewSignal()];
        private readonly TaskCompletionSource[] aheadGates = [NewSignal(), NewSignal(), NewSignal(), NewSignal(), NewSignal()];
        private int permitWaits;
        private int aheadRecordings;
        private int presents, disposals;
        public bool GateFramePermits { get; init; }
        public bool SupportsRecordingAhead { get; init; }
        public Channel<int> PermitWaits { get; } = Channel.CreateUnbounded<int>();
        public Channel<PreviewFrame> Frames { get; } = Channel.CreateUnbounded<PreviewFrame>();
        public Channel<PreviewFrame> AheadFrames { get; } = Channel.CreateUnbounded<PreviewFrame>();
        public string AdapterName => "headless fake";
        public bool IsWarp => false;
        public ViewRendererCapabilities Capabilities { get; } = new("fake", 4096, 4096, false, true);
        public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests => [];
        public EditorViewportPresentationStatistics Statistics => new(Volatile.Read(ref presents), 0, 0, 0, 100, 80, 2, null);

        public async ValueTask WaitForNextFrameAsync(CancellationToken cancellationToken = default)
        {
            if (!GateFramePermits) return;
            int index = Interlocked.Increment(ref permitWaits) - 1;
            PermitWaits.Writer.TryWrite(index);
            if (index < permits.Length) await permits[index].Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default)
        {
            int index = Interlocked.Increment(ref presents) - 1;
            Frames.Writer.TryWrite(frame);
            if (index < gates.Length) await gates[index].Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask PresentAheadAsync(PreviewFrame frame, CancellationToken cancellationToken)
        {
            int index = Interlocked.Increment(ref aheadRecordings) - 1;
            AheadFrames.Writer.TryWrite(frame);
            if (index < aheadGates.Length) await aheadGates[index].Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await PresentAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask UpdateQualityAsync(ViewQualityOptions quality, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Interlocked.Increment(ref disposals); return ValueTask.CompletedTask; }
        public void Release(int index) => gates[index].TrySetResult();
        public void ReleasePermit(int index) => permits[index].TrySetResult();
        public void ReleaseAhead(int index) => aheadGates[index].TrySetResult();
        public void ReleaseAll() { foreach (var gate in gates.Concat(permits).Concat(aheadGates)) gate.TrySetResult(); }
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
