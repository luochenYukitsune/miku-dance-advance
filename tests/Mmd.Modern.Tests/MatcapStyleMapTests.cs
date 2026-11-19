using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class MatcapStyleMapTests
{
    private static StyleMaterialBindingData Binding(
        int normalSlot = 0,
        int pSlot = 1,
        int matcapSlot = 2,
        string encoding = T8dTestHelpers.EncodingSrgbRgb,
        int uvChannel = 0,
        Vector3? baseColor = null,
        Vector3? highlightColor = null,
        float normalStrength = 2f,
        float rotationRadians = 0f,
        float highlightCoefficient = 0f,
        float shadowHardness = .5f,
        Vector3? lightControlDirection = null,
        float baselineStrength = 1f,
        bool coupleEndfieldPalette = false)
        => T8dTestHelpers.EndfieldMatcap(normalSlot, pSlot, matcapSlot, encoding, uvChannel,
            baseColor ?? new(.2f), highlightColor ?? Vector3.Zero, normalStrength, rotationRadians,
            highlightCoefficient, shadowHardness, lightControlDirection, baselineStrength, coupleEndfieldPalette);

    private static SceneTexture Texture(byte r = 120, byte g = 70, byte b = 40, byte a = 230)
        => new("authored", 1, 1, [r, g, b, a]);
    private static OriginalFrameScene Frame(SceneTexture[]? textures = null, Matrix4x4? world = null)
        => D3D12EndfieldHairBindingTests.Frame(textures: textures ?? [Texture(), Texture(), Texture()], world: world);
    private static D3D12SurfaceStyleMap Map(StyleMaterialBindingData? binding,
        string family = "endfield", OriginalFrameScene? frame = null, D3D12CharacterMaterialRole role = D3D12CharacterMaterialRole.Body,
        params StyleMaterialBindingData[] extra)
        => T8dTestHelpers.CreateDirectSurfaceStyleMap(frame ?? Frame(), [family], [new(role)],
            [binding is null && extra.Length == 0 ? null : binding is null ? extra : [binding, .. extra]]);

    [Fact]
    public void FullPackedNormalBindingRequiresItsExplicitFamilyTexturesAndEffectiveUv()
    {
        var frame = Frame(); var binding = Binding(); var map = Map(binding, frame: frame);
        map.Validate(frame);
        Assert.Equal("matcap", map.CharacterMaterials[0].PluginDraw?.ShaderModuleId);
        Assert.Contains(map.CharacterMaterials[0].PluginDraw!.BufferBindings!, static b => b.Kind == D3D12PluginBufferKind.MikkTangentCorners);
        Assert.Equal(frame.Surface.Snapshot.Materials[0].IndexCount, D3D12TangentCornerGeometry.Build(frame.Surface.Snapshot, 0, 0).Corners.Length);
        Assert.Throws<NotSupportedException>(() => new D3D12SurfaceStyleMap(["mmd"], [map.CharacterMaterials[0]], TestStylePlugins.AllPresetCatalog).Validate(frame));
        foreach (var family in new[] { "genshin", "star-rail", "wuthering-waves" })
            Assert.NotEqual("matcap", Map(binding, family, frame).CharacterMaterials[0].PluginDraw?.ShaderModuleId);
        foreach (var bad in new[] { Binding(normalSlot: 3), Binding(pSlot: 3), Binding(matcapSlot: 3) })
            Assert.Throws<ArgumentOutOfRangeException>(() => Map(bad, frame: frame).Validate(frame));
        Assert.Throws<ArgumentException>(() => Map(Binding(uvChannel: 1), frame: frame).Validate(frame));
        Assert.Null(Map(null, frame: frame).CharacterMaterials[0].PluginDraw);
        Assert.Equal("default", Map(null, frame: frame).ResolveShaderModule(0)?.ModuleId);
    }

    [Fact]
    public void CoupledPaletteRequiresExplicitSharedColorsAndExactlyOneDynamicRadialStrength()
    {
        var baseColor = new Vector3(.2f);
        var matcap = Binding(baseColor: baseColor, coupleEndfieldPalette: true);
        var hair = T8dTestHelpers.EndfieldHair(1, baseColor);
        var rim = T8dTestHelpers.EndfieldRim(baseColor, new(.3f), edgeStrength: .4f);
        Assert.True(T8dTestHelpers.ValidateEndfield([hair, rim, matcap], StyleMaterialRole.Hair).IsValid);
        foreach (var bad in new StyleMaterialBindingData[][]
        {
            [T8dTestHelpers.EndfieldHair(1, new(.5f)), rim, matcap],
            [hair, T8dTestHelpers.EndfieldRim(new(.5f), new(.3f), edgeStrength: .4f), matcap],
            [hair, rim, Binding(baseColor: baseColor, baselineStrength: 2, coupleEndfieldPalette: true)],
            [hair, T8dTestHelpers.EndfieldRim(baseColor, new(.3f), edgeStrength: .4f, radialStrength: .2f),
                Binding(baseColor: baseColor, baselineStrength: 1.2f, coupleEndfieldPalette: true)],
        })
            Assert.False(T8dTestHelpers.ValidateEndfield(bad, StyleMaterialRole.Hair).IsValid);
        Assert.True(T8dTestHelpers.ValidateEndfield([
            T8dTestHelpers.EndfieldRim(baseColor, new(.3f), edgeStrength: .4f, radialStrength: .2f),
            Binding(baseColor: baseColor, baselineStrength: 1.2f, coupleEndfieldPalette: true)], StyleMaterialRole.Hair).IsValid);
        foreach (var role in new[] { StyleMaterialRole.Face, StyleMaterialRole.Eye })
            Assert.False(T8dTestHelpers.ValidateEndfield(Binding(), role).IsValid);
    }

    [Fact]
    public void InvalidMikkDataInvalidNormalsAndNonInvertibleWorldFailBeforeRecording()
    {
        var frame = Frame(); var binding = Binding(); var validMap = Map(binding, frame: frame);
        validMap.Validate(frame);
        var snapshot = frame.Surface.Snapshot;

        var badDataDraw = validMap.CharacterMaterials[0].PluginDraw! with
        {
            BufferBindings = [new D3D12PluginBufferBinding(0, D3D12PluginBufferKind.MikkTangentCorners, "uv0", Data: new byte[63])]
        };
        Assert.Throws<ArgumentException>(() => badDataDraw.Validate());

        var normalVertices = snapshot.Vertices.ToArray();
        normalVertices[0] = normalVertices[0] with { Normal = Vector3.Zero };
        var normalFrame = new OriginalFrameScene(new(new SceneSnapshot(normalVertices, snapshot.Indices, snapshot.Materials, snapshot.Textures, snapshot.World, snapshot.Camera),
            frame.Surface.Materials, frame.Surface.Profile), options: frame.Options);
        Assert.Throws<ArgumentException>(() => validMap.Validate(normalFrame));

        Assert.Throws<ArgumentException>(() =>
        {
            var singularWorld = Frame(world: Matrix4x4.CreateScale(0, 1, 1));
            validMap.Validate(singularWorld);
        });

        var changed = snapshot.Vertices.ToArray(); changed[0] = changed[0] with { Uv = changed[0].Uv + new Vector2(.01f, 0) };
        var changedUvFrame = new OriginalFrameScene(new(new SceneSnapshot(changed, snapshot.Indices, snapshot.Materials, snapshot.Textures, snapshot.World, snapshot.Camera),
            frame.Surface.Materials, frame.Surface.Profile), options: frame.Options);
        Assert.False(D3D12SurfaceStyleMap.Equivalent(validMap.CaptureResolvedInputs(frame), validMap.CaptureResolvedInputs(changedUvFrame)));
    }

    [Fact]
    public void DetachedHistoryCapturesBoundTexturesAndRebasesActualSlots()
    {
        var frame = Frame(); var binding = Binding(); var map = Map(binding, frame: frame);
        var first = map.CaptureResolvedInputs(frame);
        Assert.NotSame(map, first);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame())));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(new(new(
            TestStylePlugins.WithTextures(frame.Surface.Snapshot, [
                new("renamed 0", 1, 1, Texture().Rgba),
                new("renamed 1", 1, 1, Texture().Rgba),
                new("renamed 2", 1, 1, Texture().Rgba)]),
            frame.Surface.Materials, frame.Surface.Profile), options: frame.Options))));
        foreach (var changed in new[] { new[] { Texture(r: 1), Texture(), Texture() }, new[] { Texture(g: 1), Texture(), Texture() },
            new[] { Texture(), Texture(r: 1), Texture() }, new[] { Texture(), Texture(a: 1), Texture() },
            new[] { Texture(), Texture(), Texture(r: 1) }, new[] { Texture(), Texture(), Texture(g: 1) },
            new[] { Texture(), Texture(), Texture(b: 1) }, new[] { Texture(), Texture(), Texture(a: 1) } })
            Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(changed))));
        var rebased = Map(Binding(normalSlot: 2, pSlot: 0, matcapSlot: 1), frame: frame)
            .CaptureResolvedInputs(Frame());
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, rebased));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame())));
    }

    [Fact]
    public void HistoryUsesConsumedAppearanceAndIgnoresPureWorldTranslationOrDescriptorIdentity()
    {
        var frame = Frame(); var binding = Binding();
        var first = Map(binding, frame: frame).CaptureResolvedInputs(frame);
        var copy = Map(Binding(), frame: frame).CaptureResolvedInputs(frame);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, copy));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, Map(binding, frame: frame).CaptureResolvedInputs(Frame(world: Matrix4x4.CreateTranslation(1, 2, 3)))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, Map(binding, frame: frame).CaptureResolvedInputs(Frame(world: Matrix4x4.CreateRotationZ(.3f)))));
        foreach (var changed in new[] { Binding(normalStrength: 1), Binding(rotationRadians: 1),
            Binding(highlightColor: Vector3.One), Binding(highlightCoefficient: 1),
            Binding(shadowHardness: .8f), Binding(baselineStrength: 2),
            Binding(encoding: T8dTestHelpers.EncodingLinearRgb), Binding(lightControlDirection: Vector3.UnitX) })
            Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(binding), Map(changed)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(binding), Map(null)));
    }
}
