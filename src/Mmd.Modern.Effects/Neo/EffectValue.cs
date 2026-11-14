using System.Globalization;
using System.Numerics;

namespace Mmd.Modern.Effects.Neo;

/// <summary>A single bound parameter value. Textures are referenced by name, not by GPU handle.</summary>
public readonly record struct EffectValue(
    EffectValueKind Kind,
    Matrix4x4 Matrix = default,
    Vector4 Vector = default,
    float Scalar = 0f,
    int Integer = 0,
    bool Boolean = false,
    string? Text = null)
{
    public static EffectValue FromMatrix(Matrix4x4 value) => new(EffectValueKind.Matrix, Matrix: value);

    public static EffectValue FromVector(Vector4 value, EffectValueKind kind = EffectValueKind.Vector4) =>
        new(kind, Vector: value);

    public static EffectValue FromVector(Vector3 value) =>
        new(EffectValueKind.Vector3, Vector: new Vector4(value, 0f));

    public static EffectValue FromVector(Vector2 value) =>
        new(EffectValueKind.Vector2, Vector: new Vector4(value, 0f, 0f));

    public static EffectValue FromScalar(float value) => new(EffectValueKind.Scalar, Scalar: value);

    public static EffectValue FromInteger(int value) => new(EffectValueKind.Integer, Integer: value);

    public static EffectValue FromBoolean(bool value) => new(EffectValueKind.Boolean, Boolean: value);

    public static EffectValue FromText(string value) => new(EffectValueKind.String, Text: value);

    public float AsFloat() => Kind switch
    {
        EffectValueKind.Scalar => Scalar,
        EffectValueKind.Integer => Integer,
        EffectValueKind.Boolean => Boolean ? 1f : 0f,
        EffectValueKind.Vector4 => Vector.X,
        EffectValueKind.Vector3 => Vector.X,
        EffectValueKind.Vector2 => Vector.X,
        _ => 0f,
    };

    public Vector4 AsVector4() => Kind switch
    {
        EffectValueKind.Scalar => new Vector4(Scalar),
        EffectValueKind.Integer => new Vector4(Integer),
        EffectValueKind.Vector4 or EffectValueKind.Vector3 or EffectValueKind.Vector2 => Vector,
        _ => Vector4.Zero,
    };
}

/// <summary>Parameter values for one effect instance, addressed by parameter name.</summary>
public sealed class EffectParameterValues
{
    private readonly Dictionary<string, EffectValue> _values = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _values.Count;

    public void Set(string name, EffectValue value) => _values[name] = value;

    public bool TryGet(string name, out EffectValue value) => _values.TryGetValue(name, out value);

    public IReadOnlyDictionary<string, EffectValue> All => _values;

    /// <summary>
    /// Declared literal defaults first, then preview placeholders for parameters without initializers.
    /// Unsupported initialized parameters remain unbound; they must never acquire invented defaults.
    /// </summary>
    public static EffectParameterValues CreateDefaults(EffectDefinition definition)
    {
        var values = new EffectParameterValues();
        foreach (var parameter in definition.Parameters)
        {
            if (parameter.DefaultValue is { } declared)
            {
                values.Set(parameter.Name, declared);
                continue;
            }
            if (parameter.Initializer is not null) continue;
            values.Set(parameter.Name, parameter.Kind switch
            {
                EffectValueKind.Matrix => EffectValue.FromMatrix(Matrix4x4.Identity),
                EffectValueKind.Vector4 => EffectValue.FromVector(Vector4.Zero),
                EffectValueKind.Vector3 => EffectValue.FromVector(Vector3.Zero),
                EffectValueKind.Vector2 => EffectValue.FromVector(Vector2.One),
                EffectValueKind.Scalar => EffectValue.FromScalar(0f),
                EffectValueKind.Integer => EffectValue.FromInteger(1),
                EffectValueKind.Boolean => EffectValue.FromBoolean(true),
                EffectValueKind.String => EffectValue.FromText(string.Empty),
                _ => EffectValue.FromText(parameter.Name),
            });
        }

        return values;
    }
}

/// <summary>
/// Parses the annotation forms legacy effects use to size render targets.
/// </summary>
public static class EffectAnnotationValues
{
    public static bool TryParseVector2(string? text, out Vector2 value)
    {
        value = default;
        var numbers = ParseNumbers(text);
        if (numbers.Count < 1)
        {
            return false;
        }

        value = new Vector2(numbers[0], numbers.Count > 1 ? numbers[1] : numbers[0]);
        return true;
    }

    public static List<float> ParseNumbers(string? text)
    {
        var result = new List<float>(4);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (string part in text.Split(['{', '}', ',', '(', ')', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
            {
                result.Add(number);
            }
        }

        return result;
    }
}

/// <summary>Render-target formats named by effect annotations (<c>string Format = "R16F";</c>).</summary>
public enum EffectRenderTargetFormat
{
    Unknown,
    R8G8B8A8_UNorm,
    R16G16B16A16_Float,
    R32G32B32A32_Float,
    R16_Float,
    R32_Float,
    R32G32_Float,
    R8_UNorm,
    D24S8,
}

public static class EffectFormats
{
    public static EffectRenderTargetFormat Parse(string? text)
    {
        string key = text?.Trim().ToUpperInvariant() ?? string.Empty;
        if (key.StartsWith("D3DFMT_", StringComparison.Ordinal))
        {
            key = key["D3DFMT_".Length..];
        }

        return key switch
        {
            "A8R8G8B8" or "X8R8G8B8" or "R8G8B8A8" => EffectRenderTargetFormat.R8G8B8A8_UNorm,
            "A16B16G16R16F" or "R16G16B16A16F" or "A16B16G16R16_FLOAT" => EffectRenderTargetFormat.R16G16B16A16_Float,
            "A32B32G32R32F" or "R32G32B32A32F" => EffectRenderTargetFormat.R32G32B32A32_Float,
            "R16F" => EffectRenderTargetFormat.R16_Float,
            "R32F" => EffectRenderTargetFormat.R32_Float,
            "G32R32F" or "R32G32F" => EffectRenderTargetFormat.R32G32_Float,
            "L8" or "R8" or "A8" => EffectRenderTargetFormat.R8_UNorm,
            "D24S8" or "D24X8" => EffectRenderTargetFormat.D24S8,
            _ => EffectRenderTargetFormat.Unknown,
        };
    }
}
