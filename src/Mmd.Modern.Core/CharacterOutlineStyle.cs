using System.Numerics;

namespace Mmd.Modern.Core;

/// <summary>Explicit modern character edge style. Null material metadata retains the source outline settings.</summary>
public sealed record CharacterOutlineStyle(float Width = .18f, Vector4? Color = null, bool Enabled = true);

public static class CharacterOutlineMath
{
    /// <summary>Neutral fallback for metadata-only callers. Rendering derives automatic color per pixel from surface textures.</summary>
    public static Vector4 DefaultColor => new(.35f, .35f, .35f, 1);

    public static void Validate(CharacterOutlineStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (!float.IsFinite(style.Width) || style.Width < 0)
            throw new ArgumentException("Character outline width must be finite and nonnegative.", nameof(style));
        if (style.Color is {} color && (!OriginalOutlineMath.Finite(color)
            || color.X < 0 || color.X > 1 || color.Y < 0 || color.Y > 1
            || color.Z < 0 || color.Z > 1 || color.W < 0 || color.W > 1))
            throw new ArgumentException("Character outline color components must be finite and between zero and one.", nameof(style));
    }

    public static OriginalOutlineMaterial ResolveMaterial(OriginalOutlineMaterial source, CharacterOutlineStyle? style)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (style is null) return source;
        Validate(style);
        return new(style.Width, style.Color ?? DefaultColor, style.Enabled && style.Width > 0);
    }

    /// <summary>Shares original edge expansion and vertex modes without modifying source geometry or materials.</summary>
    public static Vector4[] BuildPositions(OriginalOutlineData data, OriginalOutlineOptions options,
        IReadOnlyList<OriginalFrameMaterialFlags> flags)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(flags);
        if (flags.Count != data.Materials.Count || flags.Any(flag => flag is null))
            throw new ArgumentException("One non-null frame flag set per outline material is required.", nameof(flags));
        foreach (var flag in flags)
            if (flag.CharacterOutlineStyle is {} style) Validate(style);
        float modelBase = OriginalOutlineMath.ComputeModelBase(options);
        var result = new Vector4[data.Vertices.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var vertex = data.Vertices[i];
            int material = vertex.SelectedMaterialIndex;
            float width = flags[material].CharacterOutlineStyle?.Width ?? data.Materials[material].Width;
            result[i] = new(OriginalOutlineMath.ExpandPosition(vertex, modelBase, width), 1);
        }
        return result;
    }
}
