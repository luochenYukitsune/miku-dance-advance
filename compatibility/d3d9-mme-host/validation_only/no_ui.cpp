#include <windows.h>
#include <cstdio>
extern "C" int g_probeDialogErrors = 0;
extern "C" int WINAPI MdaProbeMessageBoxA(HWND, LPCSTR message, LPCSTR title, UINT) {
    ++g_probeDialogErrors;
    std::fprintf(stderr, "MME no-UI diagnostic: %s: %s\n", title ? title : "", message ? message : "");
    return IDOK;
}

extern "C" {
unsigned g_probeEffectDispatches=0,g_probeEffectPassDraws=0,g_probeEffectPassFailures=0,g_probeFallbackDraws=0,g_probePass0=0,g_probePass1=0;
void MdaProbeEffectDispatch(){++g_probeEffectDispatches;}
void MdaProbeEffectPassResult(long result,int pass){if(result<0)++g_probeEffectPassFailures;else{++g_probeEffectPassDraws;if(pass==0)++g_probePass0;else if(pass==1)++g_probePass1;}}
void MdaProbeFallbackDraw(){++g_probeFallbackDraws;}
}
