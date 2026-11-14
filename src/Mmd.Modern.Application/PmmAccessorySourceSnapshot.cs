using System.Security.Cryptography;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Owned original X bytes and the exact supported reader/adapter configuration used to prepare
/// an accessory. The original path is provenance only: copying or restoring these bytes never opens it.
/// This snapshot describes the X conversion, not the separate completeness of its texture/FX dependencies.</summary>
public sealed class PmmAccessorySourceSnapshot
{
    public const string SupportedParserIdentity = "mda.xtext-reader/1";
    public const string SupportedAdapterIdentity = "mda.x-accessory-adapter/1";

    private readonly byte[] originalBytes;
    private readonly XTextDocument document;
    private readonly OriginalFrameScene source;
    public string OriginalPath { get; }
    public string Sha256 { get; }
    public int ByteLength => originalBytes.Length;
    /// <summary>All reader settings are scalar and init-only, including the explicit string code page.</summary>
    public XTextReadOptions ReadOptions { get; }
    public string ParserIdentity => SupportedParserIdentity;
    public string AdapterIdentity => SupportedAdapterIdentity;
    /// <summary>The existing XAccessoryAdapter's explicit positive unitScale, with no implicit conversion.</summary>
    public float UnitScale { get; }

    private PmmAccessorySourceSnapshot(string originalPath, byte[] originalBytes, string sha256,
        XTextReadOptions readOptions, float unitScale, XTextDocument document, OriginalFrameScene source)
    {
        OriginalPath = originalPath; this.originalBytes = originalBytes; Sha256 = sha256;
        ReadOptions = readOptions; UnitScale = unitScale; this.document = document; this.source = source;
    }

    /// <summary>Returns a detached copy; callers cannot mutate the bytes used for parsing and hashing.</summary>
    public byte[] CopyOriginalBytes() => originalBytes.ToArray();

    internal static void ValidateInputLength(long byteLength, XTextReadOptions options)
    {
        // Match the existing reader's allocation bound before copying a supplied buffer or reading a file.
        // All remaining parser limits are validated by that reader, rather than a second parser policy.
        if (options.MaxInputBytes is < 16 or > 256 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (byteLength > options.MaxInputBytes)
            throw new InvalidDataException(".x exceeds the input size limit.");
    }

    internal static PreparedPmmAccessory Prepare(byte index, string originalPath, ReadOnlyMemory<byte> originalBytes,
        Func<string, SceneTexture?>? loadTexture, XTextReadOptions? readOptions, float unitScale,
        IEnumerable<string>? diagnostics, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        cancellationToken.ThrowIfCancellationRequested();
        readOptions ??= new();
        ValidateInputLength(originalBytes.Length, readOptions);
        // The caller's buffer may change later, including from a texture callback. Both parser and digest
        // consume this one private snapshot, never a later read of the original path or supplied memory.
        byte[] frozen = originalBytes.ToArray();
        using var input = new MemoryStream(frozen, writable: false);
        var document = XTextReader.Read(input, readOptions);
        cancellationToken.ThrowIfCancellationRequested();
        string hash = Convert.ToHexStringLower(SHA256.HashData(frozen));
        var adapted = XAccessoryAdapter.FromDocument(document, texture =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return loadTexture?.Invoke(texture);
        }, unitScale);
        cancellationToken.ThrowIfCancellationRequested();
        var capturedDiagnostics = Array.AsReadOnly(adapted.Diagnostics.Concat(diagnostics ?? []).ToArray());
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new PmmAccessorySourceSnapshot(originalPath, frozen, hash, readOptions, unitScale, document, adapted.Frame);
        return new(index, originalPath, hash, adapted.Frame, capturedDiagnostics)
            { Document = document, SourceSnapshot = snapshot };
    }

    internal void Validate(PreparedPmmAccessory asset)
    {
        if (!string.Equals(asset.Path, OriginalPath, StringComparison.Ordinal)
            || !string.Equals(asset.Sha256, Sha256, StringComparison.Ordinal)
            || !ReferenceEquals(asset.Document, document) || !ReferenceEquals(asset.Source, source))
            throw new InvalidDataException("Accessory original X provenance does not match its identity, digest or canonical prepared document/source.");
    }
}
