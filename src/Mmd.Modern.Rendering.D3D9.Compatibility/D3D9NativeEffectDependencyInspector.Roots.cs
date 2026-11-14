using System.Runtime.InteropServices;
using System.Text;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

public sealed partial class D3D9NativeEffectDependencyInspector
{
    private readonly RootDiscoverCall? discoverRoot;

    /// <summary>
    /// Explicit original-source discovery only. Selects the same readable sibling, embedded-name or default
    /// candidate as the fixed host. A winner must still be frozen by the collector; this never resolves a
    /// restored package or changes the host's default, process directory or any project state.
    /// </summary>
    public string? DiscoverImplicitRoot(string originAbsoluteObjectPath, string? defaultEffectAbsolutePath = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(library == 0, this);
            if (discoverRoot is null) throw new NotSupportedException("The native dependency runtime has no original-root discovery export.");
            int page = checked((int)GetACP()); Encoding encoding = StrictEncoding(page);
            byte[] Encode(string path)
            {
                path = D3D9EffectDependencySnapshot.CanonicalizeOrigin(path);
                byte[] bytes;
                try { bytes = encoding.GetBytes(path); }
                catch (EncoderFallbackException error)
                {
                    throw new D3D9EffectDependencyException(D3D9EffectDependencyStatus.Unsupported,
                        "root-encoding", "The original root discovery path is not representable in the active ACP.", path, error);
                }
                if (bytes.Length >= 260 || path.Length >= 260 || encoding.GetString(bytes) != path)
                    throw new D3D9EffectDependencyException(D3D9EffectDependencyStatus.Unsupported,
                        "root-path", "The original root discovery path must round-trip through ACP within MAX_PATH.", path);
                return bytes;
            }
            using var pins = new Pins();
            var request = new RootDiscoverRequest { Size = (uint)Marshal.SizeOf<RootDiscoverRequest>(), Version = 1,
                ObjectOrigin = pins.Add(Encode(originAbsoluteObjectPath)),
                DefaultOrigin = defaultEffectAbsolutePath is null ? default : pins.Add(Encode(defaultEffectAbsolutePath)) };
            byte[] selected = new byte[260], errorBytes = new byte[2048];
            var output = pins.Add(selected); var errorSpan = pins.Add(errorBytes);
            var result = new RootDiscovery { Size = (uint)Marshal.SizeOf<RootDiscovery>(), Version = 1 };
            var error = new NativeError { Size = (uint)Marshal.SizeOf<NativeError>(), Version = 1,
                Utf8 = errorSpan.Data, Capacity = errorSpan.Length };
            int status = discoverRoot(in request, output.Data, output.Length, ref result, ref error);
            int end = Array.IndexOf(errorBytes, (byte)0);
            Check(status, Encoding.UTF8.GetString(errorBytes, 0, end < 0 ? errorBytes.Length : end));
            if (result.Version != 1 || result.Size != Marshal.SizeOf<RootDiscovery>() || result.CodePage != page ||
                result.Selection > 3 || result.SplitRejected > 1 || result.Reserved != 0 || result.Length >= 260 ||
                selected[result.Length] != 0 || result.Selection == 0 != (result.Length == 0) ||
                selected.AsSpan(0, checked((int)result.Length)).Contains((byte)0))
                throw new InvalidDataException("The native original-root discovery result has an incompatible shape.");
            if (result.Selection == 0) return null;
            return D3D9EffectDependencySnapshot.CanonicalizeOrigin(encoding.GetString(selected, 0, checked((int)result.Length)));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RootDiscoverRequest
    {
        internal uint Size, Version;
        internal ByteSpan ObjectOrigin, DefaultOrigin;
        internal uint Reserved0, Reserved1;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct RootDiscovery
    {
        internal uint Size, Version, CodePage, Selection;
        internal ulong Length;
        internal uint SplitRejected, Reserved;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RootDiscoverCall(in RootDiscoverRequest request, nint selected, ulong capacity,
        ref RootDiscovery result, ref NativeError error);
}
