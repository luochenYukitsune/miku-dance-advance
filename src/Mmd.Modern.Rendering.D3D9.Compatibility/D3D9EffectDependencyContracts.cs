namespace Mmd.Modern.Rendering.D3D9.Compatibility;

public enum D3D9EffectDependencyStatus { Complete, Incomplete, Unsupported, Cancelled }
public enum D3D9EffectDependencyRequestKind { Root, IncludeLocal, IncludeSystem, ResourceName, DefaultEffect }
public enum D3D9EffectDependencyReferenceKind { Resource, Effect, TextureFunction, Symbol }
public enum D3D9EffectDependencySymbol { None, Hide, MainDefault }
public enum D3D9EffectDependencyEdgeKind { Root, IncludeLocal, IncludeSystem, ResourceName, DefaultEffect, TextureFunction, Symbol }
[Flags]
public enum D3D9EffectDependencyCategory { Effect = 1, Include = 2, Resource = 4 }

/// <summary>Original request bytes and their actual code page; these are never reconstructed from a filename.</summary>
public sealed class D3D9EffectDependencyReference
{
    private readonly byte[] bytes;
    public D3D9EffectDependencyReference(ReadOnlySpan<byte> bytes, int codePage)
    {
        if (codePage <= 0) throw new ArgumentOutOfRangeException(nameof(codePage));
        this.bytes = bytes.ToArray(); CodePage = codePage;
    }
    public int CodePage { get; }
    public int ByteLength => bytes.Length;
    public byte[] CopyBytes() => (byte[])bytes.Clone();
}

/// <summary>One atomic, stable read of an original file. Origin identity is independent of the content digest.</summary>
public sealed class D3D9EffectDependencySnapshot
{
    private readonly byte[] bytes;
    public D3D9EffectDependencySnapshot(string originAbsolutePath, ReadOnlySpan<byte> bytes)
    {
        OriginAbsolutePath = CanonicalizeOrigin(originAbsolutePath);
        this.bytes = bytes.ToArray();
        Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(this.bytes)).ToLowerInvariant();
    }
    public string OriginAbsolutePath { get; }
    public long ByteLength => bytes.LongLength;
    public string Sha256 { get; }
    public byte[] CopyBytes() => (byte[])bytes.Clone();
    public static string CanonicalizeOrigin(string path) => D3D9EffectResourcePaths.Canonicalize(path, nameof(path));
}

/// <param name="BaseOriginAbsolutePath">Null only for an absolute Root request; otherwise the base is a file, not a directory.</param>
public sealed record D3D9EffectDependencySourceRequest(string? BaseOriginAbsolutePath,
    D3D9EffectDependencyReference Reference, D3D9EffectDependencyRequestKind Kind);

/// <summary>
/// Discovery-only original source access. Must resolve strictly, atomically snapshot and reject changed files,
/// reparse points and encoding/path ambiguity. This is not a package runtime fallback resolver.
/// </summary>
public interface ID3D9EffectDependencySource
{
    D3D9EffectDependencySnapshot Capture(D3D9EffectDependencySourceRequest request, CancellationToken cancellationToken);
}

public sealed class D3D9EffectDependencyException : Exception
{
    public D3D9EffectDependencyException(D3D9EffectDependencyStatus status, string code, string message,
        string? candidateOriginAbsolutePath = null, Exception? innerException = null) : base(message, innerException)
    {
        if (status is not (D3D9EffectDependencyStatus.Incomplete or D3D9EffectDependencyStatus.Unsupported))
            throw new ArgumentOutOfRangeException(nameof(status));
        Status = status; Code = code; CandidateOriginAbsolutePath = candidateOriginAbsolutePath;
    }
    public D3D9EffectDependencyStatus Status { get; }
    public string Code { get; }
    public string? CandidateOriginAbsolutePath { get; }
}

/// <summary>Caller-frozen project/EMM provenance. Hidden objects and every legal binding must be supplied by the caller.</summary>
public sealed record D3D9EffectDependencyProvenance(Guid? OwnerId = null, int? Subset = null,
    string? EmmSection = null, int? EmmRow = null, string? Description = null, ulong? NativeObjectId = null);

public sealed class D3D9EffectDependencyRoot
{
    public D3D9EffectDependencyRoot(string originAbsolutePath, D3D9EffectDependencyProvenance? provenance = null)
    { OriginAbsolutePath = D3D9EffectDependencySnapshot.CanonicalizeOrigin(originAbsolutePath); Provenance = provenance ?? new(); }
    public string OriginAbsolutePath { get; }
    public D3D9EffectDependencyProvenance Provenance { get; }
}

public sealed record D3D9EffectDependencyMacro(string Name, string Value);

/// <summary>Actual compiler invocation, including empty macro values and empty defines for generated texture functions.</summary>
public sealed class D3D9EffectDependencyProfile
{
    public D3D9EffectDependencyProfile(string id, uint flags, IEnumerable<D3D9EffectDependencyMacro> macros,
        string? entryPoint = null, string? target = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id); ArgumentNullException.ThrowIfNull(macros);
        Id = id; Flags = flags; Macros = Array.AsReadOnly(macros.ToArray()); EntryPoint = entryPoint; Target = target;
    }
    public string Id { get; }
    public uint Flags { get; }
    public IReadOnlyList<D3D9EffectDependencyMacro> Macros { get; }
    public string? EntryPoint { get; }
    public string? Target { get; }
}

public sealed record D3D9EffectDependencyCompilerIdentity(string HostSemanticsVersion, string CompilerSha256,
    int ProcessBitness, int AnsiCodePage, string? ShaderCompilerSha256 = null);

/// <summary>Depth counts include opens below the top FX, starting at one. Parent is null only for a root-relative open.</summary>
public sealed record D3D9EffectDependencyIncludeRequest(string ProfileId, bool IsSystem,
    string? ParentOriginAbsolutePath, int Depth, D3D9EffectDependencyReference Reference);

/// <summary>
/// An event already classified by the shared native host semantic helper. The managed collector does not
/// interpret annotation strings or parse DefaultEffect rows. Row bytes retain the original token before trim.
/// </summary>
public sealed record D3D9EffectDependencySemanticReference(string ProfileId,
    D3D9EffectDependencyReferenceKind Kind, D3D9EffectDependencyReference Reference,
    string Parameter, string Annotation, int? RowOrdinal = null, string? RowKey = null,
    D3D9EffectDependencyReference? RawRow = null, D3D9EffectDependencySymbol? Symbol = null,
    string? FunctionEntryPoint = null, string? FunctionTarget = null);

/// <summary>Compiler/reflection outcome for one FX: both fixed main profiles and all required empty-macro function profiles.</summary>
public sealed class D3D9EffectDependencyInspection
{
    public D3D9EffectDependencyInspection(D3D9EffectDependencyStatus status,
        D3D9EffectDependencyCompilerIdentity identity, IEnumerable<D3D9EffectDependencyProfile> profiles,
        IEnumerable<D3D9EffectDependencySemanticReference> references,
        IEnumerable<D3D9EffectDependencyInspectorDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(profiles); ArgumentNullException.ThrowIfNull(references);
        Status = status; Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Profiles = Array.AsReadOnly(profiles.ToArray()); References = Array.AsReadOnly(references.ToArray());
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
    }
    public D3D9EffectDependencyStatus Status { get; }
    public D3D9EffectDependencyCompilerIdentity Identity { get; }
    public IReadOnlyList<D3D9EffectDependencyProfile> Profiles { get; }
    public IReadOnlyList<D3D9EffectDependencySemanticReference> References { get; }
    public IReadOnlyList<D3D9EffectDependencyInspectorDiagnostic> Diagnostics { get; }
}

public sealed record D3D9EffectDependencyInspectorDiagnostic(string Code, string Message, string? ProfileId = null,
    string? Parameter = null, string? Annotation = null, D3D9EffectDependencyReference? RawMessage = null, int? HResult = null);

/// <summary>
/// Uses the official compiler and the shared host semantic reader; never creates a device or scans a tree.
/// Includes must be obtained exclusively through captureInclude. It must propagate callback failures,
/// honor cancellation, and report all executed profiles, including the Function compile with no defines.
/// </summary>
public interface ID3D9EffectDependencyInspector
{
    D3D9EffectDependencyInspection Inspect(D3D9EffectDependencySnapshot topFx,
        Func<D3D9EffectDependencyIncludeRequest, D3D9EffectDependencySnapshot> captureInclude,
        CancellationToken cancellationToken);
}

public sealed record D3D9EffectDependencyLimits(int MaximumFiles = 65536, int MaximumEdges = 262144,
    int MaximumFxInspections = 4096, int MaximumIncludeDepth = 64, int MaximumEffectDepth = 64,
    long MaximumFileBytes = 256L * 1024 * 1024, long MaximumTotalBytes = 512L * 1024 * 1024,
    long MaximumReferenceBytes = 16L * 1024 * 1024);

public sealed record D3D9EffectDependencyNode(string ResourceId, D3D9EffectDependencySnapshot Snapshot,
    D3D9EffectDependencyCategory Category);
public sealed record D3D9EffectDependencyEdge(int RootIndex, string TopFxOriginAbsolutePath,
    string ParentOriginAbsolutePath, string ProfileId, D3D9EffectDependencyEdgeKind Kind,
    D3D9EffectDependencyReference Reference, string? TargetResourceId = null,
    D3D9EffectDependencySymbol? Symbol = null, string? Parameter = null, string? Annotation = null,
    int? RowOrdinal = null, string? RowKey = null, D3D9EffectDependencyReference? RawRow = null,
    string? FunctionEntryPoint = null, string? FunctionTarget = null);

public sealed record D3D9EffectDependencyDiagnostic(D3D9EffectDependencyStatus Status, string Code, string Message,
    int? RootIndex = null, string? TopFxOriginAbsolutePath = null, string? ParentOriginAbsolutePath = null,
    string? ProfileId = null, D3D9EffectDependencyReference? Reference = null, string? CandidateOriginAbsolutePath = null,
    string? Parameter = null, string? Annotation = null, D3D9EffectDependencyReference? RawMessage = null, int? HResult = null);

/// <summary>A complete discovery result only. A failed/cancelled collection never exposes a partial closure.</summary>
public sealed class D3D9EffectDependencyClosure
{
    internal D3D9EffectDependencyClosure(IReadOnlyList<D3D9EffectDependencyRoot> roots,
        IEnumerable<D3D9EffectDependencyNode> nodes, IEnumerable<D3D9EffectDependencyEdge> edges,
        D3D9EffectDependencyCompilerIdentity? identity, IEnumerable<D3D9EffectDependencyProfile> profiles)
    {
        Roots = Array.AsReadOnly(roots.ToArray()); Nodes = Array.AsReadOnly(nodes.ToArray());
        Edges = Array.AsReadOnly(edges.ToArray()); CompilerIdentity = identity; Profiles = Array.AsReadOnly(profiles.ToArray());
    }
    public IReadOnlyList<D3D9EffectDependencyRoot> Roots { get; }
    public IReadOnlyList<D3D9EffectDependencyNode> Nodes { get; }
    public IReadOnlyList<D3D9EffectDependencyEdge> Edges { get; }
    public D3D9EffectDependencyCompilerIdentity? CompilerIdentity { get; }
    public IReadOnlyList<D3D9EffectDependencyProfile> Profiles { get; }
}

public sealed class D3D9EffectDependencyResult
{
    internal D3D9EffectDependencyResult(D3D9EffectDependencyStatus status, D3D9EffectDependencyClosure? closure,
        IEnumerable<D3D9EffectDependencyDiagnostic> diagnostics)
    { Status = status; Closure = closure; Diagnostics = Array.AsReadOnly(diagnostics.ToArray()); }
    public D3D9EffectDependencyStatus Status { get; }
    public D3D9EffectDependencyClosure? Closure { get; }
    public IReadOnlyList<D3D9EffectDependencyDiagnostic> Diagnostics { get; }
}
