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
        IEnumerable<D3D9EffectDependencyInspectorDiagnostic>? diagnostics = null,
        IEnumerable<D3D9EffectOffscreenDeclaration>? offscreenTargets = null)
    {
        ArgumentNullException.ThrowIfNull(profiles); ArgumentNullException.ThrowIfNull(references);
        Status = status; Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Profiles = Array.AsReadOnly(profiles.ToArray()); References = Array.AsReadOnly(references.ToArray());
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
        OffscreenTargets = Array.AsReadOnly((offscreenTargets ?? []).ToArray());
    }
    public D3D9EffectDependencyStatus Status { get; }
    public D3D9EffectDependencyCompilerIdentity Identity { get; }
    public IReadOnlyList<D3D9EffectDependencyProfile> Profiles { get; }
    public IReadOnlyList<D3D9EffectDependencySemanticReference> References { get; }
    public IReadOnlyList<D3D9EffectDependencyInspectorDiagnostic> Diagnostics { get; }
    /// <summary>宿主会为其建立离屏资源的顶层 OFFSCREENRENDERTARGET 参数（按编译 profile 分别列出）。
    /// 只是声明信息，不参与依赖闭包；EMM 命名分配页以参数名作为基名。</summary>
    public IReadOnlyList<D3D9EffectOffscreenDeclaration> OffscreenTargets { get; }

    /// <summary>两个主 profile 声明的离屏目标参数名（按序数排序，与宿主写出 EMM 时 std::map 的顺序一致）。
    /// 两个 profile 不一致时宿主选哪个取决于设备的 mip 能力，此时不猜，直接报不支持。</summary>
    public IReadOnlyList<string> MainOffscreenTargetNames() => MainOffscreenTargetNames(OffscreenTargets);

    /// <summary>同上，作用于已记录的原始声明。只在真正需要为某个 FX 生成命名页时调用：
    /// 依赖收集本身不做这项检查，以免与离屏页无关的工程（含冻结重放）因此无法载入。</summary>
    public static IReadOnlyList<string> MainOffscreenTargetNames(IEnumerable<D3D9EffectOffscreenDeclaration> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        var all = declarations.ToArray();
        string[] Names(string profile) => all.Where(value => value.ProfileId == profile)
            .Select(value => value.Parameter).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var off = Names("effect-mip-off");
        if (!off.SequenceEqual(Names("effect-mip-on"), StringComparer.Ordinal))
            throw new D3D9EffectDependencyException(D3D9EffectDependencyStatus.Unsupported, "offscreen-profile-mismatch",
                "The mip-off and mip-on profiles declare different OFFSCREENRENDERTARGET parameters.");
        return off;
    }
}

/// <summary>一个顶层 OFFSCREENRENDERTARGET 参数声明。</summary>
public sealed record D3D9EffectOffscreenDeclaration(string ProfileId, string Parameter);

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
        D3D9EffectDependencyCompilerIdentity? identity, IEnumerable<D3D9EffectDependencyProfile> profiles,
        IReadOnlyDictionary<string, IReadOnlyList<D3D9EffectOffscreenDeclaration>>? offscreenTargets = null)
    {
        Roots = Array.AsReadOnly(roots.ToArray()); Nodes = Array.AsReadOnly(nodes.ToArray());
        Edges = Array.AsReadOnly(edges.ToArray()); CompilerIdentity = identity; Profiles = Array.AsReadOnly(profiles.ToArray());
        OffscreenTargets = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<D3D9EffectOffscreenDeclaration>>(
            new Dictionary<string, IReadOnlyList<D3D9EffectOffscreenDeclaration>>(
                offscreenTargets ?? new Dictionary<string, IReadOnlyList<D3D9EffectOffscreenDeclaration>>(), StringComparer.OrdinalIgnoreCase));
    }
    /// <summary>本次收集中检查过的每个 FX（原件绝对路径）按 profile 记录的原始离屏声明。
    /// 两个主 profile 是否一致不在收集时检查，见 <see cref="D3D9EffectDependencyInspection.MainOffscreenTargetNames(IEnumerable{D3D9EffectOffscreenDeclaration})"/>。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<D3D9EffectOffscreenDeclaration>> OffscreenTargets { get; }
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
