using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorRecoveryStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mda-recovery-store-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnnamedProjectCreatesIndependentVersionedHistoryWithoutTouchingTheOriginal()
    {
        string original = Path.Combine(directory, "user project.pma");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(original, "original", Token);
        var store = Store();
        using var session = await store.CreateSessionAsync(null, "Untitled", Token);
        var first = await Write(store, session, 4, "first");
        var second = await Write(store, session, 5, "second");
        Assert.NotEqual(first.CheckpointId, second.CheckpointId);
        Assert.Equal("original", await File.ReadAllTextAsync(original, Token));
        Assert.Null(second.OriginalProjectPath);
        Assert.Equal(1, second.EditorStateSchemaVersion);
        Assert.Equal(5, second.ContentRevision);
        Assert.Equal(12, second.EditorState.GetProperty("frame").GetInt32());
        session.Dispose();
        var recovered = Assert.Single(await store.ListRecoverableAsync(Token));
        Assert.Equal(session.SessionId, recovered.SessionId);
        Assert.Equal(new[] { second.CheckpointId, first.CheckpointId }, recovered.Checkpoints.Select(x => x.CheckpointId));
        Assert.Equal(second.CheckpointId, (await store.GetLatestValidCheckpointAsync(session.SessionId, Token))!.CheckpointId);
    }

    [Fact]
    public async Task MultipleActiveOsLeasesAreSkippedUntilReleasedAndExplicitCleanExitPreservesHistory()
    {
        var store = Store();
        using var one = await store.CreateSessionAsync(null, "One", Token);
        using var two = await store.CreateSessionAsync(null, "Two", Token);
        var checkpoint = await Write(store, one, 1, "one");
        await Write(store, two, 1, "two");
        Assert.Empty(await new EditorRecoveryStore(store.RootDirectory).ListRecoverableAsync(Token));
        await Assert.ThrowsAsync<IOException>(() => store.MarkInactiveSessionCleanAsync(one.SessionId, Token));
        await store.MarkCleanAsync(one, Token);
        one.Dispose(); two.Dispose();
        Assert.Equal(two.SessionId, Assert.Single(await store.ListRecoverableAsync(Token)).SessionId);
        Assert.True(File.Exists(checkpoint.PackagePath));
        await store.MarkInactiveSessionCleanAsync(two.SessionId, Token);
        Assert.Empty(await store.ListRecoverableAsync(Token));
    }

    [Fact]
    public async Task CompletingAnAutomaticOfferPreservesUncleanHistoryAndDoesNotHideANewInterruptedSession()
    {
        var store = Store();
        using var old = await store.CreateSessionAsync(null, "Earlier interrupted run", Token);
        var checkpoint = await Write(store, old, 1, "retained");
        await Assert.ThrowsAsync<IOException>(() => store.CompleteInactiveSessionStartupOfferAsync(old.SessionId, Token));
        old.Dispose();
        await store.CompleteInactiveSessionStartupOfferAsync(old.SessionId, Token);
        Assert.Equal(old.SessionId, Assert.Single(await store.ListRecoverableAsync(Token)).SessionId);
        Assert.Empty(await store.ListStartupRecoverableAsync(Token));
        Assert.Equal(checkpoint.CheckpointId, (await store.GetLatestValidCheckpointAsync(old.SessionId, Token))!.CheckpointId);
        Assert.Equal("retained", await File.ReadAllTextAsync(checkpoint.PackagePath, Token));
        using var interrupted = await store.CreateSessionAsync(null, "New interrupted run", Token);
        await Write(store, interrupted, 2, "new");
        Assert.Empty(await store.ListStartupRecoverableAsync(Token));
        interrupted.Dispose();
        Assert.Equal(interrupted.SessionId, Assert.Single(await store.ListStartupRecoverableAsync(Token)).SessionId);
        Assert.Equal(2, (await store.ListRecoverableAsync(Token)).Count);
    }

    [Fact]
    public async Task WriterFailureLeavesTheLastValidatedPackageAndRemovesOnlyItsOwnPartialFile()
    {
        var store = Store();
        using var session = await store.CreateSessionAsync("Z:/never/read/or/delete.pma", "Project", Token);
        var good = await Write(store, session, 1, "good");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteCheckpointAsync(session, Metadata(2), async (path, token) =>
        {
            await File.WriteAllTextAsync(path, "partial", token);
            throw new InvalidDataException("PMA validation failed");
        }, Token));
        Assert.Equal("good", await File.ReadAllTextAsync(good.PackagePath, Token));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(good.PackagePath)!, "*.pma"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(good.PackagePath)!, "*.tmp"));
        session.Dispose();
        Assert.Equal(good.CheckpointId, (await store.GetLatestValidCheckpointAsync(session.SessionId, Token))!.CheckpointId);
    }

    [Fact]
    public async Task CancellationAfterPartialWritePreservesPreviousPointAndDoesNotPublishMetadata()
    {
        var store = Store();
        using var session = await store.CreateSessionAsync(null, "Project", Token);
        var good = await Write(store, session, 1, "good");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteCheckpointAsync(session, Metadata(2), async (path, token) =>
        {
            await File.WriteAllTextAsync(path, "partial", token);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        }, cancellation.Token));
        session.Dispose();
        var recovered = Assert.Single(await store.ListRecoverableAsync(Token));
        Assert.Equal(good.CheckpointId, Assert.Single(recovered.Checkpoints).CheckpointId);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(good.PackagePath)!, "*.pma"));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("path")]
    [InlineData("schema")]
    public async Task DamagedLatestCheckpointIsRejectedAndAnOlderGoodPointRemainsRecoverable(string damage)
    {
        var store = Store();
        using var session = await store.CreateSessionAsync(null, "Project", Token);
        var old = await Write(store, session, 1, "old");
        var latest = await Write(store, session, 2, "new");
        session.Dispose();
        string metadataPath = Path.ChangeExtension(latest.PackagePath, ".json");
        if (damage == "hash") await File.WriteAllTextAsync(latest.PackagePath, "bad", Token);
        else
        {
            var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath, Token))!.AsObject();
            if (damage == "size") metadata["packageSizeBytes"] = latest.PackageSizeBytes + 1;
            if (damage == "path") metadata["packageFileName"] = "../outside.pma";
            if (damage == "schema") metadata["schemaVersion"] = 99;
            await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString(), Token);
        }
        var recoverable = Assert.Single(await store.ListRecoverableAsync(Token));
        Assert.Equal(old.CheckpointId, Assert.Single(recoverable.Checkpoints).CheckpointId);
        Assert.Equal(old.CheckpointId, (await store.GetLatestValidCheckpointAsync(session.SessionId, Token))!.CheckpointId);
        Assert.True(File.Exists(latest.PackagePath)); // Rejection is read-only; damaged evidence is retained.
    }

    [Fact]
    public async Task OneMalformedSessionCannotPreventScanningOtherSessions()
    {
        var store = Store();
        using var bad = await store.CreateSessionAsync(null, "Bad", Token);
        var badPoint = await Write(store, bad, 1, "bad");
        using var good = await store.CreateSessionAsync(null, "Good", Token);
        await Write(store, good, 1, "good");
        bad.Dispose(); good.Dispose();
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(badPoint.PackagePath)!, "session.json"), "{broken", Token);
        Assert.Equal(good.SessionId, Assert.Single(await store.ListRecoverableAsync(Token)).SessionId);
    }

    [Fact]
    public async Task RetentionPublishesNewVersionBeforeRemovingOnlyItsOwnOldVersions()
    {
        var store = Store(new RecoveryRetentionPolicy(2, 1024));
        using var session = await store.CreateSessionAsync(null, "Project", Token);
        var oldest = await Write(store, session, 1, "one");
        string unrelated = Path.Combine(Path.GetDirectoryName(oldest.PackagePath)!, "user-file.pma");
        await File.WriteAllTextAsync(unrelated, "keep", Token);
        var middle = await Write(store, session, 2, "two");
        var latest = await Write(store, session, 3, "three");
        Assert.False(File.Exists(oldest.PackagePath));
        Assert.True(File.Exists(middle.PackagePath)); Assert.True(File.Exists(latest.PackagePath));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated, Token));
        session.Dispose();
        Assert.Equal(2, Assert.Single(await store.ListRecoverableAsync(Token)).Checkpoints.Count);
    }

    [Fact]
    public async Task OversizedNewPointFailsWithoutDeletingTheOnlyGoodPoint()
    {
        var store = Store(new RecoveryRetentionPolicy(5, 6));
        using var session = await store.CreateSessionAsync(null, "Project", Token);
        var good = await Write(store, session, 1, "good");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Write(store, session, 2, "too large"));
        Assert.Equal("good", await File.ReadAllTextAsync(good.PackagePath, Token));
        session.Dispose();
        Assert.Equal(good.CheckpointId, Assert.Single(Assert.Single(await store.ListRecoverableAsync(Token)).Checkpoints).CheckpointId);
    }

    [Fact]
    public async Task ByteBudgetRotatesCurrentHistoryButNeverDeletesAnotherUnrecoveredSession()
    {
        var store = Store(new RecoveryRetentionPolicy(5, 10));
        using var other = await store.CreateSessionAsync(null, "Other", Token);
        var otherPoint = await Write(store, other, 1, "123456");
        other.Dispose();
        using var current = await store.CreateSessionAsync(null, "Current", Token);
        var first = await Write(store, current, 1, "1234");
        var next = await Write(store, current, 2, "5678");
        Assert.False(File.Exists(first.PackagePath)); Assert.True(File.Exists(otherPoint.PackagePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Write(store, current, 3, "12345"));
        Assert.True(File.Exists(next.PackagePath)); Assert.True(File.Exists(otherPoint.PackagePath));
    }

    [Fact]
    public async Task ConcurrentWritesForOneSessionRunSeriallyAndCleanExitRejectsFurtherWrites()
    {
        var store = Store();
        using var session = await store.CreateSessionAsync(null, "Project", Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var one = store.WriteCheckpointAsync(session, Metadata(1), async (path, token) =>
        {
            Interlocked.Increment(ref calls); entered.SetResult();
            await release.Task.WaitAsync(token);
            await File.WriteAllTextAsync(path, "one", token);
        }, Token);
        await entered.Task.WaitAsync(Token);
        var two = store.WriteCheckpointAsync(session, Metadata(2), async (path, token) =>
        {
            Interlocked.Increment(ref calls);
            await File.WriteAllTextAsync(path, "two", token);
        }, Token);
        Assert.Equal(1, Volatile.Read(ref calls));
        release.SetResult();
        await Task.WhenAll(one, two);
        Assert.Equal(2, calls);
        await store.MarkCleanAsync(session, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Write(store, session, 3, "three"));
    }

    [Fact]
    public async Task EditorStateIsDetachedAndBoundedBeforeThePackageWriterRuns()
    {
        var store = Store();
        using var session = await store.CreateSessionAsync(null, "Project", Token);
        EditorRecoveryCheckpointMetadata metadata;
        using (var document = JsonDocument.Parse("{\"frame\":42}"))
            metadata = new(null, "Project", 1, document.RootElement);
        var checkpoint = await store.WriteCheckpointAsync(session, metadata,
            (path, token) => File.WriteAllTextAsync(path, "good", token), Token);
        Assert.Equal(42, checkpoint.EditorState.GetProperty("frame").GetInt32());
        using var oversized = JsonDocument.Parse("{\"draft\":\"" + new string('x', EditorRecoveryStore.MaxEditorStateBytes) + "\"}");
        bool called = false;
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteCheckpointAsync(session,
            new(null, "Project", 2, oversized.RootElement), (_, _) => { called = true; return Task.CompletedTask; }, Token));
        Assert.False(called);
    }

    [Fact]
    public async Task DisposedOrForeignSessionsCannotWriteAndPreCancelledWriteNeverInvokesWriter()
    {
        var store = Store();
        using var session = await store.CreateSessionAsync(null, "Project", Token);
        var foreign = new EditorRecoveryStore(Path.Combine(directory, "other"));
        await Assert.ThrowsAsync<ArgumentException>(() => Write(foreign, session, 1, "foreign"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool called = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteCheckpointAsync(session, Metadata(1),
            (_, _) => { called = true; return Task.CompletedTask; }, cancellation.Token));
        Assert.False(called);
        session.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Write(store, session, 1, "disposed"));
    }

    [Fact]
    public async Task CancellationImmediatelyAfterWriterReturnsDoesNotPublishOrRotateHistory()
    {
        var store = Store(new RecoveryRetentionPolicy(1, 1024));
        using var session = await store.CreateSessionAsync(null, "Project", Token);
        var good = await Write(store, session, 1, "good");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteCheckpointAsync(session, Metadata(2), async (path, token) =>
        {
            await File.WriteAllTextAsync(path, "new", token);
            cancellation.Cancel();
        }, cancellation.Token));
        Assert.True(File.Exists(good.PackagePath));
        session.Dispose();
        Assert.Equal(good.CheckpointId, Assert.Single(Assert.Single(await store.ListRecoverableAsync(Token)).Checkpoints).CheckpointId);
    }

    [Fact]
    public async Task DisposingAnActiveWriterDoesNotReleaseItsOsLeaseBeforeTheTransactionFinishes()
    {
        var store = Store();
        using var session = await store.CreateSessionAsync(null, "Project", Token);
        await Write(store, session, 1, "old");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = store.WriteCheckpointAsync(session, Metadata(2), async (path, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            await File.WriteAllTextAsync(path, "new", token);
        }, Token);
        await entered.Task.WaitAsync(Token);
        session.Dispose();
        var anotherInstance = new EditorRecoveryStore(store.RootDirectory);
        try { Assert.Empty(await anotherInstance.ListRecoverableAsync(Token)); }
        finally { release.TrySetResult(); }
        var latest = await writing;
        Assert.Equal(latest.CheckpointId,
            Assert.Single(Assert.Single(await anotherInstance.ListRecoverableAsync(Token)).Checkpoints, x => x.ContentRevision == 2).CheckpointId);
    }

    [Fact]
    public async Task IndependentStoreInstancesSerializeGlobalBudgetPublication()
    {
        var one = Store(new RecoveryRetentionPolicy(5, 6));
        var two = new EditorRecoveryStore(one.RootDirectory, new RecoveryRetentionPolicy(5, 6));
        using var firstSession = await one.CreateSessionAsync(null, "One", Token);
        using var secondSession = await two.CreateSessionAsync(null, "Two", Token);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        async Task Writer(string path, CancellationToken token)
        {
            if (Interlocked.Increment(ref entered) == 1) ready.SetResult();
            await release.Task.WaitAsync(token);
            await File.WriteAllTextAsync(path, "four", token);
        }
        async Task<bool> Save(EditorRecoveryStore store, EditorRecoverySession session)
        {
            try { await store.WriteCheckpointAsync(session, Metadata(1), Writer, Token); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var first = Save(one, firstSession);
        await ready.Task.WaitAsync(Token);
        var second = Save(two, secondSession);
        Assert.Equal(1, Volatile.Read(ref entered));
        release.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, x => x);
        firstSession.Dispose(); secondSession.Dispose();
        var point = Assert.Single(Assert.Single(await one.ListRecoverableAsync(Token)).Checkpoints);
        Assert.Equal(4, point.PackageSizeBytes);
    }

    [Fact]
    public async Task StagingUsesPmaExtensionAndAnAbandonedStagingPackageConsumesTheGlobalBudget()
    {
        var store = Store(new RecoveryRetentionPolicy(5, 6));
        using var abandoned = await store.CreateSessionAsync(null, "Abandoned", Token);
        string abandonedDirectory = Path.Combine(store.RootDirectory, abandoned.SessionId.ToString("N"));
        string staging = Path.Combine(abandonedDirectory, Guid.NewGuid().ToString("N") + ".tmp.pma");
        await File.WriteAllTextAsync(staging, "four", Token);
        abandoned.Dispose();
        using var session = await store.CreateSessionAsync(null, "Current", Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteCheckpointAsync(session, Metadata(1), async (path, token) =>
        {
            Assert.Equal(".pma", Path.GetExtension(path));
            await File.WriteAllTextAsync(path, "new", token);
        }, Token));
        Assert.Equal("four", await File.ReadAllTextAsync(staging, Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(store.RootDirectory, session.SessionId.ToString("N")), "*.pma"));
    }

    [Fact]
    public async Task AFinalPackageNameCollisionDoesNotDeleteTheFileThatBlockedPublication()
    {
        var store = Store();
        using var session = await store.CreateSessionAsync(null, "Project", Token);
        string? collision = null;
        await Assert.ThrowsAsync<IOException>(() => store.WriteCheckpointAsync(session, Metadata(1), async (path, token) =>
        {
            collision = path[..^8] + ".pma";
            await File.WriteAllTextAsync(collision, "unrelated", token);
            await File.WriteAllTextAsync(path, "new", token);
        }, Token));
        Assert.NotNull(collision);
        Assert.Equal("unrelated", await File.ReadAllTextAsync(collision, Token));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(collision)!, "*.tmp.pma"));
    }

    [Fact]
    public async Task MetadataPublicationFailureAfterPackageRenameRetainsTheLastGoodPoint()
    {
        var store = Store(new RecoveryRetentionPolicy(1, 1024));
        using var session = await store.CreateSessionAsync(null, "Project", Token);
        var old = await Write(store, session, 1, "old");
        string? blockedMetadata = null;
        var failure = await Record.ExceptionAsync(() => store.WriteCheckpointAsync(session, Metadata(2), async (path, token) =>
        {
            blockedMetadata = path[..^8] + ".json";
            Directory.CreateDirectory(blockedMetadata);
            await File.WriteAllTextAsync(path, "new", token);
        }, Token));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal("old", await File.ReadAllTextAsync(old.PackagePath, Token));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(old.PackagePath)!, "*.pma"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(old.PackagePath)!, "*.tmp"));
        Assert.True(Directory.Exists(blockedMetadata));
        session.Dispose();
        Assert.Equal(old.CheckpointId, (await store.GetLatestValidCheckpointAsync(session.SessionId, Token))!.CheckpointId);
    }

    [Fact]
    public async Task CancellationWhileWaitingForTheRootLeaseDoesNotInvokeTheWriter()
    {
        var store = Store();
        using var session = await store.CreateSessionAsync(null, "Project", Token);
        using var held = new FileStream(Path.Combine(store.RootDirectory, "store.lock"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        bool called = false;
        var pending = store.WriteCheckpointAsync(session, Metadata(1), (_, _) =>
        {
            called = true;
            return Task.CompletedTask;
        }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(called);
    }

    [Fact]
    public async Task AValidJsonDocumentCannotRedirectARecoveryReadOutsideTheSessionDirectory()
    {
        var store = Store();
        using var session = await store.CreateSessionAsync(null, "Project", Token);
        var point = await Write(store, session, 1, "keep");
        string outside = Path.Combine(directory, "outside.pma");
        await File.WriteAllTextAsync(outside, "keep", Token);
        session.Dispose();
        string metadataPath = Path.ChangeExtension(point.PackagePath, ".json");
        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath, Token))!.AsObject();
        metadata["packageFileName"] = outside;
        await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString(), Token);
        Assert.Empty(await store.ListRecoverableAsync(Token));
        Assert.Null(await store.GetLatestValidCheckpointAsync(session.SessionId, Token));
        Assert.Equal("keep", await File.ReadAllTextAsync(outside, Token));
    }

    [Fact]
    public async Task ReparseRootAndReparseCheckpointAreRejectedWithoutFollowingTheirTargets()
    {
        Directory.CreateDirectory(directory);
        string actualRoot = Path.Combine(directory, "actual");
        string linkedRoot = Path.Combine(directory, "linked");
        Directory.CreateDirectory(actualRoot);
        try { Directory.CreateSymbolicLink(linkedRoot, actualRoot); }
        catch (UnauthorizedAccessException) { Assert.Skip("Creating a symbolic link requires OS permission."); return; }
        try { Assert.Throws<IOException>(() => new EditorRecoveryStore(linkedRoot)); }
        finally { Directory.Delete(linkedRoot); }

        var store = Store();
        using var session = await store.CreateSessionAsync(null, "Project", Token);
        var point = await Write(store, session, 1, "safe");
        session.Dispose();
        string outside = Path.Combine(directory, "outside.pma");
        File.Move(point.PackagePath, outside);
        File.CreateSymbolicLink(point.PackagePath, outside);
        try
        {
            Assert.Empty(await store.ListRecoverableAsync(Token));
            Assert.Equal("safe", await File.ReadAllTextAsync(outside, Token));
        }
        finally { File.Delete(point.PackagePath); }
    }

    [Fact]
    public async Task MalformedOversizedAndUnsupportedEditorStateMetadataIsReadOnlyAndDoesNotHideGoodHistory()
    {
        var store = Store();
        using var session = await store.CreateSessionAsync(null, "Project", Token);
        var old = await Write(store, session, 1, "old");
        var newest = await Write(store, session, 2, "new");
        session.Dispose();
        string metadataPath = Path.ChangeExtension(newest.PackagePath, ".json");
        string metadataText = await File.ReadAllTextAsync(metadataPath, Token);
        var metadata = JsonNode.Parse(metadataText)!.AsObject();
        metadata["editorStateSchemaVersion"] = 123;
        await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString(), Token);
        Assert.Equal(old.CheckpointId, (await store.GetLatestValidCheckpointAsync(session.SessionId, Token))!.CheckpointId);
        metadata = JsonNode.Parse(metadataText)!.AsObject();
        metadata["editorState"] = JsonSerializer.SerializeToNode(new { text = new string('x', EditorRecoveryStore.MaxEditorStateBytes) });
        await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString(), Token);
        Assert.Equal(old.CheckpointId, (await store.GetLatestValidCheckpointAsync(session.SessionId, Token))!.CheckpointId);
        Assert.True(File.Exists(newest.PackagePath));
        Assert.True(File.Exists(metadataPath));
    }

    [Fact]
    public async Task AbandonedStagingPackageInACleanSessionDoesNotBlockReclaimOrStayInTheBudget()
    {
        // A killed writer leaves <guid>.tmp.pma. It used to count toward the budget forever and made its clean
        // session unreclaimable, so after a few crashes every new checkpoint failed.
        var store = Store(new(MaxVersions: 5, MaxTotalBytes: 30));
        var old = await store.CreateSessionAsync(null, "Old", Token);
        var oldPoint = await Write(store, old, 1, new string('o', 10));
        string residue = Path.Combine(Path.GetDirectoryName(oldPoint.PackagePath)!, Guid.NewGuid().ToString("N") + ".tmp.pma");
        await File.WriteAllTextAsync(residue, new string('r', 10), Token);
        await store.MarkCleanAsync(old, Token); old.Dispose();
        using var current = await store.CreateSessionAsync(null, "Current", Token);
        // 10 (old) + 10 (residue) + 15 (new) > 30: the old clean history and the residue must both be reclaimed.
        var point = await Write(store, current, 1, new string('n', 15));
        Assert.True(File.Exists(point.PackagePath));
        Assert.False(File.Exists(residue));
        Assert.False(File.Exists(oldPoint.PackagePath));
    }

    private EditorRecoveryStore Store(RecoveryRetentionPolicy? policy = null) => new(Path.Combine(directory, "recovery"), policy);
    private static EditorRecoveryCheckpointMetadata Metadata(long revision) => new(null, "Project", revision,
        JsonSerializer.SerializeToElement(new { frame = 12 }));
    private Task<EditorRecoveryCheckpoint> Write(EditorRecoveryStore store, EditorRecoverySession session, long revision, string content) =>
        store.WriteCheckpointAsync(session, Metadata(revision),
            (path, token) => File.WriteAllTextAsync(path, content, new UTF8Encoding(false), token), Token);

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
