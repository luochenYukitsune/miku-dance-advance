using Mmd.Modern.Effects;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>
/// Validation of the MME front-end against real effect packs.
/// </summary>
/// <remarks>
/// The corpus lives outside the repository (third-party freeware, and the project must not
/// redistribute it), so corpus-dependent facts skip when it is absent. Everything the parser
/// needs to be correct about is also pinned by synthetic fixtures that do live in-repo.
/// </remarks>
public sealed class MmeEffectsTests
{
    private const string DefaultCorpus = @"D:\desktop\mmd渲染";

    private static string? Corpus =>
        Directory.Exists(Environment.GetEnvironmentVariable("MMD_MME_CORPUS") ?? DefaultCorpus)
            ? Environment.GetEnvironmentVariable("MMD_MME_CORPUS") ?? DefaultCorpus
            : null;

    private static FxEffect ParseText(string text)
    {
        var diagnostics = new List<MmeDiagnostic>();
        var effect = FxParser.Parse(FxTokenizer.Tokenize(text), "synthetic.fx", diagnostics);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return effect;
    }

    [Fact]
    public void Parameters_bind_by_semantic_not_by_name()
    {
        var effect = ParseText("""
            float4x4 anything : WORLDVIEWPROJECTION;
            float4x4 other : world;
            texture MatTex : MATERIALTEXTURE;
            float4 Color : TOONCOLOR;
            sampler2D MatSampler = sampler_state { texture = <MatTex>; MinFilter = LINEAR; };
            """);

        Assert.Equal("WORLDVIEWPROJECTION", effect.FindParameter("anything")!.Semantic);
        Assert.Equal("world", effect.FindParameter("other")!.Semantic);
        Assert.True(effect.FindParameter("MatTex")!.IsTexture);
        Assert.True(effect.FindParameter("MatSampler")!.IsSampler);
        Assert.Single(effect.Textures);
    }

    [Fact]
    public void Technique_annotation_concatenates_string_literals_before_splitting()
    {
        var effect = ParseText("""
            texture ScnMap;
            texture DepthBuffer;
            float4 ClearColor;

            technique Blur <
                string MMDPass = "postprocess";
                bool UseTexture = false;
                string Script =
                    "RenderColorTarget0=ScnMap;"
                    "RenderDepthStencilTarget=DepthBuffer;"
                    "ClearSetColor=ClearColor;"
                    "Clear=Color;"
                    "Pass=BlurX;";
            > {
                pass BlurX {
                    VertexShader = compile vs_3_0 VS();
                    PixelShader = compile ps_3_0 PS();
                }
            }
            """);

        var technique = effect.FindTechnique("Blur")!;
        Assert.Equal("postprocess", technique.MmdPass);

        var commands = EffectScript.Parse(technique.Script);
        Assert.Equal(["RenderColorTarget0", "RenderDepthStencilTarget", "ClearSetColor", "Clear", "Pass"],
            commands.Select(c => c.Name));
        Assert.Equal("ScnMap", commands[0].Argument);
        Assert.Equal("BlurX", commands[4].Argument);

        var graphDiagnostics = new List<MmeDiagnostic>();
        MmeRenderGraph.Build(effect, technique, graphDiagnostics);
        Assert.DoesNotContain(graphDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Pass_declarations_are_recovered_with_shader_profiles()
    {
        var effect = ParseText("""
            technique Object < string MMDPass = "object"; > {
                pass P0 {
                    VertexShader = compile vs_2_0 Object_VS();
                    PixelShader = compile ps_2_0 Object_PS();
                    ZEnable = true;
                    CullMode = CCW;
                }
                pass P1 {
                    VertexShader = compile vs_3_0 Object_VS3();
                    PixelShader = compile ps_3_0 Object_PS3();
                }
            }
            """);

        var technique = effect.FindTechnique("Object")!;
        Assert.Equal(2, technique.Passes.Count);
        Assert.Equal(["vs_2_0", "ps_2_0"], technique.Passes[0].Profiles);
        Assert.Equal(["vs_3_0", "ps_3_0"], technique.Passes[1].Profiles);
        Assert.Equal("true", technique.Passes[0].States["ZEnable"]);
    }

    [Fact]
    public void Functions_and_structs_do_not_leak_into_parameters()
    {
        var effect = ParseText("""
            struct VS_IN {
                float4 Pos : POSITION;
                float3 Nrm : NORMAL;
            };

            float4 helper(float4 x) { return x * 2; }

            float4 World : WORLD;

            technique T < string MMDPass = "object"; > { pass P0 { } }
            """);

        Assert.Equal(["World"], effect.Parameters.Select(p => p.Name));
    }

    [Fact]
    public void Shift_jis_sources_decode_without_mojibake()
    {
        // "テスト" in Shift-JIS: 0x83 0x65 0x83 0x58 0x83 0x67
        byte[] bytes =
        [
            .. "// "u8,
            0x83, 0x65, 0x83, 0x58, 0x83, 0x67,
            .. "\ntechnique T < string MMDPass = \"object\"; > { pass P0 { } }"u8,
        ];

        var (text, fallback) = FxSourceText.Decode(bytes);
        Assert.True(fallback);
        Assert.Contains("テスト", text);

        var diagnostics = new List<MmeDiagnostic>();
        var effect = FxParser.Parse(FxTokenizer.Tokenize(text), "sjis.fx", diagnostics);
        Assert.Equal("T", effect.Techniques.Single().Name);
    }

    [Fact]
    public void Preprocessor_joins_includes_and_expands_object_macros()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mme-pre-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "lib.fxsub"), """
                float4x4 M : WORLDVIEWPROJECTION;
                technique LibTech < string MMDPass = "object"; > { pass P0 { } }
                """);
            File.WriteAllText(Path.Combine(dir, "root.fx"), """
                #include "lib.fxsub"
                #define THRESHOLD "Main.png"
                string TexName = THRESHOLD;
                technique MainTech < string MMDPass = "object"; > { pass P0 { } }
                """);

            var result = EffectLibrary.Load(Path.Combine(dir, "root.fx"));
            Assert.NotNull(result.Effect);
            Assert.Equal(2, result.Effect!.Techniques.Count);
            Assert.Contains(result.Includes, i => i.EndsWith("lib.fxsub", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("\"Main.png\"", result.Effect.FindParameter("TexName")!.Initializer);
            var imported = EffectLibrary.Import(Path.Combine(dir, "root.fx"));
            Assert.Equal("Main.png", imported.Definition!.FindParameter("TexName")!.DefaultValue!.Value.Text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Conditional_directives_keep_the_live_branch()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mme-if-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "root.fx"), """
                #define HIGHQUALITY
                #ifdef HIGHQUALITY
                technique High < string MMDPass = "object"; > { pass P0 { } }
                #else
                technique Low < string MMDPass = "object"; > { pass P0 { } }
                #endif
                """);

            var result = EffectLibrary.Load(Path.Combine(dir, "root.fx"));
            Assert.Equal("High", result.Effect!.Techniques.Single().Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Graph_reports_the_same_errors_the_runtime_reports()
    {
        var effect = ParseText("""
            texture ScnMap;
            float DepthBuffer;

            technique Bad < string Script =
                "RenderColorTarget0=Missing;"
                "ClearSetColor=DepthBuffer;"
                "ClearSetDepth=ScnMap;"
                "Pass=NoSuchPass;"
                "Clear=Everything;"; > {
                pass Real { }
            }
            """);

        var diagnostics = new List<MmeDiagnostic>();
        MmeRenderGraph.Build(effect, effect.FindTechnique("Bad")!, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == MmeDiagnosticCodes.UnknownTextureName);
        Assert.Contains(diagnostics, d => d.Code == MmeDiagnosticCodes.InvalidClearColorType);
        Assert.Contains(diagnostics, d => d.Code == MmeDiagnosticCodes.InvalidClearDepthType);
        Assert.Contains(diagnostics, d => d.Code == MmeDiagnosticCodes.UnknownPassName);
        Assert.Contains(diagnostics, d => d.Code == MmeDiagnosticCodes.InvalidClearType);
    }

    [Fact]
    public void Contract_covers_the_semantics_the_corpus_actually_uses()
    {
        string? corpus = Corpus;
        if (corpus is null)
        {
            Assert.Skip("MME corpus not present");
            return;
        }

        var results = EffectLibrary.Scan(corpus, preprocess: false);
        var used = results
            .SelectMany(r => r.Effect?.Parameters ?? [])
            .Select(p => p.Semantic)
            .Where(s => s is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missing = used.Where(s => !MmeContract.IsKnownSemantic(s!)).OrderBy(s => s).ToList();
        Assert.True(missing.Count == 0, "semantics missing from the contract: " + string.Join(", ", missing));
    }

    [Fact]
    public void Pass_entry_points_and_source_text_are_retained_for_shader_compilation()
    {
        const string text = """
            texture MatTex;
            sampler2D MatSampler = sampler_state { texture = <MatTex>; MinFilter = LINEAR; };
            float4 main_ps(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(MatSampler, uv); }

            technique T < string MMDPass = "object"; > {
                pass P0 {
                    VertexShader = compile vs_3_0 main_vs();
                    PixelShader  = compile ps_2_0 main_ps();
                }
            }
            """;

        var diagnostics = new List<MmeDiagnostic>();
        var effect = FxParser.Parse(FxTokenizer.Tokenize(text), "entry.fx", diagnostics, text);

        // Shader bodies are skipped structurally but must survive verbatim: compiling a pass needs them.
        Assert.Contains("tex2D(MatSampler, uv)", effect.Source, StringComparison.Ordinal);

        var pass = effect.Techniques.Single().Passes.Single();
        // The parser stores pass states as whitespace-joined tokens, so the entry arrives without
        // its parentheses: exactly the identifier a compiler expects for -E.
        Assert.Equal("main_vs", pass.VertexEntry);
        Assert.Equal("main_ps", pass.PixelEntry);
        Assert.Equal(["vs_3_0", "ps_2_0"], pass.Profiles);
    }

    [Fact]
    public void Every_corpus_pass_exposes_its_shader_entry_points()
    {
        string? corpus = Corpus;
        if (corpus is null)
        {
            Assert.Skip("MME corpus not present");
            return;
        }

        int vertexPasses = 0, pixelPasses = 0;
        foreach (var result in EffectLibrary.Scan(corpus, preprocess: false))
        {
            foreach (var technique in result.Effect?.Techniques ?? [])
            {
                foreach (var pass in technique.Passes)
                {
                    if (pass.States.ContainsKey("VertexShader"))
                    {
                        vertexPasses++;
                        Assert.False(string.IsNullOrEmpty(pass.VertexEntry),
                            $"{result.Path}::{technique.Name}::{pass.Name} has no vertex entry point");
                    }

                    if (pass.States.ContainsKey("PixelShader"))
                    {
                        pixelPasses++;
                        Assert.False(string.IsNullOrEmpty(pass.PixelEntry),
                            $"{result.Path}::{technique.Name}::{pass.Name} has no pixel entry point");
                    }
                }
            }
        }

        Assert.True(vertexPasses >= 100, $"vertex passes {vertexPasses}");
        Assert.True(pixelPasses >= 100, $"pixel passes {pixelPasses}");
    }

    [Fact]
    public void Corpus_scan_reproduces_the_reference_histogram()
    {
        string? corpus = Corpus;
        if (corpus is null)
        {
            Assert.Skip("MME corpus not present");
            return;
        }

        // Reference numbers were cross-checked against an independent implementation
        // (tools/corpus_contract.py) for the structural facts both can compute: file count,
        // technique count and the MMDPass histogram agree exactly. Script payload statistics come
        // from this parser, which resolves multi-literal annotations and #if branches that the
        // quick Python scan truncates.
        var summary = EffectScanSummary.From(EffectLibrary.Scan(corpus, preprocess: false));

        Assert.Equal(33, summary.Files);
        Assert.Equal(194, summary.Techniques);
        Assert.Equal(0, summary.Errors);

        Assert.Equal(76, summary.MmdPassHistogram["object"]);
        Assert.Equal(67, summary.MmdPassHistogram["object_ss"]);
        Assert.Equal(16, summary.MmdPassHistogram["edge"]);
        Assert.Equal(16, summary.MmdPassHistogram["shadow"]);
        Assert.Equal(11, summary.MmdPassHistogram["zplot"]);

        Assert.Equal(186, summary.AnnotationHistogram["MMDPass"]);
        Assert.Equal(140, summary.AnnotationHistogram["UseTexture"]);
        Assert.Equal(112, summary.AnnotationHistogram["UseSphereMap"]);
        Assert.Equal(104, summary.AnnotationHistogram["UseToon"]);
        Assert.Equal(16, summary.AnnotationHistogram["UseSelfShadow"]);
        Assert.True(summary.AnnotationHistogram["Script"] >= 28,
            $"script count {summary.AnnotationHistogram["Script"]}");

        Assert.Equal(8, summary.ScriptCommandHistogram["ScriptExternal"]);
        Assert.Equal(4, summary.ScriptCommandHistogram["RenderColorTarget1"]);
        Assert.Equal(2, summary.ScriptCommandHistogram["LoopByCount"]);
        Assert.Equal(2, summary.ScriptCommandHistogram["LoopGetIndex"]);
        Assert.Equal(2, summary.ScriptCommandHistogram["LoopEnd"]);
        Assert.True(summary.ScriptCommandHistogram["Pass"] >= 28);
        Assert.True(summary.ScriptCommandHistogram["RenderColorTarget0"] >= 29);

        Assert.Equal(186, summary.ProfileHistogram["vs_3_0"]);
        Assert.Equal(186, summary.ProfileHistogram["ps_3_0"]);
        Assert.Equal(98, summary.ProfileHistogram["vs_2_0"]);
        Assert.Equal(98, summary.ProfileHistogram["ps_2_0"]);
    }
}
