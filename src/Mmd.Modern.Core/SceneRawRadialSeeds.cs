using System.Numerics;

namespace Mmd.Modern.Core;

/// <summary>
/// Immutable unnormalized raw radial seeds in exactly the scene vertex order and local coordinate space.
/// A zero seed is valid; absence of an authored stream is represented by null, never by its magnitude.
/// </summary>
public sealed class SceneRawRadialSeeds
{
    private readonly Vector3[] vertices;
    public ReadOnlySpan<Vector3> Vertices => vertices;

    public SceneRawRadialSeeds(ReadOnlySpan<Vector3> vertices)
    {
        foreach (var value in vertices)
            if (!Finite(value)) throw new ArgumentException("Raw radial seeds must be finite.", nameof(vertices));
        this.vertices = vertices.ToArray();
    }

    /// <summary>
    /// Produces seeds from correspondence-matched undeformed source-axis positions and explicitly supplied
    /// texture space. The orthogonal direction-only basis may include a reflection. No bounds, normalization,
    /// skinning, normal projection, translation or head transform is inferred or applied.
    /// </summary>
    public static SceneRawRadialSeeds FromRestPositions(ReadOnlySpan<Vector3> sourceRestPositions,
        Vector3 textureSpaceLocation, Vector3 textureSpaceHalfSize, Matrix4x4 sourceToSceneBasis)
    {
        if (!Finite(textureSpaceLocation))
            throw new ArgumentException("Texture-space location must be finite.", nameof(textureSpaceLocation));
        if (!Finite(textureSpaceHalfSize) || textureSpaceHalfSize.X == 0
            || textureSpaceHalfSize.Y == 0 || textureSpaceHalfSize.Z == 0)
            throw new ArgumentException("Texture-space half sizes must be finite and nonzero.", nameof(textureSpaceHalfSize));
        ValidateBasis(sourceToSceneBasis);
        var values = new Vector3[sourceRestPositions.Length];
        for (int i = 0; i < values.Length; i++)
        {
            var rest = sourceRestPositions[i];
            if (!Finite(rest))
                throw new ArgumentException("Source rest positions must be finite.", nameof(sourceRestPositions));
            // Double intermediates avoid overflow in rest-c and 4*s for otherwise representable seeds.
            var sourceSeed = new Vector3(
                (float)(-((double)rest.Y - textureSpaceLocation.Y) / (4d * textureSpaceHalfSize.Y)),
                (float)(((double)rest.X - textureSpaceLocation.X) / (4d * textureSpaceHalfSize.X)), 0);
            values[i] = Vector3.TransformNormal(sourceSeed, sourceToSceneBasis);
            if (!Finite(values[i]))
                throw new ArgumentException("Explicit texture space produces a nonfinite raw radial seed.", nameof(sourceRestPositions));
        }
        return new(values);
    }

    private static void ValidateBasis(Matrix4x4 basis)
    {
        var x = new Vector3(basis.M11, basis.M12, basis.M13);
        var y = new Vector3(basis.M21, basis.M22, basis.M23);
        var z = new Vector3(basis.M31, basis.M32, basis.M33);
        const float tolerance = 1e-5f;
        if (!Finite(x) || !Finite(y) || !Finite(z)
            || basis.M14 != 0 || basis.M24 != 0 || basis.M34 != 0
            || basis.M41 != 0 || basis.M42 != 0 || basis.M43 != 0 || basis.M44 != 1
            || MathF.Abs(x.LengthSquared() - 1) > tolerance
            || MathF.Abs(y.LengthSquared() - 1) > tolerance
            || MathF.Abs(z.LengthSquared() - 1) > tolerance
            || MathF.Abs(Vector3.Dot(x, y)) > tolerance
            || MathF.Abs(Vector3.Dot(x, z)) > tolerance
            || MathF.Abs(Vector3.Dot(y, z)) > tolerance)
            throw new ArgumentException("Source-to-scene basis must be finite, orthogonal, unit-length and direction-only.", nameof(basis));
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
