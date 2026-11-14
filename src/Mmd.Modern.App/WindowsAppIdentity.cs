using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mmd.Modern.App;

internal static class WindowsAppIdentity
{
    // Keep the installed shortcuts and process identity stable across upgrades.
    internal const string AppUserModelId = "MDA.MikuDanceAdvance";

    public static void Initialize()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The Shell requires this before any application window is created.
        var result = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        if (result < 0)
            Trace.TraceWarning("Unable to set MDA's Windows application identity (HRESULT 0x{0:X8}).", result);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
