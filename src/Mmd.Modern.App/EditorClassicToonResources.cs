using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

/// <summary>Frozen, read-only Classic toon images from a directory explicitly selected by the user.
/// Original MMD resources are neither application assets nor installable runtime components.</summary>
internal sealed class EditorClassicToonResources
{
    private const int FirstPngResource = 103;
    private const long MaximumExecutableBytes = 128L * 1024 * 1024;
    private const int MaximumImageBytes = 65 * 1024 * 1024;
    private const long MaximumTotalImageBytes = 128L * 1024 * 1024;
    private readonly Resource[] resources;

    private EditorClassicToonResources(string directory, string executable, string executableSha256,
        Resource[] resources, string[] diagnostics)
    {
        SelectedDirectory = directory;
        ExecutablePath = executable;
        ExecutableSha256 = executableSha256;
        this.resources = resources;
        Entries = Array.AsReadOnly(resources);
        Diagnostics = Array.AsReadOnly(diagnostics);
    }

    internal string SelectedDirectory { get; }
    internal string ExecutablePath { get; }
    internal string ExecutableSha256 { get; }
    internal IReadOnlyList<Resource> Entries { get; }
    internal IReadOnlyList<string> Diagnostics { get; }

    internal SceneTexture LoadDefault() => resources[0].Texture;

    /// <summary>PMX common toon indices are zero-based; original MMD texture slots are 1..10.</summary>
    internal SceneTexture LoadCommon(int commonIndex)
    {
        if (commonIndex is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(commonIndex));
        return resources[commonIndex + 1].Texture;
    }

    /// <summary>Capture and decode before model preparation. Rendering and subsequent loads use
    /// only these immutable images; no path lookup, file read or decode occurs on the render thread.</summary>
    internal static EditorClassicToonResources Capture(string selectedDirectory, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedDirectory);
        if (!Path.IsPathFullyQualified(selectedDirectory))
            throw new ArgumentException("An explicitly selected absolute MMD directory is required.", nameof(selectedDirectory));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(EditorLocalization.Text("Mda.Text.Classic.Resources.WindowsRequired"));
        token.ThrowIfCancellationRequested();
        string directory = Path.GetFullPath(selectedDirectory);
        string executable = Path.Combine(directory, "MikuMikuDance.exe");
        // Keep the selected executable open without write sharing throughout resource capture.
        // No executable code, DLL imports or original MMD process is loaded or started.
        using var executableInput = File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (executableInput.Length is <= 0 or > MaximumExecutableBytes)
            throw new InvalidDataException(EditorLocalization.Text("Mda.Text.Classic.Resources.InvalidExecutable"));
        string executableSha256 = Convert.ToHexString(SHA256.HashData(executableInput));
        token.ThrowIfCancellationRequested();
        const uint loadAsDataFile = 0x00000002, loadAsImageResource = 0x00000020;
        nint module = LoadLibraryExW(executable, 0, loadAsDataFile | loadAsImageResource);
        if (module == 0) throw ResourceError(executable, Marshal.GetLastPInvokeError());
        try
        {
            if ((module & 3) == 0)
                throw new InvalidDataException(EditorLocalization.Text("Mda.Text.Classic.Resources.ResourceMappingFailed"));
            var captured = new Resource[11];
            var diagnostics = new List<string>();
            long encodedBytes = 0, decodedBytes = 0;
            for (int slot = 0; slot < captured.Length; slot++)
            {
                token.ThrowIfCancellationRequested();
                byte[]? bytes = null;
                SceneTexture? texture = null;
                string authoredToken = $"toon{slot:00}.png";
                string source = executable + "#PNG/" + (FirstPngResource + slot);
                if (slot > 0)
                {
                    string relative = $"Data/toon{slot:00}.bmp";
                    string path = Path.Combine(directory, "Data", $"toon{slot:00}.bmp");
                    try
                    {
                        bytes = ReadImage(path, token);
                        texture = Decode(bytes, relative, token);
                        authoredToken = relative;
                        source = path;
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
                    {
                        // This is the original loader's embedded PNG fallback, not a synthetic ramp.
                        // Preserve its reason for diagnostics without retaining partially decoded data.
                        diagnostics.Add(path + ": " + error.Message);
                        bytes = null;
                    }
                }
                if (bytes is null)
                {
                    bytes = ReadPngResource(module, executable, FirstPngResource + slot);
                    texture = Decode(bytes, authoredToken, token);
                }
                var decoded = texture ?? throw new InvalidDataException(
                    EditorLocalization.Format("Mda.Text.Classic.Resources.TextureReadFailed", authoredToken));
                encodedBytes = checked(encodedBytes + bytes.Length);
                decodedBytes = checked(decodedBytes + (long)decoded.Width * decoded.Height * 4);
                if (encodedBytes > MaximumTotalImageBytes || decodedBytes > MaximumTotalImageBytes)
                    throw new InvalidDataException(EditorLocalization.Text("Mda.Text.Classic.Resources.BudgetExceeded"));
                captured[slot] = new(slot, source, authoredToken, bytes, decoded);
            }
            token.ThrowIfCancellationRequested();
            return new(directory, executable, executableSha256, captured, diagnostics.ToArray());
        }
        finally { FreeLibrary(module); }
    }

    private static SceneTexture Decode(byte[] bytes, string token, CancellationToken cancellationToken)
        => EditorTextureLoader.Decode(bytes, token, cancellationToken)
            ?? throw new InvalidDataException(EditorLocalization.Format("Mda.Text.Classic.Resources.TextureReadFailed", token));

    private static byte[] ReadImage(string path, CancellationToken token)
    {
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaximumImageBytes)
            throw new InvalidDataException(EditorLocalization.Format("Mda.Text.Classic.Resources.InvalidTextureSize", path));
        byte[] bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        token.ThrowIfCancellationRequested();
        return bytes;
    }

    private static byte[] ReadPngResource(nint module, string executable, int id)
    {
        nint resource = FindResourceW(module, id, "PNG");
        if (resource == 0) throw ResourceError(executable + "#PNG/" + id, Marshal.GetLastPInvokeError());
        uint length = SizeofResource(module, resource);
        if (length is 0 or > MaximumImageBytes)
            throw new InvalidDataException(EditorLocalization.Format("Mda.Text.Classic.Resources.InvalidEmbeddedSize", id));
        nint loaded = LoadResource(module, resource);
        if (loaded == 0) throw ResourceError(executable + "#PNG/" + id, Marshal.GetLastPInvokeError());
        nint data = LockResource(loaded);
        if (data == 0) throw ResourceError(executable + "#PNG/" + id, Marshal.GetLastPInvokeError());
        byte[] bytes = new byte[checked((int)length)];
        Marshal.Copy(data, bytes, 0, bytes.Length);
        ReadOnlySpan<byte> pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!bytes.AsSpan().StartsWith(pngSignature))
            throw new InvalidDataException(EditorLocalization.Format("Mda.Text.Classic.Resources.InvalidEmbeddedImage", id));
        return bytes;
    }

    private static IOException ResourceError(string source, int error)
        => new(EditorLocalization.Format("Mda.Text.Classic.Resources.ReadFailed", source, new Win32Exception(error).Message));

    /// <summary>Encoded source bytes remain captured separately from decoded pixels so a later
    /// Classic-specific portable resource codec can preserve their exact original identity.</summary>
    internal sealed class Resource
    {
        private readonly byte[] bytes;
        internal Resource(int slot, string source, string authoredToken, byte[] bytes, SceneTexture texture)
        {
            Slot = slot; Source = source; AuthoredToken = authoredToken; this.bytes = bytes;
            Texture = texture; Sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        }
        internal int Slot { get; }
        internal string Source { get; }
        internal string AuthoredToken { get; }
        internal string Sha256 { get; }
        internal SceneTexture Texture { get; }
        internal ReadOnlySpan<byte> EncodedBytes => bytes;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint LoadLibraryExW(string path, nint file, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint FindResourceW(nint module, nint name, string type);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint SizeofResource(nint module, nint resource);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint LoadResource(nint module, nint resource);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint LockResource(nint resource);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(nint module);
}
