using System.Text.Json;
using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorAutoSaveCoordinatorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mda-autosave-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CaptureRunsBeforeReturningOnTheCallerThreadAndOnlyFrozenInputsReachTheWorker()
    {
        var store = Store();
        using var coordinator = new EditorAutoSaveCoordinator(store);
        var called = new TaskCompletionSource<Task<EditorAutoSaveResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new OwnedCapture();
        int ownerThread = 0, captureThread = 0, saveThread = 0;
        string liveText = "captured";
        var thread = new Thread(() =>
        {
            try
            {
                ownerThread = Environment.CurrentManagedThreadId;
                bool captured = false;
                var save = coordinator.CheckpointAsync(Guid.NewGuid(), "first", () =>
                {
                    captured = true;
                    captureThread = Environment.CurrentManagedThreadId;
                    string frozen = liveText;
                    return new(Metadata(1), owner, (path, token) =>
                    {
                        saveThread = Environment.CurrentManagedThreadId;
                        Assert.False(owner.Disposed);
                        token.ThrowIfCancellationRequested();
                        File.WriteAllText(path, frozen);
                    });
                }, Token);
                Assert.True(captured);
                liveText = "changed after capture";
                called.SetResult(save);
            }
            catch (Exception error) { called.SetException(error); }
        });
        thread.Start();
        var result = await (await called.Task.WaitAsync(Token)).WaitAsync(Token);
        Assert.Equal(EditorAutoSaveStatus.Saved, result.Status);
        Assert.Equal(ownerThread, captureThread);
        Assert.NotEqual(ownerThread, saveThread);
        Assert.True(owner.Disposed);
        Assert.Equal("captured", await File.ReadAllTextAsync(result.Checkpoint!.PackagePath, Token));
    }

    [Fact]
    public async Task SingleFlightRejectsReentryWithoutCapturingOrQueuingAnotherWrite()
    {
        var store = Store();
        using var coordinator = new EditorAutoSaveCoordinator(store);
        using var release = new ManualResetEventSlim();
        var started = Signal();
        Guid document = Guid.NewGuid();
        var first = coordinator.CheckpointAsync(document, "one", () => Snapshot(1, (path, token) =>
        {
            started.SetResult();
            release.Wait(token);
            File.WriteAllText(path, "one");
        }), Token);
        Task idle;
        try
        {
            await started.Task.WaitAsync(Token);
            idle = coordinator.WaitForIdleAsync(Token);
            Assert.False(idle.IsCompleted);
            bool captured = false;
            var second = await coordinator.CheckpointAsync(document, "two", () =>
            {
                captured = true;
                return Snapshot(2);
            }, Token);
            Assert.Equal(EditorAutoSaveStatus.Busy, second.Status);
            Assert.False(captured);
        }
        finally { release.Set(); }
        Assert.Equal(EditorAutoSaveStatus.Saved, (await first).Status);
        await idle;
        Assert.Empty(await store.ListRecoverableAsync(Token));
        coordinator.Abandon();
        Assert.Single(Assert.Single(await store.ListRecoverableAsync(Token)).Checkpoints);
    }

    [Fact]
    public async Task UnchangedIdentitySkipsCaptureButEditorDraftChangesCreateAnotherVersion()
    {
        var store = Store();
        using var coordinator = new EditorAutoSaveCoordinator(store);
        Guid document = Guid.NewGuid();
        var firstMetadata = Metadata(1, "draft A");
        string firstIdentity = EditorAutoSaveSnapshot.GetFingerprint(firstMetadata);
        Assert.Equal(EditorAutoSaveStatus.Saved, (await coordinator.CheckpointAsync(document, firstIdentity,
            () => Snapshot(firstMetadata), Token)).Status);
        bool captured = false;
        Assert.Equal(EditorAutoSaveStatus.Unchanged, (await coordinator.CheckpointAsync(document, firstIdentity, () =>
        {
            captured = true;
            return Snapshot(firstMetadata);
        }, Token)).Status);
        Assert.False(captured);
        var secondMetadata = Metadata(1, "draft B");
        string secondIdentity = EditorAutoSaveSnapshot.GetFingerprint(secondMetadata);
        Assert.NotEqual(firstIdentity, secondIdentity);
        Assert.Equal(EditorAutoSaveStatus.Saved, (await coordinator.CheckpointAsync(document, secondIdentity,
            () => Snapshot(secondMetadata), Token)).Status);
        coordinator.Abandon();
        Assert.Equal(2, Assert.Single(await store.ListRecoverableAsync(Token)).Checkpoints.Count);
    }

    [Fact]
    public async Task CaptureFailureIsReturnedAndTheSameIdentityCanBeRetried()
    {
        var store = Store();
        using var coordinator = new EditorAutoSaveCoordinator(store);
        Guid document = Guid.NewGuid();
        var failure = new InvalidDataException("cannot freeze project");
        var failed = await coordinator.CheckpointAsync(document, "same", () => throw failure, Token);
        Assert.Equal(EditorAutoSaveStatus.Failed, failed.Status);
        Assert.Same(failure, failed.Error);
        Assert.Equal(EditorAutoSaveStatus.Saved, (await coordinator.CheckpointAsync(document, "same",
            () => Snapshot(1), Token)).Status);
    }

    [Fact]
    public async Task FailedBackgroundWriteDisposesTheCaptureRetainsHistoryAndAllowsRetry()
    {
        var store = Store();
        using var coordinator = new EditorAutoSaveCoordinator(store);
        Guid document = Guid.NewGuid();
        var old = await coordinator.CheckpointAsync(document, "old", () => Snapshot(1), Token);
        var owner = new OwnedCapture();
        var failure = new IOException("disk write failed");
        var failed = await coordinator.CheckpointAsync(document, "new", () => new(Metadata(2), owner, (path, _) =>
        {
            File.WriteAllText(path, "partial");
            throw failure;
        }), Token);
        Assert.Equal(EditorAutoSaveStatus.Failed, failed.Status);
        Assert.Same(failure, failed.Error);
        Assert.True(owner.Disposed);
        Assert.True(File.Exists(old.Checkpoint!.PackagePath));
        var retry = await coordinator.CheckpointAsync(document, "new", () => Snapshot(2), Token);
        Assert.Equal(EditorAutoSaveStatus.Saved, retry.Status);
        coordinator.Abandon();
        Assert.Equal(2, Assert.Single(await store.ListRecoverableAsync(Token)).Checkpoints.Count);
    }

    [Fact]
    public async Task CancellationDoesNotPublishThePartialWriteAndTheSameIdentityCanBeRetried()
    {
        var store = Store();
        using var coordinator = new EditorAutoSaveCoordinator(store);
        Guid document = Guid.NewGuid();
        var old = await coordinator.CheckpointAsync(document, "old", () => Snapshot(1), Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var owner = new OwnedCapture();
        var canceled = await coordinator.CheckpointAsync(document, "new", () => new(Metadata(2), owner, (path, token) =>
        {
            File.WriteAllText(path, "partial");
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
        }), cancel.Token);
        Assert.Equal(EditorAutoSaveStatus.Canceled, canceled.Status);
        Assert.IsAssignableFrom<OperationCanceledException>(canceled.Error);
        Assert.True(owner.Disposed);
        Assert.True(File.Exists(old.Checkpoint!.PackagePath));
        Assert.Equal(EditorAutoSaveStatus.Saved, (await coordinator.CheckpointAsync(document, "new",
            () => Snapshot(2), Token)).Status);
        coordinator.Abandon();
        Assert.Equal(2, Assert.Single(await store.ListRecoverableAsync(Token)).Checkpoints.Count);
    }

    [Fact]
    public async Task PreCanceledRequestDoesNotCapture()
    {
        using var coordinator = new EditorAutoSaveCoordinator(Store());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        bool captured = false;
        var result = await coordinator.CheckpointAsync(Guid.NewGuid(), "one", () =>
        {
            captured = true;
            return Snapshot(1);
        }, cancel.Token);
        Assert.Equal(EditorAutoSaveStatus.Canceled, result.Status);
        Assert.False(captured);
    }

    [Fact]
    public async Task DifferentDocumentsHaveIndependentSessionsAndOnlyExplicitRetirementMarksOldHistoryClean()
    {
        var store = Store();
        using var coordinator = new EditorAutoSaveCoordinator(store);
        Guid firstDocument = Guid.NewGuid(), secondDocument = Guid.NewGuid();
        var first = await coordinator.CheckpointAsync(firstDocument, "same", () => Snapshot(1), Token);
        var second = await coordinator.CheckpointAsync(secondDocument, "same", () => Snapshot(1), Token);
        Assert.NotEqual(first.Checkpoint!.SessionId, second.Checkpoint!.SessionId);
        Assert.Empty(await store.ListRecoverableAsync(Token));
        await coordinator.RetireCleanAsync(firstDocument, Token);
        Assert.True(File.Exists(first.Checkpoint.PackagePath));
        Assert.Equal(EditorAutoSaveStatus.Closed, (await coordinator.CheckpointAsync(firstDocument, "later",
            () => Snapshot(2), Token)).Status);
        coordinator.Abandon();
        Assert.Equal(second.Checkpoint.SessionId, Assert.Single(await store.ListRecoverableAsync(Token)).SessionId);
    }

    [Fact]
    public async Task RetirementWaitsForItsOldCaptureAndCannotMarkTheNewDocumentClean()
    {
        var store = Store();
        using var coordinator = new EditorAutoSaveCoordinator(store);
        using var release = new ManualResetEventSlim();
        var started = Signal();
        Guid oldDocument = Guid.NewGuid();
        var oldSave = coordinator.CheckpointAsync(oldDocument, "old", () => Snapshot(1, (path, token) =>
        {
            started.SetResult();
            release.Wait(token);
            File.WriteAllText(path, "old");
        }), Token);
        Task retire;
        try
        {
            await started.Task.WaitAsync(Token);
            retire = coordinator.RetireCleanAsync(oldDocument, Token);
            Assert.False(retire.IsCompleted);
        }
        finally { release.Set(); }
        var old = await oldSave;
        await retire;
        var current = await coordinator.CheckpointAsync(Guid.NewGuid(), "new", () => Snapshot(2), Token);
        coordinator.Abandon();
        Assert.True(File.Exists(old.Checkpoint!.PackagePath));
        Assert.Equal(current.Checkpoint!.SessionId, Assert.Single(await store.ListRecoverableAsync(Token)).SessionId);
    }

    [Fact]
    public async Task CanceledRetirementLeavesTheDocumentOpenAndItsLastCheckpointRecoverable()
    {
        var store = Store();
        using var coordinator = new EditorAutoSaveCoordinator(store);
        using var release = new ManualResetEventSlim();
        var started = Signal();
        Guid document = Guid.NewGuid();
        var save = coordinator.CheckpointAsync(document, "one", () => Snapshot(1, (path, token) =>
        {
            started.SetResult();
            release.Wait(token);
            File.WriteAllText(path, "one");
        }), Token);
        try
        {
            await started.Task.WaitAsync(Token);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var retire = coordinator.RetireCleanAsync(document, cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retire);
        }
        finally { release.Set(); }
        await save;
        Assert.Equal(EditorAutoSaveStatus.Saved, (await coordinator.CheckpointAsync(document, "two",
            () => Snapshot(2), Token)).Status);
        coordinator.Abandon();
        Assert.Equal(2, Assert.Single(await store.ListRecoverableAsync(Token)).Checkpoints.Count);
    }

    [Fact]
    public async Task ShutdownDrainsThenMarksCleanAndPreservesPackageBytes()
    {
        var store = Store();
        using var coordinator = new EditorAutoSaveCoordinator(store);
        using var release = new ManualResetEventSlim();
        var started = Signal();
        var save = coordinator.CheckpointAsync(Guid.NewGuid(), "one", () => Snapshot(1, (path, token) =>
        {
            started.SetResult();
            release.Wait(token);
            File.WriteAllText(path, "one");
        }), Token);
        Task shutdown;
        try
        {
            await started.Task.WaitAsync(Token);
            shutdown = coordinator.ShutdownCleanAsync(Token);
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(EditorAutoSaveStatus.Closed, (await coordinator.CheckpointAsync(Guid.NewGuid(), "two",
                () => Snapshot(2), Token)).Status);
        }
        finally { release.Set(); }
        var saved = await save;
        await shutdown;
        Assert.Empty(await store.ListRecoverableAsync(Token));
        Assert.Equal("one", await File.ReadAllTextAsync(saved.Checkpoint!.PackagePath, Token));
    }

    [Fact]
    public async Task AbandonDuringAWriteHoldsTheLeaseUntilTheFrozenCaptureHasFinished()
    {
        var store = Store();
        using var coordinator = new EditorAutoSaveCoordinator(store);
        using var release = new ManualResetEventSlim();
        var started = Signal();
        var save = coordinator.CheckpointAsync(Guid.NewGuid(), "one", () => Snapshot(1, (path, token) =>
        {
            started.SetResult();
            release.Wait(token);
            File.WriteAllText(path, "one");
        }), Token);
        try
        {
            await started.Task.WaitAsync(Token);
            coordinator.Abandon();
            Assert.Empty(await store.ListRecoverableAsync(Token));
            Assert.Equal(EditorAutoSaveStatus.Closed, (await coordinator.CheckpointAsync(Guid.NewGuid(), "two",
                () => Snapshot(2), Token)).Status);
        }
        finally { release.Set(); }
        var saved = await save;
        Assert.Equal(saved.Checkpoint!.CheckpointId,
            Assert.Single(Assert.Single(await store.ListRecoverableAsync(Token)).Checkpoints).CheckpointId);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.ShutdownCleanAsync(Token));
    }

    [Fact]
    public async Task DisposingWithoutCleanShutdownLeavesAllDocumentHistoriesRecoverable()
    {
        var store = Store();
        var coordinator = new EditorAutoSaveCoordinator(store);
        await coordinator.CheckpointAsync(Guid.NewGuid(), "one", () => Snapshot(1), Token);
        await coordinator.CheckpointAsync(Guid.NewGuid(), "two", () => Snapshot(2), Token);
        coordinator.Dispose();
        coordinator.Dispose();
        Assert.Equal(2, (await store.ListRecoverableAsync(Token)).Count);
    }

    [Fact]
    public async Task CanceledShutdownCanBeRetriedWithoutMarkingTheInflightSessionCleanEarly()
    {
        var store = Store();
        using var coordinator = new EditorAutoSaveCoordinator(store);
        using var release = new ManualResetEventSlim();
        var started = Signal();
        var save = coordinator.CheckpointAsync(Guid.NewGuid(), "one", () => Snapshot(1, (path, token) =>
        {
            started.SetResult();
            release.Wait(token);
            File.WriteAllText(path, "one");
        }), Token);
        try
        {
            await started.Task.WaitAsync(Token);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var shutdown = coordinator.ShutdownCleanAsync(cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shutdown);
            Assert.False(save.IsCompleted);
        }
        finally { release.Set(); }
        var saved = await save;
        await coordinator.ShutdownCleanAsync(Token);
        Assert.Empty(await store.ListRecoverableAsync(Token));
        Assert.True(File.Exists(saved.Checkpoint!.PackagePath));
    }

    [Fact]
    public async Task ReentrantCaptureIsRejectedAndOwnerInitiatedRetirementStillDrainsItsCapture()
    {
        var store = Store();
        using var coordinator = new EditorAutoSaveCoordinator(store);
        Guid document = Guid.NewGuid();
        Task? retire = null;
        Task<EditorAutoSaveResult>? reentrant = null;
        var save = coordinator.CheckpointAsync(document, "one", () =>
        {
            reentrant = coordinator.CheckpointAsync(document, "two", () =>
                throw new InvalidOperationException("A reentrant capture must not run."), Token);
            Assert.True(reentrant.IsCompletedSuccessfully);
            retire = coordinator.RetireCleanAsync(document, Token);
            Assert.False(retire.IsCompleted);
            return Snapshot(1);
        }, Token);
        Assert.Equal(EditorAutoSaveStatus.Saved, (await save).Status);
        Assert.NotNull(reentrant);
        Assert.Equal(EditorAutoSaveStatus.Busy, (await reentrant).Status);
        Assert.NotNull(retire);
        await retire;
        coordinator.Abandon();
        Assert.Empty(await store.ListRecoverableAsync(Token));
    }

    [Fact]
    public async Task AbandonInsideSynchronousCaptureStillOwnsTheLaterWorkerUntilItsFullWriteFinishes()
    {
        var store = Store();
        using var coordinator = new EditorAutoSaveCoordinator(store);
        using var release = new ManualResetEventSlim();
        var started = Signal();
        var owner = new OwnedCapture();
        bool captureReturned = false;
        var save = coordinator.CheckpointAsync(Guid.NewGuid(), "one", () =>
        {
            coordinator.Abandon();
            captureReturned = true;
            return new(Metadata(1), owner, (path, token) =>
            {
                Assert.True(captureReturned);
                Assert.False(owner.Disposed);
                File.WriteAllText(path, "partial");
                started.SetResult();
                release.Wait(token);
                File.WriteAllText(path, "complete");
            });
        }, Token);
        try
        {
            await started.Task.WaitAsync(Token);
            Assert.False(save.IsCompleted);
            Assert.False(owner.Disposed);
            Assert.Empty(await store.ListRecoverableAsync(Token));
        }
        finally { release.Set(); }
        var saved = await save;
        Assert.Equal(EditorAutoSaveStatus.Saved, saved.Status);
        Assert.True(owner.Disposed);
        var recovered = Assert.Single(Assert.Single(await store.ListRecoverableAsync(Token)).Checkpoints);
        Assert.Equal(saved.Checkpoint!.CheckpointId, recovered.CheckpointId);
        Assert.Equal("complete", await File.ReadAllTextAsync(recovered.PackagePath, Token));
    }

    private EditorRecoveryStore Store() => new(Path.Combine(directory, "recovery"));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static EditorRecoveryCheckpointMetadata Metadata(long revision, string draft = "draft") =>
        new(null, "Untitled", revision, JsonSerializer.SerializeToElement(new { draft }));
    private static EditorAutoSaveSnapshot Snapshot(long revision, Action<string, CancellationToken>? writer = null) =>
        Snapshot(Metadata(revision), writer);
    private static EditorAutoSaveSnapshot Snapshot(EditorRecoveryCheckpointMetadata metadata,
        Action<string, CancellationToken>? writer = null) => new(metadata, new OwnedCapture(), writer ?? ((path, token) =>
        {
            token.ThrowIfCancellationRequested();
            File.WriteAllText(path, "frozen project");
        }));

    private sealed class OwnedCapture : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    public void Dispose()
    {
        string resolved = Path.GetFullPath(directory);
        string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("mda-autosave-", StringComparison.Ordinal))
            throw new InvalidOperationException("The test directory is outside its temporary root.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
