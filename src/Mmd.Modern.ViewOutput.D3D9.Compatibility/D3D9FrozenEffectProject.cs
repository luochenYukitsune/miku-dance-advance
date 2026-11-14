using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Application;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>
/// Immutable renderer-owned effect decisions for one frozen import plan. In particular, an absent implicit
/// effect is a persisted decision for that live Guid, rather than permission to search the original disk.
/// Resource bytes remain owned by the existing schema-three effect tree.
/// </summary>
public sealed class D3D9FrozenEffectProjectState
{
    internal D3D9FrozenEffectProjectState(D3D9EffectProjectRoots projectRoots, D3D9EffectResourceTree? effectTree)
    { ProjectRoots = projectRoots; EffectTree = effectTree; }

    public D3D9EffectProjectRoots ProjectRoots { get; }
    public EmmImportPlan ImportPlan => ProjectRoots.ImportPlan;
    public IReadOnlyList<D3D9EmmRuntimeRootSeed> RootSeeds => ProjectRoots.RootSeeds;
    public IReadOnlyList<D3D9EffectDependencyRoot> Roots => ProjectRoots.Roots;
    public IReadOnlyList<D3D9EffectProjectRootOrigin> Origins => ProjectRoots.Origins;
    /// <summary>Null only when the project has no required file roots.</summary>
    public D3D9EffectResourceTree? EffectTree { get; }
}

/// <summary>
/// Metadata-only persistence of the actual root planner's complete result over neutral opaque transport.
/// Capture/save/read neither discover implicit effects nor open source files, materialize assets, create
/// devices or construct solvers. Restoration reuses the planner with only the preserved decisions, then
/// checks its exact output against the restored EMM plan and completed dependency graph.
/// </summary>
public static class D3D9FrozenEffectProject
{
    public const string ExtensionName = "org.mda.traditional-effect-project";
    public const int MaxMetadataBytes = 16 * 1024 * 1024;
    /// <summary>Combined bindings, seeds, roots and origin descriptors.</summary>
    public const int MaxDescriptors = 262_144;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };

    /// <summary>Accepts only an actual immutable planner result and its matching completed schema-three
    /// graph. A project without file roots has a null tree; it still preserves every no-effect seed.</summary>
    public static D3D9FrozenEffectProjectState Capture(D3D9EffectProjectRoots projectRoots,
        D3D9EffectResourceTree? effectTree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectRoots); cancellationToken.ThrowIfCancellationRequested();
        ValidateCounts(projectRoots.ImportPlan.LiveObjects.Count, projectRoots.RootSeeds.Count, projectRoots.Roots.Count, projectRoots.Origins.Count);
        var metadata = Describe(projectRoots);
        ValidateShape(metadata, cancellationToken);
        ValidateTree(projectRoots, effectTree, cancellationToken);
        _ = Encode(metadata);
        cancellationToken.ThrowIfCancellationRequested();
        return new(projectRoots, effectTree);
    }

    /// <summary>Adds only this extension's metadata, retaining caller options and neutral retained data.
    /// The EMM and effect-tree codecs remain responsible for their own original bytes; no FX asset is
    /// copied into a second namespace. Existing retained metadata may be replaced by an explicit re-save.</summary>
    public static PmaSaveOptions CreateSaveOptions(SceneProjectSession project, D3D9FrozenEffectProjectState state,
        PmaSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(state);
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.OptionalExtensions); ArgumentNullException.ThrowIfNull(options.AdditionalResources);
        ArgumentNullException.ThrowIfNull(options.ValidationOptions); cancellationToken.ThrowIfCancellationRequested();
        if (options.OptionalExtensions.Keys.Any(key => key.Equals(ExtensionName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Save options already contain the frozen effect-project extension.", nameof(options));
        if (options.ContentSelection?.ExcludesExtension(ExtensionName) == true)
            throw new InvalidDataException("Save content selection excludes the frozen effect-project extension.");

        var metadata = Describe(state.ProjectRoots);
        ValidateShape(metadata, cancellationToken);
        ValidateTree(state.ProjectRoots, state.EffectTree, cancellationToken);
        byte[] encoded = Encode(metadata);
        var resources = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in options.AdditionalResources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!lengths.TryAdd(pair.Key, pair.Value.Length))
                throw new ArgumentException("Save resource paths collide.", nameof(options));
            if (options.ContentSelection?.ExcludesResource(pair.Key) != true) resources.Add(pair.Key, pair.Value);
        }
        // The neutral admission validates every logical key, selection and retained-resource budget.
        // This check does not touch bytes or read model sources.
        PmaProjectStore.ValidateAdditionalResourceBudget(project, lengths,
            options.ValidationOptions with { ContentSelection = options.ContentSelection });
        var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in options.OptionalExtensions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.ContentSelection?.ExcludesExtension(pair.Key) != true) extensions.Add(pair.Key, pair.Value.Clone());
        }
        using var parsed = JsonDocument.Parse(encoded); extensions.Add(ExtensionName, parsed.RootElement.Clone());
        cancellationToken.ThrowIfCancellationRequested();
        return options with
        {
            AdditionalResources = new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(resources),
            OptionalExtensions = new ReadOnlyDictionary<string, JsonElement>(extensions)
        };
    }

    public static D3D9FrozenEffectProjectState? Read(PmaLoadedProject project, D3D9EmmPmaState? emmState,
        D3D9EffectResourceTree? effectTree, CancellationToken cancellationToken = default)
        => ReadPackage(project, emmState, effectTree, cancellationToken);

    /// <summary>
    /// Cross-validates already restored EMM and effect-tree state during neutral package preflight, before
    /// any live model or solver exists. Legacy packages without this extension return null; they do not
    /// acquire invented implicit choices. Every implicit resolver result here comes from this metadata.
    /// </summary>
    public static D3D9FrozenEffectProjectState? ReadPackage(IPmaPackageResources project, D3D9EmmPmaState? emmState,
        D3D9EffectResourceTree? effectTree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project); cancellationToken.ThrowIfCancellationRequested();
        if (!project.OptionalExtensions.TryGetValue(ExtensionName, out var element)) return null;
        if (emmState is null) throw new InvalidDataException("Frozen effect-project metadata requires its restored EMM plan.");
        try
        {
            string raw = element.GetRawText();
            if (StrictUtf8.GetByteCount(raw) > MaxMetadataBytes)
                throw new InvalidDataException("Frozen effect-project metadata exceeds its byte budget.");
            var metadata = JsonSerializer.Deserialize<Metadata>(raw, Json)
                ?? throw new InvalidDataException("Missing frozen effect-project metadata.");
            ValidateShape(metadata, cancellationToken);
            var plan = emmState.ImportPlan;
            if (metadata.AnsiCodePage != plan.Document.AnsiCodePage || metadata.EmmSha256 != DocumentDigest(plan)
                || metadata.OriginalEmmPath != plan.PathContext.OriginalEmmPath
                || metadata.OriginalWorkingDirectory != plan.PathContext.OriginalWorkingDirectory
                || !metadata.Bindings.SequenceEqual(plan.LiveObjects.Select(Binding)))
                throw new InvalidDataException("Frozen effect-project inputs disagree with the restored EMM import plan.");

            var live = plan.LiveObjects.ToDictionary(value => value.ObjectId);
            var seeds = new Dictionary<Guid, SeedData>();
            foreach (var seed in metadata.RootSeeds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!live.TryGetValue(seed.ObjectId, out var binding) || seed.IsModel != binding.IsModel
                    || !seeds.TryAdd(seed.ObjectId, seed))
                    throw new InvalidDataException("Frozen effect seeds must identify distinct live objects of the preserved kind.");
            }
            if (seeds.Count != live.Count)
                throw new InvalidDataException("One frozen effect seed, including null, is required for every live object.");

            var implicitValues = new Dictionary<Guid, string>();
            foreach (var origin in metadata.Origins)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (origin.Kind != D3D9EffectProjectRootKind.Implicit) continue;
                if (origin.ObjectId is not { } id || !seeds.TryGetValue(id, out var seed)
                    || origin.IsModel != seed.IsModel || seed.OriginAbsoluteFxPath is null
                    || !implicitValues.TryAdd(id, origin.OriginalValue))
                    throw new InvalidDataException("Frozen implicit origins must identify exactly one non-null live seed.");
            }
            if (implicitValues.Count != seeds.Values.Count(seed => seed.OriginAbsoluteFxPath is not null))
                throw new InvalidDataException("A frozen non-null implicit seed is missing its original selection value.");

            // The selected original token can retain lexical components (for example '..') while its
            // seed is canonical. Reuse that token so restoration preserves both without path guessing.
            var restored = D3D9EffectProjectRoots.Create(plan, request =>
                implicitValues.GetValueOrDefault(request.Object.ObjectId), cancellationToken);
            var expected = Describe(restored);
            if (!metadata.RootSeeds.SequenceEqual(expected.RootSeeds) || !metadata.Roots.SequenceEqual(expected.Roots)
                || !metadata.Origins.SequenceEqual(expected.Origins))
                throw new InvalidDataException("Frozen effect roots, seeds or origins disagree with the preserved planner inputs.");
            ValidateTree(restored, effectTree, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(restored, effectTree);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("Invalid frozen effect-project metadata.", error);
        }
    }

    private static void ValidateTree(D3D9EffectProjectRoots projectRoots, D3D9EffectResourceTree? tree, CancellationToken token)
    {
        if (projectRoots.Roots.Count == 0)
        {
            if (tree is not null) throw new InvalidDataException("A root-free frozen effect project cannot claim an unrelated dependency tree.");
            return;
        }
        var graph = tree?.DependencyGraph
            ?? throw new InvalidDataException("Frozen effect roots require a complete schema-three dependency tree.");
        if (!projectRoots.Roots.Select(Root).SequenceEqual(graph.Roots.Select(Root)))
            throw new InvalidDataException("Frozen effect roots and provenance disagree with the completed dependency graph.");
        var fx = graph.Nodes.Where(node => (node.Category & D3D9EffectDependencyCategory.Effect) != 0)
            .ToDictionary(node => node.OriginAbsolutePath, StringComparer.OrdinalIgnoreCase);
        foreach (var root in projectRoots.Roots)
        {
            token.ThrowIfCancellationRequested();
            if (!fx.ContainsKey(root.OriginAbsolutePath))
                throw new InvalidDataException("A frozen effect root is absent from the graph's original FX identity map.");
        }
    }

    private static Metadata Describe(D3D9EffectProjectRoots roots) => new(1, roots.ImportPlan.Document.AnsiCodePage,
        DocumentDigest(roots.ImportPlan), roots.ImportPlan.PathContext.OriginalEmmPath,
        roots.ImportPlan.PathContext.OriginalWorkingDirectory, roots.ImportPlan.LiveObjects.Select(Binding).ToArray(),
        roots.RootSeeds.Select(seed => new SeedData(seed.ObjectId, seed.IsModel, seed.OriginAbsoluteFxPath)).ToArray(),
        roots.Roots.Select(Root).ToArray(), roots.Origins.Select(origin => new OriginData(origin.RootIndex, origin.Kind,
            origin.OriginalValue, origin.OriginAbsolutePath, origin.ObjectId, origin.IsModel, origin.RecordKey,
            origin.EmmSection, origin.Subset, origin.SectionOwner)).ToArray());
    private static BindingData Binding(EmmObjectBinding binding) => new(binding.ObjectId, binding.IsModel, binding.SourcePath, binding.RenderOrder);
    private static RootData Root(D3D9EffectDependencyRoot root) => new(root.OriginAbsolutePath, root.Provenance);
    private static string DocumentDigest(EmmImportPlan plan) => Convert.ToHexStringLower(SHA256.HashData(plan.Document.CopySourceBytes()));
    private static byte[] Encode(Metadata metadata)
    {
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(metadata, Json);
        if (encoded.Length > MaxMetadataBytes) throw new InvalidDataException("Frozen effect-project metadata exceeds its byte budget.");
        return encoded;
    }
    private static void ValidateShape(Metadata metadata, CancellationToken token)
    {
        if (metadata.SchemaVersion != 1 || metadata.AnsiCodePage is not (932 or 936)
            || metadata.EmmSha256 is null || metadata.EmmSha256.Length != 64
            || metadata.EmmSha256.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || metadata.Bindings is null || metadata.RootSeeds is null || metadata.Roots is null || metadata.Origins is null)
            throw new InvalidDataException("Unsupported, incomplete or over-budget frozen effect-project metadata.");
        ValidateCounts(metadata.Bindings.Length, metadata.RootSeeds.Length, metadata.Roots.Length, metadata.Origins.Length);
        Text(metadata.OriginalEmmPath); Text(metadata.OriginalWorkingDirectory);
        foreach (var binding in metadata.Bindings)
        {
            token.ThrowIfCancellationRequested();
            if (binding is null || binding.ObjectId == Guid.Empty) throw new InvalidDataException("Invalid frozen live binding.");
            Text(binding.SourcePath);
        }
        foreach (var seed in metadata.RootSeeds)
        {
            token.ThrowIfCancellationRequested();
            if (seed is null || seed.ObjectId == Guid.Empty) throw new InvalidDataException("Invalid frozen effect seed.");
            if (seed.OriginAbsoluteFxPath is { } path) Text(path);
        }
        foreach (var root in metadata.Roots)
        {
            token.ThrowIfCancellationRequested();
            if (root is null || root.Provenance is null) throw new InvalidDataException("Missing frozen root provenance.");
            Text(root.OriginAbsolutePath); OptionalText(root.Provenance.EmmSection); OptionalText(root.Provenance.Description);
        }
        for (int index = 0; index < metadata.Origins.Length; index++)
        {
            token.ThrowIfCancellationRequested(); var origin = metadata.Origins[index];
            if (origin is null || origin.RootIndex != index || !Enum.IsDefined(origin.Kind))
                throw new InvalidDataException("Invalid, duplicate or dangling frozen root origin.");
            Text(origin.OriginalValue); Text(origin.OriginAbsolutePath); OptionalText(origin.RecordKey); OptionalText(origin.EmmSection);
            if (origin.SectionOwner is { } owner) { Text(owner.ObjectKey); OptionalText(owner.ParentTargetLabel); }
        }

        static void OptionalText(string? value) { if (value is not null) Text(value); }
        static void Text(string value)
        {
            if (value is null || value.Contains('\0') || StrictUtf8.GetByteCount(value) > 32_767)
                throw new InvalidDataException("Frozen effect-project strings require bounded Unicode text without NUL.");
        }
    }

    private static void ValidateCounts(int bindings, int seeds, int roots, int origins)
    {
        if (bindings > 65_536 || seeds != bindings || roots != origins
            || (long)bindings + seeds + roots + origins > MaxDescriptors)
            throw new InvalidDataException("Frozen effect-project descriptors exceed their count budget or disagree.");
    }

    private sealed record Metadata(int SchemaVersion, int AnsiCodePage, string EmmSha256, string OriginalEmmPath,
        string OriginalWorkingDirectory, BindingData[] Bindings, SeedData[] RootSeeds, RootData[] Roots, OriginData[] Origins);
    private sealed record BindingData(Guid ObjectId, bool IsModel, string SourcePath, int RenderOrder);
    private sealed record SeedData(Guid ObjectId, bool IsModel, string? OriginAbsoluteFxPath);
    private sealed record RootData(string OriginAbsolutePath, D3D9EffectDependencyProvenance Provenance);
    private sealed record OriginData(int RootIndex, D3D9EffectProjectRootKind Kind, string OriginalValue, string OriginAbsolutePath,
        Guid? ObjectId, bool? IsModel, string? RecordKey, string? EmmSection, int? Subset, EmmOwnerReference? SectionOwner);
}
