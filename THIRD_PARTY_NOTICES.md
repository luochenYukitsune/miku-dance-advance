# Third-party notices

MDA's original contributions use AGPL-3.0-only; this does not replace any license below. See [LICENSING.md](LICENSING.md) and [LICENSE](LICENSE). Third-party portions and their copyright notices remain under their original terms.

## Documentation website

GitHub Pages renders the documentation using the [Minimal theme](https://github.com/pages-themes/minimal), which is released under CC0-1.0. Its implementation is supplied by GitHub Pages rather than vendored into MDA. Local reading-style overrides are MDA's original contributions. The website does not redistribute standalone font files.

## HarmonyOS Sans SC (embedded interface fonts)

- Copyright 2021 Huawei Device Co., Ltd. The MDA interface uses HarmonyOS Sans.
- Source: official OpenHarmony `resources` repository at `0d79cad76b37ba0f3ccb1323c83833c78e0860de`, https://github.com/openharmony/resources/tree/0d79cad76b37ba0f3ccb1323c83833c78e0860de .
- Complete, unmodified Regular, Medium and Bold TTF files are embedded under `src/Mmd.Modern.App/Assets/Fonts`; exact source URLs and SHA256 identities are in `provenance.json` there. No subsetting, conversion or internal renaming is performed.
- The font uses the dedicated HarmonyOS Sans Fonts License Agreement, not MIT or OFL. Complete copyright and terms are retained in `Assets/Fonts/LICENSE_Fonts`, also copied to `licenses/HarmonyOS-Sans/LICENSE_Fonts` in application output and publish.
- The About dialog identifies the font. These fonts are bundled with MDA software and must not be redistributed as a standalone font product.

## D3D9/MME host source foundation (MIT-derived, optional Classic engine)

- Source: https://github.com/jstzwj/MikuDanceStudio at `bfb6297aa63a37770efcd3f7a599de5dd39be12b`; a third-party behavioral reconstruction, not the original MMD/MMEffect authors' source.
- Fixed source, complete MIT notice, per-file provenance and local changes: `compatibility/d3d9-mme-host/manifest.json`, `UPSTREAM.json`, `upstream/LICENSE` and `patches/` in that directory. The MIT grant covers the retained upstream source and its derived portions; local bridge and validation support have separate provenance.
- Extracted original shaders, textures, icons and other `res` payloads are excluded. No original MMD/MMEffect binary or D3DX runtime binary is redistributed by this source foundation.
- The native Classic bridge links this fixed host and supplies its MMD query adapters. Editor preview and export use that bridge only for the selected Classic engine. The optional Classic component has been delivered in the local installer; this public source snapshot contains its source and build support, not the installer or runtime binaries. This does not claim compatibility with every effect or pixel-identical rendering for every original project.
- Original toon images are read from a local MMD directory explicitly selected by the user, and the images actually used are retained in the user's PMC project. They are not covered by the host's MIT license and are not bundled with MDA or the optional component. Their original terms remain applicable.

## FFmpeg (separate executable bundled with the installer)

- Official source/documentation: https://ffmpeg.org/ ; license guidance: https://ffmpeg.org/legal.html .
- ViewOutput invokes an explicit executable through its CLI; no FFmpeg source or binary is embedded in the C# source package. External process use does not remove binary redistribution obligations.
- The installer includes the official download page's linked Gyan Windows release essentials build under `tools/ffmpeg/`; development inputs remain in the peripheral `.dev-cache` directory. Exact archive/executable hashes, version, build configuration, license and the scoped encoder checks are retained under `artifacts/stage-20261022-view-output/ffmpeg/` and copied into the staged license materials.
- A supplied source-material folder is copied as-is. Its presence does not prove exact correspondence to this binary and all static dependencies. Packaging records source-material transport separately and keeps `ffmpegCorrespondingSourceIncluded=false` until that correspondence and the build materials have actually been verified; public distribution remains pending that work.
- Actual builds containing libx264/libx265 can be GPL; consult the recorded build license before distributing that binary. The nanoem MPL FFmpeg plugin was reviewed for reference and was not copied.

## SMAA (vendored shader and lookup tables)

- Project: https://github.com/iryoku/smaa
- Commit: `71c806a838bdd7d517df19192a20f0c61b3ca29d`
- Copyright (C) 2013 Jorge Jimenez, Jose I. Echevarria, Belen Masia, Fernando Navarro, Diego Gutierrez.
- Full permission notice and disclaimer: [`third_party/smaa/LICENSE.txt`](third_party/smaa/LICENSE.txt).
- Unmodified `SMAA.hlsl`, `Textures/AreaTex.h`, and `Textures/SearchTex.h` retain their original notices and are embedded with the license in the D3D12 assembly.
- Local adaptation: C# D3D12 resource/pipeline host and separate shader entry wrappers; SMAA 1x, High preset, luma edges, no reprojection, gamma-space neighborhood blending for the existing UNorm color path.

## Saba (reference source, MIT)

- Project: https://github.com/benikabocha/saba
- Commit: `29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332`
- License: MIT, Copyright (c) 2016-2017 benikabocha
- Full text: [`third_party/saba/LICENCE.txt`](third_party/saba/LICENCE.txt)
- Local role: C++ sources are reference/oracle, not linked into product binaries; the C# ports below are built into the product.

Any C# file derived from saba must carry:

```
// Ported from saba (https://github.com/benikabocha/saba) @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT License, Copyright (c) 2016-2017 benikabocha
```

### PMX reader port

The following PMX reader files are ported from saba's `src/Saba/Model/MMD/PMXFile.h`
and `PMXFile.cpp` at the commit above:

- `src/Mmd.Modern.Formats/PmxModel.cs` (PMX 2.0/2.1 data model)
- `src/Mmd.Modern.Formats/PmxReader.cs` (PMX 2.0/2.1 parser)

Local adaptations: System.Numerics types, strict bounds/encoding validation with
field+offset diagnostics, normalized BDEF1/BDEF2/SDEF weights, and a corrected
QDEF weight read (saba reads `m_boneWeights[4]`, outside the declared array).

### VMD reader and bone animation ports

`src/Mmd.Modern.Formats/VmdMotion.cs`, `VmdReader.cs`, and `src/Mmd.Modern.Animation/CharacterAnimator.cs`, `CharacterRig.cs`, `BezierCurve.cs` adapt VMDFile.h/.cpp, VMDAnimation.cpp and PMXModel.cpp at the same pinned MIT commit. A full license copy is also in `src/Mmd.Modern.Animation/LICENSE.Saba.txt`.

Local adaptations: bounded CP932 reader with raw records; format-neutral immutable rigs/curves; System.Numerics row-vector matrices without OpenGL Z conversion; validated BDEF/SDEF and normals. MMD file-import policy (next-endpoint curves, redundant-block decoding, duplicate overwrite and frame-zero seed) is implemented separately from Saba's preceding-key curve policy, based on the recorded original-artifact analysis.

### IK, append transforms and character physics host

`PmxMorphAdapter.cs`, `PmxMaterialMorphAdapter.cs`, the morph weight importer and `MorphPose`/skinning UV path also adapt pinned Saba `PMXModel.cpp`: base UV XY accumulation and separate material multiply/add factors. Local adaptation preserves immutable baselines, project edge scaling and dynamic resource identities. Texture/sphere/toon material factors and additional UV1..4 are diagnosed rather than claimed supported.

`src/Mmd.Modern.Animation/MmdConstraintEvaluator.cs` and the rig metadata adapt Saba's `MMDIkSolver.cpp`, `MMDNode.cpp` and `PMXModel.cpp`: CCD, single-axis/continuous Euler limits, best-pose rollback and before/after-physics deformation stages. C# PMX policy uses the original-artifact angular-step cap and cumulative animated append translation where the pinned Saba behavior differs. The Saba angle policy is retained for native differential tests; arbitrary original-MMD limit/scheduling equivalence is not claimed.

`src/Mmd.Modern.Physics/CharacterPhysicsDefinition.cs` and `CharacterPhysicsSession.cs` adapt the PMX body/joint frames and bone synchronization from Saba's `MMDPhysics.cpp` / `PMXModel.cpp`. Full MIT notice: `src/Mmd.Modern.Physics/LICENSE.Saba.txt`. BepuPhysics 2.4.0 remains an independent experimental modern solver; the separate Bullet backend below now supplies the original Bullet numerical implementation without introducing BulletSharp.

`src/Mmd.Modern.Formats/VpdPose.cs`, `VpdReader.cs`, and `VpdWriter.cs` adapt the pinned Saba VPDFile.h/.cpp text layout, with bounded strict CP932/UTF8 handling and validate-before-write semantics. `src/Mmd.Modern.Application/PmxMorphWeights.cs` adapts PMXModel.cpp group traversal: each path is applied in source order, including repeated leaves, without merging material multipliers or bone rotations. These additions do not implement Flip or Impulse morphs.

## Bullet 2.75 compatibility backend (zlib) and nanoem joint mapping (MIT)

- Bullet source archive: https://storage.googleapis.com/google-code-archive-downloads/v2/code.google.com/bullet/bullet-2.75.zip
- Archive SHA256: `fe2e369593b034c00a1245676edd8417a58c8030a94e061220f0c228ca61fe8f`.
- Unmodified core sources and per-file provenance: `third_party/bullet-2.75/UPSTREAM.json`; license: `third_party/bullet-2.75/BulletLicense.txt` (zlib, Copyright (c) 2003-2006 Erwin Coumans).
- `scripts/build-bullet.py` builds the vendored float32 core plus our C ABI wrapper. It excludes the upstream obsolete C API, enables the supported Windows aligned allocator, and never executes upstream build scripts.
- `native/Mmd.Modern.Bullet/mmd_bullet.cpp` adapts PMX joint field mapping from nanoem's MIT component `nanoem/ext/physics_bullet.cc`, commit `30acffaa29f5d2eb9e997d69418f2e4b97b5894f`. No MPL emapp code is used.
- Full Bullet and nanoem licenses in `src/Mmd.Modern.Physics.Bullet/LICENSE.Bullet.txt` and `LICENSE.Nanoem.txt` are copied alongside managed/native build and publish outputs. The C# backend uses our own ABI; Unity scaling and UMT runtime code are not included.
- Scope: native rigid-body/contact/joint solver, not a claim that all original MMD world settings, soft bodies, bone schedules, or cross-model physics are reproduced.

## Independent Bullet 2.75 soft-body solver and tinyxfileloader (2026-10-21)

- `native/Mmd.Modern.Bullet.SoftBody/upstream/BulletSoftBody/` retains 18 unmodified files from the same pinned Bullet 2.75 archive above; exact provenance/hashes and zlib notice are in that directory's `UPSTREAM.json` and `LICENSE.Bullet.txt`.
- `scripts/build-bullet-soft.py` builds a separate ABI/library, preserving the verified rigid library. A documented cache-only value-initialization compatibility patch and legacy narrowing flag permit compilation of the old templates with modern Clang. The vendored solver bytes remain unchanged.
- The new PMX material/node mapping references nanoem's MIT `nanoem/ext/physics_bullet.cc` at the pinned commit above. Full MIT notice is retained in the native soft-body directory and Bullet managed project's `LICENSE.Nanoem.txt`. The independent solver does not provide reverse rigid impulse coupling or near-anchor equivalence.
- `src/Mmd.Modern.Formats/XTextReader.cs` adapts text .x mesh/frame/material parsing from Saba's actual `external/tinyxfileloader/include/tinyxfileloader.h` at the pinned Saba commit. MIT License, Copyright (c) 2016-2017 benikabocha. Full notice: `src/Mmd.Modern.Formats/LICENSE.TinyXFileLoader.txt`, retained in build/publish output. Local coordinates remain left-handed; Saba's OpenGL conversion is not copied. Later binary/MSZIP and triangulation additions are described below.

## Bounded X binary/MSZIP and topology (2026-10-21)

- SharpZipLib 1.4.2 is directly reused for raw DEFLATE completion and input-boundary validation; MIT License, Copyright (c) 2000-2018 SharpZipLib Contributors. Full upstream bytes: `src/Mmd.Modern.Formats/LICENSE.SharpZipLib.txt`, SHA256 `d7fbbc16f871bf24fa5624c7eaf945054b778985f32676dcacba41fddca61133`, retained alongside build/publish outputs. NuGet identity/content hashes are locked in the affected `packages.lock.json` files.
- Source: https://github.com/icsharpcode/SharpZipLib/tree/v1.4.2 . `XMszipReader` adds bounded X framing/history management and uses .NET DeflateStream to verify short-history distances. It does not contain a newly written DEFLATE implementation.
- Assimp v5.4.3 `XFileParser.cpp` and `TriangulateProcess.cpp` (BSD-3-Clause) and Microsoft X/MS-MCI documentation were inspected as format/algorithm references; the C# token reader and planar triangulator are independently written. No Assimp or Wine implementation is included in the product. Actual Assimp test cubes remain stage-only verification inputs with their upstream license; complete cubes contain skinning and remain explicitly outside the static-accessory reader scope.

## babylon-mmd PMX rotation limits (MIT)

- Project: https://github.com/noname0310/babylon-mmd
- Commit: `ccb750db2998ed1067017eaf67f3fc987cfba6f8`
- MIT License, Copyright (c) 2024 noname. Full notice: [`src/Mmd.Modern.Animation/LICENSE.BabylonMmd.txt`](src/Mmd.Modern.Animation/LICENSE.BabylonMmd.txt), retained in build/publish output.
- `src/Mmd.Modern.Animation/MmdIkRotationLimits.cs` adapts the Euler-order, singularity and early-iteration reflection structure from `src/Runtime/ikSolver.ts`, which itself credits Saba. Original-build constants and row-matrix composition are calibrated separately against retained artifact evidence. No TypeScript, WebAssembly or Babylon runtime dependency is introduced; this is a scoped PMX multi-axis limit helper, not complete original-MMD IK or scheduling equivalence.

## nanoem PMM document component (MIT)

- Project: https://github.com/hkrn/nanoem
- Commit: `30acffaa29f5d2eb9e997d69418f2e4b97b5894f`
- The **nanoem component** is MIT/X11; the separate emapp and platform applications use MPL. This project ports the MIT document component only.
- Full notices: [`third_party/nanoem-pmm/LICENSE.MIT`](third_party/nanoem-pmm/LICENSE.MIT), component boundaries: [`LICENSE.md`](third_party/nanoem-pmm/LICENSE.md).
- `src/Mmd.Modern.Formats/PmmDocument.cs`, `PmmReader.cs` and the PMM link/interpolation adapter adapt `nanoem/ext/document.c`, `document.h`, `document_p.h` into managed C#. No native nanoem library is required by the application.
- Local adaptations: bounded reads with field/offset errors, preserved ANSI raw bytes, per-reference CP936/CP932 resolution, sparse object-ID link validation, atomic project preparation and existing C# animation/renderer integration. Native nanoem's dense-index name getter is not used to resolve track ownership.
- Unmodified pinned C source is compiled only for the independent test oracle under `artifacts/stage-20261019-pmm/oracle/`; provenance and verification hashes are recorded there. Its results validate file parsing, not all original MMD runtime behavior.
- `PmmSceneTracks.cs` also adapts Saba's pinned `MMDCamera.cpp` / `VMDCameraAnimation.cpp` camera convention; Saba's MIT notice above applies.

## .NET / Avalonia / Veldrid / BepuPhysics / Vortice

### Unified rigid/soft capability and native PMA projects (2026-10-21)

The optional coupled owner compiles the same retained Bullet 2.75 core and soft
sources into one library; managed/native ABI boundaries expose owned bindings,
not Bullet object pointers. Standalone soft worlds remain available. Project
adapters add suspended-body contact scheduling and a geometric normal for the
upstream undefined coincident soft/soft contact; no Bullet vendored bytes change.

Both native build scripts compile an explicitly marked cache copy of the contact
solver with binary32 positional-error evaluation ordered as
`(-1/timestep)*(penetration*erp)`. Retained original-artifact assembly and bounded
solver observations establish this arithmetic order. Build manifests record
original/generated sources, header identities and exact compiled input hashes.
This is an intentional semantic adjustment, not merely compiler compatibility.

PMA directly uses the .NET MIT-licensed `System.IO.Compression.ZipArchive` runtime
API. OpenTimelineIO OTIOZ (Apache-2.0), glTF extension documentation, and Krita
format documentation were inspected for packaging/versioning design only; no
OTIO, glTF, or Krita implementation is copied or added as a dependency. Exact
reference links and supported format limits are in `docs/PMA_FORMAT_PLAN.md`.

NuGet packages retain their own licenses; exact resolved versions and content
hashes are recorded in `artifacts/dependency-audit.json` and `packages.lock.json`.

## AMD FidelityFX FSR1 spatial fallback (2026-10-22)

- Upstream: https://github.com/GPUOpen-Effects/FidelityFX-FSR , commit `a21ffb8f6c13233ba336352bdff293894c706575`.
- `third_party/fsr1/ffx_a.h` and `ffx_fsr1.h` are unchanged official FP32 shader headers; MIT, Copyright (c) 2021 Advanced Micro Devices, Inc. Full notice retained in `third_party/fsr1/LICENSE.txt`, embedded and copied with the D3D12 assembly. Exact upstream SHA256 values and source URLs: `docs/UPSCALING_OPEN_SOURCE_REUSE.md` and stage manifests.
- The independent C#/HLSL wrapper provides EASU and optional RCAS; this is FSR1 spatial fallback, not FSR3/4 temporal reconstruction.
- Newer SDK signed loader/upscaler DLLs are currently an explicit external cache installation for capability/dispatch verification, not vendored FSR1 headers or an unconditional application dependency. Their component-specific terms and provenance are recorded in the current runtime stage; no blanket MIT claim applies to all vendor SDK components.

## AMD FidelityFX current API layouts (2026-10-22)

The optional D3D12 runtime probe/temporal pass uses the public C API field layouts from FidelityFX SDK commit `60f4ea81909200d8542eca14dccb2628b763a9a3` (`ffx_api.h`, `ffx_api_types.h`, `ffx_api_dx12.h`, `ffx_upscale.h`). These headers carry AMD's MIT notice, Copyright (C) 2026 Advanced Micro Devices, Inc. The complete upstream component license document is retained unchanged at `third_party/fsr-api/LICENSE.md` and copied alongside build/publish outputs as `LICENSE.FidelityFXApi.md`; `UPSTREAM.json` records provenance. The signed runtime remains an explicit external installation for this milestone; no FSR4 execution is claimed on RTX2080.


## Optional vendor temporal reconstruction APIs (2026-10-22)

- AMD FidelityFX API: SDK2.3 loader and actual FSR provider3.1.5 on the tested RTX2080. Existing complete license/provenance remains in third_party/fsr-api. Optional binary component terms must be checked separately from MIT API headers.
- Intel XeSS: pinned Intel/xess de0fb9c1c510661c571164e1418ceca8101dab69, bundle3.0.2; actual xessGetVersion2.0.2. Independently authored C# ABI and motion-format conversion, no Intel reconstruction implementation source copied. Full Intel Simplified Software License and provenance in third_party/xess-api, license retained in build/publish. Runtime DLL explicitly installed externally.
- NVIDIA DLSS: pinned NVIDIA/DLSS 9981c6350dc58c0ce21f024fd65e31e851c31689. Thin native NGX C ABI bridge uses the separately licensed SDK static library and official helper headers during an optional external build. Full NVIDIA RTX SDK LICENSE in third_party/dlss-api, retained alongside output. No DLSS algorithm source or runtime DLL is vendored. Managed ABI and native syntax checks do not establish native linking or actual device execution.

## MikkTSpace (optional native tangent generator)

- Official source: https://github.com/mmikk/MikkTSpace at `3e895b49d05ea07e4c2133156cfa94369e19e409`.
- Copyright (C) 2011 Morten S. Mikkelsen. Permissive license and disclaimer retained in [`native/Mmd.Modern.Mikktspace/LICENSE.MikkTSpace.txt`](native/Mmd.Modern.Mikktspace/LICENSE.MikkTSpace.txt).
- Unmodified upstream C/H hashes and origin are pinned in `native/Mmd.Modern.Mikktspace/UPSTREAM.json`. The independently authored C ABI bridge is clearly separate.
- Used only by explicitly selected current-pose packed-normal MatCap materials; no replacement tangent solver is silently substituted. The ordinary MMD path does not load it. Build/install directions are in that folder's README.
- Blender's material equations were studied as a reference; no Blender GPL implementation or third-party plugin asset is included in this bridge.

## Formal editor shell (2026-10-24)

- Compact TextBox, ComboBox and Expander templates in `EditorWindow.axaml` and `OutputSettingsWindow.axaml` are adapted from Avalonia 11.3.22 Simple/Fluent control contracts, with local sizing, color and state changes. Source: https://github.com/AvaloniaUI/Avalonia/tree/11.3.22/src/Avalonia.Themes.Simple/Controls . The upstream MIT notice follows:

The MIT License (MIT)

Copyright (c) AvaloniaUI OÜ
All Rights Reserved

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

- SukiUI 5.3.0, MIT, Copyright (c) 2022 kikipoulet. The existing Avalonia 11.3.22 / Skia 2.88 dependency line is retained. Full notice is embedded at `src/Mmd.Modern.App/Assets/Icons/LICENSE-SukiUI.txt`; package identity and transitive dependencies are in the App lock file. Upstream: https://github.com/kikipoulet/SukiUI . Local dense toolbar templates are authored for this editor.
- Phosphor Regular icon paths, MIT, Copyright (c) 2023 Phosphor Icons, from https://github.com/phosphor-icons/core at `2b75f3ad12b420c9504ef05df8d2564a28f8500e`. Twelve unmodified path geometries are embedded in `Assets/Icons/PhosphorRegular.axaml`, with full notice in `LICENSE-Phosphor.txt` beside it.
- `scripts/extract-brand-alpha.py` is an asset preparation tool using Pillow, NumPy and SciPy, without adding a product runtime dependency. It implements a scoped white-matte recovery equation and SciPy nearest-feature transform. GIMP Color to Alpha documentation informed the approach; no GEGL implementation is copied. The user-supplied brand reference remains unchanged, and the generated transparent image is separate.
- The frontend-design and Impeccable skills are installed in the user's tool environment, with their own license and provenance records. They and the Impeccable engine are not bundled with MDA.

## Editor TGA and DDS texture decoding (2026-10-25)

- Pfim 0.11.4, MIT, Copyright (c) 2016 Nick Babcock. Pinned package and upstream source: https://github.com/nickbabcock/Pfim/tree/v0.11.4 . The complete upstream notice is retained at `src/Mmd.Modern.App/LICENSE.Pfim.txt` and copied to build/publish output. No upstream decoder source is copied or modified.
- The editor's adapter validates two-dimensional LDR inputs, normalizes TGA orientation and converts Pfim BGR/BGRA rows to the existing RGBA8 scene contract. PNG/JPEG/BMP continue through the existing Skia codec. Original textures remain read-only.

## Microsoft Visual C++ Runtime for optional upscalers (2026-10-25)

- `scripts/stage-editor-crt.ps1` stages the unchanged x64 `MSVCP140.dll`, `VCRUNTIME140.dll` and `VCRUNTIME140_1.dll` from Microsoft's official `Microsoft.VC.14.51.CRT.Redist.X64.base.vsix`, SHA256 `24a1c8dba0376d9b008ac2ba430d13c06664fc6f163511fd7866485030e289af`. The exact archive entries are under `Contents/VC/Redist/MSVC/14.51.36231/x64/Microsoft.VC145.CRT/`; the script records the actual common DLL file version, hashes and valid Microsoft Corporation signatures, and requires a runtime version at least `14.44.35229.0` for the current native build tools. No DLL is copied from a Windows system directory.
- Official archive: https://download.visualstudio.microsoft.com/download/pr/3984a6ef-2ffc-4c07-962e-bf9e9b862819/24a1c8dba0376d9b008ac2ba430d13c06664fc6f163511fd7866485030e289af/Microsoft.VC.14.51.CRT.Redist.X64.base.vsix . These are Microsoft redistributable binaries, **not permissively licensed open-source components**. The applicable proprietary Microsoft Visual C++ v14 Runtime 2026 license is retained unchanged from https://visualstudio.microsoft.com/wp-content/uploads/2025/10/Visual-C-V14-License-Redistributable_and_Runtime_ENU.docx ; the official terms page is https://visualstudio.microsoft.com/license-terms/vs2026-ga-visualcpp-v14-redist-runtime/ . Those runtime terms permit installation and use of any number of copies; third-party redistribution additionally requires the applicable Visual Studio developer redistribution rights and conditions in https://learn.microsoft.com/visualstudio/releases/2026/redistribution . Local installation/use and third-party redistribution are distinct permissions.
- Identical copies are placed beside the optional vendor DLLs in `tools/upscaling/fsr/`, `tools/upscaling/xess/` and `tools/upscaling/dlss/`, matching the native loader's DLL-directory dependency search. The original license document and per-file source/signature/SHA256 provenance accompany them in `tools/upscaling/licenses/vc-runtime/`. The script downloads nothing and refuses to replace existing destination files.

## Unicode CP932 mapping
Microsoft CP932 duplicate byte mappings from the Unicode mapping archive are embedded in Mmd.Modern.Formats under Unicode License V3. See src/Mmd.Modern.Formats/Resources/README.md and LICENSE.unicode.txt.
## Experimental D3D9 session bridge

`native/Mmd.Modern.D3D9.Compatibility` contains the project-authored C ABI, frozen-packet parsers, embedded ordinary-surface shader and session lifecycle. It links the fixed host in `compatibility/d3d9-mme-host`; that component's upstream MIT notice and ALTERED provenance remain applicable to its source. The bridge does not contain extracted original MMD/MMEffect resources. Component origins, source hashes and finite capabilities are recorded in its `UPSTREAM.json` and `manifest.json`.

The native DLL imports D3DX9_43. Optional Classic staging accepts only the unchanged, hash-pinned Windows x64 files from Microsoft.DXSDK.D3DX 9.29.952.8, together with its full LICENSE.txt, NOTICE.md, README.md and package identity. These proprietary Microsoft redistributables are not relicensed under MIT or the project's license. The installer requires acceptance of those terms when Classic is selected. No runtime binary is committed in this source tree; successful staging alone does not establish product or installation acceptance.
