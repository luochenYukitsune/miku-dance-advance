using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorCrashLogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mda-crash-log-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void UnhandledExceptionIsWrittenWithTypeMessageAndInnerException()
    {
        var error = new InvalidOperationException("outer failure", new InvalidDataException("inner cause"));
        string? path = EditorCrashLog.TryWrite("ui", error, directory);
        Assert.NotNull(path);
        string text = File.ReadAllText(path);
        Assert.Contains("Source: ui", text);
        Assert.Contains("System.InvalidOperationException: outer failure", text);
        Assert.Contains("System.IO.InvalidDataException: inner cause", text);
    }

    [Fact]
    public void LogWriteFailureIsSwallowedSoTheHandlerCannotThrow()
    {
        string blocker = Path.Combine(directory, "file");
        Directory.CreateDirectory(directory); File.WriteAllText(blocker, "x");
        Assert.Null(EditorCrashLog.TryWrite("ui", new Exception("x"), Path.Combine(blocker, "nested")));
    }

    [Fact]
    public void RepeatedExceptionsAreWrittenOnceThenAtPowersOfTwoAndOldFilesArePruned()
    {
        EditorCrashLog.ResetThrottleForTests();
        Exception Thrown() { try { throw new InvalidOperationException("per-frame"); } catch (Exception error) { return error; } }
        int announced = 0; var written = new List<int>();
        for (int i = 1; i <= 100; i++)
        {
            if (EditorCrashLog.ShouldRecord("ui", Thrown(), out string? path, directory)) announced++;
            if (path is not null) written.Add(i);
        }
        Assert.Equal(1, announced);
        Assert.Equal(new[] { 1, 2, 4, 8, 16, 32, 64 }, written);
        // 1, 2, 4, 8, 16, 32, 64.
        Assert.Equal(7, Directory.GetFiles(directory).Length);
        for (int i = 0; i < EditorCrashLog.MaxFiles + 5; i++) EditorCrashLog.TryWrite("distinct" + i, new Exception("x"), directory);
        Assert.Equal(EditorCrashLog.MaxFiles, Directory.GetFiles(directory).Length);
        EditorCrashLog.ResetThrottleForTests();
    }

    private sealed class First { public static Exception Refresh() { try { throw new InvalidOperationException("same"); } catch (Exception e) { return e; } } }
    private sealed class Second { public static Exception Refresh() { try { throw new InvalidOperationException("same"); } catch (Exception e) { return e; } } }

    [Fact]
    public void ADynamicMethodThrowSiteGetsASignatureInsteadOfThrowingFromTheHandler()
    {
        var method = new System.Reflection.Emit.DynamicMethod("Boom", typeof(void), Type.EmptyTypes);
        var il = method.GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Ldstr, "dynamic");
        il.Emit(System.Reflection.Emit.OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor([typeof(string)])!);
        il.Emit(System.Reflection.Emit.OpCodes.Throw);
        var boom = method.CreateDelegate<Action>();
        Exception thrown = Assert.ThrowsAny<Exception>(() => boom());
        string location = EditorCrashLog.Location(thrown);
        Assert.False(string.IsNullOrEmpty(location));
        EditorCrashLog.ResetThrottleForTests();
        Assert.True(EditorCrashLog.ShouldRecord("ui", thrown, out _, directory));
        EditorCrashLog.ResetThrottleForTests();
    }

    [Fact]
    public void PruningKeepsTheNewFileAndNeverTouchesFilesItDidNotWrite()
    {
        Directory.CreateDirectory(directory);
        string notes = Path.Combine(directory, "00-notes.txt"); File.WriteAllText(notes, "mine");
        // Names that sort after anything written now (a clock that moved back would do the same).
        for (int i = 0; i < EditorCrashLog.MaxFiles; i++)
            File.WriteAllText(Path.Combine(directory, $"99991231-235959-{i:000}-ui-{i:x8}.txt"), "old");
        string? path = EditorCrashLog.TryWrite("ui", new Exception("new"), directory);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(notes));
        Assert.Equal(EditorCrashLog.MaxFiles, Directory.GetFiles(directory, "*.txt").Count(file => file != notes));
    }

    [Fact]
    public void AnEarlierReturnedPathSurvivesTheNextWritersPrune()
    {
        Directory.CreateDirectory(directory);
        // MaxFiles - 1 names that sort after anything written now. Writer B's prune runs after writer A released the
        // lock (here: after A returned). Keeping only B's own new file plus the newest names would delete A's log.
        for (int i = 0; i < EditorCrashLog.MaxFiles - 1; i++)
            File.WriteAllText(Path.Combine(directory, $"99991231-235959-{i:000}-ui-{i:x8}.txt"), "old");
        string? first = EditorCrashLog.TryWrite("ui", new Exception("a"), directory);
        string? second = EditorCrashLog.TryWrite("ui", new Exception("b"), directory);
        Assert.True(File.Exists(first)); Assert.True(File.Exists(second));
        Assert.Equal(EditorCrashLog.MaxFiles, Directory.GetFiles(directory, "*.txt").Length);
    }

    [Fact]
    public async Task EveryPathReturnedToConcurrentWritersStillExists()
    {
        Directory.CreateDirectory(directory);
        for (int i = 0; i < EditorCrashLog.MaxFiles - 1; i++)
            File.WriteAllText(Path.Combine(directory, $"99991231-235959-{i:000}-ui-{i:x8}.txt"), "old");
        // 8 x 6 = 48 writes, fewer than MaxFiles: no returned path may be pruned, whichever writer prunes after it.
        var missing = new System.Collections.Concurrent.ConcurrentBag<string?>();
        var paths = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var start = new Barrier(8);
        var writers = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            start.SignalAndWait(TestContext.Current.CancellationToken);
            for (int n = 0; n < 6; n++)
            {
                string? path = EditorCrashLog.TryWrite("ui", new Exception("w" + i), directory);
                if (path is null || !File.Exists(path)) missing.Add(path); else paths.Add(path);
            }
        }, TestContext.Current.CancellationToken)).ToArray();
        await Task.WhenAll(writers);
        Assert.Empty(missing);
        Assert.All(paths, path => Assert.True(File.Exists(path)));
        Assert.Equal(EditorCrashLog.MaxFiles, Directory.GetFiles(directory, "*.txt").Length);
    }

    [Fact]
    public void AWindowFilterHitRefreshesRecencySoARecurringFaultIsNotEvicted()
    {
        var filter = new EditorCrashLog.NoticeFilter(capacity: 2);
        Assert.True(filter.MarkShown("a")); Assert.True(filter.MarkShown("b"));
        Assert.True(filter.WasShown("a"));
        Assert.True(filter.MarkShown("c"));
        Assert.True(filter.WasShown("a")); Assert.False(filter.WasShown("b")); Assert.True(filter.WasShown("c"));
    }

    [Fact]
    public void FaultsSharingTheOverflowBucketStillReachSubscribersWithDistinctSignatures()
    {
        EditorCrashLog.ResetThrottleForTests();
        for (int i = 0; i < EditorCrashLog.MaxSignatures; i++) EditorCrashLog.ShouldRecord("fill" + i, new Exception("x"), out _, directory);
        var received = new List<(string Signature, string? Path)>();
        Action<Exception, string, string?> subscriber = (_, signature, path) => received.Add((signature, path));
        EditorCrashLog.UiExceptionRecorded += subscriber;
        try
        {
            // Through the production dispatch: the overflow bucket writes counts 1 and 2, not 3, yet all three arrive.
            EditorCrashLog.DispatchUiException(First.Refresh(), directory);
            EditorCrashLog.DispatchUiException(Second.Refresh(), directory);
            EditorCrashLog.DispatchUiException(Third.Refresh(), directory);
        }
        finally { EditorCrashLog.UiExceptionRecorded -= subscriber; EditorCrashLog.ResetThrottleForTests(); }
        Assert.Equal(3, received.Count);
        Assert.Equal(3, received.Select(value => value.Signature).Distinct().Count());
        Assert.NotNull(received[0].Path); Assert.Null(received[2].Path);
        var filter = new EditorCrashLog.NoticeFilter();
        Assert.All(received, value => Assert.True(filter.MarkShown(value.Signature)));
        Assert.False(filter.MarkShown(received[0].Signature));
        filter.Clear();
        Assert.False(filter.WasShown(received[0].Signature));
    }

    private sealed class Third { public static Exception Refresh() { try { throw new InvalidOperationException("same"); } catch (Exception e) { return e; } } }

    [Fact]
    public void AggregatesWithEqualMessagesButDifferentInnerSitesAreThrottledSeparately()
    {
        EditorCrashLog.ResetThrottleForTests();
        // Unobserved-task aggregates are never thrown; their message is built from the inner messages only.
        AggregateException A() => new(First.Refresh(), First.Refresh());
        AggregateException B() => new(Second.Refresh(), Second.Refresh());
        Assert.NotEqual(EditorCrashLog.Signature("unobserved-task", A()), EditorCrashLog.Signature("unobserved-task", B()));
        Assert.True(EditorCrashLog.ShouldRecord("unobserved-task", A(), out _, directory));
        EditorCrashLog.ShouldRecord("unobserved-task", A(), out _, directory);
        Assert.True(EditorCrashLog.ShouldRecord("unobserved-task", B(), out string? path, directory));
        Assert.NotNull(path);
        EditorCrashLog.ResetThrottleForTests();
    }

    private sealed class HostileException : Exception
    {
        public override string Message => throw new InvalidOperationException("Message getter");
        public override string ToString() => throw new InvalidOperationException("ToString");
        public override string? StackTrace => throw new InvalidOperationException("StackTrace getter");
    }

    [Fact]
    public void AnExceptionWhoseMessageAndToStringThrowIsStillRecordedAndCounted()
    {
        // TargetSite is null (never thrown) and every text member throws: the handler must not throw a second time.
        var hostile = new HostileException();
        Assert.Equal(EditorCrashLog.UnknownLocation, EditorCrashLog.Location(hostile));
        EditorCrashLog.ResetThrottleForTests();
        Assert.True(EditorCrashLog.ShouldRecord("ui", hostile, out string? path, directory));
        Assert.NotNull(path);
        Assert.False(EditorCrashLog.ShouldRecord("ui", hostile, out _, directory));
        EditorCrashLog.ResetThrottleForTests();
    }

    [Fact]
    public void SameNamedMethodsOnDifferentTypesAreDistinctSignatures()
    {
        EditorCrashLog.ResetThrottleForTests();
        Assert.True(EditorCrashLog.ShouldRecord("ui", First.Refresh(), out _, directory));
        Assert.True(EditorCrashLog.ShouldRecord("ui", Second.Refresh(), out _, directory));
        EditorCrashLog.ResetThrottleForTests();
    }

    [Fact]
    public void SignaturesPastTheTableBoundAreStillThrottled()
    {
        EditorCrashLog.ResetThrottleForTests();
        for (int i = 0; i < EditorCrashLog.MaxSignatures; i++) EditorCrashLog.ShouldRecord("fill" + i, new Exception("x"), out _, directory);
        int announced = 0;
        for (int i = 0; i < 20; i++) if (EditorCrashLog.ShouldRecord("late", new Exception("y"), out _, directory)) announced++;
        Assert.Equal(1, announced);
        EditorCrashLog.ResetThrottleForTests();
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
