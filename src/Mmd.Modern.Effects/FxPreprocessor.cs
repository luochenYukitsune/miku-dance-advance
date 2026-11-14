using System.Text;

namespace Mmd.Modern.Effects;

public sealed record PreprocessResult(string Text, IReadOnlyList<string> Includes, IReadOnlyList<MmeDiagnostic> Diagnostics);

/// <summary>
/// Minimal fx_2_0 preprocessor: <c>#include</c>, object-like <c>#define</c>, and the conditional
/// directives the corpus actually uses.
/// </summary>
/// <remarks>
/// Effect packs routinely ship a <c>.fxsub</c> library plus <c>#define</c> switches
/// (<c>#define US</c>, <c>#define TEXTURE_THRESHOLD "Main.png"</c>), so a loader that ignores the
/// preprocessor silently loses techniques. Unsupported conditional expressions produce errors;
/// they never silently select a true branch.
/// </remarks>
public static class FxPreprocessor
{
    public static PreprocessResult Process(
        string rootPath,
        Func<string, string?> readFile,
        string? rootText = null,
        int maxDepth = 32)
    {
        var diagnostics = new List<MmeDiagnostic>();
        var includes = new List<string>();
        var macros = new Dictionary<string, string>(StringComparer.Ordinal);
        var stack = new List<string>();

        string expanded = Expand(rootPath, rootText, readFile, macros, diagnostics, includes, stack, 0, maxDepth);
        return new PreprocessResult(expanded, includes, diagnostics);
    }

    private static string Expand(
        string path,
        string? text,
        Func<string, string?> readFile,
        Dictionary<string, string> macros,
        List<MmeDiagnostic> diagnostics,
        List<string> includes,
        List<string> stack,
        int depth,
        int maxDepth)
    {
        if (depth > maxDepth)
        {
            diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Error, MmeDiagnosticCodes.IncludeCycle,
                $"include nesting deeper than {maxDepth}", path));
            return string.Empty;
        }

        if (text is null)
        {
            text = readFile(path);
            if (text is null)
            {
                diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Error, MmeDiagnosticCodes.IncludeNotFound,
                    "file not found", path));
                return string.Empty;
            }
        }

        stack.Add(path);
        var output = new StringBuilder(text.Length + 256);
        var conditionals = new Stack<(bool ParentActive, bool Taken, bool Active)>();
        bool active = true;

        int lineNumber = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith('#'))
            {
                string directive = trimmed[1..].TrimStart();
                int space = directive.IndexOfAny([' ', '\t']);
                string keyword = space < 0 ? directive : directive[..space];
                string rest = space < 0 ? string.Empty : directive[(space + 1)..].Trim();

                switch (keyword.ToLowerInvariant())
                {
                    case "include":
                        if (!active)
                        {
                            break;
                        }

                        string? relative = StripQuotes(rest);
                        if (relative is null)
                        {
                            diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Error,
                                MmeDiagnosticCodes.SyntaxError, $"malformed #include: {rest}", path));
                            break;
                        }

                        string dir = Path.GetDirectoryName(path) ?? ".";
                        string full = Path.GetFullPath(Path.Combine(dir, relative));
                        if (stack.Contains(full, StringComparer.OrdinalIgnoreCase))
                        {
                            diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Warning,
                                MmeDiagnosticCodes.IncludeCycle, $"include cycle at {relative}", full));
                            break;
                        }

                        includes.Add(full);
                        output.AppendLine(Expand(full, null, readFile, macros, diagnostics, includes, stack, depth + 1, maxDepth));
                        break;

                    case "define":
                        if (!active)
                        {
                            break;
                        }

                        string name = rest;
                        string value = string.Empty;
                        int valueAt = rest.IndexOfAny([' ', '\t']);
                        if (valueAt >= 0)
                        {
                            name = rest[..valueAt];
                            value = rest[(valueAt + 1)..].Trim();
                        }

                        // Function-like macros are left alone: no effect in the corpus needs them,
                        // and expanding them textually would be wrong.
                        if (name.EndsWith(')') || name.Contains('('))
                        {
                            // Preserve identity for defined(); conditional calls remain unsupported.
                            int open = name.IndexOf('(');
                            if (open > 0) macros[name[..open]] = name[..open];
                            break;
                        }

                        // `#define FLAG // explanation` is common; the comment is not part of the value.
                        int comment = value.IndexOf("//", StringComparison.Ordinal);
                        if (comment >= 0)
                        {
                            value = value[..comment].TrimEnd();
                        }

                        macros[name] = value;
                        break;

                    case "undef":
                        if (active)
                        {
                            macros.Remove(rest.Trim());
                        }

                        break;

                    case "ifdef":
                    case "ifndef":
                        {
                            bool condition = EvaluateDefined(keyword, rest, macros);
                            conditionals.Push((active, condition, active && condition));
                            active = active && condition;
                        }

                        break;

                    case "if":
                        {
                            bool condition = active && EvaluateIf(rest, macros, diagnostics, path, lineNumber);
                            conditionals.Push((active, condition, active && condition));
                            active = active && condition;
                        }

                        break;

                    case "elif":
                        if (conditionals.Count == 0)
                        {
                            break;
                        }

                        if (conditionals.Peek().ParentActive)
                        {
                            active = !conditionals.Peek().Taken && EvaluateIf(rest, macros, diagnostics, path, lineNumber);
                            var top = conditionals.Pop();
                            conditionals.Push((top.ParentActive, top.Taken || active, true));
                        }
                        else
                        {
                            active = false;
                        }

                        break;

                    case "else":
                        if (conditionals.Count == 0)
                        {
                            break;
                        }

                        {
                            var top = conditionals.Pop();
                            active = top.ParentActive && !top.Taken;
                            conditionals.Push((top.ParentActive, true, true));
                        }

                        break;

                    case "endif":
                        if (conditionals.Count > 0)
                        {
                            active = conditionals.Pop().ParentActive;
                        }

                        break;

                    default:
                        if (active)
                        {
                            diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Info,
                                MmeDiagnosticCodes.UnknownDirective, $"ignored directive #{keyword}", path));
                        }

                        break;
                }

                output.Append('\n');
                continue;
            }

            output.AppendLine(active ? ExpandMacros(line, macros) : string.Empty);
        }

        stack.RemoveAt(stack.Count - 1);
        return output.ToString();
    }

    private static bool EvaluateDefined(string keyword, string name, Dictionary<string, string> macros)
    {
        bool defined = macros.ContainsKey(name.Trim());
        return keyword.Equals("ifdef", StringComparison.OrdinalIgnoreCase) ? defined : !defined;
    }

    private static bool EvaluateIf(string expression, Dictionary<string, string> macros,
        List<MmeDiagnostic> diagnostics, string path, int line)
    {
        try
        {
            return FxConditionalExpression.Evaluate(expression, macros);
        }
        catch (Exception error) when (error is FormatException or OverflowException or DivideByZeroException)
        {
            diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Error, MmeDiagnosticCodes.InvalidConditionalExpression,
                $"invalid or unsupported #if expression '{expression}': {error.Message}", path, line));
            return false;
        }
    }

    private static string? StripQuotes(string s)
    {
        int open = s.IndexOf('"');
        int close = s.LastIndexOf('"');
        return open >= 0 && close > open ? s[(open + 1)..close] : null;
    }

    private static string ExpandMacros(string line, Dictionary<string, string> macros)
    {
        if (macros.Count == 0 || line.Length == 0)
        {
            return line;
        }

        var sb = new StringBuilder(line.Length + 16);
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '"')
            {
                int start = i++;
                while (i < line.Length && line[i] != '"')
                {
                    i += line[i] == '\\' ? 2 : 1;
                }

                i = Math.Min(i + 1, line.Length);
                sb.Append(line, start, i - start);
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                {
                    i++;
                }

                string word = line[start..i];
                // Skip empty replacements: `#define US` is a guard, not a deletion request.
                if (macros.TryGetValue(word, out var value) && value.Length > 0)
                {
                    sb.Append(value);
                }
                else
                {
                    sb.Append(word);
                }

                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
