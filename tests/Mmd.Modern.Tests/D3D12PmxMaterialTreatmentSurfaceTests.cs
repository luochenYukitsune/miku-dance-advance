using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Independent RGB/HSV witnesses for the opt-in ordinary PMX adapter.
/// No source graph, palette asset or game-specific mask is used by these fixtures.</summary>
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12PmxMaterialTreatmentSurfaceTests
{
    private const int Width = 129, Height = 97;
    private static readonly Vector3 Color = new(.7f, .3f, .1f);
    private static readonly Vector3 Toon = new(.2f, .3f, .4f);
    private static readonly Vector3 DarkNormal = new(-.6f, 0, -.8f);

    public static IEnumerable<object[]> Families()
    {
        foreach (var family in new[] { D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.StarRailPmx,
            D3D12SurfaceStyle.WutheringPmx, D3D12SurfaceStyle.EndfieldPmx }) yield return [family];
    }

    public static IEnumerable<object[]> Variants()
    {
        foreach (var family in Families())
        foreach (var variant in D3D12GenshinSurfaceTests.SurfaceVariants())
            yield return [family[0], .. variant];
    }

    [Theory, MemberData(nameof(Variants))]
    public void EveryActualFamilyVariantCompilesWithOneTailAndAllDiffuseConsumers(
        D3D12SurfaceStyle family, string entry, bool native, bool gi, bool morph, bool sub)
    {
        var shader = Compile(family, entry, native, gi, morph, sub);
        Assert.Equal(new byte[] { 68, 88, 66, 67 }, shader[..4]);
        string source = Source(family, native, gi, morph, sub);
        Assert.Equal(1, Occurrences(source, "float4 PmxMaterialTreatmentParams;"));
        Assert.Equal(1, Occurrences(source, "float3 PmxMaterialSaturation(float3 rgb)"));
        Assert.Contains("precise float3 baseColor=PmxMaterialSaturation(EgColor.rgb*", source);
        Assert.Contains("+highlight*originalC;", source);
        Assert.Contains("float alpha=EgColor.a*texel.a*sphere.a;", source);
        if (native || gi)
        {
            // The standalone GI function's endpoint receives the treatment; native lights no longer copy it into a budget.
            Assert.Equal(1, Occurrences(source, "precise float3 diffuseLit=PmxMaterialSaturation("));
            string texel = morph ? "MorphDiffuse(texel)" : "texel";
            Assert.Contains("precise float3 fill=EgColor.rgb*GiAmbientShare.rgb*" + texel + ";", source);
            Assert.Contains("if(PmxMaterialTreatmentParams.x!=1 || PmxMaterialTreatmentParams.y!=0) fill=PmxMaterialSaturation(EgColor.rgb*" + texel + ")*GiAmbientShare.rgb;", source);
        }
        if (native) Assert.Contains("float3 diffuse=PmxMaterialSaturation(NativeDiffuse.rgb*", source);
        Assert.DoesNotContain("PmxMaterialTreatmentParams", OriginalFrameShaders.CharacterToonSource(native, gi, morph, sub));
    }

    [Theory, MemberData(nameof(Families))]
    [Trait("Category", "Gpu")]
    public void SectorBasedHsvOracleKeepsHueValueGraysAndCapsOnlySaturation(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        foreach (var rgb in new[] { Color, new Vector3(.1f, .6f, .3f), new Vector3(.2f, .1f, .65f), new Vector3(.37f), Vector3.Zero })
        foreach (float saturation in new[] { .5f, 1f, 2f })
        {
            var frame = Surface(rgb);
            var actual = session.Capture(frame, Width, Height, surfaceStyles: Map(family, saturation));
            AssertPixel(actual, HsvOracle(rgb, saturation));
            if (saturation == 1 || rgb.X == rgb.Y && rgb.Y == rgb.Z)
                Assert.Equal(session.Capture(frame, Width, Height, surfaceStyles: Map(family)).Rgba, actual.Rgba);
        }
    }

    [Theory, MemberData(nameof(Families))]
    [Trait("Category", "Gpu")]
    public void ExplicitWidthIsMultipliedAfterDefaultResolutionAndOutsideProjectionUsesTheSamePalette(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        float dot = family == D3D12SurfaceStyle.EndfieldPmx ? -.18f : -.05f;
        var normal = new Vector3(dot, 0, -MathF.Sqrt(1 - dot * dot));
        foreach (bool custom in new[] { false, true })
        foreach (bool outside in new[] { false, true })
        {
            var frame = Surface(Color, normal: normal, outside: outside, response: custom ? new(.5f, .08f) : null);
            float width = (custom ? .08f : .1f) * 1.4f;
            float blend = Band(family, dot, .5f, width);
            var expected = HsvOracle(Color, .75f) * (new Vector3(blend) + Toon * (1 - blend));
            AssertPixel(session.Capture(frame, Width, Height, surfaceStyles: Map(family, .75f, 1.4f)), expected);
        }
    }

    [Theory, MemberData(nameof(Families))]
    [Trait("Category", "Gpu")]
    public void OrdinaryIdentityAndDormantMmdRetainExactRgbaWhileProtectedRolesRejectPayloads(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        var frame = Surface(Color, normal: DarkNormal, textured: true);
        foreach (var role in new[] { D3D12CharacterMaterialRole.Body, D3D12CharacterMaterialRole.Hair,
            D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye })
        {
            var settings = new D3D12CharacterMaterialStyle(role);
            var baseline = session.Capture(frame, Width, Height, surfaceStyles: new([family], [settings]));
            if (role is D3D12CharacterMaterialRole.Face or D3D12CharacterMaterialRole.Eye)
            {
                foreach (var payload in new[] { new D3D12PmxMaterialTreatment(), new(.5f, 1.4f) })
                    Assert.Throws<ArgumentException>(() => new D3D12SurfaceStyleMap([family],
                        [settings with { PmxMaterialTreatment = payload }]).Validate(frame));
                continue;
            }
            Assert.Equal(baseline.Rgba, session.Capture(frame, Width, Height,
                surfaceStyles: new([family], [settings with { PmxMaterialTreatment = new() }])).Rgba);
            var mmd = session.Capture(frame, Width, Height, surfaceStyles: new([D3D12SurfaceStyle.Mmd], [settings]));
            Assert.Equal(mmd.Rgba, session.Capture(frame, Width, Height, surfaceStyles: new([D3D12SurfaceStyle.Mmd],
                [settings with { PmxMaterialTreatment = new(.5f, 1.4f) }])).Rgba);
        }
        D3D12CharacterMaterialStyle? dedicated = family switch
        {
            D3D12SurfaceStyle.GenshinPmx => new(D3D12CharacterMaterialRole.Hair, GenshinNonMetallicSpecular: new(0)),
            D3D12SurfaceStyle.StarRailPmx or D3D12SurfaceStyle.WutheringPmx => new(D3D12CharacterMaterialRole.Hair, StarMcHair: new(0, 0)),
            _ => null,
        };
        if (dedicated is not null)
            foreach (var payload in new[] { new D3D12PmxMaterialTreatment(), new(.5f, 1.4f) })
                Assert.Throws<ArgumentException>(() => new D3D12SurfaceStyleMap([family],
                    [dedicated with { PmxMaterialTreatment = payload }]).Validate(frame));
    }

    [Theory, MemberData(nameof(Families))]
    [Trait("Category", "Gpu")]
    public void MixedOrdinaryFaceEyeDedicatedAndMmdDrawsKeepEveryUnselectedPixelExact(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        var source = Surface(Color, normal: DarkNormal, textured: true); var mesh = source.Surface.Snapshot;
        float[] rows = [1.1f, .55f, 0, -.55f, -1.1f];
        var vertices = rows.SelectMany(y => mesh.Vertices.ToArray().Select(v => v with
            { Position = new(v.Position.X, v.Position.Y * .25f + y, 0) })).ToArray();
        var indices = Enumerable.Range(0, rows.Length).SelectMany(i => new uint[]
            { (uint)(i * 4), (uint)(i * 4 + 2), (uint)(i * 4 + 1), (uint)(i * 4), (uint)(i * 4 + 3), (uint)(i * 4 + 2) }).ToArray();
        var materials = Enumerable.Range(0, rows.Length).Select(i => mesh.Materials[0] with { StartIndex = i * 6 }).ToArray();
        var scene = new SceneSnapshot(vertices, indices, materials, mesh.Textures, mesh.World, mesh.Camera, mesh.LightDirection);
        var frame = new OriginalFrameScene(new(scene, Enumerable.Repeat(source.Surface.Materials[0], rows.Length), source.Surface.Profile), options: source.Options);
        var dedicated = family switch
        {
            D3D12SurfaceStyle.GenshinPmx => new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair, GenshinNonMetallicSpecular: new(0)),
            D3D12SurfaceStyle.StarRailPmx or D3D12SurfaceStyle.WutheringPmx => new(D3D12CharacterMaterialRole.Hair, StarMcHair: new(0, 0)),
            _ => new(D3D12CharacterMaterialRole.Hair),
        };
        D3D12CharacterMaterialStyle[] settings = [new(), new(D3D12CharacterMaterialRole.Face), new(D3D12CharacterMaterialRole.Eye), dedicated, new()];
        D3D12SurfaceStyle[] families = [family, family, family, family, D3D12SurfaceStyle.Mmd];
        var baseline = session.Capture(frame, Width, Height, surfaceStyles: new(families, settings));
        var selected = settings.ToArray(); selected[0] = selected[0] with { PmxMaterialTreatment = new(.5f, 1.4f) };
        var actual = session.Capture(frame, Width, Height, surfaceStyles: new(families, selected));
        int Y(float worldY) => (int)((.5f - worldY / mesh.Camera.OrthographicHeight) * Height);
        Assert.NotEqual(PixelAt(baseline, Width / 2, Y(rows[0])), PixelAt(actual, Width / 2, Y(rows[0])));
        for (int y = Y(.8f); y < Height; y++)
            Assert.Equal(baseline.Rgba.AsSpan(y * Width * 4, Width * 4).ToArray(), actual.Rgba.AsSpan(y * Width * 4, Width * 4).ToArray());
        for (int i = 1; i < rows.Length; i++) Assert.True(PixelAt(actual, Width / 2, Y(rows[i]))[3] > 0);
        AssertCoverage(baseline, actual);
    }

    [Theory, MemberData(nameof(Families))]
    [Trait("Category", "Gpu")]
    public void AuthoredHighlightIncrementRemainsIndependentOfTheTreatedDiffuse(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        var plain = Surface(Color); var mesh = plain.Surface.Snapshot;
        var specular = new Vector3(.1f, .05f, .02f);
        var scene = new SceneSnapshot(mesh.Vertices.ToArray(), mesh.Indices, [mesh.Materials[0] with { Specular = specular }],
            mesh.Textures, mesh.World, mesh.Camera, mesh.LightDirection);
        var highlighted = new OriginalFrameScene(new(scene, plain.Surface.Materials, plain.Surface.Profile), options: plain.Options);
        foreach (float saturation in new[] { .5f, 1f })
        {
            var baseline = session.Capture(plain, Width, Height, surfaceStyles: Map(family, saturation));
            var actual = session.Capture(highlighted, Width, Height, surfaceStyles: Map(family, saturation));
            AssertPixel(actual, HsvOracle(Color, saturation) + specular);
            var a = Pixel(baseline); var b = Pixel(actual);
            for (int channel = 0; channel < 3; channel++) Assert.InRange(Math.Abs(b[channel] - a[channel] - specular[channel] * 255), 0, 1.1f);
            AssertCoverage(baseline, actual);
        }
    }

    [Theory]
    [InlineData(OriginalSphereMode.Multiply, false), InlineData(OriginalSphereMode.Multiply, true)]
    [InlineData(OriginalSphereMode.Add, false), InlineData(OriginalSphereMode.Add, true)]
    [InlineData(OriginalSphereMode.SubTexture, false), InlineData(OriginalSphereMode.SubTexture, true)]
    [Trait("Category", "Gpu")]
    public void PostMorphEndpointPrecedesSphereAndSubTextureWhileCoverageAndStraightAlphaStayRaw(OriginalSphereMode sphere, bool straight)
    {
        using var session = new D3D12FrameSession();
        var frame = Surface(Color, normal: DarkNormal, textured: true, sphere: sphere, straight: straight, transparent: true, morph: true);
        var ordinary = session.Capture(frame, Width, Height, surfaceStyles: Map(D3D12SurfaceStyle.GenshinPmx));
        var actual = session.Capture(frame, Width, Height, surfaceStyles: Map(D3D12SurfaceStyle.GenshinPmx, .5f));
        var texel = new Vector3(128 / 255f, 192 / 255f, 64 / 255f);
        var postMorph = texel * new Vector3(.8f, .7f, .6f) + new Vector3(.1f, .05f, .02f);
        var sphereColor = new Vector3(96 / 255f, 160 / 255f, 224 / 255f);
        var endpoint = HsvOracle(Color * postMorph, .5f);
        endpoint = sphere == OriginalSphereMode.Add ? endpoint + sphereColor : endpoint * sphereColor;
        float alpha = .6f * (200 / 255f) * (220 / 255f);
        if (sphere == OriginalSphereMode.SubTexture) alpha *= .4f;
        AssertPixel(actual, endpoint * Toon * (straight ? 1 : alpha), alpha);
        AssertCoverage(ordinary, actual);
        Assert.NotEqual(Pixel(ordinary), Pixel(actual));
    }

    [Fact, Trait("Category", "Gpu")]
    public void HdrAfterTextureMorphIsPreservedBeforeSphereAndSignedColorsPassThroughExactly()
    {
        using var session = new D3D12FrameSession();
        var hdr = Surface(Color, textured: true, sphere: OriginalSphereMode.Multiply,
            factors: new(new(4, 3, 2, 1), Vector4.Zero));
        var rgb = Color * new Vector3(128 / 255f, 192 / 255f, 64 / 255f) * new Vector3(4, 3, 2);
        Assert.True(rgb.X > 1, "The oracle must observe positive HDR before the sphere multiply.");
        AssertPixel(session.Capture(hdr, Width, Height, surfaceStyles: Map(D3D12SurfaceStyle.GenshinPmx, .5f)),
            HsvOracle(rgb, .5f) * new Vector3(96 / 255f, 160 / 255f, 224 / 255f));
        var signed = Surface(Color, textured: true, sphere: OriginalSphereMode.Add,
            factors: new(new(-.5f, 1, 1, 1), Vector4.Zero));
        var baseline = session.Capture(signed, Width, Height, surfaceStyles: Map(D3D12SurfaceStyle.GenshinPmx));
        Assert.Equal(baseline.Rgba, session.Capture(signed, Width, Height, surfaceStyles: Map(D3D12SurfaceStyle.GenshinPmx, .5f)).Rgba);
    }

    [Fact, Trait("Category", "Gpu")]
    public void ActualNearOcclusionAllocatesAmbientAfterHsvRatherThanChangingThePaletteHue()
    {
        using var session = new D3D12FrameSession();
        var vertices = new List<SceneVertex>(); var indices = new List<uint>(); var materials = new List<SceneMaterial>();
        void Add(float left, float right, float bottom, float top, float z)
        {
            uint first = (uint)vertices.Count; int start = indices.Count;
            vertices.AddRange([new(new(left, bottom, z), -Vector3.UnitZ, Vector2.Zero), new(new(right, bottom, z), -Vector3.UnitZ, Vector2.Zero),
                new(new(right, top, z), -Vector3.UnitZ, Vector2.Zero), new(new(left, top, z), -Vector3.UnitZ, Vector2.Zero)]);
            indices.AddRange(new uint[] { 0, 2, 1, 0, 3, 2 }.Select(i => i + first));
            materials.Add(new("independent near-occlusion geometry", start, 6, new(.1f, .1f, .1f, 1), Vector3.Zero, Vector3.Zero, 1));
        }
        Add(-.7f, .7f, -.7f, .7f, 0);
        Add(-1.7f, -.05f, -1.7f, 1.7f, -.2f); Add(.05f, 1.7f, -1.7f, 1.7f, -.2f);
        Add(-.05f, .05f, -1.7f, -.05f, -.2f); Add(-.05f, .05f, .05f, 1.7f, -.2f);
        var diffuse = new Vector3(.4f, .3f, .2f); var ambient = new Vector3(.1f, .2f, .3f);
        OriginalFrameScene Frame(bool probe)
        {
            var rows = materials.ToArray(); rows[0] = rows[0] with { Diffuse = new(probe ? Vector3.Zero : diffuse, 1), Ambient = probe ? Vector3.One : ambient };
            var scene = new SceneSnapshot(vertices.ToArray(), indices.ToArray(), rows,
                camera: new(new(0, 0, -6), Vector3.Zero, Vector3.UnitY, 4.5f, .1f, 30), lightDirection: -Vector3.UnitZ);
            return new(new(scene, Enumerable.Repeat(new OriginalStyleMaterialBinding(Vector3.One, DoubleSided: true), rows.Length),
                new(new(.5f), Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)), options: Options());
        }
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: 0, SamplesPerPixel: 256, Seed: 17,
            MaxRayDistance: 50, RayEpsilon: .001f, DirectScale: 0, OcclusionStrength: 1, OcclusionRadius: 1);
        var families = new[] { D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.Mmd, D3D12SurfaceStyle.Mmd, D3D12SurfaceStyle.Mmd, D3D12SurfaceStyle.Mmd };
        var probe = session.Capture(Frame(true), Width, Height, gi, surfaceStyles: new(families));
        // A white, all-ambient probe measures only the existing transport opacity.
        // Geometry, normals, raw coverage and seed are identical to the color test.
        float occlusion = 1 - Pixel(probe)[0] / 255f;
        Assert.True(occlusion > .75f, "The test requires actual near secondary hits.");
        var map = new D3D12SurfaceStyleMap(families, [new(PmxMaterialTreatment: new(.5f)), new(), new(), new(), new()]);
        var color = Frame(false); var endpoint = diffuse * .5f + ambient;
        var treated = HsvOracle(endpoint, .5f); var share = ambient / endpoint;
        var off = session.Capture(color, Width, Height, surfaceStyles: map);
        var on = session.Capture(color, Width, Height, gi, surfaceStyles: map);
        AssertPixel(off, treated); AssertPixel(on, treated - treated * share * occlusion);
        AssertCoverage(off, on);
    }

    [Theory, MemberData(nameof(Families))]
    [Trait("Category", "Gpu")]
    public void NativeAndActualGiUseTheTreatedEndpointAndWarmParameterChangesAddNoReadbacksOrPipelines(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        var frame = Bounce(); var map = new D3D12SurfaceStyleMap([family, D3D12SurfaceStyle.Mmd],
            [new(PmxMaterialTreatment: new(.5f)), new()]);
        var lit = HsvOracle(new Vector3(.4f, .24f, .16f) * .5f, .5f);
        var dark = lit * Toon; var budget = lit * (Vector3.One - Toon);
        var options = new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: 20, SamplesPerPixel: 32, Seed: 17,
            MaxRayDistance: 50, RayEpsilon: .001f, DirectionalLightDirection: Vector3.UnitZ,
            DirectionalIrradiance: Vector3.Zero, DirectScale: 0, OcclusionStrength: 0, Composition: RayGiComposition.ToonPalette);
        var plain = session.Capture(frame, Width, Height, surfaceStyles: map);
        var bounce = session.Capture(frame, Width, Height, options, surfaceStyles: map);
        AssertPixel(plain, dark); AssertPixel(bounce, dark + budget * .25f);
        var native = new OriginalFrameScene(frame.Surface, options: frame.Options, materials: frame.Materials,
            lighting: new([new NativeSceneLight(Guid.NewGuid(), "ordinary fill", SceneLightKind.Directional, Vector3.Zero,
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), Vector3.One, .45f, 30, CastShadows: false)]));
        var direct = session.Capture(native, Width, Height, surfaceStyles: map);
        var combined = session.Capture(native, Width, Height, options, surfaceStyles: map);
        // The native light treats the material diffuse (twice the visible endpoint here) and, being fully lit and
        // below the shared shoulder, adds exactly; GI still spends only its palette budget.
        AssertPixel(direct, dark + lit * .9f); AssertPixel(combined, dark + budget * .25f + lit * .9f);
        Assert.True(Pixel(combined)[0] > Pixel(direct)[0] + 2, "The shared endpoint must still receive actual GI.");
        AssertCoverage(plain, bounce); AssertCoverage(direct, combined);
        var warm = session.Statistics!; long readbacks = session.Runtime.Readbacks;
        for (int i = 0; i < 4; i++)
        {
            var changed = new D3D12SurfaceStyleMap(map.Materials,
                [new(PmxMaterialTreatment: new(.5f + i * .1f, 1 + i * .05f)), new()]);
            using var output = session.Render(native, Width, Height, options, surfaceStyles: changed);
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Equal(warm.ShaderCompilations, output.Statistics.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations, output.Statistics.PipelineCreations);
            Assert.InRange(output.Statistics.CachedResources, 0, warm.CachedResources + 2);
        }
        Assert.Equal(readbacks, session.Runtime.Readbacks);
    }

    private static OriginalFrameScene Surface(Vector3 rgb, Vector3? normal = null, bool outside = false,
        CharacterToonResponse? response = null, bool textured = false, OriginalSphereMode sphere = OriginalSphereMode.None,
        bool straight = false, bool transparent = false, bool morph = false, OriginalTextureFactor? factors = null)
    {
        var vertices = Quad(normal ?? -Vector3.UnitZ);
        var scene = new SceneSnapshot(vertices, [0, 2, 1, 0, 3, 2],
            [new("explicit ordinary PMX", 0, 6, new(rgb, transparent ? .6f : 1), Vector3.Zero, Vector3.Zero, 1, textured ? 0 : -1)],
            [new("authored diffuse", 1, 1, [128, 192, 64, transparent ? (byte)200 : (byte)255]),
                new("authored sphere", 1, 1, [96, 160, 224, transparent ? (byte)220 : (byte)255])],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 30),
            lightDirection: normal.HasValue ? Vector3.UnitX : -Vector3.UnitZ,
            additionalUvs: sphere == OriginalSphereMode.SubTexture ? new SceneAdditionalUvs(1,
                Enumerable.Repeat(new SceneAdditionalUvVertex(new(.25f, .25f, 0, 0), default, default, default), 4).ToArray()) : null);
        var binding = new OriginalStyleMaterialBinding(Toon, sphere == OriginalSphereMode.None ? -1 : 1, sphere, true)
        {
            TextureFactors = OriginalMaterialTextureFactors.Identity with
            {
                Diffuse = factors ?? (morph ? new(new(.8f, .7f, .6f, 1), new(.1f, .05f, .02f, 0)) : OriginalMaterialTextureFactors.Identity.Diffuse),
                Toon = sphere == OriginalSphereMode.SubTexture ? new(new(1, 1, 1, .4f), Vector4.Zero) : OriginalMaterialTextureFactors.Identity.Toon,
            },
        };
        return new(new(scene, [binding], new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: Options() with { AlphaOutput = straight ? FrameAlphaOutput.Straight : FrameAlphaOutput.LegacyMax,
                SelfShadow = outside ? new(Matrix4x4.CreateTranslation(3, 0, 0), 32, 32, false, false,
                    OriginalTextureSampling.PointClamp, OriginalDepthComparison.LessEqual) : null },
            materials: [new(false, false, false) { CharacterToonResponse = response }]);
    }

    private static OriginalFrameScene Bounce()
    {
        var source = D3D12RayGiTests.Bounce(); var mesh = source.Surface.Snapshot;
        var vertices = mesh.Vertices.ToArray();
        for (int i = 0; i < 4; i++) vertices[i] = vertices[i] with { Normal = DarkNormal };
        var materials = mesh.Materials.ToArray();
        materials[0] = materials[0] with { Diffuse = new(.4f, .24f, .16f, 1), Ambient = Vector3.Zero, Specular = Vector3.Zero };
        materials[1] = materials[1] with { Emission = new(10) };
        var scene = new SceneSnapshot(vertices, mesh.Indices, materials, mesh.Textures, mesh.World, mesh.Camera, Vector3.UnitX);
        return new(new(scene, [new(Toon), source.Surface.Materials[1]],
            new(new(.5f), Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)), options: Options(),
            materials: [new(false, false, false) { GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands }, new(false, false, false)]);
    }

    // Conventional six-sector conversion is deliberately independent of the
    // generated shader's max/chroma affine identity. Preserve HDR V throughout.
    private static Vector3 HsvOracle(Vector3 rgb, float multiplier)
    {
        if (multiplier == 1 || MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z)) < 0) return rgb;
        float value = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
        float minimum = MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z)), chroma = value - minimum;
        if (chroma == 0) return rgb;
        float hue = value == rgb.X ? (rgb.Y - rgb.Z) / chroma : value == rgb.Y ? 2 + (rgb.Z - rgb.X) / chroma : 4 + (rgb.X - rgb.Y) / chroma;
        if (hue < 0) hue += 6;
        float saturation = Math.Clamp(chroma / value * multiplier, 0, 1);
        int sector = (int)MathF.Floor(hue); float f = hue - sector;
        float p = value * (1 - saturation), q = value * (1 - saturation * f), t = value * (1 - saturation * (1 - f));
        return sector switch { 0 => new(value, t, p), 1 => new(q, value, p), 2 => new(p, value, t),
            3 => new(p, q, value), 4 => new(t, p, value), _ => new(value, p, q) };
    }

    private static float Band(D3D12SurfaceStyle family, float dot, float threshold, float width)
    {
        float h = Math.Clamp(dot * .5f + .5f, 0, 1);
        return family switch
        {
            D3D12SurfaceStyle.GenshinPmx => Smooth(threshold - width * .5f, threshold + width * .5f, h),
            D3D12SurfaceStyle.StarRailPmx => Math.Clamp((h - (threshold - width)) / width, 0, 1),
            D3D12SurfaceStyle.WutheringPmx => Smooth(threshold * 2 - 1 - width * .5f, threshold * 2 - 1 + width * .5f, dot),
            D3D12SurfaceStyle.EndfieldPmx => .5f * Smooth(threshold - width * 1.25f, threshold - width * .75f, h)
                + .5f * Smooth(threshold + width * .75f, threshold + width * 1.25f, h),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };
    }

    private static float Smooth(float lower, float upper, float value)
    { float t = Math.Clamp((value - lower) / (upper - lower), 0, 1); return t * t * (3 - 2 * t); }
    private static OriginalFrameOptions Options() => new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero);
    private static SceneVertex[] Quad(Vector3 normal) => [new(new(-.8f, -.6f, 0), normal, new(.25f)), new(new(.8f, -.6f, 0), normal, new(.25f)),
        new(new(.8f, .6f, 0), normal, new(.25f)), new(new(-.8f, .6f, 0), normal, new(.25f))];
    private static D3D12SurfaceStyleMap Map(D3D12SurfaceStyle family, float? saturation = null, float width = 1)
        => new([family], [new(PmxMaterialTreatment: saturation.HasValue ? new(saturation.Value, width) : null)]);
    private static byte[] Pixel(RenderedFrame frame) => frame.Rgba.AsSpan((Height / 2 * Width + Width / 2) * 4, 4).ToArray();
    private static byte[] PixelAt(RenderedFrame frame, int x, int y) => frame.Rgba.AsSpan((y * Width + x) * 4, 4).ToArray();
    private static void AssertPixel(RenderedFrame frame, Vector3 rgb, float alpha = 1)
    {
        var pixel = Pixel(frame); var expected = Vector4.Clamp(new(rgb, alpha), Vector4.Zero, Vector4.One);
        for (int channel = 0; channel < 4; channel++)
            Assert.True(Math.Abs(pixel[channel] - expected[channel] * 255) <= 1.1f,
                $"channel {channel}: actual=({string.Join(',', pixel)}), expected={expected} * 255");
    }
    private static void AssertCoverage(RenderedFrame a, RenderedFrame b)
    { for (int i = 3; i < a.Rgba.Length; i += 4) Assert.Equal(a.Rgba[i], b.Rgba[i]); }
    private static int Occurrences(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;
    private static string Source(D3D12SurfaceStyle family, bool native, bool gi, bool morph, bool sub) => family switch
    {
        D3D12SurfaceStyle.GenshinPmx => OriginalFrameShaders.GenshinSurfaceSource(native, gi, morph, sub),
        D3D12SurfaceStyle.StarRailPmx => OriginalFrameShaders.StarRailSurfaceSource(native, gi, morph, sub),
        D3D12SurfaceStyle.WutheringPmx => OriginalFrameShaders.WutheringSurfaceSource(native, gi, morph, sub),
        D3D12SurfaceStyle.EndfieldPmx => OriginalFrameShaders.EndfieldSurfaceSource(native, gi, morph, sub),
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };
    private static byte[] Compile(D3D12SurfaceStyle family, string entry, bool native, bool gi, bool morph, bool sub) => family switch
    {
        D3D12SurfaceStyle.GenshinPmx => OriginalFrameShaders.CompileGenshinSurface(entry, native, gi, morph, sub),
        D3D12SurfaceStyle.StarRailPmx => OriginalFrameShaders.CompileStarRailSurface(entry, native, gi, morph, sub),
        D3D12SurfaceStyle.WutheringPmx => OriginalFrameShaders.CompileWutheringSurface(entry, native, gi, morph, sub),
        D3D12SurfaceStyle.EndfieldPmx => OriginalFrameShaders.CompileEndfieldSurface(entry, native, gi, morph, sub),
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };
}
