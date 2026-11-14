// ALTERED reset244: release DEFAULT geometry ownership on lost device,
// retaining logical model/effect bindings until safe post-reset reacquisition.
// mme_host.cpp - MmeHost* integration layer: rehost the original MMHack.dll device interception logic
// without hooks.
//
// The original intercepts MMD device calls through a d3d9.dll proxy + IAT/vtable wrappers. This file ports
// each interception point's logic ([0x180002de0] BeginScene state machine, [0x180003cb0] EndScene,
// [0x180003a20] Clear、[0x180002780] DrawPrimitive、[0x1800027f0]
// DrawIndexedPrimitive, [FUN_180001db0] device destruction, plus hook-side standard-effect registration,
// PMM tracking and texture records) unchanged into exports the host can call directly. State capture still uses
// device-state inspection + Exp* host queries, matching the original.
#include "mme_host_api.h"

#include <cstdio>
#include <cstring>
#include <list>
#include <map>
#include <string>
#include <unordered_map>

#include "mmhack_state.h"
#include "mme_abi.h"
#include "mmeffect/mme_ui.h"
#include "mmeffect/mme_util.h"
#include "mmeffect/mme_context.h"
#include "mmeffect/mme_globals.h"
#include "mmeffect/model_data.h"
#include "mmeffect/device_initialization.h"
#include "mmeffect/host_object_cache.h"
#include "mda_mme_bridge_v1.h"

extern "C" void __cdecl MmeHostInitializeRuntime(HINSTANCE instance) {
    mme::MmeUiInitializeRuntime(instance);
}

extern "C" void __cdecl MmeHostShutdownRuntime() {
    mme::MmeUiShutdownRuntime();
}

// Internal direct access from src/exports/effect_api.cpp (not exported, C linkage): original model/accessory
// wide-character paths (model+0x24BC / acc+0x29C, the paths used by D3DXLoadMeshFromXW).
// Original MMHack preserves these wide paths throughout: hook [0x1800043b0] registers them unchanged in
// ObjData+0x30 (wstring, copied directly by sub_18000f150); [sub_18000f340] reads them and passes them to
// [FUN_18000e020] (wfopen_s parses materials/toon files without an ANSI round trip). The embedded version also accesses them
// directly: round-tripping ExpGetPmdFilename's wide->SJIS output back through CP_ACP loses non-SJIS characters
// and corrupts material/toon parsing. MMEffect.dll cannot resolve non-exported symbols through the EXE's import
// library, so the host bridge injects function pointers with MmeHostSetPathProviders (equivalent to the original
// in-process direct connection between MMHack and the EXE).
static const wchar_t* (__cdecl* g_mmdModelPathW)(int) = nullptr;
static const wchar_t* (__cdecl* g_mmdAcsPathW)(int) = nullptr;

namespace {

// Original MyDirect3DDevice9 wrapper's own fields (+0x1c/+0x20/+0x28/+0x2c and three object-cache
// maps); this project shares the original's single-device assumption.
struct MmeHostDeviceState {
    mme::DeviceInitialization initialization;     // Wrapper +0x1c lifecycle
    IDirect3DSurface9* trackedRT = nullptr;       // Wrapper +0x20 (borrowed pointer)
    int currentObject = 0;                        // Wrapper +0x28
    int endSceneFired = 0;                        // Wrapper +0x2c
    // A successful BeginScene is not evidence of a successful D3D9 Reset:
    // lost devices may silently accept rendering calls. Only OnResetDevice
    // after a successful reset may reopen scene admission.
    bool awaitingSuccessfulReset = false;
    std::list<MmhCachedObject*> objectList;       // Wrapper +0x58 node table
    std::unordered_map<unsigned long long, MmhCachedObject*> objectById;   // +0x30
    std::unordered_map<unsigned long long, uint64_t> geometryIdentities;
    std::map<int, MmhCachedObject*> objectByOrder;                          // +0x60
};

MmeHostDeviceState g_host;

// These captures and filename entries borrow caller textures; they never own a
// COM reference. After loss the caller must register surviving/rebuilt textures
// again before drawing. Do not dereference old captures or Release them here.
void MmhClearBorrowedTextureState()
{
    g_mmh.baseTexture = nullptr;
    g_mmh.sphereTexture = nullptr;
    g_mmh.texCache.clear();
    g_mmh.texUsed = 0;
    g_mmh.sphereUsed = 0;
    g_mmh.subTexUsed = 0;
    g_mmh.diffuseFlag = 0;
    g_mmh.sphereAdd = 0;
    g_mmh.toonUsed = false;
    g_mmh.toonAvailable = false;
}


// [FUN_180002de0 creation path, lines 4936-4943] Per-object data for newly detected objects. Materials are
// parsed directly from the model file by FUN_18000e020 (embedded PMX names / PMD "<model>.txt" lines and
// toon references). Accessories retain empty tables: GetMaterialName [0x180001130] serves only
// objects with isPmd == 1.
void MmhPopulateObjectData(unsigned long long id, bool isPmd, int hostIndex,
                           const wchar_t* modelPath)
{
    (void)hostIndex;
    MmhObjData& d = g_mmh.objData[id];
    d.id = id;
    d.isPmd = isPmd;
    // Wide-character model path (original: ObjData+0x30 wstring, read by FUN_18000f340 and passed to
    // FUN_18000e020): obtain the host's original wide path directly, without a narrow-character round trip.
    d.modelName = (modelPath != nullptr) ? modelPath : L"";
    d.mats.clear();
    if (isPmd) {
        // [FUN_18000e020] The original calls this before OnCreateModel, passing &ObjData.materials
        // as the output.
        MmhMaterialTableLoadFromFile(d.modelName.c_str(), &d.mats);
    }
    // Current-frame attachment comes from the host callback, not this cache.
    d.attachPmdIndex = -1;
    d.attachBoneIndex = 0;
}

}  // namespace

// ---------------------------------------------------------------------------
// Window / standard effect / file notifications (original hook-side recording paths)
// ---------------------------------------------------------------------------
void __cdecl MmeHostSetPathProviders(const wchar_t* (__cdecl* model_path)(int),
                                     const wchar_t* (__cdecl* acs_path)(int))
{
    g_mmdModelPathW = model_path;
    g_mmdAcsPathW = acs_path;
}

void __cdecl MmeHostSetMainWindow(HWND window)
{
    g_mmh.mainWindow = window;
    if (g_mmh.presentWindow == nullptr)
        g_mmh.presentWindow = window;

    // [Original MMHack DllMain] Enable debug mode if MMEffect.debug exists in the EXE directory
    // (the x64 original checks beside MMHack.dll, which shares the host EXE directory).
    // Probe only once (the host bridge may call this function repeatedly during per-frame refresh).
    static bool debugProbed = false;
    if (!debugProbed) {
        debugProbed = true;
        if (!g_mmh.debugMode) {
            char modulePath[MAX_PATH];
            if (GetModuleFileNameA(nullptr, modulePath, MAX_PATH) > 0) {
                char* slash = strrchr(modulePath, '\\');
                std::string debugFile =
                    (slash != nullptr)
                        ? std::string(modulePath, slash - modulePath + 1) +
                              "MMEffect.debug"
                        : std::string("MMEffect.debug");
                FILE* fp = nullptr;
                if (fopen_s(&fp, debugFile.c_str(), "rb") == 0 && fp != nullptr) {
                    g_mmh.debugMode = true;
                    fclose(fp);
                }
            }
        }
    }
}

void __cdecl MmeHostSetStandardEffect(void* effect)
{
    // [0x180004440 Hooked_D3DXCreateEffectFromResourceA] Register once only if
    // qword_18006E7A0 (currentEffect) is unset and the newly created effect is non-null
    // [condition !qword_18006E7A0 && v8 && *v8 at 0x1800044b6]. Later calls return directly,
    // without replacing the cached effect or re-registering the 12 technique handles. The host calls this at the end of device initialization.
    if (effect == nullptr)
        return;
    if (g_mmh.currentEffect != nullptr)
        return;
    // [0x1800044c7 sub_180006350] Clear the technique cache map before registration (original hash-map
    // node releases for the qword_18006CCD0/DAT_18006ccf0 family).
    g_mmh.tecCache.clear();
    ID3DXEffect* fx = static_cast<ID3DXEffect*>(effect);
    for (int i = 0; i < MMH_TEC_COUNT; ++i) {
        D3DXHANDLE handle = fx->GetTechniqueByName(g_tecTable[i].name);
        if (handle != nullptr)
            MmhRecordTecHandle(handle, i);
    }
    // [0x18000451d] Cache the current effect only after registering all 12 handles.
    g_mmh.currentEffect = effect;
}

void __cdecl MmeHostSetDrawnWindow(HWND window)
{
    g_mmh.presentWindow = window;
}

void __cdecl MmeHostNotifyPmmLoaded(const wchar_t* path)
{
    if (path == nullptr || path[0] == L'\0')
        return;
    g_mmh.loadedPMMFile = path;
    g_mmh.loadedPmmFlag = true;
}

void __cdecl MmeHostNotifyPmmSaved(const wchar_t* path)
{
    if (path == nullptr || path[0] == L'\0')
        return;
    g_mmh.savedPmmList.push_back(path);
}

void __cdecl MmeHostRecordTextureFile(const wchar_t* path,
                                        IDirect3DBaseTexture9* texture)
{
    if (path == nullptr || path[0] == L'\0')
        return;
    MmhRecordTextureFile(std::wstring(path), texture);
}

int __cdecl MmeHostEffectsActive(void)
{
    return (g_host.initialization.IsReady() && !g_mmh.effectsDisabled) ? 1 : 0;
}

// ---------------------------------------------------------------------------
// Per-frame interception points
// ---------------------------------------------------------------------------

// [0x180003a20] Device Clear slot 43.
HRESULT __cdecl MmeHostClear(IDirect3DDevice9* device, unsigned long rect_count,
                               const D3DRECT* rects, unsigned long flags,
                               D3DCOLOR color, float z, unsigned long stencil)
{
    IDirect3DSurface9* surf = nullptr;
    device->GetRenderTarget(0, &surf);
    if (surf != nullptr)
        surf->Release();                         // Borrowed only for comparison
    if (g_host.trackedRT == nullptr || !g_host.initialization.IsReady() ||
        g_mmh.effectsDisabled) {
        return device->Clear(rect_count, rects, flags, color, z, stencil);
    }
    int mainTarget = 0;
    if (surf == g_host.trackedRT && (flags & D3DCLEAR_TARGET) != 0) {
        g_mmh.clearColor = color;                // DAT_18006ccc4
        mainTarget = 1;
    }
    // Both native wrappers return the callback HRESULT unchanged (x64
    // 0x180003A20, x86 0x10003850), including device-loss failures.
    return OnClear(device, rect_count, rects, flags, color, z, stencil, mainTarget);
}

// [0x180002de0] Device BeginScene slot 41 - frame-start state machine.
HRESULT __cdecl MmeHostBeginScene(IDirect3DDevice9* device, int not_edit_mode)
{
    // Validate all snapshot geometry before starting a real D3D scene or mutating
    // the cache. Host keeps this snapshot and borrowed resources stable throughout.
    MdaMmeBeginFrameV1();
    if (g_host.awaitingSuccessfulReset)
        return D3DERR_DEVICELOST;
    if (MdaMmeRequireBoundV1() != MDA_MME_OK) return E_FAIL;
    const int pmdNum = ExpGetPmdNum();
    const int acsNum = ExpGetAcsNum();
    if (pmdNum < 0 || acsNum < 0) {
        MdaMmeRecordErrorV1(MDA_MME_INVALID, 0, MDA_MME_QUERY_NONE, 0);
        return E_FAIL;
    }
    struct LiveGeometry { uint64_t identity; IUnknown* resource; uint32_t kind; int materials; };
    std::map<unsigned long long, LiveGeometry> liveGeometry;
    for (int kind = MDA_MME_MODEL; kind <= MDA_MME_ACCESSORY; ++kind) {
        const int count = kind == MDA_MME_MODEL ? pmdNum : acsNum;
        for (int i = 0; i < count; ++i) {
            const auto id = static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(
                kind == MDA_MME_MODEL ? ExpGetPmdID(i) : ExpGetAcsID(i)));
            MdaMmeGeometryV1 result{sizeof(MdaMmeGeometryV1), MDA_MME_BRIDGE_VERSION, 0, nullptr};
            if (MdaMmeQueryGeometryV1(kind, id, &result) != MDA_MME_OK) return E_FAIL;
            const int materials = MdaMmeExplicitObjectFxV1()
                ? (kind == MDA_MME_MODEL ? ExpGetPmdMatNum(i) : ExpGetAcsMatNum(i)) : 0;
            if (MdaMmeExplicitObjectFxV1() && materials < 0) {
                MdaMmeRecordErrorV1(MDA_MME_INVALID, 0, MDA_MME_QUERY_OBJECT_FX, id);
                return E_FAIL;
            }
            if (!liveGeometry.emplace(id, LiveGeometry{result.identity,
                    static_cast<IUnknown*>(result.borrowed_iunknown), static_cast<uint32_t>(kind), materials}).second) {
                MdaMmeRecordErrorV1(MDA_MME_INVALID, 0, MDA_MME_QUERY_GEOMETRY, id);
                return E_FAIL;
            }
        }
    }
    g_host.endSceneFired = 0;                    // +0x2c = 0
    g_mmh.notEditMode = not_edit_mode;           // DAT_18006e796 (semantics supplied by host,
                                                 // replacing MmhProbeEditMode's
                                                 // 0x13D/0x1A1 control probing)
    g_mmh.frameTime = ExpGetFrameTime();         // DAT_18006e79c

    // Lazy MMEffect initialization: the first BeginScene calls Initialize.
    if (!g_host.initialization.EnsureReady(
        [device] { return Initialize(device); },
        [] {
            const char* msg = (g_mmh.currentEffect != nullptr)
                ? "Initialize Error"
                : "Initialize Error: failed to load default effect file. "
                  "Please check video card capability.";
            MdaProbeMessageBoxA(g_mmh.mainWindow, msg, "MikuMikuEffect", MB_ICONERROR);
            g_mmh.effectsDisabled = 1;           // DAT_18006e794
        }))
        return E_FAIL; // External probe: failure must never masquerade as a begun scene.

    HRESULT hr = device->BeginScene();           // Real slot 41 (+0x148)
    if (hr != 0)
        return hr;

    // Original MMHack RVA 0x31B3..0x31E1 also checks geometry lifetime: a PMM
    // reload may replace a model/accessory at the same address between frames.
    // MMHack probes its wrapper's reference count; we compare the current
    // geometry directly because unwrapped D3D resources may also be retained
    // by the device's previous stream binding or a captured state block.
    mme::RemoveStaleHostObjects(g_host.objectList, g_host.objectById,
        liveGeometry, [](MmhCachedObject* obj, const LiveGeometry& geometry) {
            const auto token = g_host.geometryIdentities.find(obj->id);
            // A null owned reference is an object detached by OnLostDevice,
            // not a deleted model. Keep its material/EMM bindings when the same
            // opaque model ID and object kind survive the device reset. Ordinary
            // live references retain the original generation+pointer checks.
            const bool sameKind = (obj->isPmd != 0) == (geometry.kind == MDA_MME_MODEL);
            if (MdaMmeExplicitObjectFxV1()) {
                // FRM1 hidden objects intentionally have no material payload.
                // After Reset, a detached owner must be rebuilt on a hidden/
                // revealed range change rather than retaining a stale table.
                const auto* context = mme::g_context;
                if (!context) return false;
                const auto found = context->modelRegistry.find(obj->id);
                if (found == context->modelRegistry.end() || !found->second ||
                    found->second->materialCount() != geometry.materials) return false;
            }
            return token != g_host.geometryIdentities.end() && sameKind &&
                   (obj->geometryRef == nullptr ||
                    (token->second == geometry.identity && obj->geometryRef == geometry.resource));
        }, [device](MmhCachedObject* obj) {
            OnDeleteModel(device, obj->id);
            if (obj->geometryRef != nullptr)
                obj->geometryRef->Release();
            g_mmh.objData.erase(obj->id);
            g_host.geometryIdentities.erase(obj->id);
            delete obj;
        });

    // Reacquire detached geometry only after full snapshot validation and a
    // successful real BeginScene. Failed Reset/query attempts must leave the
    // cache detached and must not re-pin a stale DEFAULT resource. Stale IDs
    // were removed above; every surviving entry has a checked live geometry.
    for (MmhCachedObject* obj : g_host.objectList) {
        if (obj->geometryRef != nullptr)
            continue;
        const auto& geometry = liveGeometry.at(obj->id);
        geometry.resource->AddRef();
        obj->geometryRef = geometry.resource;
        g_host.geometryIdentities.at(obj->id) = geometry.identity;
    }

    // --- Stage 3: cache new objects (OnCreateModel) ----------------------------------------------
    for (int i = 0; i < pmdNum; i++) {
        unsigned long long id = (unsigned long long)(uintptr_t)ExpGetPmdID(i);
        if (g_host.objectById.find(id) != g_host.objectById.end())
            continue;
        MmhCachedObject* obj = new MmhCachedObject();
        obj->id = id;
        obj->isPmd = 1;
        // Original RVA 0x35C0/0x35D7 captures the first registered geometry
        // wrapper in the host record and retains it until deletion.
        obj->geometryRef = liveGeometry.at(id).resource;
        g_host.geometryIdentities[id] = liveGeometry.at(id).identity;
        if (obj->geometryRef)
            obj->geometryRef->AddRef();
        // The embedded engine uses the Windows ANSI code page for paths,
        // not the exported API's fixed-size Shift-JIS buffer. Convert directly
        // from the loaded wide path so non-SJIS names cannot become empty.
        const wchar_t* widePath = g_mmdModelPathW ? g_mmdModelPathW(i) : nullptr;
        if (widePath) {
            obj->fileName = mme::MmeWideToAnsi(widePath);
        } else {
            const char* fname = ExpGetPmdFilename(i);
            if (fname) obj->fileName = fname;
        }
        MmhPopulateObjectData(id, true, i,
                              (g_mmdModelPathW != nullptr) ? g_mmdModelPathW(i) : nullptr);
        obj->data = MmhGetObjectData(id);
        g_host.objectList.push_back(obj);
        g_host.objectById[id] = obj;
        OnCreateModel(device, id, obj->fileName.c_str(), obj->isPmd,
                      (unsigned int)ExpGetPmdMatNum(i), nullptr, nullptr);
        if (widePath) {
            if (auto* model = mme::MmeFindOrCreateModelEntry(id))
                model->setDisplayFilename(widePath);
        }
    }
    for (int i = 0; i < acsNum; i++) {
        unsigned long long id = (unsigned long long)(uintptr_t)ExpGetAcsID(i);
        if (g_host.objectById.find(id) != g_host.objectById.end())
            continue;
        MmhCachedObject* obj = new MmhCachedObject();
        obj->id = id;
        obj->isPmd = 0;
        // Accessory counterpart: original RVA 0x377D/0x3795.
        obj->geometryRef = liveGeometry.at(id).resource;
        g_host.geometryIdentities[id] = liveGeometry.at(id).identity;
        if (obj->geometryRef)
            obj->geometryRef->AddRef();
        // The embedded engine uses the Windows ANSI code page for paths,
        // not the exported API's fixed-size Shift-JIS buffer. Convert directly
        // from the loaded wide path so non-SJIS names cannot become empty.
        const wchar_t* widePath = g_mmdAcsPathW ? g_mmdAcsPathW(i) : nullptr;
        if (widePath) {
            obj->fileName = mme::MmeWideToAnsi(widePath);
        } else {
            const char* fname = ExpGetAcsFilename(i);
            if (fname) obj->fileName = fname;
        }
        MmhPopulateObjectData(id, false, i,
                              (g_mmdAcsPathW != nullptr) ? g_mmdAcsPathW(i) : nullptr);
        obj->data = MmhGetObjectData(id);
        g_host.objectList.push_back(obj);
        g_host.objectById[id] = obj;
        OnCreateModel(device, id, obj->fileName.c_str(), obj->isPmd,
                      (unsigned int)ExpGetAcsMatNum(i), nullptr, nullptr);
        if (widePath) {
            if (auto* model = mme::MmeFindOrCreateModelEntry(id))
                model->setDisplayFilename(widePath);
        }
    }

    // --- Stage 4: rebuild the draw-order map ----------------------------------------------------
    g_host.objectByOrder.clear();
    for (int i = 0; i < pmdNum; i++) {
        unsigned long long id = (unsigned long long)(uintptr_t)ExpGetPmdID(i);
        std::unordered_map<unsigned long long, MmhCachedObject*>::iterator f =
            g_host.objectById.find(id);
        if (f != g_host.objectById.end())
            g_host.objectByOrder[ExpGetPmdOrder(i)] = f->second;
    }
    for (int i = 0; i < acsNum; i++) {
        unsigned long long id = (unsigned long long)(uintptr_t)ExpGetAcsID(i);
        std::unordered_map<unsigned long long, MmhCachedObject*>::iterator f =
            g_host.objectById.find(id);
        if (f != g_host.objectById.end())
            g_host.objectByOrder[ExpGetAcsOrder(i)] = f->second;
    }

    // World matrix and inverse (used for lighting matrix calculations)
    device->GetTransform(D3DTS_WORLDMATRIX(0), &g_mmh.worldMatrix);
    D3DXMatrixInverse(reinterpret_cast<D3DXMATRIX*>(&g_mmh.worldInverse), nullptr,
                      reinterpret_cast<const D3DXMATRIX*>(&g_mmh.worldMatrix));

    // Track the current render target (borrowed; not released on destruction, matching the original).
    g_host.trackedRT = nullptr;                  // +0x20 = 0
    device->GetRenderTarget(0, &g_host.trackedRT);
    if (g_host.trackedRT != nullptr)
        g_host.trackedRT->Release();

    g_mmh.currentSubsetIndex = -1;               // DAT_18006ccc0 = -1
    g_host.currentObject = 0;                    // +0x28 = 0

    if (MdaMmeExplicitObjectFxV1()) {
        // Reset the previous frame before assignment/planning. General mode
        // starts at the original host's one-turn sentinel; the existing
        // planner may request N+1 turns, consumed by the adapter as N..0.
        // Finite-object mode preserves its existing main-scene turn zero.
        ExpSetRenderRepeatCount(MdaMmeSceneFxV1() ? 1 : 0);
        if (MdaMmeApplyObjectFxV1() != MDA_MME_OK) {
            // BeginScene already succeeded. The caller cannot mark started
            // until this function returns; balance the actual scene here.
            g_host.endSceneFired = 1;
            device->EndScene();
            return E_FAIL;
        }
    }
    OnBeginScene(device);                        // DAT_18006e800
    if (MdaMmeExplicitObjectFxV1() && !MdaMmeSceneFxV1()) {
        const auto* context = mme::g_context;
        if (!context || !context->passPlanA.empty() || !context->passPlanB.empty() ||
            !context->renderPassList.empty() || ExpGetRenderRepeatCount() != 0 ||
            context->lastRepeatCount != 1) {
            MdaMmeRecordErrorV1(MDA_MME_INVALID, 0, MDA_MME_QUERY_OBJECT_FX, 0);
            g_host.endSceneFired = 1;
            device->EndScene();
            return E_FAIL;
        }
    }
    return 0;
}

// [0x180003b50/0x180003bd0/0x180003c50] Slots 34/30/32: reading the tracked render target before EndScene
// triggers OnEndScene once, early (back-buffer capture/AVI sampling path).
void __cdecl MmeHostPreRenderTargetCopy(IDirect3DDevice9* device,
                                        IDirect3DSurface9* source)
{
    if (device == nullptr || source == nullptr)
        return;
    // MMHack's copy interceptors only fire when the copy reads the render
    // target captured at BeginScene (wrapper+0x20).
    if (g_host.endSceneFired == 0 && source == g_host.trackedRT) {
        OnEndScene(device);
        g_host.endSceneFired = 1;
    }
}

// [0x180003cb0] Device EndScene slot 42.
HRESULT __cdecl MmeHostEndScene(IDirect3DDevice9* device)
{
    if (g_host.endSceneFired == 0) {
        OnEndScene(device);
        g_host.endSceneFired = 1;
    }
    return device->EndScene();
}

// ---------------------------------------------------------------------------
// Draw interception points
// ---------------------------------------------------------------------------

namespace {

// [DIP lines 4238-4281] Fixed-function texture capture.
void MmhCaptureFixedFunctionTextureState(IDirect3DDevice9* device)
{
    (void)device;
    g_mmh.effectFileUsed = false;         // DAT_18006e795 = 0
    // Reset flag dword: bytes 0..2 = 0, byte 3 = (kind != 1)
    g_mmh.texUsed = 0;
    g_mmh.sphereUsed = 0;
    g_mmh.subTexUsed = 0;
    g_mmh.diffuseFlag = (g_mmh.currentObjectKind != 1) ? 1 : 0;
    g_mmh.sphereAdd = 0;                  // DAT_18006e797 = 0
    g_mmh.baseTexture = nullptr;          // DAT_18006e7a8 = 0
    g_mmh.sphereTexture = nullptr;        // DAT_18006e7b0 = 0

    int startSampler = (g_mmh.currentObjectKind == 1) ? 1 : 0;
    for (int s = startSampler; s < 3; s++) {
        DWORD colorOp = 1;
        device->GetTextureStageState(s, D3DTSS_COLOROP, &colorOp);
        if (colorOp == D3DTOP_DISABLE)
            break;                        // Texture-stage cascade has ended
        IDirect3DBaseTexture9* tex = nullptr;
        device->GetTexture(s, &tex);
        if (tex == nullptr)
            continue;
        tex->Release();                   // Borrowed capture
        DWORD ttf = 0;
        device->GetTextureStageState(s, D3DTSS_TEXTURETRANSFORMFLAGS, &ttf);
        if (ttf == 2) {                   // D3DTTFF_COUNT2 -> sphere-map stage
            g_mmh.sphereTexture = tex;    // DAT_18006e7b0
            g_mmh.sphereUsed = 1;         // byte @0x18006e789 = 1
            DWORD tci = 0;
            device->GetTextureStageState(s, D3DTSS_TEXCOORDINDEX, &tci);
            if (tci == 0x10000)
                g_mmh.sphereAdd = (colorOp == 7 /* D3DTOP_ADD */) ? 1 : 0;
            else {
                // [0x180002C8D] Register the regular-UV-driven sphere-map stage as the subtexture (cd2) flag
                // -> GetSphereMapMode returns 3.
                g_mmh.subTexUsed = 1;     // byte @0x18006e78a = 1
                g_mmh.sphereAdd = 0;      // DAT_18006e797 = 0
            }
        } else {
            g_mmh.texUsed = 1;            // byte @0x18006e788 = 1
            g_mmh.baseTexture = tex;      // DAT_18006e7a8
        }
    }
}

// [DIP lines 4282-4341] Effect (shader) texture capture.
void MmhCaptureEffectTextureState(IDirect3DDevice9* device)
{
    g_mmh.baseTexture = nullptr;          // DAT_18006e7a8 = 0
    g_mmh.sphereTexture = nullptr;        // DAT_18006e7b0 = 0
    ID3DXEffect* effect = static_cast<ID3DXEffect*>(g_mmh.currentEffect);
    if (effect != nullptr) {
        // [0x1d8 = ID3DXEffect::GetCurrentTechnique()]
        D3DXHANDLE hTec = effect->GetCurrentTechnique();
        // [FUN_1800061a0] Handle -> table index; unknown handles map to 0 (ColorRenderTec),
        // matching newly inserted cache-node behavior.
        int idx = MmhTecCacheIndex((const char*)hTec);
        if (idx < 0)
            idx = 0;
        if (idx < MMH_TEC_COUNT) {
            DWORD flags = g_tecTable[idx].flags;
            g_mmh.texUsed = (unsigned char)(flags & 0xFF);
            g_mmh.sphereUsed = (unsigned char)((flags >> 8) & 0xFF);
            g_mmh.subTexUsed = (unsigned char)((flags >> 16) & 0xFF);
            g_mmh.diffuseFlag = (unsigned char)((flags >> 24) & 0xFF);
            if (g_mmh.texUsed != 0) {
                // [0x180004310] GetTexture(1, &baseTexture)
                device->GetTexture(1, &g_mmh.baseTexture);
                if (g_mmh.baseTexture != nullptr)
                    g_mmh.baseTexture->Release();    // Borrowed
            }
            if (g_mmh.sphereUsed != 0) {
                // [0x180004316] GetTexture(texUsed ? 2 : 1, &sphereTexture)
                device->GetTexture(g_mmh.texUsed != 0 ? 2 : 1, &g_mmh.sphereTexture);
                if (g_mmh.sphereTexture != nullptr)
                    g_mmh.sphereTexture->Release();  // Borrowed
            }
        } else {
            // [0x1800027f0 line 4334] Technique absent from cache: clear flags
            g_mmh.texUsed = 0;
            g_mmh.sphereUsed = 0;
            g_mmh.subTexUsed = 0;
            g_mmh.diffuseFlag = 0;
        }
        g_mmh.sphereAdd = 0;
        // [0xb8 = ID3DXBaseEffect::GetBool] Read the "spadd" annotation;
        // DAT_18006e797 = (result != 0)。
        BOOL spadd = 0;
        if (effect->GetBool("spadd", &spadd) == 0 && spadd != 0)
            g_mmh.sphereAdd = 1;
        g_mmh.effectFileUsed = true;      // DAT_18006e795 = 1
    }
}

}  // namespace

// [0x180002780] Device DrawPrimitive slot 81. When effects are active, do not forward: reset host state and
// call MMEffect!OnDrawPrimitive instead (it reissues the draw).
HRESULT __cdecl MmeHostDrawPrimitive(IDirect3DDevice9* device,
                                       D3DPRIMITIVETYPE type,
                                       unsigned int start_vertex,
                                       unsigned int primitive_count)
{
    if (g_mmh.effectsDisabled || g_host.endSceneFired != 0) {
        return device->DrawPrimitive(type, start_vertex, primitive_count);   // Forward unchanged
    }
    g_mmh.currentSubsetIndex = -1;        // DAT_18006ccc0 = -1
    g_host.currentObject = 0;             // +0x28 = 0
    g_mmh.currentModelID = 0;             // DAT_18006e868 = 0
    g_mmh.currentObjectKind = 0;          // DAT_18006e870 = 0
    g_mmh.currentDrawType = 0;            // DAT_18006e798 = 0
    g_mmh.effectFileUsed = false;         // DAT_18006e795 = 0
    OnDrawPrimitive(device, type, start_vertex, primitive_count);
    return 0;
}

// [0x1800027f0] Device DrawIndexedPrimitive slot 82.
HRESULT __cdecl MmeHostDrawIndexedPrimitive(IDirect3DDevice9* device,
                                              D3DPRIMITIVETYPE type,
                                              int base_vertex_index,
                                              unsigned int min_vertex_index,
                                              unsigned int vertex_count,
                                              unsigned int start_index,
                                              unsigned int primitive_count)
{
    if (g_mmh.effectsDisabled || g_host.endSceneFired != 0) {
        return device->DrawIndexedPrimitive(type, base_vertex_index, min_vertex_index,
                                            vertex_count, start_index, primitive_count);
    }

    // Get (and immediately release) the current vertex shader to identify the shader path.
    // [0x1800027f0: GetVertexShader through real slot 93 (+0x2e8)]
    IDirect3DVertexShader9* vs = nullptr;
    device->GetVertexShader(&vs);
    if (vs != nullptr)
        vs->Release();

    int objIdx = ExpGetCurrentObject();
    if (objIdx == 0) {
        g_host.currentObject = 0;
        g_mmh.currentDrawType = 0;        // DAT_18006e798
        g_mmh.currentSubsetIndex = -1;    // DAT_18006ccc0 = -1
        g_mmh.currentModelID = 0;         // DAT_18006e868
        g_mmh.currentObjectKind = 0;      // DAT_18006e870
    } else {
        if (g_host.currentObject != objIdx) {
            g_host.currentObject = objIdx;
            // Look up the object in the draw-order map (missing entries retain null defaults).
            std::map<int, MmhCachedObject*>::iterator f =
                g_host.objectByOrder.find(objIdx);
            MmhCachedObject* obj = (f != g_host.objectByOrder.end()) ? f->second : nullptr;
            g_mmh.currentModelID = obj ? obj->id : 0;               // DAT_18006e868
            g_mmh.currentObjectKind = obj ? obj->isPmd : 0;         // DAT_18006e870
        }
        g_mmh.currentSubsetIndex = ExpGetCurrentMaterial();         // DAT_18006ccc0
        g_mmh.currentDrawType = ExpGetCurrentTechnic();             // DAT_18006e798
    }

    // Blend-mode flag: GetRenderState(D3DRS_DESTBLEND = 0x14); set 1 when destination blending is
    // D3DBLEND_ONE(2). [GetRenderState through real slot 58 (+0x1d0)]
    {
        DWORD destBlend = 0;
        g_mmh.blendMode = 0;              // DAT_18006e7b8 = 0
        device->GetRenderState(D3DRS_DESTBLEND, &destBlend);
        if (destBlend == 2)
            g_mmh.blendMode = 1;
    }

    if (vs == nullptr)
        MmhCaptureFixedFunctionTextureState(device);
    else
        MmhCaptureEffectTextureState(device);

    OnDrawIndexedPrimitive(device, type, base_vertex_index, min_vertex_index,
                           vertex_count, start_index, primitive_count);
    return 0;
}

// ---------------------------------------------------------------------------
// Device lifecycle
// ---------------------------------------------------------------------------
void __cdecl MmeHostOnLostDevice(IDirect3DDevice9* device)
{
    g_host.awaitingSuccessfulReset = true;
    MmhClearBorrowedTextureState();
    if (!g_host.initialization.IsReady())
        return;
    OnLostDevice(device);
    // Effects/stateblocks release their own GPU references first. The host's
    // per-model reference is separate ownership and would otherwise prevent
    // IDirect3DDevice9::Reset for DEFAULT-pool vertex buffers. Keep the logical
    // cached object and generation record: OnDelete/Create would erase authored
    // EMM/material/visibility overrides. Null means detached until a checked
    // post-reset BeginScene; repeated lost notifications remain idempotent.
    for (MmhCachedObject* obj : g_host.objectList) {
        if (obj->geometryRef == nullptr)
            continue;
        obj->geometryRef->Release();
        obj->geometryRef = nullptr;
    }
}

void __cdecl MmeHostOnResetDevice(IDirect3DDevice9* device)
{
    // Caller invokes this only after a real successful Reset, on the owning
    // device thread. Guard failed-reset/lost devices before any effect calls.
    if (device == nullptr || device->TestCooperativeLevel() != D3D_OK)
        return;
    if (g_host.initialization.IsReady())
        OnResetDevice(device);
    g_host.awaitingSuccessfulReset = false;
}

// [FUN_180001db0 + FUN_180001c90] Device destruction bookkeeping: MMEffect!Cleanup, release geometry references
// captured by each cached object, clear object caches and the +0x1c flag. The tracked render target
// (+0x20) is borrowed (already released inside BeginScene) and is deliberately not released again here.
void __cdecl MmeHostDestroyDevice(IDirect3DDevice9* device)
{
    // Original Release (0x180001D50 -> 0x180001DB0) calls Cleanup even when
    // Initialize failed: it may already have allocated the context/engine.
    Cleanup(device);
    for (MmhCachedObject* obj : g_host.objectList) {
        if (obj->geometryRef != nullptr)
            obj->geometryRef->Release();
        delete obj;
    }
    g_host.objectList.clear();
    g_host.objectById.clear();
    g_host.geometryIdentities.clear();
    g_host.objectByOrder.clear();
    g_host.currentObject = 0;
    g_host.initialization.Reset();
    g_host.endSceneFired = 0;
    g_host.awaitingSuccessfulReset = false;
    g_host.trackedRT = nullptr;
    // Cleanup above can still read the caller-owned standard effect. Only now
    // drop borrowed pointers/handles: the caller releases its COM owners later.
    g_mmh.currentEffect = nullptr;
    g_mmh.tecCache.clear();
    mme::g_currentEffect = nullptr;
    for (auto& handle : mme::g_paramHandles)
        handle = nullptr;
    MmhClearBorrowedTextureState();
    g_mmh.objData.clear();
    g_mmh.currentModelID = 0;
    g_mmh.currentDrawType = 0;
    g_mmh.currentObjectKind = 0;
    g_mmh.currentObjectIndex = 0;
    g_mmh.currentSubsetIndex = -1;
    g_mmh.effectFileUsed = false;
    g_mmh.effectsDisabled = false;
    g_mmh.blendMode = 0;
    g_mmh.zbufferUsed = false;
    MdaMmeUnbindV1();
}
