using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>
/// The native focus HWND belongs to this same OS thread. Wait without starving
/// its window procedure, and dispatch only between native session operations.
/// Only the owner calls PumpMessages, Wait and Dispose; any writer may Signal.
/// </summary>
internal sealed class D3D9OwnerWait : IDisposable
{
    private readonly AutoResetEvent wake = new(false);
    private readonly object signalGate = new();
    private bool disposed;

    internal void Signal()
    {
        // A channel write can finish before its writer resumes here. Cleanup
        // may already have drained that work and disposed this event by then.
        lock (signalGate)
        {
            if (!disposed) wake.Set();
        }
    }

    internal void PumpMessages()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Bound each batch so a stream of posted messages cannot starve FIFO work.
        for (var count = 0; count < 32 && PeekMessageW(out var message, 0, 0, 0, 1); count++)
        {
            // This private background thread has an explicit cleanup protocol.
            // WM_QUIT must neither skip Destroy/Unload nor strand accepted work.
            if (message.Id == 0x12) continue;
            TranslateMessage(in message);
            DispatchMessageW(in message);
        }
    }

    internal unsafe void Wait()
    {
        if (!OperatingSystem.IsWindows())
        {
            wake.WaitOne();
            return;
        }
        // Dispose runs only on this owner, after its last wait has returned.
        var handle = wake.SafeWaitHandle.DangerousGetHandle();
        const uint infinite = 0xffffffff, allInput = 0x04ff, inputAvailable = 0x0004;
        var result = MsgWaitForMultipleObjectsEx(1, &handle, infinite, allInput, inputAvailable);
        if (result == 0xffffffff) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (result is not (0 or 1)) throw new InvalidOperationException($"Unexpected D3D9 owner wait result: {result}.");
    }

    public void Dispose()
    {
        lock (signalGate)
        {
            if (disposed) return;
            disposed = true;
            wake.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern unsafe uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint timeout, uint wakeMask, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out Message message, nint window, uint first, uint last, uint remove);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in Message message);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint DispatchMessageW(in Message message);
}
