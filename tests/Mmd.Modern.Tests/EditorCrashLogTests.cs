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
    public void AWindowFilterShowsEachNewSignatureOnceEvenAfterTheLogTableOverflows()
    {
        EditorCrashLog.ResetThrottleForTests();
        for (int i = 0; i < EditorCrashLog.MaxSignatures; i++) EditorCrashLog.ShouldRecord("fill" + i, new Exception("x"), out _, directory);
        var filter = new EditorCrashLog.NoticeFilter();
        // Two distinct new faults share the log's overflow bucket but are each shown once by the window.
        Assert.True(filter.MarkShown(EditorCrashLog.Signature("ui", First.Refresh())));
        Assert.True(filter.MarkShown(EditorCrashLog.Signature("ui", Second.Refresh())));
        Assert.False(filter.MarkShown(EditorCrashLog.Signature("ui", First.Refresh())));
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
