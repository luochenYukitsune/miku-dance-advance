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
            Assert.True(MeanDifference(raw, captures[a]) > 6, $"{Presets[a + 1]} vs mmd");
            for (int b = a + 1; b < captures.Length; b++)
                Assert.True(MeanDifference(captures[a], captures[b]) > 3, $"{Presets[a + 1]} vs {Presets[b + 1]}: {report}");
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
