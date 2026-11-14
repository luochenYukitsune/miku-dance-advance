using System.Numerics;
using Mmd.Modern.Effects;
using Mmd.Modern.Effects.Neo;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class MmeSemanticRegressionTests
{
    [Fact]
    public void Corpus_expression_initializers_are_preserved_without_invented_values()
    {
        string corpus = Environment.GetEnvironmentVariable("MMD_MME_CORPUS") ?? @"D:\desktop\mmd渲染";
        if (!Directory.Exists(corpus)) { Assert.Skip("MME corpus not present"); return; }
        var report = new List<object>();
        foreach (var load in EffectLibrary.Scan(corpus, preprocess: false))
        {
            if (load.Effect is null) continue;
            var diagnostics = new List<MmeDiagnostic>();
            var definition = new ImportedEffect(load.Effect, diagnostics).Build();
            var values = EffectParameterValues.CreateDefaults(definition);
            foreach (var parameter in definition.Parameters.Where(p => p.Initializer is not null && p.DefaultValue is null))
            {
                Assert.False(values.TryGet(parameter.Name, out _));
                Assert.Contains(diagnostics, d => d.Code == MmeDiagnosticCodes.UnsupportedInitializer && d.Message.Contains($"'{parameter.Name}'", StringComparison.Ordinal));
                report.Add(new
                {
                    load.Path, parameter.Name, parameter.DeclaredType, parameter.Initializer,
                    ScriptUses = load.Effect.Techniques.SelectMany(t => EffectScript.Parse(t.Script)
                        .Where(c => c.Argument.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase))
                        .Select(c => $"{t.Name}:{c.Name}" )).ToArray(),
                });
            }
        }
        string? evidence = Environment.GetEnvironmentVariable("MME_FIX_EVIDENCE");
        if (evidence is not null)
            File.WriteAllText(Path.Combine(evidence, "corpus-initializer-classification.json"),
                System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    [Theory]
    [InlineData("KEYCOLOR_NUM!=0", false)]
    [InlineData("ALIAS == 0 && defined(KEYCOLOR_NUM)", true)]
    [InlineData("!defined(MISSING) && (2 + 3 * 4 == 14)", true)]
    [InlineData("(2 + 3) * 4 == 14 || 9 / 3 - 3", false)]
    [InlineData("defined KEYCOLOR_NUM && -2 < 0 && 7 % 4 == 3", true)]
    [InlineData("0 && (1 / 0)", false)]
    [InlineData("1 || (1 / 0)", true)]
    [InlineData("010 == 8 && 0x10 == 16 && (3 << 2) == 12", true)]
    public void Conditional_expressions_expand_macros_and_honor_precedence(string expression, bool expected)
    {
        var result = FxPreprocessor.Process("conditions.fx", _ => null,
            $"#define KEYCOLOR_NUM 0\n#define ALIAS KEYCOLOR_NUM\n#if {expression}\nTRUE_BRANCH\n#else\nFALSE_BRANCH\n#endif");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(expected ? "TRUE_BRANCH" : "FALSE_BRANCH", result.Text);
        Assert.DoesNotContain(expected ? "FALSE_BRANCH" : "TRUE_BRANCH", result.Text);
    }

    [Theory]
    [InlineData("1 ? 2 : 0")]
    [InlineData("1 / 0")]
    [InlineData("(1 + )")]
    [InlineData("RECURSIVE")]
    [InlineData("-1 < 1U")]
    public void Unsupported_conditionals_are_errors(string expression)
    {
        var result = FxPreprocessor.Process("bad.fx", _ => null,
            $"#define RECURSIVE RECURSIVE\n#if {expression}\nWRONG_SUCCESS\n#endif");
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain("WRONG_SUCCESS", result.Text);
    }

    [Fact]
    public void Inactive_conditions_and_taken_elif_do_not_evaluate_arithmetic()
    {
        var result = FxPreprocessor.Process("inactive.fx", _ => null,
            "#if 0\n#if 1 / 0\nBAD\n#endif\n#elif 1\nGOOD\n#elif 1 / 0\nBAD\n#endif");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("GOOD", result.Text);
        Assert.DoesNotContain("BAD", result.Text);
    }

    [Fact]
    public void Defined_recognizes_function_macros_but_calls_are_explicitly_unsupported()
    {
        var defined = FxPreprocessor.Process("functions.fx", _ => null,
            "#define F(x) x\n#if defined(F)\nGOOD\n#endif");
        Assert.Contains("GOOD", defined.Text);
        Assert.Empty(defined.Diagnostics);
        var call = FxPreprocessor.Process("functions.fx", _ => null,
            "#define F(x) x\n#if F(1)\nBAD\n#endif");
        Assert.Contains(call.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain("BAD", call.Text);
    }

    [Fact]
    public void Exact_initializer_text_and_string_literal_values_survive_import()
    {
        var result = ImportText("string Label=\"日本語\\tlabel\"; float Value = - 2.5f; float Computed = 1 /* retained */ + 2; technique T { pass P {} }");
        Assert.True(result.Success);
        var definition = result.Definition!;
        Assert.Equal("\"日本語\\tlabel\"", definition.FindParameter("Label")!.Initializer);
        Assert.Equal("1 /* retained */ + 2", definition.FindParameter("Computed")!.Initializer);
        Assert.Contains(result.Diagnostics, d => d.Code == MmeDiagnosticCodes.UnsupportedInitializer);
        var values = EffectParameterValues.CreateDefaults(definition);
        Assert.True(values.TryGet("Label", out var label));
        Assert.Equal("日本語\tlabel", label.Text);
        Assert.True(values.TryGet("Value", out var scalar));
        Assert.Equal(-2.5f, scalar.Scalar);
        Assert.False(values.TryGet("Computed", out _));
    }

    [Fact]
    public void Sequential_loop_count_observes_previous_index_without_mutating_live_values_during_plan()
    {
        var result = ImportText("int Count=3; int Index=7; technique T < string Script=\"LoopByCount=Count;Pass=Before;LoopGetIndex=Index;LoopEnd=;LoopByCount=Index;Pass=After;LoopEnd=;\"; > { pass Before {} pass After {} }");
        var definition = result.Definition!;
        var values = EffectParameterValues.CreateDefaults(definition);
        var diagnostics = new List<MmeDiagnostic>();
        var runtime = new NeoEffectRuntime(definition, values, diagnostics);
        var plan = runtime.Plan(definition.FindTechnique("T")!);
        Assert.True(plan.IsExecutable);
        Assert.Equal(3, plan.Steps.Count(s => s.Pass?.Name == "Before"));
        Assert.Equal(2, plan.Steps.Count(s => s.Pass?.Name == "After"));
        Assert.True(values.TryGet("Index", out var original));
        Assert.Equal(7, original.Integer);
        var device = new IndexDevice(values);
        runtime.Execute(plan, device);
        Assert.Equal(new[] { "Before:7", "Before:0", "Before:1", "After:2", "After:2" }, device.Passes);
    }

    [Fact]
    public void Index_only_loop_keeps_its_assignment_when_body_has_no_device_commands()
    {
        var result = ImportText("int Count=3; int Index=7; technique T < string Script=\"LoopByCount=Count;LoopGetIndex=Index;LoopEnd=;LoopByCount=Index;Pass=After;LoopEnd=;\"; > { pass After {} }");
        var definition = result.Definition!;
        var values = EffectParameterValues.CreateDefaults(definition);
        var runtime = new NeoEffectRuntime(definition, values, new List<MmeDiagnostic>());
        var plan = runtime.Plan(definition.FindTechnique("T")!);
        Assert.Equal(2, plan.Count(EffectOperationKind.RunPass));
        var device = new IndexDevice(values);
        runtime.Execute(plan, device);
        Assert.Equal(new[] { "After:2", "After:2" }, device.Passes);
    }

    [Fact]
    public void Imported_defaults_and_source_identity_are_preserved()
    {
        const string text = "int Count=4; int Index=9; float4 Color={1,0.5,0,1}; float Depth=1.0; bool Flag=false;\ntechnique T < string Script=\"LoopByCount=Count;LoopGetIndex=Index;ClearSetColor=Color;ClearSetDepth=Depth;Pass=P;LoopEnd=;\"; > { pass P {} }";
        var result = ImportText(text);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var definition = result.Definition!;
        Assert.Equal(result.Path, definition.SourcePath);
        Assert.Equal(text, definition.OriginalSource);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(definition.Source))), definition.SourceSha256);
        Assert.Equal("4", definition.FindParameter("Count")!.Initializer);
        var values = EffectParameterValues.CreateDefaults(definition);
        Assert.True(values.TryGet("Count", out var count));
        Assert.Equal(4, count.Integer);
        Assert.True(values.TryGet("Color", out var color));
        Assert.Equal(new Vector4(1, 0.5f, 0, 1), color.Vector);
        Assert.True(values.TryGet("Flag", out var flag));
        Assert.False(flag.Boolean);
        var diagnostics = new List<MmeDiagnostic>();
        var plan = new NeoEffectRuntime(definition, values, diagnostics).Plan(definition.FindTechnique("T")!);
        Assert.Equal(4, plan.Count(EffectOperationKind.RunPass));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Unsupported_initializer_is_preserved_and_never_replaced_by_preview_guess()
    {
        var result = ImportText("int Count=Other+3; technique T < string Script=\"LoopByCount=Count;Pass=P;LoopEnd=;\"; > { pass P {} }");
        Assert.True(result.Success); // Structural import can retain shader expressions without CPU evaluation.
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("initializer", StringComparison.OrdinalIgnoreCase));
        var parameter = result.Definition!.FindParameter("Count")!;
        Assert.Equal("Other+3", parameter.Initializer);
        Assert.False(EffectParameterValues.CreateDefaults(result.Definition).TryGet("Count", out _));
        var diagnostics = new List<MmeDiagnostic>();
        var runtime = new NeoEffectRuntime(result.Definition, EffectParameterValues.CreateDefaults(result.Definition), diagnostics);
        var plan = runtime.Plan(result.Definition.FindTechnique("T")!);
        Assert.False(plan.IsExecutable);
        Assert.Throws<InvalidOperationException>(() => runtime.Execute(plan, new IndexDevice(new EffectParameterValues())));
    }

    [Fact]
    public void Loop_preserves_prefix_and_assigns_index_at_the_command_position()
    {
        var result = ImportText("int Count=2; int Index=7; technique T < string Script=\"LoopByCount=Count;Clear=Color;Pass=Before;LoopGetIndex=Index;Pass=After;LoopEnd=;\"; > { pass Before {} pass After {} }");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var definition = result.Definition!;
        var values = EffectParameterValues.CreateDefaults(definition);
        values.Set("Count", EffectValue.FromInteger(2));
        values.Set("Index", EffectValue.FromInteger(7));
        var diagnostics = new List<MmeDiagnostic>();
        var runtime = new NeoEffectRuntime(definition, values, diagnostics);
        var plan = runtime.Plan(definition.FindTechnique("T")!);
        Assert.Equal(2, plan.Count(EffectOperationKind.Clear));
        Assert.True(values.TryGet("Index", out var unchanged));
        Assert.Equal(7, unchanged.Integer);
        var device = new IndexDevice(values);
        runtime.Execute(plan, device);
        Assert.Equal(new[] { "Before:7", "After:0", "Before:0", "After:1" }, device.Passes);
    }

    [Theory]
    [InlineData("float bad;", "ClearSetColor=bad;")]
    [InlineData("float bad;", "ClearSetDepth=missing;")]
    [InlineData("float bad;", "LoopByCount=bad;LoopEnd=;")]
    [InlineData("float bad; int Count;", "LoopByCount=Count;LoopGetIndex=bad;LoopEnd=;")]
    [InlineData("texture2D bad : RENDERDEPTHSTENCILTARGET;", "RenderColorTarget0=bad;")]
    [InlineData("int Count;", "LoopByCount=Count;LoopByCount=Count;Pass=P;LoopEnd=;LoopEnd=;")]
    [InlineData("int Count;", "ScriptExternal=Unknown;")]
    [InlineData("int Count;", "Clear=Color,Unknown;")]
    public void Public_import_validates_graph_and_invalid_plans_never_call_device(string declarations, string script)
    {
        var result = ImportText($"{declarations} technique T < string Script=\"{script}\"; > {{ pass P {{}} }}");
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var definition = result.Definition!;
        var diagnostics = new List<MmeDiagnostic>();
        var runtime = new NeoEffectRuntime(definition, EffectParameterValues.CreateDefaults(definition), diagnostics);
        var plan = runtime.Plan(definition.FindTechnique("T")!);
        var device = new IndexDevice(new EffectParameterValues());
        Assert.Throws<InvalidOperationException>(() => runtime.Execute(plan, device));
        Assert.Equal(0, device.Calls);
    }

    [Theory]
    [InlineData("technique T < string Script=\"RenderColorTarget0=Both;RenderDepthStencilTarget=Both;Pass=P;\"; > { pass P {} }")]
    [InlineData("technique T < string Script=\"RenderDepthStencilTarget=Both;RenderColorTarget1=both;Pass=P;\"; > { pass P {} }")]
    [InlineData("technique Color < string Script=\"RenderColorTarget0=Both;\"; > { pass P {} } technique Depth < string Script=\"RenderDepthStencilTarget=Both;\"; > { pass P {} }")]
    public void Texture_color_depth_role_conflicts_are_rejected_without_semantic(string techniques)
    {
        var result = ImportText("texture2D Both; " + techniques);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Code == MmeDiagnosticCodes.InvalidRenderColorTarget);
        Assert.False(result.Definition!.IsExecutable);
        var runtime = new NeoEffectRuntime(result.Definition, EffectParameterValues.CreateDefaults(result.Definition), new List<MmeDiagnostic>());
        var plan = runtime.Plan(result.Definition.Techniques[0]);
        Assert.False(plan.IsExecutable);
        var device = new IndexDevice(new EffectParameterValues());
        Assert.Throws<InvalidOperationException>(() => runtime.Execute(plan, device));
        Assert.Equal(0, device.Calls);
    }

    [Fact]
    public void Sampling_a_color_target_and_rebinding_it_as_color_is_valid()
    {
        var result = ImportText("texture2D Both; sampler2D ReadBoth = sampler_state { Texture=<Both>; }; technique T < string Script=\"RenderColorTarget0=Both;RenderColorTarget1=Both;Pass=P;\"; > { pass P {} }");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var definition = result.Definition!;
        var plan = new NeoEffectRuntime(definition, EffectParameterValues.CreateDefaults(definition), new List<MmeDiagnostic>()).Plan(definition.Techniques[0]);
        Assert.True(plan.IsExecutable, string.Join("\n", plan.Diagnostics));
        Assert.Equal(EffectTextureRole.RenderTarget, Assert.Single(plan.Textures).Role);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Runtime_defensively_rejects_handwritten_color_depth_role_conflicts(bool acrossTechniques)
    {
        var definition = new ConflictingTargetEffect(acrossTechniques).Build();
        Assert.True(definition.IsExecutable); // Handwritten definitions bypass the FX validator.
        var runtime = new NeoEffectRuntime(definition, EffectParameterValues.CreateDefaults(definition), new List<MmeDiagnostic>());
        var plan = runtime.Plan(definition.Techniques[0]);
        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Code == MmeDiagnosticCodes.InvalidRenderColorTarget);
        Assert.Throws<InvalidOperationException>(() => runtime.Execute(plan, new IndexDevice(new EffectParameterValues())));
    }

    [Fact]
    public void Runtime_checks_allocation_role_before_reusing_a_texture_by_name()
    {
        var definition = new ConflictingTargetEffect(false).Build() with { Techniques = [] };
        var technique = new ConflictingTargetEffect(false).Build().Techniques[0];
        // A caller-supplied graph still needs local allocation validation independently of the
        // definition-wide role scan. The first allocation cannot be reused as a depth target.
        var runtime = new NeoEffectRuntime(definition, EffectParameterValues.CreateDefaults(definition), new List<MmeDiagnostic>());
        var plan = runtime.Plan(technique);
        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Code == MmeDiagnosticCodes.InvalidRenderColorTarget);
        Assert.Equal(EffectTextureRole.RenderTarget, Assert.Single(plan.Textures).Role);
    }

    private sealed class ConflictingTargetEffect(bool acrossTechniques) : NeoEffect("ConflictingTargets")
    {
        public override void Configure(EffectBuilder effect)
        {
            effect.Texture("Both", EffectTextureRole.Input);
            var color = effect.Technique("Color").Pass("P").RenderTarget(0, "Both");
            if (acrossTechniques)
            {
                color.End();
                effect.Technique("Depth").DepthStencil("Both").End();
            }
            else color.DepthStencil("Both").Run("P").End();
        }
    }

    private static ImportResult ImportText(string text)
    {
        string folder = Path.Combine(Path.GetTempPath(), "mme-semantic-regression");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"{Guid.NewGuid():N}.fx");
        File.WriteAllText(path, text);
        try { return EffectLibrary.Import(path); }
        finally { File.Delete(path); }
    }

    private sealed class IndexDevice(EffectParameterValues values) : IEffectDevice
    {
        public List<string> Passes { get; } = [];
        public int Calls { get; private set; }
        public void SetRenderTarget(int slot, EffectTextureAllocation? texture) => Calls++;
        public void SetDepthStencilTarget(EffectTextureAllocation? texture) => Calls++;
        public void ClearColor(Vector4 color) => Calls++;
        public void ClearDepth(float depth) => Calls++;
        public void Clear(ClearTargets targets) => Calls++;
        public void DrawScene() => Calls++;
        public void DrawPass(EffectPass pass)
        {
            Calls++;
            values.TryGet("Index", out var index);
            Passes.Add($"{pass.Name}:{index.Integer}");
        }
    }
}
