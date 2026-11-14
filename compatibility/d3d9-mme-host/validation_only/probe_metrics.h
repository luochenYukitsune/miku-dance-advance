#pragma once
extern "C" {
extern unsigned g_probeEffectDispatches,g_probeEffectPassDraws,g_probeEffectPassFailures,g_probeFallbackDraws,g_probePass0,g_probePass1;
void MdaProbeEffectDispatch();
void MdaProbeEffectPassResult(long result,int pass);
void MdaProbeFallbackDraw();
}
