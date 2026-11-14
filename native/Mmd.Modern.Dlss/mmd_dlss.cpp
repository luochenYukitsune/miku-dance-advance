// Thin ABI adapter to NVIDIA NGX. No DLSS algorithms are reimplemented here.
// Build against the pinned, separately licensed official SDK; see build-dlss.ps1.
#include <windows.h>
#include <wchar.h>
#include "nvsdk_ngx_helpers.h"

struct MmdDlssQuery {
    unsigned abi, sdkVersion, initResult, capabilityResult;
    unsigned available, needsDriver, driverMajor, driverMinor;
    unsigned featureInitResult, optimalResult, width, height;
    unsigned minWidth, minHeight, maxWidth, maxHeight;
};
static_assert(sizeof(MmdDlssQuery) == 64, "query ABI");
struct DeviceState {
    ID3D12Device* device; unsigned references; DeviceState* next;
    wchar_t* sdkPath; wchar_t* logPath; const wchar_t* pathList[1]; NVSDK_NGX_FeatureCommonInfo info;
};
struct Context { DeviceState* owner; NVSDK_NGX_Parameter* parameters; NVSDK_NGX_Handle* feature; unsigned width, height; };
static SRWLOCK gate = SRWLOCK_INIT;
static DeviceState* devices;
struct Guard { Guard() { AcquireSRWLockExclusive(&gate); } ~Guard() { ReleaseSRWLockExclusive(&gate); } };
#define API extern "C" __declspec(dllexport)
API unsigned mmd_dlss_abi() { return 1; }
static void Release(Context* context) {
    if (!context) return;
    if (context->feature) NVSDK_NGX_D3D12_ReleaseFeature(context->feature);
    if (context->parameters) NVSDK_NGX_D3D12_DestroyParameters(context->parameters);
    if (context->owner && --context->owner->references == 0) {
        DeviceState** position = &devices;
        while (*position && *position != context->owner) position = &(*position)->next;
        if (*position) *position = context->owner->next;
        NVSDK_NGX_D3D12_Shutdown1(context->owner->device);
        HeapFree(GetProcessHeap(), 0, context->owner->sdkPath);
        HeapFree(GetProcessHeap(), 0, context->owner->logPath);
        HeapFree(GetProcessHeap(), 0, context->owner);
    }
    HeapFree(GetProcessHeap(), 0, context);
}

// sdkDirectory contains the authentic nvngx_dlss.dll. Logs always go to the explicit D: cache path.
API unsigned mmd_dlss_open(ID3D12Device* device, const wchar_t* sdkDirectory, const wchar_t* logDirectory,
    unsigned outputWidth, unsigned outputHeight, int quality, Context** output, MmdDlssQuery* query) {
    Guard guard;
    if (!device || !sdkDirectory || !logDirectory || !output || !query || !outputWidth || !outputHeight || quality < 0 || quality > 5)
        return NVSDK_NGX_Result_FAIL_InvalidParameter;
    *output = nullptr; ZeroMemory(query, sizeof(*query)); query->abi = 1; query->sdkVersion = NVSDK_NGX_Version_API;
    auto context = static_cast<Context*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(Context)));
    if (!context) return NVSDK_NGX_Result_FAIL_OutOfGPUMemory;
    auto owner = devices;
    while (owner && owner->device != device) owner = owner->next;
    if (!owner) {
        owner = static_cast<DeviceState*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(DeviceState)));
        if (!owner) { Release(context); return NVSDK_NGX_Result_FAIL_OutOfGPUMemory; }
        size_t sdkBytes = (wcslen(sdkDirectory) + 1) * sizeof(wchar_t), logBytes = (wcslen(logDirectory) + 1) * sizeof(wchar_t);
        owner->sdkPath = static_cast<wchar_t*>(HeapAlloc(GetProcessHeap(), 0, sdkBytes));
        owner->logPath = static_cast<wchar_t*>(HeapAlloc(GetProcessHeap(), 0, logBytes));
        if (!owner->sdkPath || !owner->logPath) {
            if (owner->sdkPath) HeapFree(GetProcessHeap(), 0, owner->sdkPath);
            if (owner->logPath) HeapFree(GetProcessHeap(), 0, owner->logPath);
            HeapFree(GetProcessHeap(), 0, owner); Release(context); return NVSDK_NGX_Result_FAIL_OutOfGPUMemory;
        }
        CopyMemory(owner->sdkPath, sdkDirectory, sdkBytes); CopyMemory(owner->logPath, logDirectory, logBytes);
        owner->pathList[0] = owner->sdkPath; owner->info.PathListInfo.Path = owner->pathList; owner->info.PathListInfo.Length = 1;
        auto code = NVSDK_NGX_D3D12_Init_with_ProjectID("4b7da783-0600-4819-a075-986b59e099dd", NVSDK_NGX_ENGINE_TYPE_CUSTOM,
            "Mmd.Modern.1", owner->logPath, device, &owner->info);
        query->initResult = code;
        if (NVSDK_NGX_FAILED(code)) {
            HeapFree(GetProcessHeap(), 0, owner->sdkPath); HeapFree(GetProcessHeap(), 0, owner->logPath);
            HeapFree(GetProcessHeap(), 0, owner); Release(context); return code;
        }
        owner->device = device; owner->next = devices; devices = owner;
    } else {
        // One NGX SDK instance per actual device: do not silently reuse another component search directory.
        if (_wcsicmp(owner->sdkPath, sdkDirectory) != 0) { Release(context); return NVSDK_NGX_Result_FAIL_InvalidParameter; }
        query->initResult = NVSDK_NGX_Result_Success;
    }
    context->owner = owner; owner->references++;
    auto code = NVSDK_NGX_D3D12_GetCapabilityParameters(&context->parameters); query->capabilityResult = code;
    if (NVSDK_NGX_FAILED(code)) { Release(context); return code; }
    auto parameters = context->parameters;
    int value = 0;
    NVSDK_NGX_Parameter_GetI(parameters, NVSDK_NGX_Parameter_SuperSampling_Available, &value); query->available = value;
    value = 0; NVSDK_NGX_Parameter_GetI(parameters, NVSDK_NGX_Parameter_SuperSampling_NeedsUpdatedDriver, &value); query->needsDriver = value;
    NVSDK_NGX_Parameter_GetUI(parameters, NVSDK_NGX_Parameter_SuperSampling_MinDriverVersionMajor, &query->driverMajor);
    NVSDK_NGX_Parameter_GetUI(parameters, NVSDK_NGX_Parameter_SuperSampling_MinDriverVersionMinor, &query->driverMinor);
    NVSDK_NGX_Parameter_GetUI(parameters, NVSDK_NGX_Parameter_SuperSampling_FeatureInitResult, &query->featureInitResult);
    if (!query->available || query->needsDriver) { Release(context); return NVSDK_NGX_Result_FAIL_FeatureNotSupported; }
    float sharpness = 0;
    code = NGX_DLSS_GET_OPTIMAL_SETTINGS(parameters, outputWidth, outputHeight, static_cast<NVSDK_NGX_PerfQuality_Value>(quality),
        &query->width, &query->height, &query->maxWidth, &query->maxHeight, &query->minWidth, &query->minHeight, &sharpness);
    query->optimalResult = code;
    if (NVSDK_NGX_FAILED(code)) { Release(context); return code; }
    // Native AA uses the explicitly selected DLAA mode, never an invented quality ratio.
    if (quality == NVSDK_NGX_PerfQuality_Value_DLAA) query->width = outputWidth, query->height = outputHeight;
    context->width = query->width; context->height = query->height;
    *output = context;
    return NVSDK_NGX_Result_Success;
}
API unsigned mmd_dlss_create(Context* context, ID3D12GraphicsCommandList* commands, unsigned outputWidth, unsigned outputHeight, int quality) {
    Guard guard;
    if (!context || !commands || context->feature) return NVSDK_NGX_Result_FAIL_InvalidParameter;
    NVSDK_NGX_DLSS_Create_Params parameters = {};
    parameters.Feature.InWidth = context->width; parameters.Feature.InHeight = context->height;
    parameters.Feature.InTargetWidth = outputWidth; parameters.Feature.InTargetHeight = outputHeight;
    parameters.Feature.InPerfQualityValue = static_cast<NVSDK_NGX_PerfQuality_Value>(quality);
    parameters.InFeatureCreateFlags = NVSDK_NGX_DLSS_Feature_Flags_MVLowRes;
    return NGX_D3D12_CREATE_DLSS_EXT(commands, 1, 1, &context->feature, context->parameters, &parameters);
}
API unsigned mmd_dlss_evaluate(Context* context, ID3D12GraphicsCommandList* commands, ID3D12Resource* color,
    ID3D12Resource* depth, ID3D12Resource* motion, ID3D12Resource* responsive, ID3D12Resource* output,
    float jitterX, float jitterY, float frameMilliseconds, unsigned reset) {
    Guard guard;
    if (!context || !context->feature || !commands || !color || !depth || !motion || !output)
        return NVSDK_NGX_Result_FAIL_InvalidParameter;
    NVSDK_NGX_D3D12_DLSS_Eval_Params parameters = {};
    parameters.Feature.pInColor = color; parameters.Feature.pInOutput = output;
    parameters.pInDepth = depth; parameters.pInMotionVectors = motion;
    parameters.pInBiasCurrentColorMask = responsive;
    parameters.InJitterOffsetX = jitterX; parameters.InJitterOffsetY = jitterY;
    parameters.InMVScaleX = 1; parameters.InMVScaleY = 1;
    parameters.InRenderSubrectDimensions.Width = context->width; parameters.InRenderSubrectDimensions.Height = context->height;
    parameters.InPreExposure = 1; parameters.InExposureScale = 1;
    parameters.InFrameTimeDeltaInMsec = frameMilliseconds; parameters.InReset = reset != 0;
    return NGX_D3D12_EVALUATE_DLSS_EXT(commands, context->feature, context->parameters, &parameters);
}
API void mmd_dlss_close(Context* context) { Guard guard; Release(context); }
