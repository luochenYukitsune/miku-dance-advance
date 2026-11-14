using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
[Trait("Category", "Gpu")]
public sealed class CharacterDirectLightPaletteGpuTests
{
    private const int Width = 129, Height = 97;
    private static readonly Vector3 Diffuse = new(.4f, .24f, .16f);
    private static readonly Vector3 Endpoint = Diffuse * .5f;
    private static readonly Vector3 Toon = new(.2f, .3f, .4f);

    [Fact]
    public void StrongWhitePointLightStopsBeforeDiffuseClippingAndAlreadyLitColorStaysExact()
    {
        using var session = new D3D12FrameSession();
        var point = new NativeSceneLight(Guid.NewGuid(), "white point", SceneLightKind.Point, new(0, 0, -2),
            Quaternion.Identity, Vector3.One, 765, 180, CastShadows: false);
        var source = Surface();
        var legacy = session.Capture(With(source, false, point), Width, Height);
        var selected = session.Capture(With(source, true, point), Width, Height);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(legacy));
        // Ordinary MMD native light has no preliminary remaining-palette factor.
        // One shared scale preserves its hue rather than filling channels separately.
        AssertPixel(selected, new(.16f, .108f, .08f));
        AssertCoverage(legacy, selected);
        var litSource = Surface(primary: -Vector3.UnitZ);
        var off = session.Capture(With(litSource, true), Width, Height);
        AssertPixel(off, Endpoint);
        Assert.Equal(off.Rgba, session.Capture(With(litSource, true, point), Width, Height).Rgba);
        Assert.Equal(legacy.Rgba, session.Capture(With(source, false, point), Width, Height).Rgba);
    }

    [Fact]
    public void LowColoredLightKeepsLegacyBytesAndEveryInactiveLightRouteKeepsTheOriginalProgram()
    {
        using var session = new D3D12FrameSession();
        var source = Surface();
        var light = Directional(.02f, new(1, .5f, .25f));
        var legacy = session.Capture(With(source, false, light), Width, Height);
        Assert.Equal(legacy.Rgba, session.Capture(With(source, true, light), Width, Height).Rgba);
        var baseline = session.Capture(With(source, false), Width, Height);
        foreach (var lights in new[] { Array.Empty<NativeSceneLight>(), [light with { Enabled = false }],
            [light with { Intensity = 0 }], [light with { Color = Vector3.Zero }] })
        {
            Assert.Equal(baseline.Rgba, session.Capture(With(source, true, lights), Width, Height).Rgba);
            Assert.Null(session.NativeLightStatistics);
        }
        Assert.Equal(legacy.Rgba, session.Capture(With(source, true, light), Width, Height).Rgba);
    }

    [Fact]
    public void DiffuseCapKeepsAddSphereAuthoredHighlightAndRawAlphaOutsideItsBudget()
    {
        using var session = new D3D12FrameSession();
        var normal = new Vector3(.2f, 0, -MathF.Sqrt(.96f));
        var plain = Surface(normal: normal, alpha: .65f);
        var decorated = Surface(normal: normal, alpha: .65f, decorated: true);
        var light = Directional(16);
        var off = session.Capture(With(decorated, true), Width, Height);
        var lit = session.Capture(With(decorated, true, light), Width, Height);
        var plainLit = session.Capture(With(plain, true, light), Width, Height);
        const float c = .6f;
        Vector3 factor = new Vector3(c) + Toon * (1 - c);
        Vector3 budget = Endpoint * (Vector3.One - factor);
        Vector3 increment = Diffuse * 16;
        float scale = MathF.Min(budget.X / increment.X, MathF.Min(budget.Y / increment.Y, budget.Z / increment.Z));
        Vector3 sphere = new Vector3(32, 16, 8) / 255;
        Vector3 highlight = new(.08f, .04f, .02f);
        AssertPixel(lit, ((Endpoint + sphere) * factor + highlight * c + increment * scale) * .65f, .65f);
        var a = Pixel(lit); var b = Pixel(plainLit);
        Vector3 decoration = (sphere * factor + highlight * c) * .65f;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(MathF.Abs(a[channel] - b[channel] - decoration[channel] * 255), 0, 2.1f);
        AssertCoverage(off, lit);
        AssertCoverage(plainLit, lit);
    }

    [Fact]
    public void PreviouslyAcceptedSignedAndOverflowingDomainsKeepLegacyArithmeticWithoutNewRejection()
    {
        using var session = new D3D12FrameSession();
        var source = Surface();
        var mesh = source.Surface.Snapshot;
        var outsideToon = new OriginalFrameScene(new(mesh, [new(new(1.2f, .3f, .4f))], source.Surface.Profile), options: source.Options);
        var signedMesh = new SceneSnapshot(mesh.Vertices, mesh.Indices,
            [mesh.Materials[0] with { DiffuseTexture = 0 }], [new("signed morph", 1, 1, [255, 255, 255, 255])],
            camera: mesh.Camera, lightDirection: mesh.LightDirection);
        var signedBinding = source.Surface.Materials[0] with
        { TextureFactors = OriginalMaterialTextureFactors.Identity with { Diffuse = new(new(-.2f, 1, 1, 1), Vector4.Zero) } };
        var signed = new OriginalFrameScene(new(signedMesh, [signedBinding], source.Surface.Profile), options: source.Options);
        foreach (var frame in new[] { outsideToon, signed })
        {
            var light = Directional(.2f);
            Assert.Equal(session.Capture(With(frame, false, light), Width, Height).Rgba,
                session.Capture(With(frame, true, light), Width, Height).Rgba);
        }
        var overflowing = Directional(float.MaxValue, new(4));
        Assert.Equal(session.Capture(With(source, false, overflowing), Width, Height).Rgba,
            session.Capture(With(source, true, overflowing), Width, Height).Rgba);
    }

    [Fact]
    public void ActualToonPaletteBounceAndDirectLightSpendOneBudgetAndPolicyChangesResetGiHistory()
    {
        using var session = new D3D12FrameSession();
        var original = D3D12RayGiTests.Bounce(); var mesh = original.Surface.Snapshot;
        var vertices = mesh.Vertices.ToArray();
        for (int i = 0; i < 4; i++) vertices[i] = vertices[i] with { Normal = new(-.6f, 0, -.8f) };
        var materials = mesh.Materials.ToArray();
        materials[0] = materials[0] with { Diffuse = new(Diffuse, 1), Ambient = Vector3.Zero, Specular = Vector3.Zero };
        materials[1] = materials[1] with { Emission = new(10) };
        var snapshot = new SceneSnapshot(vertices, mesh.Indices, materials, camera: mesh.Camera, lightDirection: Vector3.UnitX);
        var source = new OriginalFrameScene(new(snapshot, [new(new(.2f)), original.Surface.Materials[1]],
            new(new(.5f), Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)), options: sourceOptions());
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: 20, SamplesPerPixel: 32, Seed: 17,
            MaxRayDistance: 50, RayEpsilon: .001f, DirectionalIrradiance: Vector3.Zero, DirectScale: 0,
            OcclusionStrength: 0, Composition: RayGiComposition.ToonPalette) { ToonResponse = RayGiToonResponse.PreservePaletteBands };
        var off = session.Capture(With(source, true), Width, Height);
        var bounce = session.Capture(With(source, true), Width, Height, gi);
        AssertPixel(off, Endpoint * .2f);
        AssertPixel(bounce, Endpoint * .4f);
        var native = With(source, true, Directional(.35f));
        AssertPixel(session.Capture(native, Width, Height), Endpoint * .9f);
        AssertPixel(session.Capture(native, Width, Height, gi), Endpoint);
        gi = gi with { Denoiser = RayGiDenoiser.TemporalSpatial };
        Render(native, "FirstFrame"); Render(native, "None");
        Render(With(source, false, native.Lighting!.Lights.ToArray()), "ReceiverResponseChanged");
        Render(With(source, false, native.Lighting!.Lights.ToArray()), "None");

        void Render(OriginalFrameScene frame, string reason)
        {
            using var output = session.Render(frame, Width, Height, gi);
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Equal(reason, session.RayGiStatistics!.TemporalResetReason);
        }
        static OriginalFrameOptions sourceOptions() => new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero);
    }

    private static OriginalFrameScene Surface(Vector3? normal = null, Vector3? primary = null, float alpha = 1, bool decorated = false)
    {
        var n = normal ?? -Vector3.UnitZ;
        SceneVertex[] vertices = [new(new(-.8f, -.6f, 0), n, Vector2.Zero), new(new(.8f, -.6f, 0), n, Vector2.Zero),
            new(new(.8f, .6f, 0), n, Vector2.Zero), new(new(-.8f, .6f, 0), n, Vector2.Zero)];
        var material = new SceneMaterial("ordinary MMD palette", 0, 6, new(Diffuse, alpha),
            decorated ? new(.08f, .04f, .02f) : Vector3.Zero, Vector3.Zero, 0);
        var mesh = new SceneSnapshot(vertices, [0, 2, 1, 0, 3, 2], [material],
            decorated ? [new("authored add sphere", 1, 1, [32, 16, 8, 255])] : [],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 30), lightDirection: primary ?? Vector3.UnitX);
        return new(new(mesh, [decorated ? new(Toon, 0, OriginalSphereMode.Add) : new(Toon)],
            new(new(.5f), Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero));
    }

    private static NativeSceneLight Directional(float intensity, Vector3? color = null) => new(Guid.NewGuid(), "native fill",
        SceneLightKind.Directional, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI),
        color ?? Vector3.One, intensity, 30, CastShadows: false);
    private static OriginalFrameScene With(OriginalFrameScene source, bool selected, params NativeSceneLight[] lights) => new(source.Surface,
        source.Outline, source.OutlineOptions, source.Options, source.Materials.Select(flags => flags with
        { PreserveDirectLightPalette = selected }), new(lights), source.EditorOverlay);
    private static byte[] Pixel(RenderedFrame frame) => frame.Rgba.AsSpan((Height / 2 * Width + Width / 2) * 4, 4).ToArray();
    private static void AssertPixel(RenderedFrame frame, Vector3 rgb, float alpha = 1)
    {
        var pixel = Pixel(frame); var expected = new Vector4(rgb, alpha);
        for (int channel = 0; channel < 4; channel++)
            Assert.InRange(MathF.Abs(pixel[channel] - expected[channel] * 255), 0, 2.1f);
    }
    private static void AssertCoverage(RenderedFrame left, RenderedFrame right)
    {
        for (int offset = 3; offset < left.Rgba.Length; offset += 4) Assert.Equal(left.Rgba[offset], right.Rgba[offset]);
    }
}
