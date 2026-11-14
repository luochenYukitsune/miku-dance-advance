namespace Mmd.Modern.Rendering.D3D12;

internal static class SoftwareTraceHlsl
{
    internal const string Source = """
        bool GiSoftwareBox(GiBvhNode box, GiRay ray, float maximumT)
        {
            float low = ray.TMin, high = maximumT;
            [unroll] for (uint axis = 0; axis < 3; axis++)
            {
                float origin = ray.Origin[axis], direction = ray.Direction[axis];
                if (direction == 0)
                {
                    if (origin < box.BoundsMin[axis] || origin > box.BoundsMax[axis]) return false;
                }
                else
                {
                    float a = (box.BoundsMin[axis] - origin) / direction;
                    float b = (box.BoundsMax[axis] - origin) / direction;
                    low = max(low, min(a, b)); high = min(high, max(a, b));
                    if (low > high) return false;
                }
            }
            return true;
        }

        bool GiSoftwareTriangle(uint triangleIndex, GiRay ray, float maximumT, out GiHit hit)
        {
            hit = (GiHit)0;
            GiTriangle tri = GiTriangles[triangleIndex];
            float3 e1 = tri.P1.xyz - tri.P0.xyz, e2 = tri.P2.xyz - tri.P0.xyz;
            // Scale geometry before cross products; float world positions remain the shared DXR geometry.
            float scale = max(max(max(abs(e1.x), abs(e1.y)), abs(e1.z)), max(max(abs(e2.x), abs(e2.y)), abs(e2.z)));
            if (scale == 0 || !isfinite(scale)) return false;
            e1 /= scale; e2 /= scale;
            float3 p = cross(ray.Direction, e2);
            float determinant = dot(e1, p);
            if (determinant == 0 || !isfinite(determinant)) return false;
            float3 relativeOrigin = (ray.Origin - tri.P0.xyz) / scale;
            float u = dot(relativeOrigin, p) / determinant;
            if (u < 0 || u > 1) return false;
            float3 q = cross(relativeOrigin, e1);
            float v = dot(ray.Direction, q) / determinant;
            if (v < 0 || u + v > 1) return false;
            float t = (dot(e2, q) / determinant) * scale;
            if (!isfinite(t) || t < ray.TMin || t > maximumT) return false;
            if (!AcceptCandidate(triangleIndex, float2(u, v), ray.Direction)) return false;
            hit.TriangleIndex = triangleIndex; hit.T = t; hit.Barycentrics = float2(u, v);
            hit.FrontFace = determinant > 0 ? 1 : 0;
            return true;
        }

        bool GiSoftwareTrace(GiRay ray, bool anyHit, out GiHit hit)
        {
            hit = (GiHit)0;
            uint nodeCount = GiCounts.z;
            if (nodeCount == 0) return false;
            // Common uploads a 1-node empty sentinel when the immutable CPU node span is empty.
            if (GiBvhNodes[0].Data.w != 0) return false;
            uint stack[64]; uint stackCount = 1; stack[0] = 0;
            bool found = false; float closest = ray.TMax;
            [loop] while (stackCount > 0)
            {
                GiBvhNode node = GiBvhNodes[stack[--stackCount]];
                if (!GiSoftwareBox(node, ray, closest)) continue;
                if (node.Data.z != 0)
                {
                    [loop] for (uint i = 0; i < node.Data.z; i++)
                    {
                        uint triangleIndex = GiTriangleOrder[node.Data.x + i]; GiHit candidate;
                        if (GiSoftwareTriangle(triangleIndex, ray, closest, candidate))
                        {
                            if (anyHit) { hit = candidate; return true; }
                            if (!found || candidate.T < closest || (candidate.T == closest && triangleIndex < hit.TriangleIndex))
                            { hit = candidate; closest = candidate.T; found = true; }
                        }
                    }
                }
                else
                {
                    if (stackCount + 2 > 64) { ReportTraversalOverflow(); return false; }
                    stack[stackCount++] = node.Data.y; stack[stackCount++] = node.Data.x;
                }
            }
            return found;
        }
        bool TraceClosest(GiRay ray, out GiHit hit) { return GiSoftwareTrace(ray, false, hit); }
        bool TraceAny(GiRay ray) { GiHit hit; return GiSoftwareTrace(ray, true, hit); }
        """;
}
