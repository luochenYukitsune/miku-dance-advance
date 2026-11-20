using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.DXGI;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12MultiPropMipmapCacheTests
{
    [Fact]
    public void MipmapCacheReusesSameArrayAcrossUnchangedDeformedWrappedAndAlphaMorphedFrames()
    {
        var cache = new D3D12FrameRenderer.MipmapTextureCache();
        var (frame, stylePlugin) = BuildMultiPropScene();

        bool[] expected = D3D12FrameRenderer.ComputeMipmapTextures(frame);
        bool[] first = cache.Get(frame);
        Assert.Equal(expected, first);

        // 1. Exact same frame and style -> Assert.Same reuse.
        Assert.Same(first, cache.Get(frame));

        // 2. Equivalent null vs all-"mmd" default map -> Assert.Same reuse.
        var allDefaultMap = new D3D12SurfaceStyleMap(Enumerable.Repeat(D3D12SurfaceStyleMap.DefaultStyleId, frame.Materials.Count));
        Assert.Same(first, cache.Get(frame, allDefaultMap));

        // 3. Re-wrapped vertex deformation + World change + new ReadOnlyCollection wrappers (as produced by SceneAnimationSession) -> Assert.Same reuse.
        var deformedVerts = frame.Surface.Snapshot.Vertices.ToArray();
        deformedVerts[0] = deformedVerts[0] with { Position = new(2, 3, 4) };
        var deformedSnap = frame.Surface.Snapshot.WithDeformedVertices(deformedVerts).WithWorld(Matrix4x4.CreateTranslation(1, 2, 3));
        var wrappedFrame = new OriginalFrameScene(
            new OriginalStyleScene(deformedSnap, frame.Surface.Materials.ToArray(), frame.Surface.Profile),
            frame.Outline,
            frame.OutlineOptions,
            frame.Options,
            frame.Materials.ToArray());
        Assert.Same(first, cache.Get(wrappedFrame));

        // 4. Material diffuse color/alpha morph (keeping DiffuseTexture/ToonTexture indices unchanged) -> Assert.Same reuse.
        var alphaMats = deformedSnap.Materials
            .Select((m, idx) => idx == 1 ? m with { Diffuse = new(1, 1, 1, 0.35f) } : m)
            .ToArray();
        var alphaFrame = new OriginalFrameScene(
            new OriginalStyleScene(deformedSnap.WithMaterials(alphaMats), wrappedFrame.Surface.Materials, wrappedFrame.Surface.Profile),
            wrappedFrame.Outline,
            wrappedFrame.OutlineOptions,
            wrappedFrame.Options,
            wrappedFrame.Materials);
        Assert.Same(first, cache.Get(alphaFrame));

        // 5. Switch to non-default plugin style map -> invalidates and matches ComputeMipmapTextures.
        var pluginDraw = new D3D12PluginDrawData(
            "default",
            TextureBindings: [new D3D12PluginTextureBinding(0, SceneTextureIndex: 4, MipRule: D3D12PluginTextureMipRule.GenerateMips)]);
        var pluginCharMats = Enumerable.Range(0, frame.Materials.Count)
            .Select(i => i == 0 ? new D3D12CharacterMaterialStyle(PluginDraw: pluginDraw) : new D3D12CharacterMaterialStyle())
            .ToArray();
        var styleIds = new[] { stylePlugin.StyleId, "mmd", "mmd", "mmd", "mmd" };
        var pluginMapA = new D3D12SurfaceStyleMap(styleIds, pluginCharMats, [stylePlugin]);
        var pluginMapB = new D3D12SurfaceStyleMap(styleIds, pluginCharMats, [stylePlugin]);

        bool[] expectedStyled = D3D12FrameRenderer.ComputeMipmapTextures(alphaFrame, pluginMapA);
        bool[] styledFirst = cache.Get(alphaFrame, pluginMapA);
        Assert.NotSame(first, styledFirst);
        Assert.Equal(expectedStyled, styledFirst);

        // Equivalent reconstructed D3D12SurfaceStyleMap instance (simulating CaptureResolvedInputs) -> Assert.Same reuse.
        Assert.Same(styledFirst, cache.Get(alphaFrame, pluginMapB));
    }

    [Fact]
    public void MipmapCacheInvalidatesOnStylePluginRuleTextureAndMaterialSamplingChanges()
    {
        var cache = new D3D12FrameRenderer.MipmapTextureCache();
        var (frame, stylePlugin) = BuildMultiPropScene();
        var baseMap = CreatePluginMap(frame.Materials.Count, stylePlugin, mipRule: D3D12PluginTextureMipRule.FromSource, sceneTextureIndex: 4);

        bool[] baseline = cache.Get(frame, baseMap);
        Assert.Equal(D3D12FrameRenderer.ComputeMipmapTextures(frame, baseMap), baseline);

        // 1. Plugin MipRule changed (FromSource -> GenerateMips) on unused/eligible image texture 4.
        var genMipsMap = CreatePluginMap(frame.Materials.Count, stylePlugin, mipRule: D3D12PluginTextureMipRule.GenerateMips, sceneTextureIndex: 4);
        bool[] afterGenMips = cache.Get(frame, genMipsMap);
        Assert.NotSame(baseline, afterGenMips);
        Assert.Equal(D3D12FrameRenderer.ComputeMipmapTextures(frame, genMipsMap), afterGenMips);
        Assert.True(afterGenMips[4]);

        // 2. Plugin MipRule changed (GenerateMips -> PointNoMip) on same texture index -> disables mipmaps for texture 4.
        var pointMap = CreatePluginMap(frame.Materials.Count, stylePlugin, mipRule: D3D12PluginTextureMipRule.PointNoMip, sceneTextureIndex: 4);
        bool[] afterPoint = cache.Get(frame, pointMap);
        Assert.NotSame(afterGenMips, afterPoint);
        Assert.Equal(D3D12FrameRenderer.ComputeMipmapTextures(frame, pointMap), afterPoint);
        Assert.False(afterPoint[4]);

        // 3. Plugin SceneTextureIndex changed (4 -> 1) -> disallows prop-only texture 1.
        var idxChangedMap = CreatePluginMap(frame.Materials.Count, stylePlugin, mipRule: D3D12PluginTextureMipRule.PointNoMip, sceneTextureIndex: 1);
        bool[] afterIdx = cache.Get(frame, idxChangedMap);
        Assert.NotSame(afterPoint, afterIdx);
        Assert.Equal(D3D12FrameRenderer.ComputeMipmapTextures(frame, idxChangedMap), afterIdx);
        Assert.False(afterIdx[1]);

        // 4. Material UseMipmaps toggled on prop material 1 (false -> true -> false).
        var toggledFlags = frame.Materials.Select((f, i) => i == 1 ? f with { UseMipmaps = false } : f).ToArray();
        var disabledPropFrame = new OriginalFrameScene(frame.Surface, frame.Outline, frame.OutlineOptions, frame.Options, toggledFlags);
        bool[] afterFlag = cache.Get(disabledPropFrame, baseMap);
        Assert.Equal(D3D12FrameRenderer.ComputeMipmapTextures(disabledPropFrame, baseMap), afterFlag);
        Assert.False(afterFlag[1]);

        // 5. Same-count texture instance replacement at index 1 -> invalidates and recomputes.
        var oldSnap = frame.Surface.Snapshot;
        var replacedTextures = oldSnap.Textures.ToArray();
        replacedTextures[1] = new SceneTexture("prop-replaced", 4, 4, new byte[4 * 4 * 4]);
        var replacedFrame = new OriginalFrameScene(
            new OriginalStyleScene(
                new SceneSnapshot(oldSnap.Vertices, oldSnap.Indices, oldSnap.Materials, replacedTextures),
                frame.Surface.Materials,
                frame.Surface.Profile),
            options: frame.Options,
            materials: frame.Materials);
        bool[] afterTexReplace = cache.Get(replacedFrame, baseMap);
        Assert.NotSame(afterFlag, afterTexReplace);
        Assert.Equal(D3D12FrameRenderer.ComputeMipmapTextures(replacedFrame, baseMap), afterTexReplace);

        // 6. Empty texture scene transition and back -> returns empty and does not keep stale state.
        var emptySnap = SceneSnapshot.CreateEmpty();
        var emptyFrame = new OriginalFrameScene(new OriginalStyleScene(emptySnap, []));
        bool[] emptyResult = cache.Get(emptyFrame);
        Assert.Empty(emptyResult);
        Assert.Same(emptyResult, cache.Get(emptyFrame));

        bool[] restored = cache.Get(frame, genMipsMap);
        Assert.Equal(D3D12FrameRenderer.ComputeMipmapTextures(frame, genMipsMap), restored);

        cache.Clear();
        bool[] afterClear = cache.Get(frame, genMipsMap);
        Assert.NotSame(restored, afterClear);
        Assert.Equal(restored, afterClear);
    }

    [Fact]
    public void MipmapCacheHandlesMultiPropSharedAndProceduralTexturesIdenticallyToReference()
    {
        var cache = new D3D12FrameRenderer.MipmapTextureCache();
        var texPropA = new SceneTexture("prop-a", 4, 4, new byte[4 * 4 * 4]);
        var texSharedCharProp = new SceneTexture("shared-char-prop", 4, 4, new byte[4 * 4 * 4]);
        var texPropB = new SceneTexture("prop-b", 4, 4, new byte[4 * 4 * 4]);
        var texProcToon = SceneTexture.ProceduralToon(1);

        SceneVertex[] verts =
        [
            new(new(-1, 0, 0), Vector3.UnitZ, Vector2.Zero),
            new(new(1, 0, 0), Vector3.UnitZ, Vector2.UnitX),
            new(new(0, 1, 0), Vector3.UnitZ, Vector2.UnitY),
        ];
        uint[] idx = [0, 1, 2];
        var materials = new[]
        {
            new SceneMaterial("propA", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 0, ToonTexture: 3),
            new SceneMaterial("char", 3, 0, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 1),
            new SceneMaterial("propShared", 3, 0, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 1),
            new SceneMaterial("propB", 3, 0, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 2),
        };
        var bindings = Enumerable.Repeat(new OriginalStyleMaterialBinding(Vector3.One), 4).ToArray();
        var flags = new[]
        {
            new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = true },
            new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = false },
            new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = true },
            new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = true },
        };
        var frame1 = new OriginalFrameScene(
            new OriginalStyleScene(new SceneSnapshot(verts, idx, materials, [texPropA, texSharedCharProp, texPropB, texProcToon]), bindings),
            materials: flags);

        bool[] res1 = cache.Get(frame1);
        Assert.Equal([true, false, true, false], res1);
        Assert.Equal(D3D12FrameRenderer.ComputeMipmapTextures(frame1), res1);

        // Reorder textures [texPropB, texSharedCharProp, texPropA, texProcToon] with updated material indices.
        var reorderedMaterials = new[]
        {
            materials[0] with { DiffuseTexture = 2 },
            materials[1],
            materials[2],
            materials[3] with { DiffuseTexture = 0 },
        };
        var frame2 = new OriginalFrameScene(
            new OriginalStyleScene(new SceneSnapshot(verts, idx, reorderedMaterials, [texPropB, texSharedCharProp, texPropA, texProcToon]), bindings),
            materials: flags);

        bool[] res2 = cache.Get(frame2);
        Assert.NotSame(res1, res2);
        Assert.Equal(D3D12FrameRenderer.ComputeMipmapTextures(frame2), res2);
        Assert.Equal([true, false, true, false], res2);

        // Change SphereTexture slot on propA to reference texPropB -> disallows texPropB.
        var sphereBindings = bindings.ToArray();
        sphereBindings[0] = new OriginalStyleMaterialBinding(Vector3.One, SphereTexture: 0, SphereMode: OriginalSphereMode.Multiply);
        var frame3 = new OriginalFrameScene(
            new OriginalStyleScene(frame2.Surface.Snapshot, sphereBindings),
            materials: flags);
        bool[] res3 = cache.Get(frame3);
        Assert.NotSame(res2, res3);
        Assert.Equal(D3D12FrameRenderer.ComputeMipmapTextures(frame3), res3);
        Assert.Equal([false, false, true, false], res3);
    }

    [Fact]
    public void TargetRecorderReusesSteadyStateListWithZeroAllocationsAndPreservesOrderAndImmutability()
    {
        var recorder = new D3D12FrameRenderer.PostEffectTargetRecorder();

        void RecordSequence(bool withSmaa, bool withFsrRcas, bool withDiffusion, int width, int height, Format outputFmt)
        {
            recorder.Begin();
            recorder.Add("$output", width, height, outputFmt);
            recorder.Add("$depth", width, height, Format.D32_Float);
            if (withSmaa)
            {
                recorder.Add("$smaa-edges", width, height, Format.R8G8_UNorm);
                recorder.Add("$smaa-weights", width, height, Format.R8G8B8A8_UNorm);
                recorder.Add("$smaa-output", width, height, Format.R8G8B8A8_UNorm);
            }
            if (withFsrRcas)
            {
                recorder.Add("$fsr1-easu", width * 2, height * 2, Format.R8G8B8A8_UNorm);
                recorder.Add("$fsr1-rcas", width * 2, height * 2, Format.R8G8B8A8_UNorm);
            }
            if (withDiffusion)
            {
                recorder.Add("$diffusion-h", width, height, Format.R16G16B16A16_Float);
                recorder.Add("$diffusion-y", width, height, Format.R16G16B16A16_Float);
            }
        }

        RecordSequence(withSmaa: true, withFsrRcas: true, withDiffusion: true, 1280, 720, Format.R8G8B8A8_UNorm);
        var published1 = recorder.Commit();
        Assert.Equal(9, published1.Count);
        Assert.Equal(
            ["$output", "$depth", "$smaa-edges", "$smaa-weights", "$smaa-output", "$fsr1-easu", "$fsr1-rcas", "$diffusion-h", "$diffusion-y"],
            published1.Select(t => t.Name).ToArray());
        Assert.Equal("R16G16B16A16_Float", published1[7].Format);
        Assert.Equal("D32_Float", published1[1].Format);

        // Warm steady-state pass, then verify exact reference reuse with zero heap allocations.
        RecordSequence(withSmaa: true, withFsrRcas: true, withDiffusion: true, 1280, 720, Format.R8G8B8A8_UNorm);
        var warm = recorder.Commit();
        Assert.Same(published1, warm);

        long allocStart = GC.GetAllocatedBytesForCurrentThread();
        RecordSequence(withSmaa: true, withFsrRcas: true, withDiffusion: true, 1280, 720, Format.R8G8B8A8_UNorm);
        var steady = recorder.Commit();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocStart;
        Assert.Same(published1, steady);
        Assert.Equal(0, allocated);

        // Resize / pass toggle / format change invalidates cleanly while keeping published1 immutable.
        RecordSequence(withSmaa: false, withFsrRcas: false, withDiffusion: true, 1920, 1080, Format.R16G16B16A16_Float);
        var resized = recorder.Commit();
        Assert.NotSame(published1, resized);
        Assert.Equal(4, resized.Count);
        Assert.Equal((1920, 1080, "R16G16B16A16_Float"), (resized[0].Width, resized[0].Height, resized[0].Format));
        Assert.Equal(9, published1.Count);
        Assert.Equal(1280, published1[0].Width);

        // Same-length change (e.g. target name or format change) must also invalidate.
        RecordSequence(withSmaa: false, withFsrRcas: false, withDiffusion: true, 1920, 1080, Format.R8G8B8A8_UNorm);
        var formatChanged = recorder.Commit();
        Assert.NotSame(resized, formatChanged);
        Assert.Equal("R8G8B8A8_UNorm", formatChanged[0].Format);

        // ResetStaged discards uncommitted staged entries without affecting prior committed records.
        recorder.Begin();
        recorder.Add("$aborted", 64, 64, Format.R8_UNorm);
        recorder.ResetStaged();
        var emptyAfterReset = recorder.Commit();
        Assert.Empty(emptyAfterReset);
        Assert.Equal(4, formatChanged.Count);

        recorder.Clear();
    }

    [Theory]
    [InlineData(Format.R8G8B8A8_UNorm)]
    [InlineData(Format.R8G8B8A8_UNorm_SRgb)]
    [InlineData(Format.R16G16B16A16_Float)]
    [InlineData(Format.R16G16B16A16_UNorm)]
    [InlineData(Format.R32G32B32A32_Float)]
    [InlineData(Format.R11G11B10_Float)]
    [InlineData(Format.R10G10B10A2_UNorm)]
    [InlineData(Format.R16G16_Float)]
    [InlineData(Format.R8G8_UNorm)]
    [InlineData(Format.R8_UNorm)]
    [InlineData(Format.D32_Float)]
    [InlineData(Format.D24_UNorm_S8_UInt)]
    [InlineData(Format.D16_UNorm)]
    [InlineData(Format.B8G8R8A8_UNorm)]
    public void TargetRecorderFormatNamesMatchEnumToStringExactly(Format format)
    {
        Assert.Equal(format.ToString(), D3D12FrameRenderer.PostEffectTargetRecorder.FormatName(format));
    }

    private static (OriginalFrameScene Frame, D3D12PluginStyleRegistration Plugin) BuildMultiPropScene()
    {
        var texChar = new SceneTexture("char-diff", 4, 4, new byte[4 * 4 * 4]);
        var texProp = new SceneTexture("prop-diff", 4, 4, new byte[4 * 4 * 4]);
        var texToon = new SceneTexture("toon", 4, 4, new byte[4 * 4 * 4]);
        var texSphere = new SceneTexture("sphere", 4, 4, new byte[4 * 4 * 4]);
        var texPluginOnly = new SceneTexture("plugin-extra", 4, 4, new byte[4 * 4 * 4]);
        var texProcToon = SceneTexture.ProceduralToon(1);

        SceneVertex[] verts =
        [
            new(new(-1, 0, 0), Vector3.UnitZ, Vector2.Zero),
            new(new(1, 0, 0), Vector3.UnitZ, Vector2.UnitX),
            new(new(0, 1, 0), Vector3.UnitZ, Vector2.UnitY),
        ];
        uint[] idx = [0, 1, 2];

        var materials = new[]
        {
            new SceneMaterial("char-mat", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 0, ToonTexture: 2),
            new SceneMaterial("prop-mat-mip", 3, 0, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 1),
            new SceneMaterial("prop-mat-sphere", 3, 0, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 3),
            new SceneMaterial("prop-mat-proc", 3, 0, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 5),
            new SceneMaterial("char-mat-2", 3, 0, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: -1),
        };
        var bindings = new[]
        {
            new OriginalStyleMaterialBinding(Vector3.One),
            new OriginalStyleMaterialBinding(Vector3.One),
            new OriginalStyleMaterialBinding(Vector3.One, SphereTexture: 3, SphereMode: OriginalSphereMode.Add),
            new OriginalStyleMaterialBinding(Vector3.One),
            new OriginalStyleMaterialBinding(Vector3.One),
        };
        var flags = new[]
        {
            new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = false },
            new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = true },
            new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = true },
            new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = true },
            new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = false },
        };
        var snapshot = new SceneSnapshot(
            verts, idx, materials,
            [texChar, texProp, texToon, texSphere, texPluginOnly, texProcToon]);
        var frame = new OriginalFrameScene(
            new OriginalStyleScene(snapshot, bindings),
            options: new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false),
            materials: flags);

        var plugin = new D3D12PluginStyleRegistration(
            "custom-style",
            "Custom Style",
            ShaderModules: new Dictionary<string, D3D12PluginShaderModule>(StringComparer.Ordinal)
            {
                ["default"] = new D3D12PluginShaderModule("default", null, D3D12StyleShaderHookMask.AllowStylizedShadingWrapper),
            });

        return (frame, plugin);
    }

    private static D3D12SurfaceStyleMap CreatePluginMap(
        int materialCount,
        D3D12PluginStyleRegistration plugin,
        D3D12PluginTextureMipRule mipRule,
        int sceneTextureIndex)
    {
        var styleIds = Enumerable.Range(0, materialCount)
            .Select(i => i == 0 ? plugin.StyleId : D3D12SurfaceStyleMap.DefaultStyleId)
            .ToArray();
        var draw = new D3D12PluginDrawData(
            "default",
            TextureBindings: [new D3D12PluginTextureBinding(0, SceneTextureIndex: sceneTextureIndex, MipRule: mipRule)]);
        var charMats = Enumerable.Range(0, materialCount)
            .Select(i => i == 0 ? new D3D12CharacterMaterialStyle(PluginDraw: draw) : new D3D12CharacterMaterialStyle())
            .ToArray();
        return new D3D12SurfaceStyleMap(styleIds, charMats, [plugin]);
    }
}
