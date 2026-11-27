using System.Numerics;
using System.Runtime.InteropServices;

namespace Mmd.Modern.Core;

/// <summary>3D frustum cluster grid dimensions for Clustered Forward lighting.</summary>
public readonly record struct ClusterGridDimensions(int X, int Y, int Z, int MaxLightsPerCluster)
{
    public static ClusterGridDimensions Default { get; } = new(16, 9, 24, SceneLightingSnapshot.MaxActiveLights);

    public int TotalClusters => checked(X * Y * Z);

    public int TotalIndexCapacity => checked(TotalClusters * MaxLightsPerCluster);

    public void Validate()
    {
        if (X is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(X));
        if (Y is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(Y));
        if (Z is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(Z));
        if (MaxLightsPerCluster is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(MaxLightsPerCluster));
    }

    public int ClusterIndex(int cx, int cy, int cz) => (cz * Y + cy) * X + cx;
}

/// <summary>View-space axis-aligned bounding box for one frustum cluster cell.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ClusterAabb(Vector3 Min, Vector3 Max);

/// <summary>Offset and count into the flat clustered light index buffer for one cluster.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ClusterLightRange(uint Offset, uint Count);

/// <summary>Format-neutral light bounding input consumed by cluster light culling.</summary>
public readonly record struct ClusterLightEntry(
    SceneLightKind Kind,
    Vector3 WorldPosition,
    Vector3 WorldDirection,
    float Range,
    float OuterConeRadians,
    float AreaExtent);

/// <summary>CPU reference math for 3D frustum cluster AABB generation, depth slicing, and light culling.
/// Mirrors the GPU compute and pixel shaders so CPU unit tests and GPU execution share one exact contract.</summary>
public static class ClusteredLightingMath
{
    /// <summary>Matches <see cref="SceneCamera.ViewProjection"/> view matrix construction (row-vector convention).</summary>
    public static Matrix4x4 CreateViewMatrix(SceneCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        camera.Validate();
        return camera.LeftHanded
            ? Matrix4x4.CreateLookAtLeftHanded(camera.Eye, camera.Target, camera.Up)
            : Matrix4x4.CreateLookAt(camera.Eye, camera.Target, camera.Up);
    }

    /// <summary>Matches <see cref="SceneCamera.ViewProjection"/> projection matrix construction (including jitter).</summary>
    public static Matrix4x4 CreateProjectionMatrix(SceneCamera camera, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        camera.Validate();
        Matrix4x4 projection = camera.LeftHanded
            ? (camera.VerticalFovDegrees is { } lhFov
                ? Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(lhFov * MathF.PI / 180f, (float)width / height, camera.Near, camera.Far)
                : Matrix4x4.CreateOrthographicLeftHanded(camera.OrthographicHeight * width / height, camera.OrthographicHeight, camera.Near, camera.Far))
            : (camera.VerticalFovDegrees is { } rhFov
                ? Matrix4x4.CreatePerspectiveFieldOfView(rhFov * MathF.PI / 180f, (float)width / height, camera.Near, camera.Far)
                : Matrix4x4.CreateOrthographic(camera.OrthographicHeight * width / height, camera.OrthographicHeight, camera.Near, camera.Far));
        return camera.ProjectionJitterPixels == Vector2.Zero
            ? projection
            : projection * Matrix4x4.CreateTranslation(2f * camera.ProjectionJitterPixels.X / width, -2f * camera.ProjectionJitterPixels.Y / height, 0f);
    }

    /// <summary>Positive linear view-space depth range [DepthNear, DepthFar] for depth slice <paramref name="sliceZ"/>.
    /// Perspective uses exponential/logarithmic slices: near * (far / near)^(z / gridZ).
    /// Orthographic uses uniform linear slices across [near, far].</summary>
    public static (float DepthNear, float DepthFar) SliceDepthBounds(int sliceZ, float near, float far, int gridZ, bool orthographic)
    {
        if (sliceZ < 0 || sliceZ >= gridZ) throw new ArgumentOutOfRangeException(nameof(sliceZ));
        if (!(near > 0) || !(far > near) || gridZ < 1) throw new ArgumentOutOfRangeException(nameof(near));
        float t0 = (float)sliceZ / gridZ;
        float t1 = (float)(sliceZ + 1) / gridZ;
        if (orthographic)
            return (near + (far - near) * t0, near + (far - near) * t1);
        float ratio = far / near;
        return (near * MathF.Pow(ratio, t0), near * MathF.Pow(ratio, t1));
    }

    /// <summary>Maps positive linear view-space depth to the corresponding cluster depth slice index in [0, gridZ - 1].</summary>
    public static int SliceFromViewDepth(float positiveDepth, float near, float far, int gridZ, bool orthographic)
    {
        if (gridZ < 1 || !(near > 0) || !(far > near)) throw new ArgumentOutOfRangeException(nameof(gridZ));
        float sliceF;
        if (orthographic)
        {
            float normalized = Math.Clamp((positiveDepth - near) / MathF.Max(far - near, 1e-6f), 0f, 1f);
            sliceF = normalized * gridZ;
        }
        else
        {
            float clamped = MathF.Max(positiveDepth, near);
            float logRatio = MathF.Max(MathF.Log(far / near), 1e-6f);
            sliceF = MathF.Log(clamped / near) / logRatio * gridZ;
        }
        int slice = (int)MathF.Floor(sliceF);
        return Math.Clamp(slice, 0, gridZ - 1);
    }

    /// <summary>Builds the exact view-space AABB for cluster cell (cx, cy, cz).</summary>
    public static ClusterAabb BuildClusterAabb(
        int cx,
        int cy,
        int cz,
        ClusterGridDimensions grid,
        Matrix4x4 inverseProjection,
        float near,
        float far,
        bool leftHanded,
        bool orthographic)
    {
        grid.Validate();
        if (cx < 0 || cx >= grid.X) throw new ArgumentOutOfRangeException(nameof(cx));
        if (cy < 0 || cy >= grid.Y) throw new ArgumentOutOfRangeException(nameof(cy));
        if (cz < 0 || cz >= grid.Z) throw new ArgumentOutOfRangeException(nameof(cz));

        // Screen pixel Y grows downward (0 at top, height at bottom), while NDC Y grows upward (+1 at top, -1 at bottom).
        float ndcMinX = ((float)cx / grid.X) * 2f - 1f;
        float ndcMaxX = ((float)(cx + 1) / grid.X) * 2f - 1f;
        float ndcMaxY = 1f - ((float)cy / grid.Y) * 2f;
        float ndcMinY = 1f - ((float)(cy + 1) / grid.Y) * 2f;

        var (depthNear, depthFar) = SliceDepthBounds(cz, near, far, grid.Z, orthographic);
        float zSign = leftHanded ? 1f : -1f;
        float viewZNear = zSign * depthNear;
        float viewZFar = zSign * depthFar;

        Vector3 UnprojectNear(float nx, float ny)
        {
            var clip = Vector4.Transform(new Vector4(nx, ny, 0f, 1f), inverseProjection);
            return new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
        }

        Vector3 p00 = UnprojectNear(ndcMinX, ndcMinY);
        Vector3 p10 = UnprojectNear(ndcMaxX, ndcMinY);
        Vector3 p01 = UnprojectNear(ndcMinX, ndcMaxY);
        Vector3 p11 = UnprojectNear(ndcMaxX, ndcMaxY);

        Vector3 ProjectToDepth(Vector3 nearPoint, float targetViewZ)
        {
            if (orthographic)
                return new Vector3(nearPoint.X, nearPoint.Y, targetViewZ);
            float scale = MathF.Abs(nearPoint.Z) > 1e-8f ? targetViewZ / nearPoint.Z : 1f;
            return nearPoint * scale;
        }

        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);

        void Include(Vector3 pt)
        {
            min = Vector3.Min(min, pt);
            max = Vector3.Max(max, pt);
        }

        Include(ProjectToDepth(p00, viewZNear));
        Include(ProjectToDepth(p10, viewZNear));
        Include(ProjectToDepth(p01, viewZNear));
        Include(ProjectToDepth(p11, viewZNear));
        Include(ProjectToDepth(p00, viewZFar));
        Include(ProjectToDepth(p10, viewZFar));
        Include(ProjectToDepth(p01, viewZFar));
        Include(ProjectToDepth(p11, viewZFar));

        return new ClusterAabb(min, max);
    }

    public static ClusterAabb[] BuildAllClusterAabbs(ClusterGridDimensions grid, SceneCamera camera, int width, int height)
    {
        grid.Validate();
        var projection = CreateProjectionMatrix(camera, width, height);
        if (!Matrix4x4.Invert(projection, out var inverseProjection))
            throw new ArgumentException("Camera projection matrix must be invertible.", nameof(camera));
        bool orthographic = camera.VerticalFovDegrees is null;
        var aabbs = new ClusterAabb[grid.TotalClusters];
        for (int cz = 0; cz < grid.Z; cz++)
        {
            for (int cy = 0; cy < grid.Y; cy++)
            {
                for (int cx = 0; cx < grid.X; cx++)
                {
                    aabbs[grid.ClusterIndex(cx, cy, cz)] = BuildClusterAabb(
                        cx, cy, cz, grid, inverseProjection, camera.Near, camera.Far, camera.LeftHanded, orthographic);
                }
            }
        }
        return aabbs;
    }

    /// <summary>Resolves the flat cluster index for a rasterized fragment at pixel position (pixelX, pixelY) and worldPosition.</summary>
    public static int ClusterIndexForFragment(
        float pixelX,
        float pixelY,
        Vector3 worldPosition,
        int width,
        int height,
        ClusterGridDimensions grid,
        Matrix4x4 view,
        float near,
        float far,
        bool leftHanded,
        bool orthographic)
    {
        grid.Validate();
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        int cx = Math.Clamp((int)MathF.Floor(pixelX / width * grid.X), 0, grid.X - 1);
        int cy = Math.Clamp((int)MathF.Floor(pixelY / height * grid.Y), 0, grid.Y - 1);
        float viewZ = Vector3.Transform(worldPosition, view).Z;
        float positiveDepth = leftHanded ? viewZ : -viewZ;
        int cz = SliceFromViewDepth(positiveDepth, near, far, grid.Z, orthographic);
        return grid.ClusterIndex(cx, cy, cz);
    }

    /// <summary>Exact sphere-vs-AABB intersection test in view space.</summary>
    public static bool IntersectsSphereAabb(Vector3 center, float radius, ClusterAabb aabb)
    {
        if (!(radius >= 0f)) return false;
        Vector3 closest = Vector3.Clamp(center, aabb.Min, aabb.Max);
        Vector3 delta = closest - center;
        return delta.LengthSquared() <= radius * radius;
    }

    /// <summary>Tight conservative bounding sphere for a finite spotlight cone of length <paramref name="range"/>
    /// and outer half-angle <paramref name="outerConeRadians"/> in (0, pi/2].</summary>
    public static (Vector3 Center, float Radius) ComputeSpotBoundingSphere(
        Vector3 viewPosition,
        Vector3 viewDirection,
        float range,
        float outerConeRadians)
    {
        float cosOuter = MathF.Cos(outerConeRadians);
        float sinOuter = MathF.Sin(outerConeRadians);
        if (outerConeRadians > MathF.PI * 0.25f)
            return (viewPosition + viewDirection * (range * cosOuter), range * sinOuter);
        float radius = range / MathF.Max(2f * cosOuter, 1e-4f);
        return (viewPosition + viewDirection * radius, radius);
    }

    /// <summary>Conservative light-vs-cluster intersection test matching the GPU culling compute shader.</summary>
    public static bool IntersectsLight(ClusterLightEntry light, Matrix4x4 view, ClusterAabb aabb)
    {
        if (light.Kind == SceneLightKind.Directional) return true;
        Vector3 viewPos = Vector3.Transform(light.WorldPosition, view);
        if (light.Kind == SceneLightKind.Point)
            return IntersectsSphereAabb(viewPos, light.Range, aabb);
        if (light.Kind == SceneLightKind.Spot)
        {
            if (!IntersectsSphereAabb(viewPos, light.Range, aabb)) return false;
            Vector3 viewDir = Vector3.Normalize(Vector3.TransformNormal(light.WorldDirection, view));
            var (coneCenter, coneRadius) = ComputeSpotBoundingSphere(viewPos, viewDir, light.Range, light.OuterConeRadians);
            return IntersectsSphereAabb(coneCenter, coneRadius, aabb);
        }
        // Area light: 4 Gauss quadrature samples sit within AreaExtent of light.WorldPosition.
        return IntersectsSphereAabb(viewPos, light.Range + light.AreaExtent, aabb);
    }

    /// <summary>Converts an authored native light to its cluster-culling bounding entry.</summary>
    public static ClusterLightEntry FromNativeLight(NativeSceneLight light)
    {
        ArgumentNullException.ThrowIfNull(light);
        Vector3 direction = Vector3.Normalize(Vector3.Transform(-Vector3.UnitZ, light.Rotation));
        float areaExtent = 0f;
        if (light.Kind == SceneLightKind.Area && light.AreaSize is { } size)
        {
            float halfW = size.X * 0.5f;
            float halfH = size.Y * 0.5f;
            // Matches 2x2 Gauss quadrature offset (1/sqrt(3) * half-extents).
            areaExtent = MathF.Sqrt(halfW * halfW + halfH * halfH) * 0.5773502691896258f;
        }
        return new ClusterLightEntry(light.Kind, light.Position, direction, light.Range, light.OuterConeRadians, areaExtent);
    }

    /// <summary>CPU reference light culling across all clusters, returning per-cluster (Offset, Count) ranges
    /// and the flat light index buffer matching the GPU compute shader layout.</summary>
    public static (ClusterLightRange[] Grid, uint[] Indices) CullLights(
        ClusterGridDimensions grid,
        ReadOnlySpan<ClusterAabb> aabbs,
        Matrix4x4 view,
        ReadOnlySpan<ClusterLightEntry> lights)
    {
        grid.Validate();
        int totalClusters = grid.TotalClusters;
        if (aabbs.Length != totalClusters)
            throw new ArgumentException($"Expected {totalClusters} cluster AABBs.", nameof(aabbs));

        var ranges = new ClusterLightRange[totalClusters];
        var indices = new uint[grid.TotalIndexCapacity];
        for (int c = 0; c < totalClusters; c++)
        {
            uint offset = (uint)(c * grid.MaxLightsPerCluster);
            uint count = 0;
            var aabb = aabbs[c];
            for (int i = 0; i < lights.Length; i++)
            {
                if (!IntersectsLight(lights[i], view, aabb)) continue;
                if (count < (uint)grid.MaxLightsPerCluster)
                {
                    indices[offset + count] = (uint)i;
                    count++;
                }
            }
            ranges[c] = new ClusterLightRange(offset, count);
        }
        return (ranges, indices);
    }
}
