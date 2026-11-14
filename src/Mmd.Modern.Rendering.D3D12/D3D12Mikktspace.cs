using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Optional, version-checked native reference MikkTSpace bridge. No substitute solver.</summary>
internal static class D3D12Mikktspace
{
    internal const string GeneratorRevision = "mikk-3e895b49-abi1-source-local-v1";
    internal const int MaximumCorners = 1_000_000;
    private static readonly object RuntimeLock = new();
    private static Runtime? runtime;
    // At most 16 MB of local tangent payload plus dictionary overhead. This separate cache
    // avoids rerunning the local solver when only World/camera/material presentation changes.
    private static readonly Dictionary<string, Vector4[]> TangentCache = new();
    private static readonly Queue<string> TangentOrder = new();
    private static int cachedCornerCount;

    internal static Vector4[] Generate(Vector3[] positions, Vector3[] normals, Vector2[] uvs, uint[] indices)
    {
        if (positions.Length == 0 || positions.Length > MaximumCorners || normals.Length != positions.Length || uvs.Length != positions.Length
            || indices.Length == 0 || indices.Length > MaximumCorners || indices.Length % 3 != 0)
            throw new ArgumentException("MatCap tangent generation requires a bounded indexed triangle mesh.");
        string key = Fingerprint(positions, normals, uvs, indices);
        lock (RuntimeLock)
        {
            if (TangentCache.TryGetValue(key, out var cached)) return cached;
            Runtime selected = runtime ??= Load();
            var result = new Vector4[indices.Length];
            int status = selected.Generate(positions, normals, uvs, positions.Length, indices, indices.Length, result, result.Length);
            if (status != 0)
                throw new ArgumentException($"MatCap MikkTSpace generation failed (status {status}: {Status(status)}).");
            while (TangentCache.Count >= 32 || cachedCornerCount > MaximumCorners - result.Length)
            {
                string oldest = TangentOrder.Dequeue();
                cachedCornerCount -= TangentCache[oldest].Length; TangentCache.Remove(oldest);
            }
            TangentCache.Add(key, result); TangentOrder.Enqueue(key); cachedCornerCount += result.Length;
            return result;
        }
    }

    private static string Fingerprint(Vector3[] positions, Vector3[] normals, Vector2[] uvs, uint[] indices)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(GeneratorRevision));
        ReadOnlySpan<int> lengths = [positions.Length, indices.Length];
        hash.AppendData(MemoryMarshal.AsBytes(lengths));
        hash.AppendData(MemoryMarshal.AsBytes(positions.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(normals.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(uvs.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(indices.AsSpan()));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static Runtime Load()
    {
        string? configured = Environment.GetEnvironmentVariable("MMD_MIKKTSPACE_LIBRARY_PATH");
        string libraryName = OperatingSystem.IsWindows() ? "mmd_mikktspace.dll"
            : OperatingSystem.IsMacOS() ? "libmmd_mikktspace.dylib" : "libmmd_mikktspace.so";
        string assemblyDirectory = Path.GetDirectoryName(typeof(D3D12Mikktspace).Assembly.Location) ?? AppContext.BaseDirectory;
        string path = string.IsNullOrWhiteSpace(configured) ? Path.Combine(assemblyDirectory, libraryName) : Path.GetFullPath(configured);
        nint handle = 0;
        try
        {
            handle = NativeLibrary.Load(path);
            var abi = Marshal.GetDelegateForFunctionPointer<AbiDelegate>(NativeLibrary.GetExport(handle, "mmd_mikktspace_abi_version"));
            if (abi() != 1) throw new NotSupportedException("MatCap requires MikkTSpace bridge ABI 1.");
            var generate = Marshal.GetDelegateForFunctionPointer<GenerateDelegate>(NativeLibrary.GetExport(handle, "mmd_mikktspace_generate"));
            // The verified generator remains loaded for process lifetime; captured frames may share it.
            return new(handle, generate);
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or NotSupportedException)
        {
            if (handle != 0) NativeLibrary.Free(handle);
            throw new NotSupportedException($"MatCap evaluated-UV tangents require the native MikkTSpace runtime at '{path}'. Build native/Mmd.Modern.Mikktspace and place it beside the renderer, or set MMD_MIKKTSPACE_LIBRARY_PATH to its exact file.", error);
        }
    }

    private static string Status(int value) => value switch
    {
        1 => "invalid mesh or budget", 2 => "non-finite data or invalid shading normal",
        3 => "reference generator could not produce frames", 4 => "non-finite or incomplete corner output", _ => "unknown native error"
    };

    private sealed record Runtime(nint Handle, GenerateDelegate Generate);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AbiDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GenerateDelegate([In] Vector3[] positions, [In] Vector3[] normals, [In] Vector2[] uvs,
        int vertexCount, [In] uint[] indices, int indexCount, [Out] Vector4[] cornerTangents, int outputCornerCount);
}
