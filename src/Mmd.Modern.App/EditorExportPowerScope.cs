using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mmd.Modern.App;

/// <summary>Platform calls behind the export power policy. Tests substitute a recording fake.</summary>
internal interface IEditorPowerPlatform
{
    /// <summary>Reads the process power-throttling masks; false when the platform cannot report them.</summary>
    bool TryGetProcessThrottling(out uint controlMask, out uint stateMask);
    bool SetProcessThrottling(uint controlMask, uint stateMask);
    /// <summary>SetThreadExecutionState for the calling thread: the previous state, or 0 on failure.</summary>
    uint SetThreadExecutionState(uint flags);
}

/// <summary>
/// Export-only platform policy (App layer; ViewOutput stays neutral). While an export runs, Windows
/// may neither slow this process through execution-speed throttling (minimized or occluded windows)
/// nor put the system to sleep. Dispose restores the previous state, including after failure or
/// cancellation. The sleep request belongs to the entering thread, so dispose on that thread.
/// </summary>
internal sealed class EditorExportPowerScope : IDisposable
{
    internal const uint ExecutionSpeed = 0x1;
    internal const uint Continuous = 0x80000000, SystemRequired = 0x1, DisplayRequired = 0x2, AwayModeRequired = 0x40;
    private readonly IEditorPowerPlatform platform;
    private readonly (uint Control, uint State)? restoreThrottling;
    private readonly uint? restoreExecutionState;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly SynchronizationContext? ownerContext = SynchronizationContext.Current;
    private bool disposed;

    private EditorExportPowerScope(IEditorPowerPlatform platform)
    {
        this.platform = platform;
        // Without a readable previous state, restore lets the system manage throttling again.
        var previous = platform.TryGetProcessThrottling(out uint control, out uint state) ? (control, state) : (0u, 0u);
        if (platform.SetProcessThrottling(previous.Item1 | ExecutionSpeed, previous.Item2 & ~ExecutionSpeed))
            restoreThrottling = previous;
        uint before = platform.SetThreadExecutionState(Continuous | SystemRequired);
        if (before != 0) restoreExecutionState = Continuous | (before & (SystemRequired | DisplayRequired | AwayModeRequired));
    }

    public static EditorExportPowerScope Enter(IEditorPowerPlatform platform)
        => new(platform ?? throw new ArgumentNullException(nameof(platform)));

    public bool ThrottlingDisabled => restoreThrottling is not null;
    public bool SleepBlocked => restoreExecutionState is not null;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (restoreThrottling is { } throttling && !platform.SetProcessThrottling(throttling.Control, throttling.State))
            Trace.TraceWarning("Unable to restore the process power-throttling state after export.");
        if (restoreExecutionState is not { } executionState) return;
        if (Environment.CurrentManagedThreadId == ownerThread || ownerContext is null) Restore(executionState);
        else ownerContext.Post(_ => Restore(executionState), null);
    }

    private void Restore(uint executionState)
    {
        if (platform.SetThreadExecutionState(executionState) == 0)
            Trace.TraceWarning("Unable to restore the thread execution state after export.");
    }
}

internal sealed class EditorWindowsPowerPlatform : IEditorPowerPlatform
{
    public static EditorWindowsPowerPlatform Instance { get; } = new();
    private const int ProcessPowerThrottling = 4;
    private const uint ProcessPowerThrottlingCurrentVersion = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version, ControlMask, StateMask;
    }

    public bool TryGetProcessThrottling(out uint controlMask, out uint stateMask)
    {
        controlMask = stateMask = 0;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return false;
        var value = new ProcessPowerThrottlingState { Version = ProcessPowerThrottlingCurrentVersion };
        if (!GetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref value, (uint)Marshal.SizeOf<ProcessPowerThrottlingState>()))
            return false;
        controlMask = value.ControlMask; stateMask = value.StateMask;
        return true;
    }

    public bool SetProcessThrottling(uint controlMask, uint stateMask)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299)) return false;
        var value = new ProcessPowerThrottlingState { Version = ProcessPowerThrottlingCurrentVersion, ControlMask = controlMask, StateMask = stateMask };
        return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref value, (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());
    }

    public uint SetThreadExecutionState(uint flags) => OperatingSystem.IsWindows() ? NativeSetThreadExecutionState(flags) : 0;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(nint process, int informationClass, ref ProcessPowerThrottlingState information, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(nint process, int informationClass, ref ProcessPowerThrottlingState information, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "SetThreadExecutionState", ExactSpelling = true)]
    private static extern uint NativeSetThreadExecutionState(uint flags);
}
