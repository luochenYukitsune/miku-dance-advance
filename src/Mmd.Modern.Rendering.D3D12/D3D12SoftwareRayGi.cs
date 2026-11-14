namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Software acceleration provider. Common GI owns uploads, bindings, PSO, dispatch and frame lifetime.
/// This provider replaces only traversal; it never creates a device, queue, radiance estimator or readback.</summary>
internal static class D3D12SoftwareRayGi
{
    internal static string TraceSource => SoftwareTraceHlsl.Source;
}
