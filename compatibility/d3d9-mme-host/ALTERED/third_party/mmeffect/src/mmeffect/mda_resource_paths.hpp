// Project-owned package resource adapter. Upstream sources remain unchanged.
#ifndef MDA_MME_RESOURCE_PATHS_HPP
#define MDA_MME_RESOURCE_PATHS_HPP

#include "d3dx9.h"
#include "mda_mme_bridge_v1.h"
#include <windows.h>
#include <cstring>
#include <map>
#include <memory>
#include <string>
#include <vector>

namespace mme {
namespace mda_resource_detail {

// Interpret separators only after decoding. In CP932, a byte 0x5c can be a
// multibyte trail byte and cannot safely be scanned as a path separator.
inline bool DecodePath(const std::string& value, std::wstring& wide) {
    if (value.empty() || value.size() > 32767 || value.find('\0') != std::string::npos)
        return false;
    const UINT codePage = GetACP();
    const int count = MultiByteToWideChar(codePage, MB_ERR_INVALID_CHARS,
        value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (!count) return false;
    wide.resize(count);
    if (MultiByteToWideChar(codePage, MB_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), &wide[0], count) != count) return false;
    BOOL usedDefault = FALSE;
    const DWORD flags = codePage == CP_UTF8 ? WC_ERR_INVALID_CHARS : WC_NO_BEST_FIT_CHARS;
    BOOL* used = codePage == CP_UTF8 ? nullptr : &usedDefault;
    const int bytes = WideCharToMultiByte(codePage, flags, wide.data(), count,
                                         nullptr, 0, nullptr, used);
    if (bytes != static_cast<int>(value.size()) || usedDefault) return false;
    std::string roundtrip(bytes, '\0');
    if (WideCharToMultiByte(codePage, flags, wide.data(), count, &roundtrip[0],
                           bytes, nullptr, used) != bytes || usedDefault) return false;
    return roundtrip == value;
}
inline bool DriveAbsolute(const std::wstring& path) {
    return path.size() >= 3 && ((path[0] >= L'A' && path[0] <= L'Z') ||
        (path[0] >= L'a' && path[0] <= L'z')) && path[1] == L':' &&
        (path[2] == L'\\' || path[2] == L'/');
}
inline bool SamePath(const std::string& a, const std::string& b) {
    std::wstring x, y;
    if (!DecodePath(a, x) || !DecodePath(b, y)) return false;
    for (auto& c : x) if (c == L'/') c = L'\\';
    for (auto& c : y) if (c == L'/') c = L'\\';
    return CompareStringOrdinal(x.data(), static_cast<int>(x.size()), y.data(),
                                static_cast<int>(y.size()), TRUE) == CSTR_EQUAL;
}
struct FileHandle {
    HANDLE value = INVALID_HANDLE_VALUE;
    ~FileHandle() { if (value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};
} // namespace mda_resource_detail

inline bool MdaMappedResourcePath(const std::string& base, const std::string& requested,
                                 std::string& result, std::string& error) {
    result.clear(); error.clear();
    try {
        std::wstring baseWide, requestWide;
        if (!MdaMmeMappedResourcesV1()) { error = "package resource resolver is not bound"; return false; }
        if (!mda_resource_detail::DecodePath(base, baseWide) ||
            !mda_resource_detail::DriveAbsolute(baseWide) ||
            !mda_resource_detail::DecodePath(requested, requestWide)) {
            error = "invalid or lossy ACP package resource path"; return false;
        }
        uint32_t needed = 0;
        int32_t status = MdaMmeResolveResourceV1(base.c_str(), requested.c_str(), nullptr, 0, &needed);
        if (status != MDA_MME_OK || needed < 2 || needed > 32768) {
            error = "package resource is not mapped: " + requested + " (base: " + base + ")";
            return false;
        }
        std::vector<char> buffer(needed, static_cast<char>(0xff));
        uint32_t filled = 0;
        status = MdaMmeResolveResourceV1(base.c_str(), requested.c_str(), buffer.data(), needed, &filled);
        if (status != MDA_MME_OK || filled != needed || buffer[needed - 1] != '\0' ||
            std::memchr(buffer.data(), 0, needed - 1)) {
            error = "invalid package resource resolver result: " + requested; return false;
        }
        std::string resolved(buffer.data(), needed - 1);
        std::wstring resolvedWide;
        if (!mda_resource_detail::DecodePath(resolved, resolvedWide) ||
            !mda_resource_detail::DriveAbsolute(resolvedWide)) {
            error = "package resolver returned a nonlocal or lossy path: " + requested; return false;
        }
        result.swap(resolved);
        return true;
    } catch (...) {
        result.clear();
        try { error = "package resource resolution failed"; } catch (...) {}
        return false;
    }
}

// ID3DXInclude has exactly two slots and is not an IUnknown. This derived
// class deliberately has no virtual destructor or extra leading virtuals.
class MdaPackageInclude : public ID3DXInclude {
public:
    explicit MdaPackageInclude(const std::string& root) : root_(root) {}
    const std::string& ErrorText() const { return error_; }
    HRESULT __stdcall Open(D3DXINCLUDE_TYPE type, LPCSTR request, LPCVOID parent,
                           LPCVOID* data, UINT* bytes) override {
        if (data) *data = nullptr;
        if (bytes) *bytes = 0;
        try {
            if (!data || !bytes || !request ||
                (type != D3DXINC_LOCAL && type != D3DXINC_SYSTEM))
                return Fail("invalid include arguments", E_INVALIDARG);
            const Buffer* parentBuffer = nullptr;
            if (parent) {
                const auto found = live_.find(parent);
                if (found == live_.end()) return Fail("unknown include parent buffer", E_INVALIDARG);
                parentBuffer = &found->second;
            }
            std::string requested;
            for (size_t i = 0; i < 32768; ++i) {
                if (!request[i]) { requested.assign(request, i); break; }
            }
            if (requested.empty()) return Fail("empty or oversized include name", E_INVALIDARG);
            const std::string& base = type == D3DXINC_LOCAL && parentBuffer ? parentBuffer->path : root_;
            const unsigned depth = parentBuffer ? parentBuffer->depth + 1 : 1;
            if (depth > 128 || live_.size() >= 65536)
                return Fail("package include depth or open-count limit exceeded", E_FAIL);
            std::string path, resolutionError;
            if (!MdaMappedResourcePath(base, requested, path, resolutionError))
                return Fail(Context(type, base, requested, parentBuffer) + resolutionError, E_FAIL);
            // D3DX also passes NULL parent for direct includes, including
            // original-absolute names. The immutable map authorizes every
            // request; NULL cannot distinguish these from its root Open.
            mda_resource_detail::FileHandle file;
            file.value = CreateFileA(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
            BY_HANDLE_FILE_INFORMATION info{};
            LARGE_INTEGER length{};
            if (file.value == INVALID_HANDLE_VALUE || GetFileType(file.value) != FILE_TYPE_DISK ||
                !GetFileInformationByHandle(file.value, &info) ||
                (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) ||
                !GetFileSizeEx(file.value, &length) || length.QuadPart < 0)
                return Fail(Context(type, base, requested, parentBuffer) + "mapped file cannot be read", E_FAIL);
            const uint64_t size = static_cast<uint64_t>(length.QuadPart);
            const uint64_t fileLimit = 256ull * 1024 * 1024;
            const uint64_t totalLimit = 512ull * 1024 * 1024;
            if (size > fileLimit || size > totalLimit - outstanding_)
                return Fail(Context(type, base, requested, parentBuffer) + "package include byte budget exceeded", E_FAIL);
            Buffer entry;
            entry.path = path; entry.depth = depth; entry.size = size;
            entry.chain = parentBuffer ? parentBuffer->chain + " -> " + path : root_ + " -> " + path;
            entry.data.reset(new char[size ? static_cast<size_t>(size) : 1]);
            DWORD read = 0;
            if (size && (!ReadFile(file.value, entry.data.get(), static_cast<DWORD>(size), &read, nullptr) ||
                         read != size))
                return Fail(Context(type, base, requested, parentBuffer) + "short mapped include read", E_FAIL);
            const void* pointer = entry.data.get();
            live_.emplace(pointer, std::move(entry));
            outstanding_ += size;
            *data = pointer; *bytes = static_cast<UINT>(size);
            return S_OK;
        } catch (...) {
            return Fail("package include allocation or I/O failed", E_OUTOFMEMORY);
        }
    }
    HRESULT __stdcall Close(LPCVOID data) override {
        try {
            const auto found = live_.find(data);
            if (found == live_.end()) return Fail("unknown or already closed include buffer", E_INVALIDARG);
            outstanding_ -= found->second.size;
            live_.erase(found);
            return S_OK;
        } catch (...) { return Fail("package include close failed", E_FAIL); }
    }
    MdaPackageInclude(const MdaPackageInclude&) = delete;
    MdaPackageInclude& operator=(const MdaPackageInclude&) = delete;
private:
    struct Buffer {
        std::unique_ptr<char[]> data;
        std::string path, chain;
        uint64_t size = 0;
        unsigned depth = 0;
    };
    static std::string Context(D3DXINCLUDE_TYPE type, const std::string& base,
                               const std::string& request, const Buffer* parent) {
        return "package include (" + std::string(type == D3DXINC_LOCAL ? "LOCAL" : "SYSTEM") +
            ", base: " + base + ", request: " + request + ", chain: " +
            (parent ? parent->chain : base) + "): ";
    }
    HRESULT Fail(const std::string& message, HRESULT status) noexcept {
        if (error_.empty()) { try { error_ = message; } catch (...) {} }
        return status;
    }
    HRESULT Fail(const char* message, HRESULT status) noexcept {
        if (error_.empty()) { try { error_ = message; } catch (...) {} }
        return status;
    }
    std::string root_, error_;
    uint64_t outstanding_ = 0;
    std::map<const void*, Buffer> live_;
};
} // namespace mme
#endif
