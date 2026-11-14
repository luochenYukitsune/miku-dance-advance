using System.Numerics;

namespace Mmd.Modern.Effects.Neo;

/// <summary>Per-material inputs MME exposes to an effect.</summary>
public sealed record EffectMaterialState
{
    public Vector4 Diffuse { get; init; } = new(1f, 1f, 1f, 1f);

    public Vector4 Ambient { get; init; } = Vector4.Zero;

    public Vector4 Specular { get; init; } = Vector4.Zero;

    public Vector4 Emissive { get; init; } = Vector4.Zero;

    public float SpecularPower { get; init; }

    public Vector4 ToonColor { get; init; } = new(1f, 1f, 1f, 1f);
}

/// <summary>The frame-level inputs a host must supply so effects can bind by semantic.</summary>
public sealed record EffectFrameState
{
    public Matrix4x4 World { get; init; } = Matrix4x4.Identity;

    public Matrix4x4 View { get; init; } = Matrix4x4.Identity;

    public Matrix4x4 Projection { get; init; } = Matrix4x4.Identity;

    public Vector3 CameraPosition { get; init; }

    public Vector3 LightDirection { get; init; } = -Vector3.UnitY;

    public EffectMaterialState Material { get; init; } = new();

    public Vector4 EdgeColor { get; init; }

    public Vector4 GroundShadowColor { get; init; }

    public float Time { get; init; }

    public float Time2 { get; init; }

    public int VertexCount { get; init; }

    public Vector2 ViewportPixelSize { get; init; } = Vector2.One;

    public Vector2 ViewportRatio { get; init; } = Vector2.One;
}

public sealed record SemanticBindingResult(
    int Bound,
    int UnboundTextures,
    int UnboundModelBindings,
    IReadOnlyList<string> UnknownSemantics);

/// <summary>
/// Fills effect parameters from frame state by <b>semantic</b>, which is the contract MME relies on.
/// </summary>
/// <remarks>
/// Matrix conventions follow System.Numerics (row vectors, <c>v * M</c>), so combinations read
/// <c>World * View * Projection</c>. Whether MMD's D3D9 path uses the identical composition and
/// transpose convention still has to be checked against the original renderer; until then these
/// products are internally consistent but not yet proven compatible.
/// </remarks>
public static class SemanticBinder
{
    private static readonly HashSet<string> UnboundTextures = new(StringComparer.OrdinalIgnoreCase)
    {
        "MaterialTexture", "MaterialSphereMap", "MaterialToonTexture",
        "AddingTexture", "AddingSphereTexture", "MultiplyingTexture", "MultiplyingSphereTexture",
        "TEXTURE",
    };

    private static readonly HashSet<string> ModelBindings = new(StringComparer.OrdinalIgnoreCase)
    {
        "STANDARDSGLOBAL", "CONTROLOBJECT", "VERTEXDATA", "ADDINGSPHERE", "MULTIPLYINGSPHERE",
        "_INDEX", "EDGEWIDTH", "VERTEXINDEXOFFSET",
    };

    public static SemanticBindingResult Bind(
        EffectDefinition definition,
        EffectFrameState frame,
        EffectParameterValues values,
        ICollection<MmeDiagnostic> diagnostics)
    {
        int bound = 0, unboundTextures = 0, modelBindings = 0;
        var unknown = new List<string>();

        foreach (var parameter in definition.Parameters)
        {
            if (parameter.Semantic is not { Length: > 0 } semantic)
            {
                continue;
            }

            if (TryResolve(semantic, frame, out var value))
            {
                values.Set(parameter.Name, value);
                bound++;
                continue;
            }

            if (UnboundTextures.Contains(semantic))
            {
                unboundTextures++;
                continue;
            }

            if (ModelBindings.Contains(semantic))
            {
                modelBindings++;
                continue;
            }

            if (!unknown.Contains(semantic, StringComparer.OrdinalIgnoreCase))
            {
                unknown.Add(semantic);
                diagnostics.Add(new MmeDiagnostic(DiagnosticSeverity.Warning,
                    MmeDiagnosticCodes.UnknownSemantic,
                    $"semantic '{semantic}' has no host binding (parameter '{parameter.Name}')",
                    definition.SourcePath));
            }
        }

        return new SemanticBindingResult(bound, unboundTextures, modelBindings, unknown);
    }

    private static bool TryResolve(string semantic, EffectFrameState frame, out EffectValue value)
    {
        value = default;
        if (TryResolveMatrix(semantic, frame, out var matrix))
        {
            value = EffectValue.FromMatrix(matrix);
            return true;
        }

        switch (semantic.ToUpperInvariant())
        {
            case "POSITION": value = EffectValue.FromVector(frame.CameraPosition); return true;
            case "LIGHTDIR":
            case "DIRECTION": value = EffectValue.FromVector(frame.LightDirection); return true;
            case "TIME": value = EffectValue.FromScalar(frame.Time); return true;
            case "TIME2": value = EffectValue.FromScalar(frame.Time2); return true;
            case "VERTEXCOUNT": value = EffectValue.FromInteger(frame.VertexCount); return true;
            case "VIEWPORTPIXELSIZE": value = EffectValue.FromVector(frame.ViewportPixelSize); return true;
            case "VIEWPORTRATIO": value = EffectValue.FromVector(frame.ViewportRatio); return true;
            case "TOONCOLOR": value = EffectValue.FromVector(frame.Material.ToonColor); return true;
            case "EDGECOLOR": value = EffectValue.FromVector(frame.EdgeColor); return true;
            case "GROUNDSHADOWCOLOR": value = EffectValue.FromVector(frame.GroundShadowColor); return true;
            case "DIFFUSE": value = EffectValue.FromVector(frame.Material.Diffuse); return true;
            case "SPECULAR": value = EffectValue.FromVector(frame.Material.Specular); return true;
            case "AMBIENT": value = EffectValue.FromVector(frame.Material.Ambient); return true;
            case "EMISSIVE": value = EffectValue.FromVector(frame.Material.Emissive); return true;
            case "SPECULARPOWER": value = EffectValue.FromScalar(frame.Material.SpecularPower); return true;
            default: return false;
        }
    }

    private static bool TryResolveMatrix(string semantic, EffectFrameState frame, out Matrix4x4 matrix)
    {
        matrix = default;
        Matrix4x4 worldView = frame.World * frame.View;
        Matrix4x4 viewProjection = frame.View * frame.Projection;
        Matrix4x4 worldViewProjection = frame.World * frame.View * frame.Projection;

        switch (semantic.ToUpperInvariant())
        {
            case "WORLD": matrix = frame.World; return true;
            case "VIEW": matrix = frame.View; return true;
            case "PROJECTION": matrix = frame.Projection; return true;
            case "WORLDVIEW": matrix = worldView; return true;
            case "VIEWPROJECTION": matrix = viewProjection; return true;
            case "WORLDVIEWPROJECTION": matrix = worldViewProjection; return true;
            case "WORLDTRANSPOSE": matrix = Matrix4x4.Transpose(frame.World); return true;
            case "VIEWTRANSPOSE": matrix = Matrix4x4.Transpose(frame.View); return true;
            case "PROJECTIONTRANSPOSE": matrix = Matrix4x4.Transpose(frame.Projection); return true;
            case "WORLDVIEWTRANSPOSE": matrix = Matrix4x4.Transpose(worldView); return true;
            case "VIEWPROJECTIONTRANSPOSE": matrix = Matrix4x4.Transpose(viewProjection); return true;
            case "WORLDVIEWPROJECTIONTRANSPOSE": matrix = Matrix4x4.Transpose(worldViewProjection); return true;
            case "WORLDINVERSE": return TryInvert(frame.World, out matrix);
            case "VIEWINVERSE": return TryInvert(frame.View, out matrix);
            case "PROJECTIONINVERSE": return TryInvert(frame.Projection, out matrix);
            case "WORLDVIEWINVERSE": return TryInvert(worldView, out matrix);
            case "VIEWPROJECTIONINVERSE": return TryInvert(viewProjection, out matrix);
            case "WORLDVIEWPROJECTIONINVERSE": return TryInvert(worldViewProjection, out matrix);
            case "WORLDINVERSETRANSPOSE": return TryInvertTranspose(frame.World, out matrix);
            case "VIEWINVERSETRANSPOSE": return TryInvertTranspose(frame.View, out matrix);
            case "PROJECTIONINVERSETRANSPOSE": return TryInvertTranspose(frame.Projection, out matrix);
            case "WORLDVIEWINVERSETRANSPOSE": return TryInvertTranspose(worldView, out matrix);
            case "VIEWPROJECTIONINVERSETRANSPOSE": return TryInvertTranspose(viewProjection, out matrix);
            case "WORLDVIEWPROJECTIONINVERSETRANSPOSE": return TryInvertTranspose(worldViewProjection, out matrix);
            default: return false;
        }
    }

    private static bool TryInvert(Matrix4x4 matrix, out Matrix4x4 inverse) =>
        Matrix4x4.Invert(matrix, out inverse);

    private static bool TryInvertTranspose(Matrix4x4 matrix, out Matrix4x4 result)
    {
        if (!Matrix4x4.Invert(matrix, out var inverse))
        {
            result = default;
            return false;
        }

        result = Matrix4x4.Transpose(inverse);
        return true;
    }
}
