using System.Collections.ObjectModel;
using System.Text;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>An immutable original file identity and its leased package-local file path. Neither path is opened.</summary>
public sealed class D3D9EffectResourceBinding
{
    public D3D9EffectResourceBinding(string originAbsolutePath, string materializedAbsolutePath)
    {
        OriginAbsolutePath = D3D9EffectResourcePaths.Canonicalize(originAbsolutePath, nameof(originAbsolutePath));
        MaterializedAbsolutePath = D3D9EffectResourcePaths.Canonicalize(materializedAbsolutePath, nameof(materializedAbsolutePath));
        OriginUtf8 = D3D9EffectResourcePaths.Utf8.GetBytes(OriginAbsolutePath);
        MaterializedUtf8 = D3D9EffectResourcePaths.Utf8.GetBytes(MaterializedAbsolutePath);
    }

    public string OriginAbsolutePath { get; }
    public string MaterializedAbsolutePath { get; }
    internal byte[] OriginUtf8 { get; }
    internal byte[] MaterializedUtf8 { get; }
}

/// <summary>
/// Copies a bounded, immutable mapping without filesystem access. Origin identities are provenance only;
/// every materialized file must separately be included in the complete session asset lease.
/// </summary>
public sealed class D3D9EffectResourceMap
{
    private readonly HashSet<string> localPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> originToLocal = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> localToOrigin = new(StringComparer.OrdinalIgnoreCase);

    public D3D9EffectResourceMap(IEnumerable<D3D9EffectResourceBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var rows = new List<D3D9EffectResourceBinding>();
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long pathBytes = 0;
        foreach (var row in bindings)
        {
            ArgumentNullException.ThrowIfNull(row);
            if (rows.Count == 65536) throw new ArgumentException("At most 65536 effect resource bindings are supported.", nameof(bindings));
            // Cross-column aliases would make reverse base-file lookup ambiguous. A single identity row is allowed.
            if (origins.Contains(row.OriginAbsolutePath) || localPaths.Contains(row.MaterializedAbsolutePath) ||
                localPaths.Contains(row.OriginAbsolutePath) || origins.Contains(row.MaterializedAbsolutePath))
                throw new ArgumentException("Effect resource identities contain a duplicate or cross-column alias.", nameof(bindings));
            pathBytes += row.OriginUtf8.Length + row.MaterializedUtf8.Length;
            if (pathBytes > 16 * 1024 * 1024) throw new ArgumentException("Effect resource paths exceed the 16 MiB copied-path budget.", nameof(bindings));
            origins.Add(row.OriginAbsolutePath);
            localPaths.Add(row.MaterializedAbsolutePath);
            originToLocal.Add(row.OriginAbsolutePath, row.MaterializedAbsolutePath);
            localToOrigin.Add(row.MaterializedAbsolutePath, row.OriginAbsolutePath);
            rows.Add(row);
        }
        Bindings = new ReadOnlyCollection<D3D9EffectResourceBinding>(rows);
        Utf8Bytes = pathBytes;
    }

    public IReadOnlyList<D3D9EffectResourceBinding> Bindings { get; }

    /// <summary>
    /// Mirrors native effect_resource_map.hpp: reverse the mapped local base to its original identity,
    /// resolve relative requests there, then return only a frozen local identity. This is lexical only;
    /// a missing mapping never opens or falls back to an original file.
    /// </summary>
    public string Resolve(string materializedBaseFile, string request)
    {
        string baseFile = D3D9EffectResourcePaths.Canonicalize(materializedBaseFile, nameof(materializedBaseFile));
        if (!localToOrigin.TryGetValue(baseFile, out string? origin))
            throw new ArgumentException("The effect resource base file is not materialized in this map.", nameof(materializedBaseFile));
        ArgumentException.ThrowIfNullOrEmpty(request);
        bool absoluteDrive = request.Length >= 3 && char.IsAsciiLetter(request[0]) && request[1] == ':' && request[2] is '\\' or '/';
        if (!absoluteDrive && (request[0] is '\\' or '/' || request.Contains(':')))
            throw new ArgumentException("A relative resource request has no unambiguous package origin.", nameof(request));
        string absolute = D3D9EffectResourcePaths.Canonicalize(absoluteDrive ? request :
            origin[..(origin.LastIndexOf('\\') + 1)] + request, nameof(request));
        if (originToLocal.TryGetValue(absolute, out string? local)) return local;
        if (localToOrigin.ContainsKey(absolute)) return absolute;
        throw new ArgumentException("The requested effect resource is absent from the frozen package map.", nameof(request));
    }

    internal long Utf8Bytes { get; }
    internal bool ContainsMaterializedPath(string path) => localPaths.Contains(D3D9EffectResourcePaths.Canonicalize(path, nameof(path)));
}

internal static class D3D9EffectResourcePaths
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static string Canonicalize(string path, string parameter)
    {
        ArgumentNullException.ThrowIfNull(path, parameter);
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))
            throw new ArgumentException("An absolute Windows drive path is required.", parameter);
        if (path[^1] is '\\' or '/')
            throw new ArgumentException("An effect resource path must identify a file, not a directory path.", parameter);
        int bytes;
        try { bytes = Utf8.GetByteCount(path); }
        catch (EncoderFallbackException error) { throw new ArgumentException("Effect resource paths must be valid Unicode for strict UTF-8 encoding.", parameter, error); }
        if (bytes > 32767) throw new ArgumentException("An effect resource path exceeds 32767 UTF-8 bytes.", parameter);
        for (var i = 2; i < path.Length; i++)
        {
            if (path[i] < 32 || path[i] is ':' or '<' or '>' or '"' or '|' or '?' or '*')
                throw new ArgumentException("An effect resource path contains an invalid Windows path character.", parameter);
        }
        var segments = new List<string>();
        foreach (var segment in path[3..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) throw new ArgumentException("An effect resource path traverses above the drive root.", parameter);
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
                throw new ArgumentException("Trailing dots or spaces are not supported in effect resource paths.", parameter);
            var device = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
                device[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')
                throw new ArgumentException("Reserved Windows device names are not supported in effect resource paths.", parameter);
            segments.Add(segment);
        }
        if (segments.Count == 0)
            throw new ArgumentException("An effect resource path must identify a file below the drive root.", parameter);
        return char.ToUpperInvariant(path[0]) + ":\\" + string.Join('\\', segments);
    }
}
