using System.Text.Json;

namespace Mmd.Modern.Application;

/// <summary>A detached authored project capture that can be saved on a background thread after its source
/// session has changed or closed. Owns a lease on any extracted package resources until disposed.</summary>
public sealed class PmaSaveSnapshot : IDisposable
{
    private readonly object gate = new();
    private PmaSaveData? data;
    private bool saving, disposed;
    public Guid ProjectId { get; }
    public string Format { get; }
    public string FileExtension { get; }
    internal PmaSaveSnapshot(PmaSaveData data)
    {
        this.data = data; ProjectId = data.ProjectId;
        (Format, FileExtension) = data.Kind switch
        {
            PmaPackageKind.Modern => (PmaProjectStore.Format, ".pma"),
            PmaPackageKind.Classic => (PmcProjectStore.Format, ".pmc"),
            _ => throw new ArgumentOutOfRangeException(nameof(data)),
        };
    }

    /// <summary>Writes the captured project atomically using the package validation and publication path.
    /// The capturing store fixes its format and file extension. Sequential saves are supported;
    /// concurrent saves on the same snapshot are rejected.</summary>
    public void Save(string path, CancellationToken cancellationToken = default)
        => SaveCore(path, cancellationToken);

    internal void SaveAndDispose(string path, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        string? committedDestination = null;
        try { committedDestination = SaveCore(path, cancellationToken); }
        catch (Exception exception) { failure = exception; throw; }
        finally { PmaSaveCleanup.Complete(failure, committedDestination, Dispose); }
    }

    private string SaveCore(string path, CancellationToken cancellationToken)
    {
        PmaSaveData captured;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (saving) throw new InvalidOperationException("A PMA snapshot save is already in progress.");
            captured = data!;
            saving = true;
        }
        Exception? failure = null;
        string? committedDestination = null;
        try
        {
            string destination = PmaProjectStore.PackagePath(path, captured.Kind);
            PmaProjectStore.SaveSnapshot(destination, captured, cancellationToken);
            committedDestination = destination;
            return destination;
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            PmaSaveData? release = null;
            lock (gate)
            {
                saving = false;
                if (disposed) { release = data; data = null; }
            }
            PmaSaveCleanup.Complete(failure, committedDestination, () => release?.Lease?.Dispose());
        }
    }

    /// <summary>Prevents further saves immediately. An active save keeps its frozen inputs and resource
    /// lease until it finishes; disposal does not cancel or block that save.</summary>
    public void Dispose()
    {
        PmaSaveData? release = null;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (!saving) { release = data; data = null; }
        }
        release?.Lease?.Dispose();
    }
}

internal sealed record PmaProjectInputs(SceneCompositionEditor Composition, SceneLightingEditor Lighting,
    SceneAnimationSession.SaveSnapshot Animation, ProjectSettings Settings, SceneMediaTrack[] Media,
    PmaRenderingStyleState RenderingStyles, PmaRetainedState? Retained, Guid? ProjectIdentity,
    SceneProjectAccessory[] Accessories);

internal sealed record PmaSaveData(PmaProjectInputs Project, PmaSaveOptions Options,
    Dictionary<string, byte[]> AdditionalResources, Dictionary<string, JsonElement> Extensions,
    Guid ProjectId, IDisposable? Lease, PmaPackageKind Kind);

internal enum PmaPackageKind { Modern, Classic }
