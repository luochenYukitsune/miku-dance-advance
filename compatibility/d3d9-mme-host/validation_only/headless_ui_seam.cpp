// Experimental headless seam, not upstream. No menu, window hook, assignment
// dialog, offscreen mouse capture, or effect auto-reload. Keep GDI+ lifecycle
// because the upstream animated-texture implementation needs it.
#include "mme_ui.h"
#include "mme_dlg.h"
#include <gdiplus.h>
namespace mme {
HINSTANCE g_hInst = nullptr;
static ULONG_PTR gdiplusToken = 0;
void MmeUiInitializeRuntime(HINSTANCE instance) {
    if (g_hInst != nullptr) return;
    g_hInst = instance;
    Gdiplus::GdiplusStartupInput input;
    Gdiplus::GdiplusStartup(&gdiplusToken, &input, nullptr);
}
void MmeUiShutdownRuntime() {
    if (gdiplusToken) Gdiplus::GdiplusShutdown(gdiplusToken);
    gdiplusToken = 0;
    g_hInst = nullptr;
}
bool MmeUiInstallHooks(bool) { return true; }
void MmeUiInstallOffscreenSubclass(HWND) {}
void MmeUiTickAutoReload() {}
void MmeDlgRefreshIfModelCountChanged() {}
}
