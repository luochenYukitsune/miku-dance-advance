using System.Globalization;
using System.Numerics;

namespace Mmd.Modern.Effects.Neo;

/// <summary>Strict literal defaults, deliberately not an HLSL expression interpreter.</summary>
internal static class EffectLiteralInitializer
{
    public static bool TryParse(EffectParameter parameter, out EffectValue value)
    {
        value = default;
        string text = parameter.Initializer?.Trim() ?? string.Empty;
        switch (parameter.Kind)
        {
            case EffectValueKind.Integer:
                if (!int.TryParse(Compact(text), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int integer)) return false;
                value = EffectValue.FromInteger(integer);
                return true;
            case EffectValueKind.Boolean:
                if (text is not ("true" or "false" or "0" or "1")) return false;
                value = EffectValue.FromBoolean(text is "true" or "1");
                return true;
            case EffectValueKind.String:
                var tokens = FxTokenizer.Tokenize(text);
                if (tokens.Count != 2 || tokens[0].Kind != FxTokenKind.String) return false;
                value = EffectValue.FromText(tokens[0].Text);
                return true;
            case EffectValueKind.Scalar:
                if (!Number(text, out float scalar)) return false;
                value = EffectValue.FromScalar(scalar);
                return true;
            case EffectValueKind.Vector2:
            case EffectValueKind.Vector3:
            case EffectValueKind.Vector4:
            case EffectValueKind.Matrix:
                int length = parameter.Kind switch
                {
                    EffectValueKind.Vector2 => 2, EffectValueKind.Vector3 => 3, EffectValueKind.Vector4 => 4, _ => 16,
                };
                if (text.StartsWith('{') && text.EndsWith('}')) text = text[1..^1];
                else
                {
                    int open = text.IndexOf('(');
                    if (open < 0 || !text.EndsWith(')') || text[..open].Trim() != parameter.DeclaredType) return false;
                    text = text[(open + 1)..^1];
                }
                string[] parts = text.Split(',');
                if (parts.Length != length) return false;
                var numbers = new float[length];
                for (int i = 0; i < length; i++) if (!Number(parts[i], out numbers[i])) return false;
                value = length == 16
                    ? EffectValue.FromMatrix(new Matrix4x4(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5], numbers[6], numbers[7], numbers[8], numbers[9], numbers[10], numbers[11], numbers[12], numbers[13], numbers[14], numbers[15]))
                    : EffectValue.FromVector(new Vector4(numbers[0], numbers[1], length > 2 ? numbers[2] : 0, length > 3 ? numbers[3] : 0), parameter.Kind);
                return true;
            default:
                return false;
        }
    }

    private static string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
    private static bool Number(string text, out float number) =>
        float.TryParse(Compact(text).TrimEnd('f', 'F', 'h', 'H'), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
        && float.IsFinite(number);
}
