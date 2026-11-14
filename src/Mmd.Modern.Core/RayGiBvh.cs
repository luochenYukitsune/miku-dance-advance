using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Mmd.Modern.Core;

[StructLayout(LayoutKind.Sequential)]
public readonly struct RayGiBvhNodeGpu
{
    public readonly Vector4 BoundsMin, BoundsMax;
    public readonly RayGiUInt4 Data;
    public RayGiBvhNodeGpu(Vector4 boundsMin, Vector4 boundsMax, RayGiUInt4 data)
    { BoundsMin = boundsMin; BoundsMax = boundsMax; Data = data; }
}
public readonly record struct RayGiRay(Vector3 Origin, Vector3 Direction, float TMin, float TMax);
public readonly record struct RayGiHit(uint TriangleIndex, float T, Vector2 Barycentrics, uint FrontFace);

/// <summary>Load-time balanced BVH. CPU traversal is a diagnostic/reference API, never the product's per-pixel renderer.</summary>
public sealed class RayGiBvh
{
    public const int MaximumAllowedDepth = 31;
    public const int TraversalStackCapacity = 64;
    public const int MaximumLeafSize = 4;
    private readonly RayGiScene scene;
    private readonly RayGiBvhNodeGpu[] nodes;
    private readonly uint[] triangleOrder;
    private sealed record GeometryData(RayGiBvhNodeGpu[] Nodes, uint[] Order, int Depth);
    private static readonly ConditionalWeakTable<object, GeometryData> GeometryCache = new();
    public ReadOnlySpan<RayGiBvhNodeGpu> Nodes => nodes;
    public ReadOnlySpan<uint> TriangleOrder => triangleOrder;
    public int MaximumDepth { get; }
    private RayGiBvh(RayGiScene scene, RayGiBvhNodeGpu[] nodes, uint[] order, int depth)
    { this.scene = scene; this.nodes = nodes; triangleOrder = order; MaximumDepth = depth; }

    public static RayGiBvh Build(RayGiScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var geometry = GeometryCache.GetValue(scene.GeometryCacheKey, _ => BuildGeometry(scene));
        return new(scene, geometry.Nodes, geometry.Order, geometry.Depth);
    }
    private static GeometryData BuildGeometry(RayGiScene scene)
    {
        var order = new List<uint>();
        for (int i = 0; i < scene.Triangles.Length; i++)
        {
            var t = scene.Triangles[i];
            if (Nondegenerate(t)) order.Add((uint)i);
        }
        if (order.Count == 0) return new([], [], 0);
        uint[] ids = order.ToArray();
        var output = new List<RayGiBvhNodeGpu>(checked(ids.Length * 2)); int maxDepth = 0;
        int Node(int start, int count, int depth)
        {
            if (depth > MaximumAllowedDepth) throw new InvalidOperationException("BVH exceeds the validated GPU traversal depth.");
            maxDepth = Math.Max(maxDepth, depth);
            var low = new Vector3(float.PositiveInfinity); var high = new Vector3(float.NegativeInfinity);
            for (int i = start; i < start + count; i++)
            {
                var t = scene.Triangles[(int)ids[i]];
                low = Vector3.Min(low, Vector3.Min(RayGiScene.Xyz(t.P0), Vector3.Min(RayGiScene.Xyz(t.P1), RayGiScene.Xyz(t.P2))));
                high = Vector3.Max(high, Vector3.Max(RayGiScene.Xyz(t.P0), Vector3.Max(RayGiScene.Xyz(t.P1), RayGiScene.Xyz(t.P2))));
            }
            int index = output.Count; output.Add(default);
            RayGiUInt4 data;
            if (count <= MaximumLeafSize) data = new((uint)start, 0, (uint)count, 0);
            else
            {
                // Double extent/centroid keeps finite float positions from overflowing during construction.
                double dx = (double)high.X - low.X, dy = (double)high.Y - low.Y, dz = (double)high.Z - low.Z;
                int axis = dx >= dy && dx >= dz ? 0 : dy >= dz ? 1 : 2;
                Array.Sort(ids, start, count, Comparer<uint>.Create((a, b) =>
                {
                    int compare = Centroid(scene.Triangles[(int)a], axis).CompareTo(Centroid(scene.Triangles[(int)b], axis));
                    return compare != 0 ? compare : a.CompareTo(b);
                }));
                int half = count / 2;
                data = new((uint)Node(start, half, depth + 1), (uint)Node(start + half, count - half, depth + 1), 0, 0);
            }
            output[index] = new(new(low, 0), new(high, 0), data); return index;
        }
        Node(0, ids.Length, 0);
        return new(output.ToArray(), ids, maxDepth);
    }

    public bool TraceClosest(RayGiRay ray, out RayGiHit hit) => Trace(ray, false, out hit);
    public bool TraceAny(RayGiRay ray) => Trace(ray, true, out _);
    private bool Trace(RayGiRay ray, bool any, out RayGiHit hit)
    {
        Validate(ray); hit = default; if (nodes.Length == 0) return false;
        Span<int> stack = stackalloc int[TraversalStackCapacity]; int count = 1; stack[0] = 0; bool found = false; float closest = ray.TMax;
        while (count != 0)
        {
            var node = nodes[stack[--count]];
            if (!Box(node, ray, closest)) continue;
            if (node.Data.Z != 0)
            {
                for (uint i = 0; i < node.Data.Z; i++)
                {
                    uint id = triangleOrder[node.Data.X + i];
                    if (Intersect(scene.Triangles[(int)id], ray, closest, out var t, out var bary, out var front) && scene.AcceptCandidate(id, bary, ray.Direction))
                    {
                        if (any) return true;
                        if (!found || t < closest || (t == closest && id < hit.TriangleIndex)) { found = true; closest = t; hit = new(id, t, bary, front); }
                    }
                }
            }
            else
            {
                if (count + 2 > stack.Length) throw new InvalidOperationException("BVH traversal stack overflow.");
                stack[count++] = (int)node.Data.Y; stack[count++] = (int)node.Data.X;
            }
        }
        return found;
    }

    /// <summary>Independent linear search used by tests and explicit diagnostics, not GI frame rendering.</summary>
    public bool TraceBruteForce(RayGiRay ray, out RayGiHit hit)
    {
        Validate(ray); bool found = false; hit = default; float closest = ray.TMax;
        for (uint id = 0; id < scene.Triangles.Length; id++)
            if (Intersect(scene.Triangles[(int)id], ray, closest, out var t, out var bary, out var front) && scene.AcceptCandidate(id, bary, ray.Direction)
                && (!found || t < closest || (t == closest && id < hit.TriangleIndex)))
            { found = true; closest = t; hit = new(id, t, bary, front); }
        return found;
    }

    private static bool Box(RayGiBvhNodeGpu box, RayGiRay ray, float closest)
    {
        double min = ray.TMin, max = closest;
        for (int axis = 0; axis < 3; axis++)
        {
            double o = Component(ray.Origin, axis), d = Component(ray.Direction, axis), low = Component(RayGiScene.Xyz(box.BoundsMin), axis), high = Component(RayGiScene.Xyz(box.BoundsMax), axis);
            if (d == 0) { if (o < low || o > high) return false; continue; }
            double a = (low - o) / d, b = (high - o) / d;
            min = Math.Max(min, Math.Min(a, b)); max = Math.Min(max, Math.Max(a, b)); if (min > max) return false;
        }
        return true;
    }
    private static bool Intersect(RayGiTriangleGpu t, RayGiRay ray, float closest, out float distance, out Vector2 bary, out uint front)
    {
        // Scalar double arithmetic makes the CPU oracle useful at large float world coordinates.
        var a = RayGiScene.Xyz(t.P0); var b = RayGiScene.Xyz(t.P1); var c = RayGiScene.Xyz(t.P2);
        double e1x = (double)b.X - a.X, e1y = (double)b.Y - a.Y, e1z = (double)b.Z - a.Z;
        double e2x = (double)c.X - a.X, e2y = (double)c.Y - a.Y, e2z = (double)c.Z - a.Z;
        double px = ray.Direction.Y * e2z - ray.Direction.Z * e2y, py = ray.Direction.Z * e2x - ray.Direction.X * e2z, pz = ray.Direction.X * e2y - ray.Direction.Y * e2x;
        double determinant = e1x * px + e1y * py + e1z * pz;
        distance = 0; bary = default; front = determinant > 0 ? 1u : 0u;
        if (determinant == 0 || !double.IsFinite(determinant)) return false;
        double ox = (double)ray.Origin.X - a.X, oy = (double)ray.Origin.Y - a.Y, oz = (double)ray.Origin.Z - a.Z;
        double u = (ox * px + oy * py + oz * pz) / determinant;
        if (u < 0 || u > 1) return false;
        double qx = oy * e1z - oz * e1y, qy = oz * e1x - ox * e1z, qz = ox * e1y - oy * e1x;
        double v = (ray.Direction.X * qx + ray.Direction.Y * qy + ray.Direction.Z * qz) / determinant;
        if (v < 0 || u + v > 1) return false;
        double at = (e2x * qx + e2y * qy + e2z * qz) / determinant;
        if (at < ray.TMin || at > closest || !double.IsFinite(at)) return false;
        distance = (float)at; bary = new((float)u, (float)v); return true;
    }
    private static bool Nondegenerate(RayGiTriangleGpu t)
    {
        var a = RayGiScene.Xyz(t.P0); var b = RayGiScene.Xyz(t.P1); var c = RayGiScene.Xyz(t.P2);
        double x = ((double)b.Y-a.Y)*((double)c.Z-a.Z)-((double)b.Z-a.Z)*((double)c.Y-a.Y);
        double y = ((double)b.Z-a.Z)*((double)c.X-a.X)-((double)b.X-a.X)*((double)c.Z-a.Z);
        double z = ((double)b.X-a.X)*((double)c.Y-a.Y)-((double)b.Y-a.Y)*((double)c.X-a.X);
        return x != 0 || y != 0 || z != 0;
    }
    private static double Centroid(RayGiTriangleGpu t, int axis) => (Component(RayGiScene.Xyz(t.P0), axis) + Component(RayGiScene.Xyz(t.P1), axis) + Component(RayGiScene.Xyz(t.P2), axis)) / 3;
    private static double Component(Vector3 p, int axis) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
    private static void Validate(RayGiRay ray)
    {
        if (!float.IsFinite(ray.Origin.X) || !float.IsFinite(ray.Origin.Y) || !float.IsFinite(ray.Origin.Z)
            || !float.IsFinite(ray.Direction.X) || !float.IsFinite(ray.Direction.Y) || !float.IsFinite(ray.Direction.Z)
            || ray.Direction == Vector3.Zero || !float.IsFinite(ray.TMin) || !float.IsFinite(ray.TMax) || ray.TMin < 0 || ray.TMax < ray.TMin)
            throw new ArgumentException("Ray requires finite values, nonzero direction and 0 <= TMin <= TMax.");
    }
}
