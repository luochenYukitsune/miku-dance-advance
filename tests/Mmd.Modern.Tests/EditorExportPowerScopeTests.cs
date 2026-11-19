using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorExportPowerScopeTests
{
    [Fact]
    public void ScopeDisablesThrottlingAndSleepThenRestoresThePreviousStateOnce()
    {
        var platform = new RecordingPowerPlatform { Control = 0x4, State = 0x5, ExecutionState = EditorExportPowerScope.Continuous | 0x2 };
        var scope = EditorExportPowerScope.Enter(platform);
        Assert.True(scope.ThrottlingDisabled); Assert.True(scope.SleepBlocked);
        // Execution speed is opted out; the unrelated timer-resolution bits are retained.
        Assert.Equal((0x5u, 0x4u), (platform.Control, platform.State));
        Assert.Equal(EditorExportPowerScope.Continuous | EditorExportPowerScope.SystemRequired, platform.ExecutionState);
        scope.Dispose(); scope.Dispose();
        Assert.Equal((0x4u, 0x5u), (platform.Control, platform.State));
        Assert.Equal(EditorExportPowerScope.Continuous | 0x2, platform.ExecutionState);
        Assert.Equal(2, platform.ThrottlingWrites); Assert.Equal(2, platform.ExecutionStateWrites);
    }

    [Fact]
    public void FailureInsideTheScopeStillRestoresAndUnreadableThrottlingReturnsToSystemManagement()
    {
        var platform = new RecordingPowerPlatform { CanRead = false, Control = 0x1, State = 0 };
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var scope = EditorExportPowerScope.Enter(platform);
            Assert.Equal((0x1u, 0u), (platform.Control, platform.State));
            throw new InvalidOperationException("export failed");
        }));
        Assert.Equal((0u, 0u), (platform.Control, platform.State));
        Assert.Equal(EditorExportPowerScope.Continuous, platform.ExecutionState);
    }

    [Fact]
    public void RejectedPlatformCallsAreNeitherReportedNorRestored()
    {
        var platform = new RecordingPowerPlatform { CanWrite = false, CanSetExecutionState = false };
        using (var scope = EditorExportPowerScope.Enter(platform))
        {
            Assert.False(scope.ThrottlingDisabled); Assert.False(scope.SleepBlocked);
        }
        Assert.Equal(1, platform.ThrottlingWrites); Assert.Equal(1, platform.ExecutionStateWrites);
    }

    [Fact]
    public void DisposingFromAnotherThreadPostsExecutionStateRestoreToOwnerContext()
    {
        var platform = new RecordingPowerPlatform { ExecutionState = EditorExportPowerScope.Continuous };
        var previous = SynchronizationContext.Current;
        var owner = new RecordingSyncContext();
        SynchronizationContext.SetSynchronizationContext(owner);
        EditorExportPowerScope scope;
        try { scope = EditorExportPowerScope.Enter(platform); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert.True(scope.SleepBlocked);
        Exception? failure = null;
        var worker = new Thread(() => { try { scope.Dispose(); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        if (failure is not null) throw failure;
        Assert.Equal(1, owner.Posts);
        Assert.Equal(EditorExportPowerScope.Continuous, platform.ExecutionState);
    }

    private sealed class RecordingSyncContext : SynchronizationContext
    {
        public int Posts { get; private set; }
        public override void Post(SendOrPostCallback d, object? state) { Posts++; d(state); }
    }
}

internal sealed class RecordingPowerPlatform : IEditorPowerPlatform
{
    public bool CanRead { get; init; } = true;
    public bool CanWrite { get; init; } = true;
    public bool CanSetExecutionState { get; init; } = true;
    public uint Control { get; set; }
    public uint State { get; set; }
    public uint ExecutionState { get; set; } = EditorExportPowerScope.Continuous;
    public int ThrottlingWrites { get; private set; }
    public int ExecutionStateWrites { get; private set; }
    public bool IsExportPolicyActive => (Control & EditorExportPowerScope.ExecutionSpeed) != 0 && (State & EditorExportPowerScope.ExecutionSpeed) == 0
        && (ExecutionState & EditorExportPowerScope.SystemRequired) != 0;

    public bool TryGetProcessThrottling(out uint controlMask, out uint stateMask)
    {
        controlMask = CanRead ? Control : 0; stateMask = CanRead ? State : 0;
        return CanRead;
    }

    public bool SetProcessThrottling(uint controlMask, uint stateMask)
    {
        ThrottlingWrites++;
        if (!CanWrite) return false;
        Control = controlMask; State = stateMask;
        return true;
    }

    public uint SetThreadExecutionState(uint flags)
    {
        ExecutionStateWrites++;
        if (!CanSetExecutionState) return 0;
        uint previous = ExecutionState;
        // Like Windows: a continuous call replaces the thread's persistent requirements.
        if ((flags & EditorExportPowerScope.Continuous) != 0) ExecutionState = flags;
        return previous;
    }
}
