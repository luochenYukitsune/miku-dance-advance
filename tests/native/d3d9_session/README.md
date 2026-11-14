# Native session contract diagnostics

These Python standard-library scripts test the Windows x64 ABI in
`native/Mmd.Modern.D3D9.Compatibility/mda_d3d9_session.h`. Importing them performs
no graphics work. `verify.py` explicitly loads the supplied DLL and creates a
hidden offscreen D3D9 device; run it serially with other native/GPU validation.
It does not build, install, show a window, advance MMD time, or read user assets.
Run without Python `-O` because verification uses assertions.

The scene opt-in checkpoint adds `scene_fx_hook.cpp` (link the real
`mda_mme_bridge_v1.cpp`) and `scene_fx_policy.cpp` (link the fixture-only
`sas_policy_lifetime.cpp`). These CPU tests cover atomic optional-table binding,
mode isolation and parsed-SAS validity gates. The lifetime shim refuses real
textures; it does not replace admission or device/script execution. Real scene
pixels, failures and hidden Present are separate native runtime checks, not
implied by these CPU tests. Existing scope1/2 contracts remain distinct from
the native scope3/feature1024 opt-in.

The original `verify.py` pins the surface-only capability mask (1). For the
additional-UV checkpoint (surface + UV mask 3), use `verify_uv.py`. Its defaults
name the explicitly pinned local UV274 DLL, legacy session253 build05 DLL,
runtime267 official Microsoft 43 pair and generic-final historical input
manifest; missing or changed inputs fail instead of finding installed copies.
It starts legacy/current workers serially, each with its own hidden device.

```powershell
python tests/native/d3d9_session/verify_uv.py --phase full --output C:/validation/new-uv-pixels
```

The run compares eleven ordinary-surface captures from both exact DLLs,
checks UV1 atlas sampling, multiply/add, no-diffuse behavior, Toon alpha and
raw sampled alpha, rejected packets, and three UV variants across Reset and
Destroy/recreate. Pixels, row/tail canaries, counters, full runtime module paths
and SHA256 are retained. In this ordinary-surface path the caller has already
activated its SM3 pass; the host's `rawFallbackDraws` counts direct draw
forwarding with that shader still bound. It does not prove fixed-function
rendering or configured MME execution. All images are purpose-labelled
original diagnostic triangles, with no normal-model or preview claim.

```powershell
python tests/native/d3d9_session/verify.py --dll C:/validation/Mmd.Modern.D3D9.Compatibility.dll --output C:/validation/new-session-run
```

`--output` must be a new directory. The run generates six tiny original fixtures
there, then checks structure/version/error-buffer contracts, owner-thread
rejection, one live session, invalid packets and feature requests, 32/64 pixel
captures, source frames 0.5/1.5, geometry motion, repeat determinism, exact hidden
clear, transparent-background rejection, row padding/tail canaries, resize and
destroy/recreate. Valid capture counts are checked across rejected calls and
restart at one in the recreated session.

Every `diagnostic-*.rgba` is top-down tightly packed RGBA8 with dimensions and
SHA256 in `report.json`. **These are diagnostic synthetic triangles/clear images,
not normal-material or MME visual acceptance.** The default image checks use
clear values, nonempty coverage, rightward motion and byte-identical repeats;
they do not duplicate the native shading implementation. Native no-UI counters
are recorded per capture; the test does not inspect the desktop for windows.
Reports retain raw call statuses/errors, capture results, script/DLL/fixture
hashes, counts and failures, including cleanup failures.

An independently built existing probe can be requested explicitly with both
`--reference-exe C:/validation/frame-probe.exe` and
`--reference-shader C:/validation/surface.fx`. Its CLI must support
`--surface-auto FRAME RGBA REPORT VISUAL SHADER`. The default run has no baseline
executable or historical evidence dependency.

For CPU-only fixture serialization/readback without loading any DLL:

```powershell
python tests/native/d3d9_session/generate.py --output C:/validation/new-tiny-fixtures
```

For the hidden window ownership CPU contract, using the pinned Zig executable:

```powershell
python tests/native/d3d9_session/verify_window.py --zig C:/tools/zig/zig.exe --output C:/validation/new-window-owner-run --global-cache C:/validation/zig-global-cache
```

All three paths must be absolute; `--output` must be new, and output/cache must
not overlap the source repository or compiler directory. The runner checks Zig
SHA256 `086ce9d47ba42f33a514e1a6e04eb1d4a8fa1d75e0868e0213caad447c91e864`
and compiles `window_owner.cpp` directly against the product `owner_window.hpp`.
It checks that sent/posted `WM_CLOSE` retain the hidden window until explicit
owner destruction and that no `WM_QUIT` is posted. Compile/run logs, argv,
return codes and input/executable SHA256 are retained in the output directory.
Both child processes use `CREATE_NO_WINDOW`; this CPU-only test loads no D3D DLL,
creates no GPU device and does not touch the installed application or user assets.
