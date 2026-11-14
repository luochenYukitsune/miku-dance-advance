using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
[Trait("Category", "Gpu")]
public sealed class D3D12RayGiOpaqueSphereAlphaTests
{
    private const int Width = 33, Height = 25;

    [Theory]
    [InlineData(OriginalTextureSampling.PointClamp)]
    [InlineData(OriginalTextureSampling.LinearClamp)]
    [InlineData(OriginalTextureSampling.PointWrap)]
    [InlineData(OriginalTextureSampling.LinearWrap)]
    public void EveryActiveSphereModeKeepsFrozenVisibilityAcrossAllSamplers(OriginalTextureSampling sampling)
    {
        foreach (var mode in new[] { OriginalSphereMode.Multiply, OriginalSphereMode.Add, OriginalSphereMode.SubTexture })
            AssertEquivalent(Fixture(sampling, mode, alpha: .5f, cutoff: .5f));
    }

    [Theory]
    [InlineData(OriginalSphereMode.Multiply)]
    [InlineData(OriginalSphereMode.Add)]
    [InlineData(OriginalSphereMode.SubTexture)]
    public void OpaqueDiffuseMustStillSampleAndClipTheTransparentSphere(OriginalSphereMode mode)
    {
        AssertEquivalent(Fixture(OriginalTextureSampling.PointClamp, mode, sphereLastAlpha: 0));
        AssertEquivalent(Fixture(OriginalTextureSampling.LinearClamp, mode, alpha: .5f, cutoff: .25f, sphereLastAlpha: 128));
    }

    public static TheoryData<OriginalSphereMode, float, float> AlphaBoundaries => new()
    {
        { OriginalSphereMode.Multiply, 0, 0 },
        { OriginalSphereMode.Add, float.Epsilon, float.Epsilon },
        { OriginalSphereMode.SubTexture, BitConverter.Int32BitsToSingle(0x007fffff), BitConverter.Int32BitsToSingle(0x00800000) },
        { OriginalSphereMode.Multiply, .5f, MathF.BitIncrement(.5f) },
        { OriginalSphereMode.Add, .5f, MathF.BitDecrement(.5f) },
        { OriginalSphereMode.SubTexture, -1, .5f }
    };

    [Theory]
    [MemberData(nameof(AlphaBoundaries))]
    public void BoundaryAlphaKeepsTheOriginalDiffuseThenSphereMultiplications(OriginalSphereMode mode, float alpha, float cutoff)
        => AssertEquivalent(Fixture(OriginalTextureSampling.LinearWrap, mode, alpha: alpha, cutoff: cutoff));

    [Theory]
    [InlineData(OriginalSphereMode.Multiply)]
    [InlineData(OriginalSphereMode.Add)]
    [InlineData(OriginalSphereMode.SubTexture)]
    public void ATransparentDiffuseTexelDisablesTheProofWhileSphereSamplingContinues(OriginalSphereMode mode)
        => AssertEquivalent(Fixture(OriginalTextureSampling.PointWrap, mode, diffuseLastAlpha: 0, uv: .75f));

    [Fact]
    public void MaximumFiniteDiffuseUvAndWhiteFallbackMatchTheFrozenSampler()
    {
        AssertEquivalent(Fixture(OriginalTextureSampling.LinearWrap, OriginalSphereMode.Multiply, uv: float.MaxValue));
        AssertEquivalent(Fixture(OriginalTextureSampling.LinearWrap, OriginalSphereMode.SubTexture, missingDiffuse: true));
    }

    [Fact]
    public void ZeroWeightColourMorphsDoNotRemoveRawSphereAlphaClipping()
        => AssertEquivalent(Fixture(OriginalTextureSampling.PointWrap, OriginalSphereMode.Add, sphereLastAlpha: 0, morph: true));

    [Fact]
    public void ExplicitZeroFlagRetainsTheOriginalUv1SphereSample()
    {
        var frame = Fixture(OriginalTextureSampling.PointClamp, OriginalSphereMode.SubTexture, sphereLastAlpha: 0);
        AssertSame(Capture(frame, ShaderKind.FrozenLegacy, "zero-sphere-flag-legacy"),
            Capture(frame, ShaderKind.CurrentZeroFlags, "zero-sphere-flag-current"));
    }

    [Theory]
    [InlineData(OriginalSphereMode.Multiply)]
    [InlineData(OriginalSphereMode.Add)]
    public void MixedUv1VariantKeepsDiffuseProofAndIndependentSubTextureHole(OriginalSphereMode mode)
        => AssertEquivalent(Fixture(OriginalTextureSampling.PointClamp, mode, alpha: .25f, cutoff: .5f,
            sphereLastAlpha: 0, mixedSubTexture: true));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void BackFaceDisabledAndCoincidentMaterialGatesStayUnchanged(int gate)
        => AssertEquivalent(Fixture(OriginalTextureSampling.LinearWrap, OriginalSphereMode.Add,
            backFacing: gate == 0, disabled: gate == 1, coincident: gate == 2));

    [Theory]
    [InlineData(OriginalSphereMode.Multiply)]
    [InlineData(OriginalSphereMode.Add)]
    [InlineData(OriginalSphereMode.SubTexture)]
    public void FrozenOracleDetectsRemovingOnlyTheSphereAlphaSample(OriginalSphereMode mode)
    {
        var frame = Fixture(OriginalTextureSampling.PointClamp, mode, sphereLastAlpha: 0);
        var original = Capture(frame, ShaderKind.FrozenLegacy, "sphere-sensitive-legacy");
        var broken = Capture(frame, ShaderKind.BypassSphere, "sphere-sensitive-bypass");
        Assert.True(original.Visits > 0);
        Assert.False(original.Indirect.AsSpan().SequenceEqual(broken.Indirect),
            "Ignoring the sphere sample must change raw GI, including the UV1-only transparent texel.");
    }

    private static void AssertEquivalent(OriginalFrameScene frame, float strength = .5f)
    {
        var original = Capture(frame, ShaderKind.FrozenLegacy, "opaque-sphere-alpha-legacy", strength);
        var candidate = Capture(frame, ShaderKind.Current, "opaque-sphere-alpha-current", strength);
        AssertSame(original, candidate);
    }

    private static void AssertSame(CaptureResult original, CaptureResult candidate)
    {
        Assert.True(original.Visits > 0, "The fixture must traverse real secondary geometry.");
        Assert.Equal(original.Indirect, candidate.Indirect);
        Assert.Equal(original.Pixels, candidate.Pixels);
        // All four slots include overflow, secondary hits, shadow rays and total candidates.
        Assert.Equal(original.Counters, candidate.Counters);
    }

    private enum ShaderKind { Current, FrozenLegacy, CurrentZeroFlags, BypassSphere }
    private sealed record CaptureResult(byte[] Pixels, byte[] Indirect, byte[] Counters)
    {
        internal uint Visits => BitConverter.ToUInt32(Counters, 12);
    }

    private static CaptureResult Capture(OriginalFrameScene frame, ShaderKind kind, string label, float strength = .5f)
    {
        string? previousDiagnostics = Environment.GetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS");
        string? previousTiming = Environment.GetEnvironmentVariable("MMD_RAYGI_TIMING");
        string directory = Path.Combine(Environment.GetEnvironmentVariable("MMD_TIMING_HOOK_ARTIFACTS") ?? Path.GetTempPath(),
            label + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", directory);
            Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING", null);
            using var session = new D3D12FrameSession();
            var support = session.QueryRayGiCapabilities();
            Assert.True(support.HardwareAvailable, support.UnavailableReason);
            Assert.False(session.IsWarp);
            var options = new RayGiOptions(LightingMode.HardwareRayTracing, Strength: strength, SamplesPerPixel: 4, Seed: 17,
                MaxRayDistance: 1000, RayEpsilon: .001f, DirectionalLightDirection: Vector3.UnitZ,
                DirectionalIrradiance: new Vector3(3), Denoiser: RayGiDenoiser.Off);
            session.Capture(frame, Width, Height, rayGiOptions: options);

            const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
            var renderer = typeof(D3D12FrameSession).GetField("renderer", hidden)!.GetValue(session)!;
            var gi = renderer.GetType().GetField("rayGiRenderer", hidden)!.GetValue(renderer)!;
            var pipelines = (IDictionary)gi.GetType().GetField("computePipelines", hidden)!.GetValue(gi)!;
            var pipeline = Assert.Single(pipelines.Values.Cast<object>());
            var scene = RayGiScene.FromOriginal(frame);
            string source = D3D12RayGiShaders.Source(true, !scene.DiffuseTextureFactors.IsEmpty,
                nativeLights: false, subTexture: !scene.SubTextureUvs.IsEmpty);
            if (kind != ShaderKind.Current)
            {
                if (kind == ShaderKind.CurrentZeroFlags)
                    Assert.Contains("GiMaterial material=GiMaterials[tri.Meta.x];", CurrentAcceptance(source));
                string acceptance = kind == ShaderKind.CurrentZeroFlags
                    ? CurrentAcceptance(source).Replace("GiMaterial material=GiMaterials[tri.Meta.x];",
                        "GiMaterial material=GiMaterials[tri.Meta.x]; material.Flags=uint4(0,0,0,0);", StringComparison.Ordinal)
                    : LegacyAcceptance(!scene.SubTextureUvs.IsEmpty);
                if (kind == ShaderKind.BypassSphere)
                {
                    Assert.Contains(SphereMultiplyStatement(!scene.SubTextureUvs.IsEmpty), acceptance);
                    acceptance = acceptance.Replace(SphereMultiplyStatement(!scene.SubTextureUvs.IsEmpty), "", StringComparison.Ordinal);
                }
                source = ReplaceAcceptance(source, acceptance);
            }

            // Freeze traversal too. The previous traversal oracle alone would silently share a new AcceptCandidate.
            string trace = kind is ShaderKind.FrozenLegacy or ShaderKind.BypassSphere ? LegacyTrace : D3D12HardwareRayGi.TraceSource;
            trace = trace.Replace("while(query.Proceed()) {", "while(query.Proceed()) { InterlockedAdd(GiDiagnostics[3],1);", StringComparison.Ordinal);
            Assert.Contains(D3D12HardwareRayGi.TraceSource, source);
            source = source.Replace(D3D12HardwareRayGi.TraceSource, trace, StringComparison.Ordinal);
            var compiled = new DxcShaderCompiler().CompileCompute(source, "CSMain", "cs_6_5",
                new Dictionary<string, string?> { { "GI_HARDWARE", "1" }, { "GI_DIAGNOSTICS", "1" } });
            var root = (ID3D12RootSignature)pipeline.GetType().GetProperty("Root")!.GetValue(pipeline)!;
            var old = (ID3D12PipelineState)pipeline.GetType().GetProperty("State")!.GetValue(pipeline)!;
            var state = session.Runtime.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = root, ComputeShader = compiled.Dxil });
            pipeline.GetType().GetProperty("State")!.SetValue(pipeline, state);
            old.Dispose(); // Warm Capture already completed its fence; no live command list borrows this state.

            var pixels = session.Capture(frame, Width, Height, rayGiOptions: options);
            string artifacts = Path.Combine(directory, session.Statistics!.DeviceIdentity, "frame-" + session.Statistics.FrameId);
            byte[] counters = File.ReadAllBytes(Path.Combine(artifacts, "counters-0.bin"));
            byte[] indirect = File.ReadAllBytes(Path.Combine(artifacts, "indirect-0.bin"));
            Assert.Equal(16, counters.Length);
            Assert.Equal(Width * Height * 16, indirect.Length);
            File.WriteAllBytes(Path.Combine(artifacts, "pixels.rgba"), pixels.Rgba);
            File.WriteAllText(Path.Combine(artifacts, "comparison-input.json"), JsonSerializer.Serialize(new
            {
                label, strength, kind = kind.ToString(), compiled.SourceHash, compiled.OutputHash,
                flags = scene.Materials.ToArray().Select(m => new { m.Flags.X, m.Flags.Y, m.Flags.Z, m.Flags.W }).ToArray()
            }));
            return new(pixels.Rgba, indirect, counters);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", previousDiagnostics);
            Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING", previousTiming);
        }
    }

    private static string CurrentAcceptance(string source)
    {
        int start = source.IndexOf("bool AcceptCandidate(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = source.IndexOf("float3 GiGeometricNormal(", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source[start..end];
    }

    private static string ReplaceAcceptance(string source, string acceptance)
    {
        string current = CurrentAcceptance(source);
        return source.Replace(current, acceptance + "\n        ", StringComparison.Ordinal);
    }

    private static string LegacyAcceptance(bool subTexture)
    {
        // Reproduce the old Source() specialization after installing the frozen function.
        // UV0 and sphere projection deliberately differ from UV1 in the sensitivity fixture.
        const string sphere = "SampleTexture(material.SphereTexture,GiSphereUv(tri,bary),material.DiffuseTexture.w).a";
        return subTexture ? LegacyAccept.Replace(sphere,
            "SampleTexture(material.SphereTexture,material.SphereTexture.w==3?GiSubTextureUvAt(triangleIndex,bary):GiSphereUv(tri,bary),material.DiffuseTexture.w).a",
            StringComparison.Ordinal) : LegacyAccept;
    }

    private static string SphereMultiplyStatement(bool subTexture)
        => subTexture
            ? "if(material.SphereTexture.w!=0) alpha*=SampleTexture(material.SphereTexture,material.SphereTexture.w==3?GiSubTextureUvAt(triangleIndex,bary):GiSphereUv(tri,bary),material.DiffuseTexture.w).a;"
            : "if(material.SphereTexture.w!=0) alpha*=SampleTexture(material.SphereTexture,GiSphereUv(tri,bary),material.DiffuseTexture.w).a;";

    private static OriginalFrameScene Fixture(OriginalTextureSampling sampling, OriginalSphereMode sphere, float alpha = 1, float cutoff = .5f,
        byte diffuseLastAlpha = 255, byte sphereLastAlpha = 255, float uv = .375f, float uv1 = .75f,
        bool morph = false, bool missingDiffuse = false, bool coincident = false, bool disabled = false,
        bool backFacing = false, bool mixedSubTexture = false)
    {
        var vertices = new List<SceneVertex>();
        var indices = new List<uint>();
        var materials = new List<SceneMaterial>();
        var bindings = new List<OriginalStyleMaterialBinding>();
        void Quad(float z, float half, Vector3 normal, Vector3 emission, int texture, float materialAlpha, float materialCutoff,
            bool reverse = false, OriginalSphereMode sphereMode = OriginalSphereMode.None)
        {
            uint v = (uint)vertices.Count;
            int start = indices.Count;
            float coordinate = materials.Count == 1 ? uv : .375f;
            vertices.AddRange([new(new(-half, -half, z), normal, new(coordinate)), new(new(half, -half, z), normal, new(coordinate)),
                new(new(half, half, z), normal, new(coordinate)), new(new(-half, half, z), normal, new(coordinate))]);
            indices.AddRange(reverse ? [v, v + 2, v + 1, v, v + 3, v + 2] : [v, v + 1, v + 2, v, v + 2, v + 3]);
            materials.Add(new("surface-" + materials.Count, start, 6, new(.7f, .7f, .7f, materialAlpha), Vector3.Zero, Vector3.Zero,
                1, DiffuseTexture: texture, AlphaCutoff: materialCutoff) { Emission = emission });
            bindings.Add(new(Vector3.One, sphereMode == OriginalSphereMode.None ? -1 : 2, sphereMode));
        }
        Quad(0, 2, -Vector3.UnitZ, Vector3.Zero, 0, 1, .5f, reverse: true);
        // Ordinary sphere projection selects U=.75. In mode3 it selects U=.5 instead,
        // so only independent UV1=.75 reaches the final transparent texel.
        var secondaryNormal = sphere == OriginalSphereMode.SubTexture ? Vector3.UnitZ : new Vector3(.5f, 0, .8660254f);
        Quad(-8, 64, secondaryNormal, new(.2f, .6f, .1f), missingDiffuse ? -1 : 1, alpha, cutoff,
            reverse: backFacing, sphereMode: sphere);
        if (coincident) Quad(-8, 65, Vector3.UnitZ, new(.7f, .05f, .2f), -1, 1, .5f);
        if (mixedSubTexture) Quad(-9, 68, Vector3.UnitZ, new(.6f, .15f, .4f), -1, 1, .5f, sphereMode: OriginalSphereMode.SubTexture);
        Quad(-10, 72, Vector3.UnitZ, new(.1f, .1f, .7f), -1, 1, .5f);
        if (morph)
            bindings[1] = bindings[1] with { TextureFactors = OriginalMaterialTextureFactors.Identity with
            {
                Diffuse = new(new(2, .5f, .75f, 0), new(.1f, .2f, 0, 0)),
                Sphere = new(new(.5f, 2, .75f, 0), new(.1f, 0, .2f, 0))
            } };
        SceneTexture[] textures = [new("receiver", 1, 1, [192, 192, 192, 255]),
            new("multicolour diffuse", 2, 2, [5, 29, 233, 255, 239, 37, 13, 255, 53, 211, 67, 255, 101, 83, 191, diffuseLastAlpha]),
            new("sphere alpha", 4, 1, [128, 128, 128, 255, 128, 128, 128, 255, 128, 128, 128, 255, 128, 128, 128, sphereLastAlpha])];
        var extra = new SceneAdditionalUvs(1, Enumerable.Range(0, vertices.Count).Select(i =>
            new SceneAdditionalUvVertex(new(uv1, .5f, 100 + i, -100 - i), default, default, default)).ToArray());
        var mesh = new SceneSnapshot(vertices.ToArray(), indices.ToArray(), materials, textures,
            camera: new(new(0, 0, -6), Vector3.Zero, Vector3.UnitY, 4.5f, .1f, 100), lightDirection: Vector3.UnitZ, additionalUvs: extra);
        var surface = new OriginalStyleScene(mesh, bindings, new(new(.4f), Vector3.One, Matrix4x4.Identity, sampling));
        var flags = Enumerable.Repeat(new OriginalFrameMaterialFlags(false, false, false), materials.Count).ToArray();
        if (disabled) flags[1] = flags[1] with { SurfaceEnabled = false };
        return new(surface, options: new(OriginalAlphaBlendMode.Normal, true, false), materials: flags);
    }

    // Frozen pre-optimization visibility from d80/4379933. Never obtain this body from current Shared.
    private const string LegacyAccept = """
        bool AcceptCandidate(uint triangleIndex,float2 bary,float3 rayDirection) {
            GiTriangle tri=GiTriangles[triangleIndex]; GiMaterial material=GiMaterials[tri.Meta.x];
            if(material.Parameters.z==0) return false;
            bool front=dot(cross(tri.P1.xyz-tri.P0.xyz,tri.P2.xyz-tri.P0.xyz),rayDirection)<0;
            if(material.Parameters.y==0&&!front) return false;
            float alpha=min(material.DiffuseFactor.a,1)*SampleTexture(material.DiffuseTexture,GiDiffuseUv(tri,bary),material.DiffuseTexture.w).a;
            if(material.SphereTexture.w!=0) alpha*=SampleTexture(material.SphereTexture,GiSphereUv(tri,bary),material.DiffuseTexture.w).a;
            return alpha>=material.Parameters.x;
        }
        """;

    private const string LegacyTrace = """
        bool TraceClosest(GiRay ray, out GiHit hit) {
            RayDesc desc;
            desc.Origin=ray.Origin; desc.Direction=ray.Direction; desc.TMin=ray.TMin; desc.TMax=ray.TMax;
            RayQuery<RAY_FLAG_FORCE_NON_OPAQUE> query;
            query.TraceRayInline(GiHardwareScene,RAY_FLAG_NONE,0xff,desc);
            bool found=false; float closest=ray.TMax;
            while(query.Proceed()) {
                if(query.CandidateType()!=CANDIDATE_NON_OPAQUE_TRIANGLE) continue;
                uint triangleIndex=query.CandidatePrimitiveIndex();
                float2 barycentrics=query.CandidateTriangleBarycentrics();
                float distance=query.CandidateTriangleRayT();
                if(distance<ray.TMin||distance>closest) continue;
                if(!AcceptCandidate(triangleIndex,barycentrics,ray.Direction)) continue;
                found=true; closest=distance;
                GiTriangle tri=GiTriangles[triangleIndex];
                uint front=dot(cross(tri.P1.xyz-tri.P0.xyz,tri.P2.xyz-tri.P0.xyz),ray.Direction)<0?1:0;
                hit.TriangleIndex=triangleIndex; hit.T=distance; hit.Barycentrics=barycentrics; hit.FrontFace=front;
            }
            return found;
        }
        bool TraceAny(GiRay ray) {
            RayDesc desc;
            desc.Origin=ray.Origin; desc.Direction=ray.Direction; desc.TMin=ray.TMin; desc.TMax=ray.TMax;
            RayQuery<RAY_FLAG_FORCE_NON_OPAQUE> query;
            query.TraceRayInline(GiHardwareScene,RAY_FLAG_NONE,0xff,desc);
            while(query.Proceed()) {
                if(query.CandidateType()!=CANDIDATE_NON_OPAQUE_TRIANGLE) continue;
                if(AcceptCandidate(query.CandidatePrimitiveIndex(),query.CandidateTriangleBarycentrics(),ray.Direction))
                    return true;
            }
            return false;
        }
        """;
}
