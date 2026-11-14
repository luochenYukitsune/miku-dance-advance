// Algorithm references: Assimp v5.4.3 TriangulateProcess (BSD-3-Clause), dominant-axis
// projection and ear clipping. Independently written bounded C# implementation; no copied code.
using System.Numerics;

namespace Mmd.Modern.Formats;

internal static class XPolygonTriangulator
{
    private readonly record struct Point(double X, double Y);

    internal static int[] Triangulate(Vector3[] positions, int[] face, ref long operations, int limit, out Vector3 normal)
    {
        double nx = 0, ny = 0, nz = 0;
        Vector3 origin = positions[face[0]];
        for (int i = 1; i < face.Length - 1; i++)
        {
            Vector3 a = positions[face[i]], b = positions[face[i + 1]];
            double ax = (double)a.X - origin.X, ay = (double)a.Y - origin.Y, az = (double)a.Z - origin.Z;
            double bx = (double)b.X - origin.X, by = (double)b.Y - origin.Y, bz = (double)b.Z - origin.Z;
            nx += ay * bz - az * by; ny += az * bx - ax * bz; nz += ax * by - ay * bx;
        }
        double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (face.Length == 3)
        {
            // An explicit triangle needs no polygon triangulation. Wine's D3DX retains
            // zero-area triangles too: control effects may use their vertices.
            // Keep every corner; the reader still applies supplied indexed normals.
            normal = length == 0 ? Vector3.Zero : new((float)(nx / length), (float)(ny / length), (float)(nz / length));
            return [0, 1, 2];
        }
        if (length <= 1e-8) throw Invalid("Degenerate mesh polygon.");
        normal = new((float)(nx / length), (float)(ny / length), (float)(nz / length));
        var points = new Point[face.Length];
        for (int i = 0; i < face.Length; i++)
        {
            Vector3 p = positions[face[i]];
            double x = (double)p.X - origin.X, y = (double)p.Y - origin.Y, z = (double)p.Z - origin.Z;
            if (Math.Abs((x * nx + y * ny + z * nz) / length) > 1e-4)
                throw new NotSupportedException("Nonplanar .x polygon requires explicit triangulation.");
            points[i] = Math.Abs(nx) >= Math.Abs(ny) && Math.Abs(nx) >= Math.Abs(nz) ? new(y, z)
                : Math.Abs(ny) >= Math.Abs(nz) ? new(z, x) : new(x, y);
        }
        for (int i = 0; i < points.Length; i++)
        {
            int next = (i + 1) % points.Length;
            if (points[i] == points[next]) throw Invalid("Polygon has a zero length edge.");
            for (int j = i + 1; j < points.Length; j++)
            {
                int after = (j + 1) % points.Length;
                if (next == j || after == i) continue;
                Use(ref operations, limit);
                if (Intersects(points[i], points[next], points[j], points[after]))
                    throw Invalid("Self-intersecting mesh polygon.");
            }
        }
        double area = 0;
        for (int i = 0; i < points.Length; i++) area += Cross(new(0, 0), points[i], points[(i + 1) % points.Length]);
        double winding = Math.Sign(area);
        if (Math.Abs(area) <= 1e-20) throw Invalid("Degenerate projected mesh polygon.");
        bool convex = true;
        for (int i = 0; i < points.Length; i++)
            if (Cross(points[i], points[(i + 1) % points.Length], points[(i + 2) % points.Length]) * winding < 0) convex = false;
        var output = new List<int>((face.Length - 2) * 3);
        if (convex)
        {
            // Preserve the original text reader's fan order for existing convex accessories.
            for (int i = 1; i < face.Length - 1; i++) { output.Add(0); output.Add(i); output.Add(i + 1); }
            return output.ToArray();
        }
        var remaining = Enumerable.Range(0, points.Length).ToList();
        while (remaining.Count > 3)
        {
            bool found = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                Use(ref operations, limit);
                int a = remaining[(i + remaining.Count - 1) % remaining.Count], b = remaining[i], c = remaining[(i + 1) % remaining.Count];
                if (Cross(points[a], points[b], points[c]) * winding <= Tolerance(points[a], points[b], points[c])) continue;
                bool occupied = false;
                foreach (int other in remaining)
                {
                    if (other == a || other == b || other == c) continue;
                    Use(ref operations, limit);
                    if (Cross(points[a], points[b], points[other]) * winding >= -Tolerance(points[a], points[b], points[other])
                        && Cross(points[b], points[c], points[other]) * winding >= -Tolerance(points[b], points[c], points[other])
                        && Cross(points[c], points[a], points[other]) * winding >= -Tolerance(points[c], points[a], points[other]))
                    { occupied = true; break; }
                }
                if (occupied) continue;
                output.Add(a); output.Add(b); output.Add(c); remaining.RemoveAt(i); found = true; break;
            }
            if (!found) throw Invalid("Polygon cannot be triangulated without changing its topology.");
        }
        if (Cross(points[remaining[0]], points[remaining[1]], points[remaining[2]]) * winding
            <= Tolerance(points[remaining[0]], points[remaining[1]], points[remaining[2]]))
            throw Invalid("Degenerate final polygon triangle.");
        output.AddRange(remaining);
        return output.ToArray();
    }

    private static double Cross(Point a, Point b, Point c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    // Scale uncertainty to the actual local determinant products. A tolerance based
    // on the whole polygon extent can erase a small notch near a distant large edge.
    private static double Tolerance(Point a, Point b, Point c) => Math.Max(1e-30,
        (Math.Abs((b.X - a.X) * (c.Y - a.Y)) + Math.Abs((b.Y - a.Y) * (c.X - a.X))) * 1.7763568394002505e-15);
    private static bool Intersects(Point a, Point b, Point c, Point d)
    {
        double abC = Cross(a, b, c), abD = Cross(a, b, d), cdA = Cross(c, d, a), cdB = Cross(c, d, b);
        double eAbC = Tolerance(a, b, c), eAbD = Tolerance(a, b, d), eCdA = Tolerance(c, d, a), eCdB = Tolerance(c, d, b);
        if ((abC > eAbC && abD < -eAbD || abC < -eAbC && abD > eAbD)
            && (cdA > eCdA && cdB < -eCdB || cdA < -eCdA && cdB > eCdB)) return true;
        return Math.Abs(abC) <= eAbC && On(a, b, c) || Math.Abs(abD) <= eAbD && On(a, b, d)
            || Math.Abs(cdA) <= eCdA && On(c, d, a) || Math.Abs(cdB) <= eCdB && On(c, d, b);
    }
    private static bool On(Point a, Point b, Point p) => p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X)
        && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);
    private static void Use(ref long operations, int limit)
    { if (++operations > limit) throw Invalid("Triangulation operation budget exceeded."); }
    private static InvalidDataException Invalid(string message) => new(".x polygon: " + message);
}
