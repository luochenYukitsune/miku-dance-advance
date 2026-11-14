namespace Mmd.Modern.Effects;

/// <summary>
/// Structural parser for fx_2_0 sources.
/// </summary>
/// <remarks>
/// This is not a shader compiler: HLSL bodies are skipped verbatim. What it must recover is the
/// part MME itself interprets at load time 鈥?parameters and their <b>semantics</b>, techniques,
/// technique annotations (notably <c>MMDPass</c> and <c>Script</c>), and passes.
/// Names are matched case-insensitively because the runtime lowercases them before lookup.
/// </remarks>
public static class FxParser
{
    public static FxEffect Parse(
        IReadOnlyList<FxToken> tokens,
        string path,
        ICollection<MmeDiagnostic> diagnostics,
        string? sourceText = null)
    {
        var state = new State(tokens, path, diagnostics, sourceText);
        return state.ParseEffect() with { Source = sourceText ?? string.Empty };
    }

    private sealed class State
    {
        private readonly IReadOnlyList<FxToken> _tokens;
        private readonly string _path;
        private readonly ICollection<MmeDiagnostic> _diag;
        private readonly string? _sourceText;
        private readonly List<FxParameter> _parameters = [];
        private readonly List<FxTechnique> _techniques = [];
        private readonly List<string> _includes = [];
        private int _pos;

        public State(IReadOnlyList<FxToken> tokens, string path, ICollection<MmeDiagnostic> diagnostics, string? sourceText)
        {
            _tokens = tokens;
            _path = path;
            _diag = diagnostics;
            _sourceText = sourceText;
        }

        private FxToken Current => _tokens[Math.Min(_pos, _tokens.Count - 1)];

        private FxToken Peek(int offset = 1) => _tokens[Math.Min(_pos + offset, _tokens.Count - 1)];

        private void Advance() => _pos++;

        public FxEffect ParseEffect()
        {
            while (Current.Kind != FxTokenKind.End)
            {
                if (Current.Kind != FxTokenKind.Identifier)
                {
                    Advance();
                    continue;
                }

                switch (Current.Text.ToLowerInvariant())
                {
                    case "technique":
                        ParseTechnique();
                        break;
                    case "struct":
                        SkipStruct();
                        break;
                    case "typedef":
                        SkipToSemicolon();
                        break;
                    case "sampler":
                    case "sampler1d":
                    case "sampler2d":
                    case "sampler3d":
                    case "samplercube":
                    case "texture":
                    case "texture1d":
                    case "texture2d":
                    case "texture3d":
                    case "texturecube":
                    case "string":
                    case "stateblock":
                        ParseDeclaration(Current.Text);
                        break;
                    case "const":
                    case "shared":
                    case "static":
                    case "uniform":
                    case "extern":
                    case "volatile":
                    case "precise":
                    case "row_major":
                    case "column_major":
                        // Storage/qualifier keywords precede the real type; drop and re-dispatch.
                        Advance();
                        break;
                    default:
                        ParseTypeDeclaration();
                        break;
                }
            }

            return new FxEffect(_path, _parameters, _techniques, _includes);
        }

        private void ParseTechnique()
        {
            Advance(); // technique
            if (Current.Kind != FxTokenKind.Identifier)
            {
                Error(MmeDiagnosticCodes.SyntaxError, "technique without a name", Current);
                return;
            }

            string name = Current.Text;
            Advance();

            var annotations = Current.Is("<") ? ParseAnnotationBlock() : EmptyAnnotations();

            if (!Expect("{"))
            {
                return;
            }

            var passes = new List<FxPass>();
            while (Current.Kind != FxTokenKind.End && !Current.Is("}"))
            {
                if (Current.Is("pass"))
                {
                    passes.Add(ParsePass());
                    continue;
                }

                Advance();
            }

            Expect("}");
            _techniques.Add(new FxTechnique(name, annotations, passes));
        }

        private FxPass ParsePass()
        {
            Advance(); // pass
            string name = Current.Kind == FxTokenKind.Identifier ? Current.Text : "P0";
            if (Current.Kind == FxTokenKind.Identifier)
            {
                Advance();
            }

            var annotations = Current.Is("<") ? ParseAnnotationBlock() : EmptyAnnotations();

            if (!Expect("{"))
            {
                return new FxPass(name, annotations, EmptyAnnotations(), []);
            }

            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var profiles = new List<string>();

            int depth = 1;
            while (Current.Kind != FxTokenKind.End)
            {
                if (Current.Is("{"))
                {
                    depth++;
                    Advance();
                    continue;
                }

                if (Current.Is("}"))
                {
                    depth--;
                    Advance();
                    if (depth == 0)
                    {
                        break;
                    }

                    continue;
                }

                if (Current.Kind == FxTokenKind.Identifier && Peek().Is("="))
                {
                    string key = Current.Text;
                    Advance();
                    Advance();

                    var value = new List<string>();
                    int inner = 0;
                    while (Current.Kind != FxTokenKind.End)
                    {
                        if (Current.Is("(") || Current.Is("[") || Current.Is("{"))
                        {
                            inner++;
                        }
                        else if (Current.Is(")") || Current.Is("]") || Current.Is("}"))
                        {
                            if (inner == 0)
                            {
                                break;
                            }

                            inner--;
                        }
                        else if (Current.Is(";") && inner == 0)
                        {
                            break;
                        }

                        value.Add(Current.Text);
                        Advance();
                    }

                    Expect(";");

                    string joined = string.Join(' ', value);
                    if (!states.ContainsKey(key))
                    {
                        states[key] = joined;
                    }

                    string lowered = joined.ToLowerInvariant();
                    int at = lowered.IndexOf("compile ", StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        var parts = lowered[(at + 8)..].Split([' ', '\t', '('], StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0 && !profiles.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
                        {
                            profiles.Add(parts[0]);
                        }
                    }

                    continue;
                }

                Advance();
            }

            return new FxPass(name, annotations, states, profiles);
        }

        private void ParseDeclaration(string typeName)
        {
            Advance(); // type keyword
            if (Current.Kind != FxTokenKind.Identifier)
            {
                SkipToSemicolon();
                return;
            }

            string name = Current.Text;
            Advance();
            string? semantic = Current.Is(":") ? ParseSemantic() : null;

            var annotations = Current.Is("<") ? ParseAnnotationBlock() : EmptyAnnotations();

            string? initializer = null;
            if (Current.Is("="))
            {
                Advance();
                initializer = CollectUntilSemicolon();
            }
            else
            {
                SkipToSemicolon();
            }

            _parameters.Add(new FxParameter(typeName.ToLowerInvariant(), name, semantic, annotations, initializer));
        }

        /// <summary>
        /// Reads the token after ':'. <c>register(c0)</c> is a shader register binding, not a
        /// semantic, so it is consumed and reported as none.
        /// </summary>
        private string? ParseSemantic()
        {
            Advance(); // ':'
            if (Current.Kind != FxTokenKind.Identifier)
            {
                return null;
            }

            if (Current.Is("register") && Peek().Is("("))
            {
                Advance();
                SkipBalanced("(", ")");
                return null;
            }

            string semantic = Current.Text;
            Advance();
            return semantic;
        }

        private void ParseTypeDeclaration()
        {
            string type = Current.Text;
            Advance();

            if (Current.Kind != FxTokenKind.Identifier)
            {
                // Stray expression or a keyword we do not model; resynchronise on ';' / '}'.
                Resynchronise();
                return;
            }

            string name = Current.Text;
            Advance();

            if (Current.Is("("))
            {
                SkipBalanced("(", ")");
                if (Current.Is(":"))
                {
                    Advance();
                    if (Current.Kind == FxTokenKind.Identifier)
                    {
                        Advance();
                    }
                }

                if (Current.Is("{"))
                {
                    SkipBalanced("{", "}");
                }
                else
                {
                    SkipToSemicolon();
                }

                return;
            }

            string? semantic = Current.Is(":") ? ParseSemantic() : null;

            var annotations = Current.Is("<") ? ParseAnnotationBlock() : EmptyAnnotations();

            string? initializer = null;
            if (Current.Is("="))
            {
                Advance();
                initializer = CollectUntilSemicolon();
            }
            else
            {
                SkipToSemicolon();
            }

            _parameters.Add(new FxParameter(type.ToLowerInvariant(), name, semantic, annotations, initializer));
        }

        private void SkipStruct()
        {
            Advance(); // struct
            if (Current.Kind == FxTokenKind.Identifier)
            {
                Advance();
            }

            if (Current.Is("{"))
            {
                SkipBalanced("{", "}");
            }

            SkipToSemicolon();
        }

        private Dictionary<string, string> ParseAnnotationBlock()
        {
            var annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int blockStart = _pos;
            Advance(); // '<'

            while (Current.Kind != FxTokenKind.End && !Current.Is(">"))
            {
                // type name = value ;
                if (Current.Kind != FxTokenKind.Identifier)
                {
                    Advance();
                    continue;
                }

                Advance(); // annotation type
                if (Current.Kind != FxTokenKind.Identifier)
                {
                    return Recover(blockStart, "annotation block is not well formed");
                }

                string key = Current.Text;
                Advance();

                if (!Current.Is("="))
                {
                    // Real-world cause: a `#if/#else` pair keeps two declarations of the same
                    // texture, so the first block runs into the second declaration's `:`.
                    // Rewind so the outer parser can re-read the declaration instead of losing it.
                    return Recover(blockStart, $"annotation '{key}' has no value");
                }

                Advance();

                // String-literal concatenation: "a;" "b;" means "a;b;".
                var literal = new System.Text.StringBuilder();
                bool sawString = false;
                while (Current.Kind == FxTokenKind.String)
                {
                    literal.Append(Current.Text);
                    sawString = true;
                    Advance();
                }

                string value;
                if (sawString)
                {
                    value = literal.ToString();
                }
                else
                {
                    var raw = new List<string>();
                    while (Current.Kind != FxTokenKind.End && !Current.Is(";") && !Current.Is(">"))
                    {
                        raw.Add(Current.Text);
                        Advance();
                    }

                    value = string.Join(' ', raw);
                }

                annotations[key] = value;
                Expect(";");
            }

            Expect(">");
            return annotations;
        }

        private string CollectUntilSemicolon()
        {
            var raw = new List<string>();
            int start = Current.SourceOffset;
            int depth = 0;
            while (Current.Kind != FxTokenKind.End)
            {
                if (Current.Is("(") || Current.Is("[") || Current.Is("{"))
                {
                    depth++;
                }
                else if (Current.Is(")") || Current.Is("]") || Current.Is("}"))
                {
                    if (depth == 0)
                    {
                        break;
                    }

                    depth--;
                }
                else if (Current.Is(";") && depth == 0)
                {
                    break;
                }

                raw.Add(Current.Kind == FxTokenKind.String ? Quote(Current.Text) : Current.Text);
                Advance();
            }

            int end = Current.SourceOffset;
            Expect(";");
            return _sourceText is not null && start >= 0 && end >= start
                ? _sourceText[start..end].Trim() : string.Join(' ', raw);
        }

        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

        private void SkipBalanced(string open, string close)
        {
            int depth = 0;
            while (Current.Kind != FxTokenKind.End)
            {
                if (Current.Text == open)
                {
                    depth++;
                }
                else if (Current.Text == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        Advance();
                        return;
                    }
                }

                Advance();
            }
        }

        private void SkipToSemicolon()
        {
            while (Current.Kind != FxTokenKind.End && !Current.Is(";") && !Current.Is("}"))
            {
                Advance();
            }

            if (Current.Is(";"))
            {
                Advance();
            }
        }

        private void Resynchronise()
        {
            while (Current.Kind != FxTokenKind.End && !Current.Is(";") && !Current.Is("}"))
            {
                Advance();
            }

            if (Current.Is(";"))
            {
                Advance();
            }
        }

        private bool Expect(string symbol)
        {
            if (Current.Kind == FxTokenKind.End)
            {
                return false;
            }

            if (Current.Is(symbol))
            {
                Advance();
                return true;
            }

            Error(MmeDiagnosticCodes.UnexpectedToken, $"expected '{symbol}' but found '{Current.Text}'", Current);
            return false;
        }

        private void Error(string code, string message, FxToken token) =>
            _diag.Add(new MmeDiagnostic(DiagnosticSeverity.Error, code, message, _path, token.Line, token.Column));

        private void Warning(string code, string message) =>
            _diag.Add(new MmeDiagnostic(DiagnosticSeverity.Warning, code, message, _path, Current.Line, Current.Column));

        /// <summary>
        /// Abandons a malformed annotation block without losing the surrounding declaration:
        /// rewinds to just after the opening '&lt;' so the caller re-reads the declaration.
        /// </summary>
        private Dictionary<string, string> Recover(int blockStart, string message)
        {
            Warning(MmeDiagnosticCodes.UnterminatedAnnotation, message);
            _pos = blockStart + 1;
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, string> EmptyAnnotations() => new(StringComparer.OrdinalIgnoreCase);
    }
}
