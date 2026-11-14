namespace Mmd.Modern.Rendering.D3D12;

/// <summary>The signed loader owns process-wide effect entry tables. Serialize their load/query/create
/// and release lifetimes across both capability probes and rendering; established contexts dispatch independently.</summary>
internal static class D3D12FsrNativeLifetime
{
    internal static readonly object Gate = new();
}
