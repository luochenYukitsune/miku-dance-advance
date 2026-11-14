using Mmd.Modern.Effects;
using Mmd.Modern.Effects.Neo;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>
/// The C# effect API and the legacy translation layer that feeds it.
/// </summary>
/// <remarks>
/// The point of routing imported <c>.fx</c> through the public effect API is dogfooding: if a
/// legacy effect can be expressed with the same calls a hand-written effect uses, the API is
/// proven expressive enough instead of being a parallel abstraction that only the importer knows.
/// </remarks>
public sealed class NeoEffectImportTests
{
    private const string DefaultCorpus = @"D:\desktop\mmd渲染";

    private static string? Corpus =>
        Directory.Exists(Environment.GetEnvironmentVariable("MMD_MME_CORPUS") ?? DefaultCorpus)
            ? Environment.GetEnvironmentVariable("MMD_MME_CORPUS") ?? DefaultCorpus
            : null;

    private sealed class HandWrittenEffect : NeoEffect
    {
        public HandWrittenEffect()
            : base("HandWritten")
        {
        }

        public override void Configure(EffectBuilder effect)
        {
            effect.Texture("ScnMap", EffectTextureRole.RenderTarget, "RENDERCOLORTARGET");
            effect.Texture("DepthBuffer", EffectTextureRole.DepthStencilTarget, "RENDERDEPTHSTENCILTARGET");
            effect.Vector4("ClearColor");
            effect.Matrix("WorldViewProjMatrix", "WORLDVIEWPROJECTION");

            effect.Technique("Post", "postprocess")
                .Pass("BlurX", "compile vs_3_0 VS()", "compile ps_3_0 PS()", ["vs_3_0", "ps_3_0"])
                .RenderTarget(0, "ScnMap")
                .DepthStencil("DepthBuffer")
                .ClearColor("ClearColor")
                .Clear(ClearTargets.Color | ClearTargets.Depth)
                .Scene()
                .Run("BlurX")
                .End();
        }
    }

    private static int Count(IReadOnlyList<EffectOperation> graph, EffectOperationKind kind) =>
        graph.Sum(op => (op.Kind == kind ? 1 : 0)
            + (op.Body is null ? 0 : Count(op.Body, kind)));

    private static ImportResult ImportText(string text)
    {
        var diagnostics = new List<MmeDiagnostic>();
        var effect = FxParser.Parse(FxTokenizer.Tokenize(text), "synthetic.fx", diagnostics);
        var definition = new ImportedEffect(effect, diagnostics).Build();
        return new ImportResult("synthetic.fx", definition, diagnostics);
    }

    [Fact]
    public void Hand_written_effect_declares_parameters_and_graph_through_the_public_api()
    {
        var definition = new HandWrittenEffect().Build();

        Assert.Equal("HandWritten", definition.Name);
        Assert.Equal(4, definition.Parameters.Count);
        Assert.Equal(EffectTextureRole.RenderTarget, definition.FindParameter("ScnMap")!.Role);
        Assert.Equal(EffectValueKind.Matrix, definition.FindParameter("WorldViewProjMatrix")!.Kind);

        var technique = definition.FindTechnique("Post")!;
        Assert.Equal("postprocess", technique.MmdPass);
        Assert.Equal(
            [
                EffectOperationKind.SetRenderTarget,
                EffectOperationKind.SetDepthStencilTarget,
                EffectOperationKind.ClearColor,
                EffectOperationKind.Clear,
                EffectOperationKind.DrawScene,
                EffectOperationKind.RunPass,
            ],
            technique.Graph.Select(op => op.Kind));

        Assert.Equal(ClearTargets.Color | ClearTargets.Depth, technique.Graph[3].ClearMask);
        Assert.Equal("ScnMap", technique.Graph[0].Target);
        Assert.Equal(0, technique.Graph[0].Slot);
    }

    [Fact]
    public void Legacy_script_translates_into_the_same_operations()
    {
        var result = ImportText("""
            texture2D ScnMap : RENDERCOLORTARGET;
            texture2D DepthBuffer : RENDERDEPTHSTENCILTARGET;
            float4 ClearColor;

            technique Blur < string Script =
                "RenderColorTarget0=ScnMap;"
                "RenderDepthStencilTarget=DepthBuffer;"
                "ClearSetColor=ClearColor;"
                "Clear=Color; Clear=Depth;"
                "ScriptExternal=Color;"
                "Pass=BlurX;"; > {
                pass BlurX {
                    VertexShader = compile vs_3_0 VS();
                    PixelShader = compile ps_3_0 PS();
                }
            }
            """);

        Assert.True(result.Success);
        var technique = result.Definition!.FindTechnique("Blur")!;
        Assert.Equal(
            [
                EffectOperationKind.SetRenderTarget,
                EffectOperationKind.SetDepthStencilTarget,
                EffectOperationKind.ClearColor,
                EffectOperationKind.Clear,
                EffectOperationKind.Clear,
                EffectOperationKind.DrawScene,
                EffectOperationKind.RunPass,
            ],
            technique.Graph.Select(op => op.Kind));

        // The script clears twice (`Clear=Color;` then `Clear=Depth;`); the graph keeps both
        // operations rather than merging them, so the translation stays 1:1 with the source.
        Assert.Equal(ClearTargets.Color, technique.Graph[3].ClearMask);
        Assert.Equal(ClearTargets.Depth, technique.Graph[4].ClearMask);
        Assert.Equal("BlurX", technique.Graph[6].Target);

        Assert.Equal(EffectTextureRole.RenderTarget, result.Definition.FindParameter("ScnMap")!.Role);
        Assert.Equal(EffectTextureRole.DepthStencilTarget, result.Definition.FindParameter("DepthBuffer")!.Role);
    }

    [Fact]
    public void Loop_commands_become_one_loop_operation_with_a_body()
    {
        var result = ImportText("""
            texture2D ScnMap;
            int Glare;
            int LoopIndex;
            float4 ClearColor;

            technique LoopTec < string Script =
                "LoopByCount=Glare;"
                "LoopGetIndex=LoopIndex;"
                "RenderColorTarget0=ScnMap;"
                "Clear=Color;"
                "LoopEnd=;"; > {
                pass P0 { }
            }
            """);

        Assert.True(result.Success);
        var graph = result.Definition!.FindTechnique("LoopTec")!.Graph;
        Assert.Single(graph);
        Assert.Equal(EffectOperationKind.Loop, graph[0].Kind);
        Assert.Equal("Glare", graph[0].Target);
        Assert.Equal("LoopIndex", graph[0].Source);
        Assert.Equal(2, graph[0].Body!.Count);
        Assert.Equal(EffectOperationKind.SetRenderTarget, graph[0].Body![0].Kind);
    }

    [Fact]
    public void Empty_target_argument_detaches_the_slot()
    {
        var result = ImportText("""
            texture2D ScnMap;
            technique T < string Script = "RenderColorTarget1=;"; > { pass P0 { } }
            """);

        Assert.True(result.Success);
        var op = result.Definition!.FindTechnique("T")!.Graph.Single();
        Assert.Equal(EffectOperationKind.SetRenderTarget, op.Kind);
        Assert.Equal(1, op.Slot);
        Assert.Null(op.Target);
    }

    [Fact]
    public void Unmappable_constructs_are_reported_not_silently_dropped()
    {
        var result = ImportText("""
            technique T < string Script = "Teleport=Nowhere; Pass=Missing;"; > { pass P0 { } }
            """);

        Assert.Contains(result.Diagnostics, d => d.Code == MmeDiagnosticCodes.UnsupportedScriptCommand);
        Assert.Contains(result.Diagnostics, d => d.Code == MmeDiagnosticCodes.UnknownPassName);
        Assert.False(result.Success);
    }

    [Fact]
    public void Corpus_imports_completely_and_preserves_every_graph_command()
    {
        string? corpus = Corpus;
        if (corpus is null)
        {
            Assert.Skip("MME corpus not present");
            return;
        }

        var loads = EffectLibrary.Scan(corpus, preprocess: false);
        int techniques = 0, runPass = 0, drawScene = 0, loops = 0, errors = 0;

        foreach (var load in loads)
        {
            if (load.Effect is null)
            {
                continue;
            }

            var diagnostics = new List<MmeDiagnostic>();
            var definition = new ImportedEffect(load.Effect, diagnostics).Build();
            errors += diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);

            Assert.Equal(load.Effect.Techniques.Count, definition.Techniques.Count);
            for (int i = 0; i < definition.Techniques.Count; i++)
            {
                var imported = definition.Techniques[i];
                var parsed = load.Effect.Techniques[i];
                techniques++;

                // Every command the parser accepted must appear in the graph exactly once.
                int expected = Efc(parsed);
                int actual = Count(imported.Graph, EffectOperationKind.SetRenderTarget)
                    + Count(imported.Graph, EffectOperationKind.SetDepthStencilTarget)
                    + Count(imported.Graph, EffectOperationKind.ClearColor)
                    + Count(imported.Graph, EffectOperationKind.ClearDepth)
                    + Count(imported.Graph, EffectOperationKind.Clear)
                    + Count(imported.Graph, EffectOperationKind.DrawScene)
                    + Count(imported.Graph, EffectOperationKind.RunPass)
                    + Count(imported.Graph, EffectOperationKind.Loop);
                Assert.Equal(expected, actual);

                runPass += Count(imported.Graph, EffectOperationKind.RunPass);
                drawScene += Count(imported.Graph, EffectOperationKind.DrawScene);
                loops += Count(imported.Graph, EffectOperationKind.Loop);
            }
        }

        Assert.Equal(194, techniques);
        Assert.Equal(0, errors);
        Assert.Equal(107, runPass);
        Assert.Equal(8, drawScene);
        Assert.Equal(2, loops);
    }

    /// <summary>
    /// Graph operations a parsed technique should produce: every command becomes one operation,
    /// except the loop bookkeeping commands, which are consumed by their surrounding loop.
    /// Body commands still count because the importer nests them inside the loop operation.
    /// </summary>
    private static int Efc(FxTechnique technique) =>
        EffectScript.Parse(technique.Script)
            .Count(c => c.Name.ToLowerInvariant() is not ("loopgetindex" or "loopend"));
}
