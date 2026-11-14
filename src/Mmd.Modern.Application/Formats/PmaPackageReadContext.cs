using System.Collections.ObjectModel;
using System.Text.Json;

namespace Mmd.Modern.Application;

/// <summary>Renderer-neutral, read-only access to validated package identity, opaque optional metadata,
/// and resources. Resource reads return independent bytes and enforce the requested in-memory budget.</summary>
public interface IPmaPackageResources
{
    string Format { get; }
    Guid ProjectId { get; }
    IReadOnlyDictionary<string, JsonElement> OptionalExtensions { get; }
    IReadOnlyList<string> ResourcePaths { get; }
    byte[] ReadResource(string path, long maxBytes = long.MaxValue);
}

/// <summary>Read-only package access available only during the synchronous package-preflight callback.
/// Metadata is detached and immutable; no session, extraction path, or writable resource is exposed.
/// Retaining this context does not extend its lifetime or transfer ownership of package resources.</summary>
public sealed class PmaPackageReadContext : IPmaPackageResources
{
    private readonly string format;
    private readonly Guid projectId;
    private readonly IReadOnlyDictionary<string, JsonElement> extensions;
    private readonly IReadOnlyList<string> resourcePaths;
    private readonly object gate = new();
    private PmaResourceReader? resources;

    internal PmaPackageReadContext(string format, PmaRetainedState retained)
    {
        this.format = format; projectId = retained.ProjectId;
        extensions = new ReadOnlyDictionary<string, JsonElement>(retained.Extensions
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal));
        resources = retained.ResourceReader;
        resourcePaths = Array.AsReadOnly(resources.ResourcePaths.ToArray());
    }

    public string Format { get { lock (gate) { _ = RequireResources(); return format; } } }
    public Guid ProjectId { get { lock (gate) { _ = RequireResources(); return projectId; } } }
    public IReadOnlyDictionary<string, JsonElement> OptionalExtensions
    { get { lock (gate) { _ = RequireResources(); return extensions; } } }
    public IReadOnlyList<string> ResourcePaths
    { get { lock (gate) { _ = RequireResources(); return resourcePaths; } } }

    /// <summary>Returns detached bytes, checking canonical path, requested budget, manifest length and SHA256
    /// on every read. Fails after the preflight callback returns, including when it throws or is cancelled.</summary>
    public byte[] ReadResource(string path, long maxBytes = long.MaxValue)
    {
        lock (gate) return RequireResources().ReadResource(path, maxBytes);
    }

    internal void Invalidate()
    {
        // The loaded project owns the shared reader. Expiring callback access must never close it.
        lock (gate) resources = null;
    }

    private PmaResourceReader RequireResources()
        => resources ?? throw new ObjectDisposedException(nameof(PmaPackageReadContext));
}
