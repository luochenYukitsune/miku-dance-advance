using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12StylizedPolishUnitTests
{
    [Fact]
    public void ModernDirectionalShadowProjection_SubsetFitExcludesDistantMaterialAndFallsBackSafely()
    {
        var vertices = new SceneVertex[]
        {
            // Material 0: character box around origin [-1, 1]^3
            new(new(-1, 0, -1), Vector3.UnitY, Vector2.Zero),
            new(new(1, 0, -1), Vector3.UnitY, Vector2.Zero),
            new(new(1, 2, 1), Vector3.UnitY, Vector2.Zero),
            new(new(-1, 2, 1), Vector3.UnitY, Vector2.Zero),
            // Material 1: distant stage floor [40, 60]
            new(new(40, 0, 40), Vector3.UnitY, Vector2.Zero),
            new(new(60, 0, 40), Vector3.UnitY, Vector2.Zero),
            new(new(60, 0, 60), Vector3.UnitY, Vector2.Zero),
        };
        uint[] indices = [0, 1, 2, 0, 2, 3, 4, 5, 6];
        var matChar = new SceneMaterial("character", 0, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 1);
        var matStage = new SceneMaterial("stage", 6, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1);
        var camera = new SceneCamera(new(0, 1, -8), new(0, 1, 0), Vector3.UnitY, 4, 0.1f, 100f, 35f, LeftHanded: true);
        var lightDir = Vector3.Normalize(new Vector3(-0.4f, 0.8f, -0.4f));

        var fullScene = new SceneSnapshot(vertices, indices, [matChar, matStage], camera: camera, lightDirection: lightDir);
        var charOnlyScene = new SceneSnapshot(vertices[..4], indices[..6], [matChar], camera: camera, lightDirection: lightDir);

        var fullFit = ModernDirectionalShadowProjection.Fit(fullScene);
        var charOnlyFit = ModernDirectionalShadowProjection.Fit(charOnlyScene);
        var subsetFit = ModernDirectionalShadowProjection.Fit(fullScene, [0]);

        Assert.Equal(charOnlyFit, subsetFit);
        Assert.NotEqual(fullFit, subsetFit);

        // Empty or out-of-range focus indices safely fall back to full-scene Fit.
        Assert.Equal(fullFit, ModernDirectionalShadowProjection.Fit(fullScene, Array.Empty<int>()));
        Assert.Equal(fullFit, ModernDirectionalShadowProjection.Fit(fullScene, [-1, 99]));

        Assert.Throws<ArgumentOutOfRangeException>(() => ModernDirectionalShadowProjection.Fit(fullScene, [0], paddingFraction: 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModernDirectionalShadowProjection.Fit(fullScene, [0], paddingFraction: 1.5f));
    }

    [Fact]
    public void ModernDirectionalShadowProjection_SubsetFitIncludesUpstreamCastersWithoutWideningCharacterXy()
    {
        var vertices = new SceneVertex[]
        {
            // Material 0: character around origin [-1, 1] x [0, 2] x [-1, 1]
            new(new(-1, 0, -1), Vector3.UnitY, Vector2.Zero),
            new(new(1, 0, -1), Vector3.UnitY, Vector2.Zero),
            new(new(1, 2, 1), Vector3.UnitY, Vector2.Zero),
            new(new(-1, 2, 1), Vector3.UnitY, Vector2.Zero),
            // Material 1: upstream roof sitting between light (+Y) and character at y = 20
            new(new(-6, 20, -6), Vector3.UnitY, Vector2.Zero),
            new(new(6, 20, -6), Vector3.UnitY, Vector2.Zero),
            new(new(6, 20, 6), Vector3.UnitY, Vector2.Zero),
        };
        uint[] indices = [0, 1, 2, 0, 2, 3, 4, 5, 6];
        var matChar = new SceneMaterial("character", 0, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 1);
        var matRoof = new SceneMaterial("roof", 6, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1);
        var camera = new SceneCamera(new(0, 1, -8), new(0, 1, 0), Vector3.UnitY, 4, 0.1f, 100f, 35f, LeftHanded: true);
        var lightDir = Vector3.Normalize(new Vector3(0.05f, 1f, 0.05f));

        var sceneWithRoof = new SceneSnapshot(vertices, indices, [matChar, matRoof], camera: camera, lightDirection: lightDir);
        var charOnlyScene = new SceneSnapshot(vertices[..4], indices[..6], [matChar], camera: camera, lightDirection: lightDir);

        var charOnlyFit = ModernDirectionalShadowProjection.Fit(charOnlyScene);
        var subsetFit = ModernDirectionalShadowProjection.Fit(sceneWithRoof, [0]);

        // XY texel density matches character-only bounds, while roof vertex clip Z stays inside [0, 1].
        Assert.Equal(charOnlyFit.M11, subsetFit.M11, 4);
        Assert.Equal(charOnlyFit.M22, subsetFit.M22, 4);
        for (int i = 4; i <= 6; i++)
        {
            var clip = Vector4.Transform(new Vector4(vertices[i].Position, 1f), subsetFit);
            float ndcZ = clip.Z / clip.W;
            Assert.InRange(ndcZ, 0f, 1f);
        }
    }

    [Fact]
    public void BloomPass_ComputeMipChain_ContinuesAlongLongerAxisOnNarrowTargets()
    {
        var narrow = D3D12BloomPass.ComputeMipChain(512, 4);
        Assert.Equal([(256, 2), (128, 2), (64, 2), (32, 2), (16, 2)], narrow);

        var shortNarrow = D3D12BloomPass.ComputeMipChain(6, 4);
        Assert.Equal([(3, 2), (2, 2)], shortNarrow);

        Assert.Equal([(1, 1)], D3D12BloomPass.ComputeMipChain(2, 2));
        Assert.Equal([(1, 1)], D3D12BloomPass.ComputeMipChain(1, 1));
    }

    [Fact]
    public void ViewQuality_GatesDepthRimByAlphaSupport_AndProtectsMixedSceneShadows()
    {
        var catalog = TestStylePlugins.AllPresetCatalog;
        var viewCatalog = TestStylePlugins.AllViewCatalog;
        var baseFrame = D3D12StylizedPolishTestScenes.TwoQuadShadowAndFaceScene();
        var objectId = Guid.NewGuid();
        var stageId = Guid.NewGuid();
        var bindingChar = new SceneRenderingStyleBinding(objectId, new("star-rail"), ModelRole.Character, 0, 1, true);
        var bindingStage = new SceneRenderingStyleBinding(stageId, new(D3D12SurfaceStyleMap.DefaultStyleId), ModelRole.Environment, 1, 1, true);
        var resolved = D3D12ViewRenderingStyles.Resolve(
            baseFrame, catalog, [bindingChar, bindingStage], styleCatalog: viewCatalog);

        var transparentLegacy = new OriginalFrameScene(
            resolved.Scene.Surface,
            options: resolved.Scene.Options! with
            {
                AlphaOutput = FrameAlphaOutput.LegacyMax,
                BackgroundColor = new Vector4(0f, 0f, 0f, 0f),
            },
            materials: resolved.Scene.Materials);

        var quality = new ViewQualityOptions();
        var legacyPolish = D3D12ViewQuality.ResolveStylizedPolish(true, transparentLegacy, resolved.SurfaceStyles, quality);
        Assert.Null(legacyPolish.DepthRim);
        Assert.Null(legacyPolish.Bloom);
        Assert.NotNull(legacyPolish.ToneCurve);

        var opaquePolish = D3D12ViewQuality.ResolveStylizedPolish(true, resolved.Scene, resolved.SurfaceStyles, quality);
        Assert.NotNull(opaquePolish.DepthRim);
        Assert.NotNull(opaquePolish.Bloom);
        Assert.NotNull(opaquePolish.ToneCurve);

        // When non-focused visible material receives self-shadow, keep shared shadow projection unchanged.
        var mixedReceiveScene = new OriginalFrameScene(
            resolved.Scene.Surface,
            options: resolved.Scene.Options,
            materials: [new(true, true, false), new(true, true, false)]);
        var unchanged = D3D12ViewQuality.ApplyFocusedCharacterShadow(true, mixedReceiveScene, resolved.SurfaceStyles);
        Assert.Equal(mixedReceiveScene.Options!.SelfShadow!.LightViewProjection, unchanged.Options!.SelfShadow!.LightViewProjection);

        // When non-focused material does not receive self-shadow, focus XY on stylized character materials.
        var charOnlyReceiveScene = new OriginalFrameScene(
            resolved.Scene.Surface,
            options: resolved.Scene.Options,
            materials: [new(true, true, false), new(true, false, false)]);
        var tightened = D3D12ViewQuality.ApplyFocusedCharacterShadow(true, charOnlyReceiveScene, resolved.SurfaceStyles);
        var expected = ModernDirectionalShadowProjection.Fit(charOnlyReceiveScene.Surface.Snapshot, [0]);
        Assert.Equal(expected, tightened.Options!.SelfShadow!.LightViewProjection);
    }

    [Fact]
    public void StylizedShading_ValidatesNewFieldsAndIsolatesRoles()
    {
        var baseShading = new D3D12StylizedShading(
            ShadowStrength: 1f, ShadowValue: 0.7f, ShadowHue: 0.5f, ShadowTint: new(0.9f, 0.9f, 1f),
            FaceFlatten: 0f, WarmColor: new(1f, 0.85f, 0.75f), WarmStrength: 0.2f,
            RimEdge: 0.6f, RimStrength: 0.3f, Saturation: 1.1f, Contrast: 0.1f, Brightness: 1f);

        // New fields default to OFF.
        Assert.Equal(0f, baseShading.CastShadowStrength);
        Assert.Equal(0f, baseShading.FaceLightStrength);
        Assert.Null(baseShading.FaceLightDirection);
        Assert.Equal(Vector4.Zero, baseShading.ExtraParams);
        Assert.Equal(Vector4.Zero, baseShading.FaceLightDir);

        var dir = new Vector3(0f, 0f, -2f);
        var enhanced = baseShading with { CastShadowStrength = 0.8f, FaceLightStrength = 0.65f, FaceLightDirection = dir };
        enhanced.Validate();
        Assert.Equal(new Vector4(0.8f, 0.65f, 0f, 0f), enhanced.ExtraParams);
        Assert.Equal(new Vector4(0f, 0f, -1f, 1f), enhanced.FaceLightDir);

        // Invalid scalar bounds & direction throw.
        Assert.Throws<ArgumentOutOfRangeException>(() => (enhanced with { CastShadowStrength = -0.1f }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (enhanced with { CastShadowStrength = 1.1f }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (enhanced with { FaceLightStrength = float.NaN }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (enhanced with { FaceLightDirection = Vector3.Zero }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (enhanced with { FaceLightDirection = new Vector3(float.PositiveInfinity, 0, 0) }).Validate());

        // Role isolation.
        var body = D3D12StylizedShadingCatalog.ResolveForRole(enhanced, D3D12CharacterMaterialRole.Body)!;
        Assert.Equal(0.8f, body.CastShadowStrength);
        Assert.Equal(0f, body.FaceLightStrength);
        Assert.Null(body.FaceLightDirection);

        var hair = D3D12StylizedShadingCatalog.ResolveForRole(enhanced, D3D12CharacterMaterialRole.Hair)!;
        Assert.Equal(0.8f, hair.CastShadowStrength);
        Assert.Equal(0f, hair.FaceLightStrength);
        Assert.Null(hair.FaceLightDirection);

        var face = D3D12StylizedShadingCatalog.ResolveForRole(enhanced, D3D12CharacterMaterialRole.Face)!;
        Assert.Equal(0f, face.CastShadowStrength);
        Assert.Equal(0.65f, face.FaceLightStrength);
        Assert.Equal(dir, face.FaceLightDirection);

        var eye = D3D12StylizedShadingCatalog.ResolveForRole(enhanced, D3D12CharacterMaterialRole.Eye)!;
        Assert.Equal(0f, eye.CastShadowStrength);
        Assert.Equal(0f, eye.FaceLightStrength);
        Assert.Null(eye.FaceLightDirection);
    }

    [Fact]
    public void PolishOptions_DefaultOffAndValidateBoundsAndAlphaOutput()
    {
        // All enhancements default OFF.
        Assert.False(D3D12DepthRimOptions.Off.Enabled);
        Assert.False(D3D12DepthRimOptions.Off.IsActive);
        Assert.False(new D3D12DepthRimOptions().IsActive);
        Assert.False(D3D12BloomOptions.Off.Enabled);
        Assert.False(D3D12BloomOptions.Off.IsActive);
        Assert.False(new D3D12BloomOptions().IsActive);
        Assert.False(D3D12ToneCurveOptions.Off.Enabled);
        Assert.False(D3D12ToneCurveOptions.Off.IsActive);
        Assert.False(new D3D12ToneCurveOptions().IsActive);

        // Validation enforces ranges even when Enabled is false.
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12DepthRimOptions(Strength: -0.1f).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12DepthRimOptions(WidthPixels: 0.2f).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12DepthRimOptions(DepthThreshold: 0f).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12DepthRimOptions(LightBias: 1.5f).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12DepthRimOptions(Color: new(float.NaN, 1f, 1f)).Validate());

        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12BloomOptions(Threshold: -0.1f).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12BloomOptions(SoftKnee: 1.5f).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12BloomOptions(Intensity: 3f).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12BloomOptions(Scatter: 0.05f).Validate());

        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12ToneCurveOptions(Contrast: 0.8f).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12ToneCurveOptions(Shoulder: -0.1f).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12ToneCurveOptions(Saturation: 2.5f).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12ToneCurveOptions(ShadowTint: new(0.2f, 1f, 1f)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12ToneCurveOptions(HighlightTint: new(1f, 2f, 1f)).Validate());

        // StraightAlpha & opaque LegacyMax succeed; transparent LegacyMax throws NotSupportedException.
        var activeRim = new D3D12DepthRimOptions(Enabled: true);
        activeRim.ValidateOutput(FrameAlphaOutput.Straight, 0f);
        activeRim.ValidateOutput(FrameAlphaOutput.LegacyMax, 1f);
        Assert.Throws<NotSupportedException>(() => activeRim.ValidateOutput(FrameAlphaOutput.LegacyMax, 0f));

        var activeBloom = new D3D12BloomOptions(Enabled: true);
        activeBloom.ValidateOutput(FrameAlphaOutput.Straight, 0f);
        activeBloom.ValidateOutput(FrameAlphaOutput.LegacyMax, 1f);
        Assert.Throws<NotSupportedException>(() => activeBloom.ValidateOutput(FrameAlphaOutput.LegacyMax, 0f));
    }

    [Fact]
    public async Task ShaderPrewarmer_SpecAndPrewarmCoverPolishPassesCleanly()
    {
        var quad = D3D12StylizedPolishTestScenes.QuadFrame(straightAlpha: true);
        var defaultSpec = D3D12ShaderPrewarmer.SpecFromScene(quad);
        Assert.False(defaultSpec.IncludeDepthRim);
        Assert.False(defaultSpec.IncludeBloom);
        Assert.False(defaultSpec.IncludeToneCurve);

        var activeSpec = D3D12ShaderPrewarmer.SpecFromScene(
            quad,
            depthRim: new D3D12DepthRimOptions(Enabled: true),
            bloom: new D3D12BloomOptions(Enabled: true),
            toneCurve: new D3D12ToneCurveOptions(Enabled: true));
        Assert.True(activeSpec.IncludeDepthRim);
        Assert.True(activeSpec.IncludeDepthRimAlphaCoverage);
        Assert.True(activeSpec.IncludeBloom);
        Assert.True(activeSpec.IncludeBloomAlphaCoverage);
        Assert.True(activeSpec.IncludeToneCurve);

        await D3D12ShaderPrewarmer.PrewarmAsync(activeSpec, maxParallelism: 2, TestContext.Current.CancellationToken);
        await D3D12ShaderPrewarmer.PrewarmAsync(activeSpec with { IncludeDepthRimAlphaCoverage = false, IncludeBloomAlphaCoverage = false }, maxParallelism: 2, TestContext.Current.CancellationToken);

        var resolveReqs = D3D12ScreenSpaceAoPass.GetResolvePrewarmRequests().ToList();
        Assert.Equal(2, resolveReqs.Count);
        Assert.Contains(resolveReqs, r => r.SourceName == "msaa-depth-resolve.hlsl" && r.EntryPoint == "VSMain");
        Assert.Contains(resolveReqs, r => r.SourceName == "msaa-depth-resolve.hlsl" && r.EntryPoint == "PSResolveDepth");

        foreach (bool alpha in new[] { false, true })
        {
            var rimReqs = D3D12DepthRimPass.GetPrewarmRequests(alpha).ToList();
            Assert.Contains(rimReqs, r => r.SourceName == "msaa-depth-resolve.hlsl" && r.EntryPoint == "VSMain");
            Assert.Contains(rimReqs, r => r.SourceName == "msaa-depth-resolve.hlsl" && r.EntryPoint == "PSResolveDepth");
            Assert.DoesNotContain(rimReqs, r => r.SourceName == "ssao-gtao.hlsl");
        }

        var ssaoReqs = D3D12ScreenSpaceAoPass.GetPrewarmRequests().ToList();
        Assert.Equal(5, ssaoReqs.Count);
        Assert.Contains(ssaoReqs, r => r.SourceName == "msaa-depth-resolve.hlsl" && r.EntryPoint == "VSMain");
        Assert.Contains(ssaoReqs, r => r.SourceName == "msaa-depth-resolve.hlsl" && r.EntryPoint == "PSResolveDepth");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await D3D12ShaderPrewarmer.PrewarmAsync(activeSpec, cancellationToken: cts.Token);
    }

    [Fact]
    public void StylePluginAdapter_AppliesDefaultPolishShading_AndViewPrewarmRespectsToggle()
    {
        var contract = new StyleStylizedShadingParameters(
            ShadowStrength: 1f, ShadowValue: 0.7f, ShadowHue: 0.6f, ShadowTint: new(0.85f, 0.9f, 1f),
            FaceFlatten: 0.1f, WarmColor: Vector3.One, WarmStrength: 0f,
            RimEdge: 0.6f, RimStrength: 0.3f, Saturation: 1.15f, Contrast: 0.1f, Brightness: 1f);
        var converted = D3D12StylePluginAdapter.ToD3D12StylizedShading(contract);
        Assert.Equal(D3D12StylizedPolishDefaults.DefaultCastShadowStrength, converted.CastShadowStrength);
        Assert.Equal(D3D12StylizedPolishDefaults.DefaultFaceLightStrength, converted.FaceLightStrength);

        var body = D3D12StylizedShadingCatalog.ResolveForRole(converted, D3D12CharacterMaterialRole.Body)!;
        var hair = D3D12StylizedShadingCatalog.ResolveForRole(converted, D3D12CharacterMaterialRole.Hair)!;
        var face = D3D12StylizedShadingCatalog.ResolveForRole(converted, D3D12CharacterMaterialRole.Face)!;
        var eye = D3D12StylizedShadingCatalog.ResolveForRole(converted, D3D12CharacterMaterialRole.Eye)!;
        Assert.Equal(D3D12StylizedPolishDefaults.DefaultCastShadowStrength, body.CastShadowStrength);
        Assert.Equal(0f, body.FaceLightStrength);
        Assert.Equal(D3D12StylizedPolishDefaults.DefaultCastShadowStrength, hair.CastShadowStrength);
        Assert.Equal(0f, hair.FaceLightStrength);
        Assert.Equal(0f, face.CastShadowStrength);
        Assert.Equal(D3D12StylizedPolishDefaults.DefaultFaceLightStrength, face.FaceLightStrength);
        Assert.Equal(0f, eye.CastShadowStrength);
        Assert.Equal(0f, eye.FaceLightStrength);

        var catalog = TestStylePlugins.AllViewCatalog;
        var presets = TestStylePlugins.AllPresetCatalog;
        var quality = new ViewQualityOptions();

        var offSpec = D3D12ViewShaderPrewarm.BuildFromViewRequest(
            quality, styleCatalog: catalog, stylePresets: presets, enableStylizedPolish: false);
        Assert.False(offSpec.IncludeDepthRim);
        Assert.False(offSpec.IncludeBloom);
        Assert.False(offSpec.IncludeToneCurve);

        var onSpec = D3D12ViewShaderPrewarm.BuildFromViewRequest(
            quality, styleCatalog: catalog, stylePresets: presets, enableStylizedPolish: true);
        Assert.True(onSpec.IncludeDepthRim);
        Assert.False(onSpec.IncludeDepthRimAlphaCoverage);
        Assert.True(onSpec.IncludeBloom);
        Assert.False(onSpec.IncludeBloomAlphaCoverage);
        Assert.True(onSpec.IncludeToneCurve);

        var straightSpec = D3D12ViewShaderPrewarm.BuildFromViewRequest(
            quality, straightAlpha: true, styleCatalog: catalog, stylePresets: presets, enableStylizedPolish: true);
        Assert.True(straightSpec.IncludeDepthRim);
        Assert.True(straightSpec.IncludeDepthRimAlphaCoverage);
        Assert.True(straightSpec.IncludeBloom);
        Assert.True(straightSpec.IncludeBloomAlphaCoverage);
        Assert.True(straightSpec.IncludeToneCurve);

        var noVariantsSpec = D3D12ViewShaderPrewarm.BuildFromViewRequest(
            quality, styleCatalog: D3D12ViewStyleCatalog.Empty, stylePresets: D3D12StylePresetCatalog.BuiltIn, enableStylizedPolish: true);
        Assert.False(noVariantsSpec.IncludeDepthRim);
        Assert.False(noVariantsSpec.IncludeBloom);
        Assert.False(noVariantsSpec.IncludeToneCurve);

        var pathTracingSpec = D3D12ViewShaderPrewarm.BuildFromViewRequest(
            new ViewQualityOptions(PipelineMode: RenderPipelineMode.PathTracing),
            styleCatalog: catalog, stylePresets: presets, enableStylizedPolish: true);
        Assert.False(pathTracingSpec.IncludeDepthRim);
        Assert.False(pathTracingSpec.IncludeBloom);
        Assert.False(pathTracingSpec.IncludeToneCurve);
    }
}

[Collection(RayGiTimingEnvironmentCollection.Name)]
[Trait("Category", "Gpu")]
public sealed class D3D12StylizedPolishGpuTests
{
    [Fact]
    public void MmdBaseline_ZeroPixelDiffWithOffAndZeroStrengthPolishOptions()
    {
        using var session = new D3D12FrameSession();
        var frame = D3D12StylizedPolishTestScenes.CubeScene();

        var baseline = session.Capture(frame, 128, 96);
        var off = session.Capture(
            frame, 128, 96,
            depthRim: D3D12DepthRimOptions.Off,
            bloom: D3D12BloomOptions.Off,
            toneCurve: D3D12ToneCurveOptions.Off);
        var neutral = session.Capture(
            frame, 128, 96,
            depthRim: new D3D12DepthRimOptions(Enabled: true, Strength: 0f),
            bloom: new D3D12BloomOptions(Enabled: true, Intensity: 0f),
            toneCurve: new D3D12ToneCurveOptions(Enabled: true, Contrast: 0f, Shoulder: 0f, Saturation: 1f));

        Assert.Equal(baseline.Rgba, off.Rgba);
        Assert.Equal(baseline.Rgba, neutral.Rgba);
        Assert.DoesNotContain(session.LastRecordings, r => r.Channel.StartsWith("depth-rim:", StringComparison.Ordinal));
        Assert.DoesNotContain(session.LastRecordings, r => r.Channel.StartsWith("bloom:", StringComparison.Ordinal));
        Assert.DoesNotContain(session.LastRecordings, r => r.Channel.StartsWith("tone-curve:", StringComparison.Ordinal));
        Assert.Equal(0, session.Statistics!.DepthRimPassDrawCalls);
        Assert.Equal(0, session.Statistics.BloomPassDrawCalls);
        Assert.Equal(0, session.Statistics.ToneCurvePassDrawCalls);
        Assert.Empty(session.LastDebugMessages);
    }

    [Fact]
    public void StylizedCastShadowAndFaceLight_ProduceMeasurableDifferenceOnPluginMaterials()
    {
        using var session = new D3D12FrameSession();
        var catalog = TestStylePlugins.AllPresetCatalog;
        var starRail = catalog.Find("star-rail");
        var baseShading = starRail.DefaultStylizedShading!;

        // Material 0 = Body receiver with cast shadow, Material 1 = Face with FaceLightDirection.
        var frame = D3D12StylizedPolishTestScenes.TwoQuadShadowAndFaceScene();
        var objectId = Guid.NewGuid();
        var binding = new SceneRenderingStyleBinding(objectId, new("star-rail"), ModelRole.Character, 0, 2, true);

        var offRoles = new D3D12ViewCharacterMaterial[]
        {
            new(objectId, 0, D3D12CharacterMaterialRole.Body,
                StylizedShading: D3D12StylizedShadingCatalog.ResolveForRole(
                    baseShading with { CastShadowStrength = 0f, FaceLightStrength = 0f }, D3D12CharacterMaterialRole.Body)),
            new(objectId, 1, D3D12CharacterMaterialRole.Face,
                StylizedShading: D3D12StylizedShadingCatalog.ResolveForRole(
                    baseShading with { CastShadowStrength = 0f, FaceLightStrength = 0f }, D3D12CharacterMaterialRole.Face)),
        };
        var offResolved = D3D12ViewRenderingStyles.Resolve(
            frame, catalog, [binding], offRoles, styleCatalog: TestStylePlugins.AllViewCatalog);

        var defaultRoles = new D3D12ViewCharacterMaterial[]
        {
            new(objectId, 0, D3D12CharacterMaterialRole.Body),
            new(objectId, 1, D3D12CharacterMaterialRole.Face),
        };
        var defaultResolved = D3D12ViewRenderingStyles.Resolve(
            frame, catalog, [binding], defaultRoles, styleCatalog: TestStylePlugins.AllViewCatalog);

        var explicitFaceRoles = new D3D12ViewCharacterMaterial[]
        {
            new(objectId, 0, D3D12CharacterMaterialRole.Body),
            new(objectId, 1, D3D12CharacterMaterialRole.Face,
                StylizedShading: D3D12StylizedShadingCatalog.ResolveForRole(
                    baseShading with { FaceLightDirection = Vector3.Normalize(new Vector3(-0.6f, 0f, -0.8f)) },
                    D3D12CharacterMaterialRole.Face)),
        };
        var explicitResolved = D3D12ViewRenderingStyles.Resolve(
            frame, catalog, [binding], explicitFaceRoles, styleCatalog: TestStylePlugins.AllViewCatalog);

        var offPixels = session.Capture(offResolved.Scene, 128, 96, surfaceStyles: offResolved.SurfaceStyles);
        var defaultPixels = session.Capture(defaultResolved.Scene, 128, 96, surfaceStyles: defaultResolved.SurfaceStyles);
        var explicitPixels = session.Capture(explicitResolved.Scene, 128, 96, surfaceStyles: explicitResolved.SurfaceStyles);

        Assert.NotEqual(offPixels.Rgba, defaultPixels.Rgba);

        // Verify left (Body shadow receiver) changed automatically by style defaults and right (Face) responds to both default FaceLightStrength and explicit FaceLightDirection.
        int bodyDelta = MaxRegionDelta(offPixels.Rgba, defaultPixels.Rgba, 128, 8, 56, 20, 76);
        int faceStrengthDelta = MaxRegionDelta(offPixels.Rgba, defaultPixels.Rgba, 128, 72, 120, 20, 76);
        int faceDirDelta = MaxRegionDelta(defaultPixels.Rgba, explicitPixels.Rgba, 128, 72, 120, 20, 76);
        Assert.True(bodyDelta > 5, $"Expected default cast shadow difference on Body quad, got delta={bodyDelta}.");
        Assert.True(faceStrengthDelta > 5, $"Expected default face light difference on Face quad, got delta={faceStrengthDelta}.");
        Assert.True(faceDirDelta > 5, $"Expected FaceLightDirection change on Face quad vs default direction, got delta={faceDirDelta}.");
        Assert.Empty(session.LastDebugMessages);
    }

    [Fact]
    public void DepthRim_WorksOn1xAndMsaa4x_SharesDepthResolveWithSsao_AndRejectsDisabledDepthWrite()
    {
        using var session = new D3D12FrameSession();
        var scene = D3D12StylizedPolishTestScenes.CubeScene();
        var rimOptions = new D3D12DepthRimOptions(Enabled: true, Strength: 0.9f, WidthPixels: 2.5f, DepthThreshold: 0.05f, LightBias: 0f);

        // Reject WriteDepth = false before GPU submission.
        var noDepthScene = new OriginalFrameScene(
            scene.Surface,
            options: scene.Options! with { WriteDepth = false },
            materials: scene.Materials);
        long submissionsBefore = session.Runtime.Submissions;
        Assert.Throws<NotSupportedException>(() => session.Capture(noDepthScene, 96, 96, depthRim: rimOptions));
        Assert.Equal(submissionsBefore, session.Runtime.Submissions);

        // 1x DepthRim brightens silhouette pixels while keeping background unchanged.
        var base1x = session.Capture(scene, 96, 96);
        var rim1x = session.Capture(scene, 96, 96, depthRim: rimOptions);
        Assert.Contains(session.LastRecordings, r => r.Channel == "depth-rim:apply");
        Assert.Equal(1, session.Statistics!.DepthRimPassDrawCalls);
        Assert.Equal(0, session.Statistics.ScreenSpaceAoPassDrawCalls);
        Assert.True(CountBrightenedPixels(base1x.Rgba, rim1x.Rgba) >= 20, "Expected silhouette rim pixels to brighten.");

        // Background corner pixel remains exact.
        Assert.Equal(base1x.Rgba[0], rim1x.Rgba[0]);
        Assert.Equal(base1x.Rgba[1], rim1x.Rgba[1]);
        Assert.Equal(base1x.Rgba[2], rim1x.Rgba[2]);

        // MSAA 4x without SSAO resolves depth once and applies DepthRim.
        var msaaScene = new OriginalFrameScene(
            scene.Surface,
            options: scene.Options! with { Antialiasing = FrameAntialiasing.Msaa4X },
            materials: scene.Materials);
        var msaaRim = session.Capture(msaaScene, 96, 96, depthRim: rimOptions);
        Assert.Equal(96 * 96 * 4, msaaRim.Rgba.Length);
        Assert.Single(session.LastRecordings, r => r.Channel == "msaa:depth-resolve");
        Assert.Contains(session.LastRecordings, r => r.Channel == "depth-rim:apply");
        Assert.Equal(2, session.Statistics!.DepthRimPassDrawCalls);
        Assert.Equal(0, session.Statistics.ScreenSpaceAoPassDrawCalls);

        // MSAA 4x with both SSAO and DepthRim shares a single msaa:depth-resolve pass.
        var msaaBothScene = new OriginalFrameScene(
            scene.Surface,
            options: scene.Options! with
            {
                Antialiasing = FrameAntialiasing.Msaa4X,
                ScreenSpaceAo = new ScreenSpaceAoOptions(Enabled: true, Strength: 0.6f, Radius: 1.5f, SampleCount: 4),
            },
            materials: scene.Materials);
        _ = session.Capture(msaaBothScene, 96, 96, depthRim: rimOptions);
        Assert.Single(session.LastRecordings, r => r.Channel == "msaa:depth-resolve");
        Assert.Contains(session.LastRecordings, r => r.Channel == "ssao:composite");
        Assert.Contains(session.LastRecordings, r => r.Channel == "depth-rim:apply");
        Assert.Equal(3, session.Statistics!.ScreenSpaceAoPassDrawCalls);
        Assert.Equal(1, session.Statistics.DepthRimPassDrawCalls);
        Assert.Empty(session.LastDebugMessages);
    }

    [Fact]
    public void BloomAndToneCurve_ApplyAtDisplayRes_WithZeroReadbacksAndStablePipelineCache()
    {
        using var session = new D3D12FrameSession();
        var frame = D3D12StylizedPolishTestScenes.BrightQuadOnDarkScene();
        var bloom = new D3D12BloomOptions(Enabled: true, Threshold: 0.45f, SoftKnee: 0.25f, Intensity: 0.75f, Scatter: 0.7f);
        var tone = new D3D12ToneCurveOptions(Enabled: true, Contrast: 0.15f, Shoulder: 0.30f, Saturation: 1.15f);

        var baseline = session.Capture(frame, 128, 96);
        var polished = session.Capture(frame, 128, 96, bloom: bloom, toneCurve: tone);

        Assert.Contains(session.LastRecordings, r => r.Channel == "bloom:composite");
        Assert.Contains(session.LastRecordings, r => r.Channel == "tone-curve:apply");
        Assert.True(session.Statistics!.BloomPassDrawCalls >= 2);
        Assert.Equal(1, session.Statistics.ToneCurvePassDrawCalls);
        Assert.NotEqual(baseline.Rgba, polished.Rgba);
        // Dark border adjacent to bright center quad should pick up bloom glow.
        Assert.True(CountBrightenedPixels(baseline.Rgba, polished.Rgba) >= 20);

        // Warmed GPU Render with varying parameters performs 0 readbacks and 0 extra compilations/pipelines.
        using (var first = session.Render(frame, 128, 96, bloom: bloom, toneCurve: tone))
        {
            Assert.Equal(0, session.Statistics!.ReadbackCount);
        }
        long shadersAfterFirst = session.Statistics!.ShaderCompilations;
        long pipelinesAfterFirst = session.Statistics.PipelineCreations;

        using (var second = session.Render(
            frame, 128, 96,
            bloom: bloom with { Intensity = 0.40f },
            toneCurve: tone with { Contrast = 0.05f }))
        {
            Assert.Equal(0, session.Statistics!.ReadbackCount);
            Assert.Equal(shadersAfterFirst, session.Statistics.ShaderCompilations);
            Assert.Equal(pipelinesAfterFirst, session.Statistics.PipelineCreations);
        }

        // Verify RenderScale < 1 still executes Bloom and ToneCurve after upscaling at display resolution 128x96.
        var scaledFrame = new OriginalFrameScene(
            frame.Surface,
            options: frame.Options! with { RenderScale = 0.5f },
            materials: frame.Materials);
        var scaledResult = session.Capture(scaledFrame, 128, 96, bloom: bloom, toneCurve: tone);
        Assert.Equal(128 * 96 * 4, scaledResult.Rgba.Length);
        var channels = session.LastRecordings.Select(r => r.Channel).ToList();
        int scaleIdx = channels.IndexOf("render-scale");
        int bloomIdx = channels.IndexOf("bloom:composite");
        int toneIdx = channels.IndexOf("tone-curve:apply");
        Assert.True(scaleIdx >= 0 && bloomIdx > scaleIdx && toneIdx > bloomIdx,
            $"Expected render-scale before bloom/tone-curve, got: {string.Join(" -> ", channels)}");
        Assert.Contains(session.LastTargets, t => t.Name == "$bloom-composite" && t.Width == 128 && t.Height == 96);
        Assert.Contains(session.LastTargets, t => t.Name == "$tone-curve" && t.Width == 128 && t.Height == 96);
        Assert.Empty(session.LastDebugMessages);
    }

    [Fact]
    public void StraightAlpha_ProtectsZeroAlphaBackgroundAcrossDepthRimBloomAndToneCurve()
    {
        using var session = new D3D12FrameSession();
        var straightScene = D3D12StylizedPolishTestScenes.QuadFrame(straightAlpha: true);
        var legacyTransparent = new OriginalFrameScene(
            straightScene.Surface,
            options: straightScene.Options! with { AlphaOutput = FrameAlphaOutput.LegacyMax },
            materials: straightScene.Materials);

        var rim = new D3D12DepthRimOptions(Enabled: true, Strength: 0.7f, WidthPixels: 2f, LightBias: 0f);
        var bloom = new D3D12BloomOptions(Enabled: true, Threshold: 0.4f, Intensity: 0.6f);
        var tone = new D3D12ToneCurveOptions(Enabled: true, Contrast: 0.1f, Shoulder: 0.25f, Saturation: 1.1f);

        long submissionsBefore = session.Runtime.Submissions;
        Assert.Throws<NotSupportedException>(() => session.Capture(legacyTransparent, 96, 96, depthRim: rim));
        Assert.Throws<NotSupportedException>(() => session.Capture(legacyTransparent, 96, 96, bloom: bloom));
        Assert.Equal(submissionsBefore, session.Runtime.Submissions);

        var baseStraight = session.Capture(straightScene, 96, 96);
        var polishStraight = session.Capture(straightScene, 96, 96, depthRim: rim, bloom: bloom, toneCurve: tone);

        var channels = session.LastRecordings.Select(r => r.Channel).ToList();
        int rimIdx = channels.IndexOf("depth-rim:apply");
        int bloomIdx = channels.IndexOf("bloom:composite");
        int alphaIdx = channels.IndexOf("alpha:straight");
        int toneIdx = channels.IndexOf("tone-curve:apply");
        Assert.True(rimIdx >= 0 && bloomIdx > rimIdx && alphaIdx > bloomIdx && toneIdx > alphaIdx,
            $"Unexpected channel order: {string.Join(" -> ", channels)}");

        int transparentCount = 0;
        int opaqueCount = 0;
        for (int i = 0; i < baseStraight.Rgba.Length; i += 4)
        {
            if (baseStraight.Rgba[i + 3] == 0)
            {
                transparentCount++;
                Assert.Equal(0, polishStraight.Rgba[i]);
                Assert.Equal(0, polishStraight.Rgba[i + 1]);
                Assert.Equal(0, polishStraight.Rgba[i + 2]);
                Assert.Equal(0, polishStraight.Rgba[i + 3]);
            }
            else if (baseStraight.Rgba[i + 3] == 255)
            {
                opaqueCount++;
                Assert.Equal(255, polishStraight.Rgba[i + 3]);
            }
        }
        Assert.True(transparentCount > 100, "Expected transparent background pixels.");
        Assert.True(opaqueCount > 100, "Expected opaque foreground pixels.");
        Assert.Empty(session.LastDebugMessages);
    }

    [Fact]
    public void DepthRim_ZeroFalseRimOnTiltedFullscreenQuad_AndSafeOnTinyTargets()
    {
        using var session = new D3D12FrameSession();
        var rimStandard = new D3D12DepthRimOptions(Enabled: true, Strength: 0.85f, WidthPixels: 2.0f, DepthThreshold: 0.06f, LightBias: 0f);
        var rimNarrow = rimStandard with { WidthPixels = 0.75f };
        var rimWide = rimStandard with { WidthPixels = 8.0f };

        foreach (bool straightAlpha in new[] { false, true })
        {
            foreach (bool topCloser in new[] { false, true })
            {
                var tilted = D3D12StylizedPolishTestScenes.TiltedFullscreenQuadScene(topCloser, straightAlpha);
                var baseline = session.Capture(tilted, 96, 96);
                foreach (var rim in new[] { rimStandard, rimNarrow, rimWide })
                {
                    var withRim = session.Capture(tilted, 96, 96, depthRim: rim);
                    Assert.Empty(session.LastDebugMessages);
                    int top = MaxRegionDelta(baseline.Rgba, withRim.Rgba, 96, 0, 96, 0, 4);
                    int bottom = MaxRegionDelta(baseline.Rgba, withRim.Rgba, 96, 0, 96, 92, 96);
                    int left = MaxRegionDelta(baseline.Rgba, withRim.Rgba, 96, 0, 4, 0, 96);
                    int right = MaxRegionDelta(baseline.Rgba, withRim.Rgba, 96, 92, 96, 0, 96);
                    int interior = MaxRegionDelta(baseline.Rgba, withRim.Rgba, 96, 10, 86, 10, 86);

                    Assert.True(
                        top == 0 && bottom == 0 && left == 0 && right == 0 && interior == 0,
                        $"False rim on tilted quad (straight={straightAlpha}, topCloser={topCloser}, width={rim.WidthPixels}): top={top}, bottom={bottom}, left={left}, right={right}, interior={interior}");
                }
            }
        }

        // Tiny 1x1 and 2x2 targets render cleanly without errors or debug messages.
        foreach (bool straightAlpha in new[] { false, true })
        {
            var quad = D3D12StylizedPolishTestScenes.QuadFrame(straightAlpha);
            foreach (int size in new[] { 1, 2 })
            {
                var tiny = session.Capture(quad, size, size, depthRim: rimStandard);
                Assert.Equal(size * size * 4, tiny.Rgba.Length);
                Assert.Contains(session.LastRecordings, r => r.Channel == "depth-rim:apply");
                Assert.Equal(1, session.Statistics!.DepthRimPassDrawCalls);
                Assert.Empty(session.LastDebugMessages);
            }
        }
    }

    [Fact]
    public async Task PrewarmAsync_CoversMsaa4xDepthRimBloomToneCurve_WithZeroExtraFxcCompilations()
    {
        string tempRoot = Environment.GetEnvironmentVariable("MMD_DXC_TEST_CACHE") is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.GetTempPath();
        string tempCache = Path.Combine(tempRoot, "stylized-polish-fxc-" + Guid.NewGuid().ToString("N"));
        string? previousCache = Environment.GetEnvironmentVariable("MMD_DXC_CACHE");
        Directory.CreateDirectory(tempCache);

        try
        {
            Environment.SetEnvironmentVariable("MMD_DXC_CACHE", tempCache);
            using var session = new D3D12FrameSession();
            var baseScene = D3D12StylizedPolishTestScenes.CubeScene();
            var msaa4xScene = new OriginalFrameScene(
                baseScene.Surface,
                options: baseScene.Options! with { Antialiasing = FrameAntialiasing.Msaa4X },
                materials: baseScene.Materials);

            var rim = new D3D12DepthRimOptions(Enabled: true, Strength: 0.5f, WidthPixels: 1.5f);
            var bloom = new D3D12BloomOptions(Enabled: true);
            var tone = new D3D12ToneCurveOptions(Enabled: true);

            var spec = D3D12ShaderPrewarmer.SpecFromScene(msaa4xScene, depthRim: rim, bloom: bloom, toneCurve: tone);
            Assert.False(spec.IncludeScreenSpaceAo);
            Assert.True(spec.IncludeDepthRim);
            Assert.True(spec.IncludeBloom);
            Assert.True(spec.IncludeToneCurve);

            await session.PrewarmAsync(spec, cancellationToken: TestContext.Current.CancellationToken);

            // Warm baseline MSAA4x surface shaders first.
            _ = session.Capture(msaa4xScene, 96, 96);
            Assert.Empty(session.LastDebugMessages);
            int invAfterBase = D3D12FxcShaderCache.InvocationCount;

            // Subsequent MSAA4x + DepthRim + Bloom + ToneCurve capture must trigger 0 extra FXC compiles.
            var polished = session.Capture(msaa4xScene, 96, 96, depthRim: rim, bloom: bloom, toneCurve: tone);
            int extraFxcCompiles = D3D12FxcShaderCache.InvocationCount - invAfterBase;

            Assert.Equal(0, extraFxcCompiles);
            Assert.Equal(96 * 96 * 4, polished.Rgba.Length);
            Assert.Single(session.LastRecordings, r => r.Channel == "msaa:depth-resolve");
            Assert.Contains(session.LastRecordings, r => r.Channel == "depth-rim:apply");
            Assert.Contains(session.LastRecordings, r => r.Channel == "bloom:composite");
            Assert.Contains(session.LastRecordings, r => r.Channel == "tone-curve:apply");
            Assert.Equal(0, session.Statistics!.ScreenSpaceAoPassDrawCalls);
            Assert.Empty(session.LastDebugMessages);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MMD_DXC_CACHE", previousCache);
            try { if (Directory.Exists(tempCache)) Directory.Delete(tempCache, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ViewRenderer_WithDefaultStylizedPolishEnabled_EnhancesModernStylesWhileKeepingMmdByteExact()
    {
        const int width = 96, height = 96;
        var token = TestContext.Current.CancellationToken;
        var scene = D3D12StylizedPolishTestScenes.QuadFrame(straightAlpha: true);
        var plan = new OfflineFramePlan(width, height, new(30), 0, 2);
        var objectId = Guid.NewGuid();
        var request = new ViewRenderRequest(width, height);

        OfflineFrame FrameWithStyle(int sampleIndex, string styleId) =>
            new(plan.GetSample(sampleIndex), width, height, scene, null, null, null)
            {
                RenderingStyles = [new SceneRenderingStyleBinding(objectId, new(styleId), ModelRole.Character, 0, scene.Materials.Count, true)]
            };

        await using var offRenderer = new D3D12ViewRenderer(
            request,
            stylePresets: TestStylePlugins.AllPresetCatalog,
            styleCatalog: TestStylePlugins.AllViewCatalog,
            enableStylizedPolish: false);
        await using var onRenderer = new D3D12ViewRenderer(
            request,
            stylePresets: TestStylePlugins.AllPresetCatalog,
            styleCatalog: TestStylePlugins.AllViewCatalog,
            enableStylizedPolish: true);

        byte[] mmdOff, mmdOn, starRailOff, starRailOn;
        using (var lease = await offRenderer.CaptureAsync(FrameWithStyle(0, "mmd"), token))
            mmdOff = lease.Pixels.ToArray();
        using (var lease = await onRenderer.CaptureAsync(FrameWithStyle(0, "mmd"), token))
            mmdOn = lease.Pixels.ToArray();

        Assert.Equal(mmdOff, mmdOn);

        using (var lease = await offRenderer.CaptureAsync(FrameWithStyle(1, "star-rail"), token))
            starRailOff = lease.Pixels.ToArray();
        using (var lease = await onRenderer.CaptureAsync(FrameWithStyle(1, "star-rail"), token))
            starRailOn = lease.Pixels.ToArray();

        Assert.NotEqual(starRailOff, starRailOn);
        for (int i = 0; i < starRailOff.Length; i += 4)
        {
            if (starRailOff[i + 3] == 0)
            {
                Assert.Equal(0, starRailOn[i]);
                Assert.Equal(0, starRailOn[i + 1]);
                Assert.Equal(0, starRailOn[i + 2]);
                Assert.Equal(0, starRailOn[i + 3]);
            }
        }
    }

    [Fact]
    public void ToneCurve_CompressesHighlightsMonotonically_AndPreservesZeroAlphaRgbInLegacyMax()
    {
        using var session = new D3D12FrameSession();
        var shoulderOne = new D3D12ToneCurveOptions(Enabled: true, Contrast: 0f, Shoulder: 1.0f, Saturation: 1f);

        // ~0.90 gray quad with colored zero-alpha LegacyMax background.
        var coloredBg = new Vector4(0.25f, 0.50f, 0.75f, 0f);
        var highGray = D3D12StylizedPolishTestScenes.FlatLitQuad(new(0.90f, 0.90f, 0.90f, 1f), coloredBg, FrameAlphaOutput.LegacyMax);
        var baseGray = session.Capture(highGray, 64, 64);
        var tonedGray = session.Capture(highGray, 64, 64, toneCurve: shoulderOne);

        int center = (32 * 64 + 32) * 4;
        Assert.InRange(baseGray.Rgba[center], (byte)220, (byte)240);
        Assert.True(tonedGray.Rgba[center] < baseGray.Rgba[center],
            $"Expected highlight shoulder to compress ~0.90 gray instead of lifting ({baseGray.Rgba[center]} -> {tonedGray.Rgba[center]}).");

        // Zero-alpha background RGB in LegacyMax must remain byte-exact.
        Assert.Equal(0, baseGray.Rgba[3]);
        Assert.Equal(baseGray.Rgba[0], tonedGray.Rgba[0]);
        Assert.Equal(baseGray.Rgba[1], tonedGray.Rgba[1]);
        Assert.Equal(baseGray.Rgba[2], tonedGray.Rgba[2]);
        Assert.Equal(baseGray.Rgba[3], tonedGray.Rgba[3]);

        // White (1.0) also compresses monotonically.
        var whiteQuad = D3D12StylizedPolishTestScenes.FlatLitQuad(Vector4.One, new(0f, 0f, 0f, 1f), FrameAlphaOutput.LegacyMax);
        var baseWhite = session.Capture(whiteQuad, 64, 64);
        var tonedWhite = session.Capture(whiteQuad, 64, 64, toneCurve: shoulderOne);
        Assert.Equal(255, baseWhite.Rgba[center]);
        Assert.True(tonedWhite.Rgba[center] < baseWhite.Rgba[center]);
        Assert.Empty(session.LastDebugMessages);
    }

    [Fact]
    public void StylizedCastShadow_IgnoresSelfShadowBeyondFarPlaneWhenReceiveSelfShadowIsFalse()
    {
        using var session = new D3D12FrameSession();
        var catalog = TestStylePlugins.AllPresetCatalog;
        var baseShading = catalog.Find("star-rail").DefaultStylizedShading!;
        var objectId = Guid.NewGuid();
        var binding = new SceneRenderingStyleBinding(objectId, new("star-rail"), ModelRole.Character, 0, 1, true);

        // Self-shadow projection puts the quad at light-clip z > 1, while material ReceiveSelfShadow is false.
        var farShadowProjection = Matrix4x4.CreateTranslation(0f, 0f, 5f);
        var shadow = new OriginalSelfShadowOptions(
            farShadowProjection, 64, 64, true, false,
            OriginalTextureSampling.LinearClamp, OriginalDepthComparison.LessEqual);
        var frame = D3D12StylizedPolishTestScenes.FlatLitQuad(
            new(0.85f, 0.85f, 0.90f, 1f), new(0.1f, 0.1f, 0.1f, 1f), FrameAlphaOutput.LegacyMax,
            selfShadow: shadow, receiveSelfShadow: false);

        var offRoles = new D3D12ViewCharacterMaterial[]
        {
            new(objectId, 0, D3D12CharacterMaterialRole.Body,
                StylizedShading: D3D12StylizedShadingCatalog.ResolveForRole(
                    baseShading with { CastShadowStrength = 0f, FaceLightStrength = 0f }, D3D12CharacterMaterialRole.Body)),
        };
        var onRoles = new D3D12ViewCharacterMaterial[]
        {
            new(objectId, 0, D3D12CharacterMaterialRole.Body,
                StylizedShading: D3D12StylizedShadingCatalog.ResolveForRole(
                    baseShading with { CastShadowStrength = 0.85f, FaceLightStrength = 0f }, D3D12CharacterMaterialRole.Body)),
        };

        var offResolved = D3D12ViewRenderingStyles.Resolve(frame, catalog, [binding], offRoles, styleCatalog: TestStylePlugins.AllViewCatalog);
        var onResolved = D3D12ViewRenderingStyles.Resolve(frame, catalog, [binding], onRoles, styleCatalog: TestStylePlugins.AllViewCatalog);

        var offPixels = session.Capture(offResolved.Scene, 64, 64, surfaceStyles: offResolved.SurfaceStyles);
        var onPixels = session.Capture(onResolved.Scene, 64, 64, surfaceStyles: onResolved.SurfaceStyles);
        Assert.Equal(offPixels.Rgba, onPixels.Rgba);
        Assert.Empty(session.LastDebugMessages);
    }

    private static int MaxRegionDelta(byte[] a, byte[] b, int width, int x0, int x1, int y0, int y1)
    {
        int max = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int offset = (y * width + x) * 4;
                int delta = Math.Max(
                    Math.Abs(a[offset] - b[offset]),
                    Math.Max(Math.Abs(a[offset + 1] - b[offset + 1]), Math.Abs(a[offset + 2] - b[offset + 2])));
                if (delta > max) max = delta;
            }
        return max;
    }

    private static int CountBrightenedPixels(byte[] before, byte[] after)
    {
        int count = 0;
        for (int i = 0; i < before.Length; i += 4)
        {
            int sumBefore = before[i] + before[i + 1] + before[i + 2];
            int sumAfter = after[i] + after[i + 1] + after[i + 2];
            if (sumAfter >= sumBefore + 12) count++;
        }
        return count;
    }
}

file static class D3D12StylizedPolishTestScenes
{
    internal static OriginalFrameScene QuadFrame(bool straightAlpha)
    {
        var vertices = new SceneVertex[]
        {
            new(new(-0.55f, -0.55f, 0f), -Vector3.UnitZ, Vector2.Zero),
            new(new(0.55f, -0.55f, 0f), -Vector3.UnitZ, Vector2.UnitX),
            new(new(0.55f, 0.55f, 0f), -Vector3.UnitZ, Vector2.One),
            new(new(-0.55f, 0.55f, 0f), -Vector3.UnitZ, Vector2.UnitY),
        };
        uint[] indices = [0, 1, 2, 0, 2, 3];
        var material = new SceneMaterial("quad", 0, 6, new(0.95f, 0.85f, 0.75f, 1f), Vector3.Zero, Vector3.Zero, 1f);
        var camera = new SceneCamera(new(0, 0, -2.5f), Vector3.Zero, Vector3.UnitY, 2f, 0.1f, 20f, 45f, LeftHanded: true);
        var snapshot = new SceneSnapshot(vertices, indices, [material], camera: camera, lightDirection: Vector3.Normalize(new(-0.5f, 0.6f, -0.6f)));
        var surface = new OriginalStyleScene(snapshot, [new(new(0.5f), DoubleSided: true)], new OriginalStyleProfile(new(0.5f, 0.5f, 0.5f), Vector3.One, Matrix4x4.Identity));
        var options = new OriginalFrameOptions(
            OriginalAlphaBlendMode.Normal,
            WriteDepth: true,
            Transp: false,
            AlphaOutput: straightAlpha ? FrameAlphaOutput.Straight : FrameAlphaOutput.LegacyMax,
            BackgroundColor: straightAlpha ? new Vector4(0, 0, 0, 0) : new Vector4(0.08f, 0.08f, 0.12f, 1f));
        return new OriginalFrameScene(surface, options: options, materials: [new(true, false, false)]);
    }

    internal static OriginalFrameScene TiltedFullscreenQuadScene(bool topCloser, bool straightAlpha)
    {
        float zBottom = topCloser ? 2.5f : -0.5f;
        float zTop = topCloser ? -0.5f : 2.5f;
        var normal = Vector3.Normalize(new(0f, topCloser ? 3f : -3f, -10f));
        var vertices = new SceneVertex[]
        {
            new(new(-5f, -5f, zBottom), normal, Vector2.Zero),
            new(new(5f, -5f, zBottom), normal, Vector2.UnitX),
            new(new(5f, 5f, zTop), normal, Vector2.One),
            new(new(-5f, 5f, zTop), normal, Vector2.UnitY),
        };
        uint[] indices = [0, 1, 2, 0, 2, 3];
        var material = new SceneMaterial("tilted-quad", 0, 6, new(0.65f, 0.72f, 0.80f, 1f), Vector3.Zero, Vector3.Zero, 1f);
        var camera = new SceneCamera(new(0, 0, -2.5f), Vector3.Zero, Vector3.UnitY, 2f, 0.1f, 30f, 45f, LeftHanded: true);
        var snapshot = new SceneSnapshot(vertices, indices, [material], camera: camera, lightDirection: Vector3.Normalize(new(-0.4f, 0.5f, -0.7f)));
        var surface = new OriginalStyleScene(snapshot, [new(new(0.5f), DoubleSided: true)], new OriginalStyleProfile(new(0.5f, 0.5f, 0.5f), Vector3.One, Matrix4x4.Identity));
        var options = new OriginalFrameOptions(
            OriginalAlphaBlendMode.Normal,
            WriteDepth: true,
            Transp: false,
            AlphaOutput: straightAlpha ? FrameAlphaOutput.Straight : FrameAlphaOutput.LegacyMax,
            BackgroundColor: straightAlpha ? new Vector4(0, 0, 0, 0) : new Vector4(0.08f, 0.08f, 0.12f, 1f));
        return new OriginalFrameScene(surface, options: options, materials: [new(true, false, false)]);
    }

    internal static OriginalFrameScene BrightQuadOnDarkScene()
    {
        var vertices = new SceneVertex[]
        {
            new(new(-0.45f, -0.45f, 0f), -Vector3.UnitZ, Vector2.Zero),
            new(new(0.45f, -0.45f, 0f), -Vector3.UnitZ, Vector2.UnitX),
            new(new(0.45f, 0.45f, 0f), -Vector3.UnitZ, Vector2.One),
            new(new(-0.45f, 0.45f, 0f), -Vector3.UnitZ, Vector2.UnitY),
        };
        uint[] indices = [0, 1, 2, 0, 2, 3];
        var material = new SceneMaterial("bright", 0, 6, Vector4.One, Vector3.Zero, Vector3.One, 1f);
        var camera = new SceneCamera(new(0, 0, -2.5f), Vector3.Zero, Vector3.UnitY, 2f, 0.1f, 20f, 45f, LeftHanded: true);
        var snapshot = new SceneSnapshot(vertices, indices, [material], camera: camera, lightDirection: -Vector3.UnitZ);
        var surface = new OriginalStyleScene(snapshot, [new(new(0.6f), DoubleSided: true)], new OriginalStyleProfile(Vector3.One, Vector3.One, Matrix4x4.Identity));
        var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: new Vector4(0.04f, 0.04f, 0.06f, 1f));
        return new OriginalFrameScene(surface, options: options, materials: [new(true, false, false)]);
    }

    internal static OriginalFrameScene CubeScene()
    {
        var vertices = new SceneVertex[]
        {
            // Front face (-Z)
            new(new(-0.7f, -0.7f, -0.7f), -Vector3.UnitZ, Vector2.Zero),
            new(new(0.7f, -0.7f, -0.7f), -Vector3.UnitZ, Vector2.UnitX),
            new(new(0.7f, 0.7f, -0.7f), -Vector3.UnitZ, Vector2.One),
            new(new(-0.7f, 0.7f, -0.7f), -Vector3.UnitZ, Vector2.UnitY),
            // Right face (+X)
            new(new(0.7f, -0.7f, -0.7f), Vector3.UnitX, Vector2.Zero),
            new(new(0.7f, -0.7f, 0.7f), Vector3.UnitX, Vector2.UnitX),
            new(new(0.7f, 0.7f, 0.7f), Vector3.UnitX, Vector2.One),
            new(new(0.7f, 0.7f, -0.7f), Vector3.UnitX, Vector2.UnitY),
            // Top face (+Y)
            new(new(-0.7f, 0.7f, -0.7f), Vector3.UnitY, Vector2.Zero),
            new(new(0.7f, 0.7f, -0.7f), Vector3.UnitY, Vector2.UnitX),
            new(new(0.7f, 0.7f, 0.7f), Vector3.UnitY, Vector2.One),
            new(new(-0.7f, 0.7f, 0.7f), Vector3.UnitY, Vector2.UnitY),
        };
        uint[] indices =
        [
            0, 1, 2, 0, 2, 3,
            4, 5, 6, 4, 6, 7,
            8, 9, 10, 8, 10, 11,
        ];
        var material = new SceneMaterial("cube", 0, indices.Length, new(0.45f, 0.55f, 0.72f, 1f), Vector3.Zero, Vector3.Zero, 1f);
        var camera = new SceneCamera(new(1.8f, 1.4f, -3.2f), Vector3.Zero, Vector3.UnitY, 3f, 0.1f, 30f, 45f, LeftHanded: true);
        var snapshot = new SceneSnapshot(vertices, indices, [material], camera: camera, lightDirection: Vector3.Normalize(new(-0.6f, 0.7f, -0.4f)));
        var surface = new OriginalStyleScene(snapshot, [new(new(0.5f), DoubleSided: true)], new OriginalStyleProfile(new(0.5f, 0.5f, 0.5f), Vector3.One, Matrix4x4.Identity));
        var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: new Vector4(0.05f, 0.05f, 0.08f, 1f));
        return new OriginalFrameScene(surface, options: options, materials: [new(true, false, false)]);
    }

    internal static OriginalFrameScene TwoQuadShadowAndFaceScene()
    {
        // Left quad (Body, receives shadow from occluder behind light), Right quad (Face, angled normal).
        var vertices = new SceneVertex[]
        {
            // Material 0: Body quad on left [-1.4, -0.1]
            new(new(-1.4f, -0.8f, 0f), -Vector3.UnitZ, Vector2.Zero),
            new(new(-0.1f, -0.8f, 0f), -Vector3.UnitZ, Vector2.UnitX),
            new(new(-0.1f,  0.8f, 0f), -Vector3.UnitZ, Vector2.One),
            new(new(-1.4f,  0.8f, 0f), -Vector3.UnitZ, Vector2.UnitY),
            // Material 0 also includes a smaller occluder quad in front of Body along light ray
            new(new(-1.1f, -0.35f, -0.6f), -Vector3.UnitZ, Vector2.Zero),
            new(new(-0.4f, -0.35f, -0.6f), -Vector3.UnitZ, Vector2.UnitX),
            new(new(-0.4f,  0.35f, -0.6f), -Vector3.UnitZ, Vector2.One),
            new(new(-1.1f,  0.35f, -0.6f), -Vector3.UnitZ, Vector2.UnitY),
            // Material 1: Face quad on right [0.1, 1.4] with side-angled normal so FaceLightDirection changes lighting
            new(new(0.1f, -0.8f, 0f), Vector3.Normalize(new(0.85f, 0.2f, 0.4f)), Vector2.Zero),
            new(new(1.4f, -0.8f, 0f), Vector3.Normalize(new(0.85f, 0.2f, 0.4f)), Vector2.UnitX),
            new(new(1.4f,  0.8f, 0f), Vector3.Normalize(new(0.85f, 0.2f, 0.4f)), Vector2.One),
            new(new(0.1f,  0.8f, 0f), Vector3.Normalize(new(0.85f, 0.2f, 0.4f)), Vector2.UnitY),
        };
        uint[] indices =
        [
            0, 1, 2, 0, 2, 3,
            4, 5, 6, 4, 6, 7,
            8, 9, 10, 8, 10, 11,
        ];
        var bodyMat = new SceneMaterial("body", 0, 12, new(0.85f, 0.85f, 0.90f, 1f), Vector3.Zero, Vector3.Zero, 1f);
        var faceMat = new SceneMaterial("face", 12, 6, new(0.95f, 0.84f, 0.78f, 1f), Vector3.Zero, Vector3.Zero, 1f);
        var camera = new SceneCamera(new(0, 0, -4f), Vector3.Zero, Vector3.UnitY, 2f, 0.1f, 30f, 45f, LeftHanded: true);
        var lightDir = Vector3.Normalize(new Vector3(-0.6f, 0.2f, -0.75f));
        var snapshot = new SceneSnapshot(vertices, indices, [bodyMat, faceMat], camera: camera, lightDirection: lightDir);
        var surface = new OriginalStyleScene(
            snapshot,
            [new(new(0.5f), DoubleSided: true), new(new(0.5f), DoubleSided: true)],
            new OriginalStyleProfile(new(0.6f, 0.6f, 0.6f), Vector3.One, Matrix4x4.Identity));
        var shadow = new OriginalSelfShadowOptions(
            ModernDirectionalShadowProjection.Fit(snapshot), 512, 512, true, false,
            OriginalTextureSampling.LinearClamp, OriginalDepthComparison.LessEqual);
        var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false, SelfShadow: shadow, BackgroundColor: new Vector4(0.15f, 0.15f, 0.2f, 1f));
        return new OriginalFrameScene(
            surface,
            options: options,
            materials: [new(true, true, false), new(false, false, false)]);
    }

    internal static OriginalFrameScene FlatLitQuad(
        Vector4 diffuse,
        Vector4 backgroundColor,
        FrameAlphaOutput alphaOutput,
        OriginalSelfShadowOptions? selfShadow = null,
        bool receiveSelfShadow = false)
    {
        var vertices = new SceneVertex[]
        {
            new(new(-0.4f, -0.4f, 0f), -Vector3.UnitZ, Vector2.Zero),
            new(new(0.4f, -0.4f, 0f), -Vector3.UnitZ, Vector2.UnitX),
            new(new(0.4f, 0.4f, 0f), -Vector3.UnitZ, Vector2.One),
            new(new(-0.4f, 0.4f, 0f), -Vector3.UnitZ, Vector2.UnitY),
        };
        uint[] indices = [0, 1, 2, 0, 2, 3];
        var material = new SceneMaterial("flat", 0, 6, diffuse, Vector3.Zero, Vector3.Zero, 1f);
        var camera = new SceneCamera(new(0, 0, -2.5f), Vector3.Zero, Vector3.UnitY, 2f, 0.1f, 20f, 45f, LeftHanded: true);
        var snapshot = new SceneSnapshot(vertices, indices, [material], camera: camera, lightDirection: -Vector3.UnitZ);
        var surface = new OriginalStyleScene(
            snapshot,
            [new(new(0.5f), DoubleSided: true)],
            new OriginalStyleProfile(Vector3.One, Vector3.One, Matrix4x4.Identity));
        var options = new OriginalFrameOptions(
            OriginalAlphaBlendMode.Normal,
            WriteDepth: true,
            Transp: false,
            AlphaOutput: alphaOutput,
            SelfShadow: selfShadow,
            BackgroundColor: backgroundColor);
        return new OriginalFrameScene(surface, options: options, materials: [new(true, receiveSelfShadow, false)]);
    }
}
