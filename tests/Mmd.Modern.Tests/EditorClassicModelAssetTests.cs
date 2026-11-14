using System.Numerics;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorClassicModelAssetTests
{
    [Fact]
    public void ExplicitDefaultToonSurvivesCallerDecorationAndAnimatedMaterialMorphs()
    {
        var morph = new PmxMorph { Name = "fade", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Material,
            MaterialOffsets = [new(-1, PmxMaterialMorphOperation.Multiply, new(1, 1, 1, 0), Vector3.One,
                1, Vector3.One, Vector4.One, 1, Vector4.One, Vector4.One, Vector4.One)] };
        var model = MorphApplicationTests.Model([morph]);
        string path = Path.Combine(Path.GetTempPath(), "classic-toon-model.pmx");
        var modern = EditorProjectWorkspace.CreateModelAsset(model, path, TestContext.Current.CancellationToken);
        var image = new SceneTexture("explicit original slot 0", 1, 3,
            [255, 255, 255, 255, 128, 128, 128, 255, 64, 64, 64, 255]);
        Assert.Throws<NotSupportedException>(() => EditorClassicModelAssets.RetainToonTextures(modern, path, TestContext.Current.CancellationToken));
        var retained = EditorClassicModelAssets.RetainToonTextures(modern, path,
            TestContext.Current.CancellationToken, loadDefaultToon: () => image);
        Assert.Same(model, retained.Model);
        Assert.Same(modern.Source.Outline, retained.Source.Outline);
        Assert.Equal(Vector3.One, retained.Source.Surface.Materials[0].ToonShadowColor);
        Assert.Equal(modern.Source.Materials, retained.Source.Materials);
        Assert.Equal(modern.Source.Surface.Snapshot.Vertices.ToArray(), retained.Source.Surface.Snapshot.Vertices.ToArray());
        Assert.Same(retained, EditorClassicModelAssets.RetainToonTextures(retained, path,
            TestContext.Current.CancellationToken,
            loadDefaultToon: () => throw new InvalidOperationException("A retained image must not be reloaded.")));
        using var controller = new CharacterAnimationController(model,
            new() { MorphKeyframes = [MorphApplicationTests.Key("fade", 0, 0), MorphApplicationTests.Key("fade", 10, 1)] },
            retained.Source.Surface, retained.Source.Outline!);
        var sample = controller.SampleAt(5.5).Surface.Snapshot;
        Assert.Equal(.45f, sample.Materials[0].Diffuse.W, 5);
        Assert.Same(image, sample.Textures[sample.Materials[0].ToonTexture]);
        Assert.Equal(-1, modern.Source.Surface.Snapshot.Materials[0].ToonTexture);
    }
}
