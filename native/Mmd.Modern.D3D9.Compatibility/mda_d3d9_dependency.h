#ifndef MDA_D3D9_DEPENDENCY_H
#define MDA_D3D9_DEPENDENCY_H
#include "mda_d3d9_session.h"
#ifdef __cplusplus
extern "C" {
#endif

/* INTERNAL CPU inspection ABI. No device, HWND, host runtime, disk source
   fallback, installation or portability claim. Windows x64 native alignment.
   Every struct with size/version uses version=1. Spans exclude a terminator;
   input origins/names reject embedded NUL. Output spans are borrowed immutable
   bytes valid until Destroy. Copy them before releasing the result. All getter
   calls may run on any thread, but the caller must serialize Destroy against
   every getter/copy. Callbacks execute synchronously on the Inspect thread and
   must not unwind exceptions or reenter this inspection.

   The deployment owner must have already loaded/validated its pinned
   D3DX9_43/D3DCompiler_43 modules and keep them loaded through Inspect. This
   adapter only obtains an existing D3DX9_43 module; it never loads a DLL.
   Source/annotation/child-include strings retain the active Windows ACP,
   recorded in Info. The intercepted W effect-profile root Open uses the pinned
   compiler's UTF-8 filename; the A Function-profile root Open remains ACP.
   Internal messages/module paths use UTF-8. Compiler diagnostics retain
   their original bytes (including any trailing NUL).

   Hard limits: 256MiB/source; 512MiB copied source bytes across all profiles;
   32MiB metadata; 32767 bytes/string; 65536 include events/parameters;
   262144 annotations; 4096 generated functions; 128 include/parameter depth.
   These are adapter limits, not the product's general project-size budgets. */
typedef uint64_t Mda9DependencyInspection;
typedef struct Mda9DependencyBytesV1 { const uint8_t* data; uint64_t length; } Mda9DependencyBytesV1;
enum {
 MDA9_DEPENDENCY_PROFILE_MIP_OFF=1, MDA9_DEPENDENCY_PROFILE_MIP_ON=2,
 MDA9_DEPENDENCY_PROFILE_TEXTURE_FUNCTION=3
};
enum { MDA9_DEPENDENCY_INCLUDE_LOCAL=0, MDA9_DEPENDENCY_INCLUDE_SYSTEM=1 };
enum {
 MDA9_DEPENDENCY_REFERENCE_RESOURCE=1, MDA9_DEPENDENCY_REFERENCE_ANIMATED_PNG=2,
 MDA9_DEPENDENCY_REFERENCE_ANIMATED_GIF=3, MDA9_DEPENDENCY_REFERENCE_DEFAULT_PATH=4,
 MDA9_DEPENDENCY_REFERENCE_DEFAULT_NONE=5, MDA9_DEPENDENCY_REFERENCE_DEFAULT_HIDE=6,
 MDA9_DEPENDENCY_REFERENCE_DEFAULT_MAIN=7, MDA9_DEPENDENCY_REFERENCE_FUNCTION=8
};
enum {
 MDA9_DEPENDENCY_DIAGNOSTIC_COMPILER=1, MDA9_DEPENDENCY_DIAGNOSTIC_RESOLVER=2,
 MDA9_DEPENDENCY_DIAGNOSTIC_METADATA=3, MDA9_DEPENDENCY_DIAGNOSTIC_LIMIT=4,
 MDA9_DEPENDENCY_DIAGNOSTIC_INTERNAL=5
};
/* UINT64_MAX denotes an absent parent/parameter/profile index. */
typedef struct Mda9DependencyIncludeRequestV1 {
 uint32_t size, version;
 uint64_t rootResourceId, parentResourceId, profileIndex;
 uint32_t includeKind, depth, profileKind, reserved;
 Mda9DependencyBytesV1 rawRequestAcp, entryAcp, targetAcp;
} Mda9DependencyIncludeRequestV1;
typedef struct Mda9DependencyIncludeResponseV1 {
 uint32_t size, version;
 uint64_t resourceId; /* stable nonzero frozen origin identity, never a pointer */
 Mda9DependencyBytesV1 sourceBytes;
 Mda9DependencyBytesV1 errorUtf8; /* optional; returned status must be nonzero on failure */
} Mda9DependencyIncludeResponseV1;
/* All response spans must remain readable until Inspect returns. The adapter
   copies source bytes immediately and keeps its own Open buffer through Close.
   Resolve LOCAL against parentResourceId; SYSTEM against rootResourceId. The
   raw request must be decoded using Info.codePage before interpreting slashes.
   Return MDA9_OK only for a frozen, validated resource. Never fall back to disk
   when inspecting a restored/frozen map. Root reads are intercepted internally. */
typedef Mda9Status (MDA9_CALL *Mda9DependencyResolveV1)(void* context,
 const Mda9DependencyIncludeRequestV1*, Mda9DependencyIncludeResponseV1*);
typedef struct Mda9DependencyInspectV1 {
 uint32_t size, version;
 uint64_t rootResourceId;
 Mda9DependencyBytesV1 rootOriginAcp; /* absolute drive path; strict ACP; <MAX_PATH */
 Mda9DependencyBytesV1 rootSourceBytes;
 Mda9DependencyResolveV1 resolve;
 void* context;
 uint32_t reserved[2];
} Mda9DependencyInspectV1;
typedef struct Mda9DependencyInfoV1 {
 uint32_t size, version;
 int32_t inspectionStatus; /* MDA9_OK only if all required profiles/metadata succeeded */
 uint32_t codePage, pointerBits, reserved;
 uint64_t rootResourceId, profileCount, parameterCount, annotationCount;
 uint64_t includeCount, referenceCount, diagnosticCount;
 Mda9DependencyBytesV1 d3dxModulePathUtf8, compilerModulePathUtf8;
} Mda9DependencyInfoV1;
typedef struct Mda9DependencyProfileV1 {
 uint32_t size, version;
 uint32_t kind, compilerFlags;
 int32_t status, hresult;
 uint64_t sourceParameterIndex; /* FUNCTION only; absent for main profiles */
 Mda9DependencyBytesV1 entryAcp, targetAcp;
} Mda9DependencyProfileV1;
typedef struct Mda9DependencyParameterV1 {
 uint32_t size, version;
 uint64_t profileIndex, parentParameterIndex;
 uint32_t ordinal, arrayElement; /* arrayElement=UINT32_MAX for ordinary/member parameter */
 uint32_t parameterClass, parameterType, rows, columns, elements, structMembers;
 uint32_t flags, semanticId;
 Mda9DependencyBytesV1 nameAcp, semanticAcp;
} Mda9DependencyParameterV1;
typedef struct Mda9DependencyAnnotationV1 {
 uint32_t size, version;
 uint64_t parameterIndex;
 uint32_t ordinal, parameterType;
 int32_t stringHresult;
 uint32_t reserved;
 Mda9DependencyBytesV1 nameAcp, stringAcp;
} Mda9DependencyAnnotationV1;
typedef struct Mda9DependencyIncludeV1 {
 uint32_t size, version;
 uint64_t profileIndex, rootResourceId, parentResourceId, resourceId;
 uint32_t includeKind, depth, rootOpen, closed;
 int32_t status;
 uint32_t reserved;
 Mda9DependencyBytesV1 rawRequestAcp, errorUtf8; /* rootOpen + effect profile: raw request is UTF-8 */
} Mda9DependencyIncludeV1;
typedef struct Mda9DependencyReferenceV1 {
 uint32_t size, version;
 uint64_t profileIndex, parameterIndex;
 uint32_t kind, rowOrdinal;
 uint64_t rawOffset;
 Mda9DependencyBytesV1 annotationAcp, rawAcp, keyAcp, valueAcp, targetAcp;
} Mda9DependencyReferenceV1;
typedef struct Mda9DependencyDiagnosticV1 {
 uint32_t size, version;
 uint64_t profileIndex, parameterIndex;
 uint32_t kind, messageCodePage;
 int32_t status, hresult;
 Mda9DependencyBytesV1 annotationAcp, messageBytes;
} Mda9DependencyDiagnosticV1;

/* Inspect returns MDA9_OK once an immutable report is available, including a
   report with failed profiles. Inspect's native failure returns no handle.
   Both main profiles are always attempted (_INDEX=PSIZE15, MME_MIPMAP absent
   or empty, flags=0x1000). Host-required texture Function entries are compiled
   once per (entry,target) with NULL defines/flags=0 against the SAME origin.
   FromFile APIs obtain every source byte through our ID3DXInclude, preserving
   origin-sensitive __FILE__ behavior without rewriting the original source.
   Successful inspection is not a dependency closure or portable PMC proof. */
MDA9_API Mda9Status MDA9_CALL MdaD3D9InspectDependenciesV1(
 const Mda9DependencyInspectV1*, Mda9DependencyInspection*, Mda9ErrorV1*);
MDA9_API Mda9Status MDA9_CALL MdaD3D9DependencyInfoV1(Mda9DependencyInspection, Mda9DependencyInfoV1*);
MDA9_API Mda9Status MDA9_CALL MdaD3D9DependencyProfileV1(Mda9DependencyInspection, uint64_t, Mda9DependencyProfileV1*);
MDA9_API Mda9Status MDA9_CALL MdaD3D9DependencyParameterV1(Mda9DependencyInspection, uint64_t, Mda9DependencyParameterV1*);
MDA9_API Mda9Status MDA9_CALL MdaD3D9DependencyAnnotationV1(Mda9DependencyInspection, uint64_t, Mda9DependencyAnnotationV1*);
MDA9_API Mda9Status MDA9_CALL MdaD3D9DependencyIncludeV1(Mda9DependencyInspection, uint64_t, Mda9DependencyIncludeV1*);
MDA9_API Mda9Status MDA9_CALL MdaD3D9DependencyReferenceV1(Mda9DependencyInspection, uint64_t, Mda9DependencyReferenceV1*);
MDA9_API Mda9Status MDA9_CALL MdaD3D9DependencyDiagnosticV1(Mda9DependencyInspection, uint64_t, Mda9DependencyDiagnosticV1*);
MDA9_API Mda9Status MDA9_CALL MdaD3D9DestroyDependencyInspectionV1(Mda9DependencyInspection);

#ifdef __cplusplus
}
#endif
#endif
