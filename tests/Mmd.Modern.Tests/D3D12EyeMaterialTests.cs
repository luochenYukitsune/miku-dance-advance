using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12EyeMaterialTests
{
    private const int Width = 129, Height = 97;

    [Fact]
    public void ExplicitEyeHasItsOwnCastShadowDefaultAndKeepsTheAuthoredNormalResponse()
    {
        var eye = new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Eye,
            FaceNormalShadeStrength: 0, FaceGeometricShadowStrength: 0);
        eye.Validate();
        Assert.Equal(.2f, eye.EyeGeometricShadowStrength);
        Assert.Equal(D3D12CharacterMaterialRole.Body, new D3D12CharacterMaterialStyle().Role);
        Assert.Equal(1f, new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Face).FaceGeometricShadowStrength);
        var styles = new[]
        {
            T8cTestHelpers.MmdStyleId,
            T8cTestHelpers.GenshinStyleId,
            T8cTestHelpers.StarRailStyleId,
            T8cTestHelpers.WutheringStyleId,
            T8cTestHelpers.EndfieldStyleId,
        };
        var map = T8cTestHelpers.CreateDirectMap(styles, styles.Select(_ => eye));
        for (int i = 0; i < styles.Length; i++)
        {
            Assert.Equal(1f, map.EffectiveNormalShadeStrength(i));
            Assert.Equal(styles[i] == T8cTestHelpers.MmdStyleId ? 1f : .2f,
                map.EffectiveGeometricShadowStrength(i));
        }
    }

    [Theory]
    [InlineData(-.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void EyeContrastRejectsInvalidValuesEvenOnAnUnusedBody(float strength)
    {
        foreach (var role in Enum.GetValues<D3D12CharacterMaterialRole>())
        {
            var style = new D3D12CharacterMaterialStyle(role, EyeGeometricShadowStrength: strength);
            Assert.Throws<ArgumentOutOfRangeException>(style.Validate);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => Map(T8cTestHelpers.StarRailStyleId,
            new(EyeGeometricShadowStrength: strength)).Validate(Fixture()));
    }

    [Fact]
    public void FaceSdfCannotBeAccidentallyAssignedToTheEye()
    {
        var id = Guid.NewGuid();
        foreach (string styleId in T8cTestHelpers.ModernStyleIds)
        {
            var faceSdf = T8cTestHelpers.FaceSdfBinding(styleId, 0, secondaryTextureSlot: styleId is T8cTestHelpers.GenshinStyleId or T8cTestHelpers.EndfieldStyleId ? 0 : -1,
                forward: -Vector3.UnitZ, right: Vector3.UnitX);
            var validation = T8cTestHelpers.ValidateMaterialBinding(styleId, faceSdf, D3D12CharacterMaterialRole.Eye);
            Assert.False(validation.IsValid);
            Assert.Throws<ArgumentException>(() => D3D12ViewPluginMaterialBindings.Validate(
                [new(id, 0, D3D12CharacterMaterialRole.Eye)],
                [TestStylePlugins.Bind(id, 0, faceSdf)],
                TestStylePlugins.AllViewCatalog));
        }
    }

    [Theory]
    [InlineData(T8cTestHelpers.GenshinStyleId)]
    [InlineData(T8cTestHelpers.StarRailStyleId)]
    [InlineData(T8cTestHelpers.WutheringStyleId)]
    [InlineData(T8cTestHelpers.EndfieldStyleId)]
    public void EqualityTracksOnlyConsumedEyeContrastAndUsesTheExistingHistoryContract(string family)
    {
        var eye = Map(family, new(D3D12CharacterMaterialRole.Eye));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(eye,
            Map(family, new(D3D12CharacterMaterialRole.Eye, 0, 0))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(eye,
            Map(family, new(D3D12CharacterMaterialRole.Face, 1, .2f))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(eye,
            Map(family, new(D3D12CharacterMaterialRole.Eye, EyeGeometricShadowStrength: .21f))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(family, new()),
            Map(family, new(EyeGeometricShadowStrength: 0))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(family, new()),
            Map(family, new(D3D12CharacterMaterialRole.Eye, EyeGeometricShadowStrength: 1))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(
            Map(family, new(D3D12CharacterMaterialRole.Eye, EyeGeometricShadowStrength: 0)),
            Map(family, new(D3D12CharacterMaterialRole.Eye, EyeGeometricShadowStrength: -0f))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(null, Map(T8cTestHelpers.MmdStyleId,
            new(D3D12CharacterMaterialRole.Eye, EyeGeometricShadowStrength: 0))));
    }

    [Fact]
    public void ViewInputUsesExplicitLocalIndicesAndDoesNotClassifyOtherMaterials()
    {
        var scene = Fixture();
        var instance = new SceneRenderingStyleBinding(Guid.NewGuid(), new("star-rail"),
            ModelRole.Character, 0, 3, true);
        var source = new[] { new D3D12ViewCharacterMaterial(instance.ObjectId, 0, D3D12CharacterMaterialRole.Eye) };
        var resolved = D3D12ViewRenderingStyles.Resolve(scene, TestStylePlugins.AllPresetCatalog,
            [instance], source, styleCatalog: TestStylePlugins.AllViewCatalog);
        source[0] = source[0] with { LocalMaterialIndex = 1, Role = D3D12CharacterMaterialRole.Body };
        Assert.Equal(D3D12CharacterMaterialRole.Eye, resolved.SurfaceStyles.CharacterMaterials[0].Role);
        Assert.Equal(.2f, resolved.SurfaceStyles.EffectiveGeometricShadowStrength(0));
        Assert.Equal(1f, resolved.SurfaceStyles.EffectiveNormalShadeStrength(0));
        Assert.Equal(D3D12CharacterMaterialRole.Body, resolved.SurfaceStyles.CharacterMaterials[1].Role);
        Assert.Equal(1f, resolved.SurfaceStyles.EffectiveGeometricShadowStrength(1));
        Assert.Contains(resolved.Diagnostics, diagnostic => diagnostic.Contains(instance.ObjectId.ToString("D")));
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D12ViewRenderingStyles.Resolve(scene,
            TestStylePlugins.AllPresetCatalog, [instance], [new(instance.ObjectId, 0,
                D3D12CharacterMaterialRole.Eye, EyeGeometricShadowStrength: float.NaN)],
            styleCatalog: TestStylePlugins.AllViewCatalog));
    }

    public static IEnumerable<object[]> RenderVariants()
    {
        foreach (string family in T8cTestHelpers.ModernStyleIds)
        foreach (bool subTexture in new[] { false, true })
            yield return [family, subTexture];
    }

    [Theory, MemberData(nameof(RenderVariants))]
    [Trait("Category", "Gpu")]
    public void EyeMatchesTheAcceptedCandidateAndRetainsRealShadowShapeCoverageBodyAndSpecular(
        string family, bool subTexture)
    {
        using var session = new D3D12FrameSession();
        var frame = Fixture(subTexture);
        var bodyMap = Map(family, new());
        var eyeMap = Map(family, new(D3D12CharacterMaterialRole.Eye));
        var original = session.Capture(frame, Width, Height, surfaceStyles: bodyMap);
        var eye = session.Capture(frame, Width, Height, surfaceStyles: eyeMap);
        var candidate = session.Capture(frame, Width, Height, surfaceStyles:
            Map(family, new(D3D12CharacterMaterialRole.Face, 1, .2f)));
        Assert.Equal(candidate.Rgba, eye.Rgba);

        var shadowPoint = Project(frame, new(-.9f, -.4f, 0));
        var clearPoint = Project(frame, new(-.9f, .4f, 0));
        var shadow = Pixel(eye, shadowPoint);
        var clear = Pixel(eye, clearPoint);
        var originalShadow = Pixel(original, shadowPoint);
        // A generated shadow covers only the lower half. Normal band=1 throughout;
        // visibility 0 becomes .8, so the palette response is .2 + .8*.8 = .84.
        // The unoccluded half stays 1: the actual shape survives, rather than being disabled.
        float sphere = subTexture ? 128 / 255f : 1;
        float clearAlpha = subTexture ? .75f * sphere * sphere : 1;
        float shadowAlpha = clearAlpha * (subTexture ? .8f : 1);
        var diffuse = new Vector3(.3f, .25f, .2f) * sphere;
        for (int channel = 0; channel < 3; channel++)
        {
            float expected = Component(diffuse, channel) * .84f * shadowAlpha * 255;
            Assert.InRange(Math.Abs(shadow[channel] - expected), 0, 1.2f);
            Assert.True(shadow[channel] > originalShadow[channel]);
            Assert.True(shadow[channel] < clear[channel]);
        }
        Assert.Equal(Pixel(original, clearPoint), clear);
        int changed = 0, unchangedBody = 0;
        var bodyPoint = Project(frame, new(.9f, 0, 0));
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            var before = Pixel(original, (x, y)); var after = Pixel(eye, (x, y));
            Assert.Equal(before[3], after[3]);
            if (!before.SequenceEqual(after)) changed++;
            if (bodyPoint.X < Width / 2 ? x < Width / 2 : x > Width / 2)
            {
                Assert.Equal(before, after);
                if (before[3] != 0) unchangedBody++;
            }
        }
        Assert.True(changed > 100);
        Assert.True(unchangedBody > 200);
        Assert.Equal(original.Rgba, session.Capture(frame, Width, Height, surfaceStyles:
            Map(family, new(D3D12CharacterMaterialRole.Eye, EyeGeometricShadowStrength: 1))).Rgba);

        var specularFrame = Fixture(subTexture, new(.25f, .2f, .15f));
        var bodySpecular = session.Capture(specularFrame, Width, Height, surfaceStyles: bodyMap);
        var eyeSpecular = session.Capture(specularFrame, Width, Height, surfaceStyles: eyeMap);
        var a = Pixel(original, clearPoint); var b = Pixel(bodySpecular, clearPoint);
        var c = Pixel(eye, clearPoint); var d = Pixel(eyeSpecular, clearPoint);
        for (int channel = 0; channel < 3; channel++)
        {
            Assert.True(b[channel] > a[channel] + 2, "The authored specular must remain observable after alpha blending.");
            Assert.Equal(b[channel] - a[channel], d[channel] - c[channel]);
        }
        Assert.Equal(shadow, Pixel(eyeSpecular, shadowPoint)); // originalC=0: Eye does not invent a shadow highlight.
        Assert.Equal(original.Rgba, session.Capture(frame, Width, Height, surfaceStyles: bodyMap).Rgba);

        var warm = session.Statistics!;
        long readbacks = session.Runtime.Readbacks;
        using var output = session.Render(frame, Width, Height, surfaceStyles: eyeMap);
        Assert.Equal(0, output.Statistics.ReadbackCount);
        Assert.Equal(warm.ShaderCompilations, output.Statistics.ShaderCompilations);
        Assert.Equal(warm.PipelineCreations, output.Statistics.PipelineCreations);
        Assert.Equal(readbacks, session.Runtime.Readbacks);
        long submissions = session.Runtime.Submissions;
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Render(frame, Width, Height, surfaceStyles:
            Map(family, new(D3D12CharacterMaterialRole.Eye, EyeGeometricShadowStrength: float.NaN))));
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
    }

    private static D3D12SurfaceStyleMap Map(string family, D3D12CharacterMaterialStyle eye)
        => T8cTestHelpers.CreateDirectMap([family, family, T8cTestHelpers.MmdStyleId], [eye, new(), new()]);

    private static OriginalFrameScene Fixture(bool subTexture = false, Vector3? specular = null)
    {
        SceneVertex[] Quad(float left, float right, float lower, float upper, float z) =>
            [new(new(left, lower, z), -Vector3.UnitZ, Vector2.Zero),
                new(new(right, lower, z), -Vector3.UnitZ, Vector2.Zero),
                new(new(right, upper, z), -Vector3.UnitZ, Vector2.Zero),
                new(new(left, upper, z), -Vector3.UnitZ, Vector2.Zero)];
        var vertices = Quad(-1.8f, -.2f, -.8f, .8f, 0).Concat(Quad(.2f, 1.8f, -.8f, .8f, 0))
            .Concat(Quad(-1.8f, 1.8f, -.8f, 0, 1)).ToArray();
        var materials = Enumerable.Range(0, 3).Select(i => new SceneMaterial("same-source.pmx", i * 6, 6,
            new(.3f, .25f, .2f, subTexture ? .75f : 1), specular ?? Vector3.Zero, Vector3.Zero, 1,
            subTexture ? 0 : -1)).ToArray();
        var binding = new OriginalStyleMaterialBinding(new(.2f), subTexture ? 1 : -1,
            subTexture ? OriginalSphereMode.SubTexture : OriginalSphereMode.None, true);
        if (subTexture) binding = binding with { TextureFactors = OriginalMaterialTextureFactors.Identity with
            { Toon = new(new(1, 1, 1, .8f), Vector4.Zero) } };
        var additional = subTexture ? new SceneAdditionalUvs(1, Enumerable.Repeat(
            new SceneAdditionalUvVertex(new(.25f, .25f, 0, 0), default, default, default), 12).ToArray()) : null;
        var scene = new SceneSnapshot(vertices, [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6, 8, 10, 9, 8, 11, 10], materials,
            subTexture ? [new("coverage", 1, 1, [255, 255, 255, 128]), new("subtexture", 1, 1, [128, 128, 128, 128])] : null,
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3.6f, .1f, 20),
            lightDirection: -Vector3.UnitZ, additionalUvs: additional);
        var projection = Matrix4x4.CreateLookAt(new(0, 0, 5), Vector3.Zero, Vector3.UnitY)
            * Matrix4x4.CreateOrthographic(4, 4, .1f, 20);
        return new(new(scene, [binding, binding, binding],
            new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false,
                SelfShadow: new(projection, 64, 64, true, false, OriginalTextureSampling.PointClamp, OriginalDepthComparison.LessEqual),
                BackgroundColor: Vector4.Zero),
            materials: [new(false, true, false), new(false, true, false),
                new(true, false, false, SurfaceEnabled: false, ShadowDoubleSided: true)]);
    }

    private static float Component(Vector3 value, int channel) => channel switch
        { 0 => value.X, 1 => value.Y, _ => value.Z };

    private static (int X, int Y) Project(OriginalFrameScene frame, Vector3 point)
    {
        var p = Vector4.Transform(new Vector4(point, 1), frame.Surface.Snapshot.Camera.ViewProjection(Width, Height));
        return ((int)((p.X / p.W * .5f + .5f) * Width), (int)((.5f - p.Y / p.W * .5f) * Height));
    }

    private static byte[] Pixel(RenderedFrame frame, (int X, int Y) p)
        => frame.Rgba.AsSpan((p.Y * Width + p.X) * 4, 4).ToArray();
}
