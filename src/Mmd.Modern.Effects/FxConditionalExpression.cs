using System.Globalization;

namespace Mmd.Modern.Effects;

/// <summary>Integer preprocessor expressions. Unsupported syntax fails closed with a diagnostic.</summary>
internal sealed class FxConditionalExpression
{
    private readonly IReadOnlyList<string> _tokens;
    private readonly IReadOnlyDictionary<string, string> _macros;
    private int _position;

    private FxConditionalExpression(IReadOnlyList<string> tokens, IReadOnlyDictionary<string, string> macros)
    {
        _tokens = tokens;
        _macros = macros;
    }

    public static bool Evaluate(string expression, IReadOnlyDictionary<string, string> macros)
    {
        var parser = new FxConditionalExpression(Expand(Tokenize(expression), macros, []), macros);
        long value = parser.Binary(1, true);
        if (parser._position != parser._tokens.Count)
            throw new FormatException($"unsupported token '{parser.Current}'");
        return value != 0;
    }

    private string Current => _position < _tokens.Count ? _tokens[_position] : string.Empty;
    private bool Take(string token)
    {
        if (Current != token) return false;
        _position++;
        return true;
    }

    private long Binary(int minimum, bool evaluate)
    {
        long left = Unary(evaluate);
        while (Precedence(Current) >= minimum)
        {
            string op = _tokens[_position++];
            bool rightEvaluated = evaluate && !(op == "&&" && left == 0) && !(op == "||" && left != 0);
            long right = Binary(Precedence(op) + 1, rightEvaluated);
            if (evaluate) left = Apply(op, left, right);
        }
        return left;
    }

    private long Unary(bool evaluate)
    {
        if (Take("!")) return Unary(evaluate) == 0 ? 1 : 0;
        if (Take("~")) return ~Unary(evaluate);
        if (Take("+")) return Unary(evaluate);
        if (Take("-")) { long value = Unary(evaluate); return evaluate ? checked(-value) : 0; }
        if (Take("("))
        {
            long value = Binary(1, evaluate);
            if (!Take(")")) throw new FormatException("missing ')'");
            return value;
        }
        if (Take("defined"))
        {
            bool parenthesized = Take("(");
            string name = Current;
            if (!IsIdentifier(name)) throw new FormatException("defined requires a macro name");
            _position++;
            if (parenthesized && !Take(")")) throw new FormatException("missing ')' after defined");
            return _macros.ContainsKey(name) ? 1 : 0;
        }
        string token = Current;
        if (token.Length == 0) throw new FormatException("expected integer expression");
        _position++;
        if (IsIdentifier(token)) return token == "true" ? 1 : 0; // Undefined identifiers are zero.
        if (token.Contains('u') || token.Contains('U')) throw new FormatException("unsigned conditional arithmetic is unsupported");
        string number = token.TrimEnd('l', 'L');
        if (number.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(number[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hex)
                && hex <= long.MaxValue) return (long)hex;
        }
        else if (number.Length > 1 && number[0] == '0')
        {
            long octal = 0;
            foreach (char digit in number)
            {
                if (digit is < '0' or > '7') throw new FormatException($"invalid octal literal '{token}'");
                octal = checked(octal * 8 + digit - '0');
            }
            return octal;
        }
        else if (long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out long value)) return value;
        throw new FormatException($"unsupported integer literal '{token}'");
    }

    private static int Precedence(string op) => op switch
    {
        "||" => 1, "&&" => 2, "|" => 3, "^" => 4, "&" => 5,
        "==" or "!=" => 6, "<" or ">" or "<=" or ">=" => 7,
        "<<" or ">>" => 8, "+" or "-" => 9, "*" or "/" or "%" => 10, _ => 0,
    };

    private static long Apply(string op, long left, long right) => op switch
    {
        "||" => left != 0 || right != 0 ? 1 : 0,
        "&&" => left != 0 && right != 0 ? 1 : 0,
        "==" => left == right ? 1 : 0, "!=" => left != right ? 1 : 0,
        "<" => left < right ? 1 : 0, ">" => left > right ? 1 : 0,
        "<=" => left <= right ? 1 : 0, ">=" => left >= right ? 1 : 0,
        "+" => checked(left + right), "-" => checked(left - right), "*" => checked(left * right),
        "/" => left / right, "%" => left % right,
        "&" => left & right, "|" => left | right, "^" => left ^ right,
        "<<" when right is >= 0 and < 64 => checked((long)((Int128)left << (int)right)),
        ">>" when right is >= 0 and < 64 => left >> (int)right,
        _ => throw new FormatException($"unsupported operator or shift count '{op}'"),
    };

    private static List<string> Expand(IReadOnlyList<string> tokens, IReadOnlyDictionary<string, string> macros, HashSet<string> expanding)
    {
        var result = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (token == "defined")
            {
                result.Add(token);
                if (i + 1 < tokens.Count && tokens[i + 1] == "(") result.Add(tokens[++i]);
                if (i + 1 < tokens.Count) result.Add(tokens[++i]);
                continue;
            }
            if (!macros.TryGetValue(token, out string? replacement)) { result.Add(token); continue; }
            if (expanding.Count >= 64 || !expanding.Add(token)) throw new FormatException($"recursive macro '{token}'");
            result.AddRange(Expand(Tokenize(replacement), macros, expanding));
            expanding.Remove(token);
            if (result.Count > 16384) throw new FormatException("conditional macro expansion is too large");
        }
        return result;
    }

    private static bool IsIdentifier(string token) => token.Length > 0 && (char.IsLetter(token[0]) || token[0] == '_');

    private static List<string> Tokenize(string expression)
    {
        var tokens = new List<string>();
        for (int i = 0; i < expression.Length;)
        {
            if (char.IsWhiteSpace(expression[i])) { i++; continue; }
            if (i + 1 < expression.Length && expression[i..(i + 2)] == "//") break;
            if (i + 1 < expression.Length && expression[i..(i + 2)] == "/*")
            {
                int end = expression.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new FormatException("unterminated conditional comment");
                i = end + 2;
                continue;
            }
            int start = i++;
            if (char.IsLetterOrDigit(expression[start]) || expression[start] == '_')
            {
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_')) i++;
            }
            else if (i < expression.Length && expression[start..(i + 1)] is "&&" or "||" or "==" or "!=" or "<=" or ">=" or "<<" or ">>") i++;
            tokens.Add(expression[start..i]);
        }
        return tokens;
    }
}
