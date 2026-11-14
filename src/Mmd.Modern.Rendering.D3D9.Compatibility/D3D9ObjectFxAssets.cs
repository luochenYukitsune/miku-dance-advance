using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>
/// Keeps an explicitly caller-declared COMPLETE FX/include/resource manifest present and read-only in place.
/// Every FILE assignment must be declared. No includes or resources are discovered, parsed or verified for
/// transitive completeness. Relative references retain their existing directory layout; no assets are copied
/// or changed. Only normal absolute Windows drive paths without reparse files/directories are supported.
/// Dispose closes admission to new sessions; existing session leases survive until native Destroy and module
/// unload both succeed, including initialization failures and explicit cleanup retries.
/// </summary>
public sealed class D3D9ObjectFxAssets : IDisposable
{
    private const int MaximumPaths = 65536;
    private const int MaximumPathBytes = 32767;
    private const int MaximumManifestBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object gate = new();
    private readonly List<IDisposable> handles;
    private int references = 1;
    private bool closed;

    private D3D9ObjectFxAssets(D3D9ObjectFxConfiguration configuration, string[] paths, List<IDisposable> handles)
    {
        Configuration = configuration;
        CompleteDependencyPaths = new ReadOnlyCollection<string>(paths);
        this.handles = handles;
    }

    public D3D9ObjectFxConfiguration Configuration { get; }
    public IReadOnlyList<string> CompleteDependencyPaths { get; }

    /// <summary>
    /// Opens at most 65536 declared files with at most 16 MiB of strict UTF-8 path data (32767 bytes per path).
    /// Rejects duplicate normalized names or physical file identities. Retained read-share ancestor handles
    /// prevent a declared normal path from being redirected by a directory rename/reparse replacement.
    /// Unique ancestor handles and their path data have the same independent count/byte bounds.
    /// Any failure rolls back all file and directory handles acquired by this call.
    /// </summary>
    public static D3D9ObjectFxAssets Open(D3D9ObjectFxConfiguration configuration, IEnumerable<string> completeDependencyPaths)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(completeDependencyPaths);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Object FX asset leases require Windows file-sharing semantics.");
        var paths = new List<string>();
        var normalizedPaths = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var path in completeDependencyPaths)
        {
            if (paths.Count == MaximumPaths) throw new ArgumentException("At most 65536 declared object FX dependencies are supported.", nameof(completeDependencyPaths));
            var normalized = NormalizePath(path, nameof(completeDependencyPaths));
            if (!names.Add(normalized)) throw new ArgumentException("Duplicate or aliased object FX dependency path.", nameof(completeDependencyPaths));
            bytes += Utf8Length(path, nameof(completeDependencyPaths));
            if (bytes > MaximumManifestBytes) throw new ArgumentException("Declared object FX paths exceed the 16 MiB manifest budget.", nameof(completeDependencyPaths));
            paths.Add(path);
            normalizedPaths.Add(normalized);
        }
        foreach (var assignment in configuration.Assignments)
        {
            if (assignment.Assignment == D3D9ObjectFxAssignmentMode.File &&
                !names.Contains(NormalizePath(assignment.AbsoluteFxPath!, nameof(configuration))))
                throw new ArgumentException("The complete dependency manifest must include every FILE assignment root.", nameof(completeDependencyPaths));
        }
        if (configuration.Resources is { } resources)
        {
            foreach (var binding in resources.Bindings)
                if (!names.Contains(binding.MaterializedAbsolutePath))
                    throw new ArgumentException("The complete dependency manifest must include every materialized effect resource.", nameof(completeDependencyPaths));
        }

        var acquired = new List<IDisposable>();
        try
        {
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var identities = new HashSet<FileIdentity>();
            long directoryBytes = 0;
            foreach (var path in normalizedPaths)
            {
                HoldAncestors(path, directories, acquired, ref directoryBytes);
                // Ancestors are held before traversal; OPEN_REPARSE_POINT checks the final file itself.
                var file = OpenHandle(path, directory: false);
                try
                {
                    var information = Information(file, path);
                    RejectReparse(information.Attributes, path);
                    if ((information.Attributes & 0x10) != 0)
                        throw new ArgumentException("Object FX dependencies must be files, not directories.", nameof(completeDependencyPaths));
                    var identity = new FileIdentity(information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow);
                    if (!identities.Add(identity))
                        throw new ArgumentException("Multiple declared dependency names identify the same physical file.", nameof(completeDependencyPaths));
                    var stream = new FileStream(file, FileAccess.Read);
                    acquired.Add(stream);
                }
                catch { file.Dispose(); throw; }
            }
            return new(configuration, paths.ToArray(), acquired);
        }
        catch
        {
            DisposeHandles(acquired);
            throw;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (closed) return;
            closed = true;
            ReleaseUnderLock();
        }
    }

    internal Lease Acquire()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            var lease = new Lease(this);
            checked { references++; }
            return lease;
        }
    }

    private void Release()
    {
        lock (gate) ReleaseUnderLock();
    }

    private void ReleaseUnderLock()
    {
        if (--references == 0) DisposeHandles(handles);
    }

    internal sealed class Lease(D3D9ObjectFxAssets assets) : IDisposable
    {
        private D3D9ObjectFxAssets? retained = assets;
        public void Dispose() => Interlocked.Exchange(ref retained, null)?.Release();
    }

    private static void DisposeHandles(List<IDisposable> acquired)
    {
        for (var index = acquired.Count - 1; index >= 0; index--) acquired[index].Dispose();
        acquired.Clear();
    }

    private static string NormalizePath(string path, string parameter)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            path[2] is not ('\\' or '/'))
            throw new ArgumentException("An absolute Windows drive file path is required.", parameter);
        _ = Utf8Length(path, parameter);
        for (var index = 2; index < path.Length; index++)
        {
            var value = path[index];
            if (value < 32 || value is ':' or '<' or '>' or '"' or '|' or '?' or '*')
                throw new ArgumentException("Object FX paths contain an invalid Windows file-name character.", parameter);
        }
        foreach (var segment in path[3..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..") continue;
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
                throw new ArgumentException("Trailing dots/spaces are not supported in object FX file paths.", parameter);
            var device = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
                device[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')
                throw new ArgumentException("Reserved Windows device names are not supported in object FX paths.", parameter);
        }
        return Path.GetFullPath(path).Replace('/', '\\');
    }

    private static int Utf8Length(string path, string parameter)
    {
        int length;
        try { length = StrictUtf8.GetByteCount(path); }
        catch (EncoderFallbackException error) { throw new ArgumentException("Object FX paths must be valid Unicode for strict UTF-8 encoding.", parameter, error); }
        if (length > MaximumPathBytes) throw new ArgumentException("An object FX path exceeds 32767 UTF-8 bytes.", parameter);
        return length;
    }

    private static void HoldAncestors(string path, HashSet<string> directories, List<IDisposable> acquired, ref long bytes)
    {
        var pending = new Stack<string>();
        for (var directory = Path.GetDirectoryName(path); directory is not null && !directories.Contains(directory);
            directory = Path.GetDirectoryName(directory)) pending.Push(directory);
        while (pending.TryPop(out var directory))
        {
            if (directories.Count == MaximumPaths) throw new ArgumentException("Object FX dependencies exceed the 65536 ancestor-directory budget.");
            bytes += Utf8Length(directory, "completeDependencyPaths");
            if (bytes > MaximumManifestBytes) throw new ArgumentException("Object FX ancestor paths exceed the 16 MiB budget.");
            var handle = OpenHandle(directory, directory: true);
            try
            {
                var information = Information(handle, directory);
                RejectReparse(information.Attributes, directory);
                if ((information.Attributes & 0x10) == 0) throw new ArgumentException("An object FX ancestor is not a directory.");
                acquired.Add(handle);
                directories.Add(directory);
            }
            catch { handle.Dispose(); throw; }
        }
    }

    private static SafeFileHandle OpenHandle(string path, bool directory)
    {
        const uint genericRead = 0x80000000, readAttributes = 0x80, openExisting = 3;
        const uint openReparsePoint = 0x00200000, backupSemantics = 0x02000000;
        var handle = CreateFile(path, directory ? readAttributes : genericRead, 1, 0, openExisting,
            openReparsePoint | (directory ? backupSemantics : 0), 0);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw error switch
        {
            2 => new FileNotFoundException("A declared object FX dependency was not found.", path),
            3 => new DirectoryNotFoundException($"A declared object FX directory was not found: {path}"),
            5 => new UnauthorizedAccessException($"Cannot lease declared object FX path: {path}"),
            _ => new IOException($"Cannot lease declared object FX path '{path}': {new Win32Exception(error).Message}", unchecked((int)(0x80070000U | (uint)error)))
        };
    }

    private static HandleInformation Information(SafeFileHandle handle, string path)
    {
        if (GetFileInformationByHandle(handle, out var information)) return information;
        throw new IOException($"Cannot inspect declared object FX path '{path}'.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    private static void RejectReparse(uint attributes, string path)
    {
        if ((attributes & 0x400) != 0) throw new ArgumentException($"Reparse files/directories are not supported in object FX dependency paths: {path}");
    }

    private readonly record struct FileIdentity(uint Volume, uint IndexHigh, uint IndexLow);

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        internal uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInformation information);
}
