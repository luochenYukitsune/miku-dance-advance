#ifndef MDA_D3D9_SESSION_H
#define MDA_D3D9_SESSION_H
#include <stdint.h>
#if defined(_WIN32)
# define MDA9_CALL __cdecl
# if defined(MDA_D3D9_SESSION_BUILD)
#  define MDA9_API __declspec(dllexport)
# else
#  define MDA9_API __declspec(dllimport)
# endif
#else
# define MDA9_CALL
# define MDA9_API
#endif
#ifdef __cplusplus
extern "C" {
#endif
/* Windows x64, native alignment. No packed structs, C++ types or COM pointers.
   The caller owns all pointer arguments and guarantees their readable/writable
   extents for the duration of a call. All snapshots must remain frozen.
   This DLL captures opaque-background RGBA offscreen or presents an opaque
   preview to a borrowed child HWND. Neither mode advances MMD time.
   Every packet version rejects clearColor.a<1. Accessory screen/media remains
   unsupported. Scope4 FRM5/VIS6 carries explicit original self-shadow,
   projected-ground and outline passes; CLS block v2 also carries the original
   fixed-function model selection and its source model-light channels.
   D3DX9_43 deployment and a product rendering backend are not implied.
   Every struct starts with size=sizeof(struct), version=MDA9_ABI_VERSION.
   Larger structs are accepted; extra trailing fields are never written. */
enum { MDA9_ABI_VERSION=1, MDA9_MAX_EXTENT=4096 };
typedef uint64_t Mda9Session;
typedef int32_t Mda9Status;
enum {
 MDA9_OK=0, MDA9_INVALID_ARGUMENT=-1, MDA9_ABI_MISMATCH=-2,
 MDA9_INVALID_HANDLE=-3, MDA9_WRONG_THREAD=-4, MDA9_BUSY=-5,
 MDA9_UNSUPPORTED=-6, MDA9_PACKET_INVALID=-7, MDA9_BUFFER_TOO_SMALL=-8,
 MDA9_DEVICE_FAILURE=-9, MDA9_INTERNAL_ERROR=-10, MDA9_OUT_OF_MEMORY=-11
};
enum {
 MDA9_FEATURE_SURFACE=1u, MDA9_FEATURE_UV1=2u,
 MDA9_FEATURE_SHADOW=4u, MDA9_FEATURE_OUTLINE=8u,
 MDA9_FEATURE_ACCESSORY=16u, MDA9_FEATURE_MME=32u, MDA9_FEATURE_EMM=64u
};
enum { MDA9_ORDER_ORIGINAL=0, MDA9_ORDER_OPAQUE_FIRST=1 };
enum {
 /* Mandatory explicit scope declaration. The caller must not submit a scene
    with configured MME/EMM effects as an ordinary-surface-only scene. This
    scope uses impossible synthetic host filenames to disable automatic same-
    name/[fx] discovery; original FRM1 sourcePath metadata is retained in the
    frozen snapshot but is NOT supplied to the MME file metadata getters. */
 MDA9_CAPTURE_ISOLATED_SURFACE=1u,
 MDA9_RESULT_ISOLATED_HOST_METADATA=1u, MDA9_RESULT_SOFTWARE_VERTEX_PROCESSING=2u
};
typedef struct Mda9ErrorV1 {
 uint32_t size, version;
 char* utf8;
 uint64_t capacity;
 uint64_t requiredBytes; /* includes NUL; truncation preserves UTF-8 */
 int32_t status;
 uint32_t reserved;
} Mda9ErrorV1;
typedef struct Mda9AbiV1 {
 uint32_t size, version;
 uint32_t abiVersion, pointerBits, maxWidth, maxHeight;
 uint64_t supportedFeatures;
 uint32_t reserved[2];
} Mda9AbiV1;
typedef struct Mda9CreateV1 {
 uint32_t size, version;
 uint32_t width, height, adapterOrdinal, reserved;
} Mda9CreateV1;
typedef struct Mda9ResizeV1 {
 uint32_t size, version;
 uint32_t width, height;
} Mda9ResizeV1;
typedef struct Mda9CaptureV1 {
 uint32_t size, version;
 const uint8_t* frameBytes; /* FRM1 v1/v2 or typed v3/v4/v5; exact versioned feature flags */
 uint64_t frameLength;
 const uint8_t* visualBytes; /* VIS1 v2/v3 for FRM1/2; v4/v5/v6 for FRM3/4/5; exact SHA256 */
 uint64_t visualLength;
 uint64_t requiredFeatures; /* v2 requires SURFACE|UV1; v3 matches typed UV/accessory flags */
 uint32_t flags, renderOrder;
 uint32_t width, height; /* must equal FRM1; validates caller buffer early */
 uint8_t* rgba;
 uint64_t rgbaCapacity;
 uint64_t rgbaStride; /* positive top-down stride; at least width*4 */
} Mda9CaptureV1;
typedef struct Mda9CaptureResultV1 {
 uint32_t size, version;
 uint32_t width, height;
 double sourceFrame;
 uint64_t completedCaptures, geometryGeneration;
 uint64_t rgbaBytesRequired;
 uint32_t drawCalls, flags;
 /* These are host code-path diagnostics, NOT GPU utilization/performance. */
 uint32_t effectDispatches, effectPassDraws, effectPassFailures, rawFallbackDraws;
 uint32_t noUiDiagnostics, reserved;
} Mda9CaptureResultV1;
/* Error is optional. If supplied, its size/version/buffer are validated before
   touching the session. utf8 may be NULL only when capacity==0. A short error
   buffer never replaces the operation's status. No exception crosses ABI. */
MDA9_API Mda9Status MDA9_CALL MdaD3D9QueryAbiV1(Mda9AbiV1*, Mda9ErrorV1*);
/* At most one live session per loaded DLL because the pinned host is global.
   Create binds the calling OS thread. Capture/Resize/Destroy must use exactly
   that thread. Calls never overlap or reenter; conflicting calls return BUSY.
   A failed create writes handle=0. Handles are monotonic and never recycled.
   Destroy is mandatory before unloading this DLL, on the creating thread.
   This thread owns a private hidden focus window and must dispatch Windows
   messages while waiting between calls. Never pump while reentering a call.
   WM_CLOSE does not destroy that window; Destroy owns its final teardown. */
MDA9_API Mda9Status MDA9_CALL MdaD3D9CreateV1(const Mda9CreateV1*, Mda9Session*, Mda9ErrorV1*);
MDA9_API Mda9Status MDA9_CALL MdaD3D9CaptureV1(Mda9Session, const Mda9CaptureV1*, Mda9CaptureResultV1*, Mda9ErrorV1*);
MDA9_API Mda9Status MDA9_CALL MdaD3D9ResizeV1(Mda9Session, const Mda9ResizeV1*, Mda9ErrorV1*);
MDA9_API Mda9Status MDA9_CALL MdaD3D9DestroyV1(Mda9Session, Mda9ErrorV1*);
/* Invalid input/handle/thread/buffer/packet is preflight only and leaves the
   current live session intact. A GPU failure may leave it lost; retry Resize
   or Destroy on its owner thread. A C++ exception during host BeginScene
   poisons the session: only Destroy is then allowed, not recovery by Resize.
   This does not promise arbitrary host out-of-memory rollback. A successful capture may Reset for changed
   dimensions, then reborrow all geometry and rebuild per-frame textures.
   Snapshot object IDs must name the same logical model across captures and
   resets. Allocate a new ID for a replacement logical model. */

enum { MDA9_FEATURE_PRESENT=128u };
enum { MDA9_FEATURE_OBJECT_FX=256u };
enum { MDA9_FEATURE_EFFECT_RESOURCES=512u };
enum { MDA9_CONFIGURED_OBJECT_FX=2u, MDA9_RESULT_CONFIGURED_OBJECT_FX=8u };

enum { MDA9_FEATURE_SCENE_EFFECTS=1024u };

enum { MDA9_CONFIGURED_SCENE_FX=3u, MDA9_RESULT_CONFIGURED_SCENE_FX=16u };
enum { MDA9_FX_INHERIT=0u, MDA9_FX_STANDARD=1u, MDA9_FX_FILE=2u };

typedef struct Mda9ObjectFxAssignmentV1 {
 uint32_t size, version;
 uint64_t objectId; /* stable nonzero FRM1 object ID, never enumeration index */
 int32_t subset; /* -1 whole object; >=0 material subset */
 uint32_t assignment; /* INHERIT, STANDARD, or FILE */
 const char* utf8Path; /* FILE only: absolute drive path, readable bytes, no NUL */
 uint64_t pathLength; /* excludes NUL; at most 32767, strict UTF-8 and lossless ACP */
 uint32_t reserved[2];
} Mda9ObjectFxAssignmentV1;
typedef struct Mda9ObjectFxConfigurationV1 {
 uint32_t size, version;
 const Mda9ObjectFxAssignmentV1* assignments;
 uint64_t assignmentCount; /* at most 65536; zero explicitly uses standard only */
 uint32_t reserved[2];
} Mda9ObjectFxConfigurationV1;
/* Optional INTERNAL object/subset FX slice. Configure once successfully before
   the first frame (generation==0); rejected input does not consume that choice.
   Copies rows/paths and holds root FX files open read-share-only until Destroy.
   CALLER MUST keep the complete transitive include/resource tree byte-immutable
   and present until Destroy; this native API does not freeze that tree itself.
   FILE paths must exist; unrepresentable ACP paths explicitly fail. No EMM,
   auto-discovery or scene effects. Real FRM1 source metadata is used only here.
   Whole/subset override is allowed; duplicate (objectId,subset) is rejected.
   INHERIT means absent override; STANDARD explicitly selects the existing
   standard shader. Hidden IDs remain in frames; their subset ranges validate
   when visible, while all configured FILEs still compile/admit when hidden.
   Frame requests require scope=CONFIGURED_OBJECT_FX and OBJECT_FX (plus
   SURFACE, PRESENT for preview, and UV1 when needed). Session Capture/Preview
   mode remains immutable. Old scope1 calls cannot enter this configuration.
   Admission uses the real compiled SAS: object/standard only, no offscreen,
   controls, ScriptExternal or repeat/render-pass planning; unsupported features
   fail explicitly. Failure never produces a successful ordinary fallback. */
MDA9_API Mda9Status MDA9_CALL MdaD3D9ConfigureObjectFxV1(
 Mda9Session, const Mda9ObjectFxConfigurationV1*, Mda9ErrorV1*);

typedef struct Mda9EffectResourceBindingV1 {
 uint32_t size, version;
 const char* originUtf8; uint64_t originLength;
 const char* materializedUtf8; uint64_t materializedLength;
 uint32_t reserved[2];
} Mda9EffectResourceBindingV1;
typedef struct Mda9ObjectFxConfigurationV2 {
 uint32_t size, version; /* version=2; old V1 layout and entry point stay fixed */
 const Mda9ObjectFxAssignmentV1* assignments; uint64_t assignmentCount;
 const Mda9EffectResourceBindingV1* resources; uint64_t resourceCount;
 uint32_t reserved[2];
} Mda9ObjectFxConfigurationV2;
/* Optional package-resource configuration. Copies both tables before any I/O,
   normalizes identity paths without reading origins, and leases all materialized
   files until host teardown. Every FILE root must be present in the local column.
   Only mapped paths can satisfy include/SAS lookups; no original/CWD fallback.
   Failure leaves the configuration choice unconsumed. Limits:65536 rows/table,
   32767 UTF8 bytes/path,16MiB total paths,256MiB/file,512MiB all resources.
   Callers still hold their entire immutable tree/ancestor lease through successful
   Destroy. This does not prove dependency closure or admit scene effects. */
MDA9_API Mda9Status MDA9_CALL MdaD3D9ConfigureObjectFxV2(
 Mda9Session, const Mda9ObjectFxConfigurationV2*, Mda9ErrorV1*);
/* Explicit general-scene opt-in, using the SAME version=2 copied tables,
   validation, immutable package leases and once-before-first-frame transaction
   as ConfigureObjectFxV2. A nonempty resource map is mandatory. Existing V1/V2
   object configurations keep their finite admission rules. Scene configuration
   permits the existing host's scene/pre/post, offscreen, control-object and
   ScriptExternal execution; it does not add accessory, outline, shadow or EMM
   assignment input. Every compiled dependency must resolve inside the leased
   resource map. A caller-supplied map does not prove dependency closure.
   Capture/Present require scope=CONFIGURED_SCENE_FX with SURFACE|OBJECT_FX|
   SCENE_EFFECTS (and PRESENT for preview, UV1 when needed). All descending
   render turns use one frozen sample, without advancing animation or physics.
   Failure during general-scene host execution poisons the session; only
   Destroy may follow. */
MDA9_API Mda9Status MDA9_CALL MdaD3D9ConfigureSceneFxV1(
 Mda9Session, const Mda9ObjectFxConfigurationV2*, Mda9ErrorV1*);

/* FRM4/VIS5 retains truthful material metadata for hidden models. FRM5/VIS6
   additionally requires complete explicit original passes and Original order.
   These typed capabilities are currently admitted only in scope4.
   Legacy FEATURE_EMM=64 is not the configured transport capability. */
enum { MDA9_FEATURE_MODEL_MATERIAL_METADATA=2048u, MDA9_FEATURE_EMM_CONFIGURATION=4096u };
/* Same-sample picture/video background quads: explicit scope4 FRM6/VIS7 and
   MDA9MED1 decoded RGBA only. No decoder, file access or clock is created. */
enum { MDA9_FEATURE_MEDIA=8192u };
/* Optional PresentV2 editor-only screen strokes; never part of FRM/VIS or Capture. */
enum { MDA9_FEATURE_EDITOR_OVERLAY=16384u };
enum { MDA9_CONFIGURED_EMM=4u, MDA9_RESULT_CONFIGURED_EMM=32u };
enum { MDA9_EMM_PATH_PRESENT=1u, MDA9_EMM_SHOW_PRESENT=2u, MDA9_EMM_SHOW_VALUE=4u };
enum { MDA9_EMM_DEFAULT_PRESENT=1u };
typedef struct Mda9Utf8SpanV1 { const char* data; uint64_t length; } Mda9Utf8SpanV1;
typedef struct Mda9EmmBindingV1 {
 uint32_t size, version;
 Mda9Utf8SpanV1 key; /* canonical Pmd<N>/Acs<N>, never a live enumeration index */
 uint64_t objectId;
 uint32_t kind, reserved; /* 1=model, 2=accessory */
} Mda9EmmBindingV1;
typedef struct Mda9EmmRootSeedV1 {
 uint32_t size, version;
 uint64_t objectId;
 uint32_t kind, reserved;
 Mda9Utf8SpanV1 effectPath; /* empty=frozen no-effect decision; otherwise mapped local FILE */
} Mda9EmmRootSeedV1;
typedef struct Mda9EmmEntryV1 {
 uint32_t size, version;
 Mda9Utf8SpanV1 key;
 int32_t subset; /* -1 whole; >=0 actual material subset */
 uint32_t flags; /* PATH_PRESENT, SHOW_PRESENT, SHOW_VALUE; no implicit false */
 Mda9Utf8SpanV1 path; /* present: scoped literal or mapped local FILE; absent: NULL/0 */
} Mda9EmmEntryV1;
typedef struct Mda9EmmSectionV1 {
 uint32_t size, version;
 Mda9Utf8SpanV1 name, baseName, ownerKey, parentTarget, defaultPath;
 int32_t ownerSubset;
 uint32_t flags; /* DEFAULT_PRESENT only, required on Effect, forbidden on named sections */
 const Mda9EmmEntryV1* entries;
 uint64_t entryCount;
 uint32_t reserved[2];
} Mda9EmmSectionV1;
typedef struct Mda9EmmConfigurationV1 {
 uint32_t size, version;
 const Mda9EmmSectionV1* sections; uint64_t sectionCount;
 const Mda9EmmBindingV1* bindings; uint64_t bindingCount;
 const Mda9EmmRootSeedV1* rootSeeds; uint64_t rootSeedCount;
 const Mda9EffectResourceBindingV1* resources; uint64_t resourceCount;
 uint32_t reserved[2];
} Mda9EmmConfigurationV1;
/* Configure once before the first frame. All tables/strings are bounded and
   copied before I/O; every real effect/default/seed path must be in resources'
   local column. Root seeds cover ALL live objects, including unmatched objects.
   No EMM parsing, original-path matching/discovery, CWD fallback or hot reload.
   Limits:65536 sections/bindings/seeds/resources and total entries,32767 bytes
   per string,16MiB all strings; same immutable resource lease limits as V2.
   Scope4 requires SURFACE|OBJECT_FX|SCENE_EFFECTS|EMM_CONFIGURATION, plus
   PRESENT/UV1/ACCESSORY as applicable. Every explicit/default/seed FILE compiles
   under scene admission, even if hidden or overridden. Hidden model subset
   rows require truthful retained material metadata; zero is never fabricated.
   Runtime owner/compile/target failures fail the frame and poison the session;
   Destroy is required. Native graph mutation does not promise rollback.
   This is a transport/apply API, not proof of dependency closure or general
   EMM/product support. Original bytes/encoding remain the caller's concern. */
MDA9_API Mda9Status MDA9_CALL MdaD3D9ConfigureEmmV1(
 Mda9Session, const Mda9EmmConfigurationV1*, Mda9ErrorV1*);
enum { MDA9_PRESENT_ISOLATED_SURFACE=1u };
/* Present returned D3D_OK; this is not scanout, a GPU fence or elapsed time. */
enum { MDA9_PRESENT_RESULT_API_ACCEPTED=4u };

typedef struct Mda9CreatePreviewV1 {
 uint32_t size, version;
 uint32_t width, height, adapterOrdinal, flags; /* flags must be zero */
 uint64_t targetHwnd; /* nonzero borrowed HWND, fixed for the whole session */
 uint32_t reserved[4];
} Mda9CreatePreviewV1;

typedef struct Mda9PresentV1 {
 uint32_t size, version;
 const uint8_t* frameBytes; /* same frozen FRM1 v1 through v5 packets as CaptureV1 */
 uint64_t frameLength;
 const uint8_t* visualBytes; /* same exact-version SHA-bound VIS1 v2 through v6 packets */
 uint64_t visualLength;
 uint64_t requiredFeatures; /* SURFACE|PRESENT plus configured effect features; optional UV1 */
 uint32_t flags, renderOrder; /* immutable scope 1/2/3/4; existing order enum */
 uint32_t width, height; /* must match FRM1; each 1..4096 */
 uint32_t reserved[2];
} Mda9PresentV1;

enum { MDA9_OVERLAY_SEGMENT=0u, MDA9_OVERLAY_CIRCLE=1u,
       MDA9_OVERLAY_SQUARE=2u, MDA9_OVERLAY_WEDGE=3u };
enum { MDA9_OVERLAY_DEPTH=1u, MDA9_MAX_EDITOR_OVERLAY_PRIMITIVES=1024u };
typedef struct Mda9EditorOverlayPrimitiveV1 {
 float start[2], end[2]; /* normalized screen coordinates; shapes 1/2 use start as center */
 float color[4]; /* finite straight RGBA, clamped to [0,1] when drawn */
 float width, radius; /* positive finite physical-pixel stroke width / outer radius */
 float depth[2]; /* DEPTH segments only: clipped, noninverted Classic device depths [0,1] */
 uint32_t shape, flags; /* WEDGE: two strokes from start +/- perpendicular*radius to end */
} Mda9EditorOverlayPrimitiveV1;
typedef struct Mda9PresentV2 {
 uint32_t size, version; /* version=MDA9_ABI_VERSION; V1's complete prefix stays fixed */
 const uint8_t* frameBytes;
 uint64_t frameLength;
 const uint8_t* visualBytes;
 uint64_t visualLength;
 uint64_t requiredFeatures;
 uint32_t flags, renderOrder;
 uint32_t width, height;
 uint32_t reserved[2];
 const Mda9EditorOverlayPrimitiveV1* primitives;
 uint64_t primitiveCount; /* at most 1024; copied/validated before any session mutation */
} Mda9PresentV2;

typedef struct Mda9PresentResultV1 {
 uint32_t size, version;
 uint32_t width, height; /* actual drawn target/backbuffer extent, not scanout */
 double sourceFrame;
 uint64_t completedPresents; /* incremented only after Present returns D3D_OK */
 uint64_t geometryGeneration; /* generation actually submitted by this draw */
 uint64_t readbackBytes; /* always zero; no capture/readback allocation exists */
 uint32_t drawCalls, flags;
 uint32_t effectDispatches, effectPassDraws, effectPassFailures, rawFallbackDraws;
 uint32_t noUiDiagnostics, reserved;
} Mda9PresentResultV1;

/* Optional preview exports; QueryAbiV1 reports PRESENT only with both exports.
   SHADOW=4 and OUTLINE=8 require scope4 and complete explicit FRM5/VIS6
   or FRM6/VIS7 selections; MEDIA=8192 additionally requires MED1 in FRM6.
   Older packets requesting these capabilities remain rejected.
   The same-process child HWND may belong to a different UI thread. The caller
   MUST keep this exact window alive until owner-thread Destroy completes,
   including all admitted FIFO work. IsWindow cannot prevent handle reuse.
   This API never callbacks, SendMessages, subclasses, reparents or destroys
   the borrowed window. The device retains its own private hidden focus HWND.
   Capture/Preview mode and target are immutable. Cross-mode capture/present
   returns UNSUPPORTED without GPU work. Only one session of either mode can
   be live; create/present/resize/destroy use the same OS owner thread.
   Create failure writes handle=0; failed Present leaves result bytes intact.
   Known input reserved fields must be zero; larger trailing fields untouched.
   Preview validates, uploads and draws the same ordinary surface, completes
   EndScene, then performs a same-size/format DEFAULT-pool StretchRect with
   D3DTEXF_NONE and Present(NULL,NULL,targetHwnd,NULL). It never allocates a
   SYSTEMMEM readback surface, captures, reads back, locks a readback surface
   or converts CPU RGBA. Texture-upload locks remain permitted. The initial
   presentation interval remains IMMEDIATE. Explicit typed packets and matching
   configured scope select accessories and original passes. This interface does
   not imply arbitrary MME parity, AA or product acceptance. */
MDA9_API Mda9Status MDA9_CALL MdaD3D9CreatePreviewV1(
 const Mda9CreatePreviewV1*, Mda9Session*, Mda9ErrorV1*);
MDA9_API Mda9Status MDA9_CALL MdaD3D9PresentV1(
 Mda9Session, const Mda9PresentV1*, Mda9PresentResultV1*, Mda9ErrorV1*);
/* Optional additive export, reported by FEATURE_EDITOR_OVERLAY. Nonempty rows
   require that feature bit. Rows are copied and validated before GPU work.
   After all main render turns, raw device strokes borrow the CURRENT bound
   depth and viewport into a private transparent color target, without ever
   detaching/rebinding that depth. After MME finalization and accessory-screen
   sampling, that color target is composited over the preview. This finite
   guide-visibility path does not promise arbitrary FX depth semantics. Overlay
   pixels bypass MME, Capture and the next frame's accessory screen texture.
   V1 and all FRM/VIS bytes retain their previous contract. */
MDA9_API Mda9Status MDA9_CALL MdaD3D9PresentV2(
 Mda9Session, const Mda9PresentV2*, Mda9PresentResultV1*, Mda9ErrorV1*);
#ifdef __cplusplus
}
#endif
#endif
