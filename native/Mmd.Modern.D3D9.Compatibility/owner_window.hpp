#pragma once
#include <windows.h>

namespace mda9::detail {

inline LRESULT CALLBACK ownerWindowProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    // A close message is not authorization to tear down the device's focus
    // window. The session alone destroys it, after releasing its D3D objects.
    if (message == WM_CLOSE) return 0;
    // Preserve STATIC's own creation/destruction bookkeeping. Calling only
    // DefWindowProc here would skip the original control's WM_NCDESTROY.
    auto original = reinterpret_cast<WNDPROC>(GetClassLongPtrW(window, GCLP_WNDPROC));
    return original ? CallWindowProcW(original, window, message, wParam, lParam)
                    : DefWindowProcW(window, message, wParam, lParam);
}

// This private, hidden window belongs to the device owner OS thread. The
// session releases its device before destroying the window and unloading.
inline HWND createOwnerWindow() {
    auto window = CreateWindowExW(0, L"STATIC", L"MDA D3D9 offscreen capture", WS_POPUP,
                                 0, 0, 1, 1, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!window) return nullptr;
    SetLastError(ERROR_SUCCESS);
    if (!SetWindowLongPtrW(window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(ownerWindowProcedure))
        && GetLastError() != ERROR_SUCCESS) {
        auto error = GetLastError();
        DestroyWindow(window);
        SetLastError(error);
        return nullptr;
    }
    return window;
}

}
