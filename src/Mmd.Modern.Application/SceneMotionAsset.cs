using System.Security.Cryptography;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>A validated VMD snapshot retained independently of its source file.</summary>
public sealed class SceneMotionAsset
{
    public const int MaxBytes = 64 * 1024 * 1024;
    private readonly byte[] bytes;

    public string Name { get; }
    public string Sha256 { get; }
    public int AnsiCodePage { get; }
    public IReadOnlyList<VmdNameOverride> NameOverrides { get; }
    public int Length => bytes.Length;
    public VmdMotion Motion { get; }
    internal ReadOnlySpan<byte> Bytes => bytes;

    private SceneMotionAsset(byte[] bytes, string name, string sha256, VmdMotion motion, int ansiCodePage)
    {
        this.bytes = bytes;
        Name = name;
        Sha256 = sha256;
        Motion = motion;
        AnsiCodePage = ansiCodePage;
        NameOverrides = Array.AsReadOnly(VmdNameCompatibility.CreateReadOptions(motion, new() { AnsiCodePage = ansiCodePage })
            .NameOverrides.Where(item => item.Name.AnsiCodePage != ansiCodePage).ToArray());
    }

    /// <summary>Copies and validates the exact bytes that later project saves will retain.</summary>
    public static SceneMotionAsset FromBytes(ReadOnlySpan<byte> bytes, string name = "motion.vmd", VmdReadOptions? options = null)
    {
        string baseName = ValidateName(name);
        ValidateLength(bytes.Length);
        return FromOwnedBytes(bytes.ToArray(), baseName, options ?? new());
    }

    /// <summary>Serializes edited tracks into the immutable VMD snapshot used by project saves.
    /// All retained scene tracks are preserved; unrepresentable VMD names fail before binding changes.</summary>
    public static SceneMotionAsset FromMotion(VmdMotion motion, string name = "motion.vmd", VmdWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(motion);
        string baseName = ValidateName(name);
        byte[] bytes = VmdWriter.ToBytes(motion, options);
        ValidateLength(bytes.Length);
        return FromOwnedBytes(bytes, baseName, VmdNameCompatibility.CreateReadOptions(motion, options));
    }

    /// <summary>Reads a fully qualified VMD path once without modifying its source.</summary>
    public static SceneMotionAsset Load(string fullyQualifiedPath, CancellationToken cancellationToken = default, VmdReadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedPath);
        if (!Path.IsPathFullyQualified(fullyQualifiedPath))
            throw new ArgumentException("Use a fully qualified .vmd path.", nameof(fullyQualifiedPath));
        string name = ValidateName(fullyQualifiedPath);
        string path = Path.GetFullPath(fullyQualifiedPath);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes)
            throw new InvalidDataException($"VMD input exceeds the supported size of {MaxBytes} bytes.");
        using var snapshot = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Probe at most one byte beyond the limit, even if an already-open writer grows the file.
            int requested = Math.Min(buffer.Length, MaxBytes - (int)snapshot.Length + 1);
            int count = stream.Read(buffer, 0, requested);
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0) break;
            if (snapshot.Length + count > MaxBytes)
                throw new InvalidDataException($"VMD input exceeds the supported size of {MaxBytes} bytes.");
            snapshot.Write(buffer, 0, count);
        }

        byte[] ownedBytes = snapshot.ToArray();
        ValidateLength(ownedBytes.Length);
        cancellationToken.ThrowIfCancellationRequested();
        SceneMotionAsset asset = FromOwnedBytes(ownedBytes, name, options ?? new());
        cancellationToken.ThrowIfCancellationRequested();
        return asset;
    }

    /// <summary>Retains per-binding names while sharing an already validated immutable snapshot.</summary>
    internal SceneMotionAsset WithName(string name)
        => new(bytes, ValidateName(name), Sha256, Motion, AnsiCodePage);

    private static SceneMotionAsset FromOwnedBytes(byte[] bytes, string name, VmdReadOptions options)
    {
        using var input = new MemoryStream(bytes, writable: false);
        VmdMotion motion = VmdReader.Read(input, options);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new(bytes, name, sha256, motion, options.AnsiCodePage);
    }

    private static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string baseName = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(baseName)
            || baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(baseName))
            || !string.Equals(Path.GetExtension(baseName), ".vmd", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a valid .vmd file name.", nameof(name));
        return baseName;
    }

    private static void ValidateLength(int length)
    {
        if (length is < 1 or > MaxBytes)
            throw new InvalidDataException($"VMD input must contain between 1 and {MaxBytes} bytes.");
    }
}
