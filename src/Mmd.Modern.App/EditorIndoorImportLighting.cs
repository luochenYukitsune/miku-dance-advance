using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

/// <summary>Explicit new-import choices. Existing project loads never pass through this path.</summary>
public sealed record EditorModelImportOptions(ModelPurpose Purpose, bool AddIndoorBasicLight = false);

/// <summary>A local, editable starting light. Its intensity is the brightness received at the working focus
/// (see <see cref="SceneLightFalloff"/>), and its range covers the room height around it.
/// It does not identify authored fixtures or attempt to illuminate a complete environment.</summary>
internal static class EditorIndoorImportLighting
{
    public static NativeSceneLight Create(SceneAnimationModelAsset asset, Vector3? workingFocus, IReadOnlyList<NativeSceneLight> existing)
    {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        foreach (var vertex in asset.Model.Vertices)
        {
            if (!Finite(vertex.Position)) continue;
            minimum = Vector3.Min(minimum, vertex.Position);
            maximum = Vector3.Max(maximum, vertex.Position);
        }
        bool hasBounds = Finite(minimum) && Finite(maximum);
        var focus = workingFocus is { } supplied && Finite(supplied) ? supplied : new Vector3(0, 10, 0);
        if (hasBounds) focus = Vector3.Clamp(focus, minimum, maximum);

        // Height, rather than the longest dimension, keeps a long station's starter light local.
        float localSpan = (float)Math.Clamp(hasBounds ? (double)maximum.Y - minimum.Y : 20d, 12d, 60d);
        float elevation = localSpan * .5f;
        var position = new Vector3(focus.X, (float)((double)focus.Y + elevation), (float)((double)focus.Z - localSpan * .25f));
        if (hasBounds)
        {
            position.X = Math.Clamp(position.X, minimum.X, maximum.X);
            position.Z = Math.Clamp(position.Z, minimum.Z, maximum.Z);
            double height = (double)maximum.Y - minimum.Y;
            if (height > 1)
                position.Y = (float)Math.Clamp(position.Y, minimum.Y + height * .1, maximum.Y - height * .1);
        }
        if (!Finite(position)) throw new ArgumentException(EditorLocalization.Text("Mda.Text.Context.Room.InvalidLightPlacement"));
        string label = EditorLocalization.Text("Mda.Text.Context.Room.BasicLightName");
        string name = label;
        int ordinal = 2;
        while (existing.Any(light => string.Equals(light.Name, name, StringComparison.Ordinal))) name = $"{label} {ordinal++}";
        float aim = MathF.Max(Vector3.Distance(position, focus), localSpan * .1f);
        return new(Guid.NewGuid(), name, SceneLightKind.Point, position, Quaternion.Identity, Vector3.One,
            Intensity: .85f, Range: MathF.Max(localSpan * 3, aim / SceneLightFalloff.MaxReferenceFraction), Enabled: true, CastShadows: true,
            AimDistance: aim);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
