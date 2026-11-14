using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12FaceGeometricShadeTests
{
    private const int Width = 129, Height = 97;
    private static readonly D3D12SurfaceStyle[] Families =
        [D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.Mmd];

    [Fact]
    public void OnlyExplicitGenshinFaceConsumesGeometricStrengthAndEqualityUsesConsumedValues()
    {
        var body = new D3D12SurfaceStyleMap(Families);
        var faceOne = Map(1);
        var face = Map(.35f);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(body, faceOne));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(body, face));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(face, Map(.35f, .1f)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(face, Map(.36f)));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(0), Map(-0f)));
        Assert.Equal(.35f, face.EffectiveGeometricShadowStrength(0));
        Assert.Equal(1, face.EffectiveGeometricShadowStrength(1));
        Assert.Equal(1, face.EffectiveGeometricShadowStrength(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => face.EffectiveGeometricShadowStrength(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => face.EffectiveGeometricShadowStrength(face.Count));
        var mmd = new D3D12SurfaceStyleMap([D3D12SurfaceStyle.Mmd],
            [new(D3D12CharacterMaterialRole.Face, FaceGeometricShadowStrength: 0)]);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(mmd, null));
        Assert.Equal(1, new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Face).FaceGeometricShadowStrength);
    }

    [Theory]
    [InlineData(-.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void GeometricStrengthRejectsNonfiniteAndOutOfRangeEvenOnUnusedBody(float strength)
    {
        var value = new D3D12CharacterMaterialStyle(FaceGeometricShadowStrength: strength);
        Assert.Throws<ArgumentOutOfRangeException>(value.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(() => Map(1, strength).Validate(Fixture()));
    }

    [Fact, Trait("Category", "Gpu")]
    public void ActualCastShadowIsLightenedOnlyOnFaceAndDefaultRestoresWithNoNewPipelinesOrOrdinaryReadbacks()
    {
        using var session = new D3D12FrameSession();
        var frame = Fixture();
        var plainMap = new D3D12SurfaceStyleMap(Families);
        var original = session.Capture(frame, Width, Height, surfaceStyles: plainMap);
        var selected = session.Capture(frame, Width, Height, surfaceStyles: Map(.35f));
        var face = Project(frame, new(-.9f, 0, 0));
        var body = Project(frame, new(.9f, 0, 0));
        // Band=1 in this fixture, so the fully occluded shadow is .2 by default;
        // visibility after a .35 receive strength is .65, preserving its shape.
        D3D12OriginalFrameTests.AssertPixel(original, face.X, face.Y, new(.2f, .2f, .2f, 1));
        D3D12OriginalFrameTests.AssertPixel(selected, face.X, face.Y, new(.72f, .72f, .72f, 1));
        Assert.Equal(Pixel(original, body), Pixel(selected, body));
        for (int offset = 3; offset < original.Rgba.Length; offset += 4)
            Assert.Equal(original.Rgba[offset], selected.Rgba[offset]);
        Assert.Equal(original.Rgba, session.Capture(frame, Width, Height, surfaceStyles: Map(1)).Rgba);
        Assert.Equal(original.Rgba, session.Capture(frame, Width, Height, surfaceStyles: plainMap).Rgba);
        var warm = session.Statistics!;
        long readbacks = session.Runtime.Readbacks;
        foreach (float strength in new[] { 0f, .35f, 1f, .6f, .35f })
        {
            using var output = session.Render(frame, Width, Height, surfaceStyles: Map(strength));
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Equal(warm.PipelineCreations, output.Statistics.PipelineCreations);
            Assert.Equal(warm.ShaderCompilations, output.Statistics.ShaderCompilations);
        }
        Assert.Equal(readbacks, session.Runtime.Readbacks);
        long submissions = session.Runtime.Submissions;
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Render(frame, Width, Height, surfaceStyles: Map(float.NaN)));
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
    }

    [Theory, InlineData(false), InlineData(true)]
    [Trait("Category", "Gpu")]
    public void PartialGeometricVisibilityIsRemappedAfterTheNormalBandWithoutChangingRawAlphaOrAuthoredSpecular(bool subTexture)
    {
        using var session = new D3D12FrameSession();
        var frame = Fixture(partial: true, subTexture: subTexture);
        // A unit authored lobe remains below RGB saturation here and survives the
        // .075 originalC and SubTexture alpha attenuation by nearly two UNorm steps.
        var authoredSpecular = Vector3.One;
        var specular = Fixture(partial: true, subTexture: subTexture, specular: authoredSpecular);
        var normalOnly = FaceMap(.35f, 1);
        var attenuated = FaceMap(.35f, .35f);
        var before = session.Capture(frame, Width, Height, surfaceStyles: normalOnly);
        var after = session.Capture(frame, Width, Height, surfaceStyles: attenuated);
        var beforeSpec = session.Capture(specular, Width, Height, surfaceStyles: normalOnly);
        var afterSpec = session.Capture(specular, Width, Height, surfaceStyles: attenuated);
        var p = Project(frame, new(-.9f, 0, 0));
        var body = Project(frame, new(.9f, 0, 0));
        var a = Pixel(before, p); var b = Pixel(after, p);
        var c = Pixel(beforeSpec, p); var d = Pixel(afterSpec, p);
        Assert.True(b[0] > a[0], "The real partial cast shadow must brighten.");
        // Shadow visibility=.55 (generated depth slope). NdotL=.025 gives
        // smooth band=.68359375, remapped by normal strength=.35 BEFORE visibility.
        const float normalBand = .65f + .35f * .68359375f;
        const float remappedVisibility = .65f + .35f * .55f;
        float rawAlpha = subTexture ? .75f * (128 / 255f) * (128 / 255f) * (.075f + .8f * .925f) : 1;
        float diffuse = subTexture ? .3f * (128 / 255f) : .3f;
        float expected = diffuse * (.2f + .8f * normalBand * remappedVisibility) * rawAlpha * 255;
        Assert.InRange(Math.Abs(b[0] - expected), 0, 1.2f);
        var mesh = specular.Surface.Snapshot;
        var viewProjection = mesh.Camera.ViewProjection(Width, Height);
        Matrix4x4.Invert(viewProjection, out var inverseViewProjection);
        var selectedClip = Vector4.Transform(new Vector4(-.9f, 0, 0, 1), viewProjection);
        var pixelClip = new Vector4((p.X + .5f) / Width * 2 - 1,
            1 - (p.Y + .5f) / Height * 2, selectedClip.Z / selectedClip.W, 1);
        var world = Vector4.Transform(pixelClip, inverseViewProjection);
        var position = new Vector3(world.X, world.Y, world.Z) / world.W;
        var halfVector = Vector3.Normalize(Vector3.UnitX + Vector3.Normalize(mesh.Camera.Eye - position));
        float lobe = MathF.Pow(MathF.Max(0, Vector3.Dot(mesh.Vertices[0].Normal, halfVector)), mesh.Materials[0].Shininess);
        var expectedSpecular = Vector3.Clamp(authoredSpecular * lobe, Vector3.Zero, Vector3.One) * (.075f * rawAlpha * 255);
        Assert.True(expectedSpecular.X > 1.5f, "The authored lobe must survive alpha attenuation by more than one UNorm step.");
        for (int channel = 0; channel < 3; channel++)
        {
            Assert.InRange(Math.Abs((c[channel] - a[channel]) - expectedSpecular[channel]), 0, 1.1f);
            Assert.InRange(Math.Abs((d[channel] - b[channel]) - expectedSpecular[channel]), 0, 1.1f);
            Assert.InRange(Math.Abs((c[channel] - a[channel]) - (d[channel] - b[channel])), 0, 1);
        }
        Assert.True(c[0] > a[0], "Authored nonzero specular must actually be observable.");
        Assert.Equal(a[3], b[3]); Assert.Equal(c[3], d[3]);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            var oldPixel = Pixel(before, (x, y)); var newPixel = Pixel(after, (x, y));
            Assert.Equal(oldPixel[3], newPixel[3]);
            if (body.X < Width / 2 ? x < Width / 2 : x > Width / 2)
                Assert.Equal(oldPixel, newPixel);
        }
    }

    private static D3D12SurfaceStyleMap Map(float geometry, float unusedBody = 1)
        => new(Families, [new(D3D12CharacterMaterialRole.Face, 1, geometry),
            new(FaceGeometricShadowStrength: unusedBody), new(D3D12CharacterMaterialRole.Face, 0, 0)]);

    private static D3D12SurfaceStyleMap FaceMap(float normal, float geometry)
        => new(Families, [new(D3D12CharacterMaterialRole.Face, normal, geometry), new(), new()]);

    private static OriginalFrameScene Fixture(bool partial = false, bool subTexture = false, Vector3? specular = null)
    {
        var normal = partial ? new Vector3(.025f, 0, -MathF.Sqrt(1 - .025f * .025f)) : -Vector3.UnitZ;
        SceneVertex[] Quad(float left, float right, float z) =>
            [new(new(left, -.8f, z), normal, Vector2.Zero), new(new(right, -.8f, z), normal, Vector2.Zero),
                new(new(right, .8f, z), normal, Vector2.Zero), new(new(left, .8f, z), normal, Vector2.Zero)];
        var vertices = Quad(-1.8f, -.2f, 0).Concat(Quad(.2f, 1.8f, 0)).Concat(Quad(-1.8f, 1.8f, 1)).ToArray();
        Vector4 color = partial ? new(.3f, .25f, .2f, subTexture ? .75f : 1) : Vector4.One;
        var materials = Enumerable.Range(0, 3).Select(i => new SceneMaterial("explicit " + i, i * 6, 6,
            color, specular ?? Vector3.Zero, Vector3.Zero, 1, subTexture ? 0 : -1)).ToArray();
        var binding = new OriginalStyleMaterialBinding(new(.2f), subTexture ? 1 : -1,
            subTexture ? OriginalSphereMode.SubTexture : OriginalSphereMode.None, true);
        if (subTexture) binding = binding with { TextureFactors = OriginalMaterialTextureFactors.Identity with
            { Toon = new(new(1, 1, 1, .8f), Vector4.Zero) } };
        var additional = subTexture ? new SceneAdditionalUvs(1,
            Enumerable.Repeat(new SceneAdditionalUvVertex(new(.25f, .25f, 0, 0), default, default, default), 12).ToArray()) : null;
        var mesh = new SceneSnapshot(vertices, [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6, 8, 10, 9, 8, 11, 10], materials,
            subTexture ? [new("diffuse alpha", 1, 1, [255, 255, 255, 128]), new("subtexture alpha", 1, 1, [128, 128, 128, 128])] : null,
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3.6f, .1f, 20),
            lightDirection: partial ? Vector3.UnitX : -Vector3.UnitZ, additionalUvs: additional);
        Matrix4x4 projection;
        if (partial) { projection = Matrix4x4.Identity; projection.M11 = .5f; projection.M22 = .5f; projection.M33 = -.0005f; projection.M43 = .5f; }
        else projection = Matrix4x4.CreateLookAt(new(0, 0, 5), Vector3.Zero, Vector3.UnitY) * Matrix4x4.CreateOrthographic(4, 4, .1f, 20);
        return new(new(mesh, [binding, binding, binding], new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false,
                SelfShadow: new(projection, 64, 64, true, false, OriginalTextureSampling.PointClamp, OriginalDepthComparison.LessEqual),
                BackgroundColor: Vector4.Zero),
            materials: [new(false, true, false), new(false, true, false), new(true, false, false, SurfaceEnabled: false, ShadowDoubleSided: true)]);
    }

    private static (int X, int Y) Project(OriginalFrameScene frame, Vector3 point)
    {
        var p = Vector4.Transform(new Vector4(point, 1), frame.Surface.Snapshot.Camera.ViewProjection(Width, Height));
        return ((int)((p.X / p.W * .5f + .5f) * Width), (int)((.5f - p.Y / p.W * .5f) * Height));
    }

    private static byte[] Pixel(RenderedFrame frame, (int X, int Y) p)
        => frame.Rgba.AsSpan((p.Y * Width + p.X) * 4, 4).ToArray();
}
