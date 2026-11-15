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

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
