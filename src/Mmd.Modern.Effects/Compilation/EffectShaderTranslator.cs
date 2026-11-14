using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mmd.Modern.Effects.Compilation;

/// <summary>
/// Lowers the tested 2D postprocess subset of legacy FX to ordinary SM5 HLSL. Top-level syntax
/// and entry signatures are parsed, not removed with a broad regular expression. HLSL function
/// bodies remain source code for the backend compiler; this class does not type-check HLSL.
/// </summary>
public static class EffectShaderTranslator
{
    public static EffectShaderTranslation Translate(FxEffect effect, FxTechnique technique)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(technique);
        return new Translation(effect, technique).Run();
    }

    private sealed record Field(string Type, string Name, string Semantic);
    private sealed record Function(string Type, string Name, IReadOnlyList<FxToken> Parameters, string? Semantic);
    private sealed record Edit(int Start, int End, string Text);
    private sealed record DerivedExpression(string Type, string Name, string Helper, string Expression);

    private sealed class Unsupported(string message) : Exception(message);

    private sealed class Translation(FxEffect effect, FxTechnique technique)
    {
        private readonly List<EffectUniformBinding> _uniforms = [];
        private readonly List<EffectTextureBinding> _textures = [];
        private readonly List<EffectSamplerBinding> _samplers = [];
        private readonly List<EffectTranslatedPass> _passes = [];
        private readonly Dictionary<string, Function> _functions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Field>> _structs = new(StringComparer.Ordinal);
        private readonly List<string> _bodies = [];
        private readonly List<DerivedExpression> _expressions = [];
        private readonly HashSet<string> _derived = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<FxToken>> _derivedTokens = new(StringComparer.Ordinal);
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        private readonly IReadOnlyList<FxToken> _tokens = FxTokenizer.Tokenize(effect.Source);
        private int _position;
        private int _uniformBytes;

        private FxToken Current => _tokens[_position];
        private string Slice(int start, int end) => effect.Source[_tokens[start].SourceOffset.._tokens[end].SourceOffset];
        private static int Align16(int value) => (value + 15) & ~15;

        public EffectShaderTranslation Run()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(effect.Source)) Fail("Effect has no preprocessed shader source.");
                if (!effect.Techniques.Contains(technique)) Fail("Selected technique does not belong to this effect.");
                if (technique.Passes.Count == 0) Fail("Selected technique has no passes.");
                while (Current.Kind != FxTokenKind.End) ReadTopLevel();
                ValidateDerivedExpressions();
                foreach (var sampler in _samplers)
                    if (!_textures.Any(t => t.Name == sampler.TextureName)) Fail($"Sampler '{sampler.Name}' references missing texture '{sampler.TextureName}'.");
                var source = new StringBuilder("// MMD Modern: bounded legacy 2D postFX translation\n");
                if (_uniforms.Count > 0)
                {
                    source.AppendLine("cbuffer MmdEffectUniforms : register(b0) {");
                    foreach (var uniform in _uniforms)
                    {
                        string component = uniform.ByteOffset % 16 == 0 ? "" : "." + "xyzw"[uniform.ByteOffset % 16 / 4];
                        source.AppendLine($"    {(uniform.Type.Contains('x') ? "row_major " : "")}{uniform.Type} {uniform.Name} : packoffset(c{uniform.ByteOffset / 16}{component});");
                    }
                    source.AppendLine("};");
                }
                foreach (var texture in _textures) source.AppendLine($"Texture2D<float4> {texture.Name} : register(t{texture.Slot});");
                foreach (var sampler in _samplers) source.AppendLine($"SamplerState {sampler.Name} : register(s{sampler.Slot});");
                foreach (var expression in _expressions) source.AppendLine($"{expression.Type} {expression.Helper}();");
                foreach (var expression in _expressions) source.AppendLine($"#define {expression.Name} ({expression.Helper}())");
                // Helpers bind names in global scope. Expanding the original expression at the
                // call site would accidentally capture a same-named local uniform dependency.
                foreach (var expression in _expressions) source.AppendLine($"{expression.Type} {expression.Helper}() {{ return {expression.Type}({expression.Expression}); }}");
                foreach (string body in _bodies) source.AppendLine(RewriteSampling(RemoveComments(body)));
                for (int i = 0; i < technique.Passes.Count; i++) WritePass(source, technique.Passes[i], i);
                string output = source.ToString();
                if (output.Any(c => c > 127)) Fail("Non-ASCII shader identifiers/literals are unsupported; comments have been removed before compilation.");
                return new(output, Hash(output), Align16(_uniformBytes), _uniforms.ToArray(), _textures.ToArray(), _samplers.ToArray(), _passes.ToArray(), []);
            }
            catch (Unsupported exception)
            {
                return new("", Hash(effect.Source), 0, [], [], [], [], [exception.Message]);
            }
        }

        private void ReadTopLevel()
        {
            if (Current.Is(";")) { _position++; return; }
            if (Current.Is("technique"))
            {
                _position++;
                TakeIdentifier("technique name");
                if (Current.Is("<")) Balanced("<", ">");
                Balanced("{", "}");
                return;
            }
            if (Current.Is("struct")) { ReadStruct(); return; }
            int start = _position;
            var qualifiers = new List<string>();
            while (Current.Text is "static" or "const" or "uniform" or "extern" or "shared" or "row_major" or "column_major")
                qualifiers.Add(_tokens[_position++].Text);
            string type = TakeIdentifier("declaration type");
            string name = TakeIdentifier("declaration name");
            if (name.StartsWith("__MmdModern", StringComparison.Ordinal)) Fail("Names beginning '__MmdModern' are reserved for stage wrappers.");
            if (!_names.Add(name)) Fail($"Duplicate or overloaded declaration '{name}' is unsupported.");
            if (Current.Is("("))
            {
                int open = _position;
                Balanced("(", ")");
                var parameters = _tokens.Skip(open + 1).Take(_position - open - 2).ToArray();
                string? semantic = ReadSemantic();
                Balanced("{", "}");
                _functions.Add(name, new(type, name, parameters, semantic));
                _bodies.Add(Slice(start, _position));
                return;
            }
            if (Current.Is("[")) Fail($"Global array '{name}' is unsupported by the postFX uniform layout.");
            string? globalSemantic = ReadSemantic();
            if (Current.Is("<")) Balanced("<", ">");
            string? initializer = null;
            if (Current.Is("="))
            {
                _position++;
                int valueStart = _position;
                while (!Current.Is(";") && Current.Kind != FxTokenKind.End)
                {
                    if (Current.Is("{")) Balanced("{", "}");
                    else if (Current.Is("(")) Balanced("(", ")");
                    else _position++;
                }
                initializer = Slice(valueStart, _position).Trim();
            }
            Expect(";");
            if (type is "texture" or "texture2D" or "texture2d")
            {
                if (initializer is not null) Fail($"Texture initializer '{name}' is unsupported.");
                _textures.Add(new(name, _textures.Count, globalSemantic));
            }
            else if (type is "sampler" or "sampler2D" or "sampler2d") ReadSampler(name, initializer);
            else if (type == "string") { /* FX metadata, not an HLSL runtime value. */ }
            else
            {
                if (!NumericType(type, out int rows, out int columns)) Fail($"Unsupported global type '{type}' for '{name}'.");
                if (qualifiers.Contains("column_major")) Fail($"column_major global '{name}' is unsupported; this upload contract is row_major.");
                if (qualifiers.Contains("static") || qualifiers.Contains("const"))
                {
                    if (initializer is null) Fail($"Derived global '{name}' requires an initializer.");
                    string expression = initializer!.Trim();
                    if (expression.StartsWith('{') && expression.EndsWith('}')) expression = expression[1..^1];
                    var expressionTokens = FxTokenizer.Tokenize(expression);
                    if (expressionTokens.Any(t => t.Text is "=" or "+=" or "-=" or "++" or "--" or ";")) Fail($"Derived global '{name}' contains a side effect.");
                    _derived.Add(name);
                    _derivedTokens.Add(name, expressionTokens);
                    var normalized = new StringBuilder();
                    int previousEnd = -1;
                    foreach (var token in expressionTokens.Where(t => t.Kind != FxTokenKind.End))
                    {
                        if (token.Kind == FxTokenKind.String) Fail($"Derived global '{name}' contains a string.");
                        if (previousEnd >= 0 && token.SourceOffset != previousEnd) normalized.Append(' ');
                        normalized.Append(token.Text);
                        previousEnd = token.SourceOffset + token.Text.Length;
                    }
                    _expressions.Add(new(type, name, $"__MmdModernDerived{_expressions.Count}", normalized.ToString()));
                }
                else
                {
                    int size = rows > 1 ? rows * 16 : columns * 4;
                    if (rows > 1 || _uniformBytes % 16 + size > 16) _uniformBytes = Align16(_uniformBytes);
                    _uniforms.Add(new(name, type, _uniformBytes, size, globalSemantic, initializer));
                    _uniformBytes += size;
                }
            }
        }

        private void ReadStruct()
        {
            int start = _position++;
            string name = TakeIdentifier("struct name");
            if (!_names.Add(name)) Fail($"Duplicate declaration '{name}'.");
            Expect("{");
            var fields = new List<Field>();
            while (!Current.Is("}") && Current.Kind != FxTokenKind.End)
            {
                string type = TakeIdentifier("struct field type");
                string field = TakeIdentifier("struct field name");
                string? semantic = ReadSemantic();
                if (Current.Is("[")) Fail($"Struct array '{name}.{field}' is unsupported.");
                Expect(";");
                if (!NumericType(type, out int rows, out _) || rows != 1 || semantic is null) Fail($"Struct field '{name}.{field}' must be a numeric scalar/vector with a semantic.");
                fields.Add(new(type, field, semantic!));
            }
            Expect("}");
            Expect(";");
            _structs.Add(name, fields);
            _bodies.Add(Slice(start, _position));
        }

        private void ReadSampler(string name, string? initializer)
        {
            var tokens = FxTokenizer.Tokenize(initializer ?? "");
            int at = 0;
            void Need(string value)
            {
                if (!tokens[Math.Min(at, tokens.Count - 1)].Is(value)) Fail($"Sampler '{name}' requires sampler_state with explicit 2D texture binding; expected '{value}'.");
                at++;
            }
            Need("sampler_state"); Need("{");
            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (at < tokens.Count && !tokens[at].Is("}"))
            {
                string state = tokens[at++].Text;
                Need("=");
                bool texture = state.Equals("Texture", StringComparison.OrdinalIgnoreCase);
                if (texture) Need("<");
                string value = tokens[Math.Min(at++, tokens.Count - 1)].Text;
                if (texture) Need(">");
                Need(";");
                if (!states.TryAdd(state, value)) Fail($"Duplicate sampler state '{state}' on '{name}'.");
            }
            Need("}");
            if (at != tokens.Count - 1) Fail($"Unexpected sampler initializer tokens for '{name}'.");
            foreach (string key in states.Keys)
                if (!new[] { "Texture", "MinFilter", "MagFilter", "MipFilter", "AddressU", "AddressV" }.Contains(key, StringComparer.OrdinalIgnoreCase)) Fail($"Unsupported sampler state '{key}' on '{name}'.");
            if (!states.TryGetValue("Texture", out string? textureName)) Fail($"Sampler '{name}' has no texture binding.");
            string Get(string key, string fallback, params string[] allowed)
            {
                string value = states.GetValueOrDefault(key, fallback).ToUpperInvariant();
                if (!allowed.Contains(value)) Fail($"Unsupported sampler {key} filter/address value '{value}' on '{name}'.");
                return value;
            }
            _samplers.Add(new(name, _samplers.Count, textureName!,
                Get("MinFilter", "POINT", "POINT", "LINEAR"), Get("MagFilter", "POINT", "POINT", "LINEAR"),
                Get("MipFilter", "NONE", "NONE", "POINT", "LINEAR"),
                Get("AddressU", "WRAP", "WRAP", "CLAMP", "MIRROR"), Get("AddressV", "WRAP", "WRAP", "CLAMP", "MIRROR")));
        }

        private string RewriteSampling(string source)
        {
            var tokens = FxTokenizer.Tokenize(source);
            ValidateDerivedNames(tokens);
            var edits = new List<Edit>();
            for (int i = 0; i < tokens.Count - 1; i++)
            {
                if (_derived.Contains(tokens[i].Text) && tokens[i + 1].Text is "=" or "+=" or "-=" or "*=" or "/=" or "++" or "--") Fail($"Shader writes read-only derived global '{tokens[i].Text}'.");
                if (!tokens[i].Text.StartsWith("tex", StringComparison.Ordinal) || !tokens[i + 1].Is("(")) continue;
                if (tokens[i].Text != "tex2D")
                {
                    if (tokens[i].Text is "tex1D" or "tex3D" or "texCUBE" || tokens[i].Text.StartsWith("tex2D", StringComparison.Ordinal)) Fail($"Unsupported legacy sampling intrinsic '{tokens[i].Text}'.");
                    continue;
                }
                if (i + 3 >= tokens.Count || !tokens[i + 3].Is(",")) Fail("tex2D requires a declared sampler name as its first argument.");
                int parentheses = 0, brackets = 0, braces = 0, commas = 0;
                bool closed = false;
                for (int at = i + 1; at < tokens.Count; at++)
                {
                    if (tokens[at].Is("(")) parentheses++;
                    else if (tokens[at].Is(")")) { if (--parentheses == 0) { closed = true; break; } }
                    else if (tokens[at].Is("[")) brackets++;
                    else if (tokens[at].Is("]")) brackets--;
                    else if (tokens[at].Is("{")) braces++;
                    else if (tokens[at].Is("}")) braces--;
                    else if (parentheses == 1 && brackets == 0 && braces == 0 && tokens[at].Is(",")) commas++;
                }
                if (!closed || commas != 1) Fail("tex2D supports exactly two arguments; explicit gradients and additional sampling arguments are unsupported.");
                var sampler = _samplers.FirstOrDefault(s => s.Name == tokens[i + 2].Text);
                if (sampler is null) Fail($"tex2D references undeclared sampler '{tokens[i + 2].Text}'.");
                edits.Add(new(tokens[i].SourceOffset, tokens[i + 3].SourceOffset + 1, $"{sampler!.TextureName}.Sample({sampler.Name},"));
            }
            var output = new StringBuilder(source);
            foreach (var edit in edits.OrderByDescending(e => e.Start)) output.Remove(edit.Start, edit.End - edit.Start).Insert(edit.Start, edit.Text);
            return output.ToString();
        }

        private void ValidateDerivedExpressions()
        {
            string[] pureIntrinsics = ["abs", "acos", "asin", "atan", "atan2", "ceil", "clamp", "cos", "cross", "degrees", "distance", "dot", "exp", "exp2", "floor", "fmod", "frac", "length", "lerp", "log", "log2", "max", "min", "mul", "normalize", "pow", "radians", "reflect", "refract", "round", "rsqrt", "saturate", "sign", "sin", "smoothstep", "sqrt", "step", "tan", "transpose", "trunc"];
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var active = new HashSet<string>(StringComparer.Ordinal);
            void Visit(string name)
            {
                if (!active.Add(name)) Fail($"Derived global dependency cycle at '{name}'.");
                if (visited.Contains(name)) { active.Remove(name); return; }
                var tokens = _derivedTokens[name];
                for (int i = 0; i < tokens.Count - 1; i++)
                {
                    if (tokens[i].Kind != FxTokenKind.Identifier) continue;
                    if (tokens[i + 1].Is("(") && (_functions.ContainsKey(tokens[i].Text) || (!NumericType(tokens[i].Text, out _, out _) && !pureIntrinsics.Contains(tokens[i].Text)))) Fail($"Derived global '{name}' calls '{tokens[i].Text}', which is outside the verified pure expression subset.");
                    if (_derived.Contains(tokens[i].Text) && i > 0 && tokens[i - 1].Is(".")) Fail($"Derived name '{tokens[i].Text}' conflicts with an expression member token.");
                    if (_derived.Contains(tokens[i].Text)) Visit(tokens[i].Text);
                }
                active.Remove(name);
                visited.Add(name);
            }
            foreach (string name in _derived) Visit(name);
        }

        private void ValidateDerivedNames(IReadOnlyList<FxToken> tokens)
        {
            void Reject(string name) => Fail($"A local, parameter or member declaration shadows derived global '{name}'; derived-name shadowing is unsupported.");
            for (int i = 0; i < tokens.Count - 1; i++)
            {
                if (_derived.Contains(tokens[i].Text) && i > 0 && tokens[i - 1].Is(".")) Fail($"Derived name '{tokens[i].Text}' conflicts with a shader member token.");
                if ((!NumericType(tokens[i].Text, out _, out _) && !_structs.ContainsKey(tokens[i].Text)) || tokens[i + 1].Kind != FxTokenKind.Identifier) continue;
                if (_derived.Contains(tokens[i + 1].Text)) Reject(tokens[i + 1].Text);
                if (i + 2 < tokens.Count && tokens[i + 2].Is("(")) continue; // Function declaration; parameter declarations are inspected separately.
                int depth = 0;
                for (int at = i + 2; at < tokens.Count - 1; at++)
                {
                    if (tokens[at].Is("(") || tokens[at].Is("[") || tokens[at].Is("{")) depth++;
                    else if (tokens[at].Is(")") || tokens[at].Is("]") || tokens[at].Is("}")) { if (--depth < 0) break; }
                    else if (depth == 0 && tokens[at].Is(";")) break;
                    else if (depth == 0 && tokens[at].Is(",") && _derived.Contains(tokens[at + 1].Text)) Reject(tokens[at + 1].Text);
                }
            }
        }

        private void WritePass(StringBuilder source, FxPass pass, int ordinal)
        {
            ValidateStates(pass);
            string vertex = ReadEntry(pass, "VertexShader", "vs_");
            string pixel = ReadEntry(pass, "PixelShader", "ps_");
            if (!_functions.TryGetValue(vertex, out var vs)) Fail($"Unknown vertex entry '{vertex}' for pass '{pass.Name}'.");
            if (!_functions.TryGetValue(pixel, out var ps)) Fail($"Unknown pixel entry '{pixel}' for pass '{pass.Name}'.");
            string vsEntry = $"__MmdModernVS{ordinal}";
            string psEntry = $"__MmdModernPS{ordinal}";
            var vsInputs = ReadInputs(vs!);
            var psInputs = ReadInputs(ps!);
            string Call(Function f, List<Field> inputs) => $"{f.Name}({string.Join(", ", inputs.Select(p => p.Name))})";
            string Inputs(List<Field> inputs) => string.Join(", ", inputs.Select(p => $"{p.Type} {p.Name} : {p.Semantic}"));
            bool returnsStruct = _structs.TryGetValue(vs!.Type, out var fields);
            if (!returnsStruct && (vs.Type != "float4" || vs.Semantic is null || !IsPosition(vs.Semantic))) Fail($"Vertex entry '{vertex}' requires a POSITION float4 or supported output struct.");
            List<Field> outputs = fields ?? [new("float4", "__MmdModernPosition", "POSITION")];
            if (outputs.Count(f => CanonicalSemantic(f.Semantic) == "SV_POSITION#0" && f.Type == "float4") != 1) Fail($"Vertex entry '{vertex}' needs exactly one float4 POSITION output.");
            if (outputs.Select(f => CanonicalSemantic(f.Semantic)).Distinct(StringComparer.Ordinal).Count() != outputs.Count) Fail($"Vertex entry '{vertex}' has ambiguous duplicate output semantics.");
            string outputType = $"__MmdModernOutput{ordinal}";
            source.AppendLine($"struct {outputType} {{");
            foreach (var output in outputs) source.AppendLine($"    {output.Type} {output.Name} : {(IsPosition(output.Semantic) ? "SV_POSITION" : output.Semantic)};");
            source.AppendLine("};");
            source.AppendLine($"{outputType} {vsEntry}({Inputs(vsInputs)}) {{");
            source.AppendLine($"    {vs.Type} original = {Call(vs, vsInputs)};");
            source.AppendLine($"    {outputType} result;");
            foreach (var output in outputs) source.AppendLine($"    result.{output.Name} = original{(returnsStruct ? "." + output.Name : "")};");
            source.AppendLine("    return result;\n}");
            if (ps!.Type != "float4" || ps.Semantic is null || !ps.Semantic.StartsWith("COLOR", StringComparison.OrdinalIgnoreCase)) Fail($"Pixel entry '{pixel}' requires a float4 COLOR output; MRT/output structs are unsupported.");
            string colorIndex = ps.Semantic![5..];
            if (colorIndex.Length > 0 && !int.TryParse(colorIndex, NumberStyles.None, CultureInfo.InvariantCulture, out _)) Fail($"Unsupported pixel output semantic '{ps.Semantic}'.");
            var mappedArguments = new List<string>();
            foreach (var input in psInputs)
            {
                var output = outputs.SingleOrDefault(f => CanonicalSemantic(f.Semantic) == CanonicalSemantic(input.Semantic));
                if (output is null) Fail($"Pixel entry '{pixel}' input semantic '{input.Semantic}' has no matching vertex output in '{vertex}'.");
                if (output!.Type != input.Type) Fail($"Pixel entry '{pixel}' input type '{input.Type}' for semantic '{input.Semantic}' differs from vertex output type '{output.Type}'.");
                mappedArguments.Add("__MmdModernInput." + output.Name);
            }
            // Keeping the complete VS signature is required: D3DCompiler assigns registers from
            // declaration order, even when the original PS consumes only TEXCOORD. A UV-only PS
            // wrapper can otherwise read r0 while the VS writes the same semantic to r1.
            source.AppendLine($"float4 {psEntry}({outputType} __MmdModernInput) : SV_Target{colorIndex} {{ return {ps.Name}({string.Join(", ", mappedArguments)}); }}");
            _passes.Add(new(pass.Name, vsEntry, psEntry, vertex, pixel, new Dictionary<string, string>(pass.States, StringComparer.OrdinalIgnoreCase)));
        }

        private static List<Field> ReadInputs(Function function)
        {
            var result = new List<Field>();
            var tokens = function.Parameters;
            for (int i = 0; i < tokens.Count;)
            {
                if (tokens[i].Is("in")) i++;
                if (i + 3 >= tokens.Count || !NumericType(tokens[i].Text, out int rows, out _) || rows != 1 || !tokens[i + 2].Is(":")) Fail($"Shader entry '{function.Name}' supports direct numeric scalar/vector inputs with semantics only.");
                result.Add(new(tokens[i].Text, tokens[i + 1].Text, tokens[i + 3].Text));
                i += 4;
                if (i < tokens.Count)
                {
                    if (!tokens[i].Is(",")) Fail($"Unsupported shader entry parameter syntax in '{function.Name}'.");
                    i++;
                }
            }
            return result;
        }

        private static string ReadEntry(FxPass pass, string key, string profilePrefix)
        {
            if (!pass.States.TryGetValue(key, out string? value)) Fail($"Pass '{pass.Name}' has no {key} entry.");
            var tokens = FxTokenizer.Tokenize(value!);
            if (tokens.Count != 6 || !tokens[0].Is("compile") || !tokens[1].Text.StartsWith(profilePrefix, StringComparison.Ordinal) || tokens[2].Kind != FxTokenKind.Identifier || !tokens[3].Is("(") || !tokens[4].Is(")")) Fail($"Pass '{pass.Name}' has unsupported {key} entry expression '{value}'.");
            return tokens[2].Text;
        }

        private static void ValidateStates(FxPass pass)
        {
            foreach (var pair in pass.States)
            {
                string value = pair.Value.Trim().ToUpperInvariant();
                bool supported = pair.Key.ToUpperInvariant() switch
                {
                    "VERTEXSHADER" or "PIXELSHADER" => true,
                    "ALPHABLENDENABLE" or "ZENABLE" or "ZWRITEENABLE" or "ALPHATESTENABLE" or "SEPARATEALPHABLENDENABLE" or "STENCILENABLE" => value is "TRUE" or "FALSE" or "0" or "1",
                    "CULLMODE" => value is "NONE" or "CW" or "CCW",
                    "SRCBLEND" or "DESTBLEND" or "SRCBLENDALPHA" or "DESTBLENDALPHA" => value is "ONE" or "ZERO" or "SRCALPHA" or "INVSRCALPHA" or "DESTALPHA" or "INVDESTALPHA" or "SRCCOLOR" or "INVSRCCOLOR" or "DESTCOLOR" or "INVDESTCOLOR",
                    "BLENDOP" or "BLENDOPALPHA" => value is "ADD" or "SUBTRACT" or "REVSUBTRACT" or "MIN" or "MAX",
                    "ZFUNC" => value is "NEVER" or "LESS" or "EQUAL" or "LESSEQUAL" or "GREATER" or "NOTEQUAL" or "GREATEREQUAL" or "ALWAYS",
                    "COLORWRITEENABLE" => int.TryParse(value, out int mask) && mask is >= 0 and <= 15,
                    _ => false,
                };
                if (!supported) Fail($"Unsupported pass state '{pair.Key}={pair.Value}' on '{pass.Name}'.");
            }
        }

        private string? ReadSemantic()
        {
            if (!Current.Is(":")) return null;
            _position++;
            string value = TakeIdentifier("semantic");
            if (value.Equals("register", StringComparison.OrdinalIgnoreCase)) Fail("Explicit legacy register assignments are unsupported; bindings are generated deterministically.");
            return value;
        }

        private void Balanced(string open, string close)
        {
            Expect(open);
            int depth = 1;
            while (Current.Kind != FxTokenKind.End)
            {
                if (Current.Is(open)) depth++;
                if (Current.Is(close))
                {
                    depth--;
                    if (depth == 0) { _position++; return; }
                }
                _position++;
            }
            Fail($"Unterminated '{open}' block.");
        }

        private string TakeIdentifier(string context)
        {
            if (Current.Kind != FxTokenKind.Identifier) Fail($"Expected {context}, found '{Current.Text}'.");
            return _tokens[_position++].Text;
        }

        private void Expect(string text)
        {
            if (!Current.Is(text)) Fail($"Expected '{text}', found '{Current.Text}' at line {Current.Line}.");
            _position++;
        }

        private static void Fail(string message) => throw new Unsupported(message);
    }

    private static bool IsPosition(string semantic) => semantic.Equals("POSITION", StringComparison.OrdinalIgnoreCase) || semantic.Equals("POSITION0", StringComparison.OrdinalIgnoreCase);
    private static string CanonicalSemantic(string semantic)
    {
        if (IsPosition(semantic)) return "SV_POSITION#0";
        semantic = semantic.ToUpperInvariant();
        int end = semantic.Length;
        while (end > 0 && char.IsAsciiDigit(semantic[end - 1])) end--;
        return semantic[..end] + "#" + (end == semantic.Length ? "0" : semantic[end..]);
    }
    private static string Hash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();

    // Keep code, token boundaries and line breaks. Besides stable diagnostics, ASCII output avoids
    // the old native compiler wrapper's character-count/encoded-byte-count string boundary.
    private static string RemoveComments(string source)
    {
        var result = source.ToCharArray();
        bool quoted = false;
        for (int i = 0; i < source.Length; i++)
        {
            if (quoted)
            {
                if (source[i] == '\\') i++;
                else if (source[i] == '"') quoted = false;
                continue;
            }
            if (source[i] == '"') { quoted = true; continue; }
            if (i + 1 >= source.Length || source[i] != '/' || source[i + 1] is not ('/' or '*')) continue;
            bool line = source[i + 1] == '/';
            result[i++] = ' ';
            result[i] = ' ';
            for (i++; i < source.Length; i++)
            {
                if (line && source[i] == '\n') break;
                if (!line && i + 1 < source.Length && source[i] == '*' && source[i + 1] == '/')
                { result[i++] = ' '; result[i] = ' '; break; }
                if (source[i] is not ('\r' or '\n')) result[i] = ' ';
            }
        }
        return new string(result);
    }

    private static bool NumericType(string type, out int rows, out int columns)
    {
        rows = columns = 1;
        string? prefix = new[] { "float", "int", "uint", "bool" }.FirstOrDefault(p => type.StartsWith(p, StringComparison.Ordinal));
        if (prefix is null) return false;
        string dimensions = type[prefix.Length..];
        if (dimensions.Length == 0) return true;
        if (dimensions.Length == 1 && dimensions[0] is >= '1' and <= '4') { columns = dimensions[0] - '0'; return true; }
        if (prefix == "float" && dimensions.Length == 3 && dimensions[0] is >= '2' and <= '4' && dimensions[1] == 'x' && dimensions[2] is >= '2' and <= '4')
        { rows = dimensions[0] - '0'; columns = dimensions[2] - '0'; return true; }
        return false;
    }
}
