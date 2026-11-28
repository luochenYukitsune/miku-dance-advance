using System.Numerics;
using System.Runtime.CompilerServices;

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

        var (minimum, maximum) = ComputeFullSceneWorldBounds(scene);
        return FitFromWorldBounds(scene, minimum, maximum, paddingFraction);
    }

    /// <summary>Fits the directional shadow projection XY tightly around the selected material indices
    /// (e.g., character materials in a large stage scene) to preserve texel density on the character,
    /// while keeping light-space near Z extended to include all upstream scene shadow casters.</summary>
    public static Matrix4x4 Fit(SceneSnapshot scene, IReadOnlyList<int> focusMaterialIndices, float paddingFraction = .03f)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(focusMaterialIndices);
        if (!float.IsFinite(paddingFraction) || paddingFraction is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(paddingFraction));
        if (focusMaterialIndices.Count == 0)
            return Fit(scene, paddingFraction);

        var selected = new HashSet<int>();
        foreach (int index in focusMaterialIndices)
        {
            if (index >= 0 && index < scene.Materials.Count)
                selected.Add(index);
        }
        if (selected.Count == 0)
            return Fit(scene, paddingFraction);

        Vector3 minimum = new(float.MaxValue), maximum = new(float.MinValue);
        bool anyVertex = false;

        if (!scene.HasDeferredDeformation)
        {
            foreach (int matIndex in selected)
            {
                var material = scene.Materials[matIndex];
                int end = material.StartIndex + material.IndexCount;
                for (int i = material.StartIndex; i < end; i++)
                {
                    var position = Vector3.Transform(scene.Vertices[(int)scene.Indices[i]].Position, scene.World);
                    minimum = Vector3.Min(minimum, position);
                    maximum = Vector3.Max(maximum, position);
                    anyVertex = true;
                }
            }
        }
        else
        {
            var ranges = scene.DeformationRanges;
            var hitRanges = new HashSet<int>();
            var staticView = scene.StaticVertexView;
            foreach (int matIndex in selected)
            {
                var material = scene.Materials[matIndex];
                int end = material.StartIndex + material.IndexCount;
                for (int i = material.StartIndex; i < end; i++)
                {
                    int vertex = (int)scene.Indices[i];
                    int rangeIdx = FindDeformationRange(ranges, vertex);
                    if (rangeIdx >= 0)
                    {
                        hitRanges.Add(rangeIdx);
                    }
                    else
                    {
                        var position = Vector3.Transform(staticView.GetVertex(vertex).Position, scene.World);
                        minimum = Vector3.Min(minimum, position);
                        maximum = Vector3.Max(maximum, position);
                        anyVertex = true;
                    }
                }
            }
            foreach (int rangeIdx in hitRanges)
            {
                AccumulateDeformationBounds(ranges[rangeIdx], scene.World, ref minimum, ref maximum);
                anyVertex = true;
            }
        }

        if (!anyVertex)
            return Fit(scene, paddingFraction);

        var (fullMin, fullMax) = ComputeFullSceneWorldBounds(scene);
        return FitFromWorldBounds(scene, minimum, maximum, paddingFraction, fullMin, fullMax);
    }

    private static (Vector3 Min, Vector3 Max) ComputeFullSceneWorldBounds(SceneSnapshot scene)
    {
        Vector3 minimum = new(float.MaxValue), maximum = new(float.MinValue);
        if (!scene.HasDeferredDeformation)
        {
            foreach (uint index in scene.Indices)
            {
                var position = Vector3.Transform(scene.Vertices[(int)index].Position, scene.World);
                minimum = Vector3.Min(minimum, position);
                maximum = Vector3.Max(maximum, position);
            }
        }
        else
        {
            ComputeDeferredWorldBounds(scene, ref minimum, ref maximum);
        }
        return (minimum, maximum);
    }

    private static int FindDeformationRange(IReadOnlyList<SceneDeformationRange> ranges, int vertex)
    {
        int lo = 0, hi = ranges.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (ranges[mid].Start <= vertex) lo = mid + 1;
            else hi = mid;
        }
        if (lo == 0) return -1;
        var candidate = ranges[lo - 1];
        return vertex < candidate.Start + candidate.Count ? lo - 1 : -1;
    }

    private static Matrix4x4 FitFromWorldBounds(
        SceneSnapshot scene,
        Vector3 minimum,
        Vector3 maximum,
        float paddingFraction,
        Vector3? casterMin = null,
        Vector3? casterMax = null)
    {
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
        if (casterMin is { } cMin && casterMax is { } cMax)
        {
            if (!Finite(cMin) || !Finite(cMax))
                throw new ArgumentException("Scene bounds exceed finite shadow projection range.", nameof(scene));
            // Extend light-space near Z to upstream scene casters so light-side occluders are not clipped,
            // while keeping XY strictly fitted to the focused character bounds.
            for (int corner = 0; corner < 8; corner++)
            {
                var position = new Vector3((corner & 1) == 0 ? cMin.X : cMax.X,
                    (corner & 2) == 0 ? cMin.Y : cMax.Y, (corner & 4) == 0 ? cMin.Z : cMax.Z);
                var lightPosition = Vector3.Transform(position, view);
                lightMin.Z = MathF.Min(lightMin.Z, lightPosition.Z);
            }
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
        // SceneSnapshot has validated that ranges are disjoint and inside the vertex buffer.
        // A scene composed from many deferred props can cover the entire buffer as well.
        int deferredVertices = 0;
        foreach (var range in ranges) deferredVertices += range.Count;
        if (deferredVertices < scene.VertexCount)
        {
            var staticView = scene.StaticVertexView;
            int runStart = 0, runEnd = 0;
            bool deferred = false;
            foreach (uint index in scene.Indices)
            {
                int vertex = (int)index;
                // Mesh indices typically stay within one prop for many triangles.
                if (vertex < runStart || vertex >= runEnd)
                {
                    int lo = 0, hi = ranges.Count;
                    while (lo < hi)
                    {
                        int mid = lo + (hi - lo) / 2;
                        if (ranges[mid].Start <= vertex) lo = mid + 1;
                        else hi = mid;
                    }
                    int previousEnd = lo == 0 ? 0 : ranges[lo - 1].Start + ranges[lo - 1].Count;
                    deferred = vertex < previousEnd;
                    runStart = deferred ? ranges[lo - 1].Start : previousEnd;
                    runEnd = deferred ? previousEnd : lo < ranges.Count ? ranges[lo].Start : scene.VertexCount;
                }
                if (deferred) continue;
                var position = Vector3.Transform(staticView.GetVertex(vertex).Position, scene.World);
                minimum = Vector3.Min(minimum, position); maximum = Vector3.Max(maximum, position);
            }
        }
        foreach (var range in ranges)
            AccumulateDeformationBounds(range, scene.World, ref minimum, ref maximum);
    }

    private sealed record ModelRestBounds(
        (int Bone, Vector3 Min, Vector3 Max)[] BdefBones,
        (int Bone, Vector3 AnchorMin, Vector3 AnchorMax, float BindMaxRadius)[] SdefBones,
        (int Bone, float BindMaxRadius)[] QdefBones);

    private static readonly ConditionalWeakTable<SceneSkinningModel, ModelRestBounds> restBoundsCache = new();

    private static ModelRestBounds ComputeRestBounds(SceneSkinningModel model)
    {
        var rest = model.RestVertices;
        var bindings = model.Bindings;
        var sdef = model.Sdef;
        int boneCount = model.BoneCount;

        var bdefMin = new Vector3[boneCount];
        var bdefMax = new Vector3[boneCount];
        var bdefUsed = new bool[boneCount];
        Array.Fill(bdefMin, new Vector3(float.PositiveInfinity));
        Array.Fill(bdefMax, new Vector3(float.NegativeInfinity));

        var sdefMin = model.HasSdef ? new Vector3[boneCount] : null;
        var sdefMax = model.HasSdef ? new Vector3[boneCount] : null;
        var sdefRadius = model.HasSdef ? new float[boneCount] : null;
        var sdefUsed = model.HasSdef ? new bool[boneCount] : null;
        if (model.HasSdef)
        {
            Array.Fill(sdefMin!, new Vector3(float.PositiveInfinity));
            Array.Fill(sdefMax!, new Vector3(float.NegativeInfinity));
        }

        var qdefRadius = model.HasQdef ? new float[boneCount] : null;
        var qdefUsed = model.HasQdef ? new bool[boneCount] : null;

        for (int i = 0; i < rest.Length; i++)
        {
            var p = rest[i].Position;
            var b = bindings[i];
            if (b.Kind == SceneSkinKind.Sdef)
            {
                float r = (p - sdef[i].C).Length();
                int b0 = b.Weights.X > 0 ? b.Bone0 : b.Bone1;
                int b1 = b.Weights.Y > 0 ? b.Bone1 : b.Bone0;
                Vector3 a0 = sdef[i].Anchor0, a1 = sdef[i].Anchor1;
                sdefMin![b0] = Vector3.Min(sdefMin[b0], a0); sdefMax![b0] = Vector3.Max(sdefMax[b0], a0); sdefUsed![b0] = true;
                sdefMin[b1] = Vector3.Min(sdefMin[b1], a1); sdefMax[b1] = Vector3.Max(sdefMax[b1], a1); sdefUsed[b1] = true;
                sdefRadius![b0] = MathF.Max(sdefRadius[b0], r);
                sdefRadius[b1] = MathF.Max(sdefRadius[b1], r);
            }
            else if (b.Kind == SceneSkinKind.Qdef)
            {
                float len = p.Length();
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
                    bdefMin[bone] = Vector3.Min(bdefMin[bone], p);
                    bdefMax[bone] = Vector3.Max(bdefMax[bone], p);
                    bdefUsed[bone] = true;
                }
            }
        }

        var bdefList = new List<(int, Vector3, Vector3)>();
        var sdefList = new List<(int, Vector3, Vector3, float)>();
        var qdefList = new List<(int, float)>();
        for (int b = 0; b < boneCount; b++)
        {
            if (bdefUsed[b]) bdefList.Add((b, bdefMin[b], bdefMax[b]));
            if (sdefUsed is not null && sdefUsed[b]) sdefList.Add((b, sdefMin![b], sdefMax![b], sdefRadius![b]));
            if (qdefUsed is not null && qdefUsed[b]) qdefList.Add((b, qdefRadius![b]));
        }
        return new(bdefList.ToArray(), sdefList.ToArray(), qdefList.ToArray());
    }

    private static void AccumulateDeformationBounds(SceneDeformationRange range, Matrix4x4 sceneWorld, ref Vector3 minimum, ref Vector3 maximum)
    {
        var deformation = range.Deformation;
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
        var bounds = restBoundsCache.GetValue(deformation.Model, static model => ComputeRestBounds(model));
        var skin = deformation.SkinMatrices;
        var world = range.World * sceneWorld;
        foreach (var entry in bounds.BdefBones)
        {
            AccumulateTransformedBox(entry.Min - maxMorphAbs, entry.Max + maxMorphAbs, skin[entry.Bone] * world, ref minimum, ref maximum);
        }
        foreach (var entry in bounds.SdefBones)
        {
            float r = entry.BindMaxRadius + maxMorphLen;
            var pad = maxMorphAbs + (r > 0 ? new Vector3(r) : Vector3.Zero);
            AccumulateTransformedBox(entry.AnchorMin - pad, entry.AnchorMax + pad, skin[entry.Bone] * world, ref minimum, ref maximum);
        }
        if (bounds.QdefBones.Length > 0)
        {
            float worldScale = MaxScaleFactor(world);
            foreach (var entry in bounds.QdefBones)
            {
                var qReal = deformation.QdefReal[entry.Bone];
                var qDual = deformation.QdefDual[entry.Bone];
                var t = (qDual * Quaternion.Conjugate(qReal)) * 2;
                var center = Vector3.Transform(new Vector3(t.X, t.Y, t.Z), world);
                float r = (entry.BindMaxRadius + maxMorphLen) * worldScale;
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
