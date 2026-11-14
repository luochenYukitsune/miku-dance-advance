using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace Mmd.Modern.App;

/// <summary>Shares the desktop preference and one native settings listener per visible surface.</summary>
internal static class EditorMotionPreferences
{
    private static readonly ConditionalWeakTable<TopLevel, SurfaceObserver> observers = new();

    internal static bool ClientAnimationsEnabled() => !OperatingSystem.IsWindows() ||
        !SystemParametersInfoW(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out int enabled, 0) || enabled != 0;

    // A settings notification, activation or visibility change refreshes every subscriber.
    // There is no recurring timer, and the last subscriber removes the native hook.
    internal static IDisposable Observe(TopLevel surface, Action<bool> changed) =>
        observers.GetValue(surface, static value => new SurfaceObserver(value)).Subscribe(changed);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint parameter, out int value, uint flags);

    private sealed class SurfaceObserver
    {
        private readonly TopLevel surface;
        private event Action<bool>? changed;
        private int subscribers;

        internal SurfaceObserver(TopLevel surface)
        {
            this.surface = surface;
            surface.PropertyChanged += SurfacePropertyChanged;
            if (surface is Window window) window.Activated += WindowActivated;
            if (OperatingSystem.IsWindows()) Win32Properties.AddWndProcHookCallback(surface, SettingsChanged);
        }

        internal IDisposable Subscribe(Action<bool> listener)
        {
            subscribers++;
            changed += listener;
            listener(IsMotionAllowed());
            return new Subscription(this, listener);
        }

        private bool IsMotionAllowed() => surface.IsVisible && surface.IsEffectivelyEnabled &&
            (surface is not Window window || window.WindowState != WindowState.Minimized) &&
            ClientAnimationsEnabled();

        private void SurfacePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == Visual.IsVisibleProperty || args.Property == Window.WindowStateProperty ||
                args.Property == Control.IsEffectivelyEnabledProperty)
                changed?.Invoke(IsMotionAllowed());
        }

        private void WindowActivated(object? sender, EventArgs args) => changed?.Invoke(IsMotionAllowed());

        private IntPtr SettingsChanged(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x001A /* WM_SETTINGCHANGE */) changed?.Invoke(IsMotionAllowed());
            // Observe only. Avalonia must still process the original native message.
            return IntPtr.Zero;
        }

        private void Unsubscribe(Action<bool> listener)
        {
            changed -= listener;
            if (--subscribers != 0) return;
            surface.PropertyChanged -= SurfacePropertyChanged;
            if (surface is Window window) window.Activated -= WindowActivated;
            if (OperatingSystem.IsWindows()) Win32Properties.RemoveWndProcHookCallback(surface, SettingsChanged);
            observers.Remove(surface);
        }

        private sealed class Subscription(SurfaceObserver observer, Action<bool> listener) : IDisposable
        {
            private SurfaceObserver? owner = observer;
            public void Dispose() => Interlocked.Exchange(ref owner, null)?.Unsubscribe(listener);
        }
    }
}
