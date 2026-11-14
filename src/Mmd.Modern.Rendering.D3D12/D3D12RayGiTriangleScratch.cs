using System.Runtime.CompilerServices;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>One exact-sized CPU buffer for the current recording. Upload and DXR setup consume it synchronously.</summary>
internal sealed class D3D12RayGiTriangleScratch
{
    private static readonly int MaximumTriangles = D3D12BufferUploadStaging.MaximumLargeRetainedPageBytes / Unsafe.SizeOf<RayGiTriangleGpu>();
    private RayGiTriangleGpu[]? rows;

    internal RayGiTriangleGpu[]? GetBuffer(int triangleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(triangleCount);
        if (triangleCount == 0 || triangleCount > MaximumTriangles)
        {
            Clear();
            return null;
        }
        if (rows?.Length != triangleCount)
        {
            Clear();
            rows = GC.AllocateUninitializedArray<RayGiTriangleGpu>(triangleCount);
        }
        return rows;
    }

    internal void Clear() => rows = null;
}
