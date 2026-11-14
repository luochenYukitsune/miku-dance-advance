using System.Text;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>
/// Discovery-only reads of explicitly requested original files. This does not scan directories or resolve
/// restored packages. Dispose after one collection to release the existing file/ancestor read leases.
/// </summary>
public sealed class D3D9EffectDependencyFileSource : ID3D9EffectDependencySource, IDisposable
{
    private readonly D3D9EffectDependencyLimits limits;
    private readonly EffectTreeFiles.DirectoryLeases leases = new();
    private readonly Dictionary<string, D3D9EffectDependencySnapshot> captured = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<EffectTreeFiles.Identity> identities = [];
    private long totalBytes;
    private bool disposed;

    public D3D9EffectDependencyFileSource(D3D9EffectDependencyLimits? limits = null)
    {
        this.limits = limits ?? new();
        if (this.limits.MaximumFiles is < 1 or > 65536 || this.limits.MaximumFileBytes < 1 || this.limits.MaximumFileBytes > Array.MaxLength ||
            this.limits.MaximumTotalBytes < 1 || this.limits.MaximumReferenceBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(limits));
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Original effect discovery requires Windows file leases.");
    }

    public D3D9EffectDependencySnapshot Capture(D3D9EffectDependencySourceRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        string? origin = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            origin = ResolveOrigin(request, limits.MaximumReferenceBytes);
            if (captured.TryGetValue(origin, out var snapshot)) return snapshot;
            if (captured.Count == limits.MaximumFiles) throw new InvalidDataException("Effect dependency file count exceeds its budget.");
            leases.HoldAncestors(Path.GetDirectoryName(origin)!);
            using var handle = EffectTreeFiles.Open(origin, directory: false);
            var before = EffectTreeFiles.Information(handle, origin);
            EffectTreeFiles.RejectReparse(before, origin);
            if (before.IsDirectory || identities.Contains(before.Identity))
                throw new InvalidDataException("Effect dependency origins must identify distinct regular files.");
            if (before.Length > limits.MaximumFileBytes || before.Length > limits.MaximumTotalBytes - totalBytes)
                throw new InvalidDataException("Effect dependency source bytes exceed their budget.");
            byte[] bytes = new byte[checked((int)before.Length)];
            using var stream = new FileStream(handle, FileAccess.Read);
            for (int offset = 0; offset < bytes.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = stream.Read(bytes.AsSpan(offset, Math.Min(1024 * 1024, bytes.Length - offset)));
                if (count == 0) throw new EndOfStreamException("An effect dependency source changed while reading.");
                offset += count;
            }
            if (stream.ReadByte() != -1 || EffectTreeFiles.Information(handle, origin) != before)
                throw new InvalidDataException("An effect dependency source changed while reading.");
            // Existing handles deny concurrent write/delete. Retain a second handle past this stream's lifetime.
            leases.HoldFile(origin, before.Identity);
            snapshot = new(origin, bytes);
            identities.Add(before.Identity); captured.Add(origin, snapshot); totalBytes += before.Length;
            return snapshot;
        }
        catch (OperationCanceledException) { Dispose(); throw; }
        catch (D3D9EffectDependencyException) { Dispose(); throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Dispose();
            throw new D3D9EffectDependencyException(D3D9EffectDependencyStatus.Incomplete,
                "source-read", "Cannot freeze an original effect dependency: " + error.Message, origin, error);
        }
    }

    private static string ResolveOrigin(D3D9EffectDependencySourceRequest request, long maxReferenceBytes)
    {
        if (!Enum.IsDefined(request.Kind) || request.Reference is null)
            throw new ArgumentException("A known dependency request and its original reference are required.");
        if (request.Reference.ByteLength == 0 || request.Reference.ByteLength > maxReferenceBytes)
            throw new InvalidDataException("Effect dependency reference is empty or exceeds its budget.");
        Encoding encoding = request.Reference.CodePage == 65001 ? new UTF8Encoding(false, true) :
            CodePagesEncodingProvider.Instance.GetEncoding(request.Reference.CodePage, EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback) ?? throw new D3D9EffectDependencyException(
                    D3D9EffectDependencyStatus.Unsupported, "source-encoding", "The dependency reference code page is unavailable.");
        byte[] raw = request.Reference.CopyBytes();
        string reference;
        try
        {
            reference = encoding.GetString(raw);
            if (!encoding.GetBytes(reference).AsSpan().SequenceEqual(raw)) throw new DecoderFallbackException("Reference is not round-trip exact.");
        }
        catch (ArgumentException error)
        {
            throw new D3D9EffectDependencyException(D3D9EffectDependencyStatus.Unsupported,
                "source-encoding", "The dependency reference cannot be decoded without changing its bytes.", innerException: error);
        }
        if (request.Kind == D3D9EffectDependencyRequestKind.Root)
        {
            if (request.BaseOriginAbsolutePath is not null) throw new ArgumentException("A root request cannot have a base file.");
            return D3D9EffectDependencySnapshot.CanonicalizeOrigin(reference);
        }
        if (request.BaseOriginAbsolutePath is null) throw new ArgumentException("A dependency request needs its source base file.");
        string baseOrigin = D3D9EffectDependencySnapshot.CanonicalizeOrigin(request.BaseOriginAbsolutePath);
        // Decode the entire ACP request first: a multibyte trail byte may equal a slash.
        if (reference.StartsWith('\\') || reference.StartsWith('/'))
            throw new ArgumentException("Root-relative, UNC and device dependency paths are unsupported.");
        string candidate = reference.Length >= 2 && reference[1] == ':' ? reference :
            baseOrigin[..(baseOrigin.LastIndexOf('\\') + 1)] + reference;
        return D3D9EffectDependencySnapshot.CanonicalizeOrigin(candidate);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; leases.Dispose(); captured.Clear(); identities.Clear();
    }
}
