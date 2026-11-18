using System.Numerics;

namespace Mmd.Modern.Core;

/// <summary>A selected modern scene fit, separate from the original host's projective light warp.
/// Returns deformed model-position to light-clip (World included once), using all referenced
/// geometry including camera-invisible casters. Scene LightDirection points toward the light.</summary>
public static class ModernDirectionalShadowProjection
{
    public static Matrix4x4 Fit(SceneSnapshot scene, float paddingFraction = .03f)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!float.IsFinite(paddingFraction) || paddingFraction is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(paddingFraction));

        Vector3 minimum = new(float.MaxValue), maximum = new(float.MinValue);
        if (!scene.HasDeferredDeformation)
        {
            foreach (uint index in scene.Indices)
            {
                var position = Vector3.Transform(scene.Vertices[(int)index].Position, scene.World);
                minimum = Vector3.Min(minimum, position); maximum = Vector3.Max(maximum, position);
            }
        }
        else
        {
            ComputeDeferredWorldBounds(scene, ref minimum, ref maximum);
        }
        Vector3 extent = maximum - minimum, center = minimum * .5f + maximum * .5f;
        float radius = extent.Length() * .5f;
        if (!Finite(center) || !float.IsFinite(radius))
            throw new ArgumentException("Scene bounds exceed finite shadow projection range.", nameof(scene));

        Vector3 towardLight = Vector3.Normalize(scene.LightDirection), z = -towardLight;
        Vector3 up = MathF.Abs(z.Z) < .99f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 x = Vector3.Normalize(Vector3.Cross(up, z)), y = Vector3.Cross(z, x);
        Vector3 eye = center + towardLight * (radius + 1);
        var view = new Matrix4x4(x.X, y.X, z.X, 0, x.Y, y.Y, z.Y, 0, x.Z, y.Z, z.Z, 0,
            -Vector3.Dot(x, eye), -Vector3.Dot(y, eye), -Vector3.Dot(z, eye), 1);

        Vector3 lightMin = new(float.MaxValue), lightMax = new(float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            var position = new Vector3((corner & 1) == 0 ? minimum.X : maximum.X,
                (corner & 2) == 0 ? minimum.Y : maximum.Y, (corner & 4) == 0 ? minimum.Z : maximum.Z);
            var lightPosition = Vector3.Transform(position, view);
            lightMin = Vector3.Min(lightMin, lightPosition); lightMax = Vector3.Max(lightMax, lightPosition);
        }
        Vector3 padding = Vector3.Max((lightMax - lightMin) * paddingFraction, new Vector3(.01f));
        lightMin -= padding; lightMax += padding;
        var range = lightMax - lightMin;
        var projection = new Matrix4x4(2 / range.X, 0, 0, 0, 0, 2 / range.Y, 0, 0,
            0, 0, 1 / range.Z, 0, -(lightMax.X + lightMin.X) / range.X,
            -(lightMax.Y + lightMin.Y) / range.Y, -lightMin.Z / range.Z, 1);
        var result = scene.World * view * projection;
        if (!Finite(lightMin) || !Finite(lightMax) || !Finite(result) || !Matrix4x4.Invert(result, out _))
            throw new ArgumentException("Scene cannot produce an invertible finite shadow projection.", nameof(scene));
        return result;
    }

    internal static void ComputeDeferredWorldBounds(SceneSnapshot scene, ref Vector3 minimum, ref Vector3 maximum)
    {
        var ranges = scene.DeformationRanges;
        var staticView = scene.StaticVertexView;
        bool allDeferred = ranges.Count == 1 && ranges[0].Start == 0 && ranges[0].Count == scene.VertexCount;
        if (!allDeferred)
        {
            foreach (uint index in scene.Indices)
            {
                int vertex = (int)index;
                if (IsInRanges(vertex, ranges)) continue;
                var position = Vector3.Transform(staticView.GetVertex(vertex).Position, scene.World);
                minimum = Vector3.Min(minimum, position); maximum = Vector3.Max(maximum, position);
            }
        }
        foreach (var range in ranges)
            AccumulateDeformationBounds(range, scene.World, ref minimum, ref maximum);
    }

    private static bool IsInRanges(int vertex, IReadOnlyList<SceneDeformationRange> ranges)
    {
        for (int i = 0; i < ranges.Count; i++)
        {
            var r = ranges[i];
            if (vertex >= r.Start && vertex < r.Start + r.Count) return true;
        }
        return false;
    }

    private static void AccumulateDeformationBounds(SceneDeformationRange range, Matrix4x4 sceneWorld, ref Vector3 minimum, ref Vector3 maximum)
    {
        var deformation = range.Deformation;
        var model = deformation.Model;
        var rest = model.RestVertices;
        var bindings = model.Bindings;
        var sdef = model.Sdef;
        var morphs = deformation.Morphs;
        Vector3 maxMorphAbs = Vector3.Zero;
        float maxMorphLen = 0;
        if (morphs is not null)
        {
            foreach (var delta in morphs.Positions)
            {
                maxMorphAbs = Vector3.Max(maxMorphAbs, new Vector3(MathF.Abs(delta.X), MathF.Abs(delta.Y), MathF.Abs(delta.Z)));
                maxMorphLen = MathF.Max(maxMorphLen, delta.Length());
            }
        }
        int boneCount = model.BoneCount;
        var boneMin = new Vector3[boneCount];
        var boneMax = new Vector3[boneCount];
        var boneUsed = new bool[boneCount];
        Array.Fill(boneMin, new Vector3(float.PositiveInfinity));
        Array.Fill(boneMax, new Vector3(float.NegativeInfinity));
        var sdefRadius = model.HasSdef ? new float[boneCount] : null;
        var qdefCenter = model.HasQdef ? new Vector3[boneCount] : null;
        var qdefRadius = model.HasQdef ? new float[boneCount] : null;
        var qdefUsed = model.HasQdef ? new bool[boneCount] : null;
        for (int i = 0; i < rest.Length; i++)
        {
            var p = rest[i].Position;
            var b = bindings[i];
            if (b.Kind == SceneSkinKind.Sdef)
            {
                float r = (p - sdef[i].C).Length() + maxMorphLen;
                int b0 = b.Weights.X > 0 ? b.Bone0 : b.Bone1;
                int b1 = b.Weights.Y > 0 ? b.Bone1 : b.Bone0;
                Vector3 a0 = sdef[i].Anchor0, a1 = sdef[i].Anchor1;
                boneMin[b0] = Vector3.Min(boneMin[b0], a0); boneMax[b0] = Vector3.Max(boneMax[b0], a0); boneUsed[b0] = true;
                boneMin[b1] = Vector3.Min(boneMin[b1], a1); boneMax[b1] = Vector3.Max(boneMax[b1], a1); boneUsed[b1] = true;
                sdefRadius![b0] = MathF.Max(sdefRadius[b0], r);
                sdefRadius[b1] = MathF.Max(sdefRadius[b1], r);
            }
            else if (b.Kind == SceneSkinKind.Qdef)
            {
                float len = p.Length() + maxMorphLen;
                for (int slot = 0; slot < 4; slot++)
                {
                    float w = slot switch { 0 => b.Weights.X, 1 => b.Weights.Y, 2 => b.Weights.Z, _ => b.Weights.W };
                    if (w == 0) continue;
                    int bone = slot switch { 0 => b.Bone0, 1 => b.Bone1, 2 => b.Bone2, _ => b.Bone3 };
                    qdefUsed![bone] = true;
                    qdefRadius![bone] = MathF.Max(qdefRadius[bone], len);
                }
            }
            else
            {
                int slots = b.Kind == SceneSkinKind.Bdef1 ? 1 : b.Kind == SceneSkinKind.Bdef2 ? 2 : 4;
                for (int slot = 0; slot < slots; slot++)
                {
                    float w = slot switch { 0 => b.Weights.X, 1 => b.Weights.Y, 2 => b.Weights.Z, _ => b.Weights.W };
                    if (w == 0) continue;
                    int bone = slot switch { 0 => b.Bone0, 1 => b.Bone1, 2 => b.Bone2, _ => b.Bone3 };
                    boneMin[bone] = Vector3.Min(boneMin[bone], p);
                    boneMax[bone] = Vector3.Max(boneMax[bone], p);
                    boneUsed[bone] = true;
                }
            }
        }
        var skin = deformation.SkinMatrices;
        var world = range.World * sceneWorld;
        for (int b = 0; b < boneCount; b++)
        {
            if (boneUsed[b])
            {
                var transform = skin[b] * world;
                Vector3 lo = boneMin[b] - maxMorphAbs, hi = boneMax[b] + maxMorphAbs;
                if (sdefRadius is not null && sdefRadius[b] > 0)
                {
                    var pad = new Vector3(sdefRadius[b]);
                    lo = Vector3.Min(lo, boneMin[b]) - pad;
                    hi = Vector3.Max(hi, boneMax[b]) + pad;
                }
                AccumulateTransformedBox(lo, hi, transform, ref minimum, ref maximum);
            }
            if (qdefUsed is not null && qdefUsed[b])
            {
                var qReal = deformation.QdefReal[b];
                var qDual = deformation.QdefDual[b];
                var t = (qDual * Quaternion.Conjugate(qReal)) * 2;
                var center = Vector3.Transform(new Vector3(t.X, t.Y, t.Z), world);
                float r = qdefRadius![b] * MaxScaleFactor(world);
                var pad = new Vector3(r);
                minimum = Vector3.Min(minimum, center - pad);
                maximum = Vector3.Max(maximum, center + pad);
            }
        }
    }

    private static float MaxScaleFactor(Matrix4x4 m)
    {
        float sx = new Vector3(m.M11, m.M12, m.M13).Length();
        float sy = new Vector3(m.M21, m.M22, m.M23).Length();
        float sz = new Vector3(m.M31, m.M32, m.M33).Length();
        return MathF.Max(sx, MathF.Max(sy, sz));
    }

    private static void AccumulateTransformedBox(Vector3 min, Vector3 max, Matrix4x4 transform, ref Vector3 outMin, ref Vector3 outMax)
    {
        Vector3 center = (min + max) * .5f;
        Vector3 extent = (max - min) * .5f;
        Vector3 transformedCenter = Vector3.Transform(center, transform);
        Vector3 transformedExtent = new(
            MathF.Abs(transform.M11) * extent.X + MathF.Abs(transform.M21) * extent.Y + MathF.Abs(transform.M31) * extent.Z,
            MathF.Abs(transform.M12) * extent.X + MathF.Abs(transform.M22) * extent.Y + MathF.Abs(transform.M32) * extent.Z,
            MathF.Abs(transform.M13) * extent.X + MathF.Abs(transform.M23) * extent.Y + MathF.Abs(transform.M33) * extent.Z);
        outMin = Vector3.Min(outMin, transformedCenter - transformedExtent);
        outMax = Vector3.Max(outMax, transformedCenter + transformedExtent);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
