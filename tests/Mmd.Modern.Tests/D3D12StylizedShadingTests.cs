using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>风格化明暗：四个画风在同一个普通材质上要看得出差别，MMD 保持原样，
/// 投影阴影不进入风格化颜色，材质类型按名字猜测且手动标记优先。</summary>
public sealed class D3D12StylizedShadingTests
{
    private static readonly string[] Presets = ["mmd", "genshin", "star-rail", "wuthering-waves", "endfield"];

    [Theory]
    [InlineData("顏", D3D12CharacterMaterialRole.Face)]
    [InlineData("颜2", D3D12CharacterMaterialRole.Face)]
    [InlineData("眉睫", D3D12CharacterMaterialRole.Face)]
    [InlineData("白目", D3D12CharacterMaterialRole.Eye)]
    [InlineData("Eye_L", D3D12CharacterMaterialRole.Eye)]
    [InlineData("髮飾环", D3D12CharacterMaterialRole.Hair)]
    [InlineData("前髪", D3D12CharacterMaterialRole.Hair)]
    [InlineData("齿", D3D12CharacterMaterialRole.Face)]
    [InlineData("biaoq1", D3D12CharacterMaterialRole.Face)]
    [InlineData("衣服", D3D12CharacterMaterialRole.Body)]
    [InlineData("肌", D3D12CharacterMaterialRole.Body)]
    [InlineData("", D3D12CharacterMaterialRole.Body)]
    public void MaterialNamesGuessRoles(string name, D3D12CharacterMaterialRole expected)
        => Assert.Equal(expected, D3D12CharacterMaterialRoleGuess.Guess(name));

    [Fact]
    public void CatalogGivesEachFamilyDistinctParametersAndMmdNone()
    {
        Assert.Null(TestStylePlugins.AllPresetCatalog.Find("mmd").DefaultStylizedShading);
        string[] families = ["genshin", "star-rail", "wuthering-waves", "endfield"];
        var bodies = families.Select(family => D3D12StylizedShadingCatalog.ResolveForRole(
            TestStylePlugins.AllPresetCatalog.Find(family).DefaultStylizedShading, D3D12CharacterMaterialRole.Body)!).ToArray();
        foreach (var body in bodies) body.Validate();
        Assert.Equal(bodies.Length, bodies.Distinct().Count());
        foreach (string family in families)
        {
            var baseShading = TestStylePlugins.AllPresetCatalog.Find(family).DefaultStylizedShading;
            var face = D3D12StylizedShadingCatalog.ResolveForRole(baseShading, D3D12CharacterMaterialRole.Face)!;
            var eye = D3D12StylizedShadingCatalog.ResolveForRole(baseShading, D3D12CharacterMaterialRole.Eye)!;
            Assert.True(face.FaceFlatten > .5f);
            Assert.Equal(0, face.RimStrength);
            Assert.Equal(0, eye.ShadowStrength);
        }
    }

    [Fact]
    public void ExplicitRolesWinOverNameGuessesAndGuessingCanBeDisabled()
    {
        var frame = Frame("顏");
        var binding = Binding("genshin");
        var presets = TestStylePlugins.AllPresetCatalog;
        var views = TestStylePlugins.AllViewCatalog;
        var guessed = D3D12ViewRenderingStyles.Resolve(frame, presets, [binding], styleCatalog: views);
        Assert.Equal(D3D12CharacterMaterialRole.Face, guessed.SurfaceStyles.StylizedRoles![0]);
        Assert.True(guessed.SurfaceStyles.EffectiveStylizedShading(0)!.FaceFlatten > 0);
        var explicitBody = D3D12ViewRenderingStyles.Resolve(frame, presets, [binding],
            [new D3D12ViewCharacterMaterial(binding.ObjectId, 0, D3D12CharacterMaterialRole.Body)], styleCatalog: views);
        Assert.Equal(D3D12CharacterMaterialRole.Body, explicitBody.SurfaceStyles.StylizedRoles![0]);
        var disabled = D3D12ViewRenderingStyles.Resolve(frame, presets, [binding], guessMaterialRoles: false, styleCatalog: views);
        Assert.Equal(D3D12CharacterMaterialRole.Body, disabled.SurfaceStyles.StylizedRoles![0]);
        var mmd = D3D12ViewRenderingStyles.Resolve(frame, presets, [Binding("mmd")], styleCatalog: views);
        Assert.Null(mmd.SurfaceStyles.EffectiveStylizedShading(0));
    }

    [Fact, Trait("Category", "Gpu")]
    public void FamiliesLookDistinctOnTheSameMaterialWhileMmdKeepsItsPixels()
    {
        const int width = 96, height = 96;
        var frame = Frame("衣服");
        using var session = new D3D12FrameSession();
        Assert.False(session.IsWarp);
        var raw = session.Capture(frame, width, height).Rgba;
        var presets = TestStylePlugins.AllPresetCatalog;
        var views = TestStylePlugins.AllViewCatalog;
        var mmd = D3D12ViewRenderingStyles.Resolve(frame, presets, [Binding("mmd")], styleCatalog: views);
        Assert.Equal(raw, session.Capture(mmd.Scene, width, height, surfaceStyles: mmd.SurfaceStyles).Rgba);
        var captures = Presets.Skip(1).Select(preset =>
        {
            var styled = D3D12ViewRenderingStyles.Resolve(frame, presets, [Binding(preset)], styleCatalog: views);
            return session.Capture(styled.Scene, width, height, surfaceStyles: styled.SurfaceStyles).Rgba;
        }).ToArray();
        // 任意两个画风之间，以及每个画风与 MMD 之间，平均每像素颜色差都要明显（不是只差一两个字节）。
        var report = new System.Text.StringBuilder();
        for (int a = 0; a < captures.Length; a++)
            for (int b = a + 1; b < captures.Length; b++)
                report.Append($"{Presets[a + 1]}/{Presets[b + 1]}={MeanDifference(captures[a], captures[b]):F2} ");
        for (int a = 0; a < captures.Length; a++)
        {
            double distanceFromMmd = MeanDifference(raw, captures[a]);
            Assert.True(distanceFromMmd > 6, $"{Presets[a + 1]}/mmd={distanceFromMmd:F2}, {report}");
            for (int b = a + 1; b < captures.Length; b++)
                Assert.True(MeanDifference(captures[a], captures[b]) > 3, $"{Presets[a + 1]} vs {Presets[b + 1]}: {report}");
        }
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void PaintedHighlightsKeepTheirHueAndTextureDetailAcrossTheToonBands(string family)
    {
        const int width = 160, height = 96, columns = 6;
        byte[] texels =
        [
            237, 188, 126, 255, 247, 196, 131, 255, // Two warm highlights of the same hue.
            116, 193, 239, 255, 121, 200, 248, 255, // Two cool highlights of the same hue.
            170, 120, 85, 128, 240, 210, 160, 0,
        ];
        var vertices = new List<SceneVertex>();
        var indices = new List<uint>();
        void Strip(float centerY, Vector3 normal)
        {
            uint first = (uint)vertices.Count;
            vertices.AddRange([
                new(new(-1.5f, centerY - .2f, 0), normal, new(0, 1)),
                new(new(1.5f, centerY - .2f, 0), normal, new(1, 1)),
                new(new(1.5f, centerY + .2f, 0), normal, new(1, 0)),
                new(new(-1.5f, centerY + .2f, 0), normal, new(0, 0)),
            ]);
            indices.AddRange([first, first + 2, first + 1, first, first + 3, first + 2]);
        }
        // Grazing observation of the fully lit strip exercises the rim's bright-end headroom.
        Strip(.65f, Vector3.UnitX);
        Strip(0f, -Vector3.UnitX);
        Strip(-.65f, Vector3.UnitX);
        var camera = new SceneCamera(new(0, 0, -6), Vector3.Zero, Vector3.UnitY, 2.4f, .1f, 20);
        var mesh = new SceneSnapshot(vertices.ToArray(), indices.ToArray(), [
            new("衣服", 0, 12, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 0),
            new("Eye_L", 12, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 0)],
            [new("paint", columns, 1, texels)], camera: camera, lightDirection: Vector3.UnitX);
        var binding = new OriginalStyleMaterialBinding(Vector3.One, DoubleSided: true);
        var frame = new OriginalFrameScene(new(mesh, [binding, binding],
            new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: D3D12OriginalFrameTests.Options() with { BackgroundColor = Vector4.Zero });
        var styled = D3D12ViewRenderingStyles.Resolve(frame, TestStylePlugins.AllPresetCatalog,
            [new SceneRenderingStyleBinding(ObjectId, new(family), ModelRole.Character, 0, 2, true)],
            styleCatalog: TestStylePlugins.AllViewCatalog);

        using var session = new D3D12FrameSession();
        var reference = session.Capture(frame, width, height);
        var output = session.Capture(styled.Scene, width, height, surfaceStyles: styled.SurfaceStyles);
        byte[] Pixel(float row, int col)
        {
            var clip = Vector4.Transform(new Vector4(-1.5f + (col + .5f) * 3 / columns, row, 0, 1),
                camera.ViewProjection(width, height));
            var x = (int)((.5f + .5f * clip.X / clip.W) * width);
            var y = (int)((.5f - .5f * clip.Y / clip.W) * height);
            return output.Rgba.AsSpan((y * width + x) * 4, 4).ToArray();
        }
        for (int offset = 3; offset < reference.Rgba.Length; offset += 4)
            Assert.Equal(reference.Rgba[offset], output.Rgba[offset]);
        Assert.Equal(128, Pixel(.65f, 4)[3]);
        Assert.Equal(0, Pixel(.65f, 5)[3]);

        var grade = TestStylePlugins.GetStyle(family).DefaultStylizedShading!;
        for (int col = 0; col < 4; col++)
        {
            int peak = col < 2 ? 0 : 2, low = 2 - peak;
            var source = texels.AsSpan(col * 4, 3);
            var lit = Pixel(.65f, col);
            var shadow = Pixel(0, col);
            float sourceChroma = source[peak] - source[low], litChroma = lit[peak] - lit[low];
            Assert.True(litChroma > 0 && lit[3] == 255);
            // A hue-preserving saturation/value adjustment keeps the green channel at
            // the same relative position between the red and blue endpoints (8-bit output).
            Assert.InRange(MathF.Abs((source[1] - source[low]) / sourceChroma - (lit[1] - lit[low]) / litChroma), 0, .02f);
            Assert.True(litChroma / lit[peak] >= sourceChroma / source[peak] - .01f);
            Assert.True(shadow[peak] < lit[peak] && shadow[peak] > shadow[low]);
            if (col % 2 == 0)
            {
                var next = Pixel(.65f, col + 1);
                Assert.True(next[peak] >= lit[peak] + 2 && next[peak] < 255,
                    $"{family}: painted highlights {col}/{col + 1} became {lit[peak]}/{next[peak]}");
            }

            // The eye retains its original per-channel grade on the authored lit texel.
            var eye = Pixel(-.65f, col);
            var rgb = new Vector3(source[0], source[1], source[2]) / 255;
            float luma = Vector3.Dot(rgb, new(.2126f, .7152f, .0722f));
            var saturated = new Vector3(luma) + (rgb - new Vector3(luma)) * grade.Saturation;
            for (int channel = 0; channel < 3; channel++)
            {
                float value = Math.Clamp(saturated[channel], 0, 1);
                value = float.Lerp(value, value * value * (3 - 2 * value), grade.Contrast);
                value = value * grade.Brightness / (1 + (grade.Brightness - 1) * value);
                Assert.InRange(MathF.Abs(eye[channel] - value * 255), 0, 1.1f);
            }
        }
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        long sum = 0; int pixels = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (a[i + 3] == 0 && b[i + 3] == 0) continue;
            sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
            pixels++;
        }
        return pixels == 0 ? 0 : sum / (3.0 * pixels);
    }

    private static readonly Guid ObjectId = Guid.NewGuid();

    private static SceneRenderingStyleBinding Binding(string preset)
        => new(ObjectId, new(preset), ModelRole.Character, 0, 1, true);

    /// <summary>一个细分球：从亮到暗的完整明暗过渡，带饱和的底色。</summary>
    private static OriginalFrameScene Frame(string materialName)
    {
        const int rings = 24, segments = 32;
        var vertices = new List<SceneVertex>();
        var indices = new List<uint>();
        for (int r = 0; r <= rings; r++)
            for (int s = 0; s <= segments; s++)
            {
                float theta = MathF.PI * r / rings, phi = 2 * MathF.PI * s / segments;
                var normal = new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
                vertices.Add(new(normal * .9f, normal, new((float)s / segments, (float)r / rings)));
            }
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < segments; s++)
            {
                uint a = (uint)(r * (segments + 1) + s), b = a + segments + 1;
                indices.AddRange([a, a + 1, b, a + 1, b + 1, b]);
            }
        var snapshot = new SceneSnapshot(vertices.ToArray(), indices.ToArray(),
            [new SceneMaterial(materialName, 0, indices.Count, new(.85f, .45f, .55f, 1), new(.3f, .3f, .3f), Vector3.Zero, 20)],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2.2f, .1f, 20),
            lightDirection: Vector3.Normalize(new(-.7f, .4f, -.6f)));
        return new(new(snapshot, [new OriginalStyleMaterialBinding(new(.7f))],
            new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: D3D12OriginalFrameTests.Options());
    }
}
