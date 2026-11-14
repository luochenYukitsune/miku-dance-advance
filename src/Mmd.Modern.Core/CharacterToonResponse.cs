namespace Mmd.Modern.Core;

/// <summary>Optional modern-character main-light response. The original bright and dark palette endpoints remain unchanged.
/// Null material metadata keeps the original continuous response. Threshold and width address the already clamped
/// min(normal lighting, shadow visibility) scalar; they do not alter textures, highlight, alpha or outline inputs.</summary>
public sealed record CharacterToonResponse(float Threshold = .5f, float TransitionWidth = .1f);

public static class CharacterToonMath
{
    public static void Validate(CharacterToonResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        float lower = response.Threshold - response.TransitionWidth * .5f;
        float upper = response.Threshold + response.TransitionWidth * .5f;
        if (!float.IsFinite(response.Threshold) || !float.IsFinite(response.TransitionWidth)
            || response.Threshold < 0 || response.Threshold > 1 || response.TransitionWidth <= 0
            || lower < 0 || upper > 1 || lower >= upper)
            throw new ArgumentException("Character toon threshold and transition must form a finite positive-width interval within zero and one.", nameof(response));
    }

    /// <summary>Smoothstep of the existing light scalar. This remaps the palette blend, not the texture RGB.</summary>
    public static float Evaluate(float combinedLight, CharacterToonResponse response)
    {
        Validate(response);
        if (!float.IsFinite(combinedLight)) throw new ArgumentOutOfRangeException(nameof(combinedLight));
        float lower = response.Threshold - response.TransitionWidth * .5f;
        float t = Math.Clamp((combinedLight - lower) / response.TransitionWidth, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
