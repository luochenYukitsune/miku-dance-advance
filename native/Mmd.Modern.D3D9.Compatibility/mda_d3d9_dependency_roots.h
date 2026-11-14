#ifndef MDA_D3D9_DEPENDENCY_ROOTS_H
#define MDA_D3D9_DEPENDENCY_ROOTS_H
#include "mda_d3d9_dependency.h"
#ifdef __cplusplus
extern "C" {
#endif

/* INTERNAL discovery-only CPU ABI. This call deliberately probes optional
   ORIGINAL source candidates using the same narrow CRT lookup as the fixed
   host. It is for collecting a new source snapshot, never runtime/restored-map
   lookup. It creates no device/session, loads no compiler and changes no host
   default, working directory, locale or global runtime state.

   Inputs are caller-owned ACP spans without NUL; each nonempty path must be a
   lossless ACP absolute drive path shorter than MAX_PATH in bytes and UTF-16.
   The default is optional (zero length). Symbolic EMM tokens are classified by
   the caller, not this filename API. Selection only proves _access_s(path,4)
   succeeded at discovery time; the caller must still snapshot/validate it.
   size/version follow the existing v1 ABI, and reserved fields must be zero. */
enum {
 MDA9_DEPENDENCY_ROOT_NONE=0, MDA9_DEPENDENCY_ROOT_SIBLING=1,
 MDA9_DEPENDENCY_ROOT_EMBEDDED=2, MDA9_DEPENDENCY_ROOT_DEFAULT=3,
 MDA9_DEPENDENCY_ROOT_BUFFER_CAPACITY=260
};

typedef struct Mda9DependencyRootDiscoverV1 {
 uint32_t size, version;
 Mda9DependencyBytesV1 objectOriginAcp, defaultEffectOriginAcp;
 uint32_t reserved[2];
} Mda9DependencyRootDiscoverV1;

typedef struct Mda9DependencyRootDiscoveryV1 {
 uint32_t size, version, codePage, selection;
 uint64_t selectedLength; /* excludes NUL; zero for no selected candidate */
 uint32_t splitRejected, reserved; /* CRT split failure: no fallback attempted */
} Mda9DependencyRootDiscoveryV1;

/* selectedOriginAcp must be a writable buffer of at least 260 bytes. A smaller
   buffer returns BUFFER_TOO_SMALL before any disk probe (no query/reprobe
   protocol). On success it contains selectedLength bytes and a NUL, or NUL
   alone for no candidate. Output bytes belong to the caller. No exception
   crosses the ABI; error follows the established Mda9ErrorV1 contract. */
MDA9_API Mda9Status MDA9_CALL MdaD3D9DiscoverDependencyRootV1(
 const Mda9DependencyRootDiscoverV1*, uint8_t* selectedOriginAcp,
 uint64_t selectedCapacity, Mda9DependencyRootDiscoveryV1*, Mda9ErrorV1*);

#ifdef __cplusplus
}
#endif
#endif
