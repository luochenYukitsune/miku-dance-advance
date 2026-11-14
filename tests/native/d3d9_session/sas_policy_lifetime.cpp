// CPU admission fixtures own only empty resource descriptors. They deliberately
// do not link the device/script executor. This lifetime-only shim rejects a
// real texture; no admission, parsing or execution behavior is mocked here.
#include "sas_exec.h"
#include <cstdlib>
namespace mme {
SasResource::~SasResource() {
    if (texture != nullptr) std::abort();
}
}
