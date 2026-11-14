using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12FaceMaterialTests
{
    private const int Width = 129, Height = 97;
    private static readonly D3D12SurfaceStyle[] Families =
        [D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.Mmd];

    [Fact]
    public void ExplicitMaterialRolesAreDetachedAndOnlyConsumedByTheSelectedFamily()
    {
        var settings = new[] { new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Face),
            new D3D12CharacterMaterialStyle(FaceNormalShadeStrength: 0),
            new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Face, 0) };
        var map = new D3D12SurfaceStyleMap(Families, settings);
        settings[0] = new(D3D12CharacterMaterialRole.Face, 0);
        map.Validate(Rows());
        Assert.Equal(.35f, map.EffectiveNormalShadeStrength(0));
        Assert.Equal(1, map.EffectiveNormalShadeStrength(1));
        Assert.Equal(1, map.EffectiveNormalShadeStrength(2));
        var mutable = Assert.IsAssignableFrom<IList<D3D12CharacterMaterialStyle>>(map.CharacterMaterials);
        Assert.Throws<NotSupportedException>(() => mutable[0] = new());
        var defaults = new D3D12SurfaceStyleMap(Families);
        Assert.All(defaults.CharacterMaterials, item => Assert.Equal(D3D12CharacterMaterialRole.Body, item.Role));
        Assert.All(Enumerable.Range(0, defaults.Count), i => Assert.Equal(1, defaults.EffectiveNormalShadeStrength(i)));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.EffectiveNormalShadeStrength(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.EffectiveNormalShadeStrength(map.Count));
    }

    [Fact]
    public void EffectiveEqualityIgnoresUnusedRolesAndStrengthsButDetectsRealFaceChanges()
    {
        var body = new D3D12SurfaceStyleMap(Families);
        var faceOne = Map(1, .8f);
        var face = Map(.35f, .8f);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(body, faceOne));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(face, Map(.35f, .1f)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(face, Map(.36f)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(body, face));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(0), Map(-0f)));
        var mmd = new D3D12SurfaceStyleMap([D3D12SurfaceStyle.Mmd], [new(D3D12CharacterMaterialRole.Face, 0)]);
        Assert.True(mmd.IsDefault);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(null, mmd));
    }

    [Theory]
    [InlineData(-.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonfiniteAndOutOfRangeValuesAreRejectedEvenOnAnUnusedBody(float strength)
    {
        var setting = new D3D12CharacterMaterialStyle(FaceNormalShadeStrength: strength);
        Assert.Throws<ArgumentOutOfRangeException>(setting.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12SurfaceStyleMap(Families,
            [setting, new(), new()]).Validate(Rows()));
    }

    [Fact]
    public void InvalidRolesMissingExtraAndNullMaterialSettingsAreRejected()
    {
        var frame = Rows();
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12CharacterMaterialStyle((D3D12CharacterMaterialRole)42).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12SurfaceStyleMap(Families,
            [new((D3D12CharacterMaterialRole)42), new(), new()]).Validate(frame));
        Assert.Throws<ArgumentException>(() => new D3D12SurfaceStyleMap(Families, []).Validate(frame));
        Assert.Throws<ArgumentException>(() => new D3D12SurfaceStyleMap(Families,
            [new(), new(), new(), new()]).Validate(frame));
        Assert.Throws<ArgumentException>(() => new D3D12SurfaceStyleMap(Families,
            [null!, new(), new()]).Validate(frame));
    }

    [Theory, InlineData(false), InlineData(true)]
    [Trait("Category", "Gpu")]
    public void FaceBrightensOnlyTheExplicitMaterialAndKeepsRawMorphSubTextureAlphaExact(bool subTexture)
    {
        using var session = new D3D12FrameSession();
        var frame = Rows(subTexture: subTexture);
        var bodyMap = new D3D12SurfaceStyleMap(Families);
        var body = session.Capture(frame, Width, Height, surfaceStyles: bodyMap);
        var face = session.Capture(frame, Width, Height, surfaceStyles: Map(.35f));
        var sample = Project(frame, new(0, .9f, 0));
        Assert.True(Pixel(face, sample.X, sample.Y)[0] > Pixel(body, sample.X, sample.Y)[0]);
        int changed = 0, unaffected = 0;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            var before = Pixel(body, x, y); var after = Pixel(face, x, y);
            Assert.Equal(before[3], after[3]);
            // Every other row is a different material: Body and MMD are untouched.
            if (y >= Height * .4f)
            {
                Assert.Equal(before, after);
                if (before[3] != 0) unaffected++;
            }
            else if (!before.SequenceEqual(after)) changed++;
        }
        Assert.True(changed > 100); Assert.True(unaffected > 200);
        Assert.Equal(body.Rgba, session.Capture(frame, Width, Height, surfaceStyles: Map(1)).Rgba);
        Assert.Equal(body.Rgba, session.Capture(frame, Width, Height, surfaceStyles: bodyMap).Rgba);

        var warm = session.Statistics!; long readbacks = session.Runtime.Readbacks;
        foreach (float strength in new[] { 0f, .25f, .35f, .6f, 1f, 0f })
        {
            using var rendered = session.Render(frame, Width, Height, surfaceStyles: Map(strength));
            Assert.Equal(0, rendered.Statistics.ReadbackCount);
            Assert.Equal(warm.ShaderCompilations, rendered.Statistics.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations, rendered.Statistics.PipelineCreations);
            Assert.InRange(rendered.Statistics.CachedResources, 0, warm.CachedResources + 2);
        }
        Assert.Equal(readbacks, session.Runtime.Readbacks);
    }

    [Fact, Trait("Category", "Gpu")]
    public void AuthoredBlinnIncrementIsNotBrightenedWithTheFaceDiffuseBand()
    {
        using var session = new D3D12FrameSession();
        var plain = Rows(nDotL: .025f); var specular = Rows(nDotL: .025f, specular: new(.5f, .3f, .2f));
        var map = new D3D12SurfaceStyleMap(Families);
        var bodyBase = session.Capture(plain, Width, Height, surfaceStyles: map);
        var bodySpec = session.Capture(specular, Width, Height, surfaceStyles: map);
        var faceBase = session.Capture(plain, Width, Height, surfaceStyles: Map(.35f));
        var faceSpec = session.Capture(specular, Width, Height, surfaceStyles: Map(.35f));
        var sample = Project(plain, new(0, .9f, 0));
        var a = Pixel(bodyBase, sample.X, sample.Y); var b = Pixel(bodySpec, sample.X, sample.Y);
        var c = Pixel(faceBase, sample.X, sample.Y); var d = Pixel(faceSpec, sample.X, sample.Y);
        var mesh = specular.Surface.Snapshot;
        var camera = mesh.Camera;
        var viewProjection = camera.ViewProjection(Width, Height);
        Matrix4x4.Invert(viewProjection, out var inverseViewProjection);
        var selectedClip = Vector4.Transform(new Vector4(0, .9f, 0, 1), viewProjection);
        var pixelClip = new Vector4((sample.X + .5f) / Width * 2 - 1,
            1 - (sample.Y + .5f) / Height * 2, selectedClip.Z / selectedClip.W, 1);
        var world = Vector4.Transform(pixelClip, inverseViewProjection);
        var position = new Vector3(world.X, world.Y, world.Z) / world.W;
        var normal = mesh.Vertices[0].Normal;
        var light = Vector3.Normalize(mesh.LightDirection);
        var halfVector = Vector3.Normalize(light + Vector3.Normalize(camera.Eye - position));
        var host = OriginalStyleMath.ResolveHostColors(mesh.Materials[0], specular.Surface.Profile);
        float lobe = MathF.Pow(MathF.Max(0, Vector3.Dot(normal, halfVector)), host.SpecularPower.W);
        float originalC = Math.Clamp(Vector3.Dot(normal, light) * 3, 0, 1);
        var expected = Vector3.Clamp(new Vector3(host.SpecularPower.X, host.SpecularPower.Y,
            host.SpecularPower.Z) * lobe, Vector3.Zero, Vector3.One) * (originalC * 255);
        for (int channel = 0; channel < 3; channel++)
        {
            Assert.True(b[channel] > a[channel], "A nonzero authored highlight must actually be observable.");
            Assert.True(expected[channel] > 2, "The fixture must produce a visible Blinn increment before quantization.");
            Assert.InRange(Math.Abs((b[channel] - a[channel]) - expected[channel]), 0, 1.1f);
            Assert.InRange(Math.Abs((d[channel] - c[channel]) - expected[channel]), 0, 1.1f);
            Assert.InRange(Math.Abs((d[channel] - c[channel]) - (b[channel] - a[channel])), 0, 1);
        }
        Assert.True(c[0] > a[0]);
        Assert.Equal(a[3], c[3]); Assert.Equal(b[3], d[3]);
    }

    [Fact, Trait("Category", "Gpu")]
    public void ZeroFaceNormalShadeStillReceivesTheActualGeneratedGeometricShadow()
    {
        using var session = new D3D12FrameSession();
        var source = D3D12OriginalFrameTests.Shadow(true);
        var mesh = source.Surface.Snapshot;
        const float nDotL = -.175f;
        var normal = new Vector3(nDotL, 0, -MathF.Sqrt(1 - nDotL * nDotL));
        var vertices = mesh.Vertices.ToArray().Select(vertex => vertex with { Normal = normal }).ToArray();
        var surface = new OriginalStyleScene(new(vertices, mesh.Indices, mesh.Materials,
            camera: mesh.Camera, lightDirection: Vector3.UnitX), source.Surface.Materials, source.Surface.Profile);
        var shadowed = new OriginalFrameScene(surface, options: source.Options, materials: source.Materials);
        var flags = source.Materials.ToArray(); flags[0] = flags[0] with { ReceiveSelfShadow = false };
        var unshadowed = new OriginalFrameScene(surface, options: source.Options, materials: flags);
        var map = new D3D12SurfaceStyleMap([D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.Mmd],
            [new(D3D12CharacterMaterialRole.Face, 0), new()]);
        var on = session.Capture(shadowed, Width, Height, surfaceStyles: map);
        var off = session.Capture(unshadowed, Width, Height, surfaceStyles: map);
        D3D12OriginalFrameTests.AssertPixel(on, Width / 2, Height / 2, new(.2f, .2f, .2f, 1));
        D3D12OriginalFrameTests.AssertPixel(off, Width / 2, Height / 2, Vector4.One);
        var body = session.Capture(shadowed, Width, Height,
            surfaceStyles: new([D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.Mmd]));
        // The fully occluded interior retains its exact dark endpoint. Edge penumbra
        // pixels may brighten through their remaining unoccluded light by design.
        Assert.Equal(Pixel(on, Width / 2, Height / 2), Pixel(body, Width / 2, Height / 2));
    }

    [Fact, Trait("Category", "Gpu")]
    public void EffectiveFaceChangesResetActualAaAndGiOnceAndUnusedChangesKeepHistory()
    {
        using var session = new D3D12FrameSession();
        var source = Rows();
        var frame = new OriginalFrameScene(source.Surface, options: source.Options! with
        {
            CameraOverride = source.Surface.Snapshot.Camera with { VerticalFovDegrees = 45 },
            BackgroundColor = new(0, 0, 0, 1)
        }, materials: source.Materials);
        var temporal = new D3D12TemporalUpscalingOptions
        {
            Mode = D3D12TemporalUpscalingMode.Fsr,
            LoaderPath = Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
                ?? throw new InvalidOperationException("Set MMD_FSR_LOADER_PATH for this explicit actual-SDK GPU test.")
        };
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4,
            Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial);
        Render(new(Families), 1, "FirstFrame", "FirstFrame");
        Render(Map(1, .1f), 2, "None", "None");
        Render(Map(.35f), 1, "ExplicitReset", "ExplicitRequest");
        Render(Map(.35f, .8f), 2, "None", "None");
        long submissions = session.Runtime.Submissions, reads = session.Runtime.Readbacks;
        var bad = Map(float.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Render(frame, Width, Height, gi,
            temporalUpscaling: temporal, surfaceStyles: bad));
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.Equal(reads, session.Runtime.Readbacks);
        Assert.False(session.Runtime.IsRecording);
        Render(Map(.35f, .2f), 3, "None", "None");
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(frame, Width, Height, gi,
            (_, _) => throw new OperationCanceledException("Abort the changed face before Submit."),
            temporalUpscaling: temporal, surfaceStyles: Map(0)));
        Render(Map(.35f), 4, "None", "AbortedFrame");
        Render(Map(1), 1, "ExplicitReset", "ExplicitRequest");
        Render(new(Families), 2, "None", "None");

        void Render(D3D12SurfaceStyleMap map, ulong frames, string giReason, string aaReason)
        {
            using var output = session.Render(frame, Width, Height, gi, temporalUpscaling: temporal, surfaceStyles: map);
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Equal(1, output.Statistics.SubmissionCount);
            var lighting = Assert.IsType<D3D12RayGiStatistics>(session.RayGiStatistics);
            Assert.Equal(frames, lighting.TemporalHistoryFrames);
            Assert.Equal(giReason, lighting.TemporalResetReason);
            var aa = Assert.IsType<D3D12TemporalUpscalingStatistics>(output.Statistics.TemporalUpscaling);
            Assert.Equal(aaReason, aa.ResetReason);
        }
    }

    private static D3D12SurfaceStyleMap Map(float strength, float unusedBodyStrength = .35f)
        => new(Families, [new(D3D12CharacterMaterialRole.Face, strength),
            new(FaceNormalShadeStrength: unusedBodyStrength), new(D3D12CharacterMaterialRole.Face, 0)]);

    private static OriginalFrameScene Rows(bool subTexture = false, float nDotL = -.175f, Vector3? specular = null)
    {
        var normal = new Vector3(nDotL, 0, -MathF.Sqrt(1 - nDotL * nDotL));
        var vertices = new List<SceneVertex>();
        foreach (float y in new[] { .9f, 0f, -.9f })
            vertices.AddRange([new(new(-.8f, y - .3f, 0), normal, Vector2.Zero),
                new(new(.8f, y - .3f, 0), normal, Vector2.Zero),
                new(new(.8f, y + .3f, 0), normal, Vector2.Zero),
                new(new(-.8f, y + .3f, 0), normal, Vector2.Zero)]);
        var materials = Enumerable.Range(0, 3).Select(i => new SceneMaterial($"explicit {i}", i * 6, 6,
            new(.6f, .4f, .2f, subTexture ? .75f : 1), specular ?? Vector3.Zero, Vector3.Zero, 1, subTexture ? 0 : -1)).ToArray();
        var binding = new OriginalStyleMaterialBinding(new(.2f), subTexture ? 1 : -1,
            subTexture ? OriginalSphereMode.SubTexture : OriginalSphereMode.None, true);
        if (subTexture) binding = binding with { TextureFactors = OriginalMaterialTextureFactors.Identity with
        {
            Diffuse = new(new(.8f, .7f, .6f, 1), Vector4.Zero),
            Toon = new(new(1, 1, 1, .2f), Vector4.Zero)
        } };
        var additional = subTexture ? new SceneAdditionalUvs(1,
            Enumerable.Repeat(new SceneAdditionalUvVertex(new(.25f, .25f, 0, 0), default, default, default), 12).ToArray()) : null;
        var scene = new SceneSnapshot(vertices.ToArray(), [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6, 8, 10, 9, 8, 11, 10], materials,
            subTexture ? [new("diffuse alpha", 1, 1, [255, 255, 255, 128]), new("sphere alpha", 1, 1, [128, 128, 128, 128])] : null,
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3.6f, .1f, 20), lightDirection: Vector3.UnitX, additionalUvs: additional);
        return new(new(scene, [binding, binding, binding],
            new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero));
    }

    private static (int X, int Y) Project(OriginalFrameScene frame, Vector3 point)
    {
        var clip = Vector4.Transform(new Vector4(point, 1), frame.Surface.Snapshot.Camera.ViewProjection(Width, Height));
        return ((int)((clip.X / clip.W * .5f + .5f) * Width), (int)((.5f - clip.Y / clip.W * .5f) * Height));
    }
    private static byte[] Pixel(RenderedFrame frame, int x, int y) => frame.Rgba.AsSpan((y * Width + x) * 4, 4).ToArray();
}
