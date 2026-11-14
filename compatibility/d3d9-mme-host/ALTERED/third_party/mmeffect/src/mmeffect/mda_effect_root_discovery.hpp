// ALTERED MDA: no-device extraction of this fixed MIT host's existing
// MmeFindEffectFileForModel filename lookup. Preserve narrow CRT buffers,
// byte bracket searches and candidate order; callers supply readability and
// the already selected EMM default. No host/model/global state is accessed.
#pragma once

#include <cstdlib>
#include <string>

namespace mme {

enum class MdaEffectRootSelection : unsigned int {
    None = 0, Sibling = 1, Embedded = 2, Default = 3
};

struct MdaEffectRootDiscovery {
    std::string path;
    MdaEffectRootSelection selection = MdaEffectRootSelection::None;
    bool splitRejected = false;
};

template<class Readable>
MdaEffectRootDiscovery MdaFindEffectRoot(const std::string& filename,
                                       const std::string& fallback,
                                       Readable readable)
{
    char drive[3] = { 0 };
    char dir[0x100] = { 0 };
    char base[0x100] = { 0 };
    char ext[0x40] = { 0 };
    if (_splitpath_s(filename.c_str(), drive, sizeof(drive), dir, sizeof(dir),
                     base, sizeof(base), ext, sizeof(ext)) != 0) {
        return { std::string(), MdaEffectRootSelection::None, true };
    }

    // (1) <dir><base>.fx beside the model/accessory source.
    char candidate[0x104];
    if (_makepath_s(candidate, sizeof(candidate), drive, dir, base, ".fx") == 0 &&
        readable(candidate)) {
        return { std::string(candidate), MdaEffectRootSelection::Sibling, false };
    }

    // (2) First byte '[' and following byte ']' in the narrow base name.
    const std::string baseName(base);
    size_t open = baseName.find('[');
    if (open != std::string::npos) {
        size_t close = baseName.find(']', open);
        if (close != std::string::npos && close > open + 1) {
            std::string embedded = baseName.substr(open + 1, close - open - 1);
            if (_makepath_s(candidate, sizeof(candidate), drive, dir,
                            embedded.c_str(), nullptr) == 0 &&
                readable(candidate)) {
                return { std::string(candidate), MdaEffectRootSelection::Embedded, false };
            }
        }
    }

    // (3) The EMM default only when readable; split failure never gets here.
    if (!fallback.empty() && readable(fallback.c_str())) {
        return { fallback, MdaEffectRootSelection::Default, false };
    }
    return {};
}

} // namespace mme
