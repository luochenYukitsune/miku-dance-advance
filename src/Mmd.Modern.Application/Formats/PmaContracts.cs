using System.Text.Json;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Full represented PMX sections are serialized with PmxWriter, never a render-material-only override.
/// Texture paths in an override are resolved relative to that actor's source model.</summary>
public sealed record PmaSaveOptions
{
    /// <summary>Optional package content constraints, applied only to this detached save capture.</summary>
    public PmaContentSelection? ContentSelection { get; init; }
    /// <summary>Snapshots used only by this Save call. Does not edit the live session or affect a subsequent default Save.
    /// Reopening the saved PMA adopts these model snapshots. Apply a SceneProjectSession edit to change live actor metadata.</summary>
    public IReadOnlyDictionary<Guid, PmxModel> ModelOverrides { get; init; } = new Dictionary<Guid, PmxModel>();
    /// <summary>Save-only extension overrides, defensively cloned and validated. The rendering-styles extension,
    /// when supplied here, affects only the saved package; use the session's typed edit API to change the live selection.</summary>
    public IReadOnlyDictionary<string, JsonElement> OptionalExtensions { get; init; } = new Dictionary<string, JsonElement>();
    /// <summary>Save-only renderer-neutral files under render-resources/. Contents are snapshotted before staging.
    /// Existing loaded resources are preserved by default; a matching logical path replaces only this save's copy.</summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> AdditionalResources { get; init; } = new Dictionary<string, ReadOnlyMemory<byte>>();
    /// <summary>Classic-only opt-in supplied by a trusted accessory adapter after complete asset/dependency
    /// capture. Must cover every live accessory and match the final selected extension and resource bytes.
    /// Null preserves the explicit rejection of independent accessory saves.</summary>
    public PmaAccessoryPersistenceCapture? AccessoryPersistence { get; init; }
    public string CacheDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "Mda", "pma");
    /// <summary>Limits for validating this save. Use matching load options for packages above the default budgets.
    /// ZIP64 has no 4 GiB format limit; the defaults deliberately bound input and expanded resource costs.</summary>
    public PmaLoadOptions ValidationOptions { get; init; } = new();
}

public sealed record PmaLoadOptions
{
    /// <summary>Optional constraints checked after package/reference validation and before any host model
    /// callback or physics construction. Forbidden content fails explicitly instead of being discarded.</summary>
    public PmaContentSelection? ContentSelection { get; init; }
    /// <summary>Optional synchronous consumer validation after package and reference validation, before any
    /// host model callback or session/physics construction. The context is valid only during this callback;
    /// throwing rejects the load. Optional metadata and resources remain opaque to the project reader.</summary>
    public Action<PmaPackageReadContext, CancellationToken>? PackagePreflight { get; init; }
    /// <summary>Classic-only accessory preparation during package preflight, before host model callbacks.
    /// Return a complete frozen set bound to the package's opaque extension/resources, or null when absent.
    /// The context expires after both preflight consumers return; returned assets must own their frozen inputs.</summary>
    public Func<PmaPackageReadContext, CancellationToken, PmaAccessoryPersistenceCapture?>? AccessoryPreparation { get; init; }
    /// <summary>Optional per-instance model-source decoration after package/resource validation and host model loading,
    /// before animation and physics construction. Must retain the host PMX model instance; null preserves the old load path.</summary>
    public Func<PmaModelResourceContext, SceneAnimationModelAsset, SceneAnimationModelAsset>? ModelAssetTransform { get; init; }
    /// <summary>Maximum compressed package size. Independent of the expanded and per-resource budgets.</summary>
    public long MaxPackageBytes { get; init; } = 50L * 1024 * 1024 * 1024;
    /// <summary>Maximum archive expansion and logical extraction size, including repeated logical files.</summary>
    public long MaxExpandedBytes { get; init; } = 50L * 1024 * 1024 * 1024;
    /// <summary>Maximum single packaged resource size. Consumers may impose narrower parsing or in-memory limits.</summary>
    public long MaxEntryBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public int MaxFiles { get; init; } = 100_000;
    public int MaxManifestBytes { get; init; } = 16 * 1024 * 1024;
    internal void Validate()
    {
        if (MaxPackageBytes < 1 || MaxExpandedBytes < 1 || MaxEntryBytes < 1 || MaxFiles < 1 || MaxManifestBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxExpandedBytes), "All PMA load budgets must be positive.");
    }
}

public sealed record PmaModelVersion(Guid ObjectId, string OriginalSourcePath, string OriginalModelPath, string EditedModelPath);

/// <summary>Owns the fresh session and its extracted immutable resources. Keep alive while the project is open.</summary>
public sealed class PmaLoadedProject : IDisposable, IPmaPackageResources
{
    /// <summary>Validated native package format identifier. This describes storage identity only;
    /// optional extension contents remain opaque to this reader.</summary>
    public string Format { get; }
    public SceneProjectSession Session { get; }
    public Guid ProjectId { get; }
    public string CacheDirectory { get; }
    public IReadOnlyList<PmaModelVersion> Models { get; }
    public IReadOnlyDictionary<string, JsonElement> OptionalExtensions { get; }
    /// <summary>Canonical logical paths of packaged render-resources files. No renderer interpretation is performed.</summary>
    public IReadOnlyList<string> ResourcePaths { get; }
    /// <summary>Preservation notices for unknown optional schemas/fields. Custom preset references are valid metadata,
    /// and this application reader does not infer renderer installation, capability or a rendering fallback.</summary>
    public IReadOnlyList<string> Diagnostics { get; }
    private bool disposed;
    private readonly PmaResourceReader resources;
    private readonly PmaRetainedState retained;
    internal PmaLoadedProject(SceneProjectSession session, PmaRetainedState retained, string format)
    {
        this.retained = retained;
        Format = format;
        Session = session; ProjectId = retained.ProjectId; CacheDirectory = retained.Directory;
        Models = Array.AsReadOnly(retained.Models); OptionalExtensions = new System.Collections.ObjectModel.ReadOnlyDictionary<string, JsonElement>(retained.Extensions);
        resources = retained.ResourceReader;
        ResourcePaths = resources.ResourcePaths;
        Diagnostics = Array.AsReadOnly(retained.Extensions.Keys.Where(k => k != SceneRenderingStyles.ExtensionName)
            .Select(k => $"{k}: unknown optional extension preserved without interpretation.")
            .Concat(session.RenderingStyleDiagnostics).ToArray());
    }
    /// <summary>Returns a detached copy of a validated packaged resource. Missing resources fail explicitly.
    /// This byte-array convenience API additionally rejects resources above Array.MaxLength.</summary>
    public byte[] ReadResource(string path, long maxBytes = long.MaxValue)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return resources.ReadResource(path, maxBytes);
    }
    public void Dispose()
    {
        if (disposed) return;
        // A rejected reentrant disposal must not close resources or remove the active
        // session's cache. Check the transaction guard before committing owner disposal.
        Session.RequireDisposalAllowed();
        disposed = true; resources.Dispose();
        try { Session.Dispose(); }
        finally { retained.ReleaseOwner(); }
    }
}

internal sealed class PmaRetainedState(Guid projectId, string directory, PmaModelVersion[] models,
    Dictionary<string, JsonElement> extensions, PmaResourceReader resourceReader)
{
    public Guid ProjectId { get; } = projectId;
    public string Directory { get; } = directory;
    public PmaModelVersion[] Models { get; } = models;
    public Dictionary<string, JsonElement> Extensions { get; } = extensions;
    public PmaResourceReader ResourceReader { get; } = resourceReader;
    private readonly object gate = new();
    private int references = 1;
    private bool ownerReleased;
    public PmaAdditionalResource[] Resources => ResourceReader.Resources;

    internal IDisposable AcquireLease()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(ownerReleased, this);
            var lease = new ResourceLease(this);
            references = checked(references + 1);
            return lease;
        }
    }

    internal void ReleaseOwner()
    {
        bool cleanup;
        lock (gate)
        {
            if (ownerReleased) return;
            ownerReleased = true;
            cleanup = --references == 0;
        }
        if (cleanup) DeleteDirectory();
    }

    private void ReleaseLease()
    {
        bool cleanup;
        lock (gate) cleanup = --references == 0;
        if (cleanup) DeleteDirectory();
    }

    private void DeleteDirectory()
    {
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed class ResourceLease(PmaRetainedState owner) : IDisposable
    {
        private PmaRetainedState? retained = owner;
        public void Dispose() => Interlocked.Exchange(ref retained, null)?.ReleaseLease();
    }
}

/// <summary>Validated resources and explicit instance identity available to an optional model-source decorator.
/// Resource reads remain valid only for the lifetime of the loaded project, and always return detached bytes.</summary>
public sealed class PmaModelResourceContext
{
    public Guid ObjectId { get; }
    public IReadOnlyList<string> ResourcePaths => resources.ResourcePaths;
    public IReadOnlyDictionary<string, JsonElement> OptionalExtensions { get; }
    private readonly PmaResourceReader resources;
    internal PmaModelResourceContext(Guid objectId, PmaResourceReader resources, IReadOnlyDictionary<string, JsonElement> extensions)
    { ObjectId = objectId; this.resources = resources; OptionalExtensions = extensions; }
    public byte[] ReadResource(string path, long maxBytes = long.MaxValue) => resources.ReadResource(path, maxBytes);
}
