using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.ExceptionServices;
using Mmd.Modern.Application;

namespace Mmd.Modern.App;

public sealed record RecoveryRetentionPolicy(int MaxVersions = 5, long MaxTotalBytes = 5L * 1024 * 1024 * 1024);

public sealed record EditorRecoveryCheckpointMetadata
{
    public string? OriginalProjectPath { get; }
    public string DisplayName { get; }
    public long ContentRevision { get; }
    public JsonElement EditorState { get; }
    public int EditorStateSchemaVersion { get; }
    public EditorProjectFormat ProjectFormat { get; }

    public EditorRecoveryCheckpointMetadata(string? originalProjectPath, string displayName, long contentRevision,
        JsonElement editorState, int editorStateSchemaVersion = 1, EditorProjectFormat? projectFormat = null)
    {
        OriginalProjectPath = originalProjectPath;
        DisplayName = displayName;
        ContentRevision = contentRevision;
        // Callers may dispose their JsonDocument immediately after constructing this value.
        EditorState = editorState.Clone();
        EditorStateSchemaVersion = editorStateSchemaVersion;
        ProjectFormat = projectFormat ?? EditorProjectFormat.Modern;
    }
}

public sealed record EditorRecoveryCheckpoint(Guid SessionId, Guid CheckpointId, DateTimeOffset CreatedUtc,
    string PackagePath, long PackageSizeBytes, string Sha256, string? OriginalProjectPath, string DisplayName,
    long ContentRevision, JsonElement EditorState, int EditorStateSchemaVersion, EditorProjectFormat? ProjectFormat = null)
{
    public EditorProjectFormat ProjectFormat { get; } = ProjectFormat ?? EditorProjectFormat.Modern;
    public long SizeBytes => PackageSizeBytes;
}

/// <summary>The recovery JSON and package committed; subsequent cleanup failed.</summary>
public sealed class EditorRecoveryCommittedException : IOException
{
    internal EditorRecoveryCommittedException(EditorRecoveryCheckpoint checkpoint, Exception cleanupError)
        : base("The recovery checkpoint was saved, but cleanup failed.", cleanupError) => Checkpoint = checkpoint;

    public EditorRecoveryCheckpoint Checkpoint { get; }
}

public sealed record EditorRecoverySessionInfo(Guid SessionId, DateTimeOffset CreatedUtc,
    string? OriginalProjectPath, string DisplayName, IReadOnlyList<EditorRecoveryCheckpoint> Checkpoints);

/// <summary>A held OS lease identifies a live editor; disposing it alone deliberately leaves recovery available.</summary>
public sealed class EditorRecoverySession : IDisposable
{
    private readonly object lifetimeGate = new();
    private FileStream? lease;
    private int operations;
    private bool disposed;

    internal EditorRecoverySession(EditorRecoveryStore owner, Guid sessionId, FileStream lease)
        => (Owner, SessionId, this.lease) = (owner, sessionId, lease);

    public Guid SessionId { get; }
    internal EditorRecoveryStore Owner { get; }
    internal SemaphoreSlim Writer { get; } = new(1, 1);
    internal bool IsClean { get; set; }

    internal void BeginOperation()
    {
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            operations++;
        }
    }

    internal void ThrowIfDisposed()
    {
        lock (lifetimeGate) ObjectDisposedException.ThrowIf(disposed, this);
    }

    internal void EndOperation()
    {
        lock (lifetimeGate)
        {
            operations--;
            if (disposed && operations == 0) ReleaseLease();
        }
    }

    public void Dispose()
    {
        lock (lifetimeGate)
        {
            disposed = true;
            // Do not expose an in-flight transaction as an abandoned session to another process.
            if (operations == 0) ReleaseLease();
        }
    }

    private void ReleaseLease()
    {
        lease?.Dispose();
        lease = null;
    }
}

/// <summary>
/// Independent recovery packages. The supplied writer must finish and validate its declared format before returning.
/// The store never opens the original project path, and only removes its own GUID-named checkpoints.
/// </summary>
public sealed class EditorRecoveryStore
{
    public const int MaxEditorStateBytes = 16 * 1024 * 1024;
    private const int MaxSessionMetadataBytes = 512 * 1024;
    private const int MaxMetadataBytes = MaxEditorStateBytes + MaxSessionMetadataBytes;
    private const int SchemaVersion = 1;
    private const int CheckpointSchemaVersion = 2;
    private const string SessionFileName = "session.json";
    private const string LeaseFileName = "session.lock";
    private readonly RecoveryRetentionPolicy retention;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public EditorRecoveryStore(string rootDirectory, RecoveryRetentionPolicy? retentionPolicy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        retention = retentionPolicy ?? new();
        if (retention.MaxVersions < 1 || retention.MaxTotalBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(retentionPolicy));
        RejectReparseAncestors(RootDirectory);
    }

    public string RootDirectory { get; }

    public async Task<EditorRecoverySession> CreateSessionAsync(string? originalProjectPath, string displayName,
        CancellationToken cancellationToken = default)
    {
        ValidateNames(originalProjectPath, displayName);
        cancellationToken.ThrowIfCancellationRequested();
        using var rootLock = await AcquireRootLockAsync(cancellationToken).ConfigureAwait(false);
        Guid id = Guid.NewGuid();
        string directory = SessionDirectory(id);
        if (Directory.Exists(directory)) throw new IOException("The new recovery session directory already exists.");
        Directory.CreateDirectory(directory);
        EnsureSafePath(directory);
        FileStream? lease = null;
        try
        {
            lease = new(Path.Combine(directory, LeaseFileName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            await WriteJsonAtomicallyAsync(Path.Combine(directory, SessionFileName),
                new SessionDocument(SchemaVersion, id, DateTimeOffset.UtcNow, originalProjectPath, displayName, false),
                cancellationToken).ConfigureAwait(false);
            var session = new EditorRecoverySession(this, id, lease);
            lease = null;
            return session;
        }
        catch
        {
            lease?.Dispose();
            TryDeleteOwnedFile(Path.Combine(directory, SessionFileName));
            TryDeleteOwnedFile(Path.Combine(directory, LeaseFileName));
            try { Directory.Delete(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public Task<IReadOnlyList<EditorRecoverySessionInfo>> ListRecoverableAsync(CancellationToken cancellationToken = default)
        => ListRecoverableAsync(startupOffersOnly: false, cancellationToken);

    internal Task<IReadOnlyList<EditorRecoverySessionInfo>> ListStartupRecoverableAsync(CancellationToken cancellationToken = default)
        => ListRecoverableAsync(startupOffersOnly: true, cancellationToken);

    private async Task<IReadOnlyList<EditorRecoverySessionInfo>> ListRecoverableAsync(bool startupOffersOnly,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSafePath(RootDirectory);
        if (!Directory.Exists(RootDirectory)) return [];
        var result = new List<EditorRecoverySessionInfo>();
        foreach (string directory in Directory.EnumerateDirectories(RootDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryDirectoryId(directory, out Guid id)) continue;
            try
            {
                EnsureSafePath(directory);
                using var lease = TryAcquireInactiveLease(directory);
                if (lease is null) continue;
                var session = await ReadSessionAsync(directory, id, cancellationToken).ConfigureAwait(false);
                if (session is null || session.Clean || (startupOffersOnly && session.StartupOfferCompleted)) continue;
                var points = await ReadCheckpointsAsync(directory, id, cancellationToken).ConfigureAwait(false);
                if (points.Count == 0) continue;
                result.Add(new(id, session.CreatedUtc, points[0].OriginalProjectPath, points[0].DisplayName, points.AsReadOnly()));
            }
            catch (Exception exception) when (IsInvalidEntry(exception)) { }
        }
        return result.OrderByDescending(x => x.Checkpoints[0].CreatedUtc).ThenBy(x => x.SessionId).ToArray();
    }

    public async Task<EditorRecoveryCheckpoint?> GetLatestValidCheckpointAsync(Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = SessionDirectory(sessionId);
        try
        {
            EnsureSafePath(directory);
            if (!Directory.Exists(directory)) return null;
            using var lease = TryAcquireInactiveLease(directory);
            if (lease is null) return null;
            var session = await ReadSessionAsync(directory, sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null || session.Clean) return null;
            return (await ReadCheckpointsAsync(directory, sessionId, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        }
        catch (Exception exception) when (IsInvalidEntry(exception)) { return null; }
    }

    public async Task<EditorRecoveryCheckpoint> WriteCheckpointAsync(EditorRecoverySession session,
        EditorRecoveryCheckpointMetadata metadata, Func<string, CancellationToken, Task> writeValidatedPackage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(writeValidatedPackage);
        ValidateSessionOwner(session);
        ValidateMetadata(metadata);
        cancellationToken.ThrowIfCancellationRequested();
        session.BeginOperation();
        bool entered = false, packageMoved = false;
        FileStream? rootLock = null;
        string? temporaryPackage = null, finalPackage = null;
        RetentionPlan? plan = null;
        EditorRecoveryCheckpoint? committedCheckpoint = null;
        Exception? failure = null, packageWarning = null;
        try
        {
            await session.Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            session.ThrowIfDisposed();
            if (session.IsClean) throw new InvalidOperationException("A clean recovery session cannot accept another checkpoint.");
            string directory = SessionDirectory(session.SessionId);
            EnsureSafePath(directory);
            // Hold the cross-process lease while staging too: another writer must not spend the
            // same remaining root budget, and abandoned staging packages still count toward it.
            rootLock = await AcquireRootLockAsync(cancellationToken).ConfigureAwait(false);
            session.ThrowIfDisposed();
            Guid checkpointId = Guid.NewGuid();
            string basename = checkpointId.ToString("N");
            // The frozen descriptor, not the original path or a live workspace, owns the suffix.
            string extension = metadata.ProjectFormat.Extension;
            temporaryPackage = Path.Combine(directory, basename + ".tmp" + extension);
            finalPackage = Path.Combine(directory, basename + extension);
            try { await writeValidatedPackage(temporaryPackage, cancellationToken).ConfigureAwait(false); }
            catch (PmaSaveCommittedException error) when (error.DestinationPath.Equals(
                Path.GetFullPath(temporaryPackage), PathComparison))
            {
                // The package writer committed only our staging package. Recovery itself is
                // still uncommitted until its JSON publishes below.
                packageWarning = error;
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafePath(temporaryPackage);
            long size;
            string hash;
            using (var package = new FileStream(temporaryPackage, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                size = package.Length;
                if (size == 0) throw new InvalidDataException("The recovery package is empty.");
                hash = Convert.ToHexString(await SHA256.HashDataAsync(package, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
                await package.FlushAsync(cancellationToken).ConfigureAwait(false);
                package.Flush(flushToDisk: true);
            }

            var existing = await ReadCheckpointsAsync(directory, session.SessionId, cancellationToken, verifyDigests: false).ConfigureAwait(false);
            plan = await PlanRetentionAsync(session.SessionId, existing,
                CountStoredPackageBytes(temporaryPackage, cancellationToken), size, cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (existing.Count > 0 && now <= existing[0].CreatedUtc) now = existing[0].CreatedUtc.AddTicks(1);
            var document = new CheckpointDocument(CheckpointSchemaVersion, session.SessionId, checkpointId, now,
                basename + extension, size, hash, metadata.OriginalProjectPath, metadata.DisplayName, metadata.ContentRevision,
                metadata.EditorState, metadata.EditorStateSchemaVersion, metadata.ProjectFormat.PackageFormat);
            byte[] json = SerializeBounded(document, MaxMetadataBytes);
            var preparedCheckpoint = ToCheckpoint(document, finalPackage);
            cancellationToken.ThrowIfCancellationRequested();
            await ReclaimCleanHistoryAsync(plan, temporaryPackage, size, cancellationToken).ConfigureAwait(false);
            EnsureSafePath(finalPackage);
            File.Move(temporaryPackage, finalPackage);
            packageMoved = true;
            await WriteBytesAtomicallyAsync(Path.Combine(directory, basename + ".json"), json, cancellationToken).ConfigureAwait(false);
            // Atomic metadata publication is the commit point. Cancellation thereafter cannot undo it.
            committedCheckpoint = preparedCheckpoint;
            // The current session's recovery history is retained until the new checkpoint commits.
            foreach (var old in plan.CurrentCheckpoints)
            {
                EnsureSafePath(old.PackagePath);
                string oldMetadata = Path.ChangeExtension(old.PackagePath, ".json");
                EnsureSafePath(oldMetadata);
                // Same order as clean-history reclaim: never delete metadata first. If the package deletion is
                // denied (antivirus, backup handle) a valid pair remains instead of an unreclaimable orphan package.
                File.Delete(old.PackagePath);
                File.Delete(oldMetadata);
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (temporaryPackage is not null) Cleanup(() => DeleteOwnedFile(temporaryPackage));
            if (committedCheckpoint is null && packageMoved && finalPackage is not null)
                Cleanup(() => DeleteOwnedFile(finalPackage));
            Cleanup(() => plan?.Dispose());
            Cleanup(() => rootLock?.Dispose());
            if (entered) Cleanup(() => session.Writer.Release());
            Cleanup(session.EndOperation);
        }
        if (packageWarning is not null)
        {
            if (failure is null) failure = packageWarning;
            else AttachCleanupFailure(failure, packageWarning);
        }
        if (committedCheckpoint is not null)
        {
            if (failure is not null) throw new EditorRecoveryCommittedException(committedCheckpoint, failure);
            return committedCheckpoint;
        }
        ExceptionDispatchInfo.Capture(failure ?? new InvalidOperationException("Recovery did not commit a checkpoint.")).Throw();
        throw new InvalidOperationException("Unreachable recovery result.");

        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                if (failure is null) failure = error;
                else AttachCleanupFailure(failure, error);
            }
        }
    }

    public async Task MarkCleanAsync(EditorRecoverySession session, CancellationToken cancellationToken = default)
    {
        ValidateSessionOwner(session);
        cancellationToken.ThrowIfCancellationRequested();
        session.BeginOperation();
        bool entered = false;
        try
        {
            await session.Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            session.ThrowIfDisposed();
            await MarkCleanCoreAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
            session.IsClean = true;
        }
        finally
        {
            if (entered) session.Writer.Release();
            session.EndOperation();
        }
    }

    public async Task MarkInactiveSessionCleanAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = SessionDirectory(sessionId);
        EnsureSafePath(directory);
        using var lease = TryAcquireInactiveLease(directory)
            ?? throw new IOException("The recovery session is active or its lease is unavailable.");
        await MarkCleanCoreAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A later controlled exit finishes the automatic offer, not the interrupted history.
    /// The copies remain recoverable and ineligible for clean-history retention until explicitly resolved.</summary>
    internal async Task CompleteInactiveSessionStartupOfferAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = SessionDirectory(sessionId);
        EnsureSafePath(directory);
        using var lease = TryAcquireInactiveLease(directory)
            ?? throw new IOException("The recovery session is active or its lease is unavailable.");
        var document = await ReadSessionAsync(directory, sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The recovery session metadata is invalid.");
        if (!document.Clean && !document.StartupOfferCompleted)
            await WriteJsonAtomicallyAsync(Path.Combine(directory, SessionFileName), document with { StartupOfferCompleted = true },
                cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkCleanCoreAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        string directory = SessionDirectory(sessionId);
        var document = await ReadSessionAsync(directory, sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The recovery session metadata is invalid.");
        if (!document.Clean)
            await WriteJsonAtomicallyAsync(Path.Combine(directory, SessionFileName), document with { Clean = true },
                cancellationToken).ConfigureAwait(false);
    }

    private async Task<RetentionPlan> PlanRetentionAsync(Guid currentSessionId, List<EditorRecoveryCheckpoint> existing,
        long storedBytes, long newSize, CancellationToken cancellationToken)
    {
        if (newSize > retention.MaxTotalBytes)
            throw new InvalidOperationException("The new recovery package exceeds the recovery byte budget.");
        var plan = new RetentionPlan(existing.Skip(retention.MaxVersions - 1).ToList());
        try
        {
            long projected = checked(storedBytes + newSize - plan.CurrentCheckpoints.Sum(x => x.PackageSizeBytes));
            if (projected > retention.MaxTotalBytes)
            {
                // The caller already owns store.lock. Never acquire it recursively here.
                foreach (string directory in Directory.EnumerateDirectories(RootDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryDirectoryId(directory, out Guid id) || id == currentSessionId) continue;
                    FileStream? lease = null;
                    try
                    {
                        EnsureSafePath(directory);
                        lease = TryAcquireInactiveLease(directory);
                        if (lease is null) continue;
                        var session = await ReadSessionAsync(directory, id, cancellationToken).ConfigureAwait(false);
                        if (session is null || !session.Clean) continue;
                        // We hold this clean, inactive session's lease, so no writer can be staging in it. A staging
                        // package left by a killed writer is never published; remove it so it stops counting toward the
                        // byte budget and stops making the session unreclaimable. Unclean (interrupted) sessions and
                        // published pairs are left untouched for recovery.
                        // Freed bytes count against this save's projection directly; the session may have no
                        // checkpoints to remove afterwards, so the deletion itself must lower the total.
                        projected -= RemoveAbandonedStagingPackages(directory);
                        // Other sessions' packages are verified before they may be deleted: a damaged package is never
                        // reclaimed for someone else's budget. This runs only when the budget is actually exceeded.
                        var points = await ReadCheckpointsAsync(directory, id, cancellationToken).ConfigureAwait(false);
                        // ReadCheckpoints deliberately skips damaged entries. A clean session with
                        // any damaged, orphaned, staging, foreign, or reparse entry is not ours to prune.
                        if (points.Count == 0 || !HasOnlyValidatedCheckpointFiles(directory, points)) continue;
                        plan.CleanSessions.Add(new(session, directory, points, lease));
                        lease = null;
                    }
                    catch (Exception exception) when (IsInvalidEntry(exception)) { }
                    finally { lease?.Dispose(); }
                }
                foreach (var candidate in plan.CleanSessions.SelectMany(session => session.Checkpoints.Select(point => (session, point)))
                    .OrderBy(x => x.point.CreatedUtc).ThenBy(x => x.point.SessionId).ThenBy(x => x.point.CheckpointId))
                {
                    if (projected <= retention.MaxTotalBytes) break;
                    candidate.session.Remove.Add(candidate.point);
                    projected -= candidate.point.PackageSizeBytes;
                }
            }
            foreach (var point in existing.AsEnumerable().Reverse())
            {
                if (projected <= retention.MaxTotalBytes) break;
                if (plan.CurrentCheckpoints.Any(x => x.CheckpointId == point.CheckpointId)) continue;
                plan.CurrentCheckpoints.Add(point);
                projected -= point.PackageSizeBytes;
            }
            if (projected > retention.MaxTotalBytes)
                throw new InvalidOperationException("The recovery byte budget is occupied by other sessions or retained damaged packages.");
            foreach (var unused in plan.CleanSessions.Where(x => x.Remove.Count == 0).ToArray())
            {
                unused.Dispose();
                plan.CleanSessions.Remove(unused);
            }
            return plan;
        }
        catch (Exception error)
        {
            try { plan.Dispose(); }
            catch (Exception cleanup) { AttachCleanupFailure(error, cleanup); }
            throw;
        }
    }

    /// <summary>Returns the bytes actually freed.</summary>
    private long RemoveAbandonedStagingPackages(string directory)
    {
        long freed = 0;
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string filename = Path.GetFileName(path);
            string extension = Path.GetExtension(filename);
            if (!extension.Equals(".pma", PathComparison) && !extension.Equals(".pmc", PathComparison)) continue;
            string stem = Path.GetFileNameWithoutExtension(filename);
            if (!stem.EndsWith(".tmp", StringComparison.Ordinal) || !IsCanonicalGuid(stem[..^4], out _)) continue;
            EnsureSafePath(path);
            try { long length = new FileInfo(path).Length; File.Delete(path); freed += length; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return freed;
    }

    private bool HasOnlyValidatedCheckpointFiles(string directory, List<EditorRecoveryCheckpoint> checkpoints)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal) { SessionFileName, LeaseFileName };
        foreach (var point in checkpoints)
        {
            EnsureOwnedCheckpointPaths(point, out string metadataPath);
            expected.Add(Path.GetFileName(point.PackagePath));
            expected.Add(Path.GetFileName(metadataPath));
        }
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            EnsureSafePath(path);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0 || !expected.Remove(Path.GetFileName(path)))
                return false;
        }
        return expected.Count == 0;
    }

    private async Task ReclaimCleanHistoryAsync(RetentionPlan plan, string stagingPackage, long newSize,
        CancellationToken cancellationToken)
    {
        if (plan.CleanSessions.Count == 0) return;
        // This maintenance only concerns explicitly closed/ignored sessions. It runs after the
        // new package is validated, with store.lock and all inactive leases still held. A subsequent
        // publication failure may leave closed history pruned, but never prunes current history.
        foreach (var clean in plan.CleanSessions)
            await ValidateCleanRetentionSessionAsync(clean, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var old in plan.CleanSessions.SelectMany(x => x.Remove).OrderBy(x => x.CreatedUtc)
            .ThenBy(x => x.SessionId).ThenBy(x => x.CheckpointId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOwnedCheckpointPaths(old, out string oldMetadata);
            // Never delete metadata first: a denied package deletion must leave a valid pair.
            File.Delete(old.PackagePath);
            EnsureSafePath(oldMetadata);
            // If this fails, keep the orphan metadata as evidence and abort publication. The
            // next scan conservatively rejects that session; it must not guess who removed a PMA.
            File.Delete(oldMetadata);
        }
        // Count bytes actually removed, not just the planned reclaim. Failed cleanup cannot fund
        // a new checkpoint or make repeated saves silently exceed the persistent byte budget.
        long projected = checked(CountStoredPackageBytes(stagingPackage, cancellationToken) + newSize
            - plan.CurrentCheckpoints.Sum(x => x.PackageSizeBytes));
        if (projected > retention.MaxTotalBytes)
            throw new InvalidOperationException("The recovery byte budget could not be reclaimed from closed sessions.");
    }

    private async Task ValidateCleanRetentionSessionAsync(CleanRetentionSession candidate, CancellationToken cancellationToken)
    {
        EnsureSafePath(candidate.Directory);
        var session = await ReadSessionAsync(candidate.Directory, candidate.Session.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || session != candidate.Session || !session.Clean)
            throw new InvalidDataException("The clean recovery session changed before retention cleanup.");
        var points = await ReadCheckpointsAsync(candidate.Directory, session.SessionId, cancellationToken).ConfigureAwait(false);
        if (!HasOnlyValidatedCheckpointFiles(candidate.Directory, points) || points.Count != candidate.Checkpoints.Count
            || candidate.Checkpoints.Any(old => !points.Any(current => current.CheckpointId == old.CheckpointId
                && current.CreatedUtc == old.CreatedUtc && current.PackageSizeBytes == old.PackageSizeBytes
                && string.Equals(current.Sha256, old.Sha256, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("The clean recovery checkpoints changed before retention cleanup.");
    }

    private void EnsureOwnedCheckpointPaths(EditorRecoveryCheckpoint point, out string metadataPath)
    {
        string expected = Path.Combine(SessionDirectory(point.SessionId), point.CheckpointId.ToString("N") + point.ProjectFormat.Extension);
        if (!point.PackagePath.Equals(expected, PathComparison))
            throw new IOException("The recovery checkpoint does not belong to its session directory.");
        EnsureSafePath(expected);
        metadataPath = Path.ChangeExtension(expected, ".json");
        EnsureSafePath(metadataPath);
    }

    private long CountStoredPackageBytes(string currentStagingPackage, CancellationToken cancellationToken)
    {
        long bytes = 0;
        foreach (string directory in Directory.EnumerateDirectories(RootDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryDirectoryId(directory, out _)) continue;
            EnsureSafePath(directory);
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string filename = Path.GetFileName(path);
                string extension = Path.GetExtension(filename);
                if (!extension.Equals(".pma", PathComparison) && !extension.Equals(".pmc", PathComparison)) continue;
                string stem = Path.GetFileNameWithoutExtension(filename);
                string identity = stem.EndsWith(".tmp", StringComparison.Ordinal) ? stem[..^4] : stem;
                if (!IsCanonicalGuid(identity, out _)) continue;
                EnsureSafePath(path);
                if (path.Equals(currentStagingPackage, PathComparison)) continue;
                bytes = checked(bytes + new FileInfo(path).Length);
            }
        }
        return bytes;
    }

    /// <param name="verifyDigests">Re-hash every package. Listing, restoring and reclaiming another session's history
    /// do. Planning the current session's own retention, which runs on every autosave while holding the root lock,
    /// needs only the recorded size of its own older checkpoints, so it skips re-reading each of them.</param>
    private async Task<List<EditorRecoveryCheckpoint>> ReadCheckpointsAsync(string directory, Guid sessionId,
        CancellationToken cancellationToken, bool verifyDigests = true)
    {
        var points = new List<EditorRecoveryCheckpoint>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCanonicalGuid(Path.GetFileNameWithoutExtension(path), out Guid checkpointId)) continue;
            try
            {
                var document = await ReadJsonAsync<CheckpointDocument>(path, MaxMetadataBytes, cancellationToken).ConfigureAwait(false);
                if (document is null || document.SessionId != sessionId
                    || document.CheckpointId != checkpointId || document.CreatedUtc == default
                    || document.PackageSizeBytes < 1
                    || document.Sha256 is null || document.Sha256.Length != 64 || !document.Sha256.All(Uri.IsHexDigit)) continue;
                EditorProjectFormat format = CheckpointFormat(document);
                if (document.PackageFileName != checkpointId.ToString("N") + format.Extension) continue;
                ValidateNames(document.OriginalProjectPath, document.DisplayName);
                ValidateState(document.ContentRevision, document.EditorState, document.EditorStateSchemaVersion);
                string packagePath = Path.Combine(directory, document.PackageFileName);
                EnsureSafePath(packagePath);
                using var package = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (package.Length != document.PackageSizeBytes) continue;
                if (verifyDigests)
                {
                    string hash = Convert.ToHexString(await SHA256.HashDataAsync(package, cancellationToken).ConfigureAwait(false));
                    if (!string.Equals(hash, document.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                }
                points.Add(ToCheckpoint(document, packagePath));
            }
            catch (Exception exception) when (IsInvalidEntry(exception)) { }
        }
        return points.OrderByDescending(x => x.CreatedUtc).ThenByDescending(x => x.CheckpointId).ToList();
    }

    private async Task<SessionDocument?> ReadSessionAsync(string directory, Guid sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var document = await ReadJsonAsync<SessionDocument>(Path.Combine(directory, SessionFileName), MaxSessionMetadataBytes,
                cancellationToken).ConfigureAwait(false);
            if (document is null || document.SchemaVersion != SchemaVersion || document.SessionId != sessionId
                || document.CreatedUtc == default) return null;
            ValidateNames(document.OriginalProjectPath, document.DisplayName);
            return document;
        }
        catch (Exception exception) when (IsInvalidEntry(exception)) { return null; }
    }

    private static EditorRecoveryCheckpoint ToCheckpoint(CheckpointDocument document, string packagePath) => new(
        document.SessionId, document.CheckpointId, document.CreatedUtc, packagePath, document.PackageSizeBytes, document.Sha256,
        document.OriginalProjectPath, document.DisplayName, document.ContentRevision, document.EditorState.Clone(),
        document.EditorStateSchemaVersion, CheckpointFormat(document));

    private static EditorProjectFormat CheckpointFormat(CheckpointDocument document) => document.SchemaVersion switch
    {
        // Old history had no engine identity and only ever wrote the canonical PMA suffix.
        SchemaVersion when document.ProjectFormat is null => EditorProjectFormat.Modern,
        CheckpointSchemaVersion when document.ProjectFormat is not null => EditorProjectFormat.FromPackageFormat(document.ProjectFormat),
        _ => throw new InvalidDataException("The recovery checkpoint format or schema is unsupported.")
    };

    private async Task<T?> ReadJsonAsync<T>(string path, int maxBytes, CancellationToken cancellationToken)
    {
        EnsureSafePath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
        if (stream.Length > maxBytes) throw new InvalidDataException("The recovery metadata exceeds its size limit.");
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private Task WriteJsonAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        WriteBytesAtomicallyAsync(path, SerializeBounded(value, MaxSessionMetadataBytes), cancellationToken);

    private async Task WriteBytesAtomicallyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        EnsureSafePath(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafePath(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDeleteOwnedFile(temporary); }
    }

    private async Task<FileStream> AcquireRootLockAsync(CancellationToken cancellationToken)
    {
        EnsureSafePath(RootDirectory);
        Directory.CreateDirectory(RootDirectory);
        string path = Path.Combine(RootDirectory, "store.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafePath(path);
            try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
            { await Task.Delay(25, cancellationToken).ConfigureAwait(false); }
        }
    }

    private FileStream? TryAcquireInactiveLease(string directory)
    {
        string path = Path.Combine(directory, LeaseFileName);
        EnsureSafePath(path);
        try { return new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private string SessionDirectory(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A recovery session ID cannot be empty.", nameof(id));
        return Path.Combine(RootDirectory, id.ToString("N"));
    }

    private void ValidateSessionOwner(EditorRecoverySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(session.Owner, this)) throw new ArgumentException("The recovery session belongs to another store.", nameof(session));
    }

    private static void ValidateMetadata(EditorRecoveryCheckpointMetadata metadata)
    {
        ValidateNames(metadata.OriginalProjectPath, metadata.DisplayName);
        ValidateState(metadata.ContentRevision, metadata.EditorState, metadata.EditorStateSchemaVersion);
    }

    private static void ValidateNames(string? originalProjectPath, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (displayName.Length > 4096 || originalProjectPath?.Length > 32768)
            throw new ArgumentException("The recovery project name or original path is too long.");
    }

    private static void ValidateState(long revision, JsonElement state, int schema)
    {
        if (revision < 0 || schema != 1 || state.ValueKind == JsonValueKind.Undefined
            || Encoding.UTF8.GetByteCount(state.GetRawText()) > MaxEditorStateBytes
            || JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions).Length > MaxEditorStateBytes)
            throw new ArgumentException("The recovery editor state has an unsupported version, revision, or size.");
    }

    private static byte[] SerializeBounded<T>(T value, int maxBytes)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > maxBytes) throw new ArgumentException("The recovery metadata exceeds its size limit.");
        return bytes;
    }

    private void EnsureSafePath(string path)
    {
        string full = Path.GetFullPath(path);
        string prefix = Path.EndsInDirectorySeparator(RootDirectory) ? RootDirectory : RootDirectory + Path.DirectorySeparatorChar;
        if (!full.Equals(RootDirectory, PathComparison) && !full.StartsWith(prefix, PathComparison))
            throw new IOException("The recovery path is outside its root directory.");
        RejectReparseAncestors(full);
    }

    private static void RejectReparseAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Recovery storage cannot follow symbolic links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private void TryDeleteOwnedFile(string path)
    {
        try { DeleteOwnedFile(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void DeleteOwnedFile(string path)
    {
        EnsureSafePath(path);
        File.Delete(path);
    }

    private static void AttachCleanupFailure(Exception primary, Exception cleanup)
    {
        const string key = "Recovery.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        errors.Add(cleanup);
    }

    private static bool TryDirectoryId(string directory, out Guid id) => IsCanonicalGuid(Path.GetFileName(directory), out id);
    private static bool IsCanonicalGuid(string name, out Guid id) => Guid.TryParseExact(name, "N", out id)
        && id != Guid.Empty && name == id.ToString("N");
    private static bool IsInvalidEntry(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or ArgumentException or NotSupportedException or OverflowException;

    private sealed record SessionDocument(int SchemaVersion, Guid SessionId, DateTimeOffset CreatedUtc,
        string? OriginalProjectPath, string DisplayName, bool Clean, bool StartupOfferCompleted = false);
    private sealed record CheckpointDocument(int SchemaVersion, Guid SessionId, Guid CheckpointId, DateTimeOffset CreatedUtc,
        string PackageFileName, long PackageSizeBytes, string Sha256, string? OriginalProjectPath, string DisplayName,
        long ContentRevision, JsonElement EditorState, int EditorStateSchemaVersion, string? ProjectFormat = null);

    private sealed class RetentionPlan(List<EditorRecoveryCheckpoint> currentCheckpoints) : IDisposable
    {
        public List<EditorRecoveryCheckpoint> CurrentCheckpoints { get; } = currentCheckpoints;
        public List<CleanRetentionSession> CleanSessions { get; } = [];
        public void Dispose()
        {
            foreach (var session in CleanSessions) session.Dispose();
        }
    }

    private sealed class CleanRetentionSession(SessionDocument session, string directory,
        List<EditorRecoveryCheckpoint> checkpoints, FileStream lease) : IDisposable
    {
        public SessionDocument Session { get; } = session;
        public string Directory { get; } = directory;
        public List<EditorRecoveryCheckpoint> Checkpoints { get; } = checkpoints;
        public List<EditorRecoveryCheckpoint> Remove { get; } = [];
        public void Dispose() => lease.Dispose();
    }
}
