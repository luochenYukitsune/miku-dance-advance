using System.Numerics;
using Mmd.Modern.Effects;
using Mmd.Modern.Effects.Neo;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>
/// Planning and execution of an <see cref="EffectDefinition"/> without a GPU, plus the semantic
/// binder that is the prerequisite for visual parity.
/// </summary>
public sealed class NeoEffectRuntimeTests
{
    private const string DefaultCorpus = @"D:\desktop\mmd渲染";

    private static string? Corpus =>
        Directory.Exists(Environment.GetEnvironmentVariable("MMD_MME_CORPUS") ?? DefaultCorpus)
            ? Environment.GetEnvironmentVariable("MMD_MME_CORPUS") ?? DefaultCorpus
            : null;

    private sealed class RecordingDevice : IEffectDevice
    {
        public List<string> Calls { get; } = [];

        public EffectTextureAllocation? LastColorTarget { get; private set; }

        public void SetRenderTarget(int slot, EffectTextureAllocation? texture)
        {
            LastColorTarget = texture;
            Calls.Add($"rt{slot}:{texture?.Name ?? "-"}:{texture?.Format}:{texture?.ViewportRatio.X}");
        }

        public void SetDepthStencilTarget(EffectTextureAllocation? texture) =>
            Calls.Add($"depth:{texture?.Name ?? "-"}:{texture?.Format}");

        public void ClearColor(Vector4 color) => Calls.Add($"clearColor:{color.X},{color.Y}");

        public void ClearDepth(float depth) => Calls.Add($"clearDepth:{depth}");

        public void Clear(ClearTargets targets) => Calls.Add($"clear:{targets}");

        public void DrawScene() => Calls.Add("scene");

        public void DrawPass(EffectPass pass) => Calls.Add($"pass:{pass.Name}");
    }

    private sealed class BloomEffect : NeoEffect
    {
        public BloomEffect()
            : base("Bloom")
        {
        }

        public override void Configure(EffectBuilder effect)
        {
            effect.Texture("ScnMap", EffectTextureRole.RenderTarget, "RENDERCOLORTARGET", new Dictionary<string, string>
            {
                ["Format"] = "R16F",
                ["ViewPortRatio"] = "{ 1.0 , 1.0 }",
            });
            effect.Texture("DepthBuffer", EffectTextureRole.DepthStencilTarget, "RENDERDEPTHSTENCILTARGET");
            effect.Vector4("ClearColor");
            effect.Scalar("Depth");
            effect.Integer("Steps");
            effect.Integer("Index");

            effect.Technique("Post", "postprocess")
                .Pass("BlurX", "compile vs_3_0 VS()", "compile ps_3_0 PS()", ["vs_3_0", "ps_3_0"])
                .RenderTarget(0, "ScnMap")
                .DepthStencil("DepthBuffer")
                .ClearColor("ClearColor")
                .ClearDepth("Depth")
                .Clear(ClearTargets.Color)
                .Scene()
                .Loop("Steps", "Index", body => body
                    .RenderTarget(0, "ScnMap")
                    .Clear(ClearTargets.Color)
                    .Run("BlurX"))
                .Run("BlurX")
                .End();
        }
    }

    [Fact]
    public void Plan_unrolls_loops_with_the_bound_count_and_carries_the_index()
    {
        var definition = new BloomEffect().Build();
        var values = EffectParameterValues.CreateDefaults(definition);
        values.Set("Steps", EffectValue.FromInteger(3));

        var diagnostics = new List<MmeDiagnostic>();
        var plan = new NeoEffectRuntime(definition, values, diagnostics).Plan(definition.FindTechnique("Post")!);

        Assert.Empty(diagnostics);

        // Body is RenderTarget + Clear + RunPass, unrolled three times.
        Assert.Equal(9, plan.Steps.Count(s => s.LoopIndexParameter is not null));
        Assert.Equal(
            new[] { 0, 0, 0, 1, 1, 1, 2, 2, 2 },
            plan.Steps.Where(s => s.LoopIndexParameter is not null).Select(s => s.LoopIndex).ToArray());
        Assert.Equal(3, plan.Steps.Count(s => s.Kind == EffectOperationKind.RunPass && s.LoopIndexParameter is not null));
    }

    [Fact]
    public void Execute_drives_the_device_in_graph_order_with_resolved_targets()
    {
        var definition = new BloomEffect().Build();
        var values = EffectParameterValues.CreateDefaults(definition);
        values.Set("Steps", EffectValue.FromInteger(0));
        values.Set("ClearColor", EffectValue.FromVector(new Vector4(0.25f, 0.5f, 0.75f, 1f)));
        values.Set("Depth", EffectValue.FromScalar(1f));

        var diagnostics = new List<MmeDiagnostic>();
        var runtime = new NeoEffectRuntime(definition, values, diagnostics);
        var plan = runtime.Plan(definition.FindTechnique("Post")!);

        var device = new RecordingDevice();
        runtime.Execute(plan, device);

        Assert.Equal(
            [
                "rt0:ScnMap:R16_Float:1",
                "depth:DepthBuffer:D24S8",
                "clearColor:0.25,0.5",
                "clearDepth:1",
                "clear:Color",
                "scene",
                "pass:BlurX",
            ],
            device.Calls);

        Assert.Equal(EffectRenderTargetFormat.R16_Float, device.LastColorTarget!.Format);
        Assert.Single(plan.Textures, t => t.Role == EffectTextureRole.RenderTarget);
    }

    [Fact]
    public void Missing_values_are_reported_and_error_plans_cannot_execute()
    {
        var definition = new BloomEffect().Build();
        var values = new EffectParameterValues();
        values.Set("Steps", EffectValue.FromInteger(0));

        var diagnostics = new List<MmeDiagnostic>();
        var runtime = new NeoEffectRuntime(definition, values, diagnostics);
        var plan = runtime.Plan(definition.FindTechnique("Post")!);

        Assert.Contains(diagnostics, d => d.Message.Contains("clear colour parameter", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("clear depth parameter", StringComparison.Ordinal));

        var device = new RecordingDevice();
        Assert.False(plan.IsExecutable);
        Assert.Throws<InvalidOperationException>(() => runtime.Execute(plan, device));
        Assert.Empty(device.Calls);
    }

    [Fact]
    public void Semantic_binder_fills_matrices_vectors_and_scalars()
    {
        var definition = new BloomEffect().Build();
        var frame = new EffectFrameState
        {
            World = Matrix4x4.CreateTranslation(1f, 2f, 3f),
            View = Matrix4x4.CreateLookAt(new Vector3(0f, 0f, -10f), Vector3.Zero, Vector3.UnitY),
            Projection = Matrix4x4.CreatePerspectiveFieldOfView(0.7f, 16f / 9f, 0.1f, 100f),
            Material = new EffectMaterialState
            {
                Diffuse = new Vector4(0.8f, 0.7f, 0.6f, 1f),
                ToonColor = new Vector4(0.1f, 0.2f, 0.3f, 1f),
                SpecularPower = 12f,
            },
            Time = 1.5f,
            ViewportPixelSize = new Vector2(1280f, 720f),
        };

        // A dedicated effect so every semantic family is exercised at once.
        var api = new NeoEffectApiProbe().Build();
        var values = new EffectParameterValues();
        var diagnostics = new List<MmeDiagnostic>();
        var result = SemanticBinder.Bind(api, frame, values, diagnostics);

        Assert.Empty(result.UnknownSemantics);
        Assert.Empty(diagnostics);

        Matrix4x4 expected = frame.World * frame.View * frame.Projection;
        Assert.True(values.TryGet("Wvp", out var wvp));
        Assert.Equal(expected, wvp.Matrix);

        Assert.True(values.TryGet("WorldView", out var wv));
        Assert.Equal(frame.World * frame.View, wv.Matrix);

        Assert.True(values.TryGet("WvpInverse", out var inverse));
        Assert.True(Matrix4x4.Invert(expected, out var expectedInverse));
        Assert.Equal(expectedInverse, inverse.Matrix);

        Assert.True(values.TryGet("Toon", out var toon));
        Assert.Equal(frame.Material.ToonColor, toon.AsVector4());

        Assert.True(values.TryGet("Viewport", out var viewport));
        Assert.Equal(frame.ViewportPixelSize, new Vector2(viewport.Vector.X, viewport.Vector.Y));

        Assert.True(values.TryGet("SpecPower", out var power));
        Assert.Equal(12f, power.AsFloat());

        Assert.Equal(0, result.UnboundTextures);
        Assert.True(result.Bound >= 9);
    }

    [Fact]
    public void Semantic_binder_reports_unknown_semantics_once()
    {
        var definition = new BloomEffect().Build();
        var values = new EffectParameterValues();
        var diagnostics = new List<MmeDiagnostic>();

        var result = SemanticBinder.Bind(definition, new EffectFrameState(), values, diagnostics);

        // Bloom declares RENDERCOLORTARGET / RENDERDEPTHSTENCILTARGET, which are resource roles
        // rather than frame values; the binder must not bind them and must not spam diagnostics
        // beyond one warning per unknown name.
        Assert.True(result.UnknownSemantics.Count <= 2);
        Assert.Equal(result.UnknownSemantics.Count, diagnostics.Count);
    }

    private sealed class NeoEffectApiProbe : NeoEffect
    {
        public NeoEffectApiProbe()
            : base("Probe")
        {
        }

        public override void Configure(EffectBuilder effect)
        {
            effect.Matrix("WorldView", "WORLDVIEW");
            effect.Matrix("Wvp", "WORLDVIEWPROJECTION");
            effect.Matrix("WvpInverse", "WORLDVIEWPROJECTIONINVERSE");
            effect.Vector4("Toon", "TOONCOLOR");
            effect.Vector4("Diffuse", "DIFFUSE");
            effect.Vector4("Edge", "EDGECOLOR");
            effect.Vector4("GroundShadow", "GROUNDSHADOWCOLOR");
            effect.Scalar("SpecPower", "SPECULARPOWER");
            effect.Scalar("TimeValue", "TIME");
            effect.Vector4("Viewport", "VIEWPORTPIXELSIZE");
            effect.Vector4("CameraPos", "POSITION");
        }
    }

    [Fact]
    public void Corpus_plans_with_declared_defaults_without_errors()
    {
        string? corpus = Corpus;
        if (corpus is null)
        {
            Assert.Skip("MME corpus not present");
            return;
        }

        var loads = EffectLibrary.Scan(corpus, preprocess: false);
        int techniques = 0, steps = 0, runPass = 0, scene = 0;

        foreach (var load in loads)
        {
            if (load.Effect is null)
            {
                continue;
            }

            var diagnostics = new List<MmeDiagnostic>();
            var definition = new ImportedEffect(load.Effect, diagnostics).Build();
            var values = EffectParameterValues.CreateDefaults(definition);
            var runtime = new NeoEffectRuntime(definition, values, diagnostics);

            foreach (var technique in definition.Techniques)
            {
                var plan = runtime.Plan(technique);
                techniques++;
                steps += plan.Steps.Count;
                runPass += plan.Count(EffectOperationKind.RunPass);
                scene += plan.Count(EffectOperationKind.DrawScene);

                Assert.All(plan.Steps.Where(s => s.Kind == EffectOperationKind.RunPass),
                    step => Assert.NotNull(step.Pass));
            }

            Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        }

        Assert.Equal(194, techniques);
        // The structural import suite still checks all 107 pass commands. AutoLuminous's
        // declared zero loop count now omits two passes that preview Count=1 used to invent.
        Assert.Equal(105, runPass);
        Assert.Equal(8, scene);
        Assert.True(steps > 400, $"planned steps {steps}");
    }
}
