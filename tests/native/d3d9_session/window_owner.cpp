#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "owner_window.hpp"
#include <cstdio>

int main() {
    HWND window = mda9::detail::createOwnerWindow();
    if (!window) return 10;
    if (IsWindowVisible(window)) return 11;
    SendMessageW(window, WM_CLOSE, 0, 0);
    if (!IsWindow(window)) {
        std::puts("FAIL: WM_CLOSE destroyed the focus window before device cleanup");
        return 20;
    }
    if (!PostMessageW(window, WM_CLOSE, 0, 0)) return 21;
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    if (!IsWindow(window)) return 22;
    if (!DestroyWindow(window) || IsWindow(window)) return 23;
    if (PeekMessageW(&message, nullptr, WM_QUIT, WM_QUIT, PM_NOREMOVE)) return 24;
    std::puts("PASS: sent/posted close retained hidden focus window until explicit owner destruction; no WM_QUIT");
    return 0;
}
