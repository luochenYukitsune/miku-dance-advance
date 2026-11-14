#pragma once
#include <windows.h>
extern "C" int WINAPI MdaProbeMessageBoxA(HWND, LPCSTR, LPCSTR, UINT);
#define MessageBoxA MdaProbeMessageBoxA

#include "probe_metrics.h"
