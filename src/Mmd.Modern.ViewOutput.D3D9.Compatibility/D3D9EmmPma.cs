using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Application;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>Caller-supplied original bytes and their lexical source identity. Construction never reads the path.</summary>
public sealed class D3D9EmmPmaAsset
{
    private readonly byte[] sourceBytes;
    public string OriginalAbsolutePath { get; }

    public D3D9EmmPmaAsset(string originalAbsolutePath, ReadOnlyMemory<byte> sourceBytes)
    {
        _ = D3D9EmmPma.AbsoluteFileIdentity(originalAbsolutePath);
        OriginalAbsolutePath = originalAbsolutePath;
        this.sourceBytes = sourceBytes.ToArray();
    }

    public byte[] CopySourceBytes() => (byte[])sourceBytes.Clone();
    internal ReadOnlyMemory<byte> SourceBytes => sourceBytes;
}

/// <summary>Detached EMM data and explicit dependencies; this state does not discover or execute effects.</summary>
public sealed class D3D9EmmPmaState
{
    public EmmImportPlan ImportPlan { get; }
    public EmmDocument Document => ImportPlan.Document;
    public IReadOnlyList<D3D9EmmPmaAsset> Assets { get; }
    internal ReadOnlyMemory<byte> SourceBytes { get; }

    internal D3D9EmmPmaState(EmmImportPlan plan, D3D9EmmPmaAsset[] assets)
    {
        ImportPlan = plan;
        Assets = Array.AsReadOnly(assets);
        SourceBytes = plan.Document.CopySourceBytes();
    }
}

/// <summary>Traditional-engine-owned EMM schema over the core's opaque archive transport. The archive's
/// top-level format and file extension are independent of this adapter. No original file, FX or GPU is opened.</summary>
public static class D3D9EmmPma
{
    public const string ExtensionName = "org.mda.traditional-emm";
    public const int MaxMetadataBytes = 16 * 1024 * 1024;
    public const int MaxDescriptors = 100_000;
    private const string ResourceRoot = "render-resources/traditional/emm/";
    private const string SourcePath = ResourceRoot + "raw.emm";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };

    /// <summary>Freezes a complete immutable plan and an explicit dependency list. Missing dependencies
    /// are not searched for, and captured paths remain metadata rather than archive extraction paths.</summary>
    public static D3D9EmmPmaState Capture(EmmImportPlan plan, IEnumerable<D3D9EmmPmaAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(assets);
        var selected = assets.Take(MaxDescriptors + 1).ToArray();
        ValidateDescriptorCount(plan.LiveObjects.Count, plan.Matches.Count, selected.Length);
        if (plan.StableMatchProvenance is { } stable &&
            (long)plan.LiveObjects.Count + plan.Matches.Count + selected.Length + stable.Count + 1 > MaxDescriptors)
            throw new InvalidDataException("Portable stable EMM provenance exceeds its descriptor budget.");
        if (plan.Document.Rows.Count > MaxDescriptors)
            throw new InvalidDataException("EMM scanner rows exceed the portable data budget.");
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in selected)
        {
            ArgumentNullException.ThrowIfNull(asset);
            if (!identities.Add(AbsoluteFileIdentity(asset.OriginalAbsolutePath)))
                throw new ArgumentException("EMM assets must have distinct absolute lexical source paths.", nameof(assets));
        }
        return new(plan, selected);
    }

    /// <summary>Returns save-only metadata and ordinal blobs, preserving other caller options and the
    /// neutral store's retained resources. It does not change the session, seek animation or access originals.</summary>
    public static PmaSaveOptions CreateSaveOptions(SceneProjectSession project, D3D9EmmPmaState state,
        PmaSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(state);
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.OptionalExtensions);
        ArgumentNullException.ThrowIfNull(options.AdditionalResources);
        ArgumentNullException.ThrowIfNull(options.ValidationOptions);
        if (options.OptionalExtensions.Keys.Any(key => key.Equals(ExtensionName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Save options already contain the EMM extension.", nameof(options));
        if (options.ContentSelection?.ExcludesExtension(ExtensionName) == true)
            throw new InvalidDataException("Save content selection excludes the EMM extension.");
        var resources = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in options.AdditionalResources)
            if (!resources.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException("Save options contain duplicate resource paths.", nameof(options));

        var source = Describe(SourcePath, state.SourceBytes);
        var assets = state.Assets.Select((asset, ordinal) => new AssetData(asset.OriginalAbsolutePath,
            Describe(AssetPath(ordinal), asset.SourceBytes))).ToArray();
        var plan = state.ImportPlan;
        var metadata = new Metadata(plan.StableMatchProvenance is null ? 1 : 2, plan.Document.AnsiCodePage, plan.PathContext.OriginalEmmPath,
            plan.PathContext.OriginalWorkingDirectory, source,
            plan.LiveObjects.Select(value => new BindingData(value.ObjectId, value.IsModel, value.SourcePath, value.RenderOrder)).ToArray(),
            plan.Matches.Select(value => new MatchData(value.RecordKey, value.ObjectId, value.Reason)).ToArray(), assets,
            plan.StableMatchProvenance is null ? null : "stable-guid-object-edit",
            plan.StableMatchProvenance?.ToArray());
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(metadata, Json);
        if (encoded.Length > MaxMetadataBytes) throw new InvalidDataException("Portable EMM metadata exceeds its byte budget.");
        var lengths = resources.ToDictionary(pair => pair.Key, pair => (long)pair.Value.Length, StringComparer.OrdinalIgnoreCase);
        Claim(source);
        foreach (var asset in assets) Claim(asset.Resource);
        PmaProjectStore.ValidateAdditionalResourceBudget(project, lengths,
            options.ValidationOptions with { ContentSelection = options.ContentSelection });

        // Resource budgets and all collisions have passed before the save-only byte copies are allocated.
        var frozen = resources.Where(pair => options.ContentSelection?.ExcludesResource(pair.Key) != true)
            .ToDictionary(pair => pair.Key, pair => (ReadOnlyMemory<byte>)pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        frozen.Add(SourcePath, state.SourceBytes.ToArray());
        for (int ordinal = 0; ordinal < state.Assets.Count; ordinal++)
            frozen.Add(AssetPath(ordinal), state.Assets[ordinal].CopySourceBytes());
        var extensions = options.OptionalExtensions.Where(pair => options.ContentSelection?.ExcludesExtension(pair.Key) != true)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        using var document = JsonDocument.Parse(encoded);
        extensions.Add(ExtensionName, document.RootElement.Clone());
        return options with
        {
            AdditionalResources = new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(frozen),
            OptionalExtensions = new ReadOnlyDictionary<string, JsonElement>(extensions)
        };

        void Claim(ResourceData resource)
        {
            if (options.ContentSelection?.ExcludesResource(resource.Path) == true)
                throw new InvalidDataException("Save content selection excludes an EMM resource.");
            if (!lengths.TryAdd(resource.Path, resource.ByteLength))
                throw new ArgumentException("Save options already contain resources owned by the EMM extension: " + resource.Path, nameof(options));
        }
    }

    /// <summary>Explicitly interprets this traditional schema after the neutral store has validated
    /// the archive. Raw EMM is reparsed and matching recomputed; stored matches must agree exactly.</summary>
    public static D3D9EmmPmaState? Read(PmaLoadedProject project)
        => ReadPackage(project);

    /// <summary>Interprets the same schema through neutral read-only package access, including during
    /// preflight before host models or physics exist. Does not access originals or execute effects.</summary>
    public static D3D9EmmPmaState? ReadPackage(IPmaPackageResources project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!project.OptionalExtensions.TryGetValue(ExtensionName, out var element)) return null;
        try
        {
            string raw = element.GetRawText();
            if (Encoding.UTF8.GetByteCount(raw) > MaxMetadataBytes)
                throw new InvalidDataException("Portable EMM metadata exceeds its byte budget.");
            var metadata = JsonSerializer.Deserialize<Metadata>(raw, Json)
                ?? throw new InvalidDataException("Missing portable EMM metadata.");
            if (metadata.SchemaVersion is not (1 or 2) || metadata.AnsiCodePage is not (932 or 936)
                || metadata.Source is null || metadata.Bindings is null || metadata.Matches is null || metadata.Assets is null)
                throw new InvalidDataException("Unsupported or incomplete portable EMM metadata.");
            if (metadata.SchemaVersion == 1 && (metadata.BindingPolicy is not null || metadata.StableMatches is not null) ||
                metadata.SchemaVersion == 2 && (metadata.BindingPolicy != "stable-guid-object-edit" || metadata.StableMatches is null ||
                    metadata.StableMatches.Length != metadata.Matches.Length))
                throw new InvalidDataException("Portable EMM stable bindings require their explicit policy and complete original matching provenance.");
            ValidateDescriptorCount(metadata.Bindings.Length, metadata.Matches.Length, metadata.Assets.Length);
            if (metadata.StableMatches is { } stable && (long)metadata.Bindings.Length + metadata.Matches.Length + metadata.Assets.Length + stable.Length + 1 > MaxDescriptors)
                throw new InvalidDataException("Portable stable EMM provenance exceeds its descriptor budget.");
            ValidateResource(metadata.Source, SourcePath);
            _ = AbsoluteFileIdentity(metadata.OriginalEmmPath);
            if (string.IsNullOrWhiteSpace(metadata.OriginalWorkingDirectory) || metadata.OriginalWorkingDirectory.Contains('\0')
                || !Path.IsPathFullyQualified(metadata.OriginalWorkingDirectory))
                throw new InvalidDataException("Portable EMM requires an absolute original working directory.");
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int ordinal = 0; ordinal < metadata.Assets.Length; ordinal++)
            {
                var asset = metadata.Assets[ordinal] ?? throw new InvalidDataException("Missing EMM asset descriptor.");
                ValidateResource(asset.Resource, AssetPath(ordinal));
                if (!identities.Add(AbsoluteFileIdentity(asset.OriginalAbsolutePath)))
                    throw new InvalidDataException("Portable EMM asset paths must be distinct.");
            }
            var bindings = metadata.Bindings.Select(value => value is null
                ? throw new InvalidDataException("Missing EMM object binding.")
                : new EmmObjectBinding(value.ObjectId, value.IsModel, value.SourcePath, value.RenderOrder)).ToArray();
            foreach (var match in metadata.Matches)
                if (match is null || string.IsNullOrWhiteSpace(match.RecordKey) || match.ObjectId == Guid.Empty || !Enum.IsDefined(match.Reason))
                    throw new InvalidDataException("Invalid portable EMM object match.");

            byte[] source = ReadResource(metadata.Source);
            var document = EmmReader.Read(source, new()
            {
                AnsiCodePage = metadata.AnsiCodePage,
                MaxInputBytes = Math.Max(1, source.Length),
                MaxRows = MaxDescriptors
            });
            var plan = metadata.SchemaVersion == 1
                ? EmmImportPlan.Create(document, metadata.OriginalEmmPath, bindings, metadata.OriginalWorkingDirectory)
                : EmmImportPlan.RestoreStableBindings(document, metadata.OriginalEmmPath, bindings,
                    metadata.OriginalWorkingDirectory, metadata.StableMatches!);
            if (!plan.LiveObjects.Select(value => new BindingData(value.ObjectId, value.IsModel, value.SourcePath, value.RenderOrder))
                    .SequenceEqual(metadata.Bindings)
                || !plan.Matches.SequenceEqual(metadata.Matches.Select(value => new EmmObjectMatch(value.RecordKey, value.ObjectId, value.Reason))))
                throw new InvalidDataException("Stored EMM matches disagree with the preserved document and binding inputs.");
            var assets = metadata.Assets.Select(value => new D3D9EmmPmaAsset(value.OriginalAbsolutePath, ReadResource(value.Resource))).ToArray();
            return Capture(plan, assets);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException or PathTooLongException
            or FileNotFoundException or EmmFormatException)
        {
            throw new InvalidDataException("Invalid portable EMM data.", error);
        }

        byte[] ReadResource(ResourceData resource)
        {
            byte[] bytes = project.ReadResource(resource.Path, resource.ByteLength);
            if (bytes.LongLength != resource.ByteLength || Convert.ToHexStringLower(SHA256.HashData(bytes)) != resource.Sha256)
                throw new InvalidDataException("Portable EMM resource does not match its byte length or SHA256.");
            return bytes;
        }
    }

    internal static string AbsoluteFileIdentity(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path)
            || Path.EndsInDirectorySeparator(path) || Path.GetFileName(path) is "." or "..")
            throw new ArgumentException("An absolute original file path is required.", nameof(path));
        string absolute = Path.GetFullPath(path);
        if (Path.GetFileName(absolute).Length == 0)
            throw new ArgumentException("An absolute original file path is required.", nameof(path));
        return absolute;
    }

    private static string AssetPath(int ordinal) => ResourceRoot + "asset" + ordinal.ToString("D5", CultureInfo.InvariantCulture) + ".bin";
    private static ResourceData Describe(string path, ReadOnlyMemory<byte> bytes)
        => new(path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes.Span)));
    private static void ValidateDescriptorCount(int bindings, int matches, int assets)
    {
        if ((long)bindings + matches + assets + 1 > MaxDescriptors)
            throw new InvalidDataException("Portable EMM descriptors exceed their count budget.");
    }
    private static void ValidateResource(ResourceData? resource, string expectedPath)
    {
        if (resource is null || resource.Path != expectedPath || resource.ByteLength < 0 || resource.ByteLength > Array.MaxLength
            || resource.Sha256 is null || resource.Sha256.Length != 64
            || resource.Sha256.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("Invalid portable EMM resource descriptor.");
    }

    private sealed record Metadata(int SchemaVersion, int AnsiCodePage, string OriginalEmmPath, string OriginalWorkingDirectory,
        ResourceData Source, BindingData[] Bindings, MatchData[] Matches, AssetData[] Assets,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BindingPolicy = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EmmStableMatchProvenance[]? StableMatches = null);
    private sealed record ResourceData(string Path, long ByteLength, string Sha256);
    private sealed record AssetData(string OriginalAbsolutePath, ResourceData Resource);
    private sealed record BindingData(Guid ObjectId, bool IsModel, string SourcePath, int RenderOrder);
    private sealed record MatchData(string RecordKey, Guid ObjectId, EmmObjectMatchReason Reason);
}
