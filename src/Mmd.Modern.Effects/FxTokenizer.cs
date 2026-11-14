using System.Globalization;

namespace Mmd.Modern.Effects;

public enum FxTokenKind
{
    Identifier,
    Number,
    String,
    Symbol,
    End,
}

public readonly record struct FxToken(FxTokenKind Kind, string Text, int Line, int Column, int SourceOffset = -1)
{
    public bool Is(string text) => string.Equals(Text, text, StringComparison.Ordinal);
}

/// <summary>
/// A deliberately small fx_2_0 tokenizer: enough to recover declarations, annotations,
/// techniques and passes while skipping HLSL function bodies untouched.
/// </summary>
public static class FxTokenizer
{
    private static readonly string[] MultiCharSymbols =
    [
        "++", "--", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||",
        "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "::",
    ];

    public static List<FxToken> Tokenize(string text)
    {
        var tokens = new List<FxToken>(256);
        int i = 0, line = 1, lineStart = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (c == '\n')
            {
                line++;
                i++;
                lineStart = i;
                continue;
            }

            if (c is ' ' or '\t' or '\r')
            {
                i++;
                continue;
            }

            // Comments.
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    if (text[i] == '\n')
                    {
                        line++;
                        lineStart = i + 1;
                    }

                    i++;
                }

                i = Math.Min(i + 2, text.Length);
                continue;
            }

            // Preprocessor lines are consumed whole: the preprocessor runs before tokenizing,
            // but a direct (unexpanded) scan must not trip over them.
            if (c == '#' && IsOnlyWhitespaceBeforeOnLine(text, i, lineStart))
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            int col = i - lineStart + 1;

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new FxToken(FxTokenKind.Identifier, text[start..i], line, col, start));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                int start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.'))
                {
                    i++;
                }

                tokens.Add(new FxToken(FxTokenKind.Number, text[start..i], line, col, start));
                continue;
            }

            if (c == '"')
            {
                int start = ++i;
                var sb = new System.Text.StringBuilder();
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        sb.Append(text[i + 1] switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            _ => text[i + 1],
                        });
                        i += 2;
                        continue;
                    }

                    if (text[i] == '\n')
                    {
                        line++;
                        lineStart = i + 1;
                    }

                    sb.Append(text[i]);
                    i++;
                }

                _ = start;
                if (i < text.Length)
                {
                    i++;
                }

                tokens.Add(new FxToken(FxTokenKind.String, sb.ToString(), line, col, start - 1));
                continue;
            }

            string? multi = null;
            foreach (var m in MultiCharSymbols)
            {
                if (i + m.Length <= text.Length && string.CompareOrdinal(text, i, m, 0, m.Length) == 0)
                {
                    multi = m;
                    break;
                }
            }

            if (multi is not null)
            {
                tokens.Add(new FxToken(FxTokenKind.Symbol, multi, line, col, i));
                i += multi.Length;
                continue;
            }

            tokens.Add(new FxToken(FxTokenKind.Symbol, c.ToString(CultureInfo.InvariantCulture), line, col, i));
            i++;
        }

        tokens.Add(new FxToken(FxTokenKind.End, string.Empty, line, 1, text.Length));
        return tokens;
    }

    private static bool IsOnlyWhitespaceBeforeOnLine(string text, int index, int lineStart)
    {
        for (int i = lineStart; i < index; i++)
        {
            if (text[i] is not (' ' or '\t'))
            {
                return false;
            }
        }

        return true;
    }
}
