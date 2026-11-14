using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

// A real, never-shown User32 window owned by the fake API. No D3D9 DLL or GPU is loaded.
public sealed class SurfaceOwnerMessageTests
{
    [Fact]
    public async Task IdleOwnerDispatchesSentAndPostedWindowMessages()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fake = new WindowSurfaceApi();
        await using var session = await Create(fake);
        Assert.False(fake.IsVisible);
        Assert.True(fake.SendPing());
        fake.PostPing();
        await fake.PostedPing.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.All(fake.MessageThreads, thread => Assert.Equal(fake.OwnerThread, thread));
    }

    [Fact]
    public async Task FailedDestroyKeepsWindowResponsiveUntilSameOwnerRetriesCleanup()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fake = new WindowSurfaceApi { DestroyFailures = 1 };
        await using var session = await Create(fake);
        await Assert.ThrowsAsync<D3D9SurfaceNativeException>(() => session.DisposeAsync().AsTask());
        Assert.DoesNotContain("unload", fake.Calls);
        Assert.True(fake.SendPing());
        await session.DisposeAsync();
        Assert.Equal(new[] { "query", "create", "destroy", "destroy", "unload" }, fake.Calls);
        Assert.All(fake.MessageThreads, thread => Assert.Equal(fake.OwnerThread, thread));
    }

    [Fact]
    public async Task CancelledStartupAndFailedDestroyRetainResponsiveCleanupOwner()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fake = new WindowSurfaceApi { BlockCreate = true, DestroyFailures = 1 };
        using var cancellation = new CancellationTokenSource();
        var creating = ExperimentalD3D9SurfaceSession.CreateForTestingAsync(() => fake, new(4, 2), cancellation.Token);
        try
        {
            await fake.CreateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            cancellation.Cancel();
        }
        finally { fake.CreateRelease.Set(); }
        var failure = await Assert.ThrowsAsync<D3D9SurfaceInitializationCleanupException>(() => creating);
        await using (failure)
        {
            Assert.DoesNotContain("unload", fake.Calls);
            Assert.True(fake.SendPing());
            await failure.DisposeAsync();
        }
        Assert.Equal(new[] { "query", "create", "destroy", "destroy", "unload" }, fake.Calls);
    }

    [Fact]
    public async Task ContinuousPostedMessagesDoNotStarveAcceptedWork()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fake = new WindowSurfaceApi();
        await using var session = await Create(fake);
        try
        {
            fake.StartMessageFlood();
            await fake.FloodEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await session.ResizeAsync(8, 8, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Contains("resize", fake.Calls);
        }
        finally { fake.StopMessageFlood(); }
    }

    [Fact]
    public async Task PrivateOwnerQuitMessageDoesNotBypassWorkOrCleanup()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fake = new WindowSurfaceApi();
        var session = await Create(fake);
        await using (session)
        {
            fake.PostQuit();
            fake.PostPing();
            await fake.PostedPing.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await session.ResizeAsync(8, 8, TestContext.Current.CancellationToken);
        }
        Assert.Equal(new[] { "query", "create", "resize", "destroy", "unload" }, fake.Calls);
    }

    private static Task<ExperimentalD3D9SurfaceSession> Create(WindowSurfaceApi fake) =>
        ExperimentalD3D9SurfaceSession.CreateForTestingAsync(() => fake, new(4, 2, queueCapacity: 1), TestContext.Current.CancellationToken);

    private sealed class WindowSurfaceApi : ID3D9SurfaceNativeApi
    {
        private const uint SentPingMessage = 0x8001, PostedPingMessage = 0x8002, FloodMessage = 0x8003;
        private readonly string className = "MDA.Test.SurfaceOwner." + Guid.NewGuid().ToString("N");
        private readonly WindowProcedure procedure;
        private nint window, instance;
        private bool registered;
        private volatile bool flooding;
        internal readonly ConcurrentQueue<string> Calls = new();
        internal readonly ConcurrentQueue<uint> MessageThreads = new();
        internal readonly TaskCompletionSource PostedPing = NewCompletion(), FloodEntered = NewCompletion(), CreateEntered = NewCompletion();
        internal readonly ManualResetEventSlim CreateRelease = new();
        internal uint OwnerThread;
        internal int DestroyFailures;
        internal bool BlockCreate;
        internal bool IsVisible => IsWindowVisible(window);

        internal WindowSurfaceApi() => procedure = WindowProc;
        public NativeAbi QueryAbi() { Calls.Enqueue("query"); return NativeAbi.Valid(); }
        public ulong Create(D3D9SurfaceSessionOptions options)
        {
            Calls.Enqueue("create");
            OwnerThread = GetCurrentThreadId();
            instance = GetModuleHandleW(null);
            var windowClass = new WindowClass
            {
                Style = 0, Procedure = procedure, ClassExtra = 0, WindowExtra = 0,
                Instance = instance, Icon = 0, Cursor = 0, Background = 0, MenuName = null, ClassName = className
            };
            if (RegisterClassW(in windowClass) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            registered = true;
            window = CreateWindowExW(0, className, "MDA CPU owner test", 0x80000000, 0, 0, 1, 1, 0, 0, instance, 0);
            if (window == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            CreateEntered.TrySetResult();
            if (BlockCreate && !CreateRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test Create was not released.");
            return 0x100000002UL;
        }
        public D3D9SurfaceCapture Capture(ulong handle, D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order) =>
            throw new NotSupportedException("This CPU-only fixture does not render.");
        public void Resize(ulong handle, int width, int height) => Calls.Enqueue("resize");
        public void Destroy(ulong handle)
        {
            Calls.Enqueue("destroy");
            if (DestroyFailures-- > 0) throw new D3D9SurfaceNativeException("destroy", -9, "test cleanup failure", 0, false);
            ReleaseWindow();
        }
        public void Dispose() { ReleaseWindow(); Calls.Enqueue("unload"); CreateRelease.Dispose(); }
        private void ReleaseWindow()
        {
            if (window != 0)
            {
                if (!DestroyWindow(window)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                window = 0;
            }
            if (registered)
            {
                if (!UnregisterClassW(className, instance)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                registered = false;
            }
        }
        internal bool SendPing() => SendMessageTimeoutW(window, SentPingMessage, 0, 0, 3, 1000, out var result) != 0 && result == 0x4d44;
        internal void PostPing() => Assert.True(PostMessageW(window, PostedPingMessage, 0, 0));
        internal void PostQuit() => Assert.True(PostThreadMessageW(OwnerThread, 0x12, 0, 0));
        internal void StartMessageFlood() { flooding = true; Assert.True(PostMessageW(window, FloodMessage, 0, 0)); }
        internal void StopMessageFlood() => flooding = false;
        private nint WindowProc(nint hwnd, uint message, nuint wparam, nint lparam)
        {
            if (message is SentPingMessage or PostedPingMessage or FloodMessage)
            {
                MessageThreads.Enqueue(GetCurrentThreadId());
                if (message == SentPingMessage) return 0x4d44;
                if (message == PostedPingMessage) PostedPing.TrySetResult();
                if (message == FloodMessage)
                {
                    FloodEntered.TrySetResult();
                    if (flooding) PostMessageW(hwnd, FloodMessage, 0, 0);
                }
                return 0;
            }
            return DefWindowProcW(hwnd, message, wparam, lparam);
        }
        private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate nint WindowProcedure(nint hwnd, uint message, nuint wparam, nint lparam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            internal uint Style;
            internal WindowProcedure Procedure;
            internal int ClassExtra, WindowExtra;
            internal nint Instance, Icon, Cursor, Background;
            [MarshalAs(UnmanagedType.LPWStr)] internal string? MenuName;
            [MarshalAs(UnmanagedType.LPWStr)] internal string ClassName;
        }
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(in WindowClass value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterClassW(string name, nint instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProcW(nint hwnd, uint message, nuint wparam, nint lparam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nuint wparam, nint lparam, uint flags, uint timeout, out nuint result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessageW(nint hwnd, uint message, nuint wparam, nint lparam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessageW(uint thread, uint message, nuint wparam, nint lparam);
    }
}
