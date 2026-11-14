using Mmd.Modern.Effects;
using Mmd.Modern.Effects.Compilation;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12.Shader;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EffectShaderTranslationTests
{
    // Authored fixture: exercises the same syntax categories as the retained user effect.
    public const string Fixture = """
        float Script : STANDARDSGLOBAL <string ScriptClass="scene";> = 0.8;
        float2 ViewportSize : VIEWPORTPIXELSIZE;
        row_major float4x4 World : WORLD;
        static float2 Offset = float2(0.5,0.5) / ViewportSize;
        static const float4 Gain = {World._41, World._42, World._43, 1};
        texture2D Scene : RENDERCOLORTARGET <float2 ViewportRatio={1,1};>;
        sampler2D SceneSampler = sampler_state {
            Texture=<Scene>; MinFilter=LINEAR; MagFilter=LINEAR;
            MipFilter=NONE; AddressU=CLAMP; AddressV=CLAMP;
        };
        struct VertexOutput {float4 Position : POSITION; float2 Uv : TEXCOORD0;};
        VertexOutput Vertex(float4 position:POSITION, float2 uv:TEXCOORD0) {
            VertexOutput result=(VertexOutput)0; result.Position=position;
            result.Uv=uv+Offset; return result;
        }
        float4 Pixel(float2 uv:TEXCOORD0):COLOR {return tex2D(SceneSampler, uv)*Gain;}
        technique Main {pass Filter {AlphaBlendEnable=FALSE;
            VertexShader=compile vs_2_0 Vertex(); PixelShader=compile ps_2_0 Pixel();}}
        """;

    [Fact]
    public void TranslatesActualShaderBodiesWithExplicitBindingsAndStageWrappers()
    {
        var effect = Parse(Fixture);
        var translated = EffectShaderTranslator.Translate(effect, effect.Techniques[0]);
        Assert.True(translated.Success, string.Join("\n", translated.Diagnostics));
        Assert.Equal(80, translated.UniformBufferSize);
        Assert.Collection(translated.UniformBindings,
            b => {Assert.Equal("Script", b.Name); Assert.Equal(0, b.ByteOffset);},
            b => {Assert.Equal("ViewportSize", b.Name); Assert.Equal(4, b.ByteOffset);},
            b => {Assert.Equal("World", b.Name); Assert.Equal(16, b.ByteOffset); Assert.Equal(64,b.ByteSize);});
        Assert.Contains("row_major float4x4 World : packoffset(c1)", translated.Source);
        Assert.Contains("#define Offset", translated.Source);
        Assert.Contains("#define Gain", translated.Source);
        Assert.DoesNotContain("sampler_state", translated.Source);
        Assert.Contains("Scene.Sample(SceneSampler,", translated.Source);
        Assert.Contains("float4 position:POSITION", translated.Source); // Input stays a vertex attribute.
        Assert.Equal("Scene", Assert.Single(translated.Samplers).TextureName);
        Assert.Equal("CLAMP", translated.Samplers[0].AddressU);
        var pass = Assert.Single(translated.Passes);
        Assert.Equal("Vertex", pass.OriginalVertexEntry);
        Assert.Equal("Pixel", pass.OriginalPixelEntry);
        Assert.Contains("SV_POSITION", translated.Source);
        Assert.Contains("SV_Target", translated.Source);
        Compile(translated.Source, pass.VertexEntry, "vs_5_1");
        Compile(translated.Source, pass.PixelEntry, "ps_5_1");
    }

    [Theory]
    [InlineData("Pixel()", "Missing()", "entry")]
    [InlineData("AlphaBlendEnable=FALSE", "UnrecognizedState=TRUE", "state")]
    [InlineData("MinFilter=LINEAR", "MinFilter=ANISOTROPIC", "filter")]
    [InlineData("Texture=<Scene>", "Texture=<Missing>", "texture")]
    [InlineData("float2 ViewportSize", "float2 ViewportSize[2]", "array")]
    [InlineData("tex2D(SceneSampler, uv)", "tex2Dlod(SceneSampler, float4(uv,0,0))", "tex2Dlod")]
    [InlineData("tex2D(SceneSampler, uv)", "tex2D(SceneSampler,uv,float2(0.25,0),float2(0,0.25))", "arguments")]
    public void ExplicitlyRefusesUnsupportedOrInvalidSources(string before, string after, string diagnostic)
    {
        var effect=Parse(Fixture.Replace(before, after, StringComparison.Ordinal));
        var translated=EffectShaderTranslator.Translate(effect,effect.Techniques[0]);
        Assert.False(translated.Success);
        Assert.Contains(translated.Diagnostics, d=>d.Contains(diagnostic,StringComparison.OrdinalIgnoreCase));
        Assert.Empty(translated.Passes);
    }

    [Fact]
    public void TranslationIsDeterministicAndSourceMutationChangesItsIdentity()
    {
        var original=Parse(Fixture);
        var first=EffectShaderTranslator.Translate(original,original.Techniques[0]);
        var second=EffectShaderTranslator.Translate(original,original.Techniques[0]);
        Assert.Equal(first.Source,second.Source);
        Assert.Equal(first.SourceSha256,second.SourceSha256);
        var edited=Parse(Fixture.Replace("*Gain", "*Gain*0.25",StringComparison.Ordinal));
        var mutation=EffectShaderTranslator.Translate(edited,edited.Techniques[0]);
        Assert.True(mutation.Success,string.Join("\n",mutation.Diagnostics));
        Assert.NotEqual(first.SourceSha256,mutation.SourceSha256);
        var pass=Assert.Single(first.Passes);
        Assert.NotEqual(Compile(first.Source,pass.PixelEntry,"ps_5_1"),Compile(mutation.Source,pass.PixelEntry,"ps_5_1"));
    }

    [Theory]
    [InlineData("static float2 Offset = float2(0.5,0.5) / ViewportSize;", "static float2 Offset = Other; static float2 Other = Offset;", "cycle")]
    [InlineData("static float2 Offset = float2(0.5,0.5) / ViewportSize;", "float2 Mutate() { World._41 += 1; return 0; } static float2 Offset = Mutate();", "pure")]
    public void RejectsDerivedCyclesAndUnprovenFunctionSideEffects(string before,string after,string message)
    {
        var effect=Parse(Fixture.Replace(before,after,StringComparison.Ordinal));
        var translated=EffectShaderTranslator.Translate(effect,effect.Techniques[0]);
        Assert.False(translated.Success);
        Assert.Contains(translated.Diagnostics,d=>d.Contains(message,StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NonAsciiCommentsDoNotReachTheLegacyNativeCompilerStringLengthBoundary()
    {
        var effect=Parse(Fixture.Replace("return result;", "/* 頂点座標 日本語注釈 */ return result;",StringComparison.Ordinal));
        var translated=EffectShaderTranslator.Translate(effect,effect.Techniques[0]);
        Assert.True(translated.Success,string.Join("\n",translated.Diagnostics));
        Assert.DoesNotContain(translated.Source,c=>c>127);
        var pass=Assert.Single(translated.Passes);
        Compile(translated.Source,pass.VertexEntry,"vs_5_1");
        Compile(translated.Source,pass.PixelEntry,"ps_5_1");
    }

    [Fact]
    public void CompiledStageInterfacesUseTheSameSemanticRegistersAndTypes()
    {
        var effect=Parse(Fixture);
        var translated=EffectShaderTranslator.Translate(effect,effect.Techniques[0]);
        Assert.True(translated.Success,string.Join("\n",translated.Diagnostics));
        var pass=Assert.Single(translated.Passes);
        using var vertex=Compiler.Reflect<ID3D12ShaderReflection>(Compile(translated.Source,pass.VertexEntry,"vs_5_1"));
        using var pixel=Compiler.Reflect<ID3D12ShaderReflection>(Compile(translated.Source,pass.PixelEntry,"ps_5_1"));
        Assert.Equal(vertex.OutputParameters.Length,pixel.InputParameters.Length);
        foreach(var input in pixel.InputParameters)
        {
            var output=Assert.Single(vertex.OutputParameters,p=>p.SemanticName.Equals(input.SemanticName,StringComparison.OrdinalIgnoreCase)&&p.SemanticIndex==input.SemanticIndex);
            Assert.Equal(output.Register,input.Register);
            Assert.Equal(output.ComponentType,input.ComponentType);
        }
    }

    [Theory]
    [InlineData("Pixel(float2 uv:TEXCOORD0)","Pixel(float2 uv:TEXCOORD1)","semantic")]
    [InlineData("Pixel(float2 uv:TEXCOORD0)","Pixel(float3 uv:TEXCOORD0)","type")]
    public void RefusesPixelInputsThatCannotBeProvenToMatchVertexOutputs(string before,string after,string message)
    {
        var effect=Parse(Fixture.Replace(before,after,StringComparison.Ordinal));
        var translated=EffectShaderTranslator.Translate(effect,effect.Techniques[0]);
        Assert.False(translated.Success);
        Assert.Contains(translated.Diagnostics,d=>d.Contains(message,StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DerivedExpressionsKeepGlobalBindingsWhenCallerShadowsAUniform()
    {
        var effect=Parse(Fixture.Replace("{return tex2D(SceneSampler, uv)*Gain;}","{float4x4 World=(float4x4)0; return tex2D(SceneSampler, uv)*Gain;}",StringComparison.Ordinal));
        var translated=EffectShaderTranslator.Translate(effect,effect.Techniques[0]);
        Assert.True(translated.Success,string.Join("\n",translated.Diagnostics));
        var pass=Assert.Single(translated.Passes);
        using var reflection=Compiler.Reflect<ID3D12ShaderReflection>(Compile(translated.Source,pass.PixelEntry,"ps_5_1"));
        Assert.Contains(reflection.BoundResources,b=>b.Name=="MmdEffectUniforms");
        Assert.Contains("Used",reflection.GetConstantBufferByName("MmdEffectUniforms").GetVariableByName("World").Description.Flags.ToString());
    }

    [Theory]
    [InlineData("float2 uv:TEXCOORD0", "float2 Gain:TEXCOORD0")]
    [InlineData("{return tex2D(SceneSampler, uv)*Gain;}", "{float4 Gain; return 0;}")]
    [InlineData("{return tex2D(SceneSampler, uv)*Gain;}", "{float4 other=0, Gain=0; return 0;}")]
    public void RefusesLocalOrParameterDeclarationsThatShadowDerivedNames(string before,string after)
    {
        var effect=Parse(Fixture.Replace(before,after,StringComparison.Ordinal));
        var translated=EffectShaderTranslator.Translate(effect,effect.Techniques[0]);
        Assert.False(translated.Success);
        Assert.Contains(translated.Diagnostics,d=>d.Contains("derived",StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] Compile(string source,string entry,string profile) => Compiler.Compile(
        source,entry,"authored-postfx.hlsl",profile,ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness).ToArray();

    private static FxEffect Parse(string source)
    {
        var diagnostics=new List<MmeDiagnostic>();
        var effect=FxParser.Parse(FxTokenizer.Tokenize(source),"authored.fx",diagnostics,source);
        Assert.DoesNotContain(diagnostics,d=>d.Severity==DiagnosticSeverity.Error);
        return effect;
    }
}
