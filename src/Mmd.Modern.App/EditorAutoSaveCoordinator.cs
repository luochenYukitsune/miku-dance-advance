using System.Security.Cryptography;
using System.Text.Json;
using Mmd.Modern.Application;

namespace Mmd.Modern.App;

public enum EditorAutoSaveStatus { Saved, Unchanged, Busy, Canceled, Failed, Closed }

public sealed record EditorAutoSaveResult(EditorAutoSaveStatus Status,
    EditorRecoveryCheckpoint? Checkpoint = null, Exception? Error = null);

/// <summary>Owns a frozen project and detached metadata. The save delegate must use only these
/// frozen inputs, never a live workspace. Ownership transfers to the coordinator when captured.</summary>
public sealed class EditorAutoSaveSnapshot : IDisposable
{
    private IDisposable? owner;
    private readonly Action<string, CancellationToken> save;

    public EditorAutoSaveSnapshot(EditorRecoveryCheckpointMetadata metadata, PmaSaveSnapshot snapshot)
        : this(ValidateFormat(metadata, snapshot), snapshot, snapshot.Save) { }

    public EditorAutoSaveSnapshot(EditorRecoveryCheckpointMetadata metadata, IDisposable ownedCapture,
        Action<string, CancellationToken> frozenSave)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(ownedCapture);
        ArgumentNullException.ThrowIfNull(frozenSave);
        Metadata = metadata;
        owner = ownedCapture;
        save = frozenSave;
    }

    public EditorRecoveryCheckpointMetadata Metadata { get; }

    private static EditorRecoveryCheckpointMetadata ValidateFormat(EditorRecoveryCheckpointMetadata metadata, PmaSaveSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (metadata.ProjectFormat.PackageFormat != snapshot.Format || metadata.ProjectFormat.Extension != snapshot.FileExtension)
            throw new ArgumentException("The recovery metadata and frozen package have different project formats.", nameof(metadata));
        return metadata;
    }

    /// <summary>Includes authored revision, editor drafts, schema, and display/path metadata. Compute
    /// on the owner thread before requesting an expensive package capture. Caller identities are also supported.</summary>
    public static string GetFingerprint(EditorRecoveryCheckpointMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(metadata)));
    }

    internal void Save(string path, CancellationToken cancellationToken) => save(path, cancellationToken);
    public void Dispose() => Interlocked.Exchange(ref owner, null)?.Dispose();
}

/// <summary>
/// GUI-independent single-flight autosave. Capture runs synchronously on the caller before this
/// method returns; session creation and writing run on a worker using only the owned capture.
/// Stopping a timer needs no lifecycle action. Only an explicitly confirmed close/discard should
/// retire a document or shut down cleanly; abandonment retains recovery history.
/// </summary>
public sealed class EditorAutoSaveCoordinator : IDisposable
{
    private readonly object gate = new();
    private readonly EditorRecoveryStore store;
    private readonly Dictionary<Guid, DocumentSession> documents = [];
    private readonly HashSet<Guid> retiredDocuments = [];
    private PendingCheckpoint? pending;
    private TaskCompletionSource? shutdown;
    private bool closing, abandoned;

    public EditorAutoSaveCoordinator(EditorRecoveryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <summary>Busy requests are rejected, not queued. Unchanged requests do not invoke capture.
    /// Capture/write/cancellation failures are returned with their exception and can be retried.</summary>
    public Task<EditorAutoSaveResult> CheckpointAsync(Guid documentId, string fingerprint,
        Func<EditorAutoSaveSnapshot> capture, CancellationToken cancellationToken = default)
    {
        if (documentId == Guid.Empty) throw new ArgumentException("A document identity is required.", nameof(documentId));
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(capture);
        PendingCheckpoint operation;
        lock (gate)
        {
            if (closing || abandoned || retiredDocuments.Contains(documentId)) return Result(EditorAutoSaveStatus.Closed);
            if (cancellationToken.IsCancellationRequested)
                return Task.FromResult(new EditorAutoSaveResult(EditorAutoSaveStatus.Canceled,
                    Error: new OperationCanceledException(cancellationToken)));
            if (pending is not null) return Result(EditorAutoSaveStatus.Busy);
            if (!documents.TryGetValue(documentId, out var document))
                documents.Add(documentId, document = new(documentId));
            if (document.Retirement is not null) return Result(EditorAutoSaveStatus.Closed);
            if (StringComparer.Ordinal.Equals(document.LastFingerprint, fingerprint)) return Result(EditorAutoSaveStatus.Unchanged);
            operation = new(document, fingerprint);
            pending = operation;
        }

        EditorAutoSaveSnapshot snapshot;
        try { snapshot = capture() ?? throw new InvalidOperationException("Autosave capture returned no snapshot."); }
        catch (Exception error)
        {
            Complete(operation, Failure(error, cancellationToken));
            return operation.Completion.Task;
        }
        // Never pass the caller's cancellation token to Task.Run: even a pre-start cancellation
        // must enter RunCheckpointAsync and dispose the owned snapshot and complete the operation.
        _ = Task.Run(() => RunCheckpointAsync(operation, snapshot, cancellationToken));
        return operation.Completion.Task;
    }

    /// <summary>Waits for the current capture/write only; neither marks clean nor releases leases.
    /// Stop the caller's timer first if it must remain idle after this wait.</summary>
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        Task wait;
        lock (gate) wait = pending?.Completion.Task ?? Task.CompletedTask;
        return wait.WaitAsync(cancellationToken);
    }

    /// <summary>After a confirmed project close/discard, drains that document then marks its session
    /// clean. Unknown documents are a no-op. Failure/cancellation leaves the session available for retry.</summary>
    public Task RetireCleanAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        Task wait;
        TaskCompletionSource completion;
        DocumentSession document;
        lock (gate)
        {
            if (abandoned) return Task.FromException(new ObjectDisposedException(nameof(EditorAutoSaveCoordinator)));
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
            if (retiredDocuments.Contains(documentId)) return Task.CompletedTask;
            if (!documents.TryGetValue(documentId, out document!))
            {
                retiredDocuments.Add(documentId);
                return Task.CompletedTask;
            }
            if (document.Retirement is not null) return document.Retirement.Task;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            document.Retirement = completion;
            wait = pending is { } active && ReferenceEquals(active.Document, document)
                ? active.Completion.Task : Task.CompletedTask;
        }
        _ = RetireCoreAsync(document, wait, completion, cancellationToken);
        return completion.Task;
    }

    /// <summary>Blocks new captures, drains pending work, and marks all sessions clean. A failed
    /// shutdown can be retried. Packages/history remain on disk; clean sessions are not recovery offers.</summary>
    public Task ShutdownCleanAsync(CancellationToken cancellationToken = default)
    {
        Task wait;
        Guid[] documentIds;
        TaskCompletionSource completion;
        lock (gate)
        {
            if (abandoned) return Task.FromException(new ObjectDisposedException(nameof(EditorAutoSaveCoordinator)));
            if (shutdown is not null) return shutdown.Task;
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
            closing = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            shutdown = completion;
            wait = pending?.Completion.Task ?? Task.CompletedTask;
            documentIds = documents.Keys.ToArray();
        }
        _ = ShutdownCoreAsync(wait, documentIds, completion, cancellationToken);
        return completion.Task;
    }

    /// <summary>Releases active ownership without marking clean. In-flight captures keep their lease
    /// until the transaction and snapshot disposal finish. This does not cancel an active write.</summary>
    public void Abandon()
    {
        List<EditorRecoverySession> release = [];
        lock (gate)
        {
            abandoned = true;
            closing = true;
            foreach (var document in documents.Values)
                if (pending?.Document != document && document.Retirement is null && document.Session is { } session)
                {
                    document.Session = null;
                    release.Add(session);
                }
        }
        foreach (var session in release) session.Dispose();
    }

    public void Dispose() => Abandon();

    private async Task RunCheckpointAsync(PendingCheckpoint operation, EditorAutoSaveSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        EditorAutoSaveResult result;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EditorRecoverySession? session;
            lock (gate) session = operation.Document.Session;
            if (session is null)
            {
                session = await store.CreateSessionAsync(snapshot.Metadata.OriginalProjectPath,
                    snapshot.Metadata.DisplayName, cancellationToken).ConfigureAwait(false);
                lock (gate) operation.Document.Session = session;
            }
            var checkpoint = await store.WriteCheckpointAsync(session, snapshot.Metadata, (path, token) =>
            {
                snapshot.Save(path, token);
                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
            result = new(EditorAutoSaveStatus.Saved, checkpoint);
        }
        catch (EditorRecoveryCommittedException error)
        {
            result = new(EditorAutoSaveStatus.Saved, error.Checkpoint, error.InnerException ?? error);
        }
        catch (Exception error) { result = Failure(error, cancellationToken); }
        try { snapshot.Dispose(); }
        catch (Exception error)
        {
            result = WithCleanupError(result, error);
        }
        Complete(operation, result);
    }

    private void Complete(PendingCheckpoint operation, EditorAutoSaveResult result)
    {
        EditorRecoverySession? release = null;
        lock (gate)
        {
            if (result.Checkpoint is not null) operation.Document.LastFingerprint = operation.Fingerprint;
            pending = null;
            if (abandoned && operation.Document.Retirement is null)
            {
                release = operation.Document.Session;
                operation.Document.Session = null;
            }
        }
        try { release?.Dispose(); }
        catch (Exception error)
        {
            result = WithCleanupError(result, error);
        }
        operation.Completion.TrySetResult(result);
    }

    private async Task RetireCoreAsync(DocumentSession document, Task wait, TaskCompletionSource completion,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        EditorRecoverySession? release = null;
        try
        {
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            EditorRecoverySession? session;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(abandoned, this);
                session = document.Session;
            }
            if (session is not null) await store.MarkCleanAsync(session, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                retiredDocuments.Add(document.DocumentId);
                documents.Remove(document.DocumentId);
                release = document.Session;
                document.Session = null;
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            lock (gate)
            {
                document.Retirement = null;
                if (abandoned && pending?.Document != document)
                {
                    release ??= document.Session;
                    document.Session = null;
                }
            }
            try { release?.Dispose(); }
            catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }
    }

    private async Task ShutdownCoreAsync(Task wait, Guid[] documentIds, TaskCompletionSource completion,
        CancellationToken cancellationToken)
    {
        try
        {
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            foreach (Guid id in documentIds)
                await RetireCleanAsync(id, cancellationToken).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            lock (gate) if (ReferenceEquals(shutdown, completion)) shutdown = null;
            completion.TrySetException(error);
        }
    }

    private static Task<EditorAutoSaveResult> Result(EditorAutoSaveStatus status) => Task.FromResult(new EditorAutoSaveResult(status));
    private static EditorAutoSaveResult WithCleanupError(EditorAutoSaveResult result, Exception error)
    {
        if (result.Error is { } primary)
        {
            const string key = "Recovery.CleanupErrors";
            if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
            errors.Add(error);
        }
        return result with { Status = result.Checkpoint is not null ? EditorAutoSaveStatus.Saved
                : result.Error is null ? EditorAutoSaveStatus.Failed : result.Status,
            Error = result.Error ?? error };
    }
    private static EditorAutoSaveResult Failure(Exception error, CancellationToken token) =>
        new(error is OperationCanceledException && token.IsCancellationRequested
            ? EditorAutoSaveStatus.Canceled : EditorAutoSaveStatus.Failed, Error: error);

    private sealed class DocumentSession(Guid documentId)
    {
        public Guid DocumentId { get; } = documentId;
        public EditorRecoverySession? Session { get; set; }
        public string? LastFingerprint { get; set; }
        public TaskCompletionSource? Retirement { get; set; }
    }

    private sealed class PendingCheckpoint(DocumentSession document, string fingerprint)
    {
        public DocumentSession Document { get; } = document;
        public string Fingerprint { get; } = fingerprint;
        public TaskCompletionSource<EditorAutoSaveResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
