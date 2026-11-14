// Project-authored CPU-only checks for transactional general-scene opt-in.
// Link the real bridge translation unit; no D3D device or MMD process is used.
#ifndef __cdecl
#define __cdecl
#endif
#include "mda_mme_bridge_v1.h"
#include <cstring>
#include <iostream>

namespace {
int checks = 0, failures = 0;
void check(bool value, const char* name) {
    ++checks;
    if (!value) { ++failures; std::cerr << "FAIL " << name << '\n'; }
}
struct State { int applies = 0, resolves = 0; int32_t result = 0; };
int32_t __cdecl geometry(void*, uint32_t, uint64_t, MdaMmeGeometryV1*) { return 0; }
int32_t __cdecl parent(void*, uint64_t, MdaMmeParentV1*) { return 0; }
int32_t __cdecl apply(void* value) {
    auto& state = *static_cast<State*>(value);
    ++state.applies;
    return state.result;
}
int32_t __cdecl resolve(void* value, const char*, const char*, char* output,
    uint32_t capacity, uint32_t* required) {
    ++static_cast<State*>(value)->resolves;
    const char path[] = "package/green.bmp";
    *required = sizeof path;
    if (output) {
        if (capacity < sizeof path) return MDA_MME_INVALID;
        std::memcpy(output, path, sizeof path);
    }
    return MDA_MME_OK;
}
MdaMmeCallbacksV1 geometryTable() {
    return {sizeof(MdaMmeCallbacksV1), MDA_MME_BRIDGE_VERSION, nullptr, geometry, parent};
}
MdaMmeObjectFxCallbacksV1 fxTable(State& state) {
    return {sizeof(MdaMmeObjectFxCallbacksV1), MDA_MME_BRIDGE_VERSION, &state, apply};
}
MdaMmeResourceCallbacksV1 resourceTable(State& state) {
    return {sizeof(MdaMmeResourceCallbacksV1), MDA_MME_BRIDGE_VERSION, &state, resolve};
}
void expectUnconfigured(const char* name) {
    check(!MdaMmeSceneFxV1() && !MdaMmeExplicitObjectFxV1() && !MdaMmeMappedResourcesV1(), name);
}
}

int main() {
    MdaMmeUnbindV1();
    State state;
    auto fx = fxTable(state);
    auto resources = resourceTable(state);
    expectUnconfigured("initial optional policies absent");
    check(MdaMmeBindPackageSceneFxV1(&fx, &resources) == MDA_MME_MISSING,
          "scene requires geometry before publishing either table");
    expectUnconfigured("missing geometry leaves both optional bindings absent");
    auto base = geometryTable();
    check(MdaMmeBindV1(&base) == MDA_MME_OK, "geometry registration succeeds after failed configure");

    check(MdaMmeBindPackageSceneFxV1(nullptr, &resources) == MDA_MME_INVALID, "null FX rejected");
    expectUnconfigured("null FX did not publish resource table");
    check(MdaMmeBindPackageSceneFxV1(&fx, nullptr) == MDA_MME_INVALID, "null resources rejected");
    expectUnconfigured("null resources did not publish FX table");
    for (int kind = 0; kind != 3; ++kind) {
        auto invalid = fx;
        if (kind == 0) invalid.size = sizeof invalid - 1;
        if (kind == 1) invalid.version = MDA_MME_BRIDGE_VERSION + 1;
        if (kind == 2) invalid.apply = nullptr;
        check(MdaMmeBindPackageSceneFxV1(&invalid, &resources) == MDA_MME_INVALID,
              "invalid FX table rejected atomically");
        expectUnconfigured("invalid FX table left policy state unchanged");
    }
    for (int kind = 0; kind != 3; ++kind) {
        auto invalid = resources;
        if (kind == 0) invalid.size = sizeof invalid - 1;
        if (kind == 1) invalid.version = MDA_MME_BRIDGE_VERSION + 1;
        if (kind == 2) invalid.resolve = nullptr;
        check(MdaMmeBindPackageSceneFxV1(&fx, &invalid) == MDA_MME_INVALID,
              "invalid resource table rejected atomically");
        expectUnconfigured("invalid resource table left policy state unchanged");
    }
    check(state.applies == 0 && state.resolves == 0, "configuration validation invokes no callbacks");
    check(MdaMmeRequireBoundV1() == MDA_MME_OK, "invalid optional configure preserves geometry");
    check(MdaMmeBindPackageSceneFxV1(&fx, &resources) == MDA_MME_OK, "valid scene package binds");
    check(MdaMmeSceneFxV1() && MdaMmeExplicitObjectFxV1() && MdaMmeMappedResourcesV1(),
          "scene mode retains explicit-assignment and mapped-resource semantics");

    // Changing caller-owned tables must not change the already-published binding.
    fx.context = nullptr; fx.apply = nullptr;
    resources.context = nullptr; resources.resolve = nullptr;
    check(MdaMmeApplyObjectFxV1() == MDA_MME_OK && state.applies == 1,
          "FX callback table was copied");
    uint32_t required = 0;
    check(MdaMmeResolveResourceV1("root.fx", "green.bmp", nullptr, 0, &required) == MDA_MME_OK,
          "copied resolver supports size query");
    char output[64]{};
    check(MdaMmeResolveResourceV1("root.fx", "green.bmp", output, sizeof output, &required) == MDA_MME_OK
          && std::strcmp(output, "package/green.bmp") == 0 && state.resolves == 2,
          "copied resolver retains its original context");

    fx = fxTable(state); resources = resourceTable(state);
    check(MdaMmeBindPackageObjectFxV1(&fx, &resources) == MDA_MME_ALREADY_BOUND,
          "finite configure cannot replace a scene package");
    check(MdaMmeBindPackageSceneFxV1(&fx, &resources) == MDA_MME_ALREADY_BOUND,
          "scene configure cannot replace an active scene package");
    check(MdaMmeSceneFxV1() && MdaMmeRequireBoundV1() == MDA_MME_OK,
          "failed replacement preserves scene and geometry");
    state.result = -37;
    MdaMmeBeginFrameV1();
    check(MdaMmeApplyObjectFxV1() == MDA_MME_CALLBACK_FAILED, "scene assignment failure stays explicit");
    MdaMmeErrorV1 error{sizeof error, MDA_MME_BRIDGE_VERSION, 0, 0, 0, 0, 0};
    check(MdaMmeGetErrorV1(&error) == MDA_MME_OK && error.status == MDA_MME_CALLBACK_FAILED
          && error.callback_status == -37 && error.query == MDA_MME_QUERY_OBJECT_FX,
          "scene callback retains original first-error details");
    check(MdaMmeSceneFxV1(), "callback failure does not silently switch mode");
    MdaMmeUnbindV1();
    expectUnconfigured("destroy resets scene flag and both optional tables");
    check(MdaMmeApplyObjectFxV1() == MDA_MME_OK, "destroy removes callback");

    state.result = 0;
    check(MdaMmeBindV1(&base) == MDA_MME_OK, "fresh owner binds geometry");
    check(MdaMmeBindPackageObjectFxV1(&fx, &resources) == MDA_MME_OK, "old finite package still binds");
    check(!MdaMmeSceneFxV1() && MdaMmeExplicitObjectFxV1() && MdaMmeMappedResourcesV1(),
          "old finite package remains finite");
    check(MdaMmeBindPackageSceneFxV1(&fx, &resources) == MDA_MME_ALREADY_BOUND && !MdaMmeSceneFxV1(),
          "scene opt-in cannot widen an existing finite owner");
    MdaMmeUnbindV1();

    check(MdaMmeBindV1(&base) == MDA_MME_OK, "separate optional resource setup binds geometry");
    check(MdaMmeBindResourcesV1(&resources) == MDA_MME_OK, "separate resource hook binds");
    check(MdaMmeBindPackageSceneFxV1(&fx, &resources) == MDA_MME_ALREADY_BOUND,
          "partially configured owner cannot become scene package");
    check(!MdaMmeSceneFxV1() && !MdaMmeExplicitObjectFxV1() && MdaMmeMappedResourcesV1(),
          "resource-only owner is unchanged by rejected scene configure");
    MdaMmeUnbindV1();

    check(MdaMmeBindV1(&base) == MDA_MME_OK, "separate optional FX setup binds geometry");
    check(MdaMmeBindObjectFxV1(&fx) == MDA_MME_OK, "separate FX hook binds");
    check(MdaMmeBindPackageSceneFxV1(&fx, &resources) == MDA_MME_ALREADY_BOUND,
          "FX-only owner cannot become scene package");
    check(!MdaMmeSceneFxV1() && MdaMmeExplicitObjectFxV1() && !MdaMmeMappedResourcesV1(),
          "FX-only owner is unchanged by rejected scene configure");
    MdaMmeUnbindV1();
    expectUnconfigured("all policy state released at final teardown");
    std::cout << "checks=" << checks << " failures=" << failures << '\n';
    return failures ? 1 : 0;
}
