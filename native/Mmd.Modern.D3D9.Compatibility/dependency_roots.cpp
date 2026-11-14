// Project-owned explicit source discovery; no MME/device/compiler/runtime state.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define MDA_D3D9_SESSION_BUILD
#include "mda_d3d9_dependency_roots.h"
#include "mmeffect/mda_effect_root_discovery.hpp"
#include <windows.h>
#include <algorithm>
#include <cstring>
#include <io.h>
#include <new>
#include <stdexcept>
#include <string>

static_assert(sizeof(void*) == 8, "Windows x64 is required");
static_assert(sizeof(Mda9DependencyRootDiscoverV1) == 48, "root discovery input ABI");
static_assert(sizeof(Mda9DependencyRootDiscoveryV1) == 32, "root discovery result ABI");
static_assert(MAX_PATH == MDA9_DEPENDENCY_ROOT_BUFFER_CAPACITY, "root discovery path buffer ABI");
static_assert(static_cast<uint32_t>(mme::MdaEffectRootSelection::None) == MDA9_DEPENDENCY_ROOT_NONE &&
              static_cast<uint32_t>(mme::MdaEffectRootSelection::Sibling) == MDA9_DEPENDENCY_ROOT_SIBLING &&
              static_cast<uint32_t>(mme::MdaEffectRootSelection::Embedded) == MDA9_DEPENDENCY_ROOT_EMBEDDED &&
              static_cast<uint32_t>(mme::MdaEffectRootSelection::Default) == MDA9_DEPENDENCY_ROOT_DEFAULT,
              "root discovery selection ABI");

namespace {
struct Failure : std::runtime_error {
    Mda9Status status;
    Failure(Mda9Status value, const char* text) : std::runtime_error(text), status(value) {}
};
[[noreturn]] void fail(Mda9Status status, const char* text) { throw Failure(status, text); }
template<class T> bool valid(const T* value) {
    return value && value->size >= sizeof(T) && value->version == 1;
}

void setError(Mda9ErrorV1* error, Mda9Status status, const char* text) noexcept {
    if (!valid(error)) return;
    error->status = status;
    error->reserved = 0;
    const size_t count = std::strlen(text);
    error->requiredBytes = count + 1;
    // All discovery diagnostics are ASCII, so byte truncation preserves UTF-8.
    if (error->utf8 && error->capacity) {
        const size_t n = static_cast<size_t>(std::min<uint64_t>(error->capacity - 1, count));
        std::memcpy(error->utf8, text, n);
        error->utf8[n] = 0;
    }
}

std::wstring exactWide(const std::string& value, UINT page) {
    const int n = MultiByteToWideChar(page, MB_ERR_INVALID_CHARS, value.data(),
                                    static_cast<int>(value.size()), nullptr, 0);
    if (!n) fail(MDA9_UNSUPPORTED, "discovery path cannot be decoded in the active ACP");
    std::wstring wide(n, L'\0');
    if (MultiByteToWideChar(page, MB_ERR_INVALID_CHARS, value.data(),
                            static_cast<int>(value.size()), wide.data(), n) != n)
        fail(MDA9_UNSUPPORTED, "discovery ACP path conversion failed");
    BOOL used = FALSE;
    const DWORD flags = page == CP_UTF8 ? WC_ERR_INVALID_CHARS : WC_NO_BEST_FIT_CHARS;
    const int count = WideCharToMultiByte(page, flags, wide.data(), n, nullptr, 0,
                                         nullptr, page == CP_UTF8 ? nullptr : &used);
    if (count != static_cast<int>(value.size()) || used)
        fail(MDA9_UNSUPPORTED, "discovery path is not lossless ACP");
    std::string roundtrip(count, '\0');
    used = FALSE;
    if (WideCharToMultiByte(page, flags, wide.data(), n, roundtrip.data(), count,
                            nullptr, page == CP_UTF8 ? nullptr : &used) != count ||
        used || roundtrip != value)
        fail(MDA9_UNSUPPORTED, "discovery ACP path changed on roundtrip");
    return wide;
}

void validatePath(const std::string& value, UINT page) {
    if (value.size() >= MAX_PATH)
        fail(MDA9_UNSUPPORTED, "discovery path exceeds the pinned MAX_PATH byte limit");
    const std::wstring wide = exactWide(value, page);
    if (wide.size() >= MAX_PATH || wide.size() < 3 ||
        !((wide[0] >= L'A' && wide[0] <= L'Z') || (wide[0] >= L'a' && wide[0] <= L'z')) ||
        wide[1] != L':' || (wide[2] != L'\\' && wide[2] != L'/'))
        fail(MDA9_UNSUPPORTED, "discovery requires an absolute drive path shorter than MAX_PATH");
}

std::string inputPath(Mda9DependencyBytesV1 value, bool required, UINT page) {
    if ((value.length && !value.data) || (!value.length && required))
        fail(MDA9_INVALID_ARGUMENT, "invalid discovery input path span");
    if (value.length >= MAX_PATH)
        fail(MDA9_UNSUPPORTED, "discovery path exceeds the pinned MAX_PATH byte limit");
    if (value.length && std::memchr(value.data, 0, static_cast<size_t>(value.length)))
        fail(MDA9_INVALID_ARGUMENT, "discovery input path contains embedded NUL");
    if (!value.length) return {};
    std::string path(reinterpret_cast<const char*>(value.data), static_cast<size_t>(value.length));
    validatePath(path, page);
    return path;
}
} // namespace

extern "C" Mda9Status MDA9_CALL MdaD3D9DiscoverDependencyRootV1(
    const Mda9DependencyRootDiscoverV1* input, uint8_t* selectedOriginAcp,
    uint64_t selectedCapacity, Mda9DependencyRootDiscoveryV1* output, Mda9ErrorV1* error)
{
    try {
        if (error) {
            if (!valid(error)) fail(MDA9_ABI_MISMATCH, "discovery error ABI mismatch");
            if (error->reserved || (!error->utf8 && error->capacity))
                fail(MDA9_INVALID_ARGUMENT, "invalid discovery error buffer");
        }
        if (!input || !output || !selectedOriginAcp)
            fail(MDA9_INVALID_ARGUMENT, "discovery input/output is null");
        if (!valid(input) || !valid(output))
            fail(MDA9_ABI_MISMATCH, "dependency root discovery ABI mismatch");
        if (input->reserved[0] || input->reserved[1])
            fail(MDA9_INVALID_ARGUMENT, "discovery reserved fields must be zero");
        if (selectedCapacity < MDA9_DEPENDENCY_ROOT_BUFFER_CAPACITY)
            fail(MDA9_BUFFER_TOO_SMALL, "discovery output buffer must hold at least 260 bytes");
        const UINT page = GetACP();
        const std::string origin = inputPath(input->objectOriginAcp, true, page);
        const std::string fallback = inputPath(input->defaultEffectOriginAcp, false, page);
        const auto found = mme::MdaFindEffectRoot(origin, fallback,
            [](const char* candidate) { return _access_s(candidate, 4) == 0; });
        if (!found.path.empty()) validatePath(found.path, page);
        Mda9DependencyRootDiscoveryV1 result{};
        result.size = sizeof(result);
        result.version = 1;
        result.codePage = page;
        result.selection = static_cast<uint32_t>(found.selection);
        result.selectedLength = found.path.size();
        result.splitRejected = found.splitRejected ? 1u : 0u;
        std::memcpy(selectedOriginAcp, found.path.c_str(), found.path.size() + 1);
        *output = result;
        setError(error, MDA9_OK, "");
        return MDA9_OK;
    } catch (const Failure& failure) {
        setError(error, failure.status, failure.what());
        return failure.status;
    } catch (const std::bad_alloc&) {
        setError(error, MDA9_OUT_OF_MEMORY, "dependency root discovery allocation failed");
        return MDA9_OUT_OF_MEMORY;
    } catch (...) {
        setError(error, MDA9_INTERNAL_ERROR, "dependency root discovery failed");
        return MDA9_INTERNAL_ERROR;
    }
}
