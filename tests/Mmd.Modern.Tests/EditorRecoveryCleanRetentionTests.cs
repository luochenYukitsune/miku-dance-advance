using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorRecoveryCleanRetentionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mda-recovery-clean-retention-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CleanHistorySurvivesNormalCloseUntilALaterCheckpointNeedsItsBudget()
    {
        var store = Store(8);
        using var closed = await store.CreateSessionAsync(null, "Closed", Token);
        var old = await Write(store, closed, 1, "four");
        await store.MarkCleanAsync(closed, Token);
        closed.Dispose();
        Assert.True(File.Exists(old.PackagePath));

        using var current = await store.CreateSessionAsync(null, "Current", Token);
        var first = await Write(store, current, 1, "four");
        Assert.True(File.Exists(old.PackagePath));
        var next = await Write(store, current, 2, "four");
        Assert.False(File.Exists(old.PackagePath));
        Assert.False(File.Exists(Path.ChangeExtension(old.PackagePath, ".json")));
        Assert.True(File.Exists(first.PackagePath));
        Assert.True(File.Exists(next.PackagePath));
        Assert.True(File.Exists(SessionFile(store, closed.SessionId)));
    }

    [Fact]
    public async Task OldestCleanCheckpointIsRotatedBeforeNewerCleanOrCurrentHistory()
    {
        var store = Store(12);
        using var firstClosed = await store.CreateSessionAsync(null, "First", Token);
        var oldest = await Write(store, firstClosed, 1, "four");
        await store.MarkCleanAsync(firstClosed, Token);
        firstClosed.Dispose();
        using var secondClosed = await store.CreateSessionAsync(null, "Second", Token);
        var newer = await Write(store, secondClosed, 1, "four");
        await store.MarkCleanAsync(secondClosed, Token);
        secondClosed.Dispose();
        await SetCreatedUtc(oldest, DateTimeOffset.UtcNow.AddDays(-2));
        await SetCreatedUtc(newer, DateTimeOffset.UtcNow.AddDays(-1));

        using var current = await store.CreateSessionAsync(null, "Current", Token);
        var first = await Write(store, current, 1, "four");
        await Write(store, current, 2, "four");
        Assert.False(File.Exists(oldest.PackagePath));
        Assert.True(File.Exists(newer.PackagePath));
        Assert.True(File.Exists(first.PackagePath));
    }

    [Fact]
    public async Task AnotherStoreCanRotateOnlyAfterTheCleanSessionsOsLeaseIsReleased()
    {
        var store = Store(4);
        using var closed = await store.CreateSessionAsync(null, "Closed", Token);
        var old = await Write(store, closed, 1, "four");
        await store.MarkCleanAsync(closed, Token);
        var otherStore = new EditorRecoveryStore(store.RootDirectory, new RecoveryRetentionPolicy(5, 4));
        using var current = await otherStore.CreateSessionAsync(null, "Current", Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Write(otherStore, current, 1, "four"));
        Assert.True(File.Exists(old.PackagePath));
        closed.Dispose();
        await Write(otherStore, current, 1, "four");
        Assert.False(File.Exists(old.PackagePath));
    }

    [Theory]
    [InlineData("unrecovered")]
    [InlineData("session")]
    [InlineData("checkpoint")]
    [InlineData("package")]
    [InlineData("foreign-file")]
    [InlineData("foreign-path")]
    [InlineData("missing-lease")]
    [InlineData("staging")]
    [InlineData("orphan-package")]
    [InlineData("orphan-metadata")]
    public async Task UnrecoveredDamagedAndForeignEntriesAreNeverDeletedForAnotherSessionsBudget(string kind)
    {
        var store = Store(8);
        using var closed = await store.CreateSessionAsync(null, "Closed", Token);
        var old = await Write(store, closed, 1, "four");
        var second = await Write(store, closed, 2, "four");
        if (kind != "unrecovered") await store.MarkCleanAsync(closed, Token);
        closed.Dispose();
        string closedDirectory = Path.GetDirectoryName(old.PackagePath)!;
        string outside = Path.Combine(directory, "original.pma");
        await File.WriteAllTextAsync(outside, "original", Token);
        if (kind == "session") await File.WriteAllTextAsync(SessionFile(store, closed.SessionId), "{broken", Token);
        if (kind == "checkpoint") await File.WriteAllTextAsync(Path.ChangeExtension(old.PackagePath, ".json"), "{broken", Token);
        if (kind == "package") await File.WriteAllTextAsync(old.PackagePath, "bad!", Token);
        if (kind == "foreign-file") await File.WriteAllTextAsync(Path.Combine(closedDirectory, "user-file.pma"), "foreign", Token);
        if (kind == "missing-lease") File.Delete(Path.Combine(closedDirectory, "session.lock"));
        if (kind == "staging") await File.WriteAllTextAsync(Path.Combine(closedDirectory, Guid.NewGuid().ToString("N") + ".tmp.pma"), "partial", Token);
        if (kind == "orphan-package") await File.WriteAllTextAsync(Path.Combine(closedDirectory, Guid.NewGuid().ToString("N") + ".pma"), "orphan", Token);
        if (kind == "orphan-metadata") await File.WriteAllTextAsync(Path.Combine(closedDirectory, Guid.NewGuid().ToString("N") + ".json"), "{broken", Token);
        if (kind == "foreign-path")
        {
            string metadataPath = Path.ChangeExtension(old.PackagePath, ".json");
            var document = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath, Token))!.AsObject();
            document["packageFileName"] = outside;
            await File.WriteAllTextAsync(metadataPath, document.ToJsonString(), Token);
        }
        using var current = await store.CreateSessionAsync(null, "Current", Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Write(store, current, 1, "four"));
        Assert.True(File.Exists(old.PackagePath));
        Assert.True(File.Exists(second.PackagePath));
        Assert.True(File.Exists(Path.ChangeExtension(second.PackagePath, ".json")));
        Assert.Equal("original", await File.ReadAllTextAsync(outside, Token));
    }

    [Fact]
    public async Task InsufficientReclaimableBudgetLeavesEveryCleanCheckpointUntouched()
    {
        var store = Store(8);
        using var abandoned = await store.CreateSessionAsync(null, "Abandoned", Token);
        var unrecovered = await Write(store, abandoned, 1, "123456");
        abandoned.Dispose();
        using var closed = await store.CreateSessionAsync(null, "Closed", Token);
        var clean = await Write(store, closed, 1, "12");
        await store.MarkCleanAsync(closed, Token);
        closed.Dispose();
        using var current = await store.CreateSessionAsync(null, "Current", Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Write(store, current, 1, "four"));
        Assert.True(File.Exists(clean.PackagePath));
        Assert.True(File.Exists(unrecovered.PackagePath));
    }

    [Fact]
    public async Task WriterFailurePreservesClosedHistoryButLaterPublicationFailureMayFollowAuthorizedCleanMaintenance()
    {
        var store = Store(4);
        using var closed = await store.CreateSessionAsync(null, "Closed", Token);
        var old = await Write(store, closed, 1, "four");
        await store.MarkCleanAsync(closed, Token);
        closed.Dispose();
        using var current = await store.CreateSessionAsync(null, "Current", Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteCheckpointAsync(current, Metadata(1), async (path, token) =>
        {
            await File.WriteAllTextAsync(path, "four", token);
            throw new InvalidDataException("PMA rejected");
        }, Token));
        Assert.True(File.Exists(old.PackagePath));
        var failure = await Record.ExceptionAsync(() => store.WriteCheckpointAsync(current, Metadata(1), async (path, token) =>
        {
            Directory.CreateDirectory(path[..^8] + ".json");
            await File.WriteAllTextAsync(path, "four", token);
        }, Token));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.False(File.Exists(old.PackagePath));
        Assert.False(File.Exists(Path.ChangeExtension(old.PackagePath, ".json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(store.RootDirectory, current.SessionId.ToString("N")), "*.pma"));
    }

    [Fact]
    public async Task CancellationAndOversizedPackageDoNotRotateClosedHistory()
    {
        var store = Store(4);
        using var closed = await store.CreateSessionAsync(null, "Closed", Token);
        var old = await Write(store, closed, 1, "four");
        await store.MarkCleanAsync(closed, Token);
        closed.Dispose();
        using var current = await store.CreateSessionAsync(null, "Current", Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteCheckpointAsync(current, Metadata(1), async (path, token) =>
        {
            await File.WriteAllTextAsync(path, "four", token);
            cancellation.Cancel();
        }, cancellation.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Write(store, current, 1, "12345"));
        Assert.True(File.Exists(old.PackagePath));
    }

    [Fact]
    public async Task ReparseEntryMakesTheWholeClosedSessionIneligibleWithoutFollowingItsTarget()
    {
        var store = Store(4);
        using var closed = await store.CreateSessionAsync(null, "Closed", Token);
        var old = await Write(store, closed, 1, "four");
        await store.MarkCleanAsync(closed, Token);
        closed.Dispose();
        string outside = Path.Combine(directory, "original.pma");
        await File.WriteAllTextAsync(outside, "original", Token);
        string link = Path.Combine(Path.GetDirectoryName(old.PackagePath)!, "foreign-link.txt");
        try { File.CreateSymbolicLink(link, outside); }
        catch (UnauthorizedAccessException) { Assert.Skip("Creating a symbolic link requires OS permission."); return; }
        try
        {
            using var current = await store.CreateSessionAsync(null, "Current", Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Write(store, current, 1, "four"));
            Assert.True(File.Exists(old.PackagePath));
            Assert.Equal("original", await File.ReadAllTextAsync(outside, Token));
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        }
        finally { File.Delete(link); }
    }

    [Fact]
    public async Task ASharingFailureDuringCleanMaintenancePreservesTheBlockedPairAndDoesNotPublish()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows sharing violations are required for this check."); return; }
        var store = Store(4);
        using var closed = await store.CreateSessionAsync(null, "Closed", Token);
        var old = await Write(store, closed, 1, "four");
        await store.MarkCleanAsync(closed, Token);
        closed.Dispose();
        using var current = await store.CreateSessionAsync(null, "Current", Token);
        using (var blocked = new FileStream(old.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Record.ExceptionAsync(() => Write(store, current, 1, "four"));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.True(File.Exists(old.PackagePath));
            Assert.True(File.Exists(Path.ChangeExtension(old.PackagePath, ".json")));
        }
        current.Dispose();
        Assert.Empty(await store.ListRecoverableAsync(Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(store.RootDirectory, current.SessionId.ToString("N")), "*.pma"));
    }

    [Fact]
    public async Task FailedMetadataDeletionLeavesEvidenceAndNeverTreatsTheDamagedSessionAsReclaimableAgain()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows sharing violations are required for this check."); return; }
        var store = Store(8);
        using var closed = await store.CreateSessionAsync(null, "Closed", Token);
        var oldest = await Write(store, closed, 1, "four");
        var newer = await Write(store, closed, 2, "four");
        await store.MarkCleanAsync(closed, Token);
        closed.Dispose();
        using var current = await store.CreateSessionAsync(null, "Current", Token);
        string oldMetadata = Path.ChangeExtension(oldest.PackagePath, ".json");
        using (var blocked = new FileStream(oldMetadata, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Record.ExceptionAsync(() => Write(store, current, 1, "four"));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.False(File.Exists(oldest.PackagePath));
            Assert.True(File.Exists(oldMetadata));
            Assert.True(File.Exists(newer.PackagePath));
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(store.RootDirectory, current.SessionId.ToString("N")), "*.pma"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Write(store, current, 1, "12345"));
        Assert.True(File.Exists(oldMetadata));
        Assert.True(File.Exists(newer.PackagePath));
    }

    [Fact]
    public async Task ConcurrentCleanWaitsForItsWriterAndBecomesReclaimableOnlyAfterTheLeaseIsReleased()
    {
        var store = Store(4);
        using var closing = await store.CreateSessionAsync(null, "Closing", Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = store.WriteCheckpointAsync(closing, Metadata(1), async (path, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            await File.WriteAllTextAsync(path, "four", token);
        }, Token);
        await entered.Task.WaitAsync(Token);
        var markClean = store.MarkCleanAsync(closing, Token);
        Assert.False(markClean.IsCompleted);
        release.SetResult();
        var old = await write;
        await markClean;
        using var current = await store.CreateSessionAsync(null, "Current", Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Write(store, current, 1, "four"));
        Assert.True(File.Exists(old.PackagePath));
        closing.Dispose();
        await Write(store, current, 1, "four");
        Assert.False(File.Exists(old.PackagePath));
    }

    private EditorRecoveryStore Store(long budget) => new(Path.Combine(directory, "recovery"), new RecoveryRetentionPolicy(5, budget));
    private static string SessionFile(EditorRecoveryStore store, Guid id) => Path.Combine(store.RootDirectory, id.ToString("N"), "session.json");
    private static EditorRecoveryCheckpointMetadata Metadata(long revision) => new(null, "Project", revision,
        JsonSerializer.SerializeToElement(new { frame = 12 }));
    private Task<EditorRecoveryCheckpoint> Write(EditorRecoveryStore store, EditorRecoverySession session, long revision, string content) =>
        store.WriteCheckpointAsync(session, Metadata(revision),
            (path, token) => File.WriteAllTextAsync(path, content, new UTF8Encoding(false), token), Token);
    private async Task SetCreatedUtc(EditorRecoveryCheckpoint checkpoint, DateTimeOffset value)
    {
        string path = Path.ChangeExtension(checkpoint.PackagePath, ".json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!.AsObject();
        document["createdUtc"] = JsonSerializer.SerializeToNode(value);
        await File.WriteAllTextAsync(path, document.ToJsonString(), Token);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
