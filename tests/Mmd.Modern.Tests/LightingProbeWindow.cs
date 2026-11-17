using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Mmd.Modern.Tests;

internal sealed class ProbeWindow : IDisposable
{
    private const uint DesktopAccess = 0x01 | 0x02 | 0x04 | 0x08 | 0x40 | 0x80;
    private const uint ExNoActivateTool = 0x08000000 | 0x00000080;
    private const uint StylePopup = 0x80000000;
    private const uint SwpNoActivateNoZOrderShow = 0x0010 | 0x0004 | 0x0040;
    private const uint WmQuit = 0x0012;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmWindowPosChanging = 0x0046;
    private const uint WmDisplayChange = 0x007e;
    [ThreadStatic] private static (int X, int Y)? fixedOffscreenPosition;
    private static readonly WindowProc Procedure = Dispatch;
    private readonly Thread thread;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint threadId;
    private nint handle;
    private nint desktop;
    private nint previous;
    private bool disposed;
    private Exception? cleanupError;
    private string? ownerDesktop;
    private readonly string mode;
    private readonly int width, height;
    internal nint Handle => handle;

    internal ProbeWindow(string mode, int width, int height)
    {
        if (mode is not ("private" or "offscreen" or "hidden")) throw new ArgumentException("Unknown window mode.", nameof(mode));
        this.mode = mode;
        this.width = width;
        this.height = height;
        thread = new Thread(Run) { IsBackground = true, Name = "Lighting probe native HWND" };
        // This HWND-only thread uses no COM. Assign its private desktop before any apartment initializes.
        thread.Start();
        try { ready.Task.GetAwaiter().GetResult(); }
        catch { thread.Join(); throw; }
    }

    private void Run()
    {
        string windowClass = "MDA-PR01-probe-" + Guid.NewGuid().ToString("N");
        bool registered = false;
        nint instance = GetModuleHandleW(null);
        try
        {
            threadId = GetCurrentThreadId();
            previous = GetThreadDesktop(threadId);
            if (previous == 0) throw Error("GetThreadDesktop");
            if (mode == "private")
            {
                desktop = CreateDesktopW(windowClass, 0, 0, 0, DesktopAccess, 0);
                if (desktop == 0) throw Error("CreateDesktop");
                if (!SetThreadDesktop(desktop)) throw Error("SetThreadDesktop");
            }
            ownerDesktop = Name(GetThreadDesktop(threadId));
            if (mode == "private" && (ownerDesktop != windowClass || ownerDesktop == InputDesktopName()))
                throw new InvalidOperationException("The new HWND thread was not isolated from the input desktop.");
            var @class = new WindowClass { Proc = Procedure, Instance = instance, ClassName = windowClass };
            if (RegisterClassW(ref @class) == 0) throw Error("RegisterClass");
            registered = true;
            var virtualScreen = VirtualScreen();
            int x = mode == "offscreen" ? checked(virtualScreen.Right + 512) : virtualScreen.Left;
            int y = virtualScreen.Top;
            if (mode == "offscreen") fixedOffscreenPosition = (x, y);
            handle = CreateWindowExW(ExNoActivateTool, windowClass, string.Empty, StylePopup, x, y, width, height,
                0, 0, instance, 0);
            if (handle == 0) throw Error("CreateWindowEx");
            _ = Audit();
            if (mode != "hidden" && !SetWindowPos(handle, 0, x, y, width, height, SwpNoActivateNoZOrderShow))
                throw Error("SetWindowPos");
            _ = Audit();
            // CreateWindowEx initializes the queue before publishing readiness.
            ready.TrySetResult();
            int status;
            while ((status = GetMessageW(out var message, 0, 0, 0)) > 0)
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessageW(ref message);
            }
            if (status < 0) throw Error("GetMessage");
        }
        catch (Exception error)
        {
            if (!ready.TrySetException(error)) cleanupError ??= error;
        }
        finally
        {
            if (handle != 0)
            {
                if (!DestroyWindow(handle)) cleanupError ??= Error("DestroyWindow");
                handle = 0;
            }
            if (registered && !UnregisterClassW(windowClass, instance)) cleanupError ??= Error("UnregisterClass");
            if (desktop != 0)
            {
                if (!SetThreadDesktop(previous)) cleanupError ??= Error("Restore thread desktop");
                if (!CloseDesktop(desktop)) cleanupError ??= Error("CloseDesktop");
                desktop = 0;
            }
        }
    }

    internal object Audit()
    {
        nint hwnd = handle;
        if (!GetWindowRect(hwnd, out var rect)) throw Error("GetWindowRect");
        var virtualScreen = VirtualScreen();
        string inputDesktop = InputDesktopName();
        bool visibleStyle = IsWindowVisible(hwnd);
        bool rectOnMonitor = Intersects(rect, virtualScreen);
        bool onInputDesktop = string.Equals(ownerDesktop, inputDesktop, StringComparison.OrdinalIgnoreCase);
        bool foreground = GetForegroundWindow() == hwnd;
        if (foreground || mode == "private" && onInputDesktop || mode == "offscreen" && rectOnMonitor ||
            mode == "hidden" && visibleStyle)
            throw new InvalidOperationException("The native probe HWND may be visible to the user.");
        return new { mode, ownerDesktop, inputDesktop, visibleStyle, rectOnMonitor, onInputDesktop, foreground, rect, virtualScreen };
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (thread.IsAlive && !PostThreadMessageW(threadId, WmQuit, 0, 0) && thread.IsAlive)
            throw Error("PostThreadMessage");
        if (!thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Native HWND cleanup did not return.");
        if (cleanupError is not null) throw new InvalidOperationException("Native HWND cleanup failed.", cleanupError);
    }

    private static string InputDesktopName()
    {
        nint input = OpenInputDesktop(0, false, 1);
        if (input == 0) throw Error("OpenInputDesktop");
        try { return Name(input); }
        finally { _ = CloseDesktop(input); }
    }

    private static string Name(nint desktop)
    {
        var name = new StringBuilder(512);
        if (!GetUserObjectInformationW(desktop, 2, name, name.Capacity * sizeof(char), out _))
            throw Error("GetUserObjectInformation");
        return name.ToString();
    }

    private static Rect VirtualScreen()
    {
        int x = GetSystemMetrics(76), y = GetSystemMetrics(77);
        int w = GetSystemMetrics(78), h = GetSystemMetrics(79);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("No valid virtual screen geometry.");
        return new Rect { Left = x, Top = y, Right = checked(x + w), Bottom = checked(y + h) };
    }
    private static bool Intersects(Rect first, Rect second) => first.Left < second.Right && second.Left < first.Right &&
        first.Top < second.Bottom && second.Top < first.Bottom;
    private static Exception Error(string action) => new Win32Exception(Marshal.GetLastPInvokeError(), action);
    private static nint Dispatch(nint hwnd, uint msg, nint wparam, nint lparam)
    {
        if (msg == WmMouseActivate) return 4; // MA_NOACTIVATEANDEAT.
        if (msg == WmDisplayChange && fixedOffscreenPosition is not null)
            _ = SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x0097); // Hide without moving or activating after topology changes.
        if (msg == WmWindowPosChanging && fixedOffscreenPosition is { } position)
        {
            var changing = Marshal.PtrToStructure<WindowPosition>(lparam);
            if ((changing.Flags & 0x0002) == 0)
            {
                changing.X = position.X;
                changing.Y = position.Y;
            }
            changing.Flags |= 0x0010; // SWP_NOACTIVATE.
            Marshal.StructureToPtr(changing, lparam, false);
        }
        return DefWindowProcW(hwnd, msg, wparam, lparam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProc(nint hwnd, uint msg, nint wparam, nint lparam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        internal uint Style;
        internal WindowProc Proc;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background;
        internal string? MenuName;
        internal string ClassName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { internal nint Window, InsertAfter; internal int X, Y, Width, Height; internal uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message { internal nint Window; internal uint Id; internal nint WParam, LParam; internal uint Time; internal int X, Y; internal uint Private; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint CreateDesktopW(string desktop, nint device, nint deviceMode, uint flags, uint access, nint attributes);
    [DllImport("user32", SetLastError = true)] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32", SetLastError = true)] private static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32", SetLastError = true)] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder buffer, int length, out int needed);
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern ushort RegisterClassW(ref WindowClass wndClass);
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern bool UnregisterClassW(string name, nint instance);
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint CreateWindowExW(uint exStyle, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32", SetLastError = true)] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] private static extern nint GetForegroundWindow();
    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32", SetLastError = true, ExactSpelling = true)] private static extern int GetMessageW(out Message message, nint hwnd, uint min, uint max);
    [DllImport("user32")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32", ExactSpelling = true)] private static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32", ExactSpelling = true)] private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wparam, nint lparam);
    [DllImport("user32", SetLastError = true, ExactSpelling = true)] private static extern bool PostThreadMessageW(uint thread, uint msg, nint wparam, nint lparam);
    [DllImport("user32", SetLastError = true)] private static extern bool DestroyWindow(nint hwnd);
}
