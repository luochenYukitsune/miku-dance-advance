using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Mmd.Modern.App;

/// <summary>Explicit, immutable identities for the ordinary-surface experiment's installed files.
/// This does not discover components, load code, or establish support on a graphics device.</summary>
internal sealed class EditorCompatibilityRuntimeSource
{
    private sealed record FileIdentity(string Path, long Bytes, string Sha256);
    private readonly FileIdentity[] files;
    internal string NativeLibraryPath => files[0].Path;
    internal string D3dxLibraryPath => files[1].Path;
    internal string CompilerLibraryPath => files[2].Path;

    internal EditorCompatibilityRuntimeSource(string nativeLibraryPath, string d3dxLibraryPath, string compilerLibraryPath)
    {
        files = [Capture(nativeLibraryPath, "Mmd.Modern.D3D9.Compatibility.dll"),
            Capture(d3dxLibraryPath, "d3dx9_43.dll"), Capture(compilerLibraryPath, "d3dcompiler_43.dll")];
        if (files.Select(file => System.IO.Path.GetDirectoryName(file.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            throw new ArgumentException("The isolated native library and both fixed 43 runtimes must share one explicit directory.");
    }

    internal void ValidateFiles()
    {
        foreach (var expected in files)
            if (Capture(expected.Path, System.IO.Path.GetFileName(expected.Path)) != expected)
                throw new InvalidDataException("A frozen compatibility runtime file changed: " + expected.Path);
    }

    private static FileIdentity Capture(string path, string filename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.Path.IsPathFullyQualified(path)
            || !string.Equals(System.IO.Path.GetFileName(path), filename, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An explicit absolute path to " + filename + " is required.", nameof(path));
        string absolute = System.IO.Path.GetFullPath(path);
        using var stream = File.OpenRead(absolute);
        return new(absolute, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }

    internal IDisposable LoadDependencies()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The compatibility runtime requires Windows.");
        ValidateFiles();
        var handles = new List<nint>();
        try
        {
            foreach (string path in new[] { CompilerLibraryPath, D3dxLibraryPath })
            {
                var handle = NativeLibrary.Load(path);
                handles.Add(handle);
                var actual = new StringBuilder(32768);
                uint count = GetModuleFileNameW(handle, actual, (uint)actual.Capacity);
                if (count == 0 || count >= actual.Capacity
                    || !string.Equals(System.IO.Path.GetFullPath(actual.ToString()), path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The loaded compatibility runtime did not come from its explicit frozen path: " + path);
            }
            ValidateFiles();
            return new DependencyLease(handles.ToArray());
        }
        catch { for (int i = handles.Count - 1; i >= 0; i--) NativeLibrary.Free(handles[i]); throw; }
    }

    private sealed class DependencyLease(nint[] handles) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            for (int i = handles.Length - 1; i >= 0; i--) NativeLibrary.Free(handles[i]);
            disposed = true;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(nint module, StringBuilder filename, uint size);
}
