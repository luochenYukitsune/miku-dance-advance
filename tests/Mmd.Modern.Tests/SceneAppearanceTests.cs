using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class SceneAppearanceTests
{
    private static readonly Vector4 VerticalToon = new(0, -1, 0, 1);
    private static readonly Vector4 HorizontalToon = new(1, 0, 0, .5f);

    [Fact]
    public void TextureSourceIsExplicitAndNeverInferredFromItsName()
    {
        var image = new SceneTexture("procedural-toon-0", 1, 1, [19, 23, 29, 255]);
        Assert.Equal(SceneTextureKind.Image, image.Kind);
        for (int i = 0; i < 10; i++)
        {
            var generated = SceneTexture.ProceduralToon(i);
            Assert.Equal(SceneTextureKind.IndexedProceduralToon, generated.Kind);
            Assert.Equal(ToonRamps.Get(i).Rgba.ToArray(), generated.Rgba.ToArray());
        }
    }

    [Fact]
    public void ModernSoftRampHasSmoothMonotonicGrayscaleEndpoints()
    {
        var texture = SceneAppearance.ModernSoftToon;
        Assert.Equal(SceneTextureKind.ModernSoftToon, texture.Kind);
        Assert.Equal(256, texture.Width);
        Assert.Equal(1, texture.Height);
        byte[] rgba = texture.Rgba.ToArray();
        Assert.Equal(140, rgba[0]); // nearest RGBA8 representation of .55
        Assert.Equal(255, rgba[^4]);
        var levels = new HashSet<byte>();
        for (int i = 0; i < 256; i++)
        {
            int offset = i * 4;
            Assert.Equal(rgba[offset], rgba[offset + 1]);
            Assert.Equal(rgba[offset], rgba[offset + 2]);
            Assert.Equal(255, rgba[offset + 3]);
            levels.Add(rgba[offset]);
            if (i > 0) Assert.InRange(rgba[offset] - rgba[offset - 4], 0, 2);
        }
        Assert.True(levels.Count > 100, "A small set of cel bands or a white ramp cannot pass as soft shading.");
        Assert.NotEqual(ToonRamps.Get(2).Rgba.ToArray(), rgba);
    }

    [Fact]
    public void MaterialMappedReturnsTheOriginalSnapshot()
    {
        var scene = MixedScene();
        Assert.Same(scene, SceneAppearance.Apply(scene, SceneAppearanceMode.MaterialMapped));
    }

    [Fact]
    public void ModernDefaultReplacesOnlyGeneratedAndMissingToonsWithOneSharedTexture()
    {
        var source = MixedScene();
        var result = SceneAppearance.Apply(source);
        Assert.NotSame(source, result);
        Assert.Equal(source.Textures.Count + 1, result.Textures.Count);
        int shared = source.Textures.Count;
        Assert.Same(SceneAppearance.ModernSoftToon, result.Textures[shared]);
        Assert.Equal(shared, result.Materials[0].ToonTexture);
        Assert.Equal(shared, result.Materials[1].ToonTexture);
        Assert.Equal(HorizontalToon, result.Materials[0].ToonUvTransform);
        Assert.Equal(HorizontalToon, result.Materials[1].ToonUvTransform);
        Assert.Same(source.Materials[2], result.Materials[2]);
        Assert.Equal(1, result.Materials[2].ToonTexture);
        Assert.Equal(VerticalToon, result.Materials[2].ToonUvTransform);
        Assert.Same(source.Materials[3], result.Materials[3]);
        Assert.Null(result.Materials[3].ToonUvTransform);
    }

    [Fact]
    public void ApplyingAppearancePreservesAllOtherInputAndOriginalTextureIdentities()
    {
        var source = MixedScene();
        var vertices = source.Vertices.ToArray();
        var indices = source.Indices.ToArray();
        var materials = source.Materials.ToArray();
        var textureBytes = source.Textures.Select(t => t.Rgba.ToArray()).ToArray();
        var result = SceneAppearance.Apply(source);
        Assert.Equal(vertices, source.Vertices.ToArray());
        Assert.Equal(indices, source.Indices.ToArray());
        Assert.Equal(materials, source.Materials.ToArray());
        Assert.Equal(vertices, result.Vertices.ToArray());
        Assert.Equal(indices, result.Indices.ToArray());
        Assert.Equal(source.World, result.World);
        Assert.Same(source.Camera, result.Camera);
        Assert.Equal(source.LightDirection, result.LightDirection);
        for (int i = 0; i < source.Textures.Count; i++)
        {
            Assert.Same(source.Textures[i], result.Textures[i]);
            Assert.Equal(textureBytes[i], result.Textures[i].Rgba.ToArray());
        }
        for (int i = 0; i < materials.Length; i++)
            Assert.Equal(materials[i], result.Materials[i] with
            {
                ToonTexture = materials[i].ToonTexture,
                ToonUvTransform = materials[i].ToonUvTransform,
            });
    }

    [Fact]
    public void RepeatedApplicationIsIdempotentAndReusesAnAlreadyPresentSharedRamp()
    {
        var first = SceneAppearance.Apply(MixedScene());
        var second = SceneAppearance.Apply(first);
        Assert.Same(first, second);
        var withRamp = new SceneSnapshot(first.Vertices, first.Indices,
            first.Materials.Select((m, i) => i == 0 ? m with { ToonTexture = -1 } : m),
            first.Textures, first.World, first.Camera, first.LightDirection);
        var reapplied = SceneAppearance.Apply(withRamp);
        Assert.Equal(first.Textures.Count, reapplied.Textures.Count);
        Assert.Equal(first.Materials[0].ToonTexture, reapplied.Materials[0].ToonTexture);
    }

    [Fact]
    public void AllImageToonsAreAnUnchangedNoOpRegardlessOfTheirNames()
    {
        var scene = MixedScene();
        var explicitOnly = new SceneSnapshot(scene.Vertices, scene.Indices,
            scene.Materials.Select(m => m with { ToonTexture = 1, ToonUvTransform = VerticalToon }), scene.Textures);
        Assert.Same(explicitOnly, SceneAppearance.Apply(explicitOnly));
    }

    [Fact]
    public void RejectsUnknownPolicyAndTextureKind()
    {
        Assert.Throws<ArgumentNullException>(() => SceneAppearance.Apply(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => SceneAppearance.Apply(MixedScene(), (SceneAppearanceMode)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneTexture("invalid", 1, 1, [0, 0, 0, 255], (SceneTextureKind)99));
    }

    private static SceneSnapshot MixedScene()
    {
        SceneVertex[] vertices = [new(new(-1, 0, 0), -Vector3.UnitZ, new(.2f, .3f)),
            new(new(1, 0, 0), -Vector3.UnitZ, Vector2.UnitX), new(new(0, 1, 0), -Vector3.UnitZ, Vector2.UnitY)];
        SceneMaterial[] materials = Enumerable.Range(0, 4).Select(i => new SceneMaterial($"m{i}", i * 3, 3,
            new(.2f, .4f, .6f, .8f), new(.1f, .2f, .3f), new(.02f, .03f, .04f), 7, 1,
            i == 0 ? 0 : i == 1 ? -1 : 1, i == 3 ? null : VerticalToon, .2f)).ToArray();
        return new(vertices, [0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2], materials,
            [SceneTexture.ProceduralToon(0), new("procedural-toon-0", 1, 2, [17, 29, 41, 255, 101, 103, 107, 255])],
            Matrix4x4.CreateRotationY(.23f) * Matrix4x4.CreateTranslation(1, 2, 3),
            SceneCamera.Default with { OrthographicHeight = 19 }, new(.1f, .9f, -.4f));
    }
}
